using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Npgsql;
using System.Runtime.CompilerServices;
using A = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using CCA = Hasm.Infrastructure.SystemDatabase.SystemSchema.CostCenterAssignment;
using Au = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserCompanyAccess;
using R = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using RP = Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Infrastructure.Access;

public enum AccessStatus { Ok, Forbidden, NotFound, Invalid, Conflict, PreconditionRequired, PreconditionFailed }

/// <summary>Outcome of an access operation. <see cref="Code"/> is a short machine-readable reason, never an exception message. <see cref="CurrentRevision"/> is set on a concurrency failure
/// (<see cref="AccessStatus.PreconditionFailed"/>) so the client can reload, as <c>ConfigResult</c> does.</summary>
public sealed record AccessResult<T>(AccessStatus Status, string? Code, T? Value, long? CurrentRevision = null)
{
    public bool IsOk => Status == AccessStatus.Ok;
    public static AccessResult<T> Ok(T value) => new(AccessStatus.Ok, null, value);
    public static AccessResult<T> Fail(AccessStatus status, string code) => new(status, code, default);
    public static AccessResult<T> Stale(long currentRevision) => new(AccessStatus.PreconditionFailed, "revision_conflict", default, currentRevision);
}

/// <summary>Numeric values are persisted in the audit table; never renumber.</summary>
public enum AccessEvent : short
{
    MembershipGranted = 1, MembershipRevoked = 2, RoleCreated = 3, RolePermissionsChanged = 4,
    RoleDeactivated = 5, RoleAssigned = 6, RoleAssignmentRevoked = 7,
}

public sealed record MemberView(Guid UserId, string Login, string DisplayName, UserState State, DateTimeOffset GrantedAtUtc);
public sealed record RoleView(Guid Id, string Name, bool IsPreset, string? PresetKey, bool IsActive, IReadOnlyList<string> Permissions);
public sealed record AssignmentView(Guid Id, Guid UserId, Guid RoleId, ScopeKind Scope, Guid? ScopeId, DateTimeOffset GrantedAtUtc, long? ScopeEpoch = null);
public sealed record AuditEntry(long Id, DateTimeOffset OccurredAtUtc, Guid? ActorUserId, Guid? TargetUserId, Guid? RoleId, AccessEvent Event, string? Detail);

/// <summary>The identity and company of a live session, read from the server-side session row (never from the client).</summary>
public sealed record SessionContext(Guid UserId, Guid? CompanyId);
public sealed record CompanySummary(Guid Id, string? NameAr, string? NameEn);

/// <summary>
/// Company access and role-based access control over the central system database.
///
/// Authorization model: deny by default. A user can act in a company only through an ACTIVE membership, an active role
/// assignment, an active role that contains the permission, and a scope that covers the target. Everything is read from
/// the database on every call, so revoking a membership/assignment, deactivating a role or disabling a user takes effect
/// on the very next request without a new login. Authentication alone never grants any company access.
///
/// Every public method authorizes the acting user itself (callers cannot forget to), takes the company from a
/// server-validated source and never trusts client claims. Mutations run in one transaction under a per-company advisory lock
/// and re-evaluate the actor's privileges inside that lock, so concurrent permission changes serialize (no check-then-act race).
/// </summary>
public sealed class AccessService(Func<string> connectionStringFactory, TimeProvider clock, PermissionCatalog catalog, AuditTrail audit, IScopeTargetValidator? scopeValidator = null) : IAsyncDisposable
{
    private const string UniqueViolation = "23505";
    private const int MaxDetailLength = 1000;
    public const int MaxRoleNameLength = 100;
    public const int MaxPageSize = 200;

    private readonly Lazy<NpgsqlDataSource> dataSource = new(() => NpgsqlDataSource.Create(connectionStringFactory()));

    public AccessService(string connectionString, TimeProvider clock, PermissionCatalog catalog, AuditTrail audit, IScopeTargetValidator? scopeValidator = null) : this(() => connectionString, clock, catalog, audit, scopeValidator) { }

    private readonly IScopeTargetValidator scopeValidator = scopeValidator ?? DenyAllScopeTargetValidator.Instance;

    public PermissionCatalog Catalog => catalog;

    public ValueTask DisposeAsync() => dataSource.IsValueCreated ? dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;

    // ------------------------------------------------------------------ session and company selection

