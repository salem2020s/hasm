using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// D-179..D-181: the encryption-key retirement rule (live attachments, live import originals and retained backups; fail closed on anything unknown) and the key-usage scan of stored items.
/// </summary>
public sealed class EncryptionKeyRetirementTests
{
    private static readonly Guid C1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static EncryptionKeyUsage U(params string[] ids) => new(ids, 0, 0);
    private static CompanyKeyUsage Company(EncryptionKeyUsage? attachments = null, EncryptionKeyUsage? imports = null) =>
        new(C1, true, null, attachments ?? EncryptionKeyUsage.Empty, imports ?? EncryptionKeyUsage.Empty);
    private static BackupRootKeyNeeds Root(params string[] ids) => new("/backups", true, null, ids);
    private static readonly DateTimeOffset At = new(2026, 11, 21, 9, 30, 0, TimeSpan.FromHours(3));
    private static KeyRetirementEntry Key(KeyRetirementReport r, string id) => r.Keys.Single(k => k.KeyId == id);

    [Fact]
    public void A_key_is_retirable_only_when_it_is_not_active_and_nothing_live_or_retained_needs_it()
    {
        var r = EncryptionKeyRetirement.Evaluate(["k1", "k2", "k3", "k4", "k5"], "k5",
            [Company(attachments: U("k2"), imports: U("k3"))], [Root("k4")], backupScopeAttested: true, At);
        Assert.True(r.Complete);
        Assert.Empty(r.Blockers);
        Assert.True(Key(r, "k1").Retirable);
        Assert.Equal(["needed_by_attachments"], Key(r, "k2").Reasons);
        Assert.Equal(["needed_by_import_originals"], Key(r, "k3").Reasons);                    // the import originals count (register item K follow-up)
        Assert.Equal(["needed_by_present_backups"], Key(r, "k4").Reasons);
        Assert.Equal(["active_key"], Key(r, "k5").Reasons);
        Assert.Equal(["k1"], r.Keys.Where(k => k.Retirable).Select(k => k.KeyId));
    }

    [Fact]
    public void A_needed_key_missing_from_the_key_directory_is_reported_and_never_retirable()
    {
        var r = EncryptionKeyRetirement.Evaluate(["k2"], "k2", [Company(imports: U("k0"))], [Root()], backupScopeAttested: true, At);
        var missing = Key(r, "k0");
        Assert.False(missing.Present);
        Assert.False(missing.Retirable);
        Assert.Contains("missing_from_key_directory", missing.Reasons);
    }

    public static TheoryData<string, CompanyKeyUsage[], BackupRootKeyNeeds[], string?> Incomplete => new()
    {
        { "company_unreadable", [new CompanyKeyUsage(C1, false, "company_not_ready", EncryptionKeyUsage.Empty, EncryptionKeyUsage.Empty)], [Root()], "k9" },
        { "attachment_key_unknown", [Company(attachments: new EncryptionKeyUsage([], 1, 0))], [Root()], "k9" },
        { "import_original_key_unknown", [Company(imports: new EncryptionKeyUsage([], 1, 0))], [Root()], "k9" },
        { "no_backup_root", [Company()], [], "k9" },
        // review M-FH1 of PR 182: a present run with unknown key needs, and an unfinished deletion
        { "backup_inventory_incomplete:backup_run_key_needs_unknown", [Company()], [new BackupRootKeyNeeds("/backups", false, "backup_run_key_needs_unknown", [])], "k9" },
        { "backup_inventory_incomplete:backup_root_deletion_incomplete", [Company()], [new BackupRootKeyNeeds("/backups", false, "backup_root_deletion_incomplete", [])], "k9" },
        // review H1 of PR 182: a root that proves nothing blocks, also next to a good root
        { "backup_inventory_incomplete:backup_root_missing", [Company()], [Root(), new BackupRootKeyNeeds("/typo", false, "backup_root_missing", [])], "k9" },
        { "backup_inventory_incomplete:backup_root_no_complete_verified_run", [Company()], [new BackupRootKeyNeeds("/empty", false, "backup_root_no_complete_verified_run", [])], "k9" },
        { "active_key_not_configured", [Company()], [Root()], null },
    };

    [Theory]
    [MemberData(nameof(Incomplete))]
    public void Anything_unknown_makes_the_report_incomplete_and_no_key_retirable(string blocker, CompanyKeyUsage[] companies, BackupRootKeyNeeds[] backups, string? active)
    {
        var r = EncryptionKeyRetirement.Evaluate(["k1", "k9"], active, companies, backups, backupScopeAttested: true, At);
        Assert.False(r.Complete);
        Assert.Contains(r.Blockers, b => b.StartsWith(blocker, StringComparison.Ordinal));
        Assert.All(r.Keys, k => Assert.False(k.Retirable));
        Assert.Contains("report_incomplete", Key(r, "k1").Reasons);
    }

