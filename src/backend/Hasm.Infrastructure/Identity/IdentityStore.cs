using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SI = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionIntegrity;
using UI = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity;
using UT = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag;
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

/// <summary>A session to create: its id, its plaintext token (hashed under the token key by the one insert primitive, never stored), the client, its expiries and (after the second factor) the verification time.</summary>
public sealed record SessionIssue(Guid SessionId, string Token, SessionClient Client, DateTimeOffset IdleExpires, DateTimeOffset AbsoluteExpires, DateTimeOffset? MfaVerifiedAt);

public sealed class DuplicateLoginException() : Exception("The login identifier is already in use.");

/// <summary>
/// PostgreSQL data access for the central identity tables of the system database. All statements are
/// parameterized. Uses the connection string it is given (the Backend passes the runtime, least-privilege account);
/// connections are pooled through a lazily created data source.
/// </summary>
public sealed class IdentityStore(Func<string> connectionStringFactory, TimeProvider clock, AuditTrail audit, IdentityKeys? keys = null) : IThrottleStore, IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> dataSource = new(() => NpgsqlDataSource.Create(connectionStringFactory()));
    // Arbitrary constant key for "initial administrator bootstrap" (transaction-scoped advisory lock).
    private const long BootstrapLockKey = 0x4841534D_0002;

    public IdentityStore(string connectionString, TimeProvider clock, AuditTrail audit, IdentityKeys? keys = null) : this(() => connectionString, clock, audit, keys) { }

    /// <summary>The identity keys (identity integrity S-1). A store without them cannot issue or accept a session: it fails closed with the service-unavailable exception, it never falls back to an untagged session.</summary>
    internal IdentityKeys RequireKeys() => keys ?? throw new EncryptionUnavailableException("encryption_key_unavailable", "No identity key ring is configured.");

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
        var tagged = UserRow.ForNewUser(RequireKeys(), id, login, hash, admin);                                      // a user is created already tagged (identity integrity S-2a); no key, no user
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {U.Table} ({U.Id},{U.Login},{U.DisplayName},{U.PasswordHash},{U.State},{U.IsIdentityAdministrator},{U.CreatedAtUtc},{U.StateChangedAtUtc},{U.PasswordChangedAtUtc},{UT.KeyId},{UT.Tag}) " +
            "VALUES (@id,@l,@d,@h,0,@a,@n,@n,@n,@k,@g)", conn, tx);
        cmd.Parameters.AddWithValue("k", tagged.KeyId!);
        cmd.Parameters.AddWithValue("g", tagged.Tag!);
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

    /// <summary>
    /// Finds a user whose row is AUTHENTIC (identity integrity S-2a): a row whose tag does not verify, or that has none, is answered as no user at all (the neutral failure of login and of every flow that starts from the
    /// user), and recorded in the audit stream for the operator (at most once a minute per user). Throws the service-unavailable exception when no identity key is available.
    /// </summary>
    private async Task<UserRecord?> FindUserAsync(string where, object value, CancellationToken ct)
    {
        var k = RequireKeys();
        UserRecord user; UserRow row;
        await using (var conn = await dataSource.Value.OpenConnectionAsync(ct))
        {
            await using var cmd = new NpgsqlCommand($"SELECT {UserColumns}, {UserRow.Columns("u")} FROM {U.Table} u WHERE {where}", conn);
            cmd.Parameters.AddWithValue("v", value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            user = ReadUser(r); row = UserRow.Read(r, 8);
        }
        if (row.IsValid(k)) return user;
        await RejectUserAsync(row.Id, ct);
        return null;
    }

    private async Task RejectUserAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (lastRejectionAudit.TryGetValue(userId, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        lastRejectionAudit[userId] = now;
        if (lastRejectionAudit.Count > 10_000) lastRejectionAudit.Clear();
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.IdentityRecordRejected, null, userId, $"user {userId:N} rejected: tag", ct);
        await audit.CommitAsync(tx, receipt, ct);
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

            if (actorUserId is { } actor && (await ReadStateAsync(conn, tx, RequireKeys(), actor, ct)) is not { State: UserState.Active, Authentic: true })
                return SetStateOutcome.ActorNotActive;
            var (current, isIdentityAdmin, _) = await ReadStateAsync(conn, tx, RequireKeys(), userId, ct);
            if (current is null)
                return SetStateOutcome.NotFound;

            if (state != UserState.Active && current == UserState.Active)
            {
                if (isIdentityAdmin && await CountActiveIdentityAdministratorsAsync(conn, tx, RequireKeys(), exclude: userId, ct) == 0)
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

            // the state is a covered field: it changes through the tagged primitive (verified row, revision +1, re-tag under CAS). Leaving Active is a security-reducing change and is allowed on a row that does not verify
            // (it keeps its invalid tag); re-activating a user requires an authentic row.
            var changed = await UserRow.ChangeAsync(conn, tx, RequireKeys(), userId, async () =>
            {
                await using var cmd = new NpgsqlCommand($"UPDATE {U.Table} SET {U.State} = @s, {U.StateChangedAtUtc} = @n WHERE {U.Id} = @id", conn, tx);
                cmd.Parameters.AddWithValue("s", (short)state);
                cmd.Parameters.AddWithValue("n", now);
                cmd.Parameters.AddWithValue("id", userId);
                return await cmd.ExecuteNonQueryAsync(ct) == 1;
            }, bump: true, requireValid: state == UserState.Active, ct);
            if (!changed) return SetStateOutcome.NotFound;
            if (state != UserState.Active) await RevokeUserSessionsAsync(conn, tx, keys, userId, SessionRevocation.Administrative, null, now, ct, bumpRevision: false);
            var receipt = await AuditAsync(conn, tx, AuditEventTypes.UserStateChanged, actorUserId, userId, $"state: {state}", ct);
            await audit.CommitAsync(tx, receipt, ct);
            return SetStateOutcome.Changed;
        }
        throw new InvalidOperationException("Administrator continuity locking did not converge.");
    }

    /// <summary>The state and the administrator flag of a user from the row, with whether the row VERIFIES (review L1 of PR 235: the continuity decisions are taken from the verified row; an administrator that does not verify is not a usable administrator).</summary>
    private static async Task<(UserState? State, bool IsIdentityAdministrator, bool Authentic)> ReadStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys keys, Guid userId, CancellationToken ct)
    {
        if (await UserRow.LoadAsync(conn, tx, userId, forUpdate: false, ct) is not { } row) return (null, false, false);
        var authentic = row.IsValid(keys);
        return ((UserState)row.State, authentic && row.IsAdministrator, authentic);
    }

    private static async Task<long> CountActiveIdentityAdministratorsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys keys, Guid exclude, CancellationToken ct)
    {
        var rows = new List<UserRow>();
        await using (var cmd = new NpgsqlCommand($"SELECT {UserRow.Columns("u")} FROM {U.Table} u WHERE u.{U.IsIdentityAdministrator} AND u.{U.State} = 0 AND u.{U.Id} <> @x", conn, tx))
        {
            cmd.Parameters.AddWithValue("x", exclude);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(UserRow.Read(r));
        }
        return rows.Count(row => row.IsValid(keys));                                                            // only an authentic administrator counts as one
    }

    /// <summary>Replaces the stored hash. <paramref name="passwordChanged"/> false = transparent rehash with the same password.</summary>
    public async Task UpdatePasswordHashAsync(Guid userId, string hash, bool passwordChanged, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // the password hash is a covered field: tagged primitive. A real change raises the revision (every other session ends through the revoke-all that follows); a transparent rehash of the same password
        // re-tags without a bump (it must not sign the user out of every device at each login).
        var done = await UserRow.ChangeAsync(conn, tx, RequireKeys(), userId, async () =>
        {
            await using var cmd = new NpgsqlCommand(
                $"UPDATE {U.Table} SET {U.PasswordHash} = @h" + (passwordChanged ? $", {U.PasswordChangedAtUtc} = @n" : "") + $" WHERE {U.Id} = @id", conn, tx);
            cmd.Parameters.AddWithValue("h", hash);
            cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("id", userId);
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }, bump: passwordChanged, requireValid: true, ct);
        if (!done) throw new InvalidOperationException("user_not_authentic");
        // A real password change is a security event (a transparent rehash with the same password is not).
        if (!passwordChanged) { await tx.CommitAsync(ct); return; }
        var receipt = await AuditAsync(conn, tx, AuditEventTypes.PasswordChanged, userId, userId, null, ct);
        await audit.CommitAsync(tx, receipt, ct);
    }

    // ---- Sessions ----

    public async Task CreateSessionAsync(Guid sessionId, Guid userId, string token, SessionClient client, DateTimeOffset idleExpires, DateTimeOffset absoluteExpires, DateTimeOffset? mfaVerifiedAt = null, CancellationToken ct = default)
    {
        var k = RequireKeys();
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await InsertSessionAsync(conn, tx, k, new SessionIssue(sessionId, token, client, idleExpires, absoluteExpires, mfaVerifiedAt), userId, clock.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// THE ONE primitive that creates a session (identity integrity S-1), inside the CALLER's transaction so a session can be created atomically with another change: it hashes the token under the token key,
    /// reads the user's security revision under a row lock, and writes the row with the key id and the tag over its security fields. No other code inserts a session row.
    /// </summary>
    internal static async Task InsertSessionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys keys, SessionIssue issue, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var key = keys.Active();
        long revision;
        await using (var rev = new NpgsqlCommand($"SELECT {UI.SecurityRevision} FROM {U.Table} WHERE {U.Id} = @u FOR SHARE", conn, tx))
        {
            rev.Parameters.AddWithValue("u", userId);
            revision = await rev.ExecuteScalarAsync(ct) is long l ? l : throw new InvalidOperationException("session_user_missing");
        }
        // a session created after the second factor records it: auth_methods 3 (password + totp) with the verification time; otherwise password only (1)
        var row = new SessionRow(issue.SessionId, userId, keys.TokenHash(key, issue.Token), (short)issue.Client, MacInput.Truncate(now), MacInput.Truncate(issue.AbsoluteExpires),
            issue.MfaVerifiedAt is null ? (short)1 : (short)3, issue.MfaVerifiedAt is { } mv ? MacInput.Truncate(mv) : null, false, null, revision, key.Id, null);
        row = row with { Tag = row.ComputeTag(keys, key) };
        await using (var cmd = new NpgsqlCommand(
            $"INSERT INTO {S.Table} ({S.Id},{S.UserId},{S.TokenHash},{S.CreatedAtUtc},{S.LastSeenAtUtc},{S.IdleExpiresAtUtc},{S.AbsoluteExpiresAtUtc},{S.ClientKind},{SM.AuthMethods},{SM.VerifiedAtUtc},{SM.Emergency},{SI.UserRevisionAtIssue},{SI.KeyId},{SI.Tag}) " +
            "VALUES (@id,@u,@t,@n,@n,@i,@a,@c,@am,@mv,false,@r,@k,@g)", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", row.Id);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("t", row.TokenHash);
            cmd.Parameters.AddWithValue("n", row.CreatedAt);
            cmd.Parameters.AddWithValue("i", MacInput.Truncate(issue.IdleExpires));
            cmd.Parameters.AddWithValue("a", row.AbsoluteExpires);
            cmd.Parameters.AddWithValue("c", row.ClientKind);
            cmd.Parameters.AddWithValue("am", row.AuthMethods);
            cmd.Parameters.AddWithValue("mv", (object?)row.VerifiedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("r", revision);
            cmd.Parameters.AddWithValue("k", key.Id);
            cmd.Parameters.AddWithValue("g", row.Tag!);
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
        var k = RequireKeys();
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await UserRow.LoadAsync(conn, tx, userId, forUpdate: false, ct) is not { } row) return MfaState.None;
        return row.IsValid(k) ? (MfaState)row.MfaState : MfaState.RecoveryRequired;                               // taken from the VERIFIED row; a row that does not verify is the strictest state (fail closed, review L1 of PR 235)
    }

    /// <summary>
    /// The user's CENTRAL MFA requirement (Phase 4.1 section 3, D-080 (1), D-165): identity administrator, an active Company Administrator preset, or any active role holding a privileged
    /// permission, in ANY company (never only the selected one). Read from the database on every call. A user that does not exist has no requirement (and no session).
    /// </summary>
    public async Task<MfaRequirement> GetMfaRequirementAsync(Guid userId, CancellationToken ct = default)
    {
        var reasons = MfaMandatoryReason.None;
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using (var tx0 = await conn.BeginTransactionAsync(ct))
            if (await UserRow.LoadAsync(conn, tx0, userId, forUpdate: false, ct) is { } row && (row.IsAdministrator || !row.IsValid(RequireKeys())))   // from the verified row; a row that does not verify requires the most (review L1 of PR 235)
                reasons |= MfaMandatoryReason.IdentityAdministrator;
        var roles = new Dictionary<Guid, List<string>>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT r.{RL.Id}, p.{RP.Permission} FROM {RA.Table} a JOIN {RL.Table} r ON r.{RL.Id} = a.{RA.RoleId} AND r.{RL.IsActive} " +
            $"LEFT JOIN {RP.Table} p ON p.{RP.RoleId} = r.{RL.Id} WHERE a.{RA.UserId} = @u AND a.{RA.RevokedAtUtc} IS NULL", conn))
        {
            cmd.Parameters.AddWithValue("u", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                if (!roles.TryGetValue(id, out var entry)) roles[id] = entry = [];
                if (!reader.IsDBNull(1)) entry.Add(reader.GetString(1));
            }
        }
        foreach (var (roleId, permissions) in roles)
        {
            if (roleId == PresetRoles.CompanyAdministratorId) reasons |= MfaMandatoryReason.CompanyAdministratorPreset;
            if (PrivilegedAccess.IsPrivilegedRole(roleId, permissions)) reasons |= MfaMandatoryReason.PrivilegedRole;      // the ONE central definition (LEAD D-096)
        }
        return new MfaRequirement(reasons);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> lastRejectionAudit = new();

    /// <summary>
    /// Finds a session that is not revoked, not idle-expired, not absolute-expired and whose user is Active, AND whose row is authentic: the token hash is the keyed hash under an accepted key, the tag over the
    /// security fields verifies, and the revision at issue equals the user's current security revision. A row that fails the integrity checks is not a session: it is revoked, answered with the same neutral null as an
    /// unknown token (no cryptographic detail), and recorded in the audit stream for the operator (at most once a minute per session). Throws the service-unavailable exception when no identity key is available.
    /// The user's current state and administrator flag are read on every call, so changes apply immediately.
    /// </summary>
    public async Task<ActiveSession?> FindActiveSessionAsync(string token, CancellationToken ct = default)
    {
        var k = RequireKeys();
        var now = clock.GetUtcNow();
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        foreach (var key in k.Accepted())
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT s.{S.LastSeenAtUtc}, s.{S.IdleExpiresAtUtc}, u.{U.Login}, u.{U.DisplayName}, u.{U.IsIdentityAdministrator}, u.{M.State}, u.{UI.SecurityRevision}, {SessionRow.Columns("s")}, {UserRow.Columns("u")} " +
                $"FROM {S.Table} s JOIN {U.Table} u ON u.{U.Id} = s.{S.UserId} " +
                $"WHERE s.{S.TokenHash} = @t AND s.{S.RevokedAtUtc} IS NULL AND s.{S.IdleExpiresAtUtc} > @n AND s.{S.AbsoluteExpiresAtUtc} > @n AND u.{U.State} = 0", conn);
            cmd.Parameters.AddWithValue("t", k.TokenHash(key, token));
            cmd.Parameters.AddWithValue("n", now);
            SessionRow row; UserRow userRow; DateTimeOffset lastSeen, idle; string login, display; bool admin; MfaState mfa; long currentRevision;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) continue;
                lastSeen = r.GetFieldValue<DateTimeOffset>(0); idle = r.GetFieldValue<DateTimeOffset>(1);
                login = r.GetString(2); display = r.GetString(3); admin = r.GetBoolean(4); mfa = (MfaState)r.GetInt16(5); currentRevision = r.GetInt64(6);
                row = SessionRow.Read(r, 7); userRow = UserRow.Read(r, 7 + 13);
            }
            // the tag (under the key id the row names, which must be an accepted key) must verify, and the revision at issue must still be the user's current revision. The key that hashed the TOKEN and the key
            // that tagged the row may differ during a rotation (a re-tag moves the row to the active key, the token hash stays), both are accepted keys.
            var tagValid = row.IsValid(k);
            var userValid = tagValid && userRow.IsValid(k);                                                  // identity integrity S-2a: the user the session belongs to must be authentic too
            if (!tagValid || !userValid || row.RevisionAtIssue != currentRevision)
            {
                await RejectSessionAsync(conn, row, !tagValid ? "tag" : !userValid ? "user tag" : "revision", ct);
                return null;
            }
            return new ActiveSession(row.Id, row.UserId, (SessionClient)row.ClientKind, idle, row.AbsoluteExpires, lastSeen, login, display, admin, row.VerifiedAt, mfa, row.SelectedCompanyId);
        }
        return null;
    }

    private async Task RejectSessionAsync(NpgsqlConnection conn, SessionRow row, string cause, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand($"UPDATE {S.Table} SET {S.RevokedAtUtc} = @n, {S.RevokedReason} = @r WHERE {S.Id} = @id AND {S.RevokedAtUtc} IS NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", now);
            cmd.Parameters.AddWithValue("r", (short)SessionRevocation.Administrative);
            cmd.Parameters.AddWithValue("id", row.Id);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return; }
        }
        // audit at most once a minute per session (the row is revoked here, so a repeat is rare; the bound keeps a flood from growing the stream)
        if (lastRejectionAudit.TryGetValue(row.Id, out var last) && now - last < TimeSpan.FromMinutes(1)) { await tx.CommitAsync(ct); return; }
        lastRejectionAudit[row.Id] = now;
        if (lastRejectionAudit.Count > 10_000) lastRejectionAudit.Clear();
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.IdentityRecordRejected, null, row.UserId, $"session {row.Id:N} rejected: {cause}", ct);
        await audit.CommitAsync(tx, receipt, ct);
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
        var k = RequireKeys();                                           // the user row is re-tagged and the kept session re-tagged: a key is required (identity integrity S-2a)
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await RevokeUserSessionsAsync(conn, tx, k, userId, reason, exceptSessionId, clock.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
        return n;
    }

    /// <summary>
    /// Revokes every live session of the user and raises the user's SECURITY REVISION in the same transaction (identity integrity S-1): a session row written back after the revocation (the replay of vector A)
    /// carries the old revision and is never honoured. The <paramref name="except"/> session (the one that changed the password) is kept: it is re-tagged under the new revision by compare-and-swap, so it stays live
    /// and no other row does.
    /// </summary>
    internal static async Task<int> RevokeUserSessionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdentityKeys? keys, Guid userId, SessionRevocation reason, Guid? except, DateTimeOffset now, CancellationToken ct, bool bumpRevision = true)
    {
        if (keys is null) throw new EncryptionUnavailableException("encryption_key_unavailable", "No identity key ring is configured.");
        // the revision is a covered field of the user: raised (and the user row re-tagged when it verifies; a row that does not verify keeps its invalid tag) through the tagged primitive, never by a bare UPDATE
        if (bumpRevision && !await UserRow.ChangeAsync(conn, tx, keys, userId, () => Task.FromResult(true), bump: true, requireValid: false, ct)) throw new InvalidOperationException("session_user_missing");
        long revision;
        await using (var read = new NpgsqlCommand($"SELECT {UI.SecurityRevision} FROM {U.Table} WHERE {U.Id} = @u", conn, tx))
        {
            read.Parameters.AddWithValue("u", userId);
            revision = await read.ExecuteScalarAsync(ct) is long l ? l : throw new InvalidOperationException("session_user_missing");
        }
        int revoked;
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {S.Table} SET {S.RevokedAtUtc} = @n, {S.RevokedReason} = @r WHERE {S.UserId} = @u AND {S.RevokedAtUtc} IS NULL" + (except is null ? "" : $" AND {S.Id} <> @x"), conn, tx))
        {
            cmd.Parameters.AddWithValue("n", now);
            cmd.Parameters.AddWithValue("r", (short)reason);
            cmd.Parameters.AddWithValue("u", userId);
            if (except is not null) cmd.Parameters.AddWithValue("x", except.Value);
            revoked = await cmd.ExecuteNonQueryAsync(ct);
        }
        if (except is { } keep && await SessionRow.LoadForUpdateAsync(conn, tx, keep, ct) is { } before)
        {
            // keep the session only when its own tag is valid and it belongs to the user; re-tag it under the new revision. Anything else is revoked.
            var ok = before.UserId == userId && before.IsValid(keys);
            if (ok)
            {
                var after = before with { RevisionAtIssue = revision };
                var key = keys.Active();
                after = after with { KeyId = key.Id, Tag = null };
                after = after with { Tag = after.ComputeTag(keys, key) };
                await using var up = new NpgsqlCommand($"UPDATE {S.Table} SET {SI.UserRevisionAtIssue} = @r, {SI.KeyId} = @k, {SI.Tag} = @t WHERE {S.Id} = @id AND {SI.Tag} = @old", conn, tx);
                up.Parameters.AddWithValue("r", revision); up.Parameters.AddWithValue("k", key.Id); up.Parameters.AddWithValue("t", after.Tag!);
                up.Parameters.AddWithValue("id", keep); up.Parameters.AddWithValue("old", before.Tag!);
                ok = await up.ExecuteNonQueryAsync(ct) == 1;
            }
            if (!ok)
            {
                await using var rv = new NpgsqlCommand($"UPDATE {S.Table} SET {S.RevokedAtUtc} = @n, {S.RevokedReason} = @r WHERE {S.Id} = @id AND {S.RevokedAtUtc} IS NULL", conn, tx);
                rv.Parameters.AddWithValue("n", now); rv.Parameters.AddWithValue("r", (short)reason); rv.Parameters.AddWithValue("id", keep);
                revoked += await rv.ExecuteNonQueryAsync(ct);
            }
        }
        return revoked;
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