    /// <summary>Reads the user and selected company of a live session. Null when the session or its user is no longer valid.</summary>
    public async Task<SessionContext?> GetSessionContextAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT s.{S.UserId}, s.{S.SelectedCompanyId} FROM {S.Table} s JOIN {U.Table} u ON u.{U.Id} = s.{S.UserId} " +
            $"WHERE s.{S.Id} = @s AND s.{S.RevokedAtUtc} IS NULL AND s.{S.IdleExpiresAtUtc} > @n AND s.{S.AbsoluteExpiresAtUtc} > @n AND u.{U.State} = 0", conn);
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new SessionContext(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1)) : null;
    }

    /// <summary>Companies the user may select: active memberships of companies that are provisioned, for an active user.</summary>
    public async Task<IReadOnlyList<Guid>> ListAccessibleCompaniesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.{M.CompanyId} FROM {M.Table} m JOIN {C.Table} c ON c.{C.Id} = m.{M.CompanyId} JOIN {U.Table} u ON u.{U.Id} = m.{M.UserId} " +
            $"WHERE m.{M.UserId} = @u AND m.{M.RevokedAtUtc} IS NULL AND c.{C.ProvisioningState} = {(short)ProvisioningState.Ready} AND u.{U.State} = 0 ORDER BY m.{M.GrantedAtUtc}, m.{M.CompanyId}", conn);
        cmd.Parameters.AddWithValue("u", userId);
        var list = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }

    /// <summary>
    /// The companies the user may select, with the PROJECTED display names (authoritative names live in the company databases). Only the
    /// user's own active memberships are returned, so a name is never disclosed to a non-member. A null name = not projected yet.
    /// </summary>
    public async Task<IReadOnlyList<CompanySummary>> ListAccessibleCompanySummariesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.{M.CompanyId}, c.{C.DisplayNameAr}, c.{C.DisplayNameEn} FROM {M.Table} m JOIN {C.Table} c ON c.{C.Id} = m.{M.CompanyId} JOIN {U.Table} u ON u.{U.Id} = m.{M.UserId} " +
            $"WHERE m.{M.UserId} = @u AND m.{M.RevokedAtUtc} IS NULL AND c.{C.ProvisioningState} = {(short)ProvisioningState.Ready} AND u.{U.State} = 0 ORDER BY m.{M.GrantedAtUtc}, m.{M.CompanyId}", conn);
        cmd.Parameters.AddWithValue("u", userId);
        var list = new List<CompanySummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new CompanySummary(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }

    /// <summary>
    /// Binds the session to a company after a server-side membership check; <c>null</c> clears the selection.
    /// Returns false (and changes nothing) when the user has no active membership of that company or the company is unavailable;
    /// callers must not reveal which of the two it was.
    /// </summary>
    public async Task<bool> SelectCompanyAsync(Guid sessionId, Guid userId, Guid? companyId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        if (companyId is { } id && !await IsActiveMemberAsync(conn, null, userId, id, ct)) return false;
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {S.Table} SET {S.SelectedCompanyId} = @c WHERE {S.Id} = @s AND {S.UserId} = @u AND {S.RevokedAtUtc} IS NULL", conn);
        cmd.Parameters.AddWithValue("c", (object?)companyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ------------------------------------------------------------------ authorization

    /// <summary>The permissions the user holds right now in the company (empty when not an active member).</summary>
    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        return await LoadEffectiveAsync(conn, null, userId, companyId, requireActiveUser: true, ct);
    }

    /// <summary>Single decision point for "may this user do <paramref name="permissionCode"/> on <paramref name="target"/> in this company".</summary>
    public async Task<bool> IsAllowedAsync(Guid userId, Guid companyId, string permissionCode, ScopeTarget target, CancellationToken ct = default) =>
        (await GetEffectivePermissionsAsync(userId, companyId, ct)).Has(permissionCode, target);

    internal async Task<bool> IsAllowedAsync(Guid userId, Guid companyId, string permissionCode, ScopeTarget target, Action reserveCommand, CancellationToken ct)
        => (await GetEffectivePermissionsAsync(userId, companyId, reserveCommand, ct)).Has(permissionCode, target);

    internal async Task<EffectivePermissions> GetEffectivePermissionsAsync(Guid userId, Guid companyId, Action reserveCommand, CancellationToken ct)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        return await LoadEffectiveAsync(conn, null, userId, companyId, true, ct, reserveCommand);
    }

    private async Task<EffectivePermissions> LoadEffectiveAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid userId, Guid companyId, bool requireActiveUser, CancellationToken ct, Action? reserveCommand = null)
    {
        reserveCommand?.Invoke();
        await using var cmd = new NpgsqlCommand(
            $"SELECT a.{A.Id}, a.{A.RoleId}, a.{A.ScopeKind}, a.{A.ScopeId}, rp.{RP.Permission}, COALESCE(a.{A.ScopeEpoch}, a.{A.WarehouseScopeEpoch}) " +
            $"FROM {A.Table} a " +
            $"JOIN {M.Table} m ON m.{M.UserId} = a.{A.UserId} AND m.{M.CompanyId} = a.{A.CompanyId} AND m.{M.RevokedAtUtc} IS NULL " +
            $"JOIN {U.Table} u ON u.{U.Id} = a.{A.UserId} " +
            $"JOIN {C.Table} c ON c.{C.Id} = a.{A.CompanyId} AND c.{C.ProvisioningState} = {(short)ProvisioningState.Ready} " +
            $"JOIN {R.Table} r ON r.{R.Id} = a.{A.RoleId} AND r.{R.IsActive} AND (r.{R.CompanyId} IS NULL OR r.{R.CompanyId} = a.{A.CompanyId}) " +
            $"LEFT JOIN {RP.Table} rp ON rp.{RP.RoleId} = r.{R.Id} " +
            $"WHERE a.{A.UserId} = @u AND a.{A.CompanyId} = @c AND a.{A.RevokedAtUtc} IS NULL AND (@anyUser OR u.{U.State} = 0)", conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("anyUser", !requireActiveUser);

        var grants = new Dictionary<Guid, (Guid Role, ScopeKind Scope, Guid? ScopeId, long? Epoch, HashSet<string> Permissions)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0);
                if (!grants.TryGetValue(id, out var g))
                    grants[id] = g = (r.GetGuid(1), (ScopeKind)r.GetInt16(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(5) ? null : r.GetInt64(5), []);
                if (!r.IsDBNull(4)) g.Permissions.Add(r.GetString(4));
            }
        }
        // Cost-center-scoped grants (own table: the scope check of the main assignment table cannot be extended without a DROP).
        reserveCommand?.Invoke();
        await using (var ccCmd = new NpgsqlCommand(
            $"SELECT a.{CCA.Id}, a.{CCA.RoleId}, a.{CCA.CostCenterId}, rp.{RP.Permission}, a.{CCA.Epoch} " +
            $"FROM {CCA.Table} a " +
            $"JOIN {M.Table} m ON m.{M.UserId} = a.{CCA.UserId} AND m.{M.CompanyId} = a.{CCA.CompanyId} AND m.{M.RevokedAtUtc} IS NULL " +
            $"JOIN {U.Table} u ON u.{U.Id} = a.{CCA.UserId} " +
            $"JOIN {C.Table} c ON c.{C.Id} = a.{CCA.CompanyId} AND c.{C.ProvisioningState} = {(short)ProvisioningState.Ready} " +
            $"JOIN {R.Table} r ON r.{R.Id} = a.{CCA.RoleId} AND r.{R.IsActive} AND (r.{R.CompanyId} IS NULL OR r.{R.CompanyId} = a.{CCA.CompanyId}) " +
            $"LEFT JOIN {RP.Table} rp ON rp.{RP.RoleId} = r.{R.Id} " +
            $"WHERE a.{CCA.UserId} = @u AND a.{CCA.CompanyId} = @c AND a.{CCA.RevokedAtUtc} IS NULL AND (@anyUser OR u.{U.State} = 0)", conn, tx))
        {
            ccCmd.Parameters.AddWithValue("u", userId);
            ccCmd.Parameters.AddWithValue("c", companyId);
            ccCmd.Parameters.AddWithValue("anyUser", !requireActiveUser);
            await using var rr = await ccCmd.ExecuteReaderAsync(ct);
            while (await rr.ReadAsync(ct))
            {
                var id = rr.GetGuid(0);
                if (!grants.TryGetValue(id, out var g))
                    grants[id] = g = (rr.GetGuid(1), ScopeKind.CostCenter, rr.GetGuid(2), rr.GetInt64(4), []);
                if (!rr.IsDBNull(3)) g.Permissions.Add(rr.GetString(3));
            }
        }
        // Branch- and warehouse-scoped grants are effective only while the target exists in THIS company, is active and still has the activation epoch the
        // assignment recorded. Anything else (deactivated, reactivated since, unknown, company database unavailable) drops the grant:
        // fail closed, never reinterpreted as company-wide access. Company-wide grants are unaffected. Evaluated on every call (no cache).
        foreach (var (grantId, g) in grants.Where(kv => kv.Value.Scope is ScopeKind.Branch or ScopeKind.Warehouse or ScopeKind.CostCenter).ToList())
            if (!await ScopedGrantIsLiveAsync(companyId, g.Scope, g.ScopeId, g.Epoch, ct, reserveCommand)) grants.Remove(grantId);
        return new EffectivePermissions(catalog, [.. grants.Select(kv => new GrantedRole(kv.Key, kv.Value.Role, kv.Value.Scope, kv.Value.ScopeId, kv.Value.Permissions))]);
    }

    private async Task<bool> ScopedGrantIsLiveAsync(Guid companyId, ScopeKind kind, Guid? targetId, long? recordedEpoch, CancellationToken ct, Action? reserveCommand = null)
    {
        if (targetId is not { } id || recordedEpoch is not { } epoch) return false;
        try
        {
            var state = reserveCommand is null ? await scopeValidator.GetStateAsync(companyId, kind, id, ct)
                : scopeValidator is IBudgetedScopeTargetValidator counted
                    ? await counted.GetStateAsync(companyId, kind, id, reserveCommand, ct)
                    : throw new ReadCommandBudgetException(ReadBudgetFailure.UnsupportedValidator);
            return state is { Active: true } && state.Epoch == epoch;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ReadCommandBudgetException) { return false; }
    }

    internal static async Task<bool> IsActiveMemberAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid userId, Guid companyId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT 1 FROM {M.Table} m JOIN {C.Table} c ON c.{C.Id} = m.{M.CompanyId} JOIN {U.Table} u ON u.{U.Id} = m.{M.UserId} " +
            $"WHERE m.{M.UserId} = @u AND m.{M.CompanyId} = @c AND m.{M.RevokedAtUtc} IS NULL AND c.{C.ProvisioningState} = {(short)ProvisioningState.Ready} AND u.{U.State} = 0", conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("c", companyId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // ------------------------------------------------------------------ transaction helper

    /// <summary>
    /// Opens a transaction holding the company's access-administration lock and returns the actor's permissions as seen
    /// inside that lock. Dispose to roll back; call <c>CommitAsync</c> on success.
    /// </summary>
    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx, EffectivePermissions Actor)> BeginAdminAsync(Guid actorUserId, Guid companyId, CancellationToken ct)
    {
        var conn = await dataSource.Value.OpenConnectionAsync(ct);
        try
        {
            var tx = await conn.BeginTransactionAsync(ct);
            await AdministratorContinuity.LockCompanyAsync(conn, tx, companyId, ct);
            var actor = await LoadEffectiveAsync(conn, tx, actorUserId, companyId, requireActiveUser: true, ct);
            return (conn, tx, actor);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static string Truncate(string text) => text.Length <= MaxDetailLength ? text : text[..(MaxDetailLength - 3)] + "...";

    // Audit receipts of the transaction being built; consumed by CommitAuditedAsync (anchoring happens only after the commit).
    private readonly ConditionalWeakTable<NpgsqlTransaction, List<AuditReceipt>> pendingReceipts = new();

    /// <summary>
    /// Writes the legacy access-audit projection row AND the hash-chained, key-authenticated audit event in the caller's transaction.
    /// If the chained event cannot be written an exception propagates, the transaction is abandoned and the mutation does not happen.
    /// </summary>
    private async Task WriteAuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid? actor, Guid company, Guid? target, Guid? role, AccessEvent ev, string? detail, CancellationToken ct)
    {
        var now = AuditCanonical.FromMicros(AuditCanonical.ToMicros(clock.GetUtcNow()));   // PostgreSQL precision, identical in both records
        var text = detail is null ? null : Truncate(detail);
        long legacyId;
        await using (var cmd = new NpgsqlCommand(
            $"INSERT INTO {Au.Table} ({Au.OccurredAtUtc}, {Au.ActorUserId}, {Au.CompanyId}, {Au.TargetUserId}, {Au.RoleId}, {Au.EventCode}, {Au.Detail}) " +
            $"VALUES (@t, @a, @c, @tu, @r, @e, @d) RETURNING {Au.Id}", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", now);
            cmd.Parameters.AddWithValue("a", (object?)actor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("c", company);
            cmd.Parameters.AddWithValue("tu", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("r", (object?)role ?? DBNull.Value);
            cmd.Parameters.AddWithValue("e", (short)ev);
            cmd.Parameters.AddWithValue("d", (object?)text ?? DBNull.Value);
            legacyId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var receipt = await audit.AppendAsync(conn, tx, AuditStream.ForCompany(company), now,
            new AuditEventData((short)ev, actor, target, role, text, legacyId), ct);
        pendingReceipts.GetOrCreateValue(tx).Add(receipt);
    }

    /// <summary>Commits the transaction and anchors the new audit head(s) in the external checkpoint store.</summary>
    private Task CommitAuditedAsync(NpgsqlTransaction tx, CancellationToken ct) =>
        audit.CommitAsync(tx, pendingReceipts.TryGetValue(tx, out var list) ? list : [], ct);

    private static AccessResult<T> Denied<T>(string code = "forbidden") => AccessResult<T>.Fail(AccessStatus.Forbidden, code);

    // ------------------------------------------------------------------ roles

    private sealed record RoleRow(Guid Id, Guid? CompanyId, string? PresetKey, string Name, bool IsActive, HashSet<string> Permissions)
    {
        public bool IsPreset => PresetKey is not null;
    }

    /// <summary>Loads a role visible to the company (a preset role or one of the company's own). Other companies' roles are invisible.</summary>
    /// <summary>True when the role exists for this company (or is a preset), is active and contains the permission: used to validate the role an approval stage is bound to.</summary>
    public async Task<bool> RoleHoldsPermissionAsync(Guid companyId, Guid roleId, string permission, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        var role = await FindRoleAsync(conn, null, companyId, roleId, ct);
        return role is { IsActive: true } && role.Permissions.Contains(permission);
    }

    private static async Task<RoleRow?> FindRoleAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid companyId, Guid roleId, CancellationToken ct)
    {
        var rows = await ReadRolesAsync(conn, tx, companyId, roleId, ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    private static async Task<List<RoleRow>> ReadRolesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid companyId, Guid? roleId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT r.{R.Id}, r.{R.CompanyId}, r.{R.PresetKey}, r.{R.Name}, r.{R.IsActive}, rp.{RP.Permission} FROM {R.Table} r LEFT JOIN {RP.Table} rp ON rp.{RP.RoleId} = r.{R.Id} " +
            $"WHERE (r.{R.CompanyId} IS NULL OR r.{R.CompanyId} = @c) AND (@r::uuid IS NULL OR r.{R.Id} = @r) ORDER BY r.{R.CompanyId} NULLS FIRST, lower(r.{R.Name}), r.{R.Id}, rp.{RP.Permission}", conn, tx);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)roleId ?? DBNull.Value });
        var rows = new List<RoleRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetGuid(0);
            if (rows.Count == 0 || rows[^1].Id != id)
                rows.Add(new RoleRow(id, r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetBoolean(4), []));
            if (!r.IsDBNull(5)) rows[^1].Permissions.Add(r.GetString(5));
        }
        return rows;
    }

    private static RoleView ToView(RoleRow r) => new(r.Id, r.Name, r.IsPreset, r.PresetKey, r.IsActive, [.. r.Permissions.Order(StringComparer.Ordinal)]);

    private bool TryNormalizePermissions(IEnumerable<string>? input, out HashSet<string> normalized)
    {
        normalized = [];
        if (input is null) return false;
        foreach (var code in input)
        {
            if (!catalog.TryGet(code, out _)) return false;
            normalized.Add(code);
        }
        return true;
    }

    private static bool TryNormalizeName(string? name, out string normalized)
    {
        normalized = name?.Trim() ?? "";
        return normalized.Length is >= 1 and <= MaxRoleNameLength && !normalized.Any(char.IsControl);
    }

    public async Task<AccessResult<IReadOnlyList<RoleView>>> ListRolesAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        var actor = await LoadEffectiveAsync(conn, null, actorUserId, companyId, true, ct);
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.RolesView)) return Denied<IReadOnlyList<RoleView>>();
        return AccessResult<IReadOnlyList<RoleView>>.Ok([.. (await ReadRolesAsync(conn, null, companyId, null, ct)).Select(ToView)]);
    }

    public async Task<AccessResult<RoleView>> CreateRoleAsync(Guid actorUserId, Guid companyId, string? name, IEnumerable<string>? permissions, CancellationToken ct = default)
    {
        var (conn, tx, actor) = await BeginAdminAsync(actorUserId, companyId, ct);
        await using var _c = conn; await using var _t = tx;
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.RolesCreate)) return Denied<RoleView>();
        if (!TryNormalizeName(name, out var roleName)) return AccessResult<RoleView>.Fail(AccessStatus.Invalid, "invalid_name");
        if (!TryNormalizePermissions(permissions, out var perms)) return AccessResult<RoleView>.Fail(AccessStatus.Invalid, "unknown_permission");
        // Privilege escalation guard: a role can later be assigned company-wide, so the creator must hold every permission company-wide.
        if (!actor.CanGrant(perms, ScopeTarget.Company)) return Denied<RoleView>("cannot_grant_permission");

        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        try
        {
            await using (var ins = new NpgsqlCommand(
                $"INSERT INTO {R.Table} ({R.Id}, {R.CompanyId}, {R.PresetKey}, {R.Name}, {R.IsActive}, {R.CreatedAtUtc}, {R.CreatedBy}, {R.UpdatedAtUtc}) VALUES (@i, @c, NULL, @n, true, @t, @a, @t)", conn, tx))
            {
                ins.Parameters.AddWithValue("i", id);
                ins.Parameters.AddWithValue("c", companyId);
                ins.Parameters.AddWithValue("n", roleName);
                ins.Parameters.AddWithValue("t", now);
                ins.Parameters.AddWithValue("a", actorUserId);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await InsertPermissionsAsync(conn, tx, id, perms, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return AccessResult<RoleView>.Fail(AccessStatus.Conflict, "role_name_taken");
        }
        await WriteAuditAsync(conn, tx, actorUserId, companyId, null, id, AccessEvent.RoleCreated, $"permissions: {string.Join(',', perms.Order(StringComparer.Ordinal))}", ct);
        await CommitAuditedAsync(tx, ct);
        return AccessResult<RoleView>.Ok(new RoleView(id, roleName, false, null, true, [.. perms.Order(StringComparer.Ordinal)]));
    }

    private static async Task InsertPermissionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid roleId, IEnumerable<string> permissions, CancellationToken ct)
    {
        foreach (var code in permissions)
        {
            await using var cmd = new NpgsqlCommand($"INSERT INTO {RP.Table} ({RP.RoleId}, {RP.Permission}) VALUES (@r, @p)", conn, tx);
            cmd.Parameters.AddWithValue("r", roleId);
            cmd.Parameters.AddWithValue("p", code);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Replaces a custom role's permission set. Preset roles are immutable.</summary>
    public async Task<AccessResult<RoleView>> SetRolePermissionsAsync(Guid actorUserId, Guid companyId, Guid roleId, IEnumerable<string>? permissions, CancellationToken ct = default)
    {
        var (conn, tx, actor) = await BeginAdminAsync(actorUserId, companyId, ct);
        await using var _c = conn; await using var _t = tx;
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.RolesUpdate)) return Denied<RoleView>();
        var role = await FindRoleAsync(conn, tx, companyId, roleId, ct);
        if (role is null) return AccessResult<RoleView>.Fail(AccessStatus.NotFound, "role_not_found");
        if (role.IsPreset) return Denied<RoleView>("preset_role_immutable");
        if (!TryNormalizePermissions(permissions, out var perms)) return AccessResult<RoleView>.Fail(AccessStatus.Invalid, "unknown_permission");
        // The editor must hold everything the role has now and everything it will have: it may neither add power it lacks
        // nor reshape a role that carries power it lacks.
        if (!actor.CanGrant(role.Permissions.Union(perms), ScopeTarget.Company)) return Denied<RoleView>("cannot_grant_permission");

        var added = perms.Except(role.Permissions).Order(StringComparer.Ordinal).ToList();
        var removed = role.Permissions.Except(perms).Order(StringComparer.Ordinal).ToList();
        foreach (var code in removed)
        {
            await using var del = new NpgsqlCommand($"DELETE FROM {RP.Table} WHERE {RP.RoleId} = @r AND {RP.Permission} = @p", conn, tx);
            del.Parameters.AddWithValue("r", roleId);
            del.Parameters.AddWithValue("p", code);
            await del.ExecuteNonQueryAsync(ct);
        }
        await InsertPermissionsAsync(conn, tx, roleId, added, ct);
        await TouchRoleAsync(conn, tx, roleId, null, ct);
        await WriteAuditAsync(conn, tx, actorUserId, companyId, null, roleId, AccessEvent.RolePermissionsChanged,
            $"added: {string.Join(',', added)}; removed: {string.Join(',', removed)}", ct);
        await CommitAuditedAsync(tx, ct);
        return AccessResult<RoleView>.Ok(ToView(role with { Permissions = perms }));
    }

    private async Task TouchRoleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid roleId, bool? isActive, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"UPDATE {R.Table} SET {R.UpdatedAtUtc} = @t, {R.IsActive} = coalesce(@a, {R.IsActive}) WHERE {R.Id} = @r", conn, tx);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow());
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)isActive ?? DBNull.Value });
        cmd.Parameters.AddWithValue("r", roleId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Deactivates a custom role: its assignments immediately grant nothing. Roles are never deleted.</summary>
    public async Task<AccessResult<bool>> DeactivateRoleAsync(Guid actorUserId, Guid companyId, Guid roleId, CancellationToken ct = default)
    {
        var (conn, tx, actor) = await BeginAdminAsync(actorUserId, companyId, ct);
        await using var _c = conn; await using var _t = tx;
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.RolesDelete)) return Denied<bool>();
        var role = await FindRoleAsync(conn, tx, companyId, roleId, ct);
        if (role is null) return AccessResult<bool>.Fail(AccessStatus.NotFound, "role_not_found");
        if (role.IsPreset) return Denied<bool>("preset_role_immutable");
        if (!actor.CanGrant(role.Permissions, ScopeTarget.Company)) return Denied<bool>("cannot_grant_permission");
        if (!role.IsActive) return AccessResult<bool>.Fail(AccessStatus.Conflict, "role_already_inactive");
        await TouchRoleAsync(conn, tx, roleId, false, ct);
        await WriteAuditAsync(conn, tx, actorUserId, companyId, null, roleId, AccessEvent.RoleDeactivated, null, ct);
        await CommitAuditedAsync(tx, ct);
        return AccessResult<bool>.Ok(true);
    }

    // ------------------------------------------------------------------ membership

    public async Task<AccessResult<IReadOnlyList<MemberView>>> ListMembersAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        var actor = await LoadEffectiveAsync(conn, null, actorUserId, companyId, true, ct);
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.MembersView)) return Denied<IReadOnlyList<MemberView>>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT u.{U.Id}, u.{U.Login}, u.{U.DisplayName}, u.{U.State}, m.{M.GrantedAtUtc} FROM {M.Table} m JOIN {U.Table} u ON u.{U.Id} = m.{M.UserId} " +
            $"WHERE m.{M.CompanyId} = @c AND m.{M.RevokedAtUtc} IS NULL ORDER BY u.{U.Login}", conn);
        cmd.Parameters.AddWithValue("c", companyId);
        var list = new List<MemberView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new MemberView(r.GetGuid(0), r.GetString(1), r.GetString(2), (UserState)r.GetInt16(3), r.GetFieldValue<DateTimeOffset>(4)));
        return AccessResult<IReadOnlyList<MemberView>>.Ok(list);
    }

    /// <summary>Grants (or re-grants after revocation) membership of this company to the user with the given login. Grants no roles.</summary>
    public async Task<AccessResult<MemberView>> GrantMembershipAsync(Guid actorUserId, Guid companyId, string? login, CancellationToken ct = default)
    {
        var (conn, tx, actor) = await BeginAdminAsync(actorUserId, companyId, ct);
        await using var _c = conn; await using var _t = tx;
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.MembersCreate)) return Denied<MemberView>();
        if (!LoginName.TryNormalize(login, out var normalized)) return AccessResult<MemberView>.Fail(AccessStatus.NotFound, "user_not_found");
        var result = await GrantCoreAsync(conn, tx, actorUserId, companyId, normalized, ct);
        if (result.IsOk) await CommitAuditedAsync(tx, ct);
        return result;
    }

    private async Task<AccessResult<MemberView>> GrantCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid? actorUserId, Guid companyId, string normalizedLogin, CancellationToken ct)
    {
        Guid userId; string display;
        await using (var find = new NpgsqlCommand($"SELECT {U.Id}, {U.DisplayName} FROM {U.Table} WHERE {U.Login} = @l AND {U.State} = 0", conn, tx))
        {
            find.Parameters.AddWithValue("l", normalizedLogin);
            await using var r = await find.ExecuteReaderAsync(ct);
            // Disabled/locked users are reported exactly like unknown ones.
            if (!await r.ReadAsync(ct)) return AccessResult<MemberView>.Fail(AccessStatus.NotFound, "user_not_found");
            userId = r.GetGuid(0); display = r.GetString(1);
        }
        await using (var co = new NpgsqlCommand($"SELECT 1 FROM {C.Table} WHERE {C.Id} = @c AND {C.ProvisioningState} = {(short)ProvisioningState.Ready}", conn, tx))
        {
            co.Parameters.AddWithValue("c", companyId);
            if (await co.ExecuteScalarAsync(ct) is null) return AccessResult<MemberView>.Fail(AccessStatus.NotFound, "company_unavailable");
        }
        var now = clock.GetUtcNow();
        await using (var up = new NpgsqlCommand(
            $"INSERT INTO {M.Table} ({M.UserId}, {M.CompanyId}, {M.GrantedAtUtc}, {M.GrantedBy}) VALUES (@u, @c, @t, @a) " +
            $"ON CONFLICT ({M.UserId}, {M.CompanyId}) DO UPDATE SET {M.GrantedAtUtc} = @t, {M.GrantedBy} = @a, {M.RevokedAtUtc} = NULL, {M.RevokedBy} = NULL WHERE {M.Table}.{M.RevokedAtUtc} IS NOT NULL", conn, tx))
        {
            up.Parameters.AddWithValue("u", userId);
            up.Parameters.AddWithValue("c", companyId);
            up.Parameters.AddWithValue("t", now);
            up.Parameters.AddWithValue("a", (object?)actorUserId ?? DBNull.Value);
            if (await up.ExecuteNonQueryAsync(ct) == 0) return AccessResult<MemberView>.Fail(AccessStatus.Conflict, "already_member");
        }
        await WriteAuditAsync(conn, tx, actorUserId, companyId, userId, null, AccessEvent.MembershipGranted, null, ct);
        return AccessResult<MemberView>.Ok(new MemberView(userId, normalizedLogin, display, UserState.Active, now));
    }

    /// <summary>
    /// Revokes the membership and, in the same transaction, every active role assignment of the user in this company and the
    /// company selection of the user's sessions. The actor must hold every permission the target holds (cannot remove someone more powerful).
    /// </summary>
    public async Task<AccessResult<bool>> RevokeMembershipAsync(Guid actorUserId, Guid companyId, Guid targetUserId, CancellationToken ct = default)
    {
        var (conn, tx, actor) = await BeginAdminAsync(actorUserId, companyId, ct);
        await using var _c = conn; await using var _t = tx;
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.MembersDelete)) return Denied<bool>();
        if (targetUserId == actorUserId) return Denied<bool>("cannot_modify_self");

        await using (var chk = new NpgsqlCommand($"SELECT 1 FROM {M.Table} WHERE {M.UserId} = @u AND {M.CompanyId} = @c AND {M.RevokedAtUtc} IS NULL", conn, tx))
        {
            chk.Parameters.AddWithValue("u", targetUserId);
            chk.Parameters.AddWithValue("c", companyId);
            if (await chk.ExecuteScalarAsync(ct) is null) return AccessResult<bool>.Fail(AccessStatus.NotFound, "member_not_found");
        }
        var target = await LoadEffectiveAsync(conn, tx, targetUserId, companyId, requireActiveUser: false, ct);
        if (target.Grants.Any(g => !actor.CanGrant(g.Permissions, new ScopeTarget(g.Scope, g.ScopeId)))) return Denied<bool>("target_has_higher_privileges");
        if (await AdministratorContinuity.WouldRemoveLastAsync(conn, tx, companyId, targetUserId, null, ct)) return AccessResult<bool>.Fail(AccessStatus.Conflict, "last_administrator");

        var now = clock.GetUtcNow();
        int revoked;
        await using (var rev = new NpgsqlCommand(
            $"UPDATE {A.Table} SET {A.RevokedAtUtc} = @t, {A.RevokedBy} = @a WHERE {A.UserId} = @u AND {A.CompanyId} = @c AND {A.RevokedAtUtc} IS NULL", conn, tx))
        {
            rev.Parameters.AddWithValue("t", now);
            rev.Parameters.AddWithValue("a", actorUserId);
            rev.Parameters.AddWithValue("u", targetUserId);
            rev.Parameters.AddWithValue("c", companyId);
            revoked = await rev.ExecuteNonQueryAsync(ct);
        }
        await using (var revCc = new NpgsqlCommand(
            $"UPDATE {CCA.Table} SET {CCA.RevokedAtUtc} = @t, {CCA.RevokedBy} = @a WHERE {CCA.UserId} = @u AND {CCA.CompanyId} = @c AND {CCA.RevokedAtUtc} IS NULL", conn, tx))
        {
            revCc.Parameters.AddWithValue("t", now);
            revCc.Parameters.AddWithValue("a", actorUserId);
            revCc.Parameters.AddWithValue("u", targetUserId);
            revCc.Parameters.AddWithValue("c", companyId);
            revoked += await revCc.ExecuteNonQueryAsync(ct);
        }
        await using (var mem = new NpgsqlCommand($"UPDATE {M.Table} SET {M.RevokedAtUtc} = @t, {M.RevokedBy} = @a WHERE {M.UserId} = @u AND {M.CompanyId} = @c AND {M.RevokedAtUtc} IS NULL", conn, tx))
        {
            mem.Parameters.AddWithValue("t", now);
            mem.Parameters.AddWithValue("a", actorUserId);
            mem.Parameters.AddWithValue("u", targetUserId);
            mem.Parameters.AddWithValue("c", companyId);
            await mem.ExecuteNonQueryAsync(ct);
        }
        await using (var ses = new NpgsqlCommand($"UPDATE {S.Table} SET {S.SelectedCompanyId} = NULL WHERE {S.UserId} = @u AND {S.SelectedCompanyId} = @c", conn, tx))
        {
            ses.Parameters.AddWithValue("u", targetUserId);
            ses.Parameters.AddWithValue("c", companyId);
            await ses.ExecuteNonQueryAsync(ct);
        }
        await WriteAuditAsync(conn, tx, actorUserId, companyId, targetUserId, null, AccessEvent.MembershipRevoked, $"assignments revoked: {revoked}", ct);
        await CommitAuditedAsync(tx, ct);
        return AccessResult<bool>.Ok(true);
    }

    // ------------------------------------------------------------------ assignments

    public async Task<AccessResult<IReadOnlyList<AssignmentView>>> ListAssignmentsAsync(Guid actorUserId, Guid companyId, Guid? userId = null, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        var actor = await LoadEffectiveAsync(conn, null, actorUserId, companyId, true, ct);
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.AssignmentsView)) return Denied<IReadOnlyList<AssignmentView>>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {A.Id}, {A.UserId}, {A.RoleId}, {A.ScopeKind}, {A.ScopeId}, {A.GrantedAtUtc}, COALESCE({A.ScopeEpoch}, {A.WarehouseScopeEpoch}) FROM {A.Table} " +
            $"WHERE {A.CompanyId} = @c AND {A.RevokedAtUtc} IS NULL AND (@u::uuid IS NULL OR {A.UserId} = @u) ORDER BY {A.GrantedAtUtc}, {A.Id}", conn);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        var list = new List<AssignmentView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AssignmentView(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), (ScopeKind)r.GetInt16(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetInt64(6)));
        await r.CloseAsync();
        await using (var ccList = new NpgsqlCommand(
            $"SELECT {CCA.Id}, {CCA.UserId}, {CCA.RoleId}, {CCA.CostCenterId}, {CCA.GrantedAtUtc}, {CCA.Epoch} FROM {CCA.Table} " +
            $"WHERE {CCA.CompanyId} = @c AND {CCA.RevokedAtUtc} IS NULL AND (@u::uuid IS NULL OR {CCA.UserId} = @u) ORDER BY {CCA.GrantedAtUtc}, {CCA.Id}", conn))
        {
            ccList.Parameters.AddWithValue("c", companyId);
            ccList.Parameters.Add(new NpgsqlParameter("u", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            await using var rc = await ccList.ExecuteReaderAsync(ct);
            while (await rc.ReadAsync(ct))
                list.Add(new AssignmentView(rc.GetGuid(0), rc.GetGuid(1), rc.GetGuid(2), ScopeKind.CostCenter, rc.GetGuid(3), rc.GetFieldValue<DateTimeOffset>(4), rc.GetInt64(5)));
        }
        return AccessResult<IReadOnlyList<AssignmentView>>.Ok(list);
    }

    /// <summary>Assigns a role to an active member with a scope. The actor must hold every permission of the role for that scope.</summary>
    public async Task<AccessResult<AssignmentView>> AssignRoleAsync(Guid actorUserId, Guid companyId, Guid targetUserId, Guid roleId, ScopeKind scopeKind, Guid? scopeId, CancellationToken ct = default)
    {
        var (conn, tx, actor) = await BeginAdminAsync(actorUserId, companyId, ct);
        await using var _c = conn; await using var _t = tx;
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.AssignmentsCreate)) return Denied<AssignmentView>();
        var scope = new ScopeTarget(scopeKind, scopeId);
        if (!Enum.IsDefined(scopeKind) || !scope.IsWellFormed) return AccessResult<AssignmentView>.Fail(AccessStatus.Invalid, "invalid_scope");
        // Fail closed: a Branch/Warehouse id is accepted only if the company data confirms it exists in this company. A branch must also be ACTIVE;
        // the activation epoch it has right now is recorded on the assignment (a later deactivation/reactivation makes the assignment inert for good).
        long? scopeEpoch = null;
        if (scopeKind != ScopeKind.Company)
        {
            var state = await ScopeStateAsync(companyId, scope, ct);
            if (state is null || (scopeKind != ScopeKind.Company && !state.Active))
                return AccessResult<AssignmentView>.Fail(AccessStatus.Invalid, "scope_not_verifiable");
            scopeEpoch = state.Epoch;   // Branch and Warehouse both record the activation epoch they were granted under
        }
        if (targetUserId == actorUserId) return Denied<AssignmentView>("cannot_modify_self");
        if (!await IsActiveMemberAsync(conn, tx, targetUserId, companyId, ct)) return AccessResult<AssignmentView>.Fail(AccessStatus.NotFound, "member_not_found");
        var role = await FindRoleAsync(conn, tx, companyId, roleId, ct);
        if (role is null || !role.IsActive) return AccessResult<AssignmentView>.Fail(AccessStatus.NotFound, "role_not_found");
        if (!actor.CanGrant(role.Permissions, scope)) return Denied<AssignmentView>("cannot_grant_permission");

        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        if (scopeKind == ScopeKind.CostCenter)
        {
            // Retire assignments of the same user, role and cost center recorded under an older activation epoch (already inert), then record the new one.
            await using (var stale = new NpgsqlCommand(
                $"UPDATE {CCA.Table} SET {CCA.RevokedAtUtc} = @t, {CCA.RevokedBy} = @a WHERE {CCA.UserId} = @u AND {CCA.CompanyId} = @c AND {CCA.RoleId} = @r AND {CCA.CostCenterId} = @s AND {CCA.RevokedAtUtc} IS NULL AND {CCA.Epoch} <> @e", conn, tx))
            {
                stale.Parameters.AddWithValue("t", now); stale.Parameters.AddWithValue("a", actorUserId); stale.Parameters.AddWithValue("u", targetUserId);
                stale.Parameters.AddWithValue("c", companyId); stale.Parameters.AddWithValue("r", roleId); stale.Parameters.AddWithValue("s", scopeId!.Value); stale.Parameters.AddWithValue("e", scopeEpoch!.Value);
                if (await stale.ExecuteNonQueryAsync(ct) > 0)
                    await WriteAuditAsync(conn, tx, actorUserId, companyId, targetUserId, roleId, AccessEvent.RoleAssignmentRevoked, $"{ScopeText(scope)} (stale activation epoch)", ct);
            }
            try
            {
                await using var insCc = new NpgsqlCommand(
                    $"INSERT INTO {CCA.Table} ({CCA.Id}, {CCA.UserId}, {CCA.CompanyId}, {CCA.RoleId}, {CCA.CostCenterId}, {CCA.Epoch}, {CCA.GrantedAtUtc}, {CCA.GrantedBy}) VALUES (@i, @u, @c, @r, @s, @e, @t, @a)", conn, tx);
                insCc.Parameters.AddWithValue("i", id); insCc.Parameters.AddWithValue("u", targetUserId); insCc.Parameters.AddWithValue("c", companyId); insCc.Parameters.AddWithValue("r", roleId);
                insCc.Parameters.AddWithValue("s", scopeId!.Value); insCc.Parameters.AddWithValue("e", scopeEpoch!.Value); insCc.Parameters.AddWithValue("t", now); insCc.Parameters.AddWithValue("a", actorUserId);
                await insCc.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
            {
                return AccessResult<AssignmentView>.Fail(AccessStatus.Conflict, "already_assigned");
            }
            await WriteAuditAsync(conn, tx, actorUserId, companyId, targetUserId, roleId, AccessEvent.RoleAssigned, ScopeText(scope), ct);
            await CommitAuditedAsync(tx, ct);
            return AccessResult<AssignmentView>.Ok(new AssignmentView(id, targetUserId, roleId, scopeKind, scopeId, now, scopeEpoch));
        }
        if (scopeKind is ScopeKind.Branch or ScopeKind.Warehouse)
        {
            // Retire assignments of the SAME user, role and branch recorded under an older epoch: they are already inert (never effective again)
            // and would otherwise block this explicit new assignment through the one-active-assignment index. Audited as revocations.
            var stale = new List<Guid>();
            await using (var find = new NpgsqlCommand(
                $"UPDATE {A.Table} SET {A.RevokedAtUtc} = @t, {A.RevokedBy} = @a WHERE {A.UserId} = @u AND {A.CompanyId} = @c AND {A.RoleId} = @r AND {A.ScopeKind} = @k AND {A.ScopeId} = @s " +
                $"AND {A.RevokedAtUtc} IS NULL AND COALESCE({A.ScopeEpoch}, {A.WarehouseScopeEpoch}) IS DISTINCT FROM @e RETURNING {A.Id}", conn, tx))
            {
                find.Parameters.AddWithValue("t", now); find.Parameters.AddWithValue("a", actorUserId); find.Parameters.AddWithValue("u", targetUserId);
                find.Parameters.AddWithValue("c", companyId); find.Parameters.AddWithValue("r", roleId); find.Parameters.AddWithValue("s", scopeId!.Value);
                find.Parameters.AddWithValue("e", scopeEpoch!.Value); find.Parameters.AddWithValue("k", (short)scopeKind);
                await using var rr = await find.ExecuteReaderAsync(ct);
                while (await rr.ReadAsync(ct)) stale.Add(rr.GetGuid(0));
            }
            foreach (var _ in stale)
                await WriteAuditAsync(conn, tx, actorUserId, companyId, targetUserId, roleId, AccessEvent.RoleAssignmentRevoked, $"{ScopeText(scope)} (stale activation epoch)", ct);
        }
        try
        {
            await using var ins = new NpgsqlCommand(
                $"INSERT INTO {A.Table} ({A.Id}, {A.UserId}, {A.CompanyId}, {A.RoleId}, {A.ScopeKind}, {A.ScopeId}, {A.ScopeEpoch}, {A.WarehouseScopeEpoch}, {A.GrantedAtUtc}, {A.GrantedBy}) VALUES (@i, @u, @c, @r, @k, @s, @eb, @ew, @t, @a)", conn, tx);
            ins.Parameters.Add(new NpgsqlParameter("eb", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = scopeKind == ScopeKind.Branch ? (object?)scopeEpoch ?? DBNull.Value : DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("ew", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = scopeKind == ScopeKind.Warehouse ? (object?)scopeEpoch ?? DBNull.Value : DBNull.Value });
            ins.Parameters.AddWithValue("i", id);
            ins.Parameters.AddWithValue("u", targetUserId);
            ins.Parameters.AddWithValue("c", companyId);
            ins.Parameters.AddWithValue("r", roleId);
            ins.Parameters.AddWithValue("k", (short)scopeKind);
            ins.Parameters.Add(new NpgsqlParameter("s", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)scopeId ?? DBNull.Value });
            ins.Parameters.AddWithValue("t", now);
            ins.Parameters.AddWithValue("a", actorUserId);
            await ins.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return AccessResult<AssignmentView>.Fail(AccessStatus.Conflict, "already_assigned");
        }
        await WriteAuditAsync(conn, tx, actorUserId, companyId, targetUserId, roleId, AccessEvent.RoleAssigned, ScopeText(scope), ct);
        await CommitAuditedAsync(tx, ct);
        return AccessResult<AssignmentView>.Ok(new AssignmentView(id, targetUserId, roleId, scopeKind, scopeId, now, scopeEpoch));
    }

    private static string ScopeText(ScopeTarget scope) => scope.Kind == ScopeKind.Company ? "scope: company" : $"scope: {scope.Kind.ToString().ToLowerInvariant()} {scope.Id}";

    /// <summary>Revokes one active assignment of this company. The actor must hold every permission of the assigned role for its scope.</summary>
    public async Task<AccessResult<bool>> RevokeAssignmentAsync(Guid actorUserId, Guid companyId, Guid assignmentId, CancellationToken ct = default)
    {
        var (conn, tx, actor) = await BeginAdminAsync(actorUserId, companyId, ct);
        await using var _c = conn; await using var _t = tx;
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.AssignmentsDelete)) return Denied<bool>();

        Guid userId, roleId; ScopeKind kind; Guid? scopeId;
        var costCenterAssignment = false;
        // The company filter makes assignments of other companies indistinguishable from nonexistent ones (no IDOR).
        await using (var find = new NpgsqlCommand(
            $"SELECT {A.UserId}, {A.RoleId}, {A.ScopeKind}, {A.ScopeId} FROM {A.Table} WHERE {A.Id} = @i AND {A.CompanyId} = @c AND {A.RevokedAtUtc} IS NULL " +
            $"UNION ALL SELECT {CCA.UserId}, {CCA.RoleId}, {(short)ScopeKind.CostCenter}::smallint, {CCA.CostCenterId} FROM {CCA.Table} WHERE {CCA.Id} = @i AND {CCA.CompanyId} = @c AND {CCA.RevokedAtUtc} IS NULL", conn, tx))
        {
            find.Parameters.AddWithValue("i", assignmentId);
            find.Parameters.AddWithValue("c", companyId);
            await using var r = await find.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return AccessResult<bool>.Fail(AccessStatus.NotFound, "assignment_not_found");
            userId = r.GetGuid(0); roleId = r.GetGuid(1); kind = (ScopeKind)r.GetInt16(2); scopeId = r.IsDBNull(3) ? null : r.GetGuid(3);
            costCenterAssignment = kind == ScopeKind.CostCenter;
        }
        if (userId == actorUserId) return Denied<bool>("cannot_modify_self");
        var role = await FindRoleAsync(conn, tx, companyId, roleId, ct);
        if (role is null || !actor.CanGrant(role.Permissions, new ScopeTarget(kind, scopeId))) return Denied<bool>("target_has_higher_privileges");
        if (role.PresetKey == PresetRoles.CompanyAdministratorKey && kind == ScopeKind.Company
            && await AdministratorContinuity.WouldRemoveLastAsync(conn, tx, companyId, null, assignmentId, ct))
            return AccessResult<bool>.Fail(AccessStatus.Conflict, "last_administrator");

        await using (var rev = new NpgsqlCommand(costCenterAssignment
            ? $"UPDATE {CCA.Table} SET {CCA.RevokedAtUtc} = @t, {CCA.RevokedBy} = @a WHERE {CCA.Id} = @i AND {CCA.RevokedAtUtc} IS NULL"
            : $"UPDATE {A.Table} SET {A.RevokedAtUtc} = @t, {A.RevokedBy} = @a WHERE {A.Id} = @i AND {A.RevokedAtUtc} IS NULL", conn, tx))
        {
            rev.Parameters.AddWithValue("t", clock.GetUtcNow());
            rev.Parameters.AddWithValue("a", actorUserId);
            rev.Parameters.AddWithValue("i", assignmentId);
            await rev.ExecuteNonQueryAsync(ct);
        }
        await WriteAuditAsync(conn, tx, actorUserId, companyId, userId, roleId, AccessEvent.RoleAssignmentRevoked, ScopeText(new ScopeTarget(kind, scopeId)), ct);
        await CommitAuditedAsync(tx, ct);
        return AccessResult<bool>.Ok(true);
    }

    // ------------------------------------------------------------------ safeguards

    /// <summary>Fail-closed scope check: any validator error or refusal means "not verifiable".</summary>
    /// <summary>Fail-closed state lookup: any validator error or refusal means "not verifiable" (null).</summary>
    private async Task<ScopeTargetState?> ScopeStateAsync(Guid companyId, ScopeTarget scope, CancellationToken ct)
    {
        try { return scope.Id is { } id ? await scopeValidator.GetStateAsync(companyId, scope.Kind, id, ct) : null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private async Task<bool> ScopeExistsAsync(Guid companyId, ScopeTarget scope, CancellationToken ct)
    {
        try { return scope.Id is { } id && await scopeValidator.ExistsAsync(companyId, scope.Kind, id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    // ------------------------------------------------------------------ audit

    public async Task<AccessResult<IReadOnlyList<AuditEntry>>> ListAuditAsync(Guid actorUserId, Guid companyId, int limit = 100, long? beforeId = null, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        var actor = await LoadEffectiveAsync(conn, null, actorUserId, companyId, true, ct);
        if (!actor.HasCompanyWide(PermissionCatalog.Codes.AuditView)) return Denied<IReadOnlyList<AuditEntry>>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Au.Id}, {Au.OccurredAtUtc}, {Au.ActorUserId}, {Au.TargetUserId}, {Au.RoleId}, {Au.EventCode}, {Au.Detail} FROM {Au.Table} " +
            $"WHERE {Au.CompanyId} = @c AND (@b::bigint IS NULL OR {Au.Id} < @b) ORDER BY {Au.Id} DESC LIMIT @l", conn);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)beforeId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, MaxPageSize));
        var list = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AuditEntry(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3),
                r.IsDBNull(4) ? null : r.GetGuid(4), (AccessEvent)r.GetInt16(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return AccessResult<IReadOnlyList<AuditEntry>>.Ok(list);
    }

    // ------------------------------------------------------------------ initial administration (offline only)

    public enum BootstrapStatus { Granted, AlreadyAdministrator, UserNotFound, CompanyUnavailable }

    /// <summary>
    /// Offline initial/recovery administration: makes an active user a member of a ready company with the preset
    /// Company Administrator role (company-wide). There is no HTTP endpoint for this; it is reachable only through the
    /// administrative tool with the privileged database account. Audited with no actor user. Safe to repeat.
    /// </summary>
    public async Task<BootstrapStatus> BootstrapCompanyAdministratorAsync(string login, Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AdministratorContinuity.LockCompanyAsync(conn, tx, companyId, ct);
        if (!LoginName.TryNormalize(login, out var normalized)) return BootstrapStatus.UserNotFound;
        var member = await GrantCoreAsync(conn, tx, null, companyId, normalized, ct);
        if (!member.IsOk && member.Code != "already_member")
            return member.Code == "company_unavailable" ? BootstrapStatus.CompanyUnavailable : BootstrapStatus.UserNotFound;

        Guid userId;
        await using (var find = new NpgsqlCommand($"SELECT {U.Id} FROM {U.Table} WHERE {U.Login} = @l", conn, tx))
        {
            find.Parameters.AddWithValue("l", normalized);
            userId = (Guid)(await find.ExecuteScalarAsync(ct))!;
        }
        await using var ins = new NpgsqlCommand(
            $"INSERT INTO {A.Table} ({A.Id}, {A.UserId}, {A.CompanyId}, {A.RoleId}, {A.ScopeKind}, {A.ScopeId}, {A.GrantedAtUtc}, {A.GrantedBy}) VALUES (@i, @u, @c, @r, 0, NULL, @t, NULL) " +
            "ON CONFLICT DO NOTHING", conn, tx);
        ins.Parameters.AddWithValue("i", Guid.NewGuid());
        ins.Parameters.AddWithValue("u", userId);
        ins.Parameters.AddWithValue("c", companyId);
        ins.Parameters.AddWithValue("r", PresetRoles.CompanyAdministratorId);
        ins.Parameters.AddWithValue("t", clock.GetUtcNow());
        var inserted = await ins.ExecuteNonQueryAsync(ct) == 1;
        if (inserted) await WriteAuditAsync(conn, tx, null, companyId, userId, PresetRoles.CompanyAdministratorId, AccessEvent.RoleAssigned, "scope: company; bootstrap", ct);
        await CommitAuditedAsync(tx, ct);
        return inserted ? BootstrapStatus.Granted : BootstrapStatus.AlreadyAdministrator;
    }

    // ------------------------------------------------------------------ fiscal approver delegation (offline only)

    public enum FiscalApproverStatus { Granted, AlreadyApprover, Revoked, NotApprover, UserNotFound, NotMember, CompanyUnavailable }

    /// <summary>
    /// Offline delegation of the preset Fiscal Approver role (company-wide). It is the ONLY way to create the first approver of a company and the recovery
    /// procedure for a company with none, because no company user can grant it: the service refuses to grant permissions the actor does not hold, and the
    /// Company Administrator preset deliberately lacks <c>company.fiscalreopen.approve</c>. Reachable only through the administrative tool with the
    /// privileged database account (no HTTP endpoint); the target must already be an ACTIVE member of a Ready company (membership is never created here);
    /// audited with no actor user. Per-request separation of duties still applies: an approver can never approve their own request.
    /// </summary>
    private async Task<FiscalApproverStatus> GrantPresetApproverOfflineAsync(string login, Guid companyId, Guid roleId, string label, CancellationToken ct)
    {
        if (!LoginName.TryNormalize(login, out var normalized)) return FiscalApproverStatus.UserNotFound;
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AdministratorContinuity.LockCompanyAsync(conn, tx, companyId, ct);
        var (userId, status) = await FindMemberForDelegationAsync(conn, tx, normalized, companyId, ct);
        if (userId is null) return status;
        await using var ins = new NpgsqlCommand(
            $"INSERT INTO {A.Table} ({A.Id}, {A.UserId}, {A.CompanyId}, {A.RoleId}, {A.ScopeKind}, {A.ScopeId}, {A.GrantedAtUtc}, {A.GrantedBy}) VALUES (@i, @u, @c, @r, 0, NULL, @t, NULL) ON CONFLICT DO NOTHING", conn, tx);
        ins.Parameters.AddWithValue("i", Guid.NewGuid()); ins.Parameters.AddWithValue("u", userId.Value); ins.Parameters.AddWithValue("c", companyId);
        ins.Parameters.AddWithValue("r", roleId); ins.Parameters.AddWithValue("t", clock.GetUtcNow());
        var inserted = await ins.ExecuteNonQueryAsync(ct) == 1;
        if (inserted) await WriteAuditAsync(conn, tx, null, companyId, userId.Value, roleId, AccessEvent.RoleAssigned, $"scope: company; offline {label} delegation", ct);
        await CommitAuditedAsync(tx, ct);
        return inserted ? FiscalApproverStatus.Granted : FiscalApproverStatus.AlreadyApprover;
    }

    /// <summary>Offline removal of the Fiscal Approver role (revoked, never deleted; audited with no actor user). Safe to repeat.</summary>
    private async Task<FiscalApproverStatus> RevokePresetApproverOfflineAsync(string login, Guid companyId, Guid roleId, string label, CancellationToken ct)
    {
        if (!LoginName.TryNormalize(login, out var normalized)) return FiscalApproverStatus.UserNotFound;
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AdministratorContinuity.LockCompanyAsync(conn, tx, companyId, ct);
        Guid? userId;
        await using (var find = new NpgsqlCommand($"SELECT u.{U.Id} FROM {U.Table} u JOIN {C.Table} c ON c.{C.Id} = @c WHERE u.{U.Login} = @l AND c.{C.ProvisioningState} = {(short)ProvisioningState.Ready}", conn, tx))
        {
            find.Parameters.AddWithValue("l", normalized); find.Parameters.AddWithValue("c", companyId);
            userId = await find.ExecuteScalarAsync(ct) as Guid?;
        }
        if (userId is null) return FiscalApproverStatus.UserNotFound;
        await using var rev = new NpgsqlCommand(
            $"UPDATE {A.Table} SET {A.RevokedAtUtc} = @t WHERE {A.UserId} = @u AND {A.CompanyId} = @c AND {A.RoleId} = @r AND {A.RevokedAtUtc} IS NULL", conn, tx);
        rev.Parameters.AddWithValue("t", clock.GetUtcNow()); rev.Parameters.AddWithValue("u", userId.Value);
        rev.Parameters.AddWithValue("c", companyId); rev.Parameters.AddWithValue("r", roleId);
        var revoked = await rev.ExecuteNonQueryAsync(ct) > 0;
        if (revoked) await WriteAuditAsync(conn, tx, null, companyId, userId.Value, roleId, AccessEvent.RoleAssignmentRevoked, $"scope: company; offline {label} removal", ct);
        await CommitAuditedAsync(tx, ct);
        return revoked ? FiscalApproverStatus.Revoked : FiscalApproverStatus.NotApprover;
    }

    public Task<FiscalApproverStatus> GrantFiscalApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => GrantPresetApproverOfflineAsync(login, companyId, PresetRoles.FiscalApproverId, "fiscal approver", ct);
    public Task<FiscalApproverStatus> RevokeFiscalApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => RevokePresetApproverOfflineAsync(login, companyId, PresetRoles.FiscalApproverId, "fiscal approver", ct);

    /// <summary>Offline delegation of the preset Journal Approver role (company-wide), exactly like the Fiscal Approver: no company user can grant journal approval (the administrator lacks it), so the first approver comes from the administrative tool. Per-entry separation of duties still applies.</summary>
    public Task<FiscalApproverStatus> GrantJournalApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => GrantPresetApproverOfflineAsync(login, companyId, PresetRoles.JournalApproverId, "journal approver", ct);
    public Task<FiscalApproverStatus> GrantImportApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => GrantPresetApproverOfflineAsync(login, companyId, PresetRoles.ImportApproverId, "import approver", ct);
    public Task<FiscalApproverStatus> RevokeImportApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => RevokePresetApproverOfflineAsync(login, companyId, PresetRoles.ImportApproverId, "import approver", ct);
    /// <summary>Offline delegation of the preset Commercial Approver role (company-wide), like the journal and import approvers: no company user can grant commercial approval (the administrator lacks it).</summary>
    public Task<FiscalApproverStatus> GrantCommercialApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => GrantPresetApproverOfflineAsync(login, companyId, PresetRoles.CommercialApproverId, "commercial approver", ct);
    /// <summary>Offline delegation of the preset Retention Approver role (company-wide; a SENSITIVE grant, D-080 item 6): no company user can grant it (the administrator lacks the permission), so it comes from the administrative tool.</summary>
    public Task<FiscalApproverStatus> GrantRetentionApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => GrantPresetApproverOfflineAsync(login, companyId, PresetRoles.RetentionApproverId, "retention approver", ct);
    public Task<FiscalApproverStatus> RevokeRetentionApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => RevokePresetApproverOfflineAsync(login, companyId, PresetRoles.RetentionApproverId, "retention approver", ct);
    public Task<FiscalApproverStatus> RevokeCommercialApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => RevokePresetApproverOfflineAsync(login, companyId, PresetRoles.CommercialApproverId, "commercial approver", ct);
    public Task<FiscalApproverStatus> RevokeJournalApproverOfflineAsync(string login, Guid companyId, CancellationToken ct = default) => RevokePresetApproverOfflineAsync(login, companyId, PresetRoles.JournalApproverId, "journal approver", ct);

    private static async Task<(Guid? UserId, FiscalApproverStatus Status)> FindMemberForDelegationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string login, Guid companyId, CancellationToken ct)
    {
        await using (var ready = new NpgsqlCommand($"SELECT 1 FROM {C.Table} WHERE {C.Id} = @c AND {C.ProvisioningState} = {(short)ProvisioningState.Ready}", conn, tx))
        {
            ready.Parameters.AddWithValue("c", companyId);
            if (await ready.ExecuteScalarAsync(ct) is null) return (null, FiscalApproverStatus.CompanyUnavailable);
        }
        Guid? userId;
        await using (var find = new NpgsqlCommand($"SELECT {U.Id} FROM {U.Table} WHERE {U.Login} = @l AND {U.State} = 0", conn, tx))
        {
            find.Parameters.AddWithValue("l", login);
            userId = await find.ExecuteScalarAsync(ct) as Guid?;
        }
        if (userId is null) return (null, FiscalApproverStatus.UserNotFound);
        return await IsActiveMemberAsync(conn, tx, userId.Value, companyId, ct) ? (userId, FiscalApproverStatus.Granted) : (null, FiscalApproverStatus.NotMember);
    }
}
