using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Npgsql;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using FY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LegalHold;
using H = Hasm.Infrastructure.SystemDatabase.SystemSchema.BackupLegalHold;
using P = Hasm.Infrastructure.SystemDatabase.SystemSchema.BackupRetentionPin;
using ST = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Settings;
using AE = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Infrastructure.Backup;

/// <summary>Everything the classifier needs, read at one moment: the runs of the root, the companies' calendars and hold states, the backup holds and the pins.</summary>
public sealed record RetentionInputs(IReadOnlyList<RetentionRun> Runs, IReadOnlyList<RetentionCompany> Companies, IReadOnlyList<BackupLegalHoldRow> Holds, IReadOnlyList<RetentionPinRow> Pins,
    IReadOnlyList<string> DeletingFolders);

/// <summary>The outcome of <c>backup-retention-apply</c>: what was deleted, what was skipped and why (codes only).</summary>
public sealed record RetentionApplyResult(IReadOnlyList<string> Deleted, IReadOnlyList<(string RunId, string Code)> Skipped, int Resumed);

/// <summary>Scans a backup root for retention (D-130): run folders only, their VERIFIED signed status, cleanliness, kept trials and the proof. Never decrypts, never writes.</summary>
public static partial class RetentionInventory
{
    public const string RetentionFolder = ".retention";
    public const string TombstonesFolder = "tombstones";
    public const string DeletedMarker = "deleted.json";
    /// <summary>Written into a tombstone after audit 2016, so a deletion whose 2016 was lost by a crash is detected and completed (review L2 of #134).</summary>
    public const string CompletedMarker = "completed.json";
    [GeneratedRegex(@"^(run\.status\.json(\.sig)?|manifest\.json\.hbe(\.sig)?|canary\.hbe|recovery-test\.json|system/(database\.dump\.hbe|manifest\.json\.hbe(\.sig)?)|companies/[0-9a-f]{32}/(database\.dump\.hbe|manifest\.json\.hbe(\.sig)?|attachments/[0-9a-f]{32}\.att\.hbe))$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedFile();
    [GeneratedRegex(@"^(system|companies|companies/[0-9a-f]{32}|companies/[0-9a-f]{32}/attachments)$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedDirectory();
    [GeneratedRegex(@"^companies/[0-9a-f]{32}\.trial-[0-9a-f]{32}\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex KeptTrialFile();
    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}\.deleting-[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    internal static partial Regex DeletingFolder();

    /// <summary>The run folders whose deletion started and did not finish (<c>&lt;run id&gt;.deleting-&lt;hex&gt;</c>); reported by backup-status as <c>retention_deletion_incomplete</c>.</summary>
    public static int DeletingFolderCount(string root) =>
        Directory.Exists(root) ? new DirectoryInfo(root).EnumerateDirectories().Count(d => DeletingFolder().IsMatch(d.Name)) : 0;

    public static (IReadOnlyList<RetentionRun> Runs, IReadOnlyList<string> Deleting) Scan(string root, SigningTrust? trust)
    {
        var runs = new List<RetentionRun>();
        var deleting = new List<string>();
        if (!Directory.Exists(root)) return (runs, deleting);
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (DeletingFolder().IsMatch(d.Name)) { deleting.Add(d.Name); continue; }
            if (!BackupRunFiles.IsRunId(d.Name)) continue;
            runs.Add(Read(d, trust));
        }
        return (runs, deleting);
    }

    internal static RetentionRun Read(DirectoryInfo d, SigningTrust? trust)
    {
        var started = BackupRunFiles.StartOf(d.Name)!.Value;
        RetentionRun Unknown(bool complete, bool clean = true) => new(d.Name, started, started, complete, false, false, null, null, false, [], null, clean, false, null);
        if (d.LinkTarget is not null) return Unknown(false, clean: false);
        var statusPath = Path.Combine(d.FullName, SignedDocuments.RunStatusFileName);
        if (!File.Exists(statusPath))
        {
            // an unencrypted DEV/TEST run (run.json) is complete and readable, never deleted automatically; no report at all is an incomplete run
            var report = BackupRunFiles.ReadReport(d.FullName);
            return report is null ? Unknown(false)
                : new RetentionRun(d.Name, report.StartedUtc, report.FinishedUtc, true, true, false, report.Verdict, null, false,
                    [.. report.Companies.Select(c => new RunStatusCompany(c.CompanyId, c.Outcome))], null, true, false, null);
        }
        var (clean, keptTrial) = Inspect(d.FullName);
        if (trust is null) return Unknown(true, clean) with { KeptTrial = keptTrial };
        try
        {
            var s = SignedDocuments.ReadRunStatus(d.FullName, trust);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(statusPath)));
            // review M2 of #130: key needs are known only from a verified version 2 status
            var keys = s.Version == RunStatus.CurrentVersion && s.AuditKeyIds is not null && s.Companies.All(c => c.KeyIdsRequired is not null)
                ? new RunKeyNeeds([.. s.Companies.SelectMany(c => c.KeyIdsRequired!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], s.AuditKeyIds)
                : null;
            return new RetentionRun(d.Name, s.StartedUtc, s.FinishedUtc, true, true, s.Encrypted, s.Verdict, s.RecoveryKeyId, RecoveryTestProof.IsValidFor(d.FullName, s),
                s.Companies, hash, clean, keptTrial, keys);
        }
        catch (BackupException) { return Unknown(true, clean) with { KeptTrial = keptTrial }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Unknown(true, false); }
    }

    /// <summary>Whether the run holds only the files slice 5 writes (plus the proof), no symbolic link anywhere, and whether a kept trial references it.</summary>
    internal static (bool Clean, bool KeptTrial) Inspect(string runDirectory)
    {
        var clean = true; var kept = false;
        var stack = new Stack<DirectoryInfo>([new DirectoryInfo(runDirectory)]);
        while (stack.Count > 0)
        {
            foreach (var e in stack.Pop().EnumerateFileSystemInfos())
            {
                var rel = Path.GetRelativePath(runDirectory, e.FullName).Replace(Path.DirectorySeparatorChar, '/');
                if (e.LinkTarget is not null) { clean = false; continue; }
                if (e is DirectoryInfo sub)
                {
                    if (!AllowedDirectory().IsMatch(rel)) clean = false;
                    stack.Push(sub);
                }
                else if (KeptTrialFile().IsMatch(rel)) kept = true;
                else if (!AllowedFile().IsMatch(rel)) clean = false;
            }
        }
        return (clean, kept);
    }
}

/// <summary>Backup legal holds (D-134): placed through the administrative tool with audit 2018 in the same transaction; never released in this slice.</summary>
public sealed class BackupHoldStore(HasmDatabaseOptions options, AuditTrail trail)
{
    public async Task<Guid> PlaceAsync(string? runId, Guid? companyId, DateOnly? from, DateOnly? to, string reason, string operatorName, CancellationToken ct = default)
    {
        if ((runId is null) == (companyId is null) || (runId is not null && (!BackupRunFiles.IsRunId(runId) || from is not null || to is not null))) throw new BackupException("hold_scope_invalid");
        if (companyId == Guid.Empty || (from is { } f && to is { } t && f > t)) throw new BackupException("hold_scope_invalid");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3 || reason.Length > 500) throw new BackupException("hold_reason_invalid");
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand($"INSERT INTO {H.Table} ({H.Id}, {H.Scope}, {H.RunId}, {H.CompanyId}, {H.FromDate}, {H.ToDate}, {H.Reason}, {H.PlacedBy}, {H.PlacedAtUtc}) " +
                                                 "VALUES (@id, @s, @r, @c, @f, @t, @why, @by, now())", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("s", (short)(runId is null ? 2 : 1));
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)runId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)companyId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlTypes.NpgsqlDbType.Date) { Value = (object?)from ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlTypes.NpgsqlDbType.Date) { Value = (object?)to ?? DBNull.Value });
            cmd.Parameters.AddWithValue("why", reason.Trim());
            cmd.Parameters.AddWithValue("by", operatorName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var receipt = await trail.AppendAsync(conn, tx, AuditStream.System, AuditCanonical.FromMicros(AuditCanonical.ToMicros(DateTimeOffset.UtcNow)),
            new AuditEventData(BackupAuditEvents.LegalHoldPlaced, SubjectId: id, Detail:
                $"backup legal hold placed operator={operatorName} hold={id:D} scope={(runId is null ? "company" : "run")} run={runId ?? "none"} company={(companyId is { } c ? c.ToString("D") : "none")} from={from?.ToString("yyyy-MM-dd") ?? "open"} to={to?.ToString("yyyy-MM-dd") ?? "open"} reason_length={reason.Trim().Length}"), ct);
        await trail.CommitAsync(tx, receipt, ct);
        return id;
    }

    public static async Task<IReadOnlyList<BackupLegalHoldRow>> ListAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var rows = new List<BackupLegalHoldRow>();
        await using var cmd = new NpgsqlCommand($"SELECT {H.Id}, {H.Scope}, {H.RunId}, {H.CompanyId}, {H.FromDate}, {H.ToDate}, {H.ReleasedAtUtc} IS NOT NULL FROM {H.Table} ORDER BY {H.PlacedAtUtc}, {H.Id}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new BackupLegalHoldRow(r.GetGuid(0), r.GetInt16(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3),
                r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4)), r.IsDBNull(5) ? null : DateOnly.FromDateTime(r.GetDateTime(5)), r.GetBoolean(6)));
        return rows;
    }

    public static async Task<IReadOnlyList<RetentionPinRow>> ListPinsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var rows = new List<RetentionPinRow>();
        await using var cmd = new NpgsqlCommand($"SELECT {P.Id}, {P.RunId}, {P.Kind}, {P.ExpiresAtUtc} FROM {P.Table}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new RetentionPinRow(r.GetGuid(0), r.GetString(1), r.GetInt16(2), new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero)));
        return rows;
    }
}

