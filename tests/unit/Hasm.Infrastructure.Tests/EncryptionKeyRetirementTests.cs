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
    private static KeyRetirementEntry Key(KeyRetirementReport r, string id) => r.Keys.Single(k => k.KeyId == id);

    [Fact]
    public void A_key_is_retirable_only_when_it_is_not_active_and_nothing_live_or_retained_needs_it()
    {
        var r = EncryptionKeyRetirement.Evaluate(["k1", "k2", "k3", "k4", "k5"], "k5",
            [Company(attachments: U("k2"), imports: U("k3"))], [Root("k4")]);
        Assert.True(r.Complete);
        Assert.Empty(r.Blockers);
        Assert.True(Key(r, "k1").Retirable);
        Assert.Equal(["needed_by_attachments"], Key(r, "k2").Reasons);
        Assert.Equal(["needed_by_import_originals"], Key(r, "k3").Reasons);                    // the import originals count (register item K follow-up)
        Assert.Equal(["needed_by_retained_backups"], Key(r, "k4").Reasons);
        Assert.Equal(["active_key"], Key(r, "k5").Reasons);
        Assert.Equal(["k1"], r.Keys.Where(k => k.Retirable).Select(k => k.KeyId));
    }

    [Fact]
    public void A_needed_key_missing_from_the_key_directory_is_reported_and_never_retirable()
    {
        var r = EncryptionKeyRetirement.Evaluate(["k2"], "k2", [Company(imports: U("k0"))], [Root()]);
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
        { "backup_inventory_incomplete", [Company()], [new BackupRootKeyNeeds("/backups", false, "inventory_incomplete", [])], "k9" },
        { "active_key_not_configured", [Company()], [Root()], null },
    };

    [Theory]
    [MemberData(nameof(Incomplete))]
    public void Anything_unknown_makes_the_report_incomplete_and_no_key_retirable(string blocker, CompanyKeyUsage[] companies, BackupRootKeyNeeds[] backups, string? active)
    {
        var r = EncryptionKeyRetirement.Evaluate(["k1", "k9"], active, companies, backups);
        Assert.False(r.Complete);
        Assert.Contains(r.Blockers, b => b.StartsWith(blocker, StringComparison.Ordinal));
        Assert.All(r.Keys, k => Assert.False(k.Retirable));
        Assert.Contains("report_incomplete", Key(r, "k1").Reasons);
    }

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
        var u = b.Build();
        Assert.Equal(["k1", "k2", "k3", "k4", "k5"], u.KeyIdsInUse);
        Assert.Equal(3, u.Unknown);
        Assert.Equal(4, u.UnderOtherKeys);                      // k1 (with k2), k3, k4, k5 name a key other than the active k2
    }
}
