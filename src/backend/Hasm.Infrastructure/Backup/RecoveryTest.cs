using System.Security.Cryptography;
using System.Text.Json;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Naming;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using E = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// <c>recovery-test.json</c> (D-085, D-117; review M2 and round 2 N1 of #116): the proof that the offline recovery PRIVATE key decrypted this run. It carries the decrypted canary PREIMAGE (32 single-purpose
/// random bytes, harmless once revealed); <c>backup-status</c> accepts it only when SHA-256(preimage) equals the hash in the run's SIGNED status. A hash alone proves nothing and is refused.
/// </summary>
public sealed record RecoveryTestProof(int Version, string Kind, string RunId, string RecoveryKeyId, DateTimeOffset TestedUtc, string Verdict, string CanaryPreimage, IReadOnlyList<Guid> Companies,
    bool KeyExportChecked)
{
    public const int CurrentVersion = 1;
    public const string KindValue = "recovery-test";
    public const string FileName = "recovery-test.json";
    public const string Passed = "passed";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>
    /// True when <paramref name="runDirectory"/> holds a proof for <paramref name="status"/> (the run's VERIFIED signed status): same run and recovery key, verdict passed, and a preimage that hashes to the
    /// signed canary hash. Anything else (absent, malformed, hash only, wrong preimage) is not a proof.
    /// </summary>
    public static bool IsValidFor(string runDirectory, RunStatus status)
    {
        var path = Path.Combine(runDirectory, FileName);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length > 64 * 1024) return false;
            var p = JsonSerializer.Deserialize<RecoveryTestProof>(File.ReadAllBytes(path), Json);
            if (p is not { Version: CurrentVersion, Kind: KindValue, Verdict: Passed } || p.RunId != status.RunId || p.RecoveryKeyId != status.RecoveryKeyId || p.CanaryPreimage is null) return false;
            var preimage = Convert.FromBase64String(p.CanaryPreimage);
            return preimage.Length == 32 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(preimage), Convert.FromHexString(status.CanarySha256));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>The outcome of a recovery test: findings (codes and counts), the restored companies, the TAGS of the disposable databases (never names) and whether a key export was checked.</summary>
public sealed record RecoveryTestResult(bool Passed, string RunId, string RecoveryKeyId, string SigningKeyId, IReadOnlyList<BackupFinding> Failures, IReadOnlyList<BackupFinding> Warnings,
    IReadOnlyList<Guid> Companies, IReadOnlyList<string> DatabaseTags, bool KeyExportChecked, TimeSpan Elapsed)
{
    /// <summary>The decrypted canary (only for a passed test; written into the proof, never logged).</summary>
    internal byte[]? CanaryPreimage { get; init; }
}

/// <summary>
/// <c>backup-recovery-test</c> (D-085, D-094, D-117; reviews M2, M4, M6 of #116): verifies every signature of an encrypted run with the operator's trust list, checks every stored file against its signed
/// digest, decrypts the canary, restores the system dump and every company set STREAMING into NEW disposable databases (trial-restore semantics, dropped afterwards), runs the slice 3 checks, and decrypts
/// the key export when given and checks it holds every attachment key id and audit key id the run needs. TOTP rows are never decrypted (M6): only key ids are compared. A restore operation under D-080(6)
/// (D-118): until the Phase 4.1 approval framework exists it runs through the administrative tool only, and the caller writes the system audit event.
/// </summary>
public sealed class RecoveryTestService
{
    private readonly HasmDatabaseOptions options;
    private readonly CompanyBackupService companies;
    private readonly PgBackupTool tool;
    private readonly TimeProvider clock;
    private readonly Func<string> nameGenerator;
    private readonly IReadOnlyList<MigrationScript> systemScripts;

    public RecoveryTestService(HasmDatabaseOptions options, AttachmentOptions attachments, EncryptionOptions encryption, TimeProvider? clock = null, string? pgBinDirectory = null,
        Func<string>? nameGenerator = null, IReadOnlyList<MigrationScript>? companyScripts = null, IReadOnlyList<MigrationScript>? systemScripts = null)
    {
        this.options = options;
        this.clock = clock ?? TimeProvider.System;
        this.nameGenerator = nameGenerator ?? PhysicalIdentifier.GenerateDatabaseName;
        this.systemScripts = systemScripts ?? MigrationScript.LoadEmbedded();
        tool = new PgBackupTool(options, pgBinDirectory);
        companies = new CompanyBackupService(options, attachments, encryption, this.clock, pgBinDirectory, this.nameGenerator, companyScripts);
    }