/// <summary>
/// The test-only override of D-137 layers 1 and 3 (and of the presence check of the slice 7 restore safeguards, which cannot exist yet). Review M1 of #130: it is the ONLY way past the gate, it is
/// <c>internal</c> (visible to the test assemblies only), and no production code creates one (pinned by a unit test that scans the source tree).
/// </summary>
internal sealed class RetentionGateOverride
{
    private RetentionGateOverride() { }
    internal static RetentionGateOverride ForTests() => new();
}

/// <summary>
/// The slice 7 restore safeguards (D-081 cutover with its pinned safety backup) are a runtime prerequisite of automatic deletion (D-137 layer 2). Slice 7 sets this when it delivers them.
/// </summary>
public static class RestoreSafeguards
{
    public const bool CutoverAvailable = false;
}

/// <summary>
/// Retention (Phase 4.3 slice 6, D-130..D-139): the preview (<see cref="PlanAsync"/>) and the INACTIVE deletion (<see cref="ApplyAsync"/>, D-137). Holds, pins and company calendars are read from the
/// databases; the runs from the root; nothing is decrypted.
/// </summary>
public sealed class RetentionService
{
    internal const int RestoreLockObject = 1;
    private readonly HasmDatabaseOptions options;
    private readonly RetentionOptions retention;
    private readonly TimeProvider clock;
    private readonly CompanyRegistry registry;

