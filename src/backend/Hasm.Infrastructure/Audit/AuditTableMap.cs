using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using E = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Infrastructure.Audit;

/// <summary>
/// Where one kind of audit stream is stored: the physical table and columns. The cryptographic code (canonical format, hash chain,
/// HMAC, checkpoints, verification) is shared; only the storage location differs. The map is chosen from the stream (never supplied by
/// callers), and every name is a compile-time constant from the schema maps, never user input.
/// </summary>
internal sealed record AuditTableMap(
    string Table, string StreamKey, string Seq, string EventId, string CompanyId, string OccurredAtUtc, string EventType,
    string ActorUserId, string TargetUserId, string SubjectId, string? LegacyRef, string Detail, string FormatVersion,
    string KeyId, string PrevHash, string Hash, string Mac)
{
    /// <summary>The system database table holding the system stream and the per-company access streams.</summary>
    public static AuditTableMap System { get; } = new(E.Table, E.StreamKey, E.Seq, E.EventId, E.CompanyId, E.OccurredAtUtc, E.EventType,
        E.ActorUserId, E.TargetUserId, E.SubjectId, E.LegacyRef, E.Detail, E.FormatVersion, E.KeyId, E.PrevHash, E.Hash, E.Mac);

    /// <summary>The company database table holding that company's business stream (no legacy projection exists for it).</summary>
    public static AuditTableMap Business { get; } = new(B.Table, B.StreamKey, B.Seq, B.EventId, B.CompanyId, B.OccurredAtUtc, B.EventType,
        B.ActorUserId, B.TargetUserId, B.SubjectId, null, B.Detail, B.FormatVersion, B.KeyId, B.PrevHash, B.Hash, B.Mac);

    public static AuditTableMap For(AuditStream stream) => stream.IsBusiness ? Business : System;

    public bool HasLegacyRef => LegacyRef is not null;

    /// <summary>
    /// Columns in the fixed order expected by <see cref="AuditStorage.Read"/>. A table without a legacy reference yields a typed NULL
    /// in that position, so one reader serves both tables.
    /// </summary>
    public string SelectColumns =>
        $"{StreamKey}, {Seq}, {EventId}, {OccurredAtUtc}, {EventType}, {CompanyId}, {ActorUserId}, {TargetUserId}, " +
        $"{SubjectId}, {LegacyRef ?? "NULL::bigint"}, {Detail}, {FormatVersion}, {KeyId}, {PrevHash}, {Hash}, {Mac}";

    /// <summary>INSERT statement with the named parameters s,q,id,t,e,c,a,tu,su,[l],d,v,k,p,h,m.</summary>
    public string InsertSql => HasLegacyRef
        ? $"INSERT INTO {Table} ({SelectColumns}) VALUES (@s,@q,@id,@t,@e,@c,@a,@tu,@su,@l,@d,@v,@k,@p,@h,@m)"
        : $"INSERT INTO {Table} ({StreamKey}, {Seq}, {EventId}, {OccurredAtUtc}, {EventType}, {CompanyId}, {ActorUserId}, {TargetUserId}, " +
          $"{SubjectId}, {Detail}, {FormatVersion}, {KeyId}, {PrevHash}, {Hash}, {Mac}) VALUES (@s,@q,@id,@t,@e,@c,@a,@tu,@su,@d,@v,@k,@p,@h,@m)";
}
