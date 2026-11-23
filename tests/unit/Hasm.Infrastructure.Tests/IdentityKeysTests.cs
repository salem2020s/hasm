using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Identity integrity slice S-1: the four purpose-bound subkeys, the keyed token hash, the canonical MAC encoding and the session tag. The golden values were computed with an INDEPENDENT implementation
/// (HKDF-SHA-256 and HMAC-SHA-256 written separately in Python from the documented construction), so a change of the construction fails here instead of silently invalidating every stored tag.
/// </summary>
public class IdentityKeysTests
{
    private static readonly byte[] Master = Enumerable.Repeat((byte)1, 32).ToArray();
    private static IdentityKeys Keys(InMemoryKekRing? ring = null, params string[] previous) => new(ring ?? new InMemoryKekRing("k1", Master), previous);

    [Fact]
    public void The_token_hash_is_the_documented_keyed_construction()
    {
        var keys = Keys();
        Assert.Equal("de60d7aa9e1a6986e4146d92b84a1a95f74d66144648e94a5ef25a95fb7017b3", Convert.ToHexStringLower(keys.TokenHash(keys.Active(), "abc")));
        Assert.NotEqual(SessionToken.Hash("abc"), keys.TokenHash(keys.Active(), "abc"));                               // never the plain SHA-256 of the old design
    }

