using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 3 (D-058..D-062): the pure rules of <see cref="CompanyBackupService"/> (restore locale, migration compatibility, attachment and shared-link checks, staging sweep, kept-trial file) and its path
/// refusals, which come before any database contact (the options point at 127.0.0.1:1). The database paths are covered by the PostgreSQL suite.
/// </summary>
public sealed class CompanyBackupRulesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-cbr-" + Guid.NewGuid().ToString("N"));
    public CompanyBackupRulesTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
    private string P(params string[] parts) => Path.Combine([root, .. parts]);

    private static readonly DatabaseLocale Libc = new("UTF8", "en_US.utf8", "en_US.utf8", "c");

    private static HasmDatabaseOptions Db() => new() { Host = "127.0.0.1", Port = 1, AdminUsername = "admin", AdminPassword = "pw-not-a-secret", Username = "runtime", Password = "pw-not-a-secret", SystemDatabaseName = "hasm_sys", SslMode = Npgsql.SslMode.Disable, AllowInsecureTransport = true };

    private CompanyBackupService Service(string? storage, string? keys = null) =>
        new(Db(), new AttachmentOptions { StorageDirectory = storage }, new EncryptionOptions { KeyDirectory = keys, AllowInsecureFilePermissions = true });

    // ---- restore locale (D-059) -------------------------------------------------------------

    [Fact]
    public void The_manifest_locale_is_validated_cross_checked_against_the_live_database_and_null_and_c_are_one_libc_provider()
    {
        Assert.Same(Libc, CompanyBackupService.ChooseRestoreLocale(Libc, Libc));
        Assert.Same(Libc, CompanyBackupService.ChooseRestoreLocale(Libc, Libc with { Provider = null }));                 // PostgreSQL 14 -> 15+ upgrade: no false mismatch
        var fromFourteen = Libc with { Provider = null };
        Assert.Same(fromFourteen, CompanyBackupService.ChooseRestoreLocale(fromFourteen, Libc));
        Assert.Equal("locale_mismatch", Assert.Throws<BackupException>(() => CompanyBackupService.ChooseRestoreLocale(Libc, Libc with { Collate = "C" })).Code);
        Assert.Equal("locale_mismatch", Assert.Throws<BackupException>(() => CompanyBackupService.ChooseRestoreLocale(Libc, Libc with { Encoding = "LATIN1" })).Code);
        Assert.Same(Libc, CompanyBackupService.ChooseRestoreLocale(Libc, null));                                            // the database is gone: the manifest alone
        Assert.Same(Libc, CompanyBackupService.ChooseRestoreLocale(null, Libc));                                            // an older set: the live database
        Assert.Equal("source_locale_unknown", Assert.Throws<BackupException>(() => CompanyBackupService.ChooseRestoreLocale(null, null)).Code);
        var icu = Libc with { Provider = "i" };
        Assert.Equal("locale_unsupported", Assert.Throws<BackupException>(() => CompanyBackupService.ChooseRestoreLocale(icu, icu)).Code);   // invalid even when equal to the live one
        Assert.Equal("locale_unsupported", Assert.Throws<BackupException>(() => CompanyBackupService.ChooseRestoreLocale(Libc with { Collate = "x'y" }, null)).Code);
    }

    [Fact]
    public void Restorability_at_backup_time_accepts_libc_and_postgresql_14_and_refuses_icu_builtin_and_unsafe_names()
    {
        PgBackupTool.EnsureRestorable(Libc);
        PgBackupTool.EnsureRestorable(Libc with { Provider = null });
        foreach (var bad in new[] { Libc with { Provider = "i" }, Libc with { Provider = "b" }, Libc with { Ctype = "a\nb" }, Libc with { Collate = null } })
            Assert.Equal("locale_unsupported", Assert.Throws<BackupException>(() => PgBackupTool.EnsureRestorable(bad)).Code);
    }

    // ---- migration compatibility (D-062) ------------------------------------------------------

    private static readonly Dictionary<int, string> Embedded = new() { [1] = "a", [2] = "b", [3] = "c" };

    [Fact]
    public void A_set_newer_than_the_installation_is_refused_before_the_restore_and_older_or_equal_levels_are_not()
    {
        Assert.Equal("set_newer_than_installation", Assert.Throws<BackupException>(() => CompanyBackupService.ClassifyMigrations(4, null, Embedded, beforeRestore: true)).Code);
        foreach (int? level in new int?[] { 3, 2, null })
        {
            var (f, w) = CompanyBackupService.ClassifyMigrations(level, null, Embedded, beforeRestore: true);
            Assert.Empty(f); Assert.Empty(w);
        }
    }

    [Fact]
    public void After_the_restore_the_journal_must_equal_the_manifest_level_and_the_embedded_checksums()
    {
        static (List<BackupFinding> F, List<BackupFinding> W) C(int? level, Dictionary<int, string>? journal) => CompanyBackupService.ClassifyMigrations(level, journal, Embedded, beforeRestore: false);
        var equal = C(3, new() { [1] = "a", [2] = "b", [3] = "c" });
        Assert.Empty(equal.F); Assert.Empty(equal.W);
        var older = C(2, new() { [1] = "a", [2] = "b" });
        Assert.Empty(older.F); Assert.Equal(new BackupFinding("migration_level_older", 1), Assert.Single(older.W));
        Assert.Equal("migration_level_mismatch", Assert.Single(C(3, new() { [1] = "a", [2] = "b" }).F).Code);
        Assert.Equal(new BackupFinding("migration_checksum_mismatch", 2), Assert.Single(C(3, new() { [1] = "x", [2] = "y", [3] = "c" }).F));
        Assert.Equal("set_newer_than_installation", Assert.Single(C(null, new() { [1] = "a", [2] = "b", [3] = "c", [4] = "d" }).F).Code);   // an older set without the level
        Assert.Equal("migration_journal_unreadable", Assert.Single(C(3, null).F).Code);
    }

    // ---- check (b): attachment rows against files -----------------------------------------------

    [Fact]
    public void Attachment_rows_are_checked_against_their_files_hashes_and_keys_and_a_stale_row_key_id_or_a_missing_key_is_only_a_warning()
    {
        var company = Guid.NewGuid();
        var folder = P("restored"); Directory.CreateDirectory(folder);
        var ring = new InMemoryKekRing("k1");
        string Name() => Guid.NewGuid().ToString("N") + ".att";
        static string H(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
        var plain = Encoding.ASCII.GetBytes("%PDF-1.4 plain");
        var secret = Encoding.ASCII.GetBytes("salary");

        var okPlain = new CompanyBackupService.AttachmentRow(Guid.NewGuid(), H(plain), false, Name(), null);
        File.WriteAllBytes(Path.Combine(folder, okPlain.StorageName), plain);
        Assert.Equal("", Check(okPlain));

        var sensitiveId = Guid.NewGuid();
        var okSensitive = new CompanyBackupService.AttachmentRow(sensitiveId, H(secret), true, Name(), "k1");
        File.WriteAllBytes(Path.Combine(folder, okSensitive.StorageName), new EnvelopeEncryptor(ring).Encrypt(secret, AttachmentService.Context(company, sensitiveId)));
        Assert.Equal("", Check(okSensitive));
        Assert.Equal("|attachment_key_id_stale", Check(okSensitive with { KeyId = "k0" }));                    // decryption uses the envelope

        Assert.Equal("attachment_file_missing", Check(okPlain with { StorageName = Name() }));
        Assert.Equal("attachment_name_invalid", Check(okPlain with { StorageName = "../x.att" }));
        Assert.Equal("attachment_hash_mismatch", Check(okPlain with { Sha256 = H(secret) }));
        Assert.Equal("attachment_hash_mismatch", Check(okSensitive with { Sha256 = H(plain) }));
        Assert.Equal("attachment_undecryptable", Check(okSensitive with { Id = Guid.NewGuid() }));             // the context binds the row: another id fails authentication
        Assert.Equal("attachment_undecryptable", Check(okPlain with { Sensitive = true }));                     // not an envelope

        Assert.Equal("|attachment_key_missing", Check(okSensitive, new InMemoryKekRing("k2")));

        // "<failure>|<warning>,<warning>" (empty when clean)
        string Check(CompanyBackupService.AttachmentRow row, KekRing? keys = null)
        {
            var c = CompanyBackupService.CheckAttachment(row, folder, company, keys ?? ring);
            return c.Warnings.Count == 0 ? c.Failure ?? "" : (c.Failure ?? "") + "|" + string.Join(",", c.Warnings);
        }
    }

    // ---- check (a): shared links, both directions (R2-L3) ----------------------------------------

    [Fact]
    public void Shared_links_count_unresolved_local_parties_and_registry_links_missing_locally()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), g1 = Guid.NewGuid(), g2 = Guid.NewGuid(), g3 = Guid.NewGuid();
        var local = new Dictionary<Guid, Guid?> { [a] = g1, [b] = g2, [c] = null, [d] = g3 };
        var links = new Dictionary<Guid, Guid> { [a] = g1, [b] = g3, [c] = g2, [Guid.NewGuid()] = g1 };
        // b: linked to another global party (unresolved); d: no link (unresolved); c: linked in the registry but no global id locally, plus one link whose local party is absent (missing locally)
        Assert.Equal((2, 2), CompanyBackupService.CountLinks(local, links));
        Assert.Equal((0, 0), CompanyBackupService.CountLinks(new Dictionary<Guid, Guid?> { [a] = g1, [c] = null }, new Dictionary<Guid, Guid> { [a] = g1 }));
    }

    // ---- staging sweep (L4) -----------------------------------------------------------------------

    [Fact]
    public void Only_exact_stale_staging_names_of_this_destination_are_swept()
    {
        var now = DateTimeOffset.UtcNow;
        var hex = Guid.NewGuid().ToString("N");
        string Make(string name, TimeSpan age) { var p = P(name); File.WriteAllText(p, "x"); File.SetLastWriteTimeUtc(p, (now - age).UtcDateTime); return p; }
        var stale = Make("set.dump-" + hex, TimeSpan.FromHours(25));
        var young = Make("set.dump-" + Guid.NewGuid().ToString("N"), TimeSpan.FromHours(1));
        var otherLeaf = Make("other.dump-" + Guid.NewGuid().ToString("N"), TimeSpan.FromHours(30));
        var upper = Make("set.dump-" + Guid.NewGuid().ToString("N").ToUpperInvariant(), TimeSpan.FromHours(30));
        var shortSuffix = Make("set.dump-abc", TimeSpan.FromHours(30));
        var dir = P("set.dump-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); Directory.SetLastWriteTimeUtc(dir, (now - TimeSpan.FromHours(30)).UtcDateTime);
        Assert.Equal(1, CompanyBackupService.SweepStaging(root, "set", now));
        Assert.False(File.Exists(stale));
        Assert.All(new[] { young, otherLeaf, upper, shortSuffix }, p => Assert.True(File.Exists(p)));
        Assert.True(Directory.Exists(dir));
        Assert.Equal(0, CompanyBackupService.SweepStaging(P("absent"), "set", now));
    }

    // ---- kept-trial file (L3) ---------------------------------------------------------------------

    [Fact]
    public void The_kept_trial_file_is_owner_only_and_a_malformed_one_is_refused()
    {
        var set = P("set"); Directory.CreateDirectory(set);
        var kept = new CompanyBackupService.KeptTrial(1, Guid.NewGuid(), "d" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var path = CompanyBackupService.WriteKeptFile(set, kept);
        Assert.StartsWith(set + ".trial-", path);
        Assert.Equal(kept, CompanyBackupService.ReadKeptFile(path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        void Bad(string json) { var p = P(Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(p, json); Assert.Null(CompanyBackupService.ReadKeptFile(p)); }
        Bad("not json");
        Bad($"{{\"version\":2,\"companyId\":\"{kept.CompanyId}\",\"database\":\"{kept.Database}\",\"token\":\"{kept.Token}\"}}");
        Bad($"{{\"version\":1,\"companyId\":\"{kept.CompanyId}\",\"database\":\"Bad Name\",\"token\":\"{kept.Token}\"}}");
        Bad($"{{\"version\":1,\"companyId\":\"{kept.CompanyId}\",\"database\":\"{kept.Database}\",\"token\":\"short\"}}");
        Bad($"{{\"version\":1,\"companyId\":\"{Guid.Empty}\",\"database\":\"{kept.Database}\",\"token\":\"{kept.Token}\"}}");
        Assert.Null(CompanyBackupService.ReadKeptFile(P("missing.json")));
    }

    // ---- path refusals before any database contact --------------------------------------------------

    [Fact]
    public async Task Backup_paths_are_refused_before_any_database_contact()
    {
        var storage = P("storage"); Directory.CreateDirectory(storage);
        var keys = P("keys"); Directory.CreateDirectory(keys);
        var company = Guid.NewGuid();
        Assert.Equal("attachment_storage_not_configured", (await Assert.ThrowsAsync<BackupException>(() => Service(null).BackupCompanyAsync(company, P("dest")))).Code);
        Assert.Equal("destination_overlaps_source", (await Assert.ThrowsAsync<BackupException>(() => Service(storage, keys).BackupCompanyAsync(company, Path.Combine(storage, "x")))).Code);
        Assert.Equal("destination_overlaps_source", (await Assert.ThrowsAsync<BackupException>(() => Service(storage, keys).BackupCompanyAsync(company, Path.Combine(keys, "x")))).Code);
        Assert.Equal("destination_overlaps_source", (await Assert.ThrowsAsync<BackupException>(() => Service(Path.Combine(root, "s2", "inner"), keys).BackupCompanyAsync(company, Path.Combine(root, "s2")))).Code);
        var full = P("full"); Directory.CreateDirectory(full); File.WriteAllText(Path.Combine(full, "x"), "x");
        Assert.Equal("destination_not_empty", (await Assert.ThrowsAsync<BackupException>(() => Service(storage, keys).BackupCompanyAsync(company, full))).Code);
        Assert.Equal("path_not_absolute", (await Assert.ThrowsAsync<BackupException>(() => Service(storage, keys).BackupCompanyAsync(company, "relative"))).Code);
    }

    [Fact]
    public async Task A_trial_attachment_target_in_live_storage_is_refused_also_through_a_symbolic_link()
    {
        var storage = P("storage"); Directory.CreateDirectory(storage);
        var set = P("set"); Directory.CreateDirectory(set);
        var company = Guid.NewGuid();
        Assert.Equal("target_in_live_storage", (await Assert.ThrowsAsync<BackupException>(() => Service(storage).TrialRestoreAsync(set, company, Path.Combine(storage, company.ToString("N")), false))).Code);
        if (!OperatingSystem.IsWindows())
        {
            var link = P("alias"); Directory.CreateSymbolicLink(link, storage);
            Assert.Equal("target_in_live_storage", (await Assert.ThrowsAsync<BackupException>(() => Service(storage).TrialRestoreAsync(set, company, Path.Combine(link, "t"), false))).Code);
        }
        var full = P("full"); Directory.CreateDirectory(full); File.WriteAllText(Path.Combine(full, "x"), "x");
        Assert.Equal("target_not_empty", (await Assert.ThrowsAsync<BackupException>(() => Service(storage).TrialRestoreAsync(set, company, full, false))).Code);
        Assert.Equal("backup_set_not_clean", (await Assert.ThrowsAsync<BackupException>(() => Service(storage).TrialRestoreAsync(set, company, P("t"), false))).Code);   // an empty directory is no set
    }
}
