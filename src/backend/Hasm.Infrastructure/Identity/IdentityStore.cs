using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using T = Hasm.Infrastructure.SystemDatabase.SystemSchema.LoginThrottle;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using RA = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using RL = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using RP = Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission;

namespace Hasm.Infrastructure.Identity;

public enum UserState : short { Active = 0, Disabled = 1, Locked = 2 }
public enum SessionClient : short { Web = 0, Desktop = 1 }
public enum SessionRevocation : short { Logout = 0, Administrative = 1, PasswordChanged = 2 }
public enum ThrottleScope : short { Login = 0, Source = 1 }

public enum SetStateOutcome { Changed, NotFound, LastCompanyAdministrator, LastIdentityAdministrator, ActorNotActive }

public sealed record UserRecord(
    Guid Id, string Login, string DisplayName, string PasswordHash, UserState State, bool IsIdentityAdministrator,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? LastLoginAtUtc);

/// <summary>An active session joined with the current state of its user.</summary>
public sealed record ActiveSession(
    Guid SessionId, Guid UserId, SessionClient Client, DateTimeOffset IdleExpiresAtUtc, DateTimeOffset AbsoluteExpiresAtUtc, DateTimeOffset LastSeenAtUtc,
    string Login, string DisplayName, bool IsIdentityAdministrator, DateTimeOffset? MfaVerifiedAtUtc = null, MfaState MfaState = MfaState.None, Guid? SelectedCompanyId = null);

/// <summary>A session to create: its id, the hash of its token, the client, its expiries and (after the second factor) the verification time.</summary>
public sealed record SessionIssue(Guid SessionId, byte[] TokenHash, SessionClient Client, DateTimeOffset IdleExpires, DateTimeOffset AbsoluteExpires, DateTimeOffset? MfaVerifiedAt);

public sealed class DuplicateLoginException() : Exception("The login identifier is already in use.");

