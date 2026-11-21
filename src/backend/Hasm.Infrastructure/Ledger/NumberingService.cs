using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;
using NR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberingRule;
using NC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberCounter;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;

namespace Hasm.Infrastructure.Ledger;

public static class LedgerAuditEvents
{
    public const short NumberingRuleChanged = 1036;
    public const short ExchangeRateEntered = 1037;
    public const short CostCenterCreated = 1038;
    public const short CostCenterUpdated = 1039;
    public const short CostCenterCodeChanged = 1040;
    public const short CostCenterMoved = 1041;
    public const short CostCenterDeactivated = 1042;
    public const short CostCenterReactivated = 1043;
    public const short ApprovalPolicyCreated = 1044;
    public const short ApprovalPolicyUpdated = 1045;
    public const short ApprovalPolicyDeactivated = 1046;
    public const short ApprovalPolicyReactivated = 1047;
    public const short ApprovalDecisionRecorded = 1048;
    public const short JournalCreated = 1049;
    public const short JournalUpdated = 1050;
    public const short JournalSubmitted = 1051;
    public const short JournalReturnedToDraft = 1052;
    public const short JournalCancelled = 1053;
    public const short JournalApproved = 1054;
    public const short JournalRejected = 1055;
    public const short JournalPosted = 1056;
    public const short JournalReversalDrafted = 1057;
    public const short JournalAdjustingDrafted = 1058;
    public const short JournalEventLogged = 1059;
    public const short LedgerSettingsChanged = 1060;
    public const short AccountCostCenterRuleChanged = 1061;
    public const short CostCenterBranchEligibilityChanged = 1062;
    public const short ClosingSettingsChanged = 1063;
    public const short FiscalYearClosingStarted = 1064;
    public const short FiscalYearCarryforwardStarted = 1065;
}

