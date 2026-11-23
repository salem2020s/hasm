using Hasm.Infrastructure.Security;
using Npgsql;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SI = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionIntegrity;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;

namespace Hasm.Infrastructure.Identity;

/// <summary>
/// A session row as the integrity tag sees it (identity integrity slice S-1, docs/IDENTITY-INTEGRITY-PLAN.md, owner decisions OD-1/OD-2). The tag covers the security fields: the id, the user, the keyed token hash,
/// the client kind, the creation and absolute expiry, the authentication methods and the second-factor proof time, the emergency flag, the selected company and the user's security revision at issue. The idle
/// expiry and the last-seen time are NOT covered (they move on every request and can only reach the covered absolute expiry), and neither is the revocation (OD-5: a single logout is not tag-protected; the
/// residual is documented and tested, a revoke-all and a password change are protected by the revision).
/// </summary>
internal sealed record SessionRow(
    Guid Id, Guid UserId, byte[] TokenHash, short ClientKind, DateTimeOffset CreatedAt, DateTimeOffset AbsoluteExpires, short AuthMethods, DateTimeOffset? VerifiedAt, bool Emergency,
    Guid? SelectedCompanyId, long? RevisionAtIssue, string? KeyId, byte[]? Tag)
{
    public const string Domain = "hasm.session.v1";

    public static string Columns(string alias) => string.Join(",", new[]
    {
        S.Id, S.UserId, S.TokenHash, S.ClientKind, S.CreatedAtUtc, S.AbsoluteExpiresAtUtc, SM.AuthMethods, SM.VerifiedAtUtc, SM.Emergency, S.SelectedCompanyId, SI.UserRevisionAtIssue, SI.KeyId, SI.Tag
    }.Select(c => alias + "." + c));

    public static SessionRow Read(NpgsqlDataReader r, int o = 0) => new(
        r.GetGuid(o), r.GetGuid(o + 1), (byte[])r.GetValue(o + 2), r.GetInt16(o + 3), r.GetFieldValue<DateTimeOffset>(o + 4), r.GetFieldValue<DateTimeOffset>(o + 5), r.GetInt16(o + 6),
        r.IsDBNull(o + 7) ? null : r.GetFieldValue<DateTimeOffset>(o + 7), r.GetBoolean(o + 8), r.IsDBNull(o + 9) ? null : r.GetGuid(o + 9),
        r.IsDBNull(o + 10) ? null : r.GetInt64(o + 10), r.IsDBNull(o + 11) ? null : r.GetString(o + 11), r.IsDBNull(o + 12) ? null : (byte[])r.GetValue(o + 12));

    public byte[] ComputeTag(IdentityKeys keys, KekHandle key) => keys.Mac(key, IdentityKeys.SessionPurpose, new MacInput(Domain)
        .Add(Id).Add(UserId).Add(TokenHash).Add((long)ClientKind).Add(CreatedAt).Add(AbsoluteExpires).Add((long)AuthMethods).Add(VerifiedAt).Add(Emergency)
        .Add(SelectedCompanyId).Add(RevisionAtIssue).Add(key.Id));

    /// <summary>True only for a row with a key id this installation accepts, a tag and a revision, whose tag verifies. A row without them is never honoured (no compatibility mode, OD-2).</summary>
    public bool IsValid(IdentityKeys keys)
    {
        if (KeyId is null || Tag is null || RevisionAtIssue is null || keys.FindAccepted(KeyId) is not { } key) return false;
        return IdentityKeys.Equal(ComputeTag(keys, key), Tag);
    }

    /// <summary>Locks and loads one session row (FOR UPDATE) inside the caller's transaction.</summary>
    public static async Task<SessionRow?> LoadForUpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid sessionId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns("s")} FROM {S.Table} s WHERE s.{S.Id} = @id AND s.{S.RevokedAtUtc} IS NULL FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("id", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>
    /// Writes the changed mutable security fields of <paramref name="before"/> as <paramref name="after"/> under a NEW tag by the active key, with compare-and-swap on the old tag (a concurrent writer makes it
    /// affect no row: false). The caller has verified <paramref name="before"/> and holds its row lock.
    /// </summary>
    public static async Task<bool> UpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys keys, SessionRow before, SessionRow after, CancellationToken ct)
    {
        var key = keys.Active();
        var next = after with { KeyId = key.Id, Tag = null };
        next = next with { Tag = next.ComputeTag(keys, key) };
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {S.Table} SET {SM.AuthMethods} = @am, {SM.VerifiedAtUtc} = @v, {S.SelectedCompanyId} = @c, {SI.KeyId} = @k, {SI.Tag} = @t WHERE {S.Id} = @id AND {SI.Tag} = @old AND {S.RevokedAtUtc} IS NULL", conn, tx);
        cmd.Parameters.AddWithValue("am", next.AuthMethods);
        cmd.Parameters.AddWithValue("v", (object?)next.VerifiedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("c", (object?)next.SelectedCompanyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("k", next.KeyId!);
        cmd.Parameters.AddWithValue("t", next.Tag!);
        cmd.Parameters.AddWithValue("id", before.Id);
        cmd.Parameters.AddWithValue("old", before.Tag!);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Sets (or clears) the selected company of one live, authentic session of the user by compare-and-swap; false when the session is not live, not the user's, not authentic or changed concurrently.</summary>
    public static async Task<bool> SelectCompanyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys keys, Guid sessionId, Guid userId, Guid? companyId, CancellationToken ct)
    {
        var before = await LoadForUpdateAsync(conn, tx, sessionId, ct);
        if (before is null || before.UserId != userId || !before.IsValid(keys)) return false;
        return await UpdateAsync(conn, tx, keys, before, before with { SelectedCompanyId = companyId }, ct);
    }

    /// <summary>
    /// Clears the selected company from every live session of the user that has it selected (the user's membership of that company ended). Each row is re-tagged by compare-and-swap; without keys, or when a row
    /// is not authentic, the session is revoked instead, never left with a stale or unverifiable selection.
    /// </summary>
    public static async Task ClearCompanyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys? keys, Guid userId, Guid companyId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = new List<SessionRow>();
        await using (var cmd = new NpgsqlCommand($"SELECT {Columns("s")} FROM {S.Table} s WHERE s.{S.UserId} = @u AND s.{S.SelectedCompanyId} = @c AND s.{S.RevokedAtUtc} IS NULL FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(Read(r));
        }
        foreach (var row in rows)
        {
            if (keys is not null && row.IsValid(keys) && await UpdateAsync(conn, tx, keys, row, row with { SelectedCompanyId = null }, ct)) continue;
            await using var rv = new NpgsqlCommand($"UPDATE {S.Table} SET {S.RevokedAtUtc} = @n, {S.RevokedReason} = @r WHERE {S.Id} = @id AND {S.RevokedAtUtc} IS NULL", conn, tx);
            rv.Parameters.AddWithValue("n", now);
            rv.Parameters.AddWithValue("r", (short)SessionRevocation.Administrative);
            rv.Parameters.AddWithValue("id", row.Id);
            await rv.ExecuteNonQueryAsync(ct);
        }
    }
}
