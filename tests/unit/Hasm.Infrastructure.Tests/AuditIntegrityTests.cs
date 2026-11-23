using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Audit;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

/// <summary>Deterministic serialization, hash chaining and authentication of audit records (no database).</summary>
public class AuditCanonicalTests
{
    private static readonly Guid Company = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly byte[] Master = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static AuditRecord Sample(long seq = 1, byte[]? prev = null) => new(
        "company:" + Company.ToString("N"), seq, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 1_700_000_000_123_456, 6, Company,
        Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"),
        Guid.Parse("00000000-0000-0000-0000-000000000003"), 42, "scope: company", 1, "k1",
        prev ?? AuditCanonical.GenesisHash("company:" + Company.ToString("N")), [], []);

    [Fact]
    public void Serialization_is_deterministic_and_pinned_for_format_version_1()
    {
        var a = AuditCanonical.Serialize(Sample());
        Assert.Equal(a, AuditCanonical.Serialize(Sample()));
        // Golden value: a released format version must never change (historical records must stay verifiable). Refreshing it means a
        // format change, which requires a NEW version number and keeping the old serializer.
        Assert.Equal("47f77d8ee60b889719846e35b23f1d0e3a02e6beb1d60357faf6826fe917a2f4", Convert.ToHexString(SHA256.HashData(a)).ToLowerInvariant());
    }

