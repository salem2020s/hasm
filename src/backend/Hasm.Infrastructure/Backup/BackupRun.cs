using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The manifest of the system-database part of a backup run (<c>system/manifest.json</c>; Phase 4.3 slice 4, D-070). <see cref="Excluded"/> names the secret-store tables whose DATA was left out
/// (labels such as <c>session</c>, <c>login_throttle</c>, later <c>totp</c>). It detects corruption and loss; it is NOT a signature (owner package 4.3-2).
/// </summary>
public sealed record SystemBackupManifest(int Version, string Kind, DateTimeOffset CreatedUtc, ManifestFile Database, DatabaseLocale? DatabaseLocale, int? SystemMigrationVersion, IReadOnlyList<string> Excluded);

/// <summary>Creation, reading and verification of the system part of a run: <c>database.dump</c> and <c>manifest.json</c> (written last), nothing else.</summary>
public static class SystemBackupSet
{
    public const int CurrentVersion = 1;
    public const string Kind = "system";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Writes the manifest for the dump already in <paramref name="directory"/> (temp file, then rename).</summary>
    public static SystemBackupManifest WriteManifest(string directory, DateTimeOffset createdUtc, DatabaseLocale? locale, int? migrationVersion, IReadOnlyList<string> excluded)
    {
        var dump = Path.Combine(directory, BackupSet.DatabaseFileName);
        using var fs = File.OpenRead(dump);
        var size = fs.Length;
        var manifest = new SystemBackupManifest(CurrentVersion, Kind, createdUtc, new ManifestFile(BackupSet.DatabaseFileName, size, Convert.ToHexStringLower(SHA256.HashData(fs))), locale, migrationVersion, [.. excluded]);
        BackupRunFiles.WriteOwnerOnlyAtomically(Path.Combine(directory, BackupSet.ManifestFileName), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        return manifest;
    }

    /// <summary>The validated manifest, or null when missing, malformed or of an unknown version or kind.</summary>
    public static SystemBackupManifest? ReadManifest(string directory)
    {
        var path = Path.Combine(directory, BackupSet.ManifestFileName);
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
        SystemBackupManifest? m;
        try { m = JsonSerializer.Deserialize<SystemBackupManifest>(File.ReadAllBytes(path), Json); }
        catch (JsonException) { return null; }
        if (m is null || m.Version != CurrentVersion || m.Kind != Kind || m.Database is null || m.Excluded is null) return null;
        if (m.Database.Name != BackupSet.DatabaseFileName || !Sha256Hex.IsMatch(m.Database.Sha256 ?? "") || m.Database.Size < 0) return null;
        if (m.DatabaseLocale is { IsWellFormed: false } || m.SystemMigrationVersion is <= 0) return null;
        if (m.Excluded.Any(e => e is null || !BackupRunFiles.IsCode(e))) return null;
        return m;
    }

    /// <summary>True when the manifest is valid, the dump matches its size and hash, and nothing else is in the folder.</summary>
    public static bool Verify(string directory)
    {
        var m = ReadManifest(directory);
        if (m is null) return false;
        var dump = Path.Combine(directory, BackupSet.DatabaseFileName);
        if (!File.Exists(dump)) return false;
        using (var fs = File.OpenRead(dump))
            if (fs.Length != m.Database.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(fs)), m.Database.Sha256, StringComparison.Ordinal)) return false;
        return Directory.EnumerateFileSystemEntries(directory).All(e => Path.GetFileName(e) is BackupSet.DatabaseFileName or BackupSet.ManifestFileName);
    }
}

/// <summary>One dump of a run (the system database): outcome, the code of a failure, its window and size.</summary>
public sealed record RunDump(string Outcome, string? Code, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, long DurationMs, long DumpBytes);

/// <summary>One company of a run: outcome (<c>ok</c>, <c>ok_missing_keys</c>, <c>busy</c>, <c>failed</c>), the code of a failure, its dump window and the counts. Never a physical name.</summary>
public sealed record RunCompany(Guid CompanyId, string Outcome, string? Code, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, long DurationMs, long DumpBytes, int AttachmentFiles, int MissingKeyIds);

/// <summary>
/// <c>run.json</c>, written LAST (a run folder without it is incomplete). <see cref="Verdict"/> is <c>ok</c>, <c>partial</c> or <c>failed</c>; <see cref="Encrypted"/> is false until owner package 4.3-2.
/// The run is NOT one point in time across databases: each dump carries its own window.
/// </summary>
public sealed record BackupRunReport(int Version, string RunId, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, long DurationMs, RunDump System, IReadOnlyList<RunCompany> Companies, int Skipped,
    string Verdict, IReadOnlyList<string> MissingKeyIds, IReadOnlyList<string> Excluded, int ClientMajor, bool Encrypted, IReadOnlyList<string>? Warnings = null);

