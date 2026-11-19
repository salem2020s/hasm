using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using RV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingReversal;
using PE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingEvent;
using NO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligation;
using NX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeSettlement;
using NR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligationReversal;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using JA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalAllocation;
using AC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using IV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingVersion;
using IG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingGroup;

namespace Hasm.Infrastructure.Costing;

internal sealed record ClassificationLimits(int Postings = 500, int Journals = 1000, int Lines = 10_000, int Commands = 400, TimeSpan? Deadline = null);
internal sealed record ClassifiedAllocation(Guid CostCenterId, decimal Amount, decimal Base);
internal sealed record ClassifiedLine(int No, Guid AccountId, string Currency, decimal BaseDebit, decimal BaseCredit, Guid BranchId, bool IsRounding,
    ImmutableArray<ClassifiedAllocation> Allocations);
internal sealed record ClassifiedJournal(Guid Id, short Status, short Type, long Revision, DateOnly Date, Guid BranchId, string BaseCurrency,
    DateTimeOffset? PostedAt, Guid? PeriodId, Guid? Reverses, Guid? Adjusts, ImmutableArray<ClassifiedLine> Lines);
internal sealed record ClassifiedAccount(Guid Id, bool Active, bool Group, short CurrencyPolicy, long Revision);
internal sealed record ClassifiedPosting(Guid Id, Guid DocumentId, int DocType, DateOnly Date, decimal TotalValue, Guid? VersionId, Guid? JournalId,
    Guid? SourceWarehouse, Guid? TargetWarehouse, Guid? ReversalId, short? ReversalAction, Guid? ReversalJournalId);
internal sealed record ClassifiedObligation(Guid Id, Guid PostingId, Guid? JournalId, Guid? ObligationAccountId, Guid? VarianceAccountId, Guid? CounterAccountId);
internal sealed record ClassifiedSettlement(Guid Id, Guid ObligationId, Guid ReceiptPostingId, Guid? JournalId, Guid? ReversesSettlementId, bool InNetChain);
internal sealed record ClassificationEvidence(ImmutableArray<ClassifiedPosting> Postings, ImmutableArray<ClassifiedObligation> Obligations,
    ImmutableArray<ClassifiedSettlement> Settlements, ImmutableArray<ClassifiedJournal> Journals, ImmutableArray<ClassifiedAccount> Accounts,
    string EvidenceFingerprint);

/// <summary>
/// Read-only evidence of what the ORIGINAL stock postings and the negative-stock chain booked (contract
/// <c>PHASE-3.8D-CLASSIFICATION-CONTRACT.md</c>, revision 4). The stored journal lines are the classification; versions and groups are only a
/// cross-check. This is NOT a compensation plan, approval or execution authority; no API/DI/caller. Every member of the closure must be visible to the
/// caller (stock warehouse, ledger branch, accounts, cost centres), and any invisible or missing member yields one indistinguishable
/// <c>forbidden</c>. Unsupported, ambiguous, inconsistent and OPEN-decision cases fail closed AFTER authorization.
/// </summary>
internal sealed class ClassificationEvidenceService(CompanyDatabaseResolver resolver, AccessService access, ClassificationLimits? limitsOverride = null)
{
    private readonly ClassificationLimits limits = limitsOverride ?? new();
    private sealed class Denied : Exception;
    internal sealed class Refusal(string code) : Exception(code) { internal string Code { get; } = code; }
    internal Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? SnapshotEstablished { get; set; }
    internal Func<CancellationToken, Task>? BeforeFinalAuthorization { get; set; }

    private const short Draft = 1, Pending = 2, Approved = 3, Posted = 4, Reversed = 5, Cancelled = 6;
    private const short Adjusting = 3;