    [Fact]
    public void The_mac_encoding_is_canonical_and_the_golden_values_hold()
    {
        var keys = Keys();
        var guid = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var mac = keys.Mac(keys.Active(), IdentityKeys.SessionPurpose, new MacInput("d").Add(guid).Add("x").Add(7L).Add(true).Add((string?)null));
        Assert.Equal("44beddad251e3415fbddcb74bf0cdf10ff668f0922eeb5dac5a3e61a6a59d2ff", Convert.ToHexStringLower(mac));
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);                             // sub-microsecond part is dropped: PostgreSQL stores microseconds
        Assert.Equal("6d71fb784876e5702fd46e9d8c602fb8904ab7f851f3de99e2d64fc37402beda", Convert.ToHexStringLower(keys.Mac(keys.Active(), IdentityKeys.UserPurpose, new MacInput("d").Add(at))));
        Assert.Equal(Convert.ToHexStringLower(keys.Mac(keys.Active(), IdentityKeys.UserPurpose, new MacInput("d").Add(at))),
            Convert.ToHexStringLower(keys.Mac(keys.Active(), IdentityKeys.UserPurpose, new MacInput("d").Add(at.ToOffset(TimeSpan.FromHours(3))))));   // the instant, not the offset
    }

    [Fact]
    public void Different_field_lists_never_encode_alike_and_null_is_not_empty_and_the_domain_is_part_of_the_input()
    {
        var keys = Keys(); var key = keys.Active();
        byte[] M(Func<MacInput, MacInput> f) => keys.Mac(key, IdentityKeys.SessionPurpose, f(new MacInput("d")));
        Assert.NotEqual(M(i => i.Add("ab").Add("c")), M(i => i.Add("a").Add("bc")));
        Assert.NotEqual(M(i => i.Add((string?)null)), M(i => i.Add("")));
        Assert.NotEqual(M(i => i.Add(1L)), M(i => i.Add(true)));
        Assert.NotEqual(M(i => i.Add(new byte[] { 1 })), M(i => i.Add("\u0001")));
        Assert.NotEqual(M(i => i.Add(Guid.Empty)), M(i => i.Add((Guid?)null)));
        Assert.NotEqual(M(i => i.Add((DateTimeOffset?)null)), M(i => i.Add(DateTimeOffset.UnixEpoch)));
        Assert.NotEqual(keys.Mac(key, IdentityKeys.SessionPurpose, new MacInput("d1").Add(1L)), keys.Mac(key, IdentityKeys.SessionPurpose, new MacInput("d2").Add(1L)));
    }

    [Fact]
    public void The_four_purposes_derive_four_unrelated_keys_and_a_key_id_is_part_of_the_derivation()
    {
        var keys = Keys(); var key = keys.Active();
        var input = () => new MacInput("d").Add(1L);
        var macs = new[] { IdentityKeys.TokenPurpose, IdentityKeys.SessionPurpose, IdentityKeys.UserPurpose, IdentityKeys.AccessPurpose }.Select(p => Convert.ToHexString(keys.Mac(key, p, input()))).ToList();
        Assert.Equal(4, macs.Distinct().Count());
        var other = new InMemoryKekRing("k2", Master);                                                                // same master, other key id: another key
        Assert.NotEqual(macs[1], Convert.ToHexString(Keys(other).Mac(other.GetActive(), IdentityKeys.SessionPurpose, input())));
        var otherMaster = new InMemoryKekRing("k1", Enumerable.Repeat((byte)2, 32).ToArray());
        Assert.NotEqual(macs[1], Convert.ToHexString(Keys(otherMaster).Mac(otherMaster.GetActive(), IdentityKeys.SessionPurpose, input())));   // another master
    }

    [Fact]
    public void Accepted_keys_are_the_active_key_and_only_the_listed_previous_keys_the_ring_can_still_produce()
    {
        var ring = new InMemoryKekRing("k1", Master);
        ring.Add("k2", Enumerable.Repeat((byte)2, 32).ToArray()); ring.Add("k3", Enumerable.Repeat((byte)3, 32).ToArray());
        ring.Activate("k3");
        Assert.Equal(["k3"], Keys(ring).Accepted().Select(k => k.Id));
        Assert.Equal(["k3", "k1"], Keys(ring, "k1", "unknown").Accepted().Select(k => k.Id));
        Assert.Equal(["k3"], Keys(ring, "k3").Accepted().Select(k => k.Id));
        Assert.Null(Keys(ring, "k1").FindAccepted("k2"));                                                              // never configured: not honoured
        Assert.Null(Keys(ring, "k1").FindAccepted(null));
        ring.Remove("k1");
        Assert.Equal(["k3"], Keys(ring, "k1").Accepted().Select(k => k.Id));                                           // listed but gone from the ring: not honoured
        ring.Unavailable = true;
        Assert.Throws<EncryptionUnavailableException>(() => Keys(ring).Accepted());                                    // fail closed
        Assert.Throws<EncryptionUnavailableException>(() => Keys(ring).Active());
    }

    // ---- the session tag ----

    private static SessionRow Row() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), new byte[32], 0,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), 1, null, false, null, 0, "k1", null);

    [Fact]
    public void Every_covered_field_of_a_session_changes_the_tag_and_a_tag_verifies_only_under_its_own_key_and_row()
    {
        var keys = Keys(); var key = keys.Active();
        var row = Row(); var tag = row.ComputeTag(keys, key);
        var mutations = new Dictionary<string, SessionRow>
        {
            ["id"] = row with { Id = Guid.NewGuid() }, ["user"] = row with { UserId = Guid.NewGuid() }, ["token hash"] = row with { TokenHash = [.. new byte[31], 1] },
            ["client"] = row with { ClientKind = 1 }, ["created"] = row with { CreatedAt = row.CreatedAt.AddSeconds(1) }, ["absolute expiry"] = row with { AbsoluteExpires = row.AbsoluteExpires.AddSeconds(1) },
            ["auth methods"] = row with { AuthMethods = 3 }, ["verified at"] = row with { VerifiedAt = row.CreatedAt }, ["emergency"] = row with { Emergency = true },
            ["company"] = row with { SelectedCompanyId = Guid.NewGuid() }, ["revision"] = row with { RevisionAtIssue = 1 },
        };
        foreach (var (name, changed) in mutations) Assert.False(IdentityKeys.Equal(tag, changed.ComputeTag(keys, key)), name);
        Assert.True((row with { Tag = tag }).IsValid(keys));
        Assert.False((row with { Tag = tag, KeyId = "k2" }).IsValid(keys));                                            // names a key that is not accepted
        Assert.False((row with { Tag = tag, VerifiedAt = row.CreatedAt }).IsValid(keys));
        Assert.False((row with { Tag = null }).IsValid(keys));
        Assert.False((row with { Tag = tag, RevisionAtIssue = null }).IsValid(keys));
        Assert.False((row with { Tag = tag, KeyId = null }).IsValid(keys));
        Assert.False((row with { Tag = tag }).IsValid(Keys(new InMemoryKekRing("k1", Enumerable.Repeat((byte)9, 32).ToArray()))));   // another installation's key
        Assert.True((row with { Tag = tag, AbsoluteExpires = row.AbsoluteExpires.AddTicks(3) }).IsValid(keys));       // sub-microsecond noise is not a change (PostgreSQL stores microseconds)
    }
}
