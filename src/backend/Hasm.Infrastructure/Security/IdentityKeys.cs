using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Hasm.Infrastructure.Security;

/// <summary>
/// The four purpose-bound identity keys of the identity integrity plan (docs/IDENTITY-INTEGRITY-PLAN.md, owner decision OD-1): they are derived from the EXISTING protected key ring with the same
/// construction as the TOTP key (<see cref="KekHandle.DeriveSubkey"/>: HKDF-SHA-256 over the key's wrapping key, label <c>hasm-purpose/v1/&lt;purpose&gt;/&lt;keyId&gt;</c>), so a leak or a bug in one
/// use never gives another. The raw keys never reach PostgreSQL; only key ids and tags do. A missing or unusable key fails closed (<see cref="EncryptionUnavailableException"/>, which the API answers
/// as service unavailable), and no key is ever generated implicitly. New values are written under the ACTIVE key; during a rotation a value is also accepted under the key ids listed in
/// <paramref name="previousKeyIds"/> (the controlled grace period of OD-6), and under no other.
/// </summary>
public sealed class IdentityKeys(KekRing ring, IReadOnlyList<string>? previousKeyIds = null)
{
    public const string TokenPurpose = "identity-token", SessionPurpose = "identity-session", UserPurpose = "identity-user", AccessPurpose = "identity-access";

    private readonly ConcurrentDictionary<(string Purpose, string KeyId), byte[]> subkeys = new();

    /// <summary>The key new values are written under (throws when unavailable: fail closed).</summary>
    public KekHandle Active() => ring.GetActive();

    /// <summary>The keys a stored value may have been written under and still be accepted: the active key first, then the configured previous keys that the ring can still produce. Throws when there is no active key.</summary>
    public IReadOnlyList<KekHandle> Accepted()
    {
        var active = ring.GetActive();
        var list = new List<KekHandle> { active };
        foreach (var id in previousKeyIds ?? [])
            if (id != active.Id && ring.Find(id) is { } k && list.TrueForAll(x => x.Id != k.Id)) list.Add(k);
        return list;
    }

    /// <summary>The accepted key with this id, or null when the id is not accepted (retired, unknown or never configured): a value under it is not honoured.</summary>
    public KekHandle? FindAccepted(string? keyId) => keyId is null ? null : Accepted().FirstOrDefault(k => k.Id == keyId);

    private byte[] Subkey(KekHandle key, string purpose) => subkeys.GetOrAdd((purpose, key.Id), _ => key.DeriveSubkey(purpose));

    /// <summary>HMAC-SHA-256 of a session token under the token key (the keyed replacement of the plain SHA-256 of the first design). The token never leaves this call.</summary>
    public byte[] TokenHash(KekHandle key, string token) => HMACSHA256.HashData(Subkey(key, TokenPurpose), Encoding.ASCII.GetBytes(token));

    /// <summary>One MAC over the length-prefixed fields of <paramref name="input"/> under the subkey of <paramref name="purpose"/>; the domain label is part of the input, so a tag of one record kind never verifies as another.</summary>
    public byte[] Mac(KekHandle key, string purpose, MacInput input) => HMACSHA256.HashData(Subkey(key, purpose), input.ToBytes());

    /// <summary>Constant-time comparison of two tags.</summary>
    public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

/// <summary>
/// The canonical, unambiguous byte encoding of the fields a MAC covers: the domain label first, then every field with a one-byte type tag and a four-byte big-endian length (a null is its own tag),
/// so two different field lists can never produce the same bytes. Times are encoded as UTC ticks truncated to the microsecond PostgreSQL stores, so a value that went through the database encodes
/// exactly as the value that was written.
/// </summary>
public sealed class MacInput
{
    private readonly MemoryStream bytes = new();

    public MacInput(string domain) => Write(1, Encoding.UTF8.GetBytes(domain));

    private void Write(byte tag, ReadOnlySpan<byte> value)
    {
        bytes.WriteByte(tag);
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, value.Length);
        bytes.Write(len);
        bytes.Write(value);
    }

    public MacInput Add(Guid value) => Do(() => Write(2, value.ToByteArray(bigEndian: true)));
    public MacInput Add(Guid? value) => value is { } g ? Add(g) : Null();
    public MacInput Add(string? value) => value is null ? Null() : Do(() => Write(3, Encoding.UTF8.GetBytes(value)));
    public MacInput Add(long value) => Do(() => { Span<byte> b = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(b, value); Write(4, b); });
    public MacInput Add(long? value) => value is { } v ? Add(v) : Null();
    public MacInput Add(bool value) => Do(() => Write(5, [value ? (byte)1 : (byte)0]));
    public MacInput Add(byte[]? value) => value is null ? Null() : Do(() => Write(6, value));
    public MacInput Add(DateTimeOffset value) => Do(() => Write(7, BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(Microseconds(value)))));
    public MacInput Add(DateTimeOffset? value) => value is { } d ? Add(d) : Null();

    private MacInput Null() => Do(() => Write(0, []));
    private MacInput Do(Action write) { write(); return this; }

    /// <summary>UTC ticks truncated to the microsecond (what PostgreSQL timestamptz stores).</summary>
    public static long Microseconds(DateTimeOffset value) { var t = value.UtcTicks; return t - t % 10; }

    /// <summary>The same instant with the sub-microsecond part removed: the value a caller must WRITE so the stored value encodes like the one the tag covered.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    internal byte[] ToBytes() => bytes.ToArray();
}