    [Fact]
    public void Without_the_operators_attestation_of_the_backup_scope_nothing_is_retirable_and_the_scope_limits_are_always_stated()
    {
        // review H1 of PR 182: the report sees only the given roots; other destinations, copies and standalone backup-company sets are invisible, so it cannot know without the attestation
        var unattested = EncryptionKeyRetirement.Evaluate(["k1", "k9"], "k9", [Company()], [Root()], backupScopeAttested: false, At);
        Assert.False(unattested.Complete);
        Assert.Equal(["backup_scope_not_attested"], unattested.Blockers);
        Assert.All(unattested.Keys, k => Assert.False(k.Retirable));
        var attested = EncryptionKeyRetirement.Evaluate(["k1", "k9"], "k9", [Company()], [Root()], backupScopeAttested: true, At);
        Assert.True(attested.Complete);
        Assert.True(Key(attested, "k1").Retirable);
        Assert.All(new[] { unattested, attested }, r => Assert.Equal(
            ["backup_roots_not_given_are_not_scanned", "standalone_backup_company_sets_and_copies_are_not_scanned", "uploads_in_flight_and_orphan_files_are_not_scanned"], r.ScopeLimits));
        // review L-FH1 of PR 182: the report names the roots it scanned, whether the scope was attested, and when it was generated (UTC)
        Assert.Equal(["/backups"], attested.BackupRootsScanned);
        Assert.True(attested.BackupScopeAttested);
        Assert.False(unattested.BackupScopeAttested);
        Assert.Equal(At.UtcDateTime, attested.GeneratedUtc.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, attested.GeneratedUtc.Offset);
    }