    public RetentionService(HasmDatabaseOptions options, RetentionOptions retention, TimeProvider? clock = null)
    {
        this.options = options;
        this.retention = retention;
        this.clock = clock ?? TimeProvider.System;
        registry = new CompanyRegistry(options.BuildSystemConnectionString(admin: true));
    }

    /// <summary>Reads every input of the classification at one moment. Any failure to read holds or pins throws (no plan, no deletion).</summary>
    public async Task<RetentionInputs> ReadInputsAsync(string root, SigningTrust? trust, CancellationToken ct = default)
    {
        var (runs, deleting) = RetentionInventory.Scan(root, trust);
        IReadOnlyList<BackupLegalHoldRow> holds; IReadOnlyList<RetentionPinRow> pins;
        try
        {
            await using var sys = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
            await sys.OpenAsync(ct);
            holds = await BackupHoldStore.ListAsync(sys, ct);
            pins = await BackupHoldStore.ListPinsAsync(sys, ct);
        }
        catch (NpgsqlException) { throw new BackupException("hold_check_failed"); }
        var companies = new List<RetentionCompany>();
        foreach (var id in runs.SelectMany(r => r.Companies).Select(c => c.CompanyId).Distinct()) companies.Add(await ReadCompanyAsync(id, ct));
        return new RetentionInputs(runs, companies, holds, pins, deleting);
    }

    public async Task<(RetentionPlan Plan, RetentionInputs Inputs)> PlanAsync(string root, SigningTrust? trust, CancellationToken ct = default)
    {
        retention.Validate();
        var resolved = BackupSet.RealPath(root);
        var inputs = await ReadInputsAsync(resolved, trust, ct);
        return (RetentionClassifier.Plan(inputs.Runs, inputs.Companies, inputs.Holds, inputs.Pins, retention, clock.GetUtcNow()), inputs);
    }

