using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Naming;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using AT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Attachment;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using GP = Hasm.Infrastructure.SystemDatabase.SystemSchema.GlobalParty;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PL = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyLink;

namespace Hasm.Infrastructure.Backup;

/// <summary>One finding of a backup or trial restore: a stable code and a count (never a name, a path or business data).</summary>
public sealed record BackupFinding(string Code, int Count);

/// <summary>The outcome of <see cref="CompanyBackupService.BackupCompanyAsync"/>: the manifest, the key ids the set needs that the key directory lacks (reported, never a refusal: D-061) and the stale staging folders swept.</summary>
public sealed record CompanyBackupResult(BackupManifest Manifest, IReadOnlyList<string> MissingKeyIds, int StagingFoldersSwept);

public enum TrialRestoreVerdict { Ok, OkWithWarnings, Failed }

/// <summary>The outcome of a trial restore. <see cref="KeptFile"/> is the owner-only file that names a kept trial database (with <c>keep</c> only); the database name itself is never returned.</summary>
public sealed record TrialRestoreResult(TrialRestoreVerdict Verdict, IReadOnlyList<BackupFinding> Failures, IReadOnlyList<BackupFinding> Warnings, int AttachmentFiles, int UnreferencedFiles, int? MigrationVersion, string? KeptFile, TimeSpan Elapsed = default);

/// <summary>
/// Phase 4.3 slice 3 (decisions D-058..D-062): backup of ONE registered company (its database from the registry, its attachment folder, its locale and migration level) into a verified set, and a TRIAL restore of
/// such a set into a NEW generated, unregistered database and an attachment folder outside live storage, followed by the read-only pre-cutover checks of SRS 16.2 / Scenario 21.8 (shared party links,
/// attachment rows against files, migration compatibility). Nothing here registers, grants, cuts over or changes a checkpoint: the cutover and the restore acknowledgement are BLOCKED (owner decision),
/// so this class does NOT fully meet Scenario 21.8.
/// </summary>
public sealed class CompanyBackupService
{
    /// <summary>Stale staging dumps older than this are swept (well above the 30-minute tool timeout).</summary>
    internal static readonly TimeSpan StagingMaxAge = TimeSpan.FromHours(24);
    internal const string CommentPrefix = "hasm-trial-restore:";
    private static readonly Regex Hex32 = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HasmDatabaseOptions options;
    private readonly AttachmentOptions attachments;
    private readonly EncryptionOptions encryption;
    private readonly TimeProvider clock;
    private readonly PgBackupTool tool;
    private readonly Func<string> nameGenerator;
    private readonly IReadOnlyList<MigrationScript> scripts;
    private readonly CompanyRegistry registry;

    /// <param name="nameGenerator">Names of trial databases (tests inject the disposable prefix); the default is the opaque generated company name.</param>
    public CompanyBackupService(HasmDatabaseOptions options, AttachmentOptions attachments, EncryptionOptions encryption, TimeProvider? clock = null, string? pgBinDirectory = null,
        Func<string>? nameGenerator = null, IReadOnlyList<MigrationScript>? companyScripts = null)
    {
        this.options = options;
        this.attachments = attachments;
        this.encryption = encryption;
        this.clock = clock ?? TimeProvider.System;
        tool = new PgBackupTool(options, pgBinDirectory);
        this.nameGenerator = nameGenerator ?? PhysicalIdentifier.GenerateDatabaseName;
        scripts = companyScripts ?? MigrationScript.LoadEmbeddedCompany();
        registry = new CompanyRegistry(options.BuildSystemConnectionString(admin: true));
    }

    private int SupportedMigrationVersion => scripts.Count == 0 ? 0 : scripts.Max(s => s.Version);

    /// <summary>The attachment storage root; refused when not configured (fail closed, no override: D-058).</summary>
    private string AttachmentRoot() =>
        attachments.StorageDirectory is { } root && Path.IsPathFullyQualified(root) ? root : throw new BackupException("attachment_storage_not_configured");

    // ------------------------------------------------------------------ backup

