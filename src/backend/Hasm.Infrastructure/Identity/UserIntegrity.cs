using Hasm.Infrastructure.Security;
using Npgsql;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using UI = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity;
using UT = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag;

namespace Hasm.Infrastructure.Identity;

/// <summary>
/// A user row as the integrity tag sees it (identity integrity slice S-2a, docs/IDENTITY-INTEGRITY-PLAN.md section 5, owner decisions OD-1 and OD-2). The tag covers exactly what decides who a person is and what the second
/// factor requires: id, login, password hash, lifecycle state, the identity administrator flag, the MFA state, the security revision and the three one-time enrolment-code columns (review H1 of the plan: a row whose code columns
/// a runtime-credential holder could rewrite would let him enrol his own authenticator), plus the key id. The display name and the time columns (created, last login, password changed, state changed) are not covered.
/// </summary>
internal sealed record UserRow(
    Guid Id, string Login, string PasswordHash, short State, bool IsAdministrator, short MfaState, long Revision,
    byte[]? CodeHash, DateTimeOffset? CodeExpiresAt, DateTimeOffset? CodeConsumedAt, string? KeyId, byte[]? Tag)
{
    public const string Domain = "hasm.user.v1";

    public static string Columns(string alias) => string.Join(",", new[]
    {
        U.Id, U.Login, U.PasswordHash, U.State, U.IsIdentityAdministrator, M.State, UI.SecurityRevision, M.EnrolmentCodeHash, M.EnrolmentCodeExpiresAtUtc, M.EnrolmentCodeConsumedAtUtc, UT.KeyId, UT.Tag
    }.Select(c => alias + "." + c));

    public static UserRow Read(NpgsqlDataReader r, int o = 0) => new(
        r.GetGuid(o), r.GetString(o + 1), r.GetString(o + 2), r.GetInt16(o + 3), r.GetBoolean(o + 4), r.GetInt16(o + 5), r.GetInt64(o + 6),
        r.IsDBNull(o + 7) ? null : (byte[])r.GetValue(o + 7), r.IsDBNull(o + 8) ? null : r.GetFieldValue<DateTimeOffset>(o + 8), r.IsDBNull(o + 9) ? null : r.GetFieldValue<DateTimeOffset>(o + 9),
        r.IsDBNull(o + 10) ? null : r.GetString(o + 10), r.IsDBNull(o + 11) ? null : (byte[])r.GetValue(o + 11));

    public byte[] ComputeTag(IdentityKeys keys, KekHandle key) => keys.Mac(key, IdentityKeys.UserPurpose, new MacInput(Domain)
        .Add(Id).Add(Login).Add(PasswordHash).Add((long)State).Add(IsAdministrator).Add((long)MfaState).Add(Revision)
        .Add(CodeHash).Add(CodeExpiresAt).Add(CodeConsumedAt).Add(key.Id));

    /// <summary>A digest of EXACTLY what the tag covers (the canonical input without the key), for the identity audit report: a change of any covered field changes it, and it is safe to hash into a report because it is not printed (review M1 of PR 239).</summary>
    public byte[] ReportDigest() => System.Security.Cryptography.SHA256.HashData(new MacInput("hasm.report.user.v1")
        .Add(Id).Add(Login).Add(PasswordHash).Add((long)State).Add(IsAdministrator).Add((long)MfaState).Add(Revision).Add(CodeHash).Add(CodeExpiresAt).Add(CodeConsumedAt).Add(KeyId).ToBytes());

    /// <summary>True only for a row with an accepted key id and a tag that verifies. A row without a tag (an untagged legacy user) is never honoured.</summary>
    public bool IsValid(IdentityKeys keys)
    {
        if (KeyId is null || Tag is null || keys.FindAccepted(KeyId) is not { } key) return false;
        return IdentityKeys.Equal(ComputeTag(keys, key), Tag);
    }

    /// <summary>The row of a NEW user (revision 0), tagged under the active key, ready to insert.</summary>
    public static UserRow ForNewUser(IdentityKeys keys, Guid id, string login, string passwordHash, bool administrator)
    {
        var key = keys.Active();
        var row = new UserRow(id, login, passwordHash, 0, administrator, 0, 0, null, null, null, key.Id, null);
        return row with { Tag = row.ComputeTag(keys, key) };
    }

    public static async Task<UserRow?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns("u")} FROM {U.Table} u WHERE u.{U.Id} = @id" + (forUpdate ? " FOR NO KEY UPDATE" : ""), conn, tx);
        cmd.Parameters.AddWithValue("id", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>
    /// THE way a user row's covered fields change: under the row lock, the row is verified (a row that does not verify is refused when <paramref name="requireValid"/>), the caller's statement runs
    /// (<paramref name="writer"/>, true when it changed the row), the security revision is raised by exactly one when <paramref name="bump"/>, and a verified row is re-tagged under the active key by
    /// compare-and-swap on the old tag. A row that did not verify and was changed anyway (only the security-reducing writers: deactivation, revoke-all, the restore fix-up) keeps its invalid tag, so
    /// it is never blessed. Returns false when the row is missing, refused, or the writer changed nothing.
    /// </summary>
    public static async Task<bool> ChangeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys keys, Guid userId, Func<Task<bool>> writer, bool bump, bool requireValid, CancellationToken ct)
    {
        var before = await LoadAsync(conn, tx, userId, forUpdate: true, ct);
        if (before is null) return false;
        var valid = before.IsValid(keys);
        if (requireValid && !valid) return false;
        if (!await writer()) return false;
        if (bump)
        {
            await using var up = new NpgsqlCommand($"UPDATE {U.Table} SET {UI.SecurityRevision} = {UI.SecurityRevision} + 1 WHERE {U.Id} = @id", conn, tx);
            up.Parameters.AddWithValue("id", userId);
            await up.ExecuteNonQueryAsync(ct);
        }
        if (!valid) return true;
        var after = (await LoadAsync(conn, tx, userId, forUpdate: false, ct))!;
        var key = keys.Active();
        after = after with { KeyId = key.Id, Tag = null };
        var tag = after.ComputeTag(keys, key);
        await using var set = new NpgsqlCommand($"UPDATE {U.Table} SET {UT.KeyId} = @k, {UT.Tag} = @t WHERE {U.Id} = @id AND {UT.Tag} = @old", conn, tx);
        set.Parameters.AddWithValue("k", key.Id);
        set.Parameters.AddWithValue("t", tag);
        set.Parameters.AddWithValue("id", userId);
        set.Parameters.AddWithValue("old", before.Tag!);
        if (await set.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("user_changed_concurrently");
        if (bump) await IdentityAnchors.WriteAsync(conn, tx, keys, IdentityAnchors.KindUser, userId, after.Revision, ct);   // one anchor per security change (OD-4)
        return true;
    }
}
