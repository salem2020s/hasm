using Hasm.Infrastructure.Security;
using Npgsql;
using A = Hasm.Infrastructure.SystemDatabase.SystemSchema.IdentityAnchor;

namespace Hasm.Infrastructure.Identity;

/// <summary>
/// The identity anchor (identity integrity S-2c, plan section 5, owner decision OD-4): an append-only history, one row per security change of an identity record (kind, subject, revision), each with a key id and a tag of
/// the identity-user key. A record is refused when its revision is BELOW the highest VALID anchor of its subject: an older, once-valid record written back cannot pass. Only anchors whose tag verifies under an accepted
/// key count; a junk anchor (a bad tag, an unknown key id) can neither raise the bound nor lock anyone out, it is ignored and reported (<see cref="Bound.Ignored"/>). The table cannot be updated or deleted (guard trigger,
/// and the runtime account holds SELECT and INSERT only), so the bound can only rise through valid anchors.
/// </summary>
internal static class IdentityAnchors
{
    public const short KindUser = 1;
    public const string Domain = "hasm.anchor.v1";
    /// <summary>At most this many anchors of one subject are examined per check (newest first); a flood of junk above the newest valid anchor is reported, never a reason to refuse.</summary>
    public const int ScanLimit = 5000;

    /// <summary>The highest valid anchor revision of the subject (null when there is none) and how many anchors above it were ignored.</summary>
    public readonly record struct Bound(long? MaxValidRevision, int Ignored, bool ScanLimitReached);

    public static byte[] Tag(IdentityKeys keys, KekHandle key, short kind, Guid subject, long revision) =>
        keys.Mac(key, IdentityKeys.UserPurpose, new MacInput(Domain).Add((long)kind).Add(subject).Add(revision).Add(key.Id));

    public static async Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys keys, short kind, Guid subject, long revision, CancellationToken ct)
    {
        var key = keys.Active();
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {A.Table} ({A.Id},{A.Kind},{A.SubjectId},{A.Revision},{A.KeyId},{A.Tag},{A.CreatedAtUtc}) VALUES (@i,@k,@s,@r,@kid,@t,now())", conn, tx);
        cmd.Parameters.AddWithValue("i", Guid.NewGuid());
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("s", subject);
        cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.AddWithValue("kid", key.Id);
        cmd.Parameters.AddWithValue("t", Tag(keys, key, kind, subject, revision));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<Bound> ReadBoundAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IdentityKeys keys, short kind, Guid subject, CancellationToken ct)
    {
        var ignored = 0; var seen = 0;
        await using var cmd = new NpgsqlCommand(
            $"SELECT {A.Revision},{A.KeyId},{A.Tag} FROM {A.Table} WHERE {A.Kind} = @k AND {A.SubjectId} = @s ORDER BY {A.Revision} DESC LIMIT {ScanLimit}", conn, tx);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("s", subject);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            seen++;
            var revision = r.GetInt64(0); var keyId = r.GetString(1); var tag = (byte[])r.GetValue(2);
            if (keys.FindAccepted(keyId) is { } key && IdentityKeys.Equal(Tag(keys, key, kind, subject, revision), tag)) return new Bound(revision, ignored, false);
            ignored++;
        }
        return new Bound(null, ignored, seen >= ScanLimit);
    }
}