    /// <summary>Backs up ONE registered, Ready company into <paramref name="destination"/> (absent or empty; outside the attachment root and the key directory).</summary>
    public async Task<CompanyBackupResult> BackupCompanyAsync(Guid companyId, string destination, CancellationToken ct = default)
    {
        if (companyId == Guid.Empty) throw new BackupException("invalid_company");
        if (!Path.IsPathFullyQualified(destination)) throw new BackupException("path_not_absolute");
        var root = AttachmentRoot();
        var dest = BackupSet.RealPath(destination);
        foreach (var protectedDir in new[] { root, encryption.KeyDirectory })
            if (protectedDir is not null && (BackupSet.IsInside(dest, BackupSet.RealPath(protectedDir)) || BackupSet.IsInside(BackupSet.RealPath(protectedDir), dest))) throw new BackupException("destination_overlaps_source");
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any()) throw new BackupException("destination_not_empty");
        if (Path.GetDirectoryName(dest) is not { Length: > 0 } parent || Path.GetFileName(dest) is not { Length: > 0 } leaf) throw new BackupException("target_invalid");
        var company = await registry.GetAsync(companyId, ct) ?? throw new BackupException("company_not_found");
        if (company.State != ProvisioningState.Ready) throw new BackupException("company_not_ready");
        AuditFilePermissions.CreateOwnerOnlyDirectory(parent);
        var swept = SweepStaging(parent, leaf, clock.GetUtcNow());

        // R2-L1/R2-L2: the company's provisioning lock is held for the whole backup, so no migration, provisioning or runtime grant of this company runs while it is taken
        await using var lockConn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await lockConn.OpenAsync(ct);
        // a session lock of a just-closed connection is released by the server asynchronously: the shared acquisition waits briefly (bounded) before the company is reported busy
        if (!await CompanyProvisioner.AcquireLockAsync(lockConn, companyId, ct)) throw new BackupException("company_busy");
        try { return await BackupLockedAsync(companyId, dest, root, parent, leaf, swept, ct); }
        finally
        {
            // released explicitly, so the next administrative command on this company does not race the asynchronous release of a closed session
            try { await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", lockConn); await unlock.ExecuteNonQueryAsync(CancellationToken.None); }
            catch (NpgsqlException) { /* the lock ends with the session anyway */ }
        }
    }