    /// <summary>
    /// The company facts of the classification (D-131, D-134): its configured time zone and fiscal calendar, and its attachment-hold state. A company that is not registered, not Ready or cannot be read
    /// is unreadable (all of its runs are kept).
    /// </summary>
    internal async Task<RetentionCompany> ReadCompanyAsync(Guid companyId, CancellationToken ct)
    {
        // D-134, D-114 (review H2 of #134): the Phase R legal holds (company table LegalHold) are read below. Tripwire: if the company schema ever gains another hold table that this reader does not
        // know, every company is hold_check_failed (fail closed) until retention reads it; a unit test pins the known keys so a rename breaks the build instead.
        var unknownHoldTables = UnknownHoldTables();
        try
        {
            var company = await registry.GetAsync(companyId, ct);
            if (company is null || company.State != ProvisioningState.Ready) return new RetentionCompany(companyId, false, null, []);
            await using var db = new NpgsqlConnection(options.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName));
            await db.OpenAsync(ct);
            string? zone;
            await using (var cmd = new NpgsqlCommand($"SELECT {ST.TimeZone} FROM {ST.Table}", db)) zone = await cmd.ExecuteScalarAsync(ct) as string;
            var periods = new Dictionary<Guid, List<RetentionPeriod>>();
            await using (var cmd = new NpgsqlCommand($"SELECT {FP.YearId}, {FP.StartDate}, {FP.EndDate} FROM {FP.Table}", db))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    if (!periods.TryGetValue(r.GetGuid(0), out var list)) periods[r.GetGuid(0)] = list = [];
                    list.Add(new RetentionPeriod(DateOnly.FromDateTime(r.GetDateTime(1)), DateOnly.FromDateTime(r.GetDateTime(2))));
                }
            var years = new List<RetentionFiscalYear>();
            await using (var cmd = new NpgsqlCommand($"SELECT {FY.Id}, {FY.StartDate}, {FY.EndDate}, {FY.TimeZone} FROM {FY.Table}", db))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    years.Add(new RetentionFiscalYear(DateOnly.FromDateTime(r.GetDateTime(1)), DateOnly.FromDateTime(r.GetDateTime(2)), r.IsDBNull(3) ? null : r.GetString(3),
                        periods.GetValueOrDefault(r.GetGuid(0)) ?? []));
            // review H2 of #134: ANY active Phase R legal hold of the company (company, record or attachment scope) keeps every run containing it (attachment_hold); a company whose migration
            // level predates R1 has no hold table and can have no hold; a hold table that cannot be read keeps everything (hold_check_failed)
            string? holdCode = unknownHoldTables ? "hold_check_failed" : null;
            if (holdCode is null)
            {
                try
                {
                    await using var exists = new NpgsqlCommand($"SELECT to_regclass('public.{LH.Table}') IS NOT NULL", db);
                    if ((bool)(await exists.ExecuteScalarAsync(ct))!)
                    {
                        await using var active = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {LH.Table} WHERE {LH.ReleasedAtUtc} IS NULL)", db);
                        if ((bool)(await active.ExecuteScalarAsync(ct))!) holdCode = "attachment_hold";
                    }
                    else
                    {
                        // review L5 of #134: the table is missing. That means "no hold" ONLY when the company's migration level predates the migration that creates it; at that level or later
                        // a dropped or renamed table keeps every run (fail closed)
                        await using var level = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {Hasm.Infrastructure.SystemDatabase.SystemSchema.MigrationJournalTable} WHERE version >= @v)", db);
                        level.Parameters.AddWithValue("v", LegalHoldMigrationVersion);
                        if ((bool)(await level.ExecuteScalarAsync(ct))!) holdCode = "hold_check_failed";
                    }
                }
                catch (NpgsqlException) { holdCode = "hold_check_failed"; }
            }
            return new RetentionCompany(companyId, true, zone, years, holdCode);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or InvalidCastException) { return new RetentionCompany(companyId, false, null, []); }
    }

    /// <summary>The company-schema hold tables retention reads (review H2 of #134); any OTHER table whose logical name contains "Hold" trips the fail-closed tripwire.</summary>
    internal static readonly IReadOnlySet<string> KnownHoldTables = new HashSet<string>(StringComparer.Ordinal) { "LegalHold" };

    /// <summary>The company migration that creates the Phase R LegalHold table (derived from the embedded scripts, never a fixed number).</summary>
    internal static int LegalHoldMigrationVersion => legalHoldMigration.Value;

    private static readonly Lazy<int> legalHoldMigration = new(() =>
        Hasm.Infrastructure.SystemDatabase.MigrationScript.LoadEmbeddedCompany().First(s => s.Sql.Contains($"CREATE TABLE {LH.Table} ", StringComparison.Ordinal)).Version);

    internal static bool UnknownHoldTables() =>
        CompanySchema.Map.Keys.Any(k => !k.Contains('.') && k.Contains("Hold", StringComparison.OrdinalIgnoreCase) && !KnownHoldTables.Contains(k));

    /// <summary>
    /// <c>backup-retention-apply</c> (D-136): the ONLY deletion path, INACTIVE (D-137). While the code gate is off or the installation has not opted in it writes audit 2017 and throws
    /// <c>retention_deletion_inactive</c>; nothing is deleted.
    /// </summary>
    public Task<RetentionApplyResult> ApplyAsync(string root, SigningTrust? trust, BackupOptions backup, AuditTrail trail, string operatorName, CancellationToken ct = default) =>
        ApplyCoreAsync(root, trust, backup, (code, detail, c) => BackupAudit.WriteAsync(options.BuildSystemConnectionString(admin: true), trail, code, detail, null, c), operatorName, null, ct);

    internal async Task<RetentionApplyResult> ApplyCoreAsync(string root, SigningTrust? trust, BackupOptions backup, Func<short, string, CancellationToken, Task> audit, string operatorName,
        RetentionGateOverride? seam, CancellationToken ct)
    {
        // D-137 layers 1 and 3 (also checked again inside the executor's lowest-level routine, review M1)
        if (!BackupRetention.GateOpen(retention) && seam is null)
        {
            await audit(BackupAuditEvents.DeletionRefused, $"backup deletion refused operator={operatorName} run=* reason=retention_deletion_inactive", ct);
            throw new BackupException("retention_deletion_inactive");
        }
        // layer 2: runtime prerequisites on every execution; a refusal is audited too (review L4 of #134)
        try
        {
            if (BackupGuard.Resolve(backup) != BackupProtection.Encrypted) throw new BackupException("backup_encryption_not_configured");
            retention.Validate();
            if (!RestoreSafeguards.CutoverAvailable && seam is null) throw new BackupException("restore_safeguards_missing");
        }
        catch (BackupException ex)
        {
            await audit(BackupAuditEvents.DeletionRefused, $"backup deletion refused operator={operatorName} run=* reason={(BackupRunFiles.IsCode(ex.Code) ? ex.Code : "prerequisite_missing")}", ct);
            throw;
        }
        var resolved = BackupSet.RealPath(root);
        await using var lockConn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await lockConn.OpenAsync(ct);
        if (!await TryLockAsync(lockConn, 0, ct)) throw new BackupException("run_in_progress");
        if (!await TryLockAsync(lockConn, RestoreLockObject, ct)) throw new BackupException("restore_in_progress");
        try
        {
            var status = BackupRunStatus.Evaluate(BackupRunStatus.List(resolved, trust), clock.GetUtcNow(), BackupRunStatus.DefaultMaxAge);
            return await RetentionExecutor.ExecuteAsync(new RetentionExecutor.Context(resolved, retention, clock.GetUtcNow(), status.Code,
                c => ReadInputsAsync(resolved, trust, c), audit, operatorName, DeletedRecordedAsync), seam, ct);
        }
        finally
        {
            try { await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", lockConn); await unlock.ExecuteNonQueryAsync(CancellationToken.None); }
            catch (NpgsqlException) { /* the locks end with the session anyway */ }
        }
    }

    /// <summary>True when the system stream already holds a 2016 for <paramref name="runId"/> (review L2 of #134: a recovered completion writes its 2016 exactly once).</summary>
    internal async Task<bool> DeletedRecordedAsync(string runId, CancellationToken ct)
    {
        if (!BackupRunFiles.IsRunId(runId)) throw new BackupException("invalid_run_id");
        await using var conn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {AE.Table} WHERE {AE.StreamKey} = @s AND {AE.EventType} = @t AND {AE.Detail} LIKE @d", conn);
        cmd.Parameters.AddWithValue("s", AuditStream.SystemKey);
        cmd.Parameters.AddWithValue("t", BackupAuditEvents.Deleted);
        cmd.Parameters.AddWithValue("d", "% run=" + runId + " %");
        return (long)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection conn, int obj, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@ns, @o)", conn);
        cmd.Parameters.AddWithValue("ns", BackupRunService.RunLockNamespace);
        cmd.Parameters.AddWithValue("o", obj);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// D-135: a trial restore or recovery test holds this SHARED lock for its duration; deletion takes it exclusively, so the two never overlap. Returns the holder of the lock
    /// (<c>retention_in_progress</c> while a deletion runs). Disposing the holder releases the lock EXPLICITLY before closing the session: closing an unpooled session alone frees it only when the
    /// server backend has exited, which is asynchronous, so a deletion right after a restore could otherwise be refused (review L6 of #134).
    /// </summary>
    public static async Task<RestoreLockHolder> AcquireRestoreLockAsync(HasmDatabaseOptions options, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock_shared(@ns, @o)", conn);
            cmd.Parameters.AddWithValue("ns", BackupRunService.RunLockNamespace);
            cmd.Parameters.AddWithValue("o", RestoreLockObject);
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) throw new BackupException("retention_in_progress");
            return new RestoreLockHolder(conn);
        }
        catch { await conn.DisposeAsync(); throw; }
    }
}