    [Fact]
    public void Every_protected_field_changes_the_hash()
    {
        var baseline = AuditCanonical.ComputeHash(AuditCanonical.Serialize(Sample()));
        var variants = new Dictionary<string, AuditRecord>
        {
            ["stream"] = Sample() with { StreamKey = "system", CompanyId = null },
            ["seq"] = Sample() with { Seq = 2 },
            ["event id"] = Sample() with { EventId = Guid.NewGuid() },
            ["timestamp"] = Sample() with { OccurredMicros = 1_700_000_000_123_457 },
            ["type"] = Sample() with { EventType = 7 },
            ["company"] = Sample() with { CompanyId = Guid.NewGuid() },
            ["actor"] = Sample() with { ActorUserId = Guid.NewGuid() },
            ["actor removed"] = Sample() with { ActorUserId = null },
            ["target"] = Sample() with { TargetUserId = Guid.NewGuid() },
            ["subject"] = Sample() with { SubjectId = Guid.NewGuid() },
            ["legacy ref"] = Sample() with { LegacyRef = 43 },
            ["legacy ref removed"] = Sample() with { LegacyRef = null },
            ["detail"] = Sample() with { Detail = "scope: company " },
            ["detail removed"] = Sample() with { Detail = null },
            ["detail empty vs null"] = Sample() with { Detail = "" },
            ["key id"] = Sample() with { KeyId = "k2" },
            ["previous hash"] = Sample(prev: new byte[32]),
        };
        foreach (var (name, record) in variants)
            Assert.NotEqual(baseline, AuditCanonical.ComputeHash(AuditCanonical.Serialize(record)));
        Assert.Equal(variants.Count, variants.Values.Select(r => Convert.ToHexString(AuditCanonical.ComputeHash(AuditCanonical.Serialize(r)))).Distinct().Count());
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted_between_adjacent_strings()
    {
        // Length prefixes make ("ab","c") and ("a","bc") different byte sequences.
        var one = AuditCanonical.Serialize(Sample() with { Detail = "ab", KeyId = "c" });
        var two = AuditCanonical.Serialize(Sample() with { Detail = "a", KeyId = "bc" });
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void Timestamps_round_trip_at_microsecond_precision()
    {
        var t = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var micros = AuditCanonical.ToMicros(t);
        Assert.Equal(t.UtcTicks / 10, micros + DateTimeOffset.UnixEpoch.UtcTicks / 10);
        Assert.Equal(micros, AuditCanonical.ToMicros(AuditCanonical.FromMicros(micros)));
    }

    [Fact]
    public void Genesis_hash_is_per_stream()
    {
        Assert.NotEqual(AuditCanonical.GenesisHash("system"), AuditCanonical.GenesisHash("company:" + Company.ToString("N")));
        Assert.Equal(AuditCanonical.GenesisHash("system"), AuditCanonical.GenesisHash("system"));
    }

    [Fact]
    public void A_sealed_record_verifies_and_every_tamper_is_detected()
    {
        var keys = new InMemoryAuditKeyRing("k1");
        keys.Add("k1", Master);
        var sealedRecord = AuditCanonical.Seal(Sample(), keys.GetActive());
        Assert.Equal(RecordCheck.Ok, AuditVerifier.CheckRecord(sealedRecord, keys));

        Assert.Equal(RecordCheck.HashMismatch, AuditVerifier.CheckRecord(sealedRecord with { Detail = "scope: branch" }, keys));
        Assert.Equal(RecordCheck.HashMismatch, AuditVerifier.CheckRecord(sealedRecord with { OccurredMicros = sealedRecord.OccurredMicros + 1 }, keys));
        Assert.Equal(RecordCheck.HashMismatch, AuditVerifier.CheckRecord(sealedRecord with { ActorUserId = Guid.NewGuid() }, keys));
    }

    [Fact]
    public void An_attacker_without_the_key_cannot_forge_a_record_even_after_recomputing_the_hash()
    {
        var keys = new InMemoryAuditKeyRing("k1");
        keys.Add("k1", Master);
        var genuine = AuditCanonical.Seal(Sample(), keys.GetActive());

        var attackerKeys = new InMemoryAuditKeyRing("k1");            // same identifier, different secret
        var forged = AuditCanonical.Seal(genuine with { Detail = "scope: company; forged" }, attackerKeys.GetActive());

        Assert.Equal(RecordCheck.MacInvalid, AuditVerifier.CheckRecord(forged, keys));
        // A mere hash recomputation (database-level tamper without any key) is also rejected.
        var rehashed = genuine with { Detail = "changed" };
        rehashed = rehashed with { Hash = AuditCanonical.ComputeHash(AuditCanonical.Serialize(rehashed with { Hash = [], Mac = [] })) };
        Assert.Equal(RecordCheck.MacInvalid, AuditVerifier.CheckRecord(rehashed, keys));
    }

    [Fact]
    public void The_key_identifier_is_authenticated_so_it_cannot_be_swapped()
    {
        var keys = new InMemoryAuditKeyRing("k1");
        keys.Add("k1", Master);
        keys.Add("k2", Enumerable.Range(50, 32).Select(i => (byte)i).ToArray());
        var rec = AuditCanonical.Seal(Sample(), keys.GetActive());
        Assert.Equal(RecordCheck.HashMismatch, AuditVerifier.CheckRecord(rec with { KeyId = "k2" }, keys));
    }

    [Fact]
    public void A_missing_key_is_reported_as_unverifiable_not_as_valid()
    {
        var keys = new InMemoryAuditKeyRing("k1");
        var rec = AuditCanonical.Seal(Sample(), keys.GetActive());
        keys.Remove("k1");
        Assert.Equal(RecordCheck.KeyMissing, AuditVerifier.CheckRecord(rec, keys));
    }

    [Fact]
    public void An_unknown_format_version_is_never_reported_as_valid()
    {
        var keys = new InMemoryAuditKeyRing("k1");
        var rec = AuditCanonical.Seal(Sample(), keys.GetActive());
        Assert.Equal(RecordCheck.UnsupportedFormat, AuditVerifier.CheckRecord(rec with { FormatVersion = 2 }, keys));
    }
}

public class AuditStreamTests
{
    [Fact]
    public void Streams_parse_and_have_distinct_stable_keys()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.True(AuditStream.TryParse("system", out var s)); Assert.Equal("system", s.Key); Assert.Null(s.CompanyId);
        Assert.True(AuditStream.TryParse(id.ToString(), out var c)); Assert.Equal("company:11111111222233334444555555555555", c.Key);
        Assert.True(AuditStream.TryParse("company:" + id.ToString("N"), out var c2)); Assert.Equal(c, c2);
        Assert.False(AuditStream.TryParse("company:nope", out _));
        Assert.False(AuditStream.TryParse(Guid.Empty.ToString(), out _));
        Assert.False(AuditStream.TryParse(null, out _));
        Assert.Throws<ArgumentException>(() => AuditStream.ForCompany(Guid.Empty));
    }
}

public class AuditKeyTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "hasm-audit-test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void Event_and_checkpoint_keys_are_domain_separated_and_deterministic_per_key_id()
    {
        var master = RandomNumberGenerator.GetBytes(32);
        var a = new AuditKeyHandle("k1", master);
        var again = new AuditKeyHandle("k1", master);
        var other = new AuditKeyHandle("k2", master);
        var data = "data"u8.ToArray();
        Assert.Equal(a.EventMac(data), again.EventMac(data));
        Assert.NotEqual(a.EventMac(data), a.CheckpointMac(data));      // an event code can never double as a checkpoint code
        Assert.NotEqual(a.EventMac(data), other.EventMac(data));       // sub-keys are bound to the key id
        Assert.DoesNotContain(Convert.ToHexString(master), a.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("k1", true)] [InlineData("2026-11-primary", true)] [InlineData("a", true)]
    [InlineData("", false)] [InlineData("K1", false)] [InlineData("-x", false)] [InlineData("../evil", false)]
    [InlineData("has space", false)] [InlineData("this-identifier-is-definitely-much-too-long", false)]
    public void Key_identifiers_are_restricted_to_safe_characters(string id, bool valid) => Assert.Equal(valid, AuditKeyRing.IsValidKeyId(id));