/// <summary>
/// Document numbering (Phase 3.2B, slice S1). <b>No authorization here by design:</b> <see cref="NumberingManagementService"/> wraps the configuration side.
/// <see cref="IssueAsync"/> is called only INSIDE the transaction that creates the document (the posting engine), so a rollback leaves no gap: the counter row
/// is locked by its own INSERT ... ON CONFLICT DO UPDATE, concurrent issuers queue on it, and PostgreSQL independently refuses a counter that skips a value,
/// a counter value without its issue record, a duplicate number, and any change or deletion of an issued number (a voided number stays recorded forever and
/// is therefore never issued again).
/// </summary>
public sealed class NumberingService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    /// <summary>
    /// Whether a failure is the number-issue uniqueness guard refusing a number that is already issued (the counter and the issued numbers disagree, e.g. after a direct write). Callers map it to the stable,
    /// retryable refusal <c>concurrent_change</c>: the whole transaction rolls back, so the counter does not move and no number is consumed. Narrowed to that one index so no other unique violation is masked.
    /// </summary>
    public static bool IsNumberCollision(PostgresException ex) => ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == NI.UniqueNumber;

    internal const int LockKey1 = 0x48434E52;   // "HCNR": advisory-lock namespace of numbering configuration
    internal const int LockKey2 = 1;
    internal static readonly Guid None = Guid.Empty;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, null, currentRevision);
    }

    public async Task<IReadOnlyList<NumberingRuleView>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var list = new List<NumberingRuleView>();
        foreach (var t in DocumentTypes.All) list.Add(await ReadAsync(conn, null, t, false, ct));
        return list;
    }

    public async Task<NumberingRuleView?> GetAsync(Guid companyId, string docType, CancellationToken ct = default)
    {
        if (!DocumentTypes.IsKnown(docType)) return null;
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await ReadAsync(conn, null, docType, false, ct);
    }

    internal static async Task<NumberingRuleView> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string docType, bool forUpdate, CancellationToken ct)
    {
        bool issued;
        await using (var any = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {NI.Table} WHERE {NI.DocType} = @d)", conn, tx))
        {
            any.Parameters.AddWithValue("d", docType);
            issued = (bool)(await any.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand(
            $"SELECT {NR.Prefix}, {NR.Separator}, {NR.Width}, {NR.PerBranch}, {NR.PerFiscalYear}, {NR.Revision} FROM {NR.Table} WHERE {NR.DocType} = @d{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("d", docType);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return NumberingRuleView.Default(docType) with { NumbersIssued = issued };
        return new NumberingRuleView(docType, r.GetString(0), r.GetString(1), r.GetInt16(2), r.GetBoolean(3), r.GetBoolean(4), r.GetInt64(5), true, issued);
    }

    public async Task<ConfigResult<NumberingRuleView>> PutRuleAsync(Guid actor, Guid companyId, string docType, long? expectedRevision, NumberingRuleInput? input, CancellationToken ct = default)
    {
        if (!DocumentTypes.IsKnown(docType)) return ConfigResult<NumberingRuleView>.Fail(ConfigStatus.NotFound, "document_type_not_found");
        if (expectedRevision is null) return ConfigResult<NumberingRuleView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!NumberingValidation.TryNormalize(input, out var prefix, out var sep, out var width, out var perBranch, out var perYear, out var errors)) return ConfigResult<NumberingRuleView>.Invalid(errors);
        try
        {
            return ConfigResult<NumberingRuleView>.Ok(await transactions.ExecuteAsync(companyId, async work =>
            {
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
                {
                    lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2);
                    await lk.ExecuteNonQueryAsync(ct);
                }
                var cur = await ReadAsync(work.Connection, work.Transaction, docType, true, ct);
                if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
                if (cur.Exists && cur.Prefix == prefix && cur.Separator == sep && cur.Width == width && cur.PerBranch == perBranch && cur.PerFiscalYear == perYear) throw new Abort(ConfigStatus.Ok, "unchanged");
                if (cur.NumbersIssued && (cur.PerBranch != perBranch || cur.PerFiscalYear != perYear)) throw new Abort(ConfigStatus.Conflict, "numbering_scope_locked");
                var now = clock.GetUtcNow().UtcDateTime;
                await using (var w = new NpgsqlCommand(cur.Exists
                    ? $"UPDATE {NR.Table} SET {NR.Prefix} = @p, {NR.Separator} = @s, {NR.Width} = @w, {NR.PerBranch} = @b, {NR.PerFiscalYear} = @y, {NR.Revision} = {NR.Revision} + 1, {NR.UpdatedAtUtc} = @now WHERE {NR.DocType} = @d"
                    : $"INSERT INTO {NR.Table} ({NR.DocType}, {NR.Prefix}, {NR.Separator}, {NR.Width}, {NR.PerBranch}, {NR.PerFiscalYear}, {NR.Revision}, {NR.UpdatedAtUtc}) VALUES (@d, @p, @s, @w, @b, @y, 1, @now)",
                    work.Connection, work.Transaction))
                {
                    w.Parameters.AddWithValue("d", docType); w.Parameters.AddWithValue("p", prefix); w.Parameters.AddWithValue("s", sep);
                    w.Parameters.AddWithValue("w", (short)width); w.Parameters.AddWithValue("b", perBranch); w.Parameters.AddWithValue("y", perYear); w.Parameters.AddWithValue("now", now);
                    if (await w.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("numbering rule not written");
                }
                var revision = cur.Revision + 1;
                var changes = new JsonObject();
                void Diff(string n, object? o, object? v) { if (!Equals(o, v)) changes[n] = new JsonObject { ["old"] = JsonValue.Create(o), ["new"] = JsonValue.Create(v) }; }
                Diff("prefix", cur.Prefix, prefix); Diff("separator", cur.Separator, sep); Diff("width", cur.Width, width); Diff("perBranch", cur.PerBranch, perBranch); Diff("perFiscalYear", cur.PerFiscalYear, perYear);
                var json = changes.ToJsonString();
                var receipt = await work.AppendAuditAsync(new AuditEventData(LedgerAuditEvents.NumberingRuleChanged, ActorUserId: actor,
                    Detail: $"numbering rule {docType} r{revision} f={string.Join(',', changes.Select(c => c.Key))} h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
                await using var h = new NpgsqlCommand(
                    $"INSERT INTO {LH.Table} ({LH.Entity}, {LH.EntityKey}, {LH.Revision}, {LH.ActorUserId}, {LH.ChangedAtUtc}, {LH.Changes}, {LH.AuditStream}, {LH.AuditSeq}) VALUES (1, @k, @r, @a, @t, @c, @s, @q)",
                    work.Connection, work.Transaction);
                h.Parameters.AddWithValue("k", docType); h.Parameters.AddWithValue("r", revision); h.Parameters.AddWithValue("a", actor); h.Parameters.AddWithValue("t", now);
                h.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
                h.Parameters.AddWithValue("s", receipt.StreamKey); h.Parameters.AddWithValue("q", receipt.Seq);
                await h.ExecuteNonQueryAsync(ct);
                return cur with { Prefix = prefix, Separator = sep, Width = width, PerBranch = perBranch, PerFiscalYear = perYear, Revision = revision, Exists = true };
            }, ct));
        }
        catch (Abort a) when (a.Message == "unchanged") { return ConfigResult<NumberingRuleView>.Ok((await GetAsync(companyId, docType, ct))!); }
        catch (Abort a) { return a.To<NumberingRuleView>(); }
    }

    /// <summary>
    /// Issues the next official number for a document inside the CALLER's transaction. The scope decides the counter: a per-branch rule needs a branch,
    /// a per-fiscal-year rule needs a fiscal year; a scope the rule does not use is ignored (the all-zero uuid stands for "none").
    /// </summary>
    public static Task<IssuedNumber> IssueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string docType, NumberScope scope, Guid documentId, Guid actor, DateTime nowUtc, CancellationToken ct)
    {
        if (!DocumentTypes.IsKnown(docType)) throw new ArgumentException("unknown document type", nameof(docType));
        return IssueCoreAsync(conn, tx, docType, scope, documentId, actor, nowUtc, ct);
    }

    /// <summary>Same issue for a type the CALLER vouches for from its own registry (commercial documents: the internal test type is registered only in the test composition, D-049). Not part of the public surface.</summary>
    internal static Task<IssuedNumber> IssueForRegisteredTypeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string docType, NumberScope scope, Guid documentId, Guid actor, DateTime nowUtc, CancellationToken ct) =>
        IssueCoreAsync(conn, tx, docType, scope, documentId, actor, nowUtc, ct);

    private static async Task<IssuedNumber> IssueCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string docType, NumberScope scope, Guid documentId, Guid actor, DateTime nowUtc, CancellationToken ct)
    {
        var rule = await ReadAsync(conn, tx, docType, false, ct);
        if (rule.PerBranch && (scope.BranchId is null || string.IsNullOrWhiteSpace(scope.BranchCode))) throw new InvalidOperationException("a branch is required to number this document");
        if (rule.PerFiscalYear && (scope.FiscalYearId is null || string.IsNullOrWhiteSpace(scope.FiscalYearLabel))) throw new InvalidOperationException("a fiscal year is required to number this document");
        var branch = rule.PerBranch ? scope.BranchId!.Value : None;
        var year = rule.PerFiscalYear ? scope.FiscalYearId!.Value : None;

        long value;
        await using (var c = new NpgsqlCommand(
            $"INSERT INTO {NC.Table} ({NC.DocType}, {NC.BranchId}, {NC.FiscalYearId}, {NC.LastValue}) VALUES (@d, @b, @y, 1) " +
            $"ON CONFLICT ({NC.DocType}, {NC.BranchId}, {NC.FiscalYearId}) DO UPDATE SET {NC.LastValue} = {NC.Table}.{NC.LastValue} + 1 RETURNING {NC.LastValue}", conn, tx))
        {
            c.Parameters.AddWithValue("d", docType); c.Parameters.AddWithValue("b", branch); c.Parameters.AddWithValue("y", year);
            value = (long)(await c.ExecuteScalarAsync(ct))!;
        }
        var number = Format(rule, scope, value);
        await using var i = new NpgsqlCommand(
            $"INSERT INTO {NI.Table} ({NI.DocType}, {NI.Number}, {NI.BranchId}, {NI.FiscalYearId}, {NI.Value}, {NI.DocumentId}, {NI.IssuedAtUtc}, {NI.IssuedBy}) VALUES (@d, @n, @b, @y, @v, @doc, @t, @a)", conn, tx);
        i.Parameters.AddWithValue("d", docType); i.Parameters.AddWithValue("n", number); i.Parameters.AddWithValue("b", branch); i.Parameters.AddWithValue("y", year);
        i.Parameters.AddWithValue("v", value); i.Parameters.AddWithValue("doc", documentId); i.Parameters.AddWithValue("t", nowUtc); i.Parameters.AddWithValue("a", actor);
        await i.ExecuteNonQueryAsync(ct);   // a number that already exists violates UNIQUE(document type, number): the whole posting fails and nothing is issued
        return new IssuedNumber(number, value, branch, year);
    }

    /// <summary>The number is the non-empty segments joined by the separator: [prefix] [fiscal year label] [branch code] [zero-padded sequence].</summary>
    internal static string Format(NumberingRuleView rule, NumberScope scope, long value)
    {
        var parts = new List<string>();
        if (rule.Prefix.Length > 0) parts.Add(rule.Prefix);
        if (rule.PerFiscalYear) parts.Add(scope.FiscalYearLabel!.Trim());
        if (rule.PerBranch) parts.Add(scope.BranchCode!.Trim());
        parts.Add(value.ToString().PadLeft(rule.Width, '0'));
        return string.Join(rule.Separator, parts);
    }

    /// <summary>Marks an issued number as void (the number stays recorded and is never issued again). Once only; the reason is mandatory.</summary>
    public static async Task VoidAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string docType, string number, Guid actor, string reason, DateTime nowUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 200) throw new ArgumentException("a void reason of 1-200 characters is required", nameof(reason));
        await using var cmd = new NpgsqlCommand($"UPDATE {NI.Table} SET {NI.VoidedAtUtc} = @t, {NI.VoidedBy} = @a, {NI.VoidReason} = @r WHERE {NI.DocType} = @d AND {NI.Number} = @n", conn, tx);
        cmd.Parameters.AddWithValue("t", nowUtc); cmd.Parameters.AddWithValue("a", actor); cmd.Parameters.AddWithValue("r", reason.Trim()); cmd.Parameters.AddWithValue("d", docType); cmd.Parameters.AddWithValue("n", number);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("issued number not found or already void");
    }
}