/// <summary>Holds the shared restore lock (D-135) on its own session; disposal releases every advisory lock of that session explicitly, then closes it (review L6 of #134).</summary>
public sealed class RestoreLockHolder : IAsyncDisposable
{
    private readonly NpgsqlConnection conn;
    private bool disposed;

    internal RestoreLockHolder(NpgsqlConnection conn) => this.conn = conn;

    /// <summary>Test hook only: runs on the holder's own session after the explicit unlock and before the session is closed, so a test can assert that no advisory lock remains on it.</summary>
    internal Func<NpgsqlConnection, Task>? BeforeCloseForTests { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", conn); await unlock.ExecuteNonQueryAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { /* a broken session ends its locks with the backend anyway */ }
        try { if (BeforeCloseForTests is { } hook) await hook(conn); }
        finally { await conn.DisposeAsync(); }
    }
}

/// <summary>
/// The deletion engine (D-136), reachable only through <see cref="RetentionService.ApplyAsync"/> (inactive) and the internal test seam. Audit-first, resumable, re-validating each run immediately before
/// touching it; the gate is checked again in <see cref="DeleteRunFolder"/>, the lowest-level routine (review M1 of #130).
/// </summary>
internal static class RetentionExecutor
{
    internal sealed record Context(string Root, RetentionOptions Options, DateTimeOffset Now, string BackupStatusCode, Func<CancellationToken, Task<RetentionInputs>> Read,
        Func<short, string, CancellationToken, Task> Audit, string Operator, Func<string, CancellationToken, Task<bool>> DeletedRecorded);

