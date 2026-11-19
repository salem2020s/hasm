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
    Guid? SourceWarehouse, Guid? TargetWarehouse, Guid? ReversalId, short? ReversalAction, Guid? ReversalJournalId,
    Guid? CostOperationId = null, Guid? ReversalOperationId = null);
internal sealed record ClassifiedObligation(Guid Id, Guid PostingId, Guid? JournalId, Guid? ObligationAccountId, Guid? VarianceAccountId, Guid? CounterAccountId);
internal sealed record ClassifiedSettlement(Guid Id, Guid ObligationId, Guid ReceiptPostingId, Guid? JournalId, Guid? ReversesSettlementId, Guid? ReversalId, bool InNetChain);
internal sealed record ClassifiedObligationReversal(Guid Id, Guid ObligationId, Guid StockReversalId);
internal sealed record ClassifiedEvent(Guid PostingId, short Kind, Guid JournalId);
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
        Loaded? loaded = null; EffectivePermissions? boot = null, latest = null; var authorized = false;
        // Until EVERY closure member is authorized no diagnostic may depend on the contents of the closure: a caller who cannot see all of it gets the
        // single indistinguishable forbidden; only a caller holding every permission company-wide may see a specific code.
        ConfigResult<ClassificationEvidence> Masked(string code) =>
            (latest ?? boot) is { } basis && FullyVisible(basis) ? ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Conflict, code)
                : ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Forbidden, "forbidden");
        try
        {
            if (postingIds.IsDefaultOrEmpty || postingIds.Length > limits.Postings || postingIds.Any(i => i == Guid.Empty))
                return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Conflict, "invalid_classification_request");
            postingIds = [.. postingIds.Distinct()];
            boot = await access.GetEffectivePermissionsAsync(user, company, quota.Reserve, token);
            if (!Bootstrap(boot)) throw new Denied();
            await using var conn = await resolver.OpenAsync(company, quota.Reserve, token);
            quota.Reserve(2);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await using (var q = Cmd("SET TRANSACTION READ ONLY", conn, tx, quota)) await q.ExecuteNonQueryAsync(token);
            if (SnapshotEstablished is { } observe) await observe(conn, tx, token);
            loaded = await LoadClosure(conn, tx, postingIds, quota, token);
            Authorize(boot, loaded);
            authorized = true;
            result = ConfigResult<ClassificationEvidence>.Ok(Evaluate(company, postingIds, loaded));
        }
        catch (ReadCommandBudgetException) when (!authorized) { token.ThrowIfCancellationRequested(); result = Masked("classification_history_too_large"); }
        catch (Denied) { result = ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Forbidden, "forbidden"); }
        catch (Refusal r) { result = authorized ? ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Conflict, r.Code) : Masked(r.Code); }
        if (BeforeFinalAuthorization is { } hook) await hook(token);
        try
        {
            // Final live recheck on success AND failure: a revocation during the read wins.
            var fresh = await access.GetEffectivePermissionsAsync(user, company, quota.Reserve, token); latest = fresh;
            if (!Bootstrap(fresh)) return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Forbidden, "forbidden");
            // A specific diagnostic produced before authorization is only allowed while the caller is STILL fully visible.
            if (!authorized && !result.IsOk && result.Status == ConfigStatus.Conflict && !FullyVisible(fresh))
                return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Forbidden, "forbidden");
            if (loaded is not null) Authorize(fresh, loaded);
        }
        catch (Denied) { return ConfigResult<ClassificationEvidence>.Fail(ConfigStatus.Forbidden, "forbidden"); }
        catch (ReadCommandBudgetException e) { token.ThrowIfCancellationRequested(); return Masked(e.Failure == ReadBudgetFailure.Exhausted ? "classification_history_too_large" : "classification_query_budget_unavailable"); }
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal static bool Bootstrap(EffectivePermissions p) =>
        p.Has(PermissionCatalog.Codes.AccountsView, ScopeTarget.Company)
        && (p.HasCompanyWide(PermissionCatalog.Codes.JournalView) || p.Grants.Any(g => g.Scope == ScopeKind.Branch && g.Permissions.Contains(PermissionCatalog.Codes.JournalView)))
        && (p.HasCompanyWide(PermissionCatalog.Codes.StockDocsView) || p.Grants.Any(g => g.Scope == ScopeKind.Warehouse && g.Permissions.Contains(PermissionCatalog.Codes.StockDocsView)));

    /// <summary>Every member of any closure is visible: all permissions held company-wide, so a specific diagnostic cannot reveal anything the caller may not see.</summary>
    internal static bool FullyVisible(EffectivePermissions p) =>
        p.HasCompanyWide(PermissionCatalog.Codes.AccountsView) && p.HasCompanyWide(PermissionCatalog.Codes.JournalView)
        && p.HasCompanyWide(PermissionCatalog.Codes.StockDocsView) && p.HasCompanyWide(PermissionCatalog.Codes.CostCentersView);

    private static NpgsqlCommand Cmd(string sql, NpgsqlConnection conn, NpgsqlTransaction tx, ReadCommandBudget quota)
    { quota.Reserve(); return new NpgsqlCommand(sql, conn, tx); }

    internal sealed record Loaded(Dictionary<Guid, ClassifiedPosting> Postings, List<ClassifiedObligation> Obligations,
        List<ClassifiedSettlement> Settlements, List<ClassifiedObligationReversal> ObligationReversals,
        Dictionary<Guid, ClassifiedJournal> Journals, Dictionary<Guid, ClassifiedAccount> Accounts, HashSet<Guid> AdjustedJournals,
        HashSet<ClassifiedEvent> Events, Dictionary<Guid, HashSet<Guid>> VersionAccounts, Dictionary<Guid, Guid> ReversalRowOfPosting);

    private async Task<Loaded> LoadClosure(NpgsqlConnection conn, NpgsqlTransaction tx, ImmutableArray<Guid> requested, ReadCommandBudget quota, CancellationToken ct)
    {
        var postingSet = requested.ToHashSet();
        var obligationSet = new HashSet<Guid>();
        var obligations = new Dictionary<Guid, ClassifiedObligation>();
        var settlements = new Dictionary<Guid, ClassifiedSettlement>();
        // Bounded fixpoint: postings -> obligations -> settlements -> receipt postings -> ... Every iteration reserves its commands.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var before = (postingSet.Count, obligationSet.Count, settlements.Count);
            await using (var q = Cmd($"SELECT {NO.Id},{NO.PostingId},{NO.JournalEntryId},{NO.ObligationAccountId},{NO.VarianceAccountId},{NO.CounterAccountId} FROM {NO.Table} " +
                $"WHERE {NO.PostingId}=ANY(@p) OR {NO.Id}=ANY(@o) LIMIT @l", conn, tx, quota))
            {
                q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("o", obligationSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
                await using var r = await q.ExecuteReaderAsync(ct); var rows = 0;
                while (await r.ReadAsync(ct))
                {
                    if (++rows > limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);   // rows of THIS read, so a closure exactly at the limit is accepted
                    var o = new ClassifiedObligation(r.GetGuid(0), r.GetGuid(1), N(r, 2), N(r, 3), N(r, 4), N(r, 5));
                    obligations[o.Id] = o; obligationSet.Add(o.Id); postingSet.Add(o.PostingId);
                }
            }
            await using (var q = Cmd($"SELECT {NX.Id},{NX.ObligationId},{NX.ReceiptPostingId},{NX.JournalEntryId},{NX.ReversesSettlementId},{NX.ReversalId} FROM {NX.Table} " +
                $"WHERE {NX.ObligationId}=ANY(@o) OR {NX.ReceiptPostingId}=ANY(@p) LIMIT @l", conn, tx, quota))
            {
                q.Parameters.AddWithValue("o", obligationSet.ToArray()); q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
                await using var r = await q.ExecuteReaderAsync(ct); var rows = 0;
                while (await r.ReadAsync(ct))
                {
                    if (++rows > limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                    var s = new ClassifiedSettlement(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), N(r, 3), N(r, 4), N(r, 5), true);
                    settlements[s.Id] = s; obligationSet.Add(s.ObligationId); postingSet.Add(s.ReceiptPostingId);
                }
            }
            if (postingSet.Count > limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
            if (before == (postingSet.Count, obligationSet.Count, settlements.Count)) break;
        }
        // Net chain exactly as ForwardSettlementsAsync: both the reversed settlement and the negating row leave the NET chain (journals stay evidence).
        var reversedIds = settlements.Values.Where(x => x.ReversesSettlementId is not null).Select(x => x.ReversesSettlementId!.Value).ToHashSet();
        var settlementList = settlements.Values.Select(x => x with { InNetChain = x.ReversesSettlementId is null && !reversedIds.Contains(x.Id) })
            .OrderBy(s => s.Id).ToList();

        var obligationReversals = new List<ClassifiedObligationReversal>();
        await using (var q = Cmd($"SELECT {NR.Id},{NR.ObligationId},{NR.StockReversalId} FROM {NR.Table} WHERE {NR.ObligationId}=ANY(@o) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("o", obligationSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (obligationReversals.Count >= limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                obligationReversals.Add(new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2)));
            }
        }

        var reversals = new Dictionary<Guid, (Guid Id, short Action, Guid? Journal, Guid? Operation)>();
        var reversalRowOfPosting = new Dictionary<Guid, Guid>();
        await using (var q = Cmd($"SELECT {RV.Id},{RV.PostingId},{RV.JournalAction},{RV.JournalEntryId},{RV.CostOperationId} FROM {RV.Table} WHERE {RV.PostingId}=ANY(@p) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (reversals.Count >= limits.Postings) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                if (!reversals.TryAdd(r.GetGuid(1), (r.GetGuid(0), r.GetInt16(2), N(r, 3), N(r, 4)))) throw new Refusal("classification_inconsistent");
                reversalRowOfPosting[r.GetGuid(1)] = r.GetGuid(0);
            }
        }

        var postings = new Dictionary<Guid, ClassifiedPosting>();
        await using (var q = Cmd($"SELECT p.{SP.Id},p.{SP.DocumentId},p.{SP.DocType},p.{SP.EffectiveDate},p.{SP.TotalValue},p.{SP.AccountingVersionId},p.{SP.JournalEntryId}," +
            $"d.{SD.SourceWarehouseId},d.{SD.TargetWarehouseId},p.{SP.CostOperationId} FROM {SP.Table} p JOIN {SD.Table} d ON d.{SD.Id}=p.{SP.DocumentId} WHERE p.{SP.Id}=ANY(@p) LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("l", limits.Postings + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0); reversals.TryGetValue(id, out var rv);
                postings[id] = new(id, r.GetGuid(1), r.GetInt16(2), DateOnly.FromDateTime(r.GetDateTime(3)), r.GetDecimal(4), N(r, 5), N(r, 6), N(r, 7), N(r, 8),
                    reversals.ContainsKey(id) ? rv.Id : null, reversals.ContainsKey(id) ? rv.Action : null, reversals.ContainsKey(id) ? rv.Journal : null,
                    N(r, 9), reversals.ContainsKey(id) ? rv.Operation : null);
            }
        }
        // A requested id that is not a stock posting (missing, adjustment, foreign) is indistinguishable from a denied one.
        if (requested.Any(id => !postings.ContainsKey(id)) || postingSet.Any(id => !postings.ContainsKey(id))) throw new Denied();

        var events = new HashSet<ClassifiedEvent>();
        await using (var q = Cmd($"SELECT {PE.PostingId},{PE.Kind},{PE.JournalEntryId} FROM {PE.Table} WHERE {PE.PostingId}=ANY(@p) AND {PE.JournalEntryId} IS NOT NULL LIMIT @l", conn, tx, quota))
        {
            q.Parameters.AddWithValue("p", postingSet.ToArray()); q.Parameters.AddWithValue("l", limits.Journals * 4 + 1);
            await using var r = await q.ExecuteReaderAsync(ct); var n = 0;
            while (await r.ReadAsync(ct))
            {
                if (++n > limits.Journals * 4) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
                events.Add(new(r.GetGuid(0), r.GetInt16(1), r.GetGuid(2)));
            }
        }

        var journalIds = new HashSet<Guid>();
        foreach (var p in postings.Values) { if (p.JournalId is { } a) journalIds.Add(a); if (p.ReversalJournalId is { } b) journalIds.Add(b); }
        foreach (var o in obligations.Values) if (o.JournalId is { } j) journalIds.Add(j);
        foreach (var s in settlementList) if (s.JournalId is { } j) journalIds.Add(j);
        foreach (var e in events) journalIds.Add(e.JournalId);

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
                { Allocations = [.. allocations[lineIds[(id, l.No)]].OrderBy(a => a.CostCenterId).ThenBy(a => a.Amount).ThenBy(a => a.Base)] })] };

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
        return new(postings, [.. obligations.Values.OrderBy(o => o.Id)], settlementList, obligationReversals, journals, accounts, adjusted, events, versionAccounts, reversalRowOfPosting);
    }

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
        // Transfers (TM-D7, working baseline): no journal and no accounting version by design, one item, same inventory account on both legs, same branch
        // (cross-branch transfers are refused at validation). Their effect reaches the ledger only through downstream issues. A transfer WITH a journal or version
        // is inconsistent; a transfer is never "classified", it is journal-less by construction.
        foreach (var t in d.Postings.Values.Where(p => p.DocType == (int)StockDocType.Transfer))
            if (t.JournalId is not null || t.VersionId is not null || t.ReversalJournalId is not null || t.SourceWarehouse is null || t.TargetWarehouse is null)
                throw new Refusal("classification_inconsistent");
        if (d.Journals.Keys.Any(d.AdjustedJournals.Contains)) throw new Refusal("open_decision");
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
        // A journal that reverses a closure journal but that no posting, obligation, settlement or event references is a manual reversal in progress
        // (the effect is still live while a reversal is pending): an owner decision, never silently ignored.
        var referenced = new HashSet<Guid>();
        foreach (var p in d.Postings.Values) { if (p.JournalId is { } x) referenced.Add(x); if (p.ReversalJournalId is { } y) referenced.Add(y); }
        foreach (var o in d.Obligations) if (o.JournalId is { } x) referenced.Add(x);
        foreach (var x in d.Settlements) if (x.JournalId is { } y) referenced.Add(y);
        foreach (var e in d.Events) referenced.Add(e.JournalId);
        if (d.Journals.Values.Any(j => j.Reverses is not null && !referenced.Contains(j.Id))) throw new Refusal("open_decision");

        // Cross-check only: a journal whose accounts the accounting version could not have produced is AMBIGUOUS, never silently resolved.
        void CrossCheck(ClassifiedJournal journal, HashSet<Guid> allowed)
        {
            if (journal.Lines.Any(l => !allowed.Contains(l.AccountId))) throw new Refusal("classification_ambiguous");
        }
        HashSet<Guid> VersionOf(ClassifiedPosting posting) =>
            posting.VersionId is { } v && d.VersionAccounts.TryGetValue(v, out var set) ? set : throw new Refusal("classification_inconsistent");

        foreach (var p in d.Postings.Values.Where(x => x.DocType != (int)StockDocType.Transfer))
        {
            if (p.ReversalId is null)
                // A receipt fully matched against negative obligations books nothing of its own: its value sits in the settlement journals.
                LiveJournal(p.JournalId, p.TotalValue == 0 || d.Settlements.Any(x => x.ReceiptPostingId == p.Id && x.ReversesSettlementId is null));
            else ReversedJournal(p.JournalId, p.ReversalJournalId, p.ReversalAction);
            if (p.JournalId is { } jid) CrossCheck(Journal(jid), VersionOf(p));   // a journal without a recorded version is inconsistent
        }
        foreach (var o in d.Obligations)
        {
            var issue = d.Postings[o.PostingId];
            if (o.JournalId != issue.JournalId) throw new Refusal("classification_inconsistent"); // obligation journal IS the issue posting journal
            if (issue.ReversalId is { } reversal)
            {
                // A reversed issue needs its obligation reversal row, and nothing may remain in the net settlement chain.
                if (!d.ObligationReversals.Any(x => x.ObligationId == o.Id && x.StockReversalId == reversal)) throw new Refusal("classification_inconsistent");
                if (d.Settlements.Any(x => x.ObligationId == o.Id && x.InNetChain)) throw new Refusal("classification_inconsistent");
            }
        }
        foreach (var r in d.ObligationReversals)
        {
            var o = d.Obligations.FirstOrDefault(x => x.Id == r.ObligationId) ?? throw new Refusal("classification_inconsistent");
            if (d.Postings[o.PostingId].ReversalId != r.StockReversalId) throw new Refusal("classification_unavailable"); // path not traceable: fail closed
        }
        var byId = d.Settlements.ToDictionary(x => x.Id);
        var negating = d.Settlements.Where(x => x.ReversesSettlementId is not null).ToList();
        if (negating.GroupBy(x => x.ReversesSettlementId).Any(g => g.Count() > 1)) throw new Refusal("classification_inconsistent");   // reversed twice
        foreach (var s in d.Settlements)
        {
            var obligation = d.Obligations.FirstOrDefault(x => x.Id == s.ObligationId) ?? throw new Refusal("classification_inconsistent");
            if (s.JournalId is { } sj)
            {
                // Settlement accounts: O and V (and the counter) are stored on the obligation, G only in the journal lines (the receipt's version).
                var allowed = new HashSet<Guid>(new[] { obligation.ObligationAccountId, obligation.VarianceAccountId, obligation.CounterAccountId }.Where(a => a.HasValue).Select(a => a!.Value));
                var journal = Journal(sj);
                // A zero-value receipt has no recorded version (nothing to configure); its settlement journal then only uses the obligation's own accounts.
                // The receipt's version is required exactly when the journal needs an account beyond them (the receipt counter account G).
                if (journal.Lines.Any(l => !allowed.Contains(l.AccountId))) allowed.UnionWith(VersionOf(d.Postings.GetValueOrDefault(s.ReceiptPostingId) ?? throw new Refusal("classification_inconsistent")));
                CrossCheck(journal, allowed);
            }
            if (s.ReversesSettlementId is { } reversed)
            {
                var original = byId.GetValueOrDefault(reversed) ?? throw new Refusal("classification_inconsistent");
                if (original.ReversesSettlementId is not null) throw new Refusal("classification_inconsistent");   // a negating row is never itself reversed
                // The settlement rows store no JournalAction; the Reverses link is the proof (see ReversedJournal).
                ReversedJournal(original.JournalId, s.JournalId, original.JournalId is null ? (short)0 : (short)2);
                if (original.JournalId is null && s.JournalId is not null) throw new Refusal("classification_inconsistent");
            }
            else if (!byId.Values.Any(x => x.ReversesSettlementId == s.Id)) LiveJournal(s.JournalId, true);
            // A reversed original settlement is covered by its negating row above.
        }
        var postings = d.Postings.Values.OrderBy(x => x.Id).ToImmutableArray();
        var journals = d.Journals.Values.OrderBy(x => x.Id).ToImmutableArray();
        var accounts = d.Accounts.Values.OrderBy(x => x.Id).ToImmutableArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Contract = "stock-classification-evidence-v1", Company = company, Requested = requested.Distinct().OrderBy(i => i).ToArray(), Postings = postings,
            Obligations = d.Obligations.OrderBy(x => x.Id).ToArray(), Settlements = d.Settlements.OrderBy(x => x.Id).ToArray(),
            ObligationReversals = d.ObligationReversals.OrderBy(x => x.Id).ToArray(), Journals = journals, Accounts = accounts,
            Events = d.Events.OrderBy(e => e.PostingId).ThenBy(e => e.Kind).ThenBy(e => e.JournalId).ToArray()
        }, Canonical))).ToLowerInvariant();
        return new(postings, [.. d.Obligations], [.. d.Settlements], journals, accounts, fingerprint);
    }

    // decimal is written without its scale so 1.0 and 1.00 are the same evidence.
    internal static readonly JsonSerializerOptions Canonical = new() { Converters = { new CanonicalDecimal() } };
    private sealed class CanonicalDecimal : System.Text.Json.Serialization.JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue((value / 1.0000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
