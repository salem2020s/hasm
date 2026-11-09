namespace Hasm.Infrastructure.Audit;

/// <summary>
/// An independent, append-only, hash-chained audit stream: one system-wide stream (identity and other cross-company events)
/// and one stream per company. Streams never share a chain, a sequence or a lock, so company data cannot leak across streams.
/// </summary>
public readonly record struct AuditStream
{
    public const string SystemKey = "system";
    private const string CompanyPrefix = "company:";

    public string Key { get; }
    public Guid? CompanyId { get; }

    private AuditStream(string key, Guid? companyId) { Key = key; CompanyId = companyId; }

    public static AuditStream System => new(SystemKey, null);

    public static AuditStream ForCompany(Guid companyId) =>
        companyId == Guid.Empty ? throw new ArgumentException("A company stream needs a company id.", nameof(companyId))
                                : new(CompanyPrefix + companyId.ToString("N"), companyId);

    /// <summary>Parses "system", "company:&lt;id&gt;" or a bare company id.</summary>
    public static bool TryParse(string? text, out AuditStream stream)
    {
        stream = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Equals(SystemKey, StringComparison.OrdinalIgnoreCase)) { stream = System; return true; }
        if (text.StartsWith(CompanyPrefix, StringComparison.OrdinalIgnoreCase)) text = text[CompanyPrefix.Length..];
        if (Guid.TryParse(text, out var id) && id != Guid.Empty) { stream = ForCompany(id); return true; }
        return false;
    }

    public override string ToString() => Key;
}

/// <summary>
/// Persisted event type codes (never renumber). Ranges are reserved so that later phases can register their own
/// events without touching the integrity mechanism: 1-99 company access (Phase 2.3B), 100-199 identity (system stream),
/// 1000+ reserved for future accounting, inventory, approval and privileged administration events.
/// </summary>
public static class AuditEventTypes
{
    public const short MembershipGranted = 1, MembershipRevoked = 2, RoleCreated = 3, RolePermissionsChanged = 4,
        RoleDeactivated = 5, RoleAssigned = 6, RoleAssignmentRevoked = 7;
    public const short UserCreated = 100, InitialAdministratorCreated = 101, UserStateChanged = 102, PasswordChanged = 103;

    public const short FutureRangeStart = 1000;
    public const short Max = short.MaxValue;
}

/// <summary>What a caller supplies for one audit event. Identity, ordering, time, hashes and the MAC are assigned by the trail.</summary>
public sealed record AuditEventData(short EventType, Guid? ActorUserId = null, Guid? TargetUserId = null, Guid? SubjectId = null,
    string? Detail = null, long? LegacyRef = null)
{
    public const int MaxDetailLength = 1000;
}

/// <summary>An event as stored (the integrity fields included). Never exposes key material.</summary>
public sealed record AuditRecord(
    string StreamKey, long Seq, Guid EventId, long OccurredMicros, short EventType, Guid? CompanyId, Guid? ActorUserId,
    Guid? TargetUserId, Guid? SubjectId, long? LegacyRef, string? Detail, short FormatVersion, string KeyId,
    byte[] PrevHash, byte[] Hash, byte[] Mac)
{
    public DateTimeOffset OccurredAtUtc => AuditCanonical.FromMicros(OccurredMicros);
}

/// <summary>Proof that an event was written inside a transaction; used to anchor it in the external checkpoint store after commit.</summary>
public sealed record AuditReceipt(string StreamKey, long Seq, byte[] Hash, long OccurredMicros);

/// <summary>
/// The audit trail cannot be written safely (keys, checkpoint store or configuration unavailable). Callers abort the protected
/// mutation: an audited mutation never succeeds without its audit event. The message never contains keys, paths or secrets.
/// </summary>
public class AuditUnavailableException(string code, string message) : Exception(message)
{
    /// <summary>Short machine-readable reason, safe to return to clients.</summary>
    public string Code { get; } = code;
}

/// <summary>The stored audit evidence is inconsistent with the protected checkpoints or keys; the chain is never reset or extended silently.</summary>
public sealed class AuditIntegrityException(string code, string message) : AuditUnavailableException(code, message);