    /// <summary>
    /// Runs the test. <paramref name="workDirectory"/> (absent or empty, outside live storage) receives the decrypted attachments of each trial restore and is removed afterwards. Refusals before anything is
    /// restored (unsigned or unverifiable status or manifests, another recovery key) throw <see cref="BackupException"/>; findings during the restores are returned.
    /// </summary>
    public async Task<RecoveryTestResult> RunAsync(string runDirectory, RestoreKeys keys, string? keysExport, string workDirectory, CancellationToken ct = default)
    {
        var started = clock.GetTimestamp();
        if (!Path.IsPathFullyQualified(runDirectory) || !Path.IsPathFullyQualified(workDirectory) || (keysExport is not null && !Path.IsPathFullyQualified(keysExport))) throw new BackupException("path_not_absolute");
        var run = BackupSet.RealPath(runDirectory);
        if (!File.Exists(Path.Combine(run, SignedDocuments.RunStatusFileName))) throw new BackupException(File.Exists(Path.Combine(run, BackupRunFiles.ReportFileName)) ? "unencrypted_run" : "run_incomplete");
        var status = SignedDocuments.ReadRunStatus(run, keys.Trust);
        if (status.RecoveryKeyId != keys.Recovery.KeyId) throw new BackupException("recovery_key_mismatch");
        var runManifest = await SignedDocuments.ReadManifestAsync(run, keys.Trust, keys.Recovery, BackupRunFiles.RunKind, ct);
        if (runManifest.RunId != status.RunId || runManifest.Files is not [{ Name: BackupRunFiles.CanaryFileName, Purpose: BackupRunFiles.CanaryPurpose } canaryEntry]) throw new BackupException("manifest_invalid");
        await EncryptedSets.VerifyEntriesAsync(run, runManifest.Files, ct);
        if (Directory.Exists(workDirectory) && Directory.EnumerateFileSystemEntries(workDirectory).Any()) throw new BackupException("target_not_empty");

        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var warnings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Fail(string code, int n = 1) => failures[code] = failures.GetValueOrDefault(code) + n;
        void Warn(string code, int n = 1) => warnings[code] = warnings.GetValueOrDefault(code) + n;
        var tags = new List<string>();
        var restored = new List<Guid>();
        var attachmentKeys = new SortedSet<string>(StringComparer.Ordinal);
        var auditKeys = new SortedSet<string>(StringComparer.Ordinal);

        // 1. the canary (D-117): only its hash is in the signed status; the preimage is the proof
        byte[]? preimage = null;
        using (var canary = new BoundedMemoryStream(1024))
        {
            await EncryptedSets.DecryptAsync(run, canaryEntry, keys.Recovery, canary, ct);
            var bytes = canary.ToArray();
            if (bytes.Length == 32 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(status.CanarySha256))) preimage = bytes;
            else { Fail("canary_mismatch"); CryptographicOperations.ZeroMemory(bytes); }
        }

        // 2. the system dump into a NEW disposable database
        try { await TestSystemAsync(Path.Combine(run, BackupRunFiles.SystemFolder), status.RunId, keys, tags, auditKeys, Fail, ct); }
        catch (BackupException ex) { Fail(BackupRunFiles.IsCode(ex.Code) ? "system_" + ex.Code : "system_restore_failed"); }
        catch (NpgsqlException) { Fail("system_database_error"); }