    [Fact]
    public void Short_keys_are_rejected() => Assert.Throws<AuditConfigurationException>(() => new AuditKeyHandle("k1", new byte[31]));

    [Fact]
    public void A_generated_key_file_is_loaded_never_overwritten_and_not_world_readable()
    {
        FileAuditKeyRing.Generate(dir, "k1");
        var options = new AuditOptions { KeyDirectory = dir, ActiveKeyId = "k1", CheckpointDirectory = dir };
        var ring = new FileAuditKeyRing(options);
        var key = ring.GetActive();
        Assert.Equal("k1", key.Id);
        Assert.Same(key, ring.Find("k1"));

        var before = File.ReadAllText(Path.Combine(dir, "k1.hkey"));
        Assert.ThrowsAny<IOException>(() => FileAuditKeyRing.Generate(dir, "k1"));         // never overwrites an existing key
        Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "k1.hkey")));
        Assert.True(Convert.FromBase64String(before.Trim()).Length >= 32);

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "k1.hkey")));
    }

    [Fact]
    public void A_missing_or_malformed_key_fails_closed_without_leaking_paths_or_contents()
    {
        Directory.CreateDirectory(dir);
        var options = new AuditOptions { KeyDirectory = dir, ActiveKeyId = "k1", CheckpointDirectory = dir, AllowInsecureFilePermissions = true };
        var ring = new FileAuditKeyRing(options);
        var missing = Assert.Throws<AuditUnavailableException>(ring.GetActive);
        Assert.Equal("audit_key_unavailable", missing.Code);
        Assert.DoesNotContain(dir, missing.Message);

        File.WriteAllText(Path.Combine(dir, "k1.hkey"), "not-base64-!!!");
        Assert.Throws<AuditUnavailableException>(() => new FileAuditKeyRing(options).GetActive());
        File.WriteAllText(Path.Combine(dir, "k1.hkey"), Convert.ToBase64String(new byte[16]));   // too short
        Assert.Throws<AuditUnavailableException>(() => new FileAuditKeyRing(options).GetActive());
        Assert.Null(new FileAuditKeyRing(options).Find("../k1"));
    }

    [Fact]
    public void A_key_readable_by_other_accounts_is_refused()
    {
        FileAuditKeyRing.Generate(dir, "k1");
        var path = Path.Combine(dir, "k1.hkey");
        var options = new AuditOptions { KeyDirectory = dir, ActiveKeyId = "k1", CheckpointDirectory = dir };
        if (OperatingSystem.IsWindows()) GrantEveryoneRead(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Throws<AuditUnavailableException>(() => new FileAuditKeyRing(options).GetActive());
        Assert.NotNull(new FileAuditKeyRing(new AuditOptions { KeyDirectory = dir, ActiveKeyId = "k1", CheckpointDirectory = dir, AllowInsecureFilePermissions = true }).Find("k1"));   // explicit test-only opt-out
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void GrantEveryoneRead(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    public void Rotation_keeps_retired_keys_available_for_verification()
    {
        FileAuditKeyRing.Generate(dir, "k1");
        FileAuditKeyRing.Generate(dir, "k2");
        var v1 = new FileAuditKeyRing(new AuditOptions { KeyDirectory = dir, ActiveKeyId = "k1", CheckpointDirectory = dir });
        var v2 = new FileAuditKeyRing(new AuditOptions { KeyDirectory = dir, ActiveKeyId = "k2", CheckpointDirectory = dir });
        var data = "x"u8.ToArray();
        var oldMac = v1.GetActive().EventMac(data);
        Assert.Equal("k2", v2.GetActive().Id);
        Assert.True(v2.Find("k1")!.VerifyEventMac(data, oldMac));      // the new configuration still verifies old records
    }

    [Fact]
    public void Options_are_validated_by_setting_name_never_by_value()
    {
        var empty = AuditOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var ex = Assert.Throws<AuditConfigurationException>(() => empty.EnsureValid(true, true));
        Assert.Contains("Hasm:Audit:KeyDirectory", ex.Message);
        Assert.Contains("Hasm:Audit:ActiveKeyId", ex.Message);
        Assert.Contains("Hasm:Audit:CheckpointDirectory", ex.Message);

        var bad = AuditOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hasm:Audit:KeyDirectory"] = "/secret/path", ["Hasm:Audit:ActiveKeyId"] = "BAD KEY", ["Hasm:Audit:CheckpointDirectory"] = "/cp",
            ["Hasm:Audit:MaxUnanchoredEvents"] = "5000",
        }).Build());
        var bad2 = Assert.Throws<AuditConfigurationException>(() => bad.EnsureValid(true, true));
        Assert.DoesNotContain("/secret/path", bad2.Message);
        Assert.DoesNotContain("BAD KEY", bad2.Message);
        Assert.Contains("MaxUnanchoredEvents", bad2.Message);
        Assert.Equal(0, AuditOptions.FromConfiguration(new ConfigurationBuilder().Build()).MaxUnanchoredEvents);
    }
}

