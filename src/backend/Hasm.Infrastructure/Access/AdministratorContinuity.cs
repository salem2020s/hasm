using Hasm.Infrastructure.Companies;
using Npgsql;
using A = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserCompanyAccess;
using R = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Infrastructure.Access;

/// <summary>
/// Shared definition of a "usable Company Administrator" and of the per-company lock that serializes every operation that
/// can remove one (membership revocation, assignment revocation in <see cref="AccessService"/>, and disabling/locking a user
/// in <c>IdentityStore</c>). A usable administrator is an Active user with an active membership of a Ready company holding
/// the active preset Company Administrator role company-wide.
/// </summary>
internal static class AdministratorContinuity
{
    /// <summary>Takes the per-company transaction-scoped advisory lock (released at commit/rollback).</summary>
    public static async Task LockCompanyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid companyId, CancellationToken ct)
    {
        await using var lck = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 4))", conn, tx);
        lck.Parameters.AddWithValue("k", "hasm.access." + companyId.ToString("N"));
        await lck.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// True when removing the given user (all their access) or the given assignment would leave the company with none while it
    /// currently has at least one. Must be called inside the company lock. A company that already has none is never blocked,
    /// so cleanup stays possible and the offline recovery command is unaffected.
    /// </summary>
    public static async Task<bool> WouldRemoveLastAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid companyId, Guid? excludeUser, Guid? excludeAssignment, CancellationToken ct)
    {
        var total = await CountAsync(conn, tx, companyId, null, null, ct);
        var remaining = await CountAsync(conn, tx, companyId, excludeUser, excludeAssignment, ct);
        return total > 0 && remaining == 0;
    }

    public static async Task<long> CountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid companyId, Guid? excludeUser, Guid? excludeAssignment, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(DISTINCT a.{A.UserId}) FROM {A.Table} a " +
            $"JOIN {M.Table} m ON m.{M.UserId} = a.{A.UserId} AND m.{M.CompanyId} = a.{A.CompanyId} AND m.{M.RevokedAtUtc} IS NULL " +
            $"JOIN {U.Table} u ON u.{U.Id} = a.{A.UserId} AND u.{U.State} = 0 " +
            $"JOIN {C.Table} c ON c.{C.Id} = a.{A.CompanyId} AND c.{C.ProvisioningState} = {(short)ProvisioningState.Ready} " +
            $"JOIN {R.Table} r ON r.{R.Id} = a.{A.RoleId} AND r.{R.IsActive} AND r.{R.PresetKey} = @key " +
            $"WHERE a.{A.CompanyId} = @c AND a.{A.RevokedAtUtc} IS NULL AND a.{A.ScopeKind} = 0 " +
            $"AND (@u::uuid IS NULL OR a.{A.UserId} <> @u) AND (@a::uuid IS NULL OR a.{A.Id} <> @a)", conn, tx);
        cmd.Parameters.AddWithValue("key", PresetRoles.CompanyAdministratorKey);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)excludeUser ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)excludeAssignment ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Companies in which the user currently counts as a usable administrator (their own state is not considered, so this is
    /// meaningful for a user about to be disabled). Sorted, so callers can lock them in a deterministic order.
    /// </summary>
    public static async Task<List<Guid>> CompaniesAdministeredAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT a.{A.CompanyId} FROM {A.Table} a " +
            $"JOIN {M.Table} m ON m.{M.UserId} = a.{A.UserId} AND m.{M.CompanyId} = a.{A.CompanyId} AND m.{M.RevokedAtUtc} IS NULL " +
            $"JOIN {C.Table} c ON c.{C.Id} = a.{A.CompanyId} AND c.{C.ProvisioningState} = {(short)ProvisioningState.Ready} " +
            $"JOIN {R.Table} r ON r.{R.Id} = a.{A.RoleId} AND r.{R.IsActive} AND r.{R.PresetKey} = @key " +
            $"WHERE a.{A.UserId} = @u AND a.{A.RevokedAtUtc} IS NULL AND a.{A.ScopeKind} = 0 ORDER BY 1", conn, tx);
        cmd.Parameters.AddWithValue("key", PresetRoles.CompanyAdministratorKey);
        cmd.Parameters.AddWithValue("u", userId);
        var list = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }
}