/// <summary>Run ids, the report file and owner-only writes.</summary>
public static class BackupRunFiles
{
    public const string ReportFileName = "run.json";
    public const string SystemFolder = "system";
    public const string CompaniesFolder = "companies";
    public const int ReportVersion = 1;
    private static readonly Regex RunIdPattern = new("^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CodePattern = new("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static readonly string[] Verdicts = ["ok", "partial", "failed"];
    internal static readonly string[] CompanyOutcomes = ["ok", "ok_missing_keys", "busy", "failed"];

    /// <summary><c>yyyyMMddTHHmmssZ-&lt;8 hex&gt;</c> (UTC): ids sort by time.</summary>
    public static string NewRunId(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public static bool IsRunId(string? id) => id is not null && RunIdPattern.IsMatch(id);

    /// <summary>The start time encoded in a run id (UTC), or null.</summary>
    public static DateTimeOffset? StartOf(string runId) =>
        IsRunId(runId) && DateTime.TryParseExact(runId[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    internal static bool IsCode(string? s) => s is not null && CodePattern.IsMatch(s);

    /// <summary>Creates <paramref name="path"/> exclusively and owner-only via a temporary file, then renames it into place.</summary>
    internal static void WriteOwnerOnlyAtomically(string path, byte[] content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            AuditFilePermissions.CreateOwnerOnlyFile(temp, content);
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void WriteReport(string runDirectory, BackupRunReport report) =>
        WriteOwnerOnlyAtomically(Path.Combine(runDirectory, ReportFileName), JsonSerializer.SerializeToUtf8Bytes(report, Json));

    /// <summary>
    /// The validated report of <paramref name="runDirectory"/>; null when missing or not acceptable: unknown version, a run id that is not the folder's, an unknown verdict or outcome, a code or label that is not a
    /// plain lowercase code (so no path, name or message can be carried), an invalid key id, or <c>encrypted</c> true (no encrypted format exists yet).
    /// </summary>
    public static BackupRunReport? ReadReport(string runDirectory)
    {
        var path = Path.Combine(runDirectory, ReportFileName);
        if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
        BackupRunReport? r;
        try { r = JsonSerializer.Deserialize<BackupRunReport>(File.ReadAllBytes(path), Json); }
        catch (JsonException) { return null; }
        if (r is null || r.Version != ReportVersion || r.RunId != Path.GetFileName(Path.TrimEndingDirectorySeparator(runDirectory)) || !IsRunId(r.RunId)) return null;
        if (!Verdicts.Contains(r.Verdict) || r.System is null || r.Companies is null || r.MissingKeyIds is null || r.Excluded is null || r.Encrypted || r.Skipped < 0) return null;
        if (r.System.Outcome is not ("ok" or "failed") || (r.System.Code is not null && !IsCode(r.System.Code))) return null;
        foreach (var c in r.Companies)
            if (c is null || c.CompanyId == Guid.Empty || !CompanyOutcomes.Contains(c.Outcome) || (c.Code is not null && !IsCode(c.Code)) || c.AttachmentFiles < 0 || c.MissingKeyIds < 0) return null;
        if (r.MissingKeyIds.Any(k => !KekRing.IsValidKeyId(k)) || r.Excluded.Any(e => !IsCode(e)) || (r.Warnings?.Any(w => !IsCode(w)) ?? false)) return null;
        return r;
    }
}

/// <summary>
/// Phase 4.3 slice 4 (D-069..D-073): ONE unattended backup run of the whole installation, started by the operating-system scheduler (<c>Hasm.Admin backup-run</c>). The system database is dumped first (without the
/// data of the secret-store tables), then every Ready company through <see cref="CompanyBackupService"/>; one company's failure never stops the others; one run at a time. Nothing is encrypted, signed or
/// deleted (owner package 4.3-2). A run is NOT one point in time across databases: each dump has its own window, recorded in <c>run.json</c>.
/// </summary>
public sealed class BackupRunService
{
    /// <summary>Advisory-lock namespace of "one run at a time" ("HBRU"), apart from the per-company provisioning locks.</summary>
    internal const int RunLockNamespace = 0x48425255;

    /// <summary>
    /// The secret-store tables whose DATA is excluded from the system dump, as (label, logical name). Pinned by a unit test to <see cref="SystemSchema.SecretStoreTables"/> in both directions; the Phase 4.1 TOTP
    /// table is added to both in the slice that creates it.
    /// </summary>
    public static IReadOnlyList<(string Label, string LogicalTable)> SystemDumpExclusions { get; } = [("session", "Session.Table"), ("login_throttle", "LoginThrottle.Table"), ("user_totp", "UserTotp.Table")];

    private readonly HasmDatabaseOptions options;
    private readonly AttachmentOptions attachments;
    private readonly EncryptionOptions encryption;
    private readonly TimeProvider clock;
    private readonly PgBackupTool tool;
    private readonly CompanyBackupService companies;
    private readonly CompanyRegistry registry;

    public BackupRunService(HasmDatabaseOptions options, AttachmentOptions attachments, EncryptionOptions encryption, TimeProvider? clock = null, string? pgBinDirectory = null)
    {
        this.options = options;
        this.attachments = attachments;
        this.encryption = encryption;
        this.clock = clock ?? TimeProvider.System;
        tool = new PgBackupTool(options, pgBinDirectory);
        companies = new CompanyBackupService(options, attachments, encryption, this.clock, pgBinDirectory);
        registry = new CompanyRegistry(options.BuildSystemConnectionString(admin: true));
    }

    /// <summary>Refuses a root that is not absolute or overlaps the attachment root or the key directory (both directions); the attachment storage must be configured.</summary>
    internal static string CheckRoot(string root, AttachmentOptions attachments, EncryptionOptions encryption)
    {
        if (!Path.IsPathFullyQualified(root)) throw new BackupException("path_not_absolute");
        if (attachments.StorageDirectory is not { } storage || !Path.IsPathFullyQualified(storage)) throw new BackupException("attachment_storage_not_configured");
        var resolved = BackupSet.RealPath(root);
        foreach (var protectedDir in new[] { storage, encryption.KeyDirectory })
            if (protectedDir is not null && (BackupSet.IsInside(resolved, BackupSet.RealPath(protectedDir)) || BackupSet.IsInside(BackupSet.RealPath(protectedDir), resolved))) throw new BackupException("destination_overlaps_source");
        return resolved;
    }

    /// <summary>Runs one backup of the installation into a new owner-only folder under <paramref name="root"/> and returns its report (also written as <c>run.json</c>, last).</summary>
    public async Task<(BackupRunReport Report, string RunDirectory)> RunAsync(string root, CancellationToken ct = default)
    {
        var resolved = CheckRoot(root, attachments, encryption);
        await using var lockConn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await lockConn.OpenAsync(ct);
        await using (var lk = new NpgsqlCommand("SELECT pg_try_advisory_lock(@ns, 0)", lockConn))
        {
            lk.Parameters.AddWithValue("ns", RunLockNamespace);
            if (!(bool)(await lk.ExecuteScalarAsync(ct))!) throw new BackupException("run_in_progress");
        }
        // review L3: the run lock is a SESSION lock; a periodic no-op keeps the idle lock connection from being closed by idle_session_timeout or an idle network path while the dumps run
        var keepAlive = new SessionKeepAlive(lockConn, KeepAliveInterval);
        try { return await RunLockedAsync(resolved, lockConn, keepAlive, ct); }
        finally
        {
            await keepAlive.DisposeAsync();
            try { await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", lockConn); await unlock.ExecuteNonQueryAsync(CancellationToken.None); }
            catch (NpgsqlException) { /* the lock ends with the session anyway */ }
        }
    }

    /// <summary>Interval of the no-op on the lock connection (review L3); <c>idle_session_timeout</c> of the admin role must be 0 or longer than twice this.</summary>
    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private async Task<(BackupRunReport, string)> RunLockedAsync(string root, NpgsqlConnection lockConn, SessionKeepAlive keepAlive, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var watch = clock.GetTimestamp();
        AuditFilePermissions.CreateOwnerOnlyDirectory(root);
        var runId = BackupRunFiles.NewRunId(started);
        var runDir = Path.Combine(root, runId);
        CompanyBackupService.CreatePrivateDirectory(runDir, "run_exists", "run_not_private");
        var excluded = SystemDumpExclusions.Select(e => e.Label).ToList();

        var system = await BackupSystemAsync(runDir, ct);
        var clientMajor = await tool.ClientMajorAsync("pg_dump", ct);

        var outcomes = new List<RunCompany>();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        var companiesDir = Path.Combine(runDir, BackupRunFiles.CompaniesFolder);
        CompanyBackupService.CreatePrivateDirectory(companiesDir, "run_exists", "run_not_private");
        foreach (var company in await registry.ListAsync(ct))
        {
            if (company.State != ProvisioningState.Ready) { skipped++; continue; }
            outcomes.Add(await BackupCompanyAsync(company.Id, Path.Combine(companiesDir, company.Id.ToString("N")), missing, ct));
        }

        // review L6: stop the keep-alive, then check SYNCHRONOUSLY that this session still holds the run lock before the verdict (a loss in the last keep-alive interval is not missed)
        await keepAlive.DisposeAsync();
        var lockHeld = !keepAlive.Failed && await IsRunLockHeldAsync(lockConn);
        var (verdict, warnings) = ComputeVerdict(system, outcomes, lockHeld);
        var finished = clock.GetUtcNow();
        var report = new BackupRunReport(BackupRunFiles.ReportVersion, runId, started, finished, (long)clock.GetElapsedTime(watch).TotalMilliseconds, system, outcomes, skipped, verdict, [.. missing], excluded, clientMajor, false,
            warnings);
        BackupRunFiles.WriteReport(runDir, report);
        return (report, runDir);
    }

    /// <summary>
    /// The verdict: <c>failed</c> when the system dump failed or no set was written; <c>partial</c> when a company was not <c>ok</c> or the run lock was lost (a lost lock means another run could have
    /// started meanwhile: the sets stay valid, the warning <c>run_lock_lost</c> is recorded and monitoring exits 8); otherwise <c>ok</c>.
    /// </summary>
    internal static (string Verdict, IReadOnlyList<string>? Warnings) ComputeVerdict(RunDump system, IReadOnlyList<RunCompany> companies, bool lockHeld)
    {
        var anySet = system.Outcome == "ok" || companies.Any(o => o.Outcome is "ok" or "ok_missing_keys");
        var verdict = system.Outcome != "ok" || !anySet ? "failed"
            : companies.Any(o => o.Outcome is not "ok") || !lockHeld ? "partial" : "ok";
        return (verdict, lockHeld ? null : ["run_lock_lost"]);
    }

    /// <summary>True when THIS session (<paramref name="lockConn"/>) still holds the granted run advisory lock (namespace <see cref="RunLockNamespace"/>, object 0); any failure counts as not held.</summary>
    internal static async Task<bool> IsRunLockHeldAsync(NpgsqlConnection lockConn)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND classid::bigint = @ns AND objid::bigint = 0 AND objsubid = 2 AND granted)", lockConn);
            cmd.Parameters.AddWithValue("ns", (long)RunLockNamespace);
            return await cmd.ExecuteScalarAsync(CancellationToken.None) is true;
        }
        catch (Exception) { return false; }   // the session, and with it the lock, may be gone
    }

    private async Task<RunDump> BackupSystemAsync(string runDir, CancellationToken ct)
    {
        var start = clock.GetUtcNow(); var t = clock.GetTimestamp();
        var dir = Path.Combine(runDir, BackupRunFiles.SystemFolder);
        try
        {
            CompanyBackupService.CreatePrivateDirectory(dir, "run_exists", "run_not_private");
            var dump = Path.Combine(dir, BackupSet.DatabaseFileName);
            var locale = await tool.DumpSystemAsync(dump, [.. SystemDumpExclusions.Select(e => SystemSchema.Resolve(e.LogicalTable))], ct);
            await tool.VerifyArchiveAsync(dump, ct);
            var level = await ReadSystemMigrationLevelAsync(ct);
            var manifest = SystemBackupSet.WriteManifest(dir, clock.GetUtcNow(), locale, level, [.. SystemDumpExclusions.Select(e => e.Label)]);
            if (!SystemBackupSet.Verify(dir)) throw new BackupException("system_set_not_clean");
            return new RunDump("ok", null, start, clock.GetUtcNow(), (long)clock.GetElapsedTime(t).TotalMilliseconds, manifest.Database.Size);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RunDump("failed", Code(ex), start, clock.GetUtcNow(), (long)clock.GetElapsedTime(t).TotalMilliseconds, 0);
        }
    }

    private async Task<RunCompany> BackupCompanyAsync(Guid companyId, string destination, SortedSet<string> missing, CancellationToken ct)
    {
        var start = clock.GetUtcNow(); var t = clock.GetTimestamp();
        RunCompany Done(string outcome, string? code, long bytes = 0, int files = 0, int keys = 0) =>
            new(companyId, outcome, code, start, clock.GetUtcNow(), (long)clock.GetElapsedTime(t).TotalMilliseconds, bytes, files, keys);
        try
        {
            var r = await companies.BackupCompanyAsync(companyId, destination, ct);
            foreach (var k in r.MissingKeyIds) missing.Add(k);
            return Done(r.MissingKeyIds.Count == 0 ? "ok" : "ok_missing_keys", null, r.Manifest.Database.Size, r.Manifest.Attachments.Count, r.MissingKeyIds.Count);
        }
        catch (BackupException ex) when (ex.Code == "company_busy") { return Done("busy", "company_busy"); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Done("failed", Code(ex)); }   // one company's failure never stops the others
    }

    /// <summary>A stable code only: the backup code, or <c>database_error</c> / <c>io_error</c> / <c>unexpected_error</c> (never a message).</summary>
    private static string Code(Exception ex) => ex switch
    {
        BackupException be when BackupRunFiles.IsCode(be.Code) => be.Code,
        NpgsqlException => "database_error",
        IOException or UnauthorizedAccessException => "io_error",
        _ => "unexpected_error",
    };

    private async Task<int?> ReadSystemMigrationLevelAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT max(version) FROM {SystemSchema.MigrationJournalTable}", conn);
        return await cmd.ExecuteScalarAsync(ct) is int v ? v : null;
    }
}

/// <summary>One run folder as seen by status and retention: its id, its report (null when absent or unreadable), whether a report file exists, and the folder's last write time.</summary>
public sealed record RunEntry(string RunId, BackupRunReport? Report, bool HasReportFile, DateTimeOffset LastWriteUtc)
{
    public bool IsComplete => Report is not null;
    public bool IsUnreadable => HasReportFile && Report is null;
}

/// <summary>The status of the newest complete run (<c>backup-status</c>, D-071).</summary>
public sealed record BackupStatusResult(int ExitCode, string Code, BackupRunReport? Latest, int IncompleteRuns, int UnreadableRuns, IReadOnlyDictionary<Guid, int> MissedRuns);

/// <summary>Read-only evaluation of a backup root: status (D-071) and retention preview (D-072). Never writes or deletes anything.</summary>
public static class BackupRunStatus
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(26);
    public static readonly TimeSpan IncompleteAge = TimeSpan.FromHours(24);

    /// <summary>The run folders of <paramref name="root"/> (names that are run ids; anything else is ignored), newest first. A missing root yields none.</summary>
    public static IReadOnlyList<RunEntry> List(string root)
    {
        if (!Directory.Exists(root)) return [];
        return [.. new DirectoryInfo(root).EnumerateDirectories()
            .Where(d => BackupRunFiles.IsRunId(d.Name) && d.LinkTarget is null)
            .Select(d => new RunEntry(d.Name, BackupRunFiles.ReadReport(d.FullName), File.Exists(Path.Combine(d.FullName, BackupRunFiles.ReportFileName)), new DateTimeOffset(d.LastWriteTimeUtc, TimeSpan.Zero)))
            .OrderByDescending(e => e.RunId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Exit 1: no complete run (<c>backup_missing</c>), the newest complete run failed (<c>backup_failed</c>) or is older than <paramref name="maxAge"/> by its finish time (<c>backup_stale</c>). Exit 8: it is
    /// partial (<c>backup_partial</c>), a company missed two or more consecutive runs (<c>company_missed_runs</c>) or a run's report is unreadable (<c>unreadable_reports</c>). Otherwise 0 (<c>ok</c>). Incomplete runs older than 24 hours are counted (information).
    /// </summary>
    public static BackupStatusResult Evaluate(IReadOnlyList<RunEntry> runs, DateTimeOffset now, TimeSpan maxAge)
    {
        var ordered = runs.OrderByDescending(r => r.RunId, StringComparer.Ordinal).ToList();
        var complete = ordered.Where(r => r.IsComplete).Select(r => r.Report!).ToList();
        var incomplete = ordered.Count(r => !r.IsComplete && !r.HasReportFile && now - r.LastWriteUtc > IncompleteAge);
        var unreadable = ordered.Count(r => r.IsUnreadable);
        var missed = MissedRuns(complete);
        if (complete.Count == 0) return new(1, "backup_missing", null, incomplete, unreadable, missed);
        var latest = complete[0];
        if (latest.Verdict == "failed") return new(1, "backup_failed", latest, incomplete, unreadable, missed);
        if (now - latest.FinishedUtc > maxAge) return new(1, "backup_stale", latest, incomplete, unreadable, missed);
        if (latest.Verdict == "partial") return new(8, "backup_partial", latest, incomplete, unreadable, missed);
        if (missed.Count > 0) return new(8, "company_missed_runs", latest, incomplete, unreadable, missed);
        // review L2 (lead decision under D-048): an unreadable report (possibly the newest run, damaged or altered) never lets the status pass silently: monitoring stays fail-closed
        if (unreadable > 0) return new(8, "unreadable_reports", latest, incomplete, unreadable, missed);
        return new(0, "ok", latest, incomplete, unreadable, missed);
    }

    /// <summary>Per company, the number of CONSECUTIVE complete runs (newest first) in which it was busy or failed; only companies with two or more are returned.</summary>
    internal static IReadOnlyDictionary<Guid, int> MissedRuns(IReadOnlyList<BackupRunReport> completeNewestFirst)
    {
        var result = new Dictionary<Guid, int>();
        if (completeNewestFirst.Count == 0) return result;
        foreach (var id in completeNewestFirst.SelectMany(r => r.Companies).Select(c => c.CompanyId).Distinct())
        {
            var n = 0;
            foreach (var run in completeNewestFirst)
            {
                var c = run.Companies.FirstOrDefault(x => x.CompanyId == id);
                if (c is null || c.Outcome is not ("busy" or "failed")) break;
                n++;
            }
            if (n >= 2) result[id] = n;
        }
        return result;
    }

    /// <summary>
    /// The retention PREVIEW (D-072; nothing is ever deleted): the complete runs that WOULD be removed when only the newest <paramref name="keepLast"/> runs and, when given, the runs started within
    /// <paramref name="keepDays"/> days are kept (a run kept by either rule is kept). The newest complete run is always kept; incomplete and unreadable runs are never selected.
    /// </summary>
    public static IReadOnlyList<string> RetentionPreview(IReadOnlyList<RunEntry> runs, DateTimeOffset now, int keepLast, int? keepDays)
    {
        if (keepLast < 1) throw new BackupException("keep_last_invalid");
        if (keepDays is < 0) throw new BackupException("keep_days_invalid");
        var complete = runs.Where(r => r.IsComplete).OrderByDescending(r => r.RunId, StringComparer.Ordinal).ToList();
        var selected = new List<string>();
        for (var i = 0; i < complete.Count; i++)
        {
            if (i < keepLast) continue;                                                       // includes the newest complete run (keepLast >= 1)
            if (keepDays is { } d && complete[i].Report!.StartedUtc >= now - TimeSpan.FromDays(d)) continue;
            selected.Add(complete[i].RunId);
        }
        return selected;
    }
}

/// <summary>
/// Keeps an otherwise idle session alive with a periodic <c>SELECT 1</c> (review L3: a session advisory lock ends with its session, for example through <c>idle_session_timeout</c> or a dropped idle network path).
/// <see cref="Failed"/> becomes true when a no-op fails: the session, and with it the lock, may be gone. Disposing stops the loop BEFORE the connection is used again (the connection is never used concurrently).
/// </summary>
internal sealed class SessionKeepAlive : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private volatile bool failed;
    private int disposed;

    public SessionKeepAlive(NpgsqlConnection connection, TimeSpan interval) : this(async () =>
    {
        await using var cmd = new NpgsqlCommand("SELECT 1", connection);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);               // instantaneous; never cancelled half-way, so the connection stays usable for the lock check and the unlock
    }, interval) { }

    /// <summary>Test seam: <paramref name="ping"/> replaces the no-op query.</summary>
    internal SessionKeepAlive(Func<Task> ping, TimeSpan interval) => loop = RunAsync(ping, interval);

    public bool Failed => failed;

    private async Task RunAsync(Func<Task> ping, TimeSpan interval)
    {
        while (true)
        {
            try { await Task.Delay(interval, stop.Token); } catch (OperationCanceledException) { return; }   // only the stop requested by DisposeAsync
            try { await ping(); }
            catch (Exception) { failed = true; return; }                       // review L7: ANY failure means the session may be gone; never rethrown
        }
    }

    /// <summary>Stops the loop (idempotent) and waits for it; never throws, so it is safe inside a finally.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        try { await loop; } catch (Exception) { failed = true; }
        stop.Dispose();
    }
}
