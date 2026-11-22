using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Npgsql;
using RA = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using RL = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using SE = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using P = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaPolicy;
using PR = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaPolicyRole;

namespace Hasm.Infrastructure.Identity;

/// <summary>The company MFA policy kinds (Phase 4.1 section 3, D-080 (1)). Persisted numbers; never renumber. A company with no stored policy is Off.</summary>
public enum CompanyMfaPolicyKind : short { Off = 0, RequiredForRoles = 1, RequiredForAll = 2 }

/// <summary><paramref name="Revision"/> is the optimistic-concurrency revision: 0 while the company has no stored policy (Off), 1 after the first write, +1 on every change.</summary>
public sealed record CompanyMfaPolicy(Guid CompanyId, CompanyMfaPolicyKind Kind, IReadOnlyList<Guid> RoleIds, DateTimeOffset? UpdatedAtUtc, long Revision);

/// <summary>The one question the request pipeline asks: does the selected company's policy require MFA of this user? (an interface so the authorization handler is testable without a database)</summary>
/// <summary>The answer of the atomic company selection (review H1 of PR 181): only <see cref="Selected"/> wrote the selection; every other answer changed nothing, so the previously selected company stays.</summary>
public enum CompanySelectionOutcome { Selected, Forbidden, EnrolmentRequired, StepUpRequired }

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
    public MfaPolicyService(string connectionString, TimeProvider clock, AuditTrail audit, AccessService access, IdentityStore identity) : this(() => connectionString, clock, audit, access, identity) { }

    private readonly Lazy<NpgsqlDataSource> dataSource = new(() => NpgsqlDataSource.Create(connectionStringFactory()));
    public ValueTask DisposeAsync() => dataSource.IsValueCreated ? dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;

    public const int MaxRoles = 100;

    public async Task<AccessResult<CompanyMfaPolicy>> GetAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(actorUserId, companyId, PermissionCatalog.Codes.MfaPolicyView, ScopeTarget.Company, ct)) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Forbidden, "forbidden");
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        return AccessResult<CompanyMfaPolicy>.Ok(await ReadAsync(conn, null, companyId, false, ct));
    }

    /// <summary>Reads the policy. With <paramref name="forUpdate"/> the policy row is locked (FOR UPDATE) until the transaction ends, so the revision check, the audit "before" and the write see one state.</summary>
    private static async Task<CompanyMfaPolicy> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid companyId, bool forUpdate, CancellationToken ct)
    {
        var kind = CompanyMfaPolicyKind.Off;
        DateTimeOffset? at = null;
        var revision = 0L;
        await using (var cmd = new NpgsqlCommand($"SELECT {P.Policy}, {P.UpdatedAtUtc}, {P.Revision} FROM {P.Table} WHERE {P.CompanyId} = @c{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { kind = (CompanyMfaPolicyKind)r.GetInt16(0); at = r.GetFieldValue<DateTimeOffset>(1); revision = r.GetInt64(2); }
        }
        var roles = new List<Guid>();
        await using (var cmd = new NpgsqlCommand($"SELECT {PR.RoleId} FROM {PR.Table} WHERE {PR.CompanyId} = @c ORDER BY {PR.RoleId}", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) roles.Add(r.GetGuid(0));
        }
        return new CompanyMfaPolicy(companyId, kind, roles, at, revision);
    }

    /// <summary>Does the policy of <paramref name="companyId"/> require MFA of <paramref name="userId"/>? Off: no. RequiredForAll: yes. RequiredForRoles: yes when the user holds an active assignment of a listed role in that company.</summary>
    public async Task<bool> RequiresMfaAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        return await RequiresAsync(conn, null, userId, companyId, ct);
    }

    /// <summary>
    /// One lock per company orders policy changes against company selections (review H1 of PR 181): a selection holds it SHARED for its transaction, a policy change holds it EXCLUSIVE. A selection therefore
    /// either commits before the change (the session then belongs to "selected before the policy required MFA" and is refused per request by the company-policy requirement) or sees the committed change.
    /// </summary>
    private static async Task LockCompanyPolicyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid companyId, bool exclusive, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {(exclusive ? "pg_advisory_xact_lock" : "pg_advisory_xact_lock_shared")}(hashtextextended(@k, 41))", conn, tx);
        cmd.Parameters.AddWithValue("k", "hasm-mfa-policy:" + companyId.ToString("N"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Test-only hook inside the selection transaction, after the policy was evaluated and before the selection is written (never set in production).</summary>
    internal Func<CancellationToken, Task>? AfterSelectionPolicyCheckForTests { get; set; }

    /// <summary>
    /// Company selection as ONE decision (review H1 of PR 181): membership, then the company's MFA policy, and the selection is written ONLY when both allow it, in one transaction under the company's
    /// policy lock. Nothing is ever written and undone, so no request can observe a company the session may not enter, and a refused selection leaves the previously selected company in place.
    /// The policy is asked only after the membership check, so a company the caller cannot enter reveals nothing about its policy (<see cref="CompanySelectionOutcome.Forbidden"/> also covers an unknown or
    /// unavailable company and a session that is no longer live). <paramref name="mfaState"/> and <paramref name="mfaVerifiedAtUtc"/> are the session's, as validated on this request.
    /// </summary>
    public async Task<CompanySelectionOutcome> SelectCompanyAsync(Guid sessionId, Guid userId, Guid companyId, MfaState mfaState, DateTimeOffset? mfaVerifiedAtUtc, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await AccessService.IsActiveMemberAsync(conn, tx, userId, companyId, ct)) return CompanySelectionOutcome.Forbidden;
        await LockCompanyPolicyAsync(conn, tx, companyId, exclusive: false, ct);
        var carriesProof = mfaState != MfaState.None && mfaVerifiedAtUtc is not null;
        if (!carriesProof && await RequiresAsync(conn, tx, userId, companyId, ct))
            return mfaState == MfaState.None ? CompanySelectionOutcome.EnrolmentRequired : CompanySelectionOutcome.StepUpRequired;
        if (AfterSelectionPolicyCheckForTests is { } hook) await hook(ct);
        await using (var cmd = new NpgsqlCommand($"UPDATE {SE.Table} SET {SE.SelectedCompanyId} = @c WHERE {SE.Id} = @s AND {SE.UserId} = @u AND {SE.RevokedAtUtc} IS NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", sessionId);
            cmd.Parameters.AddWithValue("u", userId);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return CompanySelectionOutcome.Forbidden;
        }
        await tx.CommitAsync(ct);
        return CompanySelectionOutcome.Selected;
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

    /// <summary>The role list in the audit detail (review L2 of PR 181): the count and the first 16 hex characters of the SHA-256 of the sorted role ids, so a change from one set of roles to another
    /// set of the same size is visible (and comparable with the stored list) without putting up to 100 identifiers into the detail. An empty list is <c>0</c>.</summary>
    internal static string RoleSetLabel(IEnumerable<Guid> roleIds)
    {
        var sorted = roleIds.Select(r => r.ToString("N")).Order(StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? "0" : $"{sorted.Count}#{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(string.Join(",", sorted))))[..16].ToLowerInvariant()}";
    }

    /// <summary>Test-only hook between the locked revision read and the write (a start barrier for the first-write race; never set in production).</summary>
    internal Func<CancellationToken, Task>? AfterRevisionReadForTests { get; set; }

    /// <summary>
    /// Replaces the policy of the company. <paramref name="roleIds"/> must be empty unless the kind is RequiredForRoles, where it must list 1..<see cref="MaxRoles"/> distinct ACTIVE roles that are presets or belong to this company.
    /// Optimistic concurrency (review M1 of PR 181, the convention of the other settings): <paramref name="expectedRevision"/> is the revision the caller read (0 = no stored policy yet); null is
    /// <see cref="AccessStatus.PreconditionRequired"/>, a different current revision is <see cref="AccessStatus.PreconditionFailed"/> <c>revision_conflict</c> with the current revision. The revision check, the
    /// audit "before" and the write happen under the policy row lock; the first write inserts with ON CONFLICT DO NOTHING, so of two concurrent first writers exactly one wins and the other writes nothing.
    /// Every successful change (also one that stores the same policy again) moves the revision by one and is audited.
    /// </summary>
    public async Task<AccessResult<CompanyMfaPolicy>> SetAsync(Guid actorUserId, Guid companyId, long? expectedRevision, CompanyMfaPolicyKind kind, IReadOnlyList<Guid>? roleIds, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(actorUserId, companyId, PermissionCatalog.Codes.MfaPolicyUpdate, ScopeTarget.Company, ct)) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Forbidden, "forbidden");
        if (expectedRevision is not { } expected) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.PreconditionRequired, "precondition_required");
        if (!Enum.IsDefined(kind)) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Invalid, "invalid_request");
        var roles = (roleIds ?? []).Distinct().ToList();
        if (kind == CompanyMfaPolicyKind.RequiredForRoles ? roles.Count is 0 or > MaxRoles : roles.Count != 0) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Invalid, "invalid_request");

        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockCompanyPolicyAsync(conn, tx, companyId, exclusive: true, ct);                                       // ordered against company selections (review H1 of PR 181)
        // the policy row is locked from here to the commit: a concurrent change of an existing policy waits and then sees the new revision (412). With no row yet there is nothing to lock; the insert below decides.
        var before = await ReadAsync(conn, tx, companyId, forUpdate: true, ct);
        if (before.Revision != expected) return AccessResult<CompanyMfaPolicy>.Stale(before.Revision);
        if (AfterRevisionReadForTests is { } hook) await hook(ct);
        foreach (var role in roles)
        {
            await using var cmd = new NpgsqlCommand($"SELECT 1 FROM {RL.Table} WHERE {RL.Id} = @r AND {RL.IsActive} AND ({RL.CompanyId} IS NULL OR {RL.CompanyId} = @c)", conn, tx);
            cmd.Parameters.AddWithValue("r", role);
            cmd.Parameters.AddWithValue("c", companyId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Invalid, "unknown_role");
        }

        // The lock-out guard, strict (review L3 of PR 181, a tightening of D-169): EVERY change of the policy, switching it off included, requires that the person making it has an authenticator,
        // whether or not the policy covers them. (The route requires a FRESH second factor of an enrolled caller; a caller without an authenticator passes that requirement by D-161 and is refused here.)
        // So a company can never lock its administrators out by a policy change, and a password-only session can never weaken or switch off a company's MFA policy.
        if (await identity.GetMfaStateAsync(actorUserId, ct) != MfaState.Enrolled) return AccessResult<CompanyMfaPolicy>.Fail(AccessStatus.Conflict, "mfa_policy_requires_own_enrolment");

        var now = clock.GetUtcNow();
        var revision = expected + 1;
        // first write: INSERT ... ON CONFLICT DO NOTHING (a concurrent first writer that committed first leaves 0 rows here); change: UPDATE guarded by the expected revision. Anything but exactly one row is a lost race.
        await using (var cmd = new NpgsqlCommand(expected == 0
            ? $"INSERT INTO {P.Table} ({P.CompanyId},{P.Policy},{P.Revision},{P.UpdatedAtUtc},{P.UpdatedBy}) VALUES (@c,@p,1,@n,@u) ON CONFLICT ({P.CompanyId}) DO NOTHING"
            : $"UPDATE {P.Table} SET {P.Policy} = @p, {P.Revision} = {P.Revision} + 1, {P.UpdatedAtUtc} = @n, {P.UpdatedBy} = @u WHERE {P.CompanyId} = @c AND {P.Revision} = @e", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("p", (short)kind);
            cmd.Parameters.AddWithValue("n", now);
            cmd.Parameters.AddWithValue("u", actorUserId);
            cmd.Parameters.AddWithValue("e", expected);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            {
                var current = (await ReadAsync(conn, tx, companyId, false, ct)).Revision;                             // read committed: the winner's row is visible to this new statement
                await tx.RollbackAsync(ct);
                return AccessResult<CompanyMfaPolicy>.Stale(current);
            }
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
            new AuditEventData(MfaAuditEvents.PolicyChanged, actorUserId, null, null, $"company {companyId:N} mfa policy {before.Kind} -> {kind} roles {RoleSetLabel(before.RoleIds)} -> {RoleSetLabel(roles)} r{revision}"), ct);
        await audit.CommitAsync(tx, receipt, ct);
        return AccessResult<CompanyMfaPolicy>.Ok(new CompanyMfaPolicy(companyId, kind, roles.Order().ToList(), now, revision));
    }
}