    private async Task<CompanyBackupResult> BackupLockedAsync(Guid companyId, string dest, string root, string parent, string leaf, int swept, CancellationToken ct)
    {
        var company = await registry.GetAsync(companyId, ct) ?? throw new BackupException("company_not_found");  // re-read under the lock
        if (company.State != ProvisioningState.Ready) throw new BackupException("company_not_ready");

        // restorability up front (D-059): an ICU/builtin company is refused before anything is dumped
        await using (var maint = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await maint.OpenAsync(ct);
            PgBackupTool.EnsureRestorable(await PgBackupTool.ReadLocaleAsync(maint, company.PhysicalDatabaseName, ct) ?? throw new BackupException("company_database_missing"));
        }
        var level = await ReadMigrationLevelAsync(company.PhysicalDatabaseName, ct) ?? throw new BackupException("migration_journal_unreadable");

        // review M1: the dump is written into a FRESH owner-only (0700) staging folder, so the full company dump is never readable by other local users during the dump, the copy or after a crash
        var stagingDir = CreateStagingDirectory(parent, leaf);
        var staging = Path.Combine(stagingDir, BackupSet.DatabaseFileName);
        try
        {
            var locale = await tool.DumpAsync(company.PhysicalDatabaseName, staging, ct);
            PgBackupTool.EnsureRestorable(locale);
            // R2-L2 belt and braces: the level is re-read after the dump; a change (impossible while the lock is held) refuses the set
            if (await ReadMigrationLevelAsync(company.PhysicalDatabaseName, ct) != level) throw new BackupException("migration_level_changed");
            var manifest = BackupSet.Create(companyId, staging, Path.Combine(root, companyId.ToString("N")), dest, clock.GetUtcNow(), locale, level);
            if (!BackupSet.Verify(dest).IsClean)
            {
                try { Directory.Delete(dest, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
                throw new BackupException("backup_set_not_clean");
            }
            var missing = BackupSet.CheckKeys(manifest, encryption.KeyDirectory is null ? [] : BackupSet.ListKeyIds(encryption.KeyDirectory)).Missing;
            return new CompanyBackupResult(manifest, missing, swept);
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; the next run sweeps it */ }
        }
    }

    /// <summary>Creates <c>&lt;parent&gt;/&lt;leaf&gt;.staging-&lt;32 hex&gt;</c> owner-only on Linux (0700, verified; on Windows the folder inherits the parent's ACL: choose a private destination parent there); refuses (<c>staging_exists</c>) when it already exists.</summary>
    internal static string CreateStagingDirectory(string parent, string leaf)
    {
        var path = Path.Combine(parent, leaf + ".staging-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(path, "staging_exists", "staging_not_private");
        return path;
    }

    /// <summary>Creates a NEW owner-only directory (Linux 0700, verified); <paramref name="existsCode"/> when it already exists, <paramref name="notPrivateCode"/> when group or other bits remain (Linux).</summary>
    internal static void CreatePrivateDirectory(string path, string existsCode, string notPrivateCode)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new BackupException(existsCode);
        AuditFilePermissions.CreateOwnerOnlyDirectory(path);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new BackupException(notPrivateCode);
    }

    /// <summary>
    /// Removes ONLY directories named exactly <c>&lt;leaf&gt;.staging-&lt;32 lowercase hex&gt;</c> in <paramref name="parent"/> (never a symbolic link, never a file) last written more than <see cref="StagingMaxAge"/>
    /// ago: the private staging folders of interrupted runs. Returns the number removed.
    /// </summary>
    internal static int SweepStaging(string parent, string leaf, DateTimeOffset now)
    {
        if (!Directory.Exists(parent)) return 0;
        var pattern = new Regex("^" + Regex.Escape(leaf) + "\\.staging-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        var removed = 0;
        foreach (var dir in new DirectoryInfo(parent).EnumerateDirectories())
        {
            if (!pattern.IsMatch(dir.Name) || dir.LinkTarget is not null || dir.LastWriteTimeUtc > now.UtcDateTime - StagingMaxAge) continue;
            try { dir.Delete(true); removed++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for the next run */ }
        }
        return removed;
    }

    private async Task<int?> ReadMigrationLevelAsync(string database, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(options.BuildCompanyAdminConnectionString(database));
        await conn.OpenAsync(ct);
        if (!await TableExistsAsync(conn, SystemSchema.MigrationJournalTable, ct)) return null;
        await using var cmd = new NpgsqlCommand($"SELECT max(version) FROM {SystemSchema.MigrationJournalTable}", conn);
        return await cmd.ExecuteScalarAsync(ct) is int v ? v : null;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("t", "public." + table);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ------------------------------------------------------------------ trial restore

    /// <summary>
    /// The locale a trial restore uses (D-059): the manifest locale is always validated; when the live database exists they must be the same (null and <c>c</c> are one libc provider), otherwise
    /// <c>locale_mismatch</c>; the manifest alone when the database is gone; the live locale for an older set; neither is <c>source_locale_unknown</c>.
    /// </summary>
    internal static DatabaseLocale ChooseRestoreLocale(DatabaseLocale? manifest, DatabaseLocale? live)
    {
        if (manifest is not null)
        {
            if (!manifest.IsWellFormed) throw new BackupException("locale_unsupported");
            PgBackupTool.EnsureRestorable(manifest);
            if (live is not null && !PgBackupTool.SameLocale(manifest, live)) throw new BackupException("locale_mismatch");
            return manifest;
        }
        if (live is null) throw new BackupException("source_locale_unknown");
        PgBackupTool.EnsureRestorable(live);
        return live;
    }

    /// <summary>
    /// Migration compatibility (D-062). <paramref name="manifestLevel"/> is the level captured at backup time (null for an older set), <paramref name="restoredJournal"/> the restored journal (null before the restore
    /// or when unreadable), <paramref name="embedded"/> this installation's company scripts (version to checksum). Throws <c>set_newer_than_installation</c> BEFORE a restore; returns failures and warnings after.
    /// </summary>
    internal static (List<BackupFinding> Failures, List<BackupFinding> Warnings) ClassifyMigrations(int? manifestLevel, IReadOnlyDictionary<int, string>? restoredJournal, IReadOnlyDictionary<int, string> embedded, bool beforeRestore)
    {
        var supported = embedded.Count == 0 ? 0 : embedded.Keys.Max();
        var failures = new List<BackupFinding>(); var warnings = new List<BackupFinding>();
        if (beforeRestore)
        {
            if (manifestLevel is { } lv && lv > supported) throw new BackupException("set_newer_than_installation");
            return (failures, warnings);
        }
        if (restoredJournal is null || restoredJournal.Count == 0) { failures.Add(new("migration_journal_unreadable", 1)); return (failures, warnings); }
        var restored = restoredJournal.Keys.Max();
        if (manifestLevel is { } level && restored != level) failures.Add(new("migration_level_mismatch", 1));
        if (restored > supported) failures.Add(new("set_newer_than_installation", 1));
        else if (restored < supported) warnings.Add(new("migration_level_older", 1));
        var diverged = restoredJournal.Count(j => embedded.TryGetValue(j.Key, out var sum) && !string.Equals(sum, j.Value, StringComparison.Ordinal));
        if (diverged > 0) failures.Add(new("migration_checksum_mismatch", diverged));
        return (failures, warnings);
    }

    /// <summary>One attachment row as restored (the columns the check needs).</summary>
    internal sealed record AttachmentRow(Guid Id, string Sha256, bool Sensitive, string StorageName, string? KeyId);

    /// <summary>
    /// Check (b) for one row against the restored folder: a failure code (<c>attachment_name_invalid</c>, <c>attachment_file_missing</c>, <c>attachment_hash_mismatch</c>, <c>attachment_undecryptable</c>) or null, and
    /// warnings (<c>attachment_key_missing</c>: the envelope's key is not in the key directory; <c>attachment_key_id_stale</c>: the row names another key than the envelope, decryption uses the envelope).
    /// </summary>
    internal static (string? Failure, List<string> Warnings) CheckAttachment(AttachmentRow row, string folder, Guid companyId, KekRing ring)
    {
        var warnings = new List<string>();
        if (!BackupSet.IsStorageName(row.StorageName)) return ("attachment_name_invalid", warnings);
        var path = Path.Combine(folder, row.StorageName);
        if (!File.Exists(path)) return ("attachment_file_missing", warnings);
        var bytes = File.ReadAllBytes(path);
        if (!row.Sensitive) return (Hash(bytes) == row.Sha256 ? null : "attachment_hash_mismatch", warnings);
        string envelopeKey;
        try { envelopeKey = EnvelopeEncryptor.KeyIdOf(bytes); }
        catch (EncryptionUnavailableException) { return ("attachment_undecryptable", warnings); }
        if (!string.Equals(row.KeyId, envelopeKey, StringComparison.Ordinal)) warnings.Add("attachment_key_id_stale");
        if (ring.Find(envelopeKey) is null) { warnings.Add("attachment_key_missing"); return (null, warnings); }
        byte[] plain;
        try { plain = new EnvelopeEncryptor(ring).Decrypt(bytes, AttachmentService.Context(companyId, row.Id)); }
        catch (EncryptionUnavailableException) { return ("attachment_undecryptable", warnings); }
        try { return (Hash(plain) == row.Sha256 ? null : "attachment_hash_mismatch", warnings); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Restores the set into a NEW generated database and <paramref name="attachmentTarget"/> (absent or empty, outside the live attachment root), runs the read-only checks and drops the database again, unless
    /// <paramref name="keep"/>: then an owner-only file next to the set records it (<see cref="DropTrialRestoreAsync"/> removes it later). Refusals before anything is created throw <see cref="BackupException"/>.
    /// </summary>
    public async Task<TrialRestoreResult> TrialRestoreAsync(string setDirectory, Guid expectedCompanyId, string attachmentTarget, bool keep, CancellationToken ct = default)
    {
        // D-073: the measured restore time of this set (verification and drop included) is part of the result
        var started = clock.GetTimestamp();
        var result = await TrialRestoreCoreAsync(setDirectory, expectedCompanyId, attachmentTarget, keep, ct);
        return result with { Elapsed = clock.GetElapsedTime(started) };
    }

    private async Task<TrialRestoreResult> TrialRestoreCoreAsync(string setDirectory, Guid expectedCompanyId, string attachmentTarget, bool keep, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(setDirectory) || !Path.IsPathFullyQualified(attachmentTarget)) throw new BackupException("path_not_absolute");
        if (expectedCompanyId == Guid.Empty) throw new BackupException("invalid_company");
        var root = BackupSet.RealPath(AttachmentRoot());
        var target = BackupSet.RealPath(attachmentTarget);
        if (BackupSet.IsInside(target, root) || BackupSet.IsInside(root, target)) throw new BackupException("target_in_live_storage");
        if (encryption.KeyDirectory is { } keyDir && (BackupSet.IsInside(target, BackupSet.RealPath(keyDir)) || BackupSet.IsInside(BackupSet.RealPath(keyDir), target))) throw new BackupException("target_overlaps_keys");   // review L1
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new BackupException("target_not_empty");
        var set = BackupSet.RealPath(setDirectory);
        var verification = BackupSet.Verify(set);
        if (!verification.IsClean || verification.Manifest is not { } manifest) throw new BackupException("backup_set_not_clean");
        if (manifest.CompanyId != expectedCompanyId) throw new BackupException("company_mismatch");
        var company = await registry.GetAsync(expectedCompanyId, ct) ?? throw new BackupException("company_not_found");

        var embedded = scripts.ToDictionary(s => s.Version, s => s.Checksum);
        ClassifyMigrations(manifest.CompanyMigrationVersion, null, embedded, beforeRestore: true);

        DatabaseLocale locale;
        await using (var maint = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await maint.OpenAsync(ct);
            locale = ChooseRestoreLocale(manifest.DatabaseLocale, await PgBackupTool.ReadLocaleAsync(maint, company.PhysicalDatabaseName, ct));
        }

        var trial = nameGenerator();
        PhysicalIdentifier.Validate(trial, nameof(trial));
        await tool.RestoreIntoNewDatabaseAsync(Path.Combine(set, BackupSet.DatabaseFileName), trial, company.PhysicalDatabaseName, locale, ct);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        TrialRestoreResult result;
        try
        {
            await MaintenanceAsync($"COMMENT ON DATABASE {Quote(trial)} IS '{CommentPrefix}{token}'", ct);
            var files = BackupSet.RestoreAttachments(set, target, expectedCompanyId);

            var failures = new List<BackupFinding>(); var warnings = new List<BackupFinding>();
            if (manifest.CompanyMigrationVersion is { } lv && lv < SupportedMigrationVersion) warnings.Add(new("migration_level_older", 1));

            // the restored database itself: locale and migrations (check c)
            await using (var maint = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
            {
                await maint.OpenAsync(ct);
                if (await PgBackupTool.ReadLocaleAsync(maint, trial, ct) is not { } restoredLocale || !PgBackupTool.SameLocale(restoredLocale, locale)) failures.Add(new("restored_locale_mismatch", 1));
            }
            await using var db = new NpgsqlConnection(options.BuildCompanyAdminConnectionString(trial));
            await db.OpenAsync(ct);
            var journal = await ReadJournalAsync(db, ct);
            var (mf, mw) = ClassifyMigrations(manifest.CompanyMigrationVersion, journal, embedded, beforeRestore: false);
            failures.AddRange(mf);
            foreach (var w in mw) if (!warnings.Any(x => x.Code == w.Code)) warnings.Add(w);

            // check a: shared party links, both directions, read only
            var (unresolved, missingLocally) = await CheckSharedLinksAsync(db, expectedCompanyId, ct);
            if (unresolved > 0) failures.Add(new("shared_links_unresolved", unresolved));
            if (missingLocally > 0) warnings.Add(new("shared_links_missing_locally", missingLocally));

            // check b: attachment rows against the restored files
            var rows = await ReadAttachmentRowsAsync(db, ct);
            var ring = new FileKekRing(encryption);
            var failCounts = new SortedDictionary<string, int>(StringComparer.Ordinal); var warnCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var (failure, rowWarnings) = CheckAttachment(row, target, expectedCompanyId, ring);
                if (failure is not null) failCounts[failure] = failCounts.GetValueOrDefault(failure) + 1;
                foreach (var w in rowWarnings) warnCounts[w] = warnCounts.GetValueOrDefault(w) + 1;
            }
            failures.AddRange(failCounts.Select(kv => new BackupFinding(kv.Key, kv.Value)));
            warnings.AddRange(warnCounts.Select(kv => new BackupFinding(kv.Key, kv.Value)));
            var referenced = rows.Select(r => r.StorageName).ToHashSet(StringComparer.Ordinal);
            var unreferenced = Directory.Exists(target) ? Directory.EnumerateFiles(target).Count(f => !referenced.Contains(Path.GetFileName(f))) : 0;

            var keyCoverage = BackupSet.CheckKeys(manifest, encryption.KeyDirectory is null ? [] : BackupSet.ListKeyIds(encryption.KeyDirectory));
            if (!keyCoverage.IsComplete) warnings.Add(new("key_ids_missing", keyCoverage.Missing.Count));

            var verdict = failures.Count > 0 ? TrialRestoreVerdict.Failed : warnings.Count > 0 ? TrialRestoreVerdict.OkWithWarnings : TrialRestoreVerdict.Ok;
            result = new TrialRestoreResult(verdict, failures, warnings, files.Files, unreferenced, journal is { Count: > 0 } ? journal.Keys.Max() : null, null);
        }
        catch (Exception ex)
        {
            // the trial database this run created never stays behind silently: dropped, or recorded in a kept-trial file and reported
            if (!await DropAsync(trial))
            {
                try { WriteKeptFile(set, new KeptTrial(1, expectedCompanyId, trial, token)); } catch (Exception wx) when (wx is IOException or UnauthorizedAccessException) { /* reported below */ }
                throw new BackupException("trial_drop_failed", ex is BackupException be ? be.Code : ex.GetType().Name);
            }
            throw;
        }
        if (keep) return result with { KeptFile = WriteKeptFile(set, new KeptTrial(1, expectedCompanyId, trial, token)) };
        if (await DropAsync(trial)) return result;
        // the database could not be dropped: it is recorded for drop-trial-restore instead of being left unnamed
        return result with { Verdict = TrialRestoreVerdict.Failed, Failures = [.. result.Failures, new BackupFinding("trial_drop_failed", 1)], KeptFile = WriteKeptFile(set, new KeptTrial(1, expectedCompanyId, trial, token)) };
    }

    private static async Task<IReadOnlyDictionary<int, string>?> ReadJournalAsync(NpgsqlConnection db, CancellationToken ct)
    {
        if (!await TableExistsAsync(db, SystemSchema.MigrationJournalTable, ct)) return null;
        var journal = new Dictionary<int, string>();
        await using var cmd = new NpgsqlCommand($"SELECT version, checksum FROM {SystemSchema.MigrationJournalTable}", db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) journal[r.GetInt32(0)] = r.GetString(1);
        return journal;
    }

    /// <summary>
    /// Check (a): every restored local party with a global party id must have THIS company's link to the SAME global party in <c>Hasm_System</c> (unresolved = failure); links of this company in the
    /// registry whose local party is absent from the restored copy or carries no or another global id are reported (R2-L3, warning). Read only, counts only.
    /// </summary>
    private async Task<(int Unresolved, int MissingLocally)> CheckSharedLinksAsync(NpgsqlConnection db, Guid companyId, CancellationToken ct)
    {
        var local = new Dictionary<Guid, Guid?>();
        if (await TableExistsAsync(db, LP.Table, ct))
        {
            await using var cmd = new NpgsqlCommand($"SELECT {LP.Id}, {LP.GlobalPartyId} FROM {LP.Table}", db);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) local[r.GetGuid(0)] = r.IsDBNull(1) ? null : r.GetGuid(1);
        }
        var links = new Dictionary<Guid, Guid>();
        await using (var sys = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true)))
        {
            await sys.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand($"SELECT l.{PL.LocalPartyId}, l.{PL.GlobalPartyId} FROM {PL.Table} l JOIN {GP.Table} g ON g.{GP.Id} = l.{PL.GlobalPartyId} WHERE l.{PL.CompanyId} = @c", sys);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) links[r.GetGuid(0)] = r.GetGuid(1);
        }
        return CountLinks(local, links);
    }

