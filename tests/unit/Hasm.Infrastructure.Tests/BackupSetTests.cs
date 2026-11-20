using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 1: the coordinated company backup set (database dump + attachment folder + keys needed) is created, verified and restored to an isolated empty target
/// without touching any other directory. Real temporary directories, random disposable keys only; no database, no secrets.
/// </summary>
public sealed class BackupSetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-bk-" + Guid.NewGuid().ToString("N"));
    private readonly Guid company = Guid.NewGuid();
    private readonly InMemoryKekRing ring = new("k1");

    public BackupSetTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private string P(params string[] parts) => Path.Combine([root, .. parts]);
    private static string Name() => Guid.NewGuid().ToString("N") + ".att";

    private (string Dump, string Source, string Plain, string Sensitive) Source(string sensitiveKey = "k1")
    {
        var source = P("att", company.ToString("N"));
        Directory.CreateDirectory(source);
        var dump = P("db.dump");
        File.WriteAllBytes(dump, RandomNumberGenerator.GetBytes(4096));
        var plain = Name(); File.WriteAllBytes(Path.Combine(source, plain), Encoding.ASCII.GetBytes("%PDF-1.4 plain"));
        var enc = new EnvelopeEncryptor(ring);
        if (sensitiveKey != "k1") { ring.Add(sensitiveKey, RandomNumberGenerator.GetBytes(32)); ring.Activate(sensitiveKey); }
        var sensitive = Name(); File.WriteAllBytes(Path.Combine(source, sensitive), enc.Encrypt(Encoding.ASCII.GetBytes("secret"), "ctx"));
        return (dump, source, plain, sensitive);
    }

    [Fact]
    public void A_set_is_created_with_hashes_the_needed_key_ids_and_no_key_material_and_verifies_clean()
    {
        var s = Source();
        File.WriteAllBytes(Path.Combine(s.Source, "upload.att.tmp-abc"), [1, 2, 3]);              // a stray temporary upload
        var set = P("set");
        var m = BackupSet.Create(company, s.Dump, s.Source, set, DateTimeOffset.UnixEpoch);
        Assert.Equal(2, m.Attachments.Count);
        Assert.Equal(new[] { "k1" }, m.KeyIdsRequired);
        Assert.Equal(1, m.SkippedStrayFiles);
        Assert.False(File.Exists(Path.Combine(set, "attachments", "upload.att.tmp-abc")));
        var v = BackupSet.Verify(set);
        Assert.True(v.IsClean);
        var json = File.ReadAllText(Path.Combine(set, BackupSet.ManifestFileName));
        Assert.DoesNotContain("contract", json);                                                   // only generated storage names
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(s.Dump))), json);
    }

    [Fact]
    public void Removed_attachment_bytes_and_every_key_they_need_are_part_of_the_set()
    {
        var s = Source();
        var older = Name();                                                                         // a retained file under an older key k0
        var ring0 = new InMemoryKekRing("k0");
        File.WriteAllBytes(Path.Combine(s.Source, older), new EnvelopeEncryptor(ring0).Encrypt([9], "ctx"));
        var m = BackupSet.Create(company, s.Dump, s.Source, P("set"), DateTimeOffset.UnixEpoch);
        Assert.Equal(3, m.Attachments.Count);
        Assert.Equal(new[] { "k0", "k1" }, m.KeyIdsRequired);
    }

    [Fact]
    public void Altered_missing_and_unlisted_files_and_a_tampered_manifest_make_the_set_not_clean_and_are_named_by_generated_name_only()
    {
        var s = Source();
        var set = P("set");
        BackupSet.Create(company, s.Dump, s.Source, set, DateTimeOffset.UnixEpoch);

        File.AppendAllText(Path.Combine(set, "attachments", s.Plain), "x");
        var v = BackupSet.Verify(set);
        Assert.False(v.IsClean);
        Assert.Equal(new[] { s.Plain }, v.HashMismatch);

        File.Delete(Path.Combine(set, "attachments", s.Sensitive));
        File.WriteAllText(Path.Combine(set, "attachments", "extra.att"), "x");
        File.WriteAllBytes(Path.Combine(set, BackupSet.DatabaseFileName), [1]);
        v = BackupSet.Verify(set);
        Assert.Equal(new[] { s.Sensitive }, v.Missing);
        Assert.Single(v.Unexpected);
        Assert.False(v.DatabaseOk);
        Assert.Contains(BackupSet.DatabaseFileName, v.HashMismatch);

        File.WriteAllText(Path.Combine(set, BackupSet.ManifestFileName), "{ not json");
        Assert.False(BackupSet.Verify(set).ManifestValid);
    }

    [Fact]
    public void A_manifest_naming_a_path_outside_the_set_is_rejected_as_invalid()
    {
        var s = Source();
        var set = P("set");
        BackupSet.Create(company, s.Dump, s.Source, set, DateTimeOffset.UnixEpoch);
        var path = Path.Combine(set, BackupSet.ManifestFileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace(s.Plain, "../../evil1.att").Replace(s.Sensitive, "../../evil2.att"));
        Assert.Null(BackupSet.ReadManifest(set));
        Assert.False(BackupSet.Verify(set).IsClean);
    }

    [Fact]
    public void Restore_writes_only_the_empty_target_byte_identically_and_leaves_other_company_folders_untouched()
    {
        var s = Source();
        var other = P("att", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, Name()), "another company");
        var before = Snapshot(P("att"));
        var set = P("set");
        BackupSet.Create(company, s.Dump, s.Source, set, DateTimeOffset.UnixEpoch);

        var target = P("restored", "company");
        Assert.Equal(2, BackupSet.RestoreAttachments(set, target));
        foreach (var f in new[] { s.Plain, s.Sensitive })
            Assert.Equal(File.ReadAllBytes(Path.Combine(s.Source, f)), File.ReadAllBytes(Path.Combine(target, f)));
        Assert.Equal(before, Snapshot(P("att")));                                                   // sources and other companies unchanged
        Assert.Empty(Directory.GetDirectories(P("restored"), "*restoring*"));                       // no staging left behind
    }

    [Fact]
    public void Restore_refuses_a_non_empty_target_an_overlapping_target_and_a_set_that_is_not_clean_and_writes_nothing()
    {
        var s = Source();
        var set = P("set");
        BackupSet.Create(company, s.Dump, s.Source, set, DateTimeOffset.UnixEpoch);

        var occupied = P("occupied"); Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "keep.txt"), "x");
        Assert.Equal("target_not_empty", Assert.Throws<BackupException>(() => BackupSet.RestoreAttachments(set, occupied)).Code);
        Assert.Equal("target_overlaps_set", Assert.Throws<BackupException>(() => BackupSet.RestoreAttachments(set, Path.Combine(set, "attachments"))).Code);
        Assert.Equal("target_overlaps_set", Assert.Throws<BackupException>(() => BackupSet.RestoreAttachments(set, root)).Code);

        File.AppendAllText(Path.Combine(set, "attachments", s.Plain), "x");
        var target = P("never");
        Assert.Equal("backup_set_not_clean", Assert.Throws<BackupException>(() => BackupSet.RestoreAttachments(set, target)).Code);
        Assert.False(Directory.Exists(target));
        Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(occupied).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void Create_refuses_a_missing_dump_a_non_empty_or_overlapping_destination_and_an_unreadable_envelope()
    {
        var s = Source();
        Assert.Equal("database_dump_missing", Assert.Throws<BackupException>(() => BackupSet.Create(company, P("nope"), s.Source, P("a"), DateTimeOffset.UnixEpoch)).Code);
        var busy = P("busy"); Directory.CreateDirectory(busy); File.WriteAllText(Path.Combine(busy, "x"), "x");
        Assert.Equal("destination_not_empty", Assert.Throws<BackupException>(() => BackupSet.Create(company, s.Dump, s.Source, busy, DateTimeOffset.UnixEpoch)).Code);
        Assert.Equal("destination_overlaps_source", Assert.Throws<BackupException>(() => BackupSet.Create(company, s.Dump, s.Source, Path.Combine(s.Source, "inside"), DateTimeOffset.UnixEpoch)).Code);
        Assert.Equal("invalid_company", Assert.Throws<BackupException>(() => BackupSet.Create(Guid.Empty, s.Dump, s.Source, P("b"), DateTimeOffset.UnixEpoch)).Code);
        File.WriteAllBytes(Path.Combine(s.Source, Name()), "HFE1\x01"u8.ToArray());                 // envelope magic with a truncated header
        Assert.Equal("encrypted_file_unreadable", Assert.Throws<BackupException>(() => BackupSet.Create(company, s.Dump, s.Source, P("c"), DateTimeOffset.UnixEpoch)).Code);
    }

    [Fact]
    public void Key_coverage_names_the_key_ids_a_restored_key_directory_still_lacks_and_reads_no_key_material()
    {
        var s = Source();
        var older = Name();
        File.WriteAllBytes(Path.Combine(s.Source, older), new EnvelopeEncryptor(new InMemoryKekRing("k0")).Encrypt([9], "ctx"));
        var m = BackupSet.Create(company, s.Dump, s.Source, P("set"), DateTimeOffset.UnixEpoch);

        var keys = P("keys");
        FileKekRing.Generate(keys, "k1");
        File.WriteAllText(Path.Combine(keys, "notes.txt"), "ignored");
        Assert.Equal(new[] { "k1" }, BackupSet.ListKeyIds(keys));
        var c = BackupSet.CheckKeys(m, BackupSet.ListKeyIds(keys));
        Assert.False(c.IsComplete);
        Assert.Equal(new[] { "k0" }, c.Missing);                                                            // a retained file's key must be restored too (decision K)

        FileKekRing.Generate(keys, "k0");
        Assert.True(BackupSet.CheckKeys(m, BackupSet.ListKeyIds(keys)).IsComplete);
        Assert.Empty(BackupSet.ListKeyIds(P("no-such-directory")));
    }

    private static string[] Snapshot(string dir) =>
        [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(dir, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))))];
}