        // 3. every company set of the run (trial-restore semantics: new database, checks, dropped)
        try
        {
            foreach (var c in status.Companies.Where(c => c.Outcome is "ok" or "ok_missing_keys"))
            {
                var target = Path.Combine(workDirectory, c.CompanyId.ToString("N"));
                try
                {
                    var r = await companies.TrialRestoreAsync(Path.Combine(run, BackupRunFiles.CompaniesFolder, c.CompanyId.ToString("N")), c.CompanyId, target, keep: false, keys, ct);
                    restored.Add(c.CompanyId);
                    if (r.DatabaseTag is { } tag) tags.Add(tag);
                    foreach (var f in r.Failures) Fail(f.Code, f.Count);
                    foreach (var w in r.Warnings) Warn(w.Code, w.Count);
                    foreach (var k in r.KeyIdsRequired ?? []) attachmentKeys.Add(k);
                    foreach (var k in r.AuditKeyIds ?? []) auditKeys.Add(k);
                }
                catch (BackupException ex) { Fail(BackupRunFiles.IsCode(ex.Code) ? ex.Code : "company_restore_failed"); }
                catch (NpgsqlException) { Fail("company_database_error"); }
                finally { try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ } }
            }
        }
        finally { try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ } }

        // 4. key recovery (D-094, review M4): the export decrypts and holds every key id the run needs. Phase 4.1's TOTP KEK ids join this check once the user_totp table exists; its rows are never decrypted (M6).
        var keyExportChecked = false;
        if (keysExport is null) Warn("key_export_not_checked");
        else
        {
            try
            {
                var (export, _) = await KeyExport.ReadAsync(keysExport, keys.Trust, keys.Recovery, ct);
                keyExportChecked = true;
                var missingEncryption = attachmentKeys.Count(k => !export.EncryptionKeyIds.Contains(k));
                var missingAudit = auditKeys.Count(k => !export.AuditKeyIds.Contains(k));
                if (missingEncryption > 0) Fail("key_export_missing_keys", missingEncryption);
                if (missingAudit > 0) Fail("key_export_missing_audit_keys", missingAudit);
            }
            catch (BackupException ex) { Fail(BackupRunFiles.IsCode(ex.Code) ? "key_export_" + ex.Code : "key_export_invalid"); }
        }

        var passed = failures.Count == 0 && preimage is not null;
        if (!passed && preimage is not null) { CryptographicOperations.ZeroMemory(preimage); preimage = null; }
        return new RecoveryTestResult(passed, status.RunId, status.RecoveryKeyId, status.SigningKeyId, [.. failures.Select(kv => new BackupFinding(kv.Key, kv.Value))],
            [.. warnings.Select(kv => new BackupFinding(kv.Key, kv.Value))], restored, tags, keyExportChecked, clock.GetElapsedTime(started)) { CanaryPreimage = preimage };
    }

    /// <summary>Writes the proof of a PASSED test into the run folder (owner-only, replacing an earlier proof). Call only after the system audit event was written (D-118).</summary>
    public static string WriteProof(string runDirectory, RecoveryTestResult result, DateTimeOffset testedUtc)
    {
        if (!result.Passed || result.CanaryPreimage is not { Length: 32 } preimage) throw new BackupException("recovery_test_not_passed");
        var proof = new RecoveryTestProof(RecoveryTestProof.CurrentVersion, RecoveryTestProof.KindValue, result.RunId, result.RecoveryKeyId, testedUtc, RecoveryTestProof.Passed,
            Convert.ToBase64String(preimage), result.Companies, result.KeyExportChecked);
        var path = Path.Combine(BackupSet.RealPath(runDirectory), RecoveryTestProof.FileName);
        if (File.Exists(path)) File.Delete(path);
        BackupRunFiles.WriteOwnerOnlyAtomically(path, proof.ToBytes());
        return path;
    }

    private async Task TestSystemAsync(string systemDir, string runId, RestoreKeys keys, List<string> tags, SortedSet<string> auditKeys, Action<string, int> fail, CancellationToken ct)
    {
        if (!Directory.Exists(systemDir)) throw new BackupException("set_missing");
        var m = await SignedDocuments.ReadManifestAsync(systemDir, keys.Trust, keys.Recovery, EncryptedSets.SystemKind, ct);
        if (m.RunId != runId || m.System?.DatabaseLocale is not { } locale || m.Files is not [{ Name: EncryptedSets.DumpName, Purpose: EncryptedSets.SystemDumpPurpose } dump]) throw new BackupException("manifest_invalid");
        await EncryptedSets.VerifyStoredAsync(systemDir, m, ct);
        var copy = nameGenerator();
        PhysicalIdentifier.Validate(copy, nameof(copy));
        await tool.RestoreSystemCopyFromStreamAsync((stdin, c) => EncryptedSets.DecryptAsync(systemDir, dump, keys.Recovery, stdin, c), copy, locale, ct);
        tags.Add(BackupAudit.DatabaseTag(copy));
        try
        {
            await using var db = new NpgsqlConnection(options.BuildCompanyAdminConnectionString(copy));
            await db.OpenAsync(ct);
            // migration compatibility of the system copy: the restored level is the signed one, and no restored script diverges from this installation's
            var journal = new Dictionary<int, string>();
            await using (var cmd = new NpgsqlCommand($"SELECT version, checksum FROM {SystemSchema.MigrationJournalTable}", db))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) journal[r.GetInt32(0)] = r.GetString(1);
            if (journal.Count == 0) fail("system_migration_journal_unreadable", 1);
            else
            {
                if (m.System.SystemMigrationVersion is { } lv && journal.Keys.Max() != lv) fail("system_migration_level_mismatch", 1);
                var embedded = systemScripts.ToDictionary(s => s.Version, s => s.Checksum);
                if (journal.Keys.Max() > (embedded.Count == 0 ? 0 : embedded.Keys.Max())) fail("system_set_newer_than_installation", 1);
                var diverged = journal.Count(j => embedded.TryGetValue(j.Key, out var sum) && !string.Equals(sum, j.Value, StringComparison.Ordinal));
                if (diverged > 0) fail("system_migration_checksum_mismatch", diverged);
            }
            // review M4: the audit key ids of the system and company streams (key ids only)
            foreach (var k in await CompanyBackupService.ReadAuditKeyIdsAsync(db, ct, E.Table, E.KeyId)) auditKeys.Add(k);
        }
        finally
        {
            try
            {
                await using var maint = new NpgsqlConnection(options.BuildMaintenanceConnectionString());
                await maint.OpenAsync(CancellationToken.None);
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {new NpgsqlCommandBuilder().QuoteIdentifier(copy)} WITH (FORCE)", maint);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (NpgsqlException) { fail("system_copy_drop_failed", 1); }
        }
    }
}