    /// <summary>The pure counting of check (a): <paramref name="local"/> = restored local party to its global id (or null), <paramref name="links"/> = this company's registry links.</summary>
    internal static (int Unresolved, int MissingLocally) CountLinks(IReadOnlyDictionary<Guid, Guid?> local, IReadOnlyDictionary<Guid, Guid> links)
    {
        var unresolved = local.Count(p => p.Value is { } g && (!links.TryGetValue(p.Key, out var linked) || linked != g));
        var missingLocally = links.Count(l => !local.TryGetValue(l.Key, out var g) || g is null);
        return (unresolved, missingLocally);
    }

    private static async Task<List<AttachmentRow>> ReadAttachmentRowsAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var rows = new List<AttachmentRow>();
        if (!await TableExistsAsync(db, AT.Table, ct)) return rows;
        await using var cmd = new NpgsqlCommand($"SELECT {AT.Id}, {AT.Sha256}, {AT.Sensitivity}, {AT.StorageName}, {AT.KeyId} FROM {AT.Table}", db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new AttachmentRow(r.GetGuid(0), r.GetString(1), r.GetInt16(2) == 2, r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return rows;
    }

    // ------------------------------------------------------------------ kept trial databases

    /// <summary>The content of the owner-only file that names a kept trial database.</summary>
    internal sealed record KeptTrial(int Version, Guid CompanyId, string Database, string Token);

    private static readonly JsonSerializerOptions KeptJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static string WriteKeptFile(string set, KeptTrial kept)
    {
        var path = set.TrimEnd(Path.DirectorySeparatorChar) + ".trial-" + Guid.NewGuid().ToString("N") + ".json";
        AuditFilePermissions.CreateOwnerOnlyFile(path, JsonSerializer.SerializeToUtf8Bytes(kept, KeptJson));
        return path;
    }

    /// <summary>Reads and validates a kept-trial file; null when it is not one (malformed, unknown version, invalid name or token).</summary>
    internal static KeptTrial? ReadKeptFile(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
        KeptTrial? k;
        try { k = JsonSerializer.Deserialize<KeptTrial>(File.ReadAllBytes(path), KeptJson); }
        catch (JsonException) { return null; }
        return k is { Version: 1 } && k.CompanyId != Guid.Empty && PhysicalIdentifier.IsValid(k.Database) && k.Token is not null && Hex32.IsMatch(k.Token) ? k : null;
    }

    /// <summary>
    /// Drops a kept trial database. Refused (<c>not_a_trial_restore</c>) unless the file is valid, the database is not a registered company database or a system, maintenance or built-in database, and its comment
    /// carries the file's token. R2-L1: the registry table is locked in SHARE mode (no company can be registered or changed) from the check until the DROP has completed.
    /// </summary>
    public async Task DropTrialRestoreAsync(string keptFile, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(keptFile)) throw new BackupException("path_not_absolute");
        var kept = ReadKeptFile(keptFile) ?? throw new BackupException("not_a_trial_restore");
        if (kept.Database == options.SystemDatabaseName || kept.Database == options.MaintenanceDatabase || kept.Database is "postgres" or "template0" or "template1") throw new BackupException("not_a_trial_restore");

        await using var sys = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await sys.OpenAsync(ct);
        await using var tx = await sys.BeginTransactionAsync(ct);
        // review L2: a registry writer holding the table is waited for at most 5 seconds, then reported as busy (never an unbounded wait)
        await using (var lt = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", sys, tx)) await lt.ExecuteNonQueryAsync(ct);
        try { await using var lk = new NpgsqlCommand($"LOCK TABLE {Co.Table} IN SHARE MODE", sys, tx); await lk.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable) { throw new BackupException("company_busy"); }
        await using (var q = new NpgsqlCommand($"SELECT count(*) FROM {Co.Table} WHERE {Co.PhysicalDatabaseName} = @n", sys, tx))
        {
            q.Parameters.AddWithValue("n", kept.Database);
            if ((long)(await q.ExecuteScalarAsync(ct))! != 0) throw new BackupException("not_a_trial_restore");
        }
        await using (var maint = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await maint.OpenAsync(ct);
            await using (var c = new NpgsqlCommand("SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = @n", maint))
            {
                c.Parameters.AddWithValue("n", kept.Database);
                var comment = await c.ExecuteScalarAsync(ct);
                if (comment is null) throw new BackupException("trial_database_missing");
                if (comment is not string s || !string.Equals(s, CommentPrefix + kept.Token, StringComparison.Ordinal)) throw new BackupException("not_a_trial_restore");
            }
            await using var drop = new NpgsqlCommand($"DROP DATABASE {Quote(kept.Database)} WITH (FORCE)", maint);
            await drop.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        File.Delete(keptFile);
    }

    private static string Quote(string name) => new NpgsqlCommandBuilder().QuoteIdentifier(name);

    private async Task MaintenanceAsync(string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Drops the trial database this run created (never another one); not cancellable. False when it could not be dropped.</summary>
    private async Task<bool> DropAsync(string trial)
    {
        try { await MaintenanceAsync($"DROP DATABASE IF EXISTS {Quote(trial)} WITH (FORCE)", CancellationToken.None); return true; }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { return false; }
    }
}
