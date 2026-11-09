using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Hasm.Infrastructure.Audit;

/// <summary>
/// Deterministic, versioned binary serialization of an audit record, and the hash / authentication-code computation over it.
/// Format version 1 (never change a released version; add a new one and keep the old reader):
/// <code>
/// "HASM-AUDIT" | u16 version | str streamKey | i64 seq | 16B eventId | i64 occurredMicros | i16 eventType |
/// opt(guid companyId) | opt(guid actor) | opt(guid target) | opt(guid subject) | opt(i64 legacyRef) | opt(str detail) |
/// str keyId | 32B prevHash
/// </code>
/// All integers are big-endian; <c>str</c> is a u32 byte length plus UTF-8 bytes (no normalization); <c>opt</c> is a 0/1 flag
/// followed by the value when present. Guids are the RFC 4122 byte order (big-endian). The key identifier is inside the
/// authenticated bytes, so it cannot be swapped without detection.
/// <c>hash = SHA-256(canonical)</c> links the chain (the next record's prevHash); <c>mac = HMAC-SHA-256(eventKey, canonical)</c>
/// authenticates it, so someone who can write the database but does not hold the key cannot recompute a valid record.
/// </summary>
public static class AuditCanonical
{
    public const ushort CurrentVersion = 1;
    public const int HashLength = 32;
    private static readonly byte[] Magic = "HASM-AUDIT"u8.ToArray();
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    /// <summary>Timestamps are persisted and hashed at PostgreSQL's microsecond precision.</summary>
    public static long ToMicros(DateTimeOffset value) => (value.UtcTicks - Epoch.UtcTicks) / 10;
    public static DateTimeOffset FromMicros(long micros) => new(Epoch.UtcTicks + micros * 10, TimeSpan.Zero);

    public static bool IsSupported(int version) => version == CurrentVersion;

    /// <summary>Previous-hash value of the first record of a stream (domain-separated per stream).</summary>
    public static byte[] GenesisHash(string streamKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("HASM-AUDIT-GENESIS-v1\0" + streamKey));

    public static byte[] Serialize(AuditRecord r)
    {
        if (!IsSupported(r.FormatVersion)) throw new NotSupportedException("Unsupported audit format version.");
        if (r.PrevHash.Length != HashLength) throw new ArgumentException("Previous hash must be 32 bytes.", nameof(r));
        using var ms = new MemoryStream(256);
        ms.Write(Magic);
        WriteU16(ms, (ushort)r.FormatVersion);
        WriteStr(ms, r.StreamKey);
        WriteI64(ms, r.Seq);
        ms.Write(GuidBytes(r.EventId));
        WriteI64(ms, r.OccurredMicros);
        WriteU16(ms, unchecked((ushort)r.EventType));
        WriteOptGuid(ms, r.CompanyId);
        WriteOptGuid(ms, r.ActorUserId);
        WriteOptGuid(ms, r.TargetUserId);
        WriteOptGuid(ms, r.SubjectId);
        if (r.LegacyRef is { } legacy) { ms.WriteByte(1); WriteI64(ms, legacy); } else ms.WriteByte(0);
        if (r.Detail is { } detail) { ms.WriteByte(1); WriteStr(ms, detail); } else ms.WriteByte(0);
        WriteStr(ms, r.KeyId);
        ms.Write(r.PrevHash);
        return ms.ToArray();
    }

    public static byte[] ComputeHash(byte[] canonical) => SHA256.HashData(canonical);

    /// <summary>Fills in Hash and Mac for a record whose other fields are set.</summary>
    public static AuditRecord Seal(AuditRecord unsealed, AuditKeyHandle key)
    {
        var canonical = Serialize(unsealed with { Hash = [], Mac = [] });
        return unsealed with { Hash = ComputeHash(canonical), Mac = key.EventMac(canonical) };
    }

    private static byte[] GuidBytes(Guid g)
    {
        var b = new byte[16];
        g.TryWriteBytes(b, bigEndian: true, out _);
        return b;
    }

    private static void WriteOptGuid(Stream s, Guid? g)
    {
        if (g is { } v) { s.WriteByte(1); s.Write(GuidBytes(v)); } else s.WriteByte(0);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteI64(Stream s, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteStr(Stream s, string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
        s.Write(len);
        s.Write(bytes);
    }

    internal static byte[] SerializeCheckpoint(string streamKey, long seq, byte[] hash, long micros, string keyId, byte[] prevMac)
    {
        using var ms = new MemoryStream(160);
        ms.Write("HASM-AUDIT-CP"u8);
        WriteU16(ms, 1);
        WriteStr(ms, streamKey);
        WriteI64(ms, seq);
        ms.Write(hash);
        WriteI64(ms, micros);
        WriteStr(ms, keyId);
        ms.Write(prevMac);
        return ms.ToArray();
    }
}