    private sealed record Tombstone(int Version, string RunId, DateTimeOffset DeletedUtc, string StatusSha256);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<RetentionApplyResult> ExecuteAsync(Context ctx, RetentionGateOverride? seam, CancellationToken ct)
    {
        var deleted = new List<string>(); var skipped = new List<(string, string)>(); var resumed = 0;
        async Task Refuse(string run, string code) { await ctx.Audit(BackupAuditEvents.DeletionRefused, $"backup deletion refused operator={ctx.Operator} run={run} reason={code}", ct); skipped.Add((run, code)); }

        if (!BackupRetention.GateOpen(ctx.Options) && seam is null) { await Refuse("*", "retention_deletion_inactive"); throw new BackupException("retention_deletion_inactive"); }
        // never delete while backups are failing, stale, partial, untested or unreadable
        if (ctx.BackupStatusCode != "ok") { await Refuse("*", "backups_not_current"); return new(deleted, skipped, resumed); }

        var inputs = await ctx.Read(ct);
        // finish deletions an earlier execution started (audit 2015 was written before the rename)
        foreach (var folder in inputs.DeletingFolders)
        {
            // review L3 of #134: resume only a deletion this engine started (its tombstone, written after audit 2015, exists); anything else is left for the operator
            if (!File.Exists(Path.Combine(ctx.Root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder, folder[..25], RetentionInventory.DeletedMarker)))
            { await Refuse(folder[..25], "resume_unverified"); continue; }
            try { DeleteRunFolder(Path.Combine(ctx.Root, folder), ctx.Options, seam); }
            catch (BackupException ex) when (ex.Code is "symbolic_link_refused" or "path_escaped_deleting_folder") { await Refuse(folder[..25], ex.Code); continue; }
            await ctx.Audit(BackupAuditEvents.Deleted, $"backup deleted operator={ctx.Operator} run={folder[..25]} resumed=yes", ct);
            WriteCompleted(ctx.Root, folder[..25]);
            resumed++;
        }
        // review L2 of #134: a deletion whose folder is gone but whose 2016 was never recorded (a crash between the delete and the event) gets its 2016 now, exactly once
        foreach (var runId in IncompleteTombstones(ctx.Root))
        {
            if (Directory.Exists(Path.Combine(ctx.Root, runId)) || inputs.DeletingFolders.Any(f => f.StartsWith(runId + ".deleting-", StringComparison.Ordinal)))
                continue;                                                                                      // not deleted yet: the run or its resume handles it
            // the folder is absent: the engine removed it before a crash, or something outside the engine did; the event says which facts are known (review L-d of #156)
            if (!await ctx.DeletedRecorded(runId, ct))
                await ctx.Audit(BackupAuditEvents.Deleted, $"backup deleted operator={ctx.Operator} run={runId} resumed=yes recovered=audit folder=absent", ct);
            WriteCompleted(ctx.Root, runId);
            resumed++;
        }
        // clock sanity
        var newestFinished = inputs.Runs.Where(r => r.Complete).Select(r => r.FinishedUtc).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        if (ctx.Now < newestFinished || ctx.Now < LastDeletion(ctx.Root)) { await Refuse("*", "clock_inconsistent"); return new(deleted, skipped, resumed); }

        var plan = RetentionClassifier.Plan(inputs.Runs, inputs.Companies, inputs.Holds, inputs.Pins, ctx.Options, ctx.Now);
        var eligible = plan.Eligible.OrderBy(d => d.RunId, StringComparer.Ordinal).ToList();
        var completeCount = inputs.Runs.Count(r => r.Complete);
        // the mass-deletion brake: a changed clock, calendar or configuration must never empty a root in one execution
        if (eligible.Count > ctx.Options.MaxDeletionsPerRun || eligible.Count * 2 > completeCount) { await Refuse("*", "deletion_volume_suspicious"); return new(deleted, skipped, resumed); }

