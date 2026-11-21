using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Npgsql;
using RA = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using RL = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using P = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaPolicy;
using PR = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaPolicyRole;

namespace Hasm.Infrastructure.Identity;

/// <summary>The company MFA policy kinds (Phase 4.1 section 3, D-080 (1)). Persisted numbers; never renumber. A company with no stored policy is Off.</summary>
public enum CompanyMfaPolicyKind : short { Off = 0, RequiredForRoles = 1, RequiredForAll = 2 }

public sealed record CompanyMfaPolicy(Guid CompanyId, CompanyMfaPolicyKind Kind, IReadOnlyList<Guid> RoleIds, DateTimeOffset? UpdatedAtUtc);

/// <summary>The one question the request pipeline asks: does the selected company's policy require MFA of this user? (an interface so the authorization handler is testable without a database)</summary>
public interface ICompanyMfaPolicyEvaluator
{
    Task<bool> RequiresMfaAsync(Guid userId, Guid companyId, CancellationToken ct = default);
}

/// <summary>
/// The company MFA policy (Phase 4.1 S3b-1b, D-167): read and change it, and evaluate it. Reading needs <c>access.mfapolicy.view</c>, changing it needs <c>access.mfapolicy.update</c> in that company
/// (the HTTP layer additionally requires a FRESH second factor). The effective requirement of a session in company C is the central requirement OR this policy (the central requirement is reported, not
/// enforced for a user without MFA until S4, D-165 (c)). A change that would require MFA of the administrator who makes it, while that administrator has no authenticator, is REFUSED so a company can never lock
/// its own administrators out by a policy change. Every change is audited in the system stream in the same transaction.
/// </summary>
public sealed class MfaPolicyService(Func<string> connectionStringFactory, TimeProvider clock, AuditTrail audit, AccessService access, IdentityStore identity) : ICompanyMfaPolicyEvaluator, IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> dataSource = new(() => NpgsqlDataSource.Create(connectionStringFactory()));
    public ValueTask DisposeAsync() => dataSource.IsValueCreated ? dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;

    public const int MaxRoles = 100;

    public async Task<AccessResult<CompanyMfaPolicy>> GetAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(actorUserId, companyId, PermissionCatalog.Codes.MfaPolicyView, ScopeTarget.Company, ct)) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Forbidden, "forbidden");
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        return AccessResult<CompanyMfaPolicy>.Ok(await ReadAsync(conn, null, companyId, ct));
    }

    private static async Task<CompanyMfaPolicy> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid companyId, CancellationToken ct)
    {
        var kind = CompanyMfaPolicyKind.Off;
        DateTimeOffset? at = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {P.Policy}, {P.UpdatedAtUtc} FROM {P.Table} WHERE {P.CompanyId} = @c", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { kind = (CompanyMfaPolicyKind)r.GetInt16(0); at = r.GetFieldValue<DateTimeOffset>(1); }
        }
        var roles = new List<Guid>();
        await using (var cmd = new NpgsqlCommand($"SELECT {PR.RoleId} FROM {PR.Table} WHERE {PR.CompanyId} = @c ORDER BY {PR.RoleId}", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) roles.Add(r.GetGuid(0));
        }
        return new CompanyMfaPolicy(companyId, kind, roles, at);
    }

    /// <summary>Does the policy of <paramref name="companyId"/> require MFA of <paramref name="userId"/>? Off: no. RequiredForAll: yes. RequiredForRoles: yes when the user holds an active assignment of a listed role in that company.</summary>
    public async Task<bool> RequiresMfaAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        return await RequiresAsync(conn, null, userId, companyId, ct);
    }

    private static async Task<bool> RequiresAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid userId, Guid companyId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT p.{P.Policy} = 2 OR (p.{P.Policy} = 1 AND EXISTS (SELECT 1 FROM {PR.Table} pr JOIN {RA.Table} a ON a.{RA.RoleId} = pr.{PR.RoleId} AND a.{RA.CompanyId} = pr.{PR.CompanyId} " +
            $"WHERE pr.{PR.CompanyId} = p.{P.CompanyId} AND a.{RA.UserId} = @u AND a.{RA.RevokedAtUtc} IS NULL)) FROM {P.Table} p WHERE p.{P.CompanyId} = @c", conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("c", companyId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Replaces the policy of the company. <paramref name="roleIds"/> must be empty unless the kind is RequiredForRoles, where it must list 1..<see cref="MaxRoles"/> distinct ACTIVE roles that are presets or belong to this company.</summary>
    public async Task<AccessResult<CompanyMfaPolicy>> SetAsync(Guid actorUserId, Guid companyId, CompanyMfaPolicyKind kind, IReadOnlyList<Guid>? roleIds, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(actorUserId, companyId, PermissionCatalog.Codes.MfaPolicyUpdate, ScopeTarget.Company, ct)) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Forbidden, "forbidden");
        if (!Enum.IsDefined(kind)) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Invalid, "invalid_request");
        var roles = (roleIds ?? []).Distinct().ToList();
        if (kind == CompanyMfaPolicyKind.RequiredForRoles ? roles.Count is 0 or > MaxRoles : roles.Count != 0) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Invalid, "invalid_request");

        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var role in roles)
        {
            await using var cmd = new NpgsqlCommand($"SELECT 1 FROM {RL.Table} WHERE {RL.Id} = @r AND {RL.IsActive} AND ({RL.CompanyId} IS NULL OR {RL.CompanyId} = @c)", conn, tx);
            cmd.Parameters.AddWithValue("r", role);
            cmd.Parameters.AddWithValue("c", companyId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Invalid, "unknown_role");
        }

        // the lock-out guard: a change that would require MFA of the person making it, while they have no authenticator, is refused (the same rule the policy later applies to everyone)
        var actorState = await identity.GetMfaStateAsync(actorUserId, ct);
        if (actorState == MfaState.None && kind != CompanyMfaPolicyKind.Off)
        {
            var actorWouldBeRequired = kind == CompanyMfaPolicyKind.RequiredForAll;
            if (!actorWouldBeRequired)
            {
                await using var cmd = new NpgsqlCommand($"SELECT 1 FROM {RA.Table} WHERE {RA.UserId} = @u AND {RA.CompanyId} = @c AND {RA.RevokedAtUtc} IS NULL AND {RA.RoleId} = ANY(@roles)", conn, tx);
                cmd.Parameters.AddWithValue("u", actorUserId);
                cmd.Parameters.AddWithValue("c", companyId);
                cmd.Parameters.AddWithValue("roles", roles.ToArray());
                actorWouldBeRequired = await cmd.ExecuteScalarAsync(ct) is not null;
            }
            if (actorWouldBeRequired) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Conflict, "mfa_policy_requires_own_enrolment");
        }

        var before = await ReadAsync(conn, tx, companyId, ct);
        var now = clock.GetUtcNow();
        await using (var cmd = new NpgsqlCommand(
            $"INSERT INTO {P.Table} ({P.CompanyId},{P.Policy},{P.UpdatedAtUtc},{P.UpdatedBy}) VALUES (@c,@p,@n,@u) ON CONFLICT ({P.CompanyId}) DO UPDATE SET {P.Policy} = EXCLUDED.{P.Policy}, {P.UpdatedAtUtc} = EXCLUDED.{P.UpdatedAtUtc}, {P.UpdatedBy} = EXCLUDED.{P.UpdatedBy}", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("p", (short)kind);
            cmd.Parameters.AddWithValue("n", now);
            cmd.Parameters.AddWithValue("u", actorUserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand($"DELETE FROM {PR.Table} WHERE {PR.CompanyId} = @c", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", companyId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var role in roles)
        {
            await using var cmd = new NpgsqlCommand($"INSERT INTO {PR.Table} ({PR.CompanyId},{PR.RoleId}) VALUES (@c,@r)", conn, tx);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("r", role);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var receipt = await audit.AppendAsync(conn, tx, AuditStream.System, AuditCanonical.FromMicros(AuditCanonical.ToMicros(now)),
            new AuditEventData(MfaAuditEvents.PolicyChanged, actorUserId, null, null, $"company {companyId:N} mfa policy {before.Kind} -> {kind} roles {before.RoleIds.Count} -> {roles.Count}"), ct);
        await audit.CommitAsync(tx, receipt, ct);
        return AccessResult<CompanyMfaPolicy>.Ok(new CompanyMfaPolicy(companyId, kind, roles.Order().ToList(), now));
    }
}