public class FileCheckpointStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "hasm-cp-test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
    private readonly AuditKeyHandle key = new("k1", RandomNumberGenerator.GetBytes(32));
    private static byte[] H(byte b) => Enumerable.Repeat(b, 32).ToArray();
    private const string Stream = "company:11111111222233334444555555555555";

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public async Task Checkpoints_are_appended_chained_and_read_back()
    {
        var store = new FileCheckpointStore(dir);
        Assert.Null(await store.ReadLastAsync(Stream));
        var first = await store.AppendAsync(Stream, 1, H(1), 100, key);
        var second = await store.AppendAsync(Stream, 2, H(2), 200, key);

        Assert.Equal(AuditCheckpoint.GenesisPrevMac(Stream), first.PrevMac);
        Assert.Equal(first.Mac, second.PrevMac);
        Assert.True(key.VerifyCheckpointMac(second.Canonical(), second.Mac));
        var all = await store.ReadAllAsync(Stream);
        Assert.Equal([1L, 2L], all.Checkpoints.Select(c => c.Seq));
        Assert.Equal(2, (await store.ReadLastAsync(Stream))!.Seq);
        Assert.Equal(0, all.UnreadableLines);
    }

    [Fact]
    public async Task File_names_are_opaque_and_streams_are_isolated()
    {
        var store = new FileCheckpointStore(dir);
        await store.AppendAsync(Stream, 1, H(1), 1, key);
        await store.AppendAsync("system", 1, H(9), 1, key);
        var names = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
        Assert.Equal(2, names.Count);
        // the whole company id, not a four-digit fragment: the opaque file names are hex hashes, in which "1111" occurs by chance now and then
        Assert.All(names, n => { Assert.DoesNotContain("11111111222233334444555555555555", n!); Assert.DoesNotContain("system", n!); Assert.EndsWith(".hcp", n!); });
        Assert.Single((await store.ReadAllAsync(Stream)).Checkpoints);
        Assert.Equal(H(9), (await store.ReadLastAsync("system"))!.Hash);
    }

    [Fact]
    public async Task Checkpoints_only_move_forward_and_a_fork_is_an_integrity_failure()
    {
        var store = new FileCheckpointStore(dir);
        await store.AppendAsync(Stream, 5, H(5), 1, key);

        await store.AppendAsync(Stream, 5, H(5), 2, key);                                                   // identical repeat: no-op
        Assert.Single((await store.ReadAllAsync(Stream)).Checkpoints);
        await Assert.ThrowsAsync<AuditIntegrityException>(() => store.AppendAsync(Stream, 5, H(6), 3, key)); // same seq, other hash
        await Assert.ThrowsAsync<AuditIntegrityException>(() => store.AppendAsync(Stream, 4, H(4), 3, key)); // rollback
        Assert.Single((await store.ReadAllAsync(Stream)).Checkpoints);
    }

    [Fact]
    public async Task A_torn_final_line_from_an_interrupted_write_does_not_corrupt_later_checkpoints()
    {
        var store = new FileCheckpointStore(dir);
        await store.AppendAsync(Stream, 1, H(1), 1, key);
        var file = Directory.GetFiles(dir).Single();
        await File.AppendAllTextAsync(file, "{\"v\":1,\"stream\":\"" + Stream + "\",\"seq\":2,\"ha");     // crash mid-line, no newline

        Assert.Equal(1, (await store.ReadLastAsync(Stream))!.Seq);                                         // torn line ignored
        var third = await store.AppendAsync(Stream, 3, H(3), 3, key);                                      // next append starts on a fresh line

        var all = await store.ReadAllAsync(Stream);
        Assert.Equal([1L, 3L], all.Checkpoints.Select(c => c.Seq));
        Assert.Equal(1, all.UnreadableLines);                                                              // surfaced to verification as incomplete evidence
        Assert.Equal(all.Checkpoints[0].Mac, third.PrevMac);
    }

    [Fact]
    public async Task Writers_sharing_one_directory_never_fork_the_checkpoint_chain()
    {
        // Separate store instances stand in for separate processes: only the file handle serializes them.
        var stores = Enumerable.Range(0, 4).Select(_ => new FileCheckpointStore(dir)).ToArray();
        var appended = 0;
        await Task.WhenAll(Enumerable.Range(1, 60).Select(async i =>
        {
            try { await stores[i % stores.Length].AppendAsync(Stream, i, H((byte)i), i, key); Interlocked.Increment(ref appended); }
            catch (AuditIntegrityException) { /* an older sequence lost the race to a newer one: correctly refused */ }
        }));

        var all = (await new FileCheckpointStore(dir).ReadAllAsync(Stream)).Checkpoints;
        Assert.Equal(appended, all.Count);
        Assert.Equal(all.Select(c => c.Seq).Order(), all.Select(c => c.Seq));                              // strictly forward
        Assert.Equal(all.Count, all.Select(c => c.Seq).Distinct().Count());
        Assert.Equal(AuditCheckpoint.GenesisPrevMac(Stream), all[0].PrevMac);
        for (var i = 1; i < all.Count; i++) Assert.Equal(all[i - 1].Mac, all[i].PrevMac);                  // one unbroken chain
        Assert.All(all, c => Assert.True(key.VerifyCheckpointMac(c.Canonical(), c.Mac)));
    }

    [Fact]
    public async Task An_unwritable_store_fails_with_a_safe_error()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "hasm-cp-blocker-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)));
        await File.WriteAllTextAsync(blocker, "a file where the directory should be");
        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => new FileCheckpointStore(Path.Combine(blocker, "cp")).AppendAsync(Stream, 1, H(1), 1, key));
            Assert.True(ex is AuditUnavailableException or IOException or UnauthorizedAccessException);
            if (ex is AuditUnavailableException a) { Assert.Equal("audit_checkpoint_unavailable", a.Code); Assert.DoesNotContain(blocker, a.Message); }
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public async Task A_checkpoint_directory_writable_by_others_is_refused_on_linux()
    {
        if (OperatingSystem.IsWindows()) return;     // Windows ACL rules are covered by the key-permission test
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => new FileCheckpointStore(dir).AppendAsync(Stream, 1, H(1), 1, key));
        Assert.Equal("audit_checkpoint_permissions", ex.Code);
    }

    [Fact]
    public void The_serialized_checkpoint_contains_no_key_material()
    {
        var text = Encoding.UTF8.GetString(AuditCanonical.SerializeCheckpoint(Stream, 1, H(1), 1, "k1", H(0)));
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }
}