        foreach (var candidate in eligible)
        {
            // re-validate immediately before touching the run: fresh inputs, same status hash, still eligible, keepers unchanged after the simulated removal, clean and inside the root
            var now = await ctx.Read(ct);
            var fresh = RetentionClassifier.Plan(now.Runs, now.Companies, now.Holds, now.Pins, ctx.Options, ctx.Now);
            var d = fresh.Runs.FirstOrDefault(x => x.RunId == candidate.RunId);
            if (d is null || !d.Eligible) { await Refuse(candidate.RunId, "run_no_longer_eligible"); continue; }
            if (d.StatusSha256 != candidate.StatusSha256 || d.StatusSha256 is null) { await Refuse(candidate.RunId, "status_changed"); continue; }
            var without = RetentionClassifier.Plan([.. now.Runs.Where(r => r.RunId != candidate.RunId)], now.Companies, now.Holds, now.Pins, ctx.Options, ctx.Now);
            if (!KeepersPreserved(fresh, without, candidate.RunId)) { await Refuse(candidate.RunId, "invariants_changed"); continue; }
            var dir = Path.Combine(ctx.Root, candidate.RunId);
            var info = new DirectoryInfo(dir);
            if (!info.Exists || info.LinkTarget is not null || !BackupSet.IsInside(BackupSet.RealPath(dir), ctx.Root) || !RetentionInventory.Inspect(dir).Clean) { await Refuse(candidate.RunId, "run_not_clean"); continue; }

            await ctx.Audit(BackupAuditEvents.DeletionStarted, $"backup deletion started operator={ctx.Operator} run={candidate.RunId} status={candidate.StatusSha256} reasons=none", ct);   // audit first
            WriteTombstone(ctx.Root, candidate.RunId, candidate.StatusSha256!, ctx.Now);
            var renamed = RenameForDeletion(dir, ctx.Options, seam);
            int files; long bytes;
            try { (files, bytes) = DeleteRunFolder(renamed, ctx.Options, seam); }
            catch (BackupException ex) when (ex.Code is "symbolic_link_refused" or "path_escaped_deleting_folder") { await Refuse(candidate.RunId, ex.Code); continue; }   // left for the operator
            await ctx.Audit(BackupAuditEvents.Deleted, $"backup deleted operator={ctx.Operator} run={candidate.RunId} status={candidate.StatusSha256} files={files} bytes={bytes} tombstone=yes resumed=no", ct);
            WriteCompleted(ctx.Root, candidate.RunId);
            deleted.Add(candidate.RunId);
        }
        return new(deleted, skipped, resumed);
    }

    /// <summary>Every keep reason of every OTHER run survives the simulated removal of <paramref name="runId"/> (minimums, last recoverable, period ends, proofs, holds, pins).</summary>
    internal static bool KeepersPreserved(RetentionPlan before, RetentionPlan after, string runId)
    {
        var a = after.Runs.ToDictionary(r => r.RunId, StringComparer.Ordinal);
        return before.Runs.Where(r => r.RunId != runId).All(r => a.TryGetValue(r.RunId, out var x) && r.Reasons.All(x.Reasons.Contains));
    }