    [Fact]
    public void A_backup_root_must_be_an_existing_real_directory()
    {
        var work = Path.Combine(Path.GetTempPath(), "hasm-krr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var real = Path.Combine(work, "runs"); Directory.CreateDirectory(real);
            var file = Path.Combine(work, "a-file"); File.WriteAllText(file, "x");
            Assert.Null(EncryptionKeyRetirementService.RootProblem(real));
            Assert.Null(EncryptionKeyRetirementService.RootProblem(real + Path.DirectorySeparatorChar));
            Assert.Equal("backup_root_missing", EncryptionKeyRetirementService.RootProblem(Path.Combine(work, "typo")));
            Assert.Equal("backup_root_not_a_directory", EncryptionKeyRetirementService.RootProblem(file));
            Assert.Equal("backup_root_not_absolute", EncryptionKeyRetirementService.RootProblem("runs"));
            if (OperatingSystem.IsWindows()) return;   // creating a link needs a privilege there; the rule is the same
            var link = Path.Combine(work, "link"); Directory.CreateSymbolicLink(link, real);
            var fileLink = Path.Combine(work, "file-link"); File.CreateSymbolicLink(fileLink, file);
            var dangling = Path.Combine(work, "dangling"); Directory.CreateSymbolicLink(dangling, Path.Combine(work, "gone"));
            Assert.Equal("backup_root_symbolic_link", EncryptionKeyRetirementService.RootProblem(link));
            Assert.Equal("backup_root_symbolic_link", EncryptionKeyRetirementService.RootProblem(link + Path.DirectorySeparatorChar));
            Assert.Equal("backup_root_symbolic_link", EncryptionKeyRetirementService.RootProblem(fileLink));
            Assert.Equal("backup_root_symbolic_link", EncryptionKeyRetirementService.RootProblem(dangling));
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public void Only_a_finished_verified_encrypted_run_with_known_key_needs_and_a_verdict_of_ok_or_partial_proves_a_backup_root()
    {
        var at = new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero);
        var good = new RetentionRun("20260301T020000Z-0a0b0c0d", at, at, Complete: true, Verified: true, Encrypted: true, "ok", "r1", false, [], "hash", true, false, new RunKeyNeeds([], []));
        Assert.True(EncryptionKeyRetirementService.IsCompleteVerifiedRun(good));
        Assert.True(EncryptionKeyRetirementService.IsCompleteVerifiedRun(good with { Verdict = "partial" }));
        Assert.False(EncryptionKeyRetirementService.IsCompleteVerifiedRun(good with { Verdict = "failed" }));      // a failed run wrote no usable set
        Assert.False(EncryptionKeyRetirementService.IsCompleteVerifiedRun(good with { Verdict = null }));
        Assert.False(EncryptionKeyRetirementService.IsCompleteVerifiedRun(good with { Complete = false }));        // no status: the run never finished
        Assert.False(EncryptionKeyRetirementService.IsCompleteVerifiedRun(good with { Verified = false }));        // unsigned, forged or signed by an untrusted key
        Assert.False(EncryptionKeyRetirementService.IsCompleteVerifiedRun(good with { Keys = null }));             // a version 1 status: key needs unknown
        Assert.False(EncryptionKeyRetirementService.IsCompleteVerifiedRun(good with { Encrypted = false }));       // an unencrypted DEV/TEST run
    }

    [Fact]
    public void The_backup_inventory_counts_every_run_present_in_the_root_and_is_incomplete_on_an_unknown_run_or_an_unfinished_deletion()
    {
        // review M-FH1 of PR 182: the inventory is of the runs ON DISK. It never asks which of them the retention plan would delete: such a run is still restorable.
        var at = new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero);
        RetentionRun Run(string id, params string[] keys) => new(id, at, at, Complete: true, Verified: true, Encrypted: true, "ok", "r1", false, [], "hash", true, false, new RunKeyNeeds(keys, []));
        var newer = Run("20260313T020000Z-0a0b0c0d", "k1");
        var older = Run("20260304T020000Z-0a0b0c0d", "k0", "k1");                                                    // e.g. a run that is no longer a daily, weekly or period-end run
        Assert.Equal((null, "k0,k1"), Flat(EncryptionKeyRetirementService.InventoryOfPresentRuns([older, newer], [])));
        Assert.Equal((null, "k1"), Flat(EncryptionKeyRetirementService.InventoryOfPresentRuns([newer], [])));

        // any present run whose key needs are unknown: unfinished, unverifiable, a version 1 status, an unencrypted DEV/TEST run. The known keys are still listed.
        foreach (var unknown in new[] { older with { Keys = null }, older with { Complete = false, Keys = null }, older with { Verified = false, Keys = null }, older with { Encrypted = false, Keys = null } })
            Assert.Equal(("backup_run_key_needs_unknown", "k1"), Flat(EncryptionKeyRetirementService.InventoryOfPresentRuns([unknown, newer], [])));
        // an unfinished deletion: what is left of that run is not readable as a run
        Assert.Equal(("backup_root_deletion_incomplete", "k1"), Flat(EncryptionKeyRetirementService.InventoryOfPresentRuns([newer], ["20260304T020000Z-0a0b0c0d.deleting-0123456789abcdef"])));
        // nothing proves the root: that code comes first
        Assert.Equal(("backup_root_no_complete_verified_run", ""), Flat(EncryptionKeyRetirementService.InventoryOfPresentRuns([], ["20260304T020000Z-0a0b0c0d.deleting-0123456789abcdef"])));
        Assert.Equal(("backup_root_no_complete_verified_run", ""), Flat(EncryptionKeyRetirementService.InventoryOfPresentRuns([older with { Keys = null }], [])));

        // and the rule turns each of them into "nothing retirable"; a key only the older run needs is kept
        var needed = EncryptionKeyRetirement.Evaluate(["k0", "k1"], "k1", [Company()], [new BackupRootKeyNeeds("/backups", true, null, ["k0", "k1"])], backupScopeAttested: true, At);
        Assert.True(needed.Complete);
        Assert.Equal(["needed_by_present_backups"], Key(needed, "k0").Reasons);
        Assert.DoesNotContain(needed.Keys, k => k.Retirable);
    }

    private static (string?, string) Flat((string? Problem, IReadOnlyList<string> Keys) x) => (x.Problem, string.Join(",", x.Keys));

    private static byte[] EnvelopeUnder(string keyId)
    {
        var ring = new InMemoryKekRing(keyId);
        return new EnvelopeEncryptor(ring).Encrypt("x"u8, "ctx");
    }

    [Fact]
    public void The_usage_scan_keeps_the_row_key_and_the_envelope_key_and_counts_what_it_cannot_determine()
    {
        var b = new EncryptionKeyUsageBuilder("k2");
        b.Add("k1", () => EnvelopeUnder("k2"));                 // a rewrap crashed after the file replacement: both keys stay needed
        b.Add("k2", () => EnvelopeUnder("k2"));                 // current
        b.Add("k3", () => null);                                // the file is gone: the row still names its key
        b.Add("k4", () => throw new IOException("unreadable"));   // unknown: its row key counts AND the item is unknown
        b.Add("k5", () => [1, 2, 3]);                           // a malformed envelope: unknown
        b.Add("NOT A KEY ID", () => EnvelopeUnder("k2"));       // a row with an invalid key id: unknown
        b.Add("k6", () => throw new ArgumentException("Invalid stored file name."));   // review L-FH2 of PR 182: a stored name the store refuses is unknown, not an unhandled error
        var u = b.Build();
        Assert.Equal(["k1", "k2", "k3", "k4", "k5", "k6"], u.KeyIdsInUse);
        Assert.Equal(4, u.Unknown);
        Assert.Equal(5, u.UnderOtherKeys);                      // k1 (with k2), k3, k4, k5, k6 name a key other than the active k2
    }
}