    internal async Task<ConfigResult<ClassificationEvidence>> LoadAsync(Guid user, Guid company, ImmutableArray<Guid> postingIds, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limits.Deadline ?? TimeSpan.FromSeconds(30)); var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var quota = new ReadCommandBudget(limits.Commands, token);
        ConfigResult<ClassificationEvidence> result;
        Loaded? loaded = null;
        try
        {
            if (postingIds.IsDefaultOrEmpty || postingIds.Length > limits.Postings || postingIds.Any(i => i == Guid.Empty))
                return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Conflict, "invalid_classification_request");
            var boot = await access.GetEffectivePermissionsAsync(user, company, quota.Reserve, token);
            if (!Bootstrap(boot)) throw new Denied();
            await using var conn = await resolver.OpenAsync(company, quota.Reserve, token);
            quota.Reserve(2);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await using (var q = Cmd("SET TRANSACTION READ ONLY", conn, tx, quota)) await q.ExecuteNonQueryAsync(token);
            if (SnapshotEstablished is { } observe) await observe(conn, tx, token);
            loaded = await LoadClosure(conn, tx, postingIds, quota, token);
            // Authorization of EVERY member precedes any diagnostic: refusals must not reveal ledger facts to an unauthorized caller.
            Authorize(boot, loaded);
            result = ConfigResult<ClassificationEvidence>.Ok(Evaluate(company, postingIds, loaded));
        }
        catch (ReadCommandBudgetException) { token.ThrowIfCancellationRequested(); return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Conflict, "classification_history_too_large"); }
        catch (Denied) { result = ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Forbidden, "forbidden"); }
        catch (Refusal r) { result = ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Conflict, r.Code); }
        if (BeforeFinalAuthorization is { } hook) await hook(token);
        try
        {
            // Final live recheck on success AND failure: a revocation during the read wins.
            var fresh = await access.GetEffectivePermissionsAsync(user, company, quota.Reserve, token);
            if (!Bootstrap(fresh)) return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Forbidden, "forbidden");
            if (loaded is not null) Authorize(fresh, loaded);
        }
        catch (Denied) { return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Forbidden, "forbidden"); }
        catch (ReadCommandBudgetException) { token.ThrowIfCancellationRequested(); return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Conflict, "classification_query_budget_unavailable"); }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static bool Bootstrap(EffectivePermissions p) =>
        p.Has(PermissionCatalog.Codes.AccountsView, ScopeTarget.Company)
        && (p.HasCompanyWide(PermissionCatalog.Codes.JournalView) || p.Grants.Any(g => g.Scope == ScopeKind.Branch))
        && (p.HasCompanyWide(PermissionCatalog.Codes.StockDocsView) || p.Grants.Any(g => g.Scope == ScopeKind.Warehouse));

    private static NpgsqlCommand Cmd(string sql, NpgsqlConnection conn, NpgsqlTransaction tx, ReadCommandBudget quota)
    { quota.Reserve(); return new NpgsqlCommand(sql, conn, tx); }

    internal sealed record Loaded(Dictionary<Guid, ClassifiedPosting> Postings, List<ClassifiedObligation> Obligations,
        List<ClassifiedSettlement> Settlements, List<(Guid Id, Guid ObligationId, Guid StockReversalId)> ObligationReversals,
        Dictionary<Guid, ClassifiedJournal> Journals, Dictionary<Guid, ClassifiedAccount> Accounts, HashSet<Guid> AdjustedJournals,
        Dictionary<Guid, HashSet<Guid>> EventJournals, Dictionary<Guid, HashSet<Guid>> VersionAccounts, Dictionary<Guid, Guid> ReversalRowOfPosting);

    private async Task<Loaded> LoadClosure(NpgsqlConnection conn, NpgsqlTransaction tx, ImmutableArray<Guid> requested, ReadCommandBudget quota, CancellationToken ct)
    {
        var postingSet = requested.ToHashSet();
        var obligationSet = new HashSet<Guid>();
        var obligations = new Dictionary<Guid, ClassifiedObligation>();
        var settlements = new Dictionary<Guid, (ClassifiedSettlement S, Guid? ReversalId)>();
        // Bounded fixpoint: postings -> obligations -> settlements -> receipt postings -> ... Every iteration reserves its commands.
        while (true)
        {
            token(ct);
            var before = (postingSet.Count, obligationSet.Count, settlements.Count);
            await using (var q = Cmd($"SELECT {NO.Id},{NO.PostingId},{NO.JournalEntryId},{NO.ObligationAccountId},{NO.VarianceAccountId},{NO.CounterAccountId} FROM {NO.Table} " +
                $"WHERE {NO.PostingId}=ANY(@p) OR {NO.Id}=ANY(@o) LIMIT @l", conn, tx, quota))
            {
                q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("o", obligationSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (obligations.Count >= limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                    var o = new ClassifiedObligation(r.GetGuid(0), r.GetGuid(1), N(r, 2), N(r, 3), N(r, 4), N(r, 5));
                    obligations[o.Id] = o; obligationSet.Add(o.Id); postingSet.Add(o.PostingId);
                }
            }
            await using (var q = Cmd($"SELECT {NX.Id},{NX.ObligationId},{NX.ReceiptPostingId},{NX.JournalEntryId},{NX.ReversesSettlementId},{NX.ReversalId} FROM {NX.Table} " +
                $"WHERE {NX.ObligationId}=ANY(@o) OR {NX.ReceiptPostingId}=ANY(@p) LIMIT @l", conn, tx, quota))
            {
                q.Parameters.AddWithValue("o", obligationSet.ToArray()); q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (settlements.Count >= limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                    var s = new ClassifiedSettlement(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), N(r, 3), N(r, 4), true);
                    settlements[s.Id] = (s, N(r, 5)); obligationSet.Add(s.ObligationId); postingSet.Add(s.ReceiptPostingId);
                }
            }
            if (postingSet.Count > limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
            if (before == (postingSet.Count, obligationSet.Count, settlements.Count)) break;
        }
        // Net chain exactly as ForwardSettlementsAsync: both the reversed settlement and the negating row leave the NET chain (journals stay evidence).
        var reversedIds = settlements.Values.Where(x => x.S.ReversesSettlementId is not null).Select(x => x.S.ReversesSettlementId!.Value).ToHashSet();
        var settlementList = settlements.Values.Select(x => x.S with { InNetChain = x.S.ReversesSettlementId is null && !reversedIds.Contains(x.S.Id) })
            .OrderBy(s => s.Id).ToList();

        var obligationReversals = new List<(Guid, Guid, Guid)>();
        await using (var q = Cmd($"SELECT {NR.Id},{NR.ObligationId},{NR.StockReversalId} FROM {NR.Table} WHERE {NR.ObligationId}=ANY(@o) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("o", obligationSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (obligationReversals.Count >= limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                obligationReversals.Add((r.GetGuid(0), r.GetGuid(1), r.GetGuid(2)));
            }
        }

        var reversals = new Dictionary<Guid, (Guid Id, short Action, Guid? Journal)>();
        var reversalRowOfPosting = new Dictionary<Guid, Guid>();
        await using (var q = Cmd($"SELECT {RV.Id},{RV.PostingId},{RV.JournalAction},{RV.JournalEntryId} FROM {RV.Table} WHERE {RV.PostingId}=ANY(@p) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (reversals.Count >= limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                if (!reversals.TryAdd(r.GetGuid(1), (r.GetGuid(0), r.GetInt16(2), N(r, 3)))) throw new Refusal("classification_inconsistent");
                reversalRowOfPosting[r.GetGuid(1)] = r.GetGuid(0);
            }
        }

        var postings = new Dictionary<Guid, ClassifiedPosting>();
        await using (var q = Cmd($"SELECT p.{SP.Id},p.{SP.DocumentId},p.{SP.DocType},p.{SP.EffectiveDate},p.{SP.TotalValue},p.{SP.AccountingVersionId},p.{SP.JournalEntryId}," +
            $"d.{SD.SourceWarehouseId},d.{SD.TargetWarehouseId} FROM {SP.Table} p JOIN {SD.Table} d ON d.{SD.Id}=p.{SP.DocumentId} WHERE p.{SP.Id}=ANY(@p) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0); reversals.TryGetValue(id, out var rv);
                postings[id] = new(id, r.GetGuid(1), r.GetInt16(2), DateOnly.FromDateTime(r.GetDateTime(3)), r.GetDecimal(4), N(r, 5), N(r, 6), N(r, 7), N(r, 8),
                    reversals.ContainsKey(id) ? rv.Id : null, reversals.ContainsKey(id) ? rv.Action : null, reversals.ContainsKey(id) ? rv.Journal : null);
            }
        }
        // A requested id that is not a stock posting (missing, adjustment, foreign) is indistinguishable from a denied one.
        if (requested.Any(id => !postings.ContainsKey(id)) || postingSet.Any(id => !postings.ContainsKey(id))) throw new Denied();

        var eventJournals = new Dictionary<Guid, HashSet<Guid>>();
        await using (var q = Cmd($"SELECT {PE.PostingId},{PE.JournalEntryId} FROM {PE.Table} WHERE {PE.PostingId}=ANY(@p) AND {PE.JournalEntryId} IS NOT NULL LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("l", limits.Journals * 4 + 1);
            await using var r = await q.ExecuteReaderAsync(ct); var n = 0;
            while (await r.ReadAsync(ct))
            {
                if (++n > limits.Journals * 4) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                if (!eventJournals.TryGetValue(r.GetGuid(0), out var set)) eventJournals[r.GetGuid(0)] = set = [];
                set.Add(r.GetGuid(1));
            }
        }

        var journalIds = new HashSet<Guid>();
        foreach (var p in postings.Values) { if (p.JournalId is { } a) journalIds.Add(a); if (p.ReversalJournalId is { } b) journalIds.Add(b); }
        foreach (var o in obligations.Values) if (o.JournalId is { } j) journalIds.Add(j);
        foreach (var s in settlementList) if (s.JournalId is { } j) journalIds.Add(j);
        foreach (var set in eventJournals.Values) journalIds.UnionWith(set);

        // Mandatory reverse lookups: reversing journals and adjusting journals of every journal in the closure.
        var adjusted = new HashSet<Guid>();
        await using (var q = Cmd($"SELECT {JE.Id},{JE.Type},{JE.Reverses},{JE.Adjusts} FROM {JE.Table} WHERE {JE.Reverses}=ANY(@j) OR {JE.Adjusts}=ANY(@j) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("j", journalIds.ToArray()); q.Parameters.AddWithValue("l", limits.Journals + 1);
            await using var r = await q.ExecuteReaderAsync(ct); var n = 0;
            while (await r.ReadAsync(ct))
            {
                if (++n > limits.Journals) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                if (N(r, 3) is { } adjustedJournal) adjusted.Add(adjustedJournal);
                if (N(r, 2) is not null || r.GetInt16(1) == Adjusting) journalIds.Add(r.GetGuid(0));
            }
        }
        if (journalIds.Count > limits.Journals) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);

        var journals = new Dictionary<Guid, ClassifiedJournal>();
        var lines = new Dictionary<Guid, List<ClassifiedLine>>();
        await using (var q = Cmd($"SELECT e.{JE.Id},e.{JE.Status},e.{JE.Type},e.{JE.Rev},e.{JE.Date},e.{JE.Branch},e.{JE.BaseCur},e.{JE.PostedAt},e.{JE.Period},e.{JE.Reverses},e.{JE.Adjusts} " +
            $"FROM {JE.Table} e WHERE e.{JE.Id}=ANY(@j) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("j", journalIds.ToArray()); q.Parameters.AddWithValue("l", limits.Journals + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                journals[r.GetGuid(0)] = new(r.GetGuid(0), r.GetInt16(1), r.GetInt16(2), r.GetInt64(3), DateOnly.FromDateTime(r.GetDateTime(4)), r.GetGuid(5),
                    r.GetString(6).Trim(), r.IsDBNull(7) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc)), N(r, 8), N(r, 9), N(r, 10), []);
                lines[r.GetGuid(0)] = [];
            }
        }
        if (journalIds.Any(id => !journals.ContainsKey(id))) throw new Denied(); // dangling reference: indistinguishable from invisible
        var allocations = new Dictionary<Guid, List<ClassifiedAllocation>>();
        var lineEntry = new Dictionary<Guid, Guid>();
        var lineIds = new Dictionary<(Guid Entry, int No), Guid>();
        await using (var q = Cmd($"SELECT {JL.Id},{JL.Entry},{JL.No},{JL.Account},{JL.Cur},{JL.BaseDebit},{JL.BaseCredit},{JL.Branch},{JL.IsRounding} FROM {JL.Table} WHERE {JL.Entry}=ANY(@j) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("j", journals.Keys.ToArray()); q.Parameters.AddWithValue("l", limits.Lines + 1);
            await using var r = await q.ExecuteReaderAsync(ct); var n = 0;
                        while (await r.ReadAsync(ct))
            {
                if (++n > limits.Lines) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                var id = r.GetGuid(0); lineEntry[id] = r.GetGuid(1); lineIds[(r.GetGuid(1), r.GetInt32(2))] = id;
                lines[r.GetGuid(1)].Add(new(r.GetInt32(2), r.GetGuid(3), r.GetString(4).Trim(), r.GetDecimal(5), r.GetDecimal(6), r.GetGuid(7), r.GetBoolean(8), []));
                allocations[id] = [];
            }
        }
        await using (var q = Cmd($"SELECT {JA.Line},{JA.CC},{JA.Amount},{JA.Base} FROM {JA.Table} WHERE {JA.Line}=ANY(@l) LIMIT @m", conn, tx, quota))
        {
            q.Parameters.AddWithValue("l", lineEntry.Keys.ToArray()); q.Parameters.AddWithValue("m", limits.Lines + 1);
            await using var r = await q.ExecuteReaderAsync(ct); var n = 0;
            while (await r.ReadAsync(ct))
            {
                if (++n > limits.Lines) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                if (!allocations.TryGetValue(r.GetGuid(0), out var list)) throw new Refusal("classification_inconsistent");
                list.Add(new(r.GetGuid(1), r.GetDecimal(2), r.GetDecimal(3)));
            }
        }
        foreach (var (id, journal) in journals.ToArray())
            journals[id] = journal with { Lines = [.. lines[id].OrderBy(l => l.No).Select(l => l with
                { Allocations = [.. allocations[lineIds[(id, l.No)]].OrderBy(a => a.CostCenterId).ThenBy(a => a.Amount)] })] };

        var accountIds = journals.Values.SelectMany(j => j.Lines.Select(l => l.AccountId))
            .Concat(obligations.Values.SelectMany(o => new[] { o.ObligationAccountId, o.VarianceAccountId, o.CounterAccountId }.Where(a => a.HasValue).Select(a => a!.Value))).Distinct().ToArray();
        var accounts = new Dictionary<Guid, ClassifiedAccount>();
        await using (var q = Cmd($"SELECT {AC.Id},{AC.IsActive},{AC.IsGroup},{AC.CurrencyPolicy},{AC.Revision} FROM {AC.Table} WHERE {AC.Id}=ANY(@a) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("a", accountIds); q.Parameters.AddWithValue("l", limits.Lines + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) accounts[r.GetGuid(0)] = new(r.GetGuid(0), r.GetBoolean(1), r.GetBoolean(2), r.GetInt16(3), r.GetInt64(4));
        }
        if (accountIds.Any(id => !accounts.ContainsKey(id))) throw new Denied();

        // Cross-check material: accounts that the posting's own accounting version could have produced.
        var versionAccounts = new Dictionary<Guid, HashSet<Guid>>();
        var versionIds = postings.Values.Where(p => p.VersionId.HasValue).Select(p => p.VersionId!.Value).Distinct().ToArray();
        if (versionIds.Length > 0)
        {
            await using (var q = Cmd($"SELECT {IV.Id},{IV.DefaultAccountId},{IV.ReceiptCounterAccountId},{IV.IssueCounterAccountId},{IV.AdjustmentCounterAccountId},{IV.ObligationAccountId},{IV.VarianceAccountId} FROM {IV.Table} WHERE {IV.Id}=ANY(@v)", conn, tx, quota))
            {
                q.Parameters.AddWithValue("v", versionIds); await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) { var set = new HashSet<Guid>(); for (var i = 1; i <= 6; i++) if (N(r, i) is { } a) set.Add(a); versionAccounts[r.GetGuid(0)] = set; }
            }
            await using (var q = Cmd($"SELECT {IG.VersionId},{IG.AccountId} FROM {IG.Table} WHERE {IG.VersionId}=ANY(@v)", conn, tx, quota))
            {
                q.Parameters.AddWithValue("v", versionIds); await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) if (versionAccounts.TryGetValue(r.GetGuid(0), out var set)) set.Add(r.GetGuid(1));
            }
        }
        return new(postings, [.. obligations.Values.OrderBy(o => o.Id)], settlementList, obligationReversals, journals, accounts, adjusted, eventJournals, versionAccounts, reversalRowOfPosting);
    }

    private static void token(CancellationToken ct) => ct.ThrowIfCancellationRequested();
    private static Guid? N(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    private static void Authorize(EffectivePermissions p, Loaded d)
    {
        foreach (var posting in d.Postings.Values)
            foreach (var w in new[] { posting.SourceWarehouse, posting.TargetWarehouse })
                if (w is { } id && !p.Has(PermissionCatalog.Codes.StockDocsView, ScopeTarget.Warehouse(id))) throw new Denied();
        foreach (var j in d.Journals.Values)
        {
            if (!p.Has(PermissionCatalog.Codes.JournalView, ScopeTarget.Branch(j.BranchId))) throw new Denied();
            foreach (var l in j.Lines)
            {
                if (!p.Has(PermissionCatalog.Codes.JournalView, ScopeTarget.Branch(l.BranchId))) throw new Denied();
                foreach (var a in l.Allocations)
                    if (!p.Has(PermissionCatalog.Codes.CostCentersView, ScopeTarget.CostCenter(a.CostCenterId))) throw new Denied();
            }
        }
    }

    internal static ClassificationEvidence Evaluate(Guid company, ImmutableArray<Guid> requested, Loaded d)
    {
        // Unsupported kinds first: transfers have no journal by design (NOT "no change needed").
        if (d.Postings.Values.Any(p => p.DocType == (int)StockDocType.Transfer)) throw new Refusal("classification_unavailable");
        if (d.Journals.Values.Any(j => j.Id is var id && d.AdjustedJournals.Contains(id))) throw new Refusal("open_decision");
        foreach (var j in d.Journals.Values)
        {
            if (j.Lines.Any(l => l.IsRounding || l.Currency != j.BaseCurrency)) throw new Refusal("classification_unexpected_line");
            if (j.Lines.Length < 2 || j.Lines.Sum(l => l.BaseDebit) != j.Lines.Sum(l => l.BaseCredit)) throw new Refusal("classification_inconsistent");
            if (j.Type == Adjusting) throw new Refusal("open_decision");
        }
        foreach (var a in d.Accounts.Values) if (!a.Active || a.Group || a.CurrencyPolicy == (short)AccountCurrencyPolicy.Specific) throw new Refusal("open_decision");

        ClassifiedJournal Journal(Guid? id) => id is { } v && d.Journals.TryGetValue(v, out var j) ? j : throw new Refusal("classification_inconsistent");
        // Rules shared by posting, obligation and settlement journals: a LIVE effect.
        void LiveJournal(Guid? id, bool allowNone)
        {
            if (id is null) { if (!allowNone) throw new Refusal("classification_inconsistent"); return; }
            var j = Journal(id);
            if (j.Status is Cancelled or Reversed) throw new Refusal("open_decision");   // journal dead while the effect lives
            if (j.Status is not (Draft or Pending or Approved or Posted)) throw new Refusal("classification_inconsistent");
        }
        // The effect was reversed: original journal + reversing journal (Reverses link is the proof; JournalAction is only a hint).
        void ReversedJournal(Guid? original, Guid? reversing, short? action)
        {
            if (original is null) { if (reversing is not null) throw new Refusal("classification_inconsistent"); return; }
            if (action != 2 || reversing is null || reversing == original) throw new Refusal("open_decision"); // draft cancelled / outside reversal
            var o = Journal(original); var r = Journal(reversing);
            if (r.Reverses != original) throw new Refusal("classification_inconsistent");
            if (r.Status != Posted || o.Status != Reversed) throw new Refusal("open_decision");   // reversing journal not posted / cancelled by hand
        }
        foreach (var p in d.Postings.Values)
        {
            if (p.ReversalId is null) // A receipt fully matched against negative obligations books nothing of its own: its value sits in the settlement journals.
            LiveJournal(p.JournalId, p.TotalValue == 0 || d.Settlements.Any(x => x.ReceiptPostingId == p.Id));
            else ReversedJournal(p.JournalId, p.ReversalJournalId, p.ReversalAction);
            // Cross-check only: a posted journal whose accounts its own version could not have produced is AMBIGUOUS, never silently resolved.
            if (p.ReversalId is null && p.JournalId is { } jid && p.VersionId is { } vid && Journal(jid).Status == Posted)
            {
                if (!d.VersionAccounts.TryGetValue(vid, out var allowed)) throw new Refusal("classification_inconsistent");
                if (Journal(jid).Lines.Any(l => !allowed.Contains(l.AccountId))) throw new Refusal("classification_ambiguous");
            }
        }
        foreach (var o in d.Obligations)
        {
            var issuePosting = d.Postings[o.PostingId];
            if (o.JournalId != issuePosting.JournalId) throw new Refusal("classification_inconsistent"); // obligation journal IS the issue posting journal
        }
        foreach (var (_, obligationId, reversalRow) in d.ObligationReversals)
        {
            var o = d.Obligations.FirstOrDefault(x => x.Id == obligationId) ?? throw new Refusal("classification_inconsistent");
            if (d.Postings[o.PostingId].ReversalId != reversalRow) throw new Refusal("classification_unavailable"); // path not traceable: fail closed
        }
        var byId = d.Settlements.ToDictionary(s => s.Id);
        foreach (var s in d.Settlements)
        {
            if (s.ReversesSettlementId is { } reversed)
            {
                var original = byId.GetValueOrDefault(reversed) ?? throw new Refusal("classification_inconsistent");
                ReversedJournal(original.JournalId, s.JournalId, original.JournalId is null ? (short)0 : (short)2);
                if (original.JournalId is null && s.JournalId is not null) throw new Refusal("classification_inconsistent");
            }
            else if (!byId.Values.Any(x => x.ReversesSettlementId == s.Id)) LiveJournal(s.JournalId, true);
            // A reversed original settlement is covered by its negating row above.
        }
        var postings = d.Postings.Values.OrderBy(p => p.Id).ToImmutableArray();
        var journals = d.Journals.Values.OrderBy(j => j.Id).ToImmutableArray();
        var accounts = d.Accounts.Values.OrderBy(a => a.Id).ToImmutableArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Contract = "stock-classification-evidence-v1", Company = company, Requested = requested.OrderBy(i => i).ToArray(), Postings = postings,
            Obligations = d.Obligations, Settlements = d.Settlements, ObligationReversals = d.ObligationReversals.OrderBy(x => x.Id).ToArray(),
            Journals = journals, Accounts = accounts,
            Events = d.EventJournals.OrderBy(k => k.Key).Select(k => new { Posting = k.Key, Journals = k.Value.OrderBy(i => i).ToArray() }).ToArray()
        }))).ToLowerInvariant();
        return new(postings, [.. d.Obligations], [.. d.Settlements], journals, accounts, fingerprint);
    }
}