    private static DateTimeOffset LastDeletion(string root)
    {
        var dir = Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder);
        if (!Directory.Exists(dir)) return DateTimeOffset.MinValue;
        var last = DateTimeOffset.MinValue;
        foreach (var f in Directory.EnumerateFiles(dir, RetentionInventory.DeletedMarker, SearchOption.AllDirectories))
            try { if (JsonSerializer.Deserialize<Tombstone>(File.ReadAllBytes(f), Json) is { } t && t.DeletedUtc > last) last = t.DeletedUtc; }
            catch (JsonException) { /* an unreadable marker never moves the clock check */ }
        return last;
    }

    /// <summary>Keeps the deleted run's signed status and signature, verifiable with the kit's trust list, plus the deletion time (owner-only; never deleted by HASM).</summary>
    private static void WriteTombstone(string root, string runId, string statusSha256, DateTimeOffset now)
    {
        var dir = Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder, runId);
        Hasm.Infrastructure.Audit.AuditFilePermissions.CreateOwnerOnlyDirectory(Path.Combine(root, RetentionInventory.RetentionFolder));
        Hasm.Infrastructure.Audit.AuditFilePermissions.CreateOwnerOnlyDirectory(Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder));
        Hasm.Infrastructure.Audit.AuditFilePermissions.CreateOwnerOnlyDirectory(dir);
        foreach (var name in new[] { SignedDocuments.RunStatusFileName, SignedDocuments.RunStatusFileName + SignedDocuments.SignatureSuffix })
            if (!File.Exists(Path.Combine(dir, name))) BackupRunFiles.WriteOwnerOnlyAtomically(Path.Combine(dir, name), File.ReadAllBytes(Path.Combine(root, runId, name)));
        if (!File.Exists(Path.Combine(dir, RetentionInventory.DeletedMarker)))
            BackupRunFiles.WriteOwnerOnlyAtomically(Path.Combine(dir, RetentionInventory.DeletedMarker), JsonSerializer.SerializeToUtf8Bytes(new Tombstone(1, runId, now, statusSha256), Json));
    }

    /// <summary>The run ids whose tombstone has the deletion marker but not the completion marker (review L2 of #134).</summary>
    internal static IEnumerable<string> IncompleteTombstones(string root)
    {
        var dir = Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder);
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).EnumerateDirectories()
            .Where(d => d.LinkTarget is null && BackupRunFiles.IsRunId(d.Name)
                        && File.Exists(Path.Combine(d.FullName, RetentionInventory.DeletedMarker)) && !File.Exists(Path.Combine(d.FullName, RetentionInventory.CompletedMarker)))
            .Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private sealed record Completion(int Version, string RunId);

    private static void WriteCompleted(string root, string runId) =>
        BackupRunFiles.WriteOwnerOnlyAtomically(Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder, runId, RetentionInventory.CompletedMarker),
            JsonSerializer.SerializeToUtf8Bytes(new Completion(1, runId), Json));

    /// <summary>Renames a run folder to <c>&lt;run id&gt;.deleting-&lt;16 hex&gt;</c>, so it is no longer a run. Gate checked here as well (review M1).</summary>
    internal static string RenameForDeletion(string runDirectory, RetentionOptions options, RetentionGateOverride? seam)
    {
        RequireGate(options, seam);
        var target = runDirectory + ".deleting-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        Directory.Move(runDirectory, target);
        return target;
    }

    /// <summary>
    /// The lowest-level deletion routine (review M1 of #130): the code gate and the opt-in are checked HERE, so no caller can delete without them except through the test seam. Deletes only a
    /// <c>.deleting-*</c> folder, never following a symbolic link (a link itself is removed, never its target). Returns the number of files and bytes removed.
    /// Review L1 of #134 (time of check to time of use): every directory is re-checked FRESH immediately before it is enumerated or removed, and the directory holding each file immediately before
    /// the file is deleted. Each must not be a link and must still resolve INSIDE the deleting folder; otherwise the walk stops with <c>path_escaped_deleting_folder</c> and the remainder is left for
    /// the operator. <paramref name="beforeDescend"/> is a test hook only (it lets a test swap an entry between the enumeration and the descent).
    /// </summary>
    internal static (int Files, long Bytes) DeleteRunFolder(string deletingFolder, RetentionOptions options, RetentionGateOverride? seam, Action<string>? beforeDescend = null)
    {
        RequireGate(options, seam);
        if (!RetentionInventory.DeletingFolder().IsMatch(Path.GetFileName(deletingFolder))) throw new BackupException("not_a_deleting_folder");
        var root = new DirectoryInfo(deletingFolder);
        if (root.LinkTarget is not null) throw new BackupException("symbolic_link_refused");
        var realRoot = BackupSet.RealPath(deletingFolder);
        var files = 0; long bytes = 0;
        void RequireInside(string directory)
        {
            var fresh = new DirectoryInfo(directory);
            if (fresh.LinkTarget is not null || !BackupSet.IsInside(BackupSet.RealPath(directory), realRoot)) throw new BackupException("path_escaped_deleting_folder");
        }
        void Remove(string path)
        {
            RequireInside(path);
            foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (e.LinkTarget is not null) { if (e is DirectoryInfo) Directory.Delete(e.FullName); else File.Delete(e.FullName); continue; }   // the link itself, never its target
                if (e is DirectoryInfo) { beforeDescend?.Invoke(e.FullName); Remove(e.FullName); continue; }
                RequireInside(path);
                var f = new FileInfo(e.FullName);
                if (f.LinkTarget is not null) { File.Delete(f.FullName); continue; }
                bytes += f.Length; files++; File.Delete(f.FullName);
            }
            RequireInside(path);
            Directory.Delete(path);
        }
        Remove(deletingFolder);
        return (files, bytes);
    }

    private static void RequireGate(RetentionOptions options, RetentionGateOverride? seam)
    {
        if (!BackupRetention.GateOpen(options) && seam is null) throw new BackupException("retention_deletion_inactive");
    }
}