/// <summary>
/// PostgreSQL data access for the central identity tables of the system database. All statements are
/// parameterized. Uses the connection string it is given (the Backend passes the runtime, least-privilege account);
/// connections are pooled through a lazily created data source.
/// </summary>
public sealed class IdentityStore(Func<string> connectionStringFactory, TimeProvider clock, AuditTrail audit) : IThrottleStore, IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> dataSource = new(() => NpgsqlDataSource.Create(connectionStringFactory()));
    // Arbitrary constant key for "initial administrator bootstrap" (transaction-scoped advisory lock).
    private const long BootstrapLockKey = 0x4841534D_0002;

    public IdentityStore(string connectionString, TimeProvider clock, AuditTrail audit) : this(() => connectionString, clock, audit) { }

    public DateTimeOffset UtcNow => clock.GetUtcNow();

    public ValueTask DisposeAsync() => dataSource.IsValueCreated ? dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;

    private static string UserColumns =>
        $"{U.Id}, {U.Login}, {U.DisplayName}, {U.PasswordHash}, {U.State}, {U.IsIdentityAdministrator}, {U.CreatedAtUtc}, {U.LastLoginAtUtc}";

    private static UserRecord ReadUser(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), (UserState)r.GetInt16(4), r.GetBoolean(5),
        r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7));

    // ---- Users ----

    public async Task<UserRecord> CreateUserAsync(string normalizedLogin, string displayName, string passwordHash, bool isAdministrator, Guid? actorUserId = null, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var user = await InsertUserAsync(conn, tx, normalizedLogin, displayName, passwordHash, isAdministrator, ct);
        // The user and its audit event commit together; if the event cannot be written the user is not created.
        var receipt = await AuditAsync(conn, tx, AuditEventTypes.UserCreated, actorUserId, user.Id, $"administrator: {(isAdministrator ? "yes" : "no")}", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return user;
    }

    private Task<AuditReceipt> AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, short eventType, Guid? actor, Guid? target, string? detail, CancellationToken ct) =>
        audit.AppendAsync(conn, tx, AuditStream.System, AuditCanonical.FromMicros(AuditCanonical.ToMicros(clock.GetUtcNow())),
            new AuditEventData(eventType, actor, target, null, detail), ct);

    /// <summary>
    /// Creates the first identity administrator, atomically refusing when one already exists (serialized by an
    /// advisory lock so concurrent bootstraps cannot both succeed). Returns null when an administrator exists.
    /// </summary>
    public async Task<UserRecord?> CreateInitialAdministratorAsync(string normalizedLogin, string displayName, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lck = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, tx))
        {
            lck.Parameters.AddWithValue("k", BootstrapLockKey);
            await lck.ExecuteNonQueryAsync(ct);
        }
        await using (var exists = new NpgsqlCommand($"SELECT 1 FROM {U.Table} WHERE {U.IsIdentityAdministrator} LIMIT 1", conn, tx))
        {
            if (await exists.ExecuteScalarAsync(ct) is not null) return null;
        }
        var user = await InsertUserAsync(conn, tx, normalizedLogin, displayName, passwordHash, true, ct);
        var receipt = await AuditAsync(conn, tx, AuditEventTypes.InitialAdministratorCreated, null, user.Id, null, ct);
        await audit.CommitAsync(tx, receipt, ct);
        return user;
    }

    private async Task<UserRecord> InsertUserAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string login, string displayName, string hash, bool admin, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {U.Table} ({U.Id},{U.Login},{U.DisplayName},{U.PasswordHash},{U.State},{U.IsIdentityAdministrator},{U.CreatedAtUtc},{U.StateChangedAtUtc},{U.PasswordChangedAtUtc}) " +
            "VALUES (@id,@l,@d,@h,0,@a,@n,@n,@n)", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("l", login);
        cmd.Parameters.AddWithValue("d", displayName);
        cmd.Parameters.AddWithValue("h", hash);
        cmd.Parameters.AddWithValue("a", admin);
        cmd.Parameters.AddWithValue("n", now);
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == U.UniqueLogin)
        {
            throw new DuplicateLoginException();
        }
        return new UserRecord(id, login, displayName, hash, UserState.Active, admin, now, null);
    }

    public Task<UserRecord?> FindUserByLoginAsync(string normalizedLogin, CancellationToken ct = default) =>
        FindUserAsync($"{U.Login} = @v", normalizedLogin, ct);

    public Task<UserRecord?> FindUserByIdAsync(Guid id, CancellationToken ct = default) =>
        FindUserAsync($"{U.Id} = @v", id, ct);

    private async Task<UserRecord?> FindUserAsync(string where, object value, CancellationToken ct)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {UserColumns} FROM {U.Table} WHERE {where}", conn);
        cmd.Parameters.AddWithValue("v", value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadUser(r) : null;
    }

    public async Task<bool> AnyIdentityAdministratorAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT 1 FROM {U.Table} WHERE {U.IsIdentityAdministrator} LIMIT 1", conn);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Changes the lifecycle state; any non-Active state revokes all of the user's sessions atomically. Moving a user out of
    /// Active is refused when it would leave a Ready company without a usable Company Administrator
    /// (<see cref="SetStateOutcome.LastCompanyAdministrator"/>) or the system without an Active identity administrator
    /// (<see cref="SetStateOutcome.LastIdentityAdministrator"/>). The guards run in one transaction under the same per-company
    /// advisory lock as the access service plus an identity-administrator lock, so concurrent disables, locks and
    /// revocations serialize and the later one sees the earlier one's effect. When <paramref name="actorUserId"/> is given, the
    /// actor must still be Active inside the lock (mutual disabling by two administrators cannot both succeed).
    /// Re-activating a user is never blocked. A company that already has no administrator does not block anything.
    /// </summary>
    public async Task<SetStateOutcome> SetStateAsync(Guid? actorUserId, Guid userId, UserState state, CancellationToken ct = default)
    {
        var companyLocks = new SortedSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();

            if (state != UserState.Active)
            {
                await using (var lck = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 4))", conn, tx))
                {
                    lck.Parameters.AddWithValue("k", "hasm.identity.administrators");
                    await lck.ExecuteNonQueryAsync(ct);
                }
                foreach (var key in companyLocks) await AdministratorContinuity.LockCompanyAsync(conn, tx, Guid.ParseExact(key, "N"), ct);
            }

            if (actorUserId is { } actor && (await ReadStateAsync(conn, tx, actor, ct)).State != UserState.Active)
                return SetStateOutcome.ActorNotActive;
            var (current, isIdentityAdmin) = await ReadStateAsync(conn, tx, userId, ct);
            if (current is null)
                return SetStateOutcome.NotFound;

            if (state != UserState.Active && current == UserState.Active)
            {
                if (isIdentityAdmin && await CountActiveIdentityAdministratorsAsync(conn, tx, exclude: userId, ct) == 0)
                    return SetStateOutcome.LastIdentityAdministrator;

                // Companies this user administers: lock them all (sorted) and re-check; a membership granted meanwhile adds a
                // company, in which case start over holding the larger lock set (no lock is ever taken out of order).
                var administered = await AdministratorContinuity.CompaniesAdministeredAsync(conn, tx, userId, ct);
                var missing = administered.Where(c => !companyLocks.Contains(c.ToString("N"))).ToList();
                if (missing.Count > 0)
                {
                    foreach (var c in missing) companyLocks.Add(c.ToString("N"));
                    continue; // transaction disposed without commit: locks released, retried with the full set
                }
                foreach (var company in administered)
                    if (await AdministratorContinuity.WouldRemoveLastAsync(conn, tx, company, userId, null, ct))
                        return SetStateOutcome.LastCompanyAdministrator;
            }

            await using (var cmd = new NpgsqlCommand(
                $"UPDATE {U.Table} SET {U.State} = @s, {U.StateChangedAtUtc} = @n WHERE {U.Id} = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("s", (short)state);
                cmd.Parameters.AddWithValue("n", now);
                cmd.Parameters.AddWithValue("id", userId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (state != UserState.Active) await RevokeUserSessionsAsync(conn, tx, userId, SessionRevocation.Administrative, null, now, ct);
            var receipt = await AuditAsync(conn, tx, AuditEventTypes.UserStateChanged, actorUserId, userId, $"state: {state}", ct);
            await audit.CommitAsync(tx, receipt, ct);
            return SetStateOutcome.Changed;
        }
        throw new InvalidOperationException("Administrator continuity locking did not converge.");
    }

    private static async Task<(UserState? State, bool IsIdentityAdministrator)> ReadStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {U.State}, {U.IsIdentityAdministrator} FROM {U.Table} WHERE {U.Id} = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ((UserState)r.GetInt16(0), r.GetBoolean(1)) : (null, false);
    }

    private static async Task<long> CountActiveIdentityAdministratorsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid exclude, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {U.Table} WHERE {U.IsIdentityAdministrator} AND {U.State} = 0 AND {U.Id} <> @x", conn, tx);
        cmd.Parameters.AddWithValue("x", exclude);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Replaces the stored hash. <paramref name="passwordChanged"/> false = transparent rehash with the same password.</summary>
    public async Task UpdatePasswordHashAsync(Guid userId, string hash, bool passwordChanged, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {U.Table} SET {U.PasswordHash} = @h" + (passwordChanged ? $", {U.PasswordChangedAtUtc} = @n" : "") + $" WHERE {U.Id} = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("h", hash);
            cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("id", userId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // A real password change is a security event (a transparent rehash with the same password is not).
        if (!passwordChanged) { await tx.CommitAsync(ct); return; }
        var receipt = await AuditAsync(conn, tx, AuditEventTypes.PasswordChanged, userId, userId, null, ct);
        await audit.CommitAsync(tx, receipt, ct);
    }

    // ---- Sessions ----

    public async Task CreateSessionAsync(Guid sessionId, Guid userId, byte[] tokenHash, SessionClient client, DateTimeOffset idleExpires, DateTimeOffset absoluteExpires, DateTimeOffset? mfaVerifiedAt = null, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await InsertSessionAsync(conn, tx, new SessionIssue(sessionId, tokenHash, client, idleExpires, absoluteExpires, mfaVerifiedAt), userId, clock.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Inserts a session (and records the login time) inside the CALLER's transaction, so a session can be created atomically with another change.</summary>
    internal static async Task InsertSessionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SessionIssue issue, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var (sessionId, tokenHash, client, idleExpires, absoluteExpires, mfaVerifiedAt) = (issue.SessionId, issue.TokenHash, issue.Client, issue.IdleExpires, issue.AbsoluteExpires, issue.MfaVerifiedAt);
        // a session created after the second factor records it: auth_methods 3 (password + totp) with the verification time; otherwise the column defaults (1, password only)
        var mfaColumns = mfaVerifiedAt is null ? "" : $",{SM.AuthMethods},{SM.VerifiedAtUtc}";
        var mfaValues = mfaVerifiedAt is null ? "" : ",3,@mv";
        await using (var cmd = new NpgsqlCommand(
            $"INSERT INTO {S.Table} ({S.Id},{S.UserId},{S.TokenHash},{S.CreatedAtUtc},{S.LastSeenAtUtc},{S.IdleExpiresAtUtc},{S.AbsoluteExpiresAtUtc},{S.ClientKind}{mfaColumns}) " +
            $"VALUES (@id,@u,@t,@n,@n,@i,@a,@c{mfaValues})", conn, tx))
        {
            if (mfaVerifiedAt is { } mv) cmd.Parameters.AddWithValue("mv", mv);
            cmd.Parameters.AddWithValue("id", sessionId);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("t", tokenHash);
            cmd.Parameters.AddWithValue("n", now);
            cmd.Parameters.AddWithValue("i", idleExpires);
            cmd.Parameters.AddWithValue("a", absoluteExpires);
            cmd.Parameters.AddWithValue("c", (short)client);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var login = new NpgsqlCommand($"UPDATE {U.Table} SET {U.LastLoginAtUtc} = @n WHERE {U.Id} = @u", conn, tx))
        {
            login.Parameters.AddWithValue("n", now);
            login.Parameters.AddWithValue("u", userId);
            await login.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The MFA state of a user (None when the user does not exist). Read by the login to refuse a password-only session when no second-factor services are wired.</summary>
    public async Task<MfaState> GetMfaStateAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {M.State} FROM {U.Table} WHERE {U.Id} = @u", conn);
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteScalarAsync(ct) is short s ? (MfaState)s : MfaState.None;
    }

    /// <summary>
    /// The user's CENTRAL MFA requirement (Phase 4.1 section 3, D-080 (1), D-165): identity administrator, an active Company Administrator preset, or any active role holding a privileged
    /// permission, in ANY company (never only the selected one). Read from the database on every call. A user that does not exist has no requirement (and no session).
    /// </summary>
    public async Task<MfaRequirement> GetMfaRequirementAsync(Guid userId, CancellationToken ct = default)
    {
        var reasons = MfaMandatoryReason.None;
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand($"SELECT {U.IsIdentityAdministrator} FROM {U.Table} WHERE {U.Id} = @u", conn))
        {
            cmd.Parameters.AddWithValue("u", userId);
            if (await cmd.ExecuteScalarAsync(ct) is true) reasons |= MfaMandatoryReason.IdentityAdministrator;
        }
        var roles = new Dictionary<Guid, (string? Preset, List<string> Permissions)>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT r.{RL.Id}, r.{RL.PresetKey}, p.{RP.Permission} FROM {RA.Table} a JOIN {RL.Table} r ON r.{RL.Id} = a.{RA.RoleId} AND r.{RL.IsActive} " +
            $"LEFT JOIN {RP.Table} p ON p.{RP.RoleId} = r.{RL.Id} WHERE a.{RA.UserId} = @u AND a.{RA.RevokedAtUtc} IS NULL", conn))
        {
            cmd.Parameters.AddWithValue("u", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                if (!roles.TryGetValue(id, out var entry)) roles[id] = entry = (reader.IsDBNull(1) ? null : reader.GetString(1), []);
                if (!reader.IsDBNull(2)) entry.Permissions.Add(reader.GetString(2));
            }
        }
        foreach (var (preset, permissions) in roles.Values)
        {
            if (preset == PresetRoles.CompanyAdministratorKey) reasons |= MfaMandatoryReason.CompanyAdministratorPreset;
            if (MfaClassification.IsPrivilegedRole(preset, permissions)) reasons |= MfaMandatoryReason.PrivilegedRole;
        }
        return new MfaRequirement(reasons);
    }

    /// <summary>
    /// Finds a session that is not revoked, not idle-expired, not absolute-expired and whose user is Active.
    /// The user's current state and administrator flag are read on every call, so changes apply immediately.
    /// </summary>
    public async Task<ActiveSession?> FindActiveSessionAsync(byte[] tokenHash, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT s.{S.Id}, s.{S.UserId}, s.{S.ClientKind}, s.{S.IdleExpiresAtUtc}, s.{S.AbsoluteExpiresAtUtc}, s.{S.LastSeenAtUtc}, u.{U.Login}, u.{U.DisplayName}, u.{U.IsIdentityAdministrator}, s.{SM.VerifiedAtUtc}, u.{M.State}, s.{S.SelectedCompanyId} " +
            $"FROM {S.Table} s JOIN {U.Table} u ON u.{U.Id} = s.{S.UserId} " +
            $"WHERE s.{S.TokenHash} = @t AND s.{S.RevokedAtUtc} IS NULL AND s.{S.IdleExpiresAtUtc} > @n AND s.{S.AbsoluteExpiresAtUtc} > @n AND u.{U.State} = 0", conn);
        cmd.Parameters.AddWithValue("t", tokenHash);
        cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ActiveSession(
            r.GetGuid(0), r.GetGuid(1), (SessionClient)r.GetInt16(2),
            r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5),
            r.GetString(6), r.GetString(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), (MfaState)r.GetInt16(10), r.IsDBNull(11) ? null : r.GetGuid(11));
    }

    /// <summary>Records activity and extends the idle expiry (capped at the absolute expiry) of a live session.</summary>
    public async Task TouchSessionAsync(Guid sessionId, DateTimeOffset newIdleExpiry, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {S.Table} SET {S.LastSeenAtUtc} = @n, {S.IdleExpiresAtUtc} = LEAST(@i, {S.AbsoluteExpiresAtUtc}) " +
            $"WHERE {S.Id} = @id AND {S.RevokedAtUtc} IS NULL", conn);
        cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("i", newIdleExpiry);
        cmd.Parameters.AddWithValue("id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokeSessionAsync(Guid sessionId, SessionRevocation reason, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {S.Table} SET {S.RevokedAtUtc} = @n, {S.RevokedReason} = @r WHERE {S.Id} = @id AND {S.RevokedAtUtc} IS NULL", conn);
        cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("r", (short)reason);
        cmd.Parameters.AddWithValue("id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> RevokeUserSessionsAsync(Guid userId, SessionRevocation reason, Guid? exceptSessionId = null, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        return await RevokeUserSessionsAsync(conn, null, userId, reason, exceptSessionId, clock.GetUtcNow(), ct);
    }

    internal static async Task<int> RevokeUserSessionsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid userId, SessionRevocation reason, Guid? except, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {S.Table} SET {S.RevokedAtUtc} = @n, {S.RevokedReason} = @r WHERE {S.UserId} = @u AND {S.RevokedAtUtc} IS NULL" +
            (except is null ? "" : $" AND {S.Id} <> @x"), conn, tx);
        cmd.Parameters.AddWithValue("n", now);
        cmd.Parameters.AddWithValue("r", (short)reason);
        cmd.Parameters.AddWithValue("u", userId);
        if (except is not null) cmd.Parameters.AddWithValue("x", except.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Failed-login throttling ----

    /// <summary>Latest block expiry among the given keys that is still in the future, or null when none is blocked.</summary>
    public async Task<DateTimeOffset?> GetBlockedUntilAsync(IReadOnlyList<(ThrottleScope Scope, byte[] KeyHash)> keys, CancellationToken ct = default)
    {
        DateTimeOffset? latest = null;
        var now = clock.GetUtcNow();
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        foreach (var (scope, key) in keys)
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT {T.BlockedUntilUtc} FROM {T.Table} WHERE {T.Scope} = @s AND {T.KeyHash} = @k AND {T.BlockedUntilUtc} > @n", conn);
            cmd.Parameters.AddWithValue("s", (short)scope);
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("n", now);
            if (await cmd.ExecuteScalarAsync(ct) is DateTime until)
            {
                var value = new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc));
                if (latest is null || value > latest) latest = value;
            }
        }
        return latest;
    }

    /// <summary>Counts a failure (one atomic upsert). Returns true when this failure caused or kept a block.</summary>
    public async Task<bool> RecordFailureAsync(ThrottleScope scope, byte[] keyHash, int limit, TimeSpan window, TimeSpan block, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        var expiredSql = $"t.{T.WindowStartedAtUtc} + make_interval(secs => @w) <= @n";
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {T.Table} AS t ({T.Scope},{T.KeyHash},{T.Failures},{T.WindowStartedAtUtc},{T.BlockedUntilUtc}) " +
            "VALUES (@s,@k,1,@n,CASE WHEN @l <= 1 THEN @n + make_interval(secs => @b) END) " +
            $"ON CONFLICT ({T.Scope},{T.KeyHash}) DO UPDATE SET " +
            $"{T.Failures} = CASE WHEN {expiredSql} THEN 1 ELSE t.{T.Failures} + 1 END, " +
            $"{T.WindowStartedAtUtc} = CASE WHEN {expiredSql} THEN @n ELSE t.{T.WindowStartedAtUtc} END, " +
            $"{T.BlockedUntilUtc} = CASE WHEN (CASE WHEN {expiredSql} THEN 1 ELSE t.{T.Failures} + 1 END) >= @l THEN @n + make_interval(secs => @b) ELSE t.{T.BlockedUntilUtc} END " +
            $"RETURNING {T.BlockedUntilUtc} > @n", conn);
        cmd.Parameters.AddWithValue("s", (short)scope);
        cmd.Parameters.AddWithValue("k", keyHash);
        cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("l", limit);
        cmd.Parameters.AddWithValue("w", window.TotalSeconds);
        cmd.Parameters.AddWithValue("b", block.TotalSeconds);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task ResetThrottleAsync(ThrottleScope scope, byte[] keyHash, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"DELETE FROM {T.Table} WHERE {T.Scope} = @s AND {T.KeyHash} = @k", conn);
        cmd.Parameters.AddWithValue("s", (short)scope);
        cmd.Parameters.AddWithValue("k", keyHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Housekeeping: removes long-expired/revoked sessions and stale throttle rows. Returns rows removed.</summary>
    public async Task<int> PurgeAsync(TimeSpan sessionRetention, TimeSpan throttleRetention, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var sessions = new NpgsqlCommand(
            $"DELETE FROM {S.Table} WHERE {S.AbsoluteExpiresAtUtc} < @c OR {S.IdleExpiresAtUtc} < @c OR ({S.RevokedAtUtc} IS NOT NULL AND {S.RevokedAtUtc} < @c)", conn);
        sessions.Parameters.AddWithValue("c", now - sessionRetention);
        var removed = await sessions.ExecuteNonQueryAsync(ct);
        await using var throttle = new NpgsqlCommand(
            $"DELETE FROM {T.Table} WHERE {T.WindowStartedAtUtc} < @c AND ({T.BlockedUntilUtc} IS NULL OR {T.BlockedUntilUtc} < @n)", conn);
        throttle.Parameters.AddWithValue("c", now - throttleRetention);
        throttle.Parameters.AddWithValue("n", now);
        return removed + await throttle.ExecuteNonQueryAsync(ct);
    }
}
