using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// Writes the SYSTEM-stream audit events of the backup commands (D-118, block 2010-2019: <see cref="BackupAuditEvents"/>). Until the Phase 4.1 administrative-tool login exists (D-099) the operator is the
/// operating-system account (interim mode, a dated limitation). The detail carries codes, key ids, company ids and target-database TAGS (a SHA-256 prefix) only; never a physical database name or a path.
/// </summary>
public static partial class BackupAudit
{
    [GeneratedRegex("[^A-Za-z0-9._-]", RegexOptions.CultureInvariant)] private static partial Regex Unsafe();

    /// <summary>The operator as recorded: <c>os:&lt;account&gt;</c> (only letters, digits, dot, underscore and hyphen; at most 64 characters).</summary>
    public static string Operator()
    {
        var name = Unsafe().Replace(Environment.UserName ?? "", "_");
        return "os:" + (name.Length == 0 ? "unknown" : name.Length > 64 ? name[..64] : name);
    }

    /// <summary>A tag of a physical database name: the first 12 hex characters of its SHA-256 (the name itself is never recorded).</summary>
    public static string DatabaseTag(string physicalName) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physicalName)))[..12];

    /// <summary>Appends one event to the system stream in its own transaction and commits it (anchoring the head). Throws when the audit trail is unavailable (the caller fails closed).</summary>
    public static async Task WriteAsync(string systemConnectionString, AuditTrail trail, short eventType, string detail, Guid? subjectId = null, CancellationToken ct = default)
    {
        if (eventType is < 2010 or > 2019) throw new ArgumentOutOfRangeException(nameof(eventType), "Backup audit events use the block 2010-2019.");
        await using var conn = new NpgsqlConnection(systemConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var receipt = await trail.AppendAsync(conn, tx, AuditStream.System, AuditCanonical.FromMicros(AuditCanonical.ToMicros(DateTimeOffset.UtcNow)),
            new AuditEventData(eventType, SubjectId: subjectId, Detail: detail), ct);
        await trail.CommitAsync(tx, receipt, ct);
    }
}
