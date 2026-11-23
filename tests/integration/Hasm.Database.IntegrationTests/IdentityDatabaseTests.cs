using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SI = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionIntegrity;
using UI = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity;
using UT = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag;
using T = Hasm.Infrastructure.SystemDatabase.SystemSchema.LoginThrottle;
using A = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserCompanyAccess;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;

namespace Hasm.Database.IntegrationTests;

/// <summary>Controllable clock; the application supplies every timestamp, so session/throttle expiry is testable without sleeping.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

/// <summary>Identity layer wired to a real, disposable PostgreSQL system database (same safety gate as all PostgreSQL tests).</summary>
public abstract class IdentityTestBase : PostgresTestBase
{
    protected const string Pw = "Correct-Horse-Battery-9";
    protected FakeClock Clock { get; } = new();
    protected AuthOptions AuthSettings { get; } = new();
    /// <summary>Audit evidence for the suite: in-memory keys and checkpoints (the file-based implementations have their own tests).</summary>
    protected InMemoryAuditKeyRing AuditKeys { get; } = new();
    protected InMemoryCheckpointStore AuditCheckpoints { get; } = new();
    protected AuditTrail Audit { get; }
    protected IdentityStore Store { get; private set; } = null!;
    protected AuthService Auth { get; private set; } = null!;
    /// <summary>The identity keys the stores of this suite use: the assembly's disposable ring unless a suite runs its API on a ring of its own (then it overrides this with the same ring).</summary>
    protected virtual IdentityKeys SessionKeys => TestIdentityKeys.Instance;
    protected IdentityTestBase() => Audit = new AuditTrail(new AuditServices(AuditKeys, AuditCheckpoints, 0), Clock);

    protected PasswordService Passwords { get; } = new(1000); // low cost keeps the suite fast; production floor is enforced by AuthOptions

    protected async Task Start()
    {
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        Store = new IdentityStore(Options.BuildSystemConnectionString(admin: true), Clock, Audit, keys: SessionKeys);
        Auth = new AuthService(Store, Passwords, AuthSettings);
    }

    /// <summary>
    /// A direct SQL write to the user table by a test stands for "a legitimate writer set the row" (a restore, an offline step): the users are re-tagged afterwards under the suite's keys, so the identity checks
    /// of the product see authentic rows. The tamper tests of the product use their own writers that do NOT re-tag (IdentitySessionIntegrityTests, UserIntegrityTests).
    /// </summary>
    protected override async Task AfterExecAsync(string sql)
    {
        var text = sql.TrimStart();
        if (!sql.Contains(U.Table, StringComparison.Ordinal) || !(text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))) return;
        await RetagAllUsersAsync();
    }

    protected async Task RetagAllUsersAsync()
    {
        var keys = SessionKeys; var key = keys.Active();
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var ids = new List<Guid>();
        await using (var sel = new NpgsqlCommand($"SELECT {U.Id} FROM {U.Table}", conn, tx))
        await using (var r = await sel.ExecuteReaderAsync()) while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        foreach (var id in ids)
        {
            var row = (await UserRow.LoadAsync(conn, tx, id, false, default))!;
            row = row with { KeyId = key.Id, Tag = null };
            await using var set = new NpgsqlCommand($"UPDATE {U.Table} SET {UT.KeyId} = @k, {UT.Tag} = @t WHERE {U.Id} = @i", conn, tx);
            set.Parameters.AddWithValue("k", key.Id); set.Parameters.AddWithValue("t", row.ComputeTag(keys, key)); set.Parameters.AddWithValue("i", id);
            await set.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    /// <summary>Sets (or clears) the selected company of every live session of the user through the TAGGED primitive (what a company selection made earlier left behind): a direct column write would be a tampered row, which is no session.</summary>
    protected async Task SelectCompanyForSessions(Guid userId, Guid? companyId, bool mustApply = true)
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand($"SELECT {S.Id} FROM {S.Table} WHERE {S.UserId} = @u AND {S.RevokedAtUtc} IS NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        }
        foreach (var id in ids) { var applied = await SessionRow.SelectCompanyAsync(conn, tx, SessionKeys, id, userId, companyId, default); if (mustApply) Assert.True(applied); }   // a session whose revision is stale (the user changed since) takes no selection: that is the point of the revision
        await tx.CommitAsync();
    }

    protected async Task<UserRecord> NewUser(string login = "alice", string password = Pw, bool admin = false)
    {
        var r = await Auth.CreateUserAsync(login, "Display " + login, password, admin);
        Assert.Equal(CreateUserStatus.Created, r.Status);
        return r.User!;
    }

    protected Task<LoginOutcome> Login(string login, string password, string? ip = "10.0.0.1", SessionClient client = SessionClient.Web) =>
        Auth.LoginAsync(login, password, client, ip);

    protected async Task<LoginOutcome.Success> LoginOk(string login = "alice", string password = Pw, string? ip = "10.0.0.1") =>
        Assert.IsType<LoginOutcome.Success>(await Login(login, password, ip));
}

public class IdentityMigrationTests : IdentityTestBase
{
    [PostgresFact]
    public async Task Migration_0003_is_applied_once_and_repeating_is_a_noop()
    {
        var first = await new SystemDatabaseInitializer(Options).InitializeAsync();
        Assert.Contains(3, first.AppliedMigrations);
        var checksum = await Scalar<string>($"SELECT checksum FROM {SystemSchema.MigrationJournalTable} WHERE version = 3");

        var second = await new SystemDatabaseInitializer(Options).InitializeAsync();

        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(checksum, await Scalar<string>($"SELECT checksum FROM {SystemSchema.MigrationJournalTable} WHERE version = 3"));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable} WHERE version = 3"));
    }

    [PostgresFact]
    public async Task Existing_database_at_version_2_upgrades_to_latest_preserving_data()
    {
        await new SystemDatabaseInitializer(Options).InitializeAsync(); // creates the per-test disposable database
        // Simulate an older installation by undoing migrations 4 and 3 by hand (inside the disposable test database only), then re-run the real initializer.
        await Exec("DROP TABLE " + SystemSchema.AccessAudit.Table + ", " + SystemSchema.RoleAssignment.Table + ", " + SystemSchema.RolePermission.Table + ", " + SystemSchema.Role.Table + " CASCADE;" +
                   "DROP FUNCTION " + SystemSchema.AccessAudit.GuardFunction + "(); DROP FUNCTION " + SystemSchema.Role.GuardFunction + "(); DROP FUNCTION " + SystemSchema.RolePermission.GuardFunction + "(); DROP FUNCTION " + SystemSchema.RoleAssignment.GuardFunction + "();");
        await Exec("DROP TABLE " + A.Table + ", " + T.Table + ", " + S.Table + ", " + U.Table + " CASCADE; DROP FUNCTION " + U.GuardFunction + "();");
        await Exec($"DELETE FROM {SystemSchema.MigrationJournalTable} WHERE version IN (3, 4)");
        await Exec($"INSERT INTO {SystemSchema.Company.Table} ({SystemSchema.Company.Id},{SystemSchema.Company.PhysicalDatabaseName},{SystemSchema.Company.ProvisioningState},{SystemSchema.Company.CreatedAtUtc},{SystemSchema.Company.StateChangedAtUtc}) VALUES (@i,'dkeepme',0,now(),now())", ("i", Guid.NewGuid()));

        var result = await new SystemDatabaseInitializer(Options).InitializeAsync();

        Assert.Equal([3, 4], result.AppliedMigrations);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.Company.Table}"));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {U.Table}"));
    }

    [PostgresFact]
    public async Task Physical_identity_schema_matches_the_logical_map()
    {
        await Start();
        var expected = new Dictionary<string, string[]>
        {
            [U.Table] = [U.Id, U.Login, U.DisplayName, U.PasswordHash, U.State, U.IsIdentityAdministrator, U.CreatedAtUtc, U.StateChangedAtUtc, U.PasswordChangedAtUtc, U.LastLoginAtUtc, M.State, M.StateChangedAtUtc, M.EnrolmentCodeHash, M.EnrolmentCodeExpiresAtUtc, M.EnrolmentCodeConsumedAtUtc, UI.SecurityRevision, UT.KeyId, UT.Tag],
            [S.Table] = [S.Id, S.UserId, S.TokenHash, S.CreatedAtUtc, S.LastSeenAtUtc, S.IdleExpiresAtUtc, S.AbsoluteExpiresAtUtc, S.RevokedAtUtc, S.RevokedReason, S.ClientKind, S.SelectedCompanyId, SM.VerifiedAtUtc, SM.AuthMethods, SM.Emergency, SI.KeyId, SI.Tag, SI.UserRevisionAtIssue],
            [T.Table] = [T.Scope, T.KeyHash, T.Failures, T.WindowStartedAtUtc, T.BlockedUntilUtc],
            [A.Table] = [A.UserId, A.CompanyId, A.GrantedAtUtc, A.GrantedBy, A.RevokedAtUtc, A.RevokedBy],
        };
        foreach (var (table, columns) in expected)
        {
            Assert.Equal(columns.Length, (int)await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_name=@t", ("t", table)));
            foreach (var c in columns)
                Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_name=@t AND column_name=@c", ("t", table), ("c", c)));
        }
        foreach (var k in new[] { U.PrimaryKey, U.UniqueLogin, U.CheckState, U.CheckLogin, U.CheckDisplayName, U.CheckPasswordHash,
                     S.PrimaryKey, S.ForeignKeyUser, S.UniqueTokenHash, S.CheckTokenHash, S.CheckClientKind, S.CheckRevocation, S.IndexUser,
                     T.PrimaryKey, T.CheckScope, T.CheckKeyHash, A.PrimaryKey, A.ForeignKeyUser, A.ForeignKeyCompany })
            Assert.True(await Scalar<long>(
                "SELECT (SELECT count(*) FROM pg_constraint WHERE conname=@n) + (SELECT count(*) FROM pg_class WHERE relname=@n)", ("n", k)) > 0, k);
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_trigger WHERE tgname=@n", ("n", U.GuardTrigger)));
    }

    [PostgresFact]
    public async Task Physical_identity_names_are_opaque()
    {
        await Start();
        var names = await Scalar<string>(
            "SELECT string_agg(table_name || ',' || column_name, ';') FROM information_schema.columns WHERE table_name = ANY(@t)",
            ("t", new[] { U.Table, S.Table, T.Table, A.Table }));
        foreach (var word in new[] { "user", "password", "session", "login", "token", "admin", "throttle", "hash" })
            Assert.DoesNotContain(word, names, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Database_rejects_invalid_rows_even_when_the_application_is_bypassed()
    {
        await Start();
        var hash = Passwords.Hash(Pw);
        string Insert(string login, string name = "N", short state = 0) =>
            $"INSERT INTO {U.Table} ({U.Id},{U.Login},{U.DisplayName},{U.PasswordHash},{U.State},{U.IsIdentityAdministrator},{U.CreatedAtUtc},{U.StateChangedAtUtc},{U.PasswordChangedAtUtc}) VALUES (@i,'{login}','{name}',@h,{state},false,now(),now(),now())";

        await Exec(Insert("valid.user"), ("i", Guid.NewGuid()), ("h", hash));
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("valid.user"), ("i", Guid.NewGuid()), ("h", hash)));          // duplicate
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("Upper.Case"), ("i", Guid.NewGuid()), ("h", hash)));          // not normalized
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("with space"), ("i", Guid.NewGuid()), ("h", hash)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("ab"), ("i", Guid.NewGuid()), ("h", hash)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("other.user", state: 7), ("i", Guid.NewGuid()), ("h", hash)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("blank.name", name: "  "), ("i", Guid.NewGuid()), ("h", hash)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("plain.pw"), ("i", Guid.NewGuid()), ("h", "short-plaintext"))); // too short to be a hash
    }

    [PostgresFact]
    public async Task User_identity_is_immutable_and_rows_cannot_be_deleted()
    {
        await Start();
        var user = await NewUser();
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {U.Table} SET {U.Login} = 'someone.else' WHERE {U.Id} = @i", ("i", user.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {U.Table} SET {U.Id} = @n WHERE {U.Id} = @i", ("n", Guid.NewGuid()), ("i", user.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {U.Table} WHERE {U.Id} = @i", ("i", user.Id)));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {U.Table}"));
    }

    [PostgresFact]
    public async Task Session_rows_must_reference_a_user_and_hold_a_32_byte_token_digest()
    {
        await Start();
        var user = await NewUser();
        string Insert() => $"INSERT INTO {S.Table} ({S.Id},{S.UserId},{S.TokenHash},{S.CreatedAtUtc},{S.LastSeenAtUtc},{S.IdleExpiresAtUtc},{S.AbsoluteExpiresAtUtc},{S.ClientKind}) VALUES (@i,@u,@t,now(),now(),now(),now(),0)";
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert(), ("i", Guid.NewGuid()), ("u", Guid.NewGuid()), ("t", SessionToken.Hash(SessionToken.Generate()))));
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert(), ("i", Guid.NewGuid()), ("u", user.Id), ("t", new byte[] { 1, 2, 3 })));
        await Exec(Insert(), ("i", Guid.NewGuid()), ("u", user.Id), ("t", SessionToken.Hash(SessionToken.Generate())));
    }

    [PostgresFact]
    public async Task Company_association_is_explicit_and_never_created_implicitly()
    {
        await Start();
        var user = await NewUser();
        await LoginOk();
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {A.Table}"));   // creating a user / logging in grants nothing

        await Assert.ThrowsAsync<PostgresException>(() => Exec(                    // association must reference a registered company
            $"INSERT INTO {A.Table} ({A.UserId},{A.CompanyId},{A.GrantedAtUtc}) VALUES (@u,@c,now())", ("u", user.Id), ("c", Guid.NewGuid())));
    }
}

public class UserCreationTests : IdentityTestBase
{
    [PostgresFact]
    public async Task Creates_a_user_with_a_stable_id_and_a_hashed_password()
    {
        await Start();
        var user = await NewUser("Alice.Smith");

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("alice.smith", user.Login);
        Assert.Equal(UserState.Active, user.State);
        Assert.False(user.IsIdentityAdministrator);

        var stored = await Scalar<string>($"SELECT {U.PasswordHash} FROM {U.Table} WHERE {U.Id} = @i", ("i", user.Id));
        Assert.DoesNotContain(Pw, stored);
        Assert.Equal(PasswordVerification.Success, Passwords.Verify(stored, Pw));
        Assert.Equal(user.Id, (await Store.FindUserByLoginAsync("alice.smith"))!.Id);
    }

    [PostgresTheory]
    [InlineData("alice", " ALICE ")]
    [InlineData("alice", "Alice")]
    [InlineData("alice", "ＡＬＩＣＥ")]
    public async Task Logins_are_unique_after_normalization(string first, string second)
    {
        await Start();
        await NewUser(first);
        var duplicate = await Auth.CreateUserAsync(second, "Other", Pw, false);
        Assert.Equal(CreateUserStatus.LoginTaken, duplicate.Status);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {U.Table}"));
    }

    [PostgresFact]
    public async Task Concurrent_creation_of_the_same_login_yields_exactly_one_user()
    {
        await Start();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Auth.CreateUserAsync("racer", "Racer", Pw, false)));
        Assert.Equal(1, results.Count(r => r.Status == CreateUserStatus.Created));
        Assert.Equal(5, results.Count(r => r.Status == CreateUserStatus.LoginTaken));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {U.Table}"));
    }

    [PostgresFact]
    public async Task Invalid_input_creates_nothing()
    {
        await Start();
        Assert.Equal(CreateUserStatus.InvalidLogin, (await Auth.CreateUserAsync("a b", "X", Pw, false)).Status);
        Assert.Equal(CreateUserStatus.InvalidDisplayName, (await Auth.CreateUserAsync("bob", "  ", Pw, false)).Status);
        var weak = await Auth.CreateUserAsync("bob", "Bob", "short", false);
        Assert.Equal(CreateUserStatus.WeakPassword, weak.Status);
        Assert.Contains("too_short", weak.PasswordProblems!);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {U.Table}"));
    }

    [PostgresFact]
    public async Task Initial_administrator_bootstrap_succeeds_once_and_refuses_afterwards()
    {
        await Start();
        var first = await Auth.BootstrapAdministratorAsync("root", "Root", Pw);
        Assert.Equal(CreateUserStatus.Created, first!.Status);
        Assert.True(first.User!.IsIdentityAdministrator);

        Assert.Null(await Auth.BootstrapAdministratorAsync("second.root", "Second", Pw));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {U.Table}"));
    }

    [PostgresFact]
    public async Task Concurrent_bootstrap_creates_exactly_one_administrator()
    {
        await Start();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Auth.BootstrapAdministratorAsync($"root{i}", "Root", Pw)));
        Assert.Equal(1, results.Count(r => r is { Status: CreateUserStatus.Created }));
        Assert.Equal(4, results.Count(r => r is null));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {U.Table} WHERE {U.IsIdentityAdministrator}"));
    }

    [PostgresFact]
    public async Task Bootstrap_rejects_weak_passwords()
    {
        await Start();
        Assert.Equal(CreateUserStatus.WeakPassword, (await Auth.BootstrapAdministratorAsync("root", "Root", "short"))!.Status);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {U.Table}"));
    }
}

public class LoginTests : IdentityTestBase
{
    [PostgresFact]
    public async Task Successful_login_creates_a_session_and_returns_the_user()
    {
        await Start();
        var user = await NewUser();
        var ok = await LoginOk();
        Assert.Equal(user.Id, ok.User.Id);
        Assert.True(SessionToken.IsWellFormed(ok.Token));
        Assert.NotNull((await Store.FindUserByIdAsync(user.Id))!.LastLoginAtUtc);
    }

    [PostgresFact]
    public async Task Wrong_password_unknown_login_malformed_login_disabled_and_locked_are_indistinguishable()
    {
        await Start();
        var disabled = await NewUser("disabled.user");
        var locked = await NewUser("locked.user");
        await NewUser("alice");
        await Store.SetStateAsync(null, disabled.Id, UserState.Disabled);
        await Store.SetStateAsync(null, locked.Id, UserState.Locked);

        var outcomes = new[]
        {
            await Login("alice", "wrong-password-123"),
            await Login("nobody", Pw),
            await Login("not a valid login!", Pw),
            await Login("disabled.user", Pw),    // correct password, disabled
            await Login("locked.user", Pw),      // correct password, locked
        };
        Assert.All(outcomes, o => Assert.IsType<LoginOutcome.InvalidCredentials>(o));
    }

    [PostgresFact]
    public async Task Disabled_and_locked_accounts_can_log_in_again_once_reactivated()
    {
        await Start();
        var user = await NewUser();
        await Store.SetStateAsync(null, user.Id, UserState.Disabled);
        Assert.IsType<LoginOutcome.InvalidCredentials>(await Login("alice", Pw));
        await Store.SetStateAsync(null, user.Id, UserState.Locked);
        Assert.IsType<LoginOutcome.InvalidCredentials>(await Login("alice", Pw));
        await Store.SetStateAsync(null, user.Id, UserState.Active);
        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, ip: "10.0.0.9"));
    }

    [PostgresFact]
    public async Task Login_is_case_and_whitespace_insensitive_on_the_identifier_but_not_the_password()
    {
        await Start();
        await NewUser("alice");
        Assert.IsType<LoginOutcome.Success>(await Login("  ALICE ", Pw));
        Assert.IsType<LoginOutcome.InvalidCredentials>(await Login("alice", Pw.ToUpperInvariant()));
    }

    [PostgresFact]
    public async Task Hashes_made_with_a_lower_cost_are_upgraded_transparently()
    {
        await Start();
        await NewUser();
        var before = await Scalar<string>($"SELECT {U.PasswordHash} FROM {U.Table}");
        var stronger = new AuthService(Store, new PasswordService(2000), AuthSettings);

        Assert.IsType<LoginOutcome.Success>(await stronger.LoginAsync("alice", Pw, SessionClient.Web, "10.0.0.1"));

        var after = await Scalar<string>($"SELECT {U.PasswordHash} FROM {U.Table}");
        Assert.NotEqual(before, after);
        Assert.Equal(PasswordVerification.Success, new PasswordService(2000).Verify(after, Pw));
    }

    [PostgresFact]
    public async Task Change_password_requires_the_current_password_and_revokes_other_sessions()
    {
        await Start();
        await NewUser();
        var a = await LoginOk();
        var b = await LoginOk();
        var sessionA = (await Auth.AuthenticateAsync(a.Token))!;

        var wrong = await Auth.ChangePasswordAsync(sessionA, "not-the-password", "A-Brand-New-Passphrase-1");
        Assert.Equal(ChangePasswordStatus.InvalidCurrentPassword, wrong.Status);
        var weak = await Auth.ChangePasswordAsync(sessionA, Pw, "short");
        Assert.Equal(ChangePasswordStatus.WeakPassword, weak.Status);
        Assert.NotNull(await Auth.AuthenticateAsync(b.Token));

        var changed = await Auth.ChangePasswordAsync(sessionA, Pw, "A-Brand-New-Passphrase-1");
        Assert.Equal(ChangePasswordStatus.Changed, changed.Status);

        Assert.NotNull(await Auth.AuthenticateAsync(a.Token));   // the session that changed it survives
        Assert.Null(await Auth.AuthenticateAsync(b.Token));      // every other session is revoked
        Assert.IsType<LoginOutcome.InvalidCredentials>(await Login("alice", Pw, "10.0.0.5"));
        Assert.IsType<LoginOutcome.Success>(await Login("alice", "A-Brand-New-Passphrase-1", "10.0.0.5"));
    }

    [PostgresFact]
    public async Task Change_password_attempts_are_throttled_per_user_independently_of_sign_in_failures()
    {
        await Start();
        await NewUser();
        var session = (await Auth.AuthenticateAsync((await LoginOk()).Token))!;
        for (var i = 0; i < AuthSettings.MaxFailuresPerLoginSource; i++)
            await Auth.ChangePasswordAsync(session, "guess-" + i + "-xxxxxxxx", "A-Brand-New-Passphrase-1");
        Assert.Equal(ChangePasswordStatus.Throttled, (await Auth.ChangePasswordAsync(session, Pw, "A-Brand-New-Passphrase-1")).Status);
    }
}

public class SessionTests : IdentityTestBase
{
    [PostgresFact]
    public async Task Session_is_created_and_the_raw_token_is_never_stored()
    {
        await Start();
        var user = await NewUser();
        var ok = await LoginOk();

        var session = await Auth.AuthenticateAsync(ok.Token);
        Assert.NotNull(session);
        Assert.Equal(user.Id, session!.UserId);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {S.Table}"));
        var stored = await Scalar<byte[]>($"SELECT {S.TokenHash} FROM {S.Table}");
        Assert.Equal(SessionKeys.TokenHash(SessionKeys.Active(), ok.Token), stored);                              // the KEYED hash (identity integrity S-1) ...
        Assert.NotEqual(SessionToken.Hash(ok.Token), stored);                                                      // ... never the plain SHA-256 of the old design
        Assert.DoesNotContain(ok.Token, Convert.ToBase64String(stored));
    }

    [PostgresFact]
    public async Task Each_login_gets_a_new_unrelated_session_token()
    {
        await Start();
        await NewUser();
        var a = await LoginOk();
        var b = await LoginOk();
        Assert.NotEqual(a.Token, b.Token);
        Assert.NotEqual(a.SessionId, b.SessionId);
    }

    [PostgresFact]
    public async Task Unknown_malformed_and_tampered_tokens_do_not_authenticate()
    {
        await Start();
        await NewUser();
        var ok = await LoginOk();
        Assert.Null(await Auth.AuthenticateAsync(null));
        Assert.Null(await Auth.AuthenticateAsync(""));
        Assert.Null(await Auth.AuthenticateAsync("garbage"));
        Assert.Null(await Auth.AuthenticateAsync(SessionToken.Generate()));
        Assert.Null(await Auth.AuthenticateAsync(ok.Token[..^1] + (ok.Token[^1] == 'A' ? 'B' : 'A')));
        Assert.Null(await Auth.AuthenticateAsync("' OR 1=1 --aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [PostgresFact]
    public async Task Session_expires_after_the_idle_timeout()
    {
        await Start();
        await NewUser();
        var ok = await LoginOk();
        Clock.Advance(AuthSettings.SessionIdle - TimeSpan.FromSeconds(1));
        Assert.NotNull(await Auth.AuthenticateAsync(ok.Token));
        Clock.Advance(AuthSettings.SessionIdle + TimeSpan.FromSeconds(1));
        Assert.Null(await Auth.AuthenticateAsync(ok.Token));
    }

    [PostgresFact]
    public async Task Activity_extends_the_idle_expiry_but_never_beyond_the_absolute_limit()
    {
        await Start();
        await NewUser();
        var ok = await LoginOk();

        for (var i = 0; i < 20; i++) // 20 x 20 minutes = 6h20m of continuous use, far past the 30-minute idle timeout
        {
            Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.NotNull(await Auth.AuthenticateAsync(ok.Token));
        }

        Clock.Advance(AuthSettings.SessionAbsolute); // now beyond 12h since login
        Assert.Null(await Auth.AuthenticateAsync(ok.Token));
    }

    [PostgresFact]
    public async Task Idle_expiry_is_capped_at_the_absolute_expiry()
    {
        await Start();
        await NewUser();
        var ok = await LoginOk();
        Assert.True(ok.IdleExpiresAtUtc <= ok.AbsoluteExpiresAtUtc);

        for (var i = 0; i < 35; i++) { Clock.Advance(TimeSpan.FromMinutes(20)); Assert.NotNull(await Auth.AuthenticateAsync(ok.Token)); } // 11h40m of activity
        var late = (await Auth.AuthenticateAsync(ok.Token))!;
        Assert.Equal(ok.AbsoluteExpiresAtUtc, late.AbsoluteExpiresAtUtc);
        Clock.Advance(TimeSpan.FromMinutes(19));
        Assert.NotNull(await Auth.AuthenticateAsync(ok.Token));           // 11h59m
        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await Auth.AuthenticateAsync(ok.Token));              // past the absolute expiry, however recent the activity
    }

    [PostgresFact]
    public async Task Logout_revokes_the_session_server_side()
    {
        await Start();
        await NewUser();
        var ok = await LoginOk();
        var session = (await Auth.AuthenticateAsync(ok.Token))!;

        await Auth.LogoutAsync(session.SessionId);

        Assert.Null(await Auth.AuthenticateAsync(ok.Token));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {S.Table} WHERE {S.RevokedAtUtc} IS NOT NULL AND {S.RevokedReason} = 0"));
        await Auth.LogoutAsync(session.SessionId); // idempotent
    }

    [PostgresFact]
    public async Task Revocation_of_one_session_leaves_other_sessions_of_the_user_valid()
    {
        await Start();
        await NewUser();
        var a = await LoginOk();
        var b = await LoginOk();
        await Auth.LogoutAsync(a.SessionId);
        Assert.Null(await Auth.AuthenticateAsync(a.Token));
        Assert.NotNull(await Auth.AuthenticateAsync(b.Token));
    }

    [PostgresFact]
    public async Task Disabling_or_locking_a_user_revokes_all_sessions_immediately_and_permanently()
    {
        await Start();
        var user = await NewUser();
        var a = await LoginOk();
        var b = await LoginOk();

        Assert.Equal(SetStateOutcome.Changed, await Store.SetStateAsync(null, user.Id, UserState.Disabled));
        Assert.Null(await Auth.AuthenticateAsync(a.Token));
        Assert.Null(await Auth.AuthenticateAsync(b.Token));

        await Store.SetStateAsync(null, user.Id, UserState.Active);
        Assert.Null(await Auth.AuthenticateAsync(a.Token));   // reactivation does not resurrect old sessions

        var c = await LoginOk(ip: "10.0.0.7");
        await Store.SetStateAsync(null, user.Id, UserState.Locked);
        Assert.Null(await Auth.AuthenticateAsync(c.Token));
    }

    [PostgresFact]
    public async Task Administrator_flag_is_read_from_the_database_on_every_validation()
    {
        await Start();
        var admin = await NewUser("root", admin: true);
        var ok = await LoginOk("root");
        Assert.True((await Auth.AuthenticateAsync(ok.Token))!.IsIdentityAdministrator);

        await Exec($"UPDATE {U.Table} SET {U.IsIdentityAdministrator} = false WHERE {U.Id} = @i", ("i", admin.Id));
        Assert.False((await Auth.AuthenticateAsync(ok.Token))!.IsIdentityAdministrator);
    }

    [PostgresFact]
    public async Task Housekeeping_removes_only_long_expired_or_revoked_sessions()
    {
        await Start();
        await NewUser();
        var old = await LoginOk();
        await Auth.LogoutAsync(old.SessionId);
        Clock.Advance(TimeSpan.FromDays(8));
        var current = await LoginOk(ip: "10.0.0.8");

        var removed = await Store.PurgeAsync(TimeSpan.FromDays(7), TimeSpan.FromDays(1));

        Assert.True(removed >= 1);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {S.Table}"));
        Assert.NotNull(await Auth.AuthenticateAsync(current.Token));
    }
}

public class BruteForceProtectionTests : IdentityTestBase
{
    private async Task Fail(string login, string ip, int times)
    {
        for (var i = 0; i < times; i++) Assert.IsType<LoginOutcome.InvalidCredentials>(await Login(login, "wrong-password-" + i, ip));
    }

    [PostgresFact]
    public async Task Repeated_failures_block_that_client_for_the_account_even_with_the_correct_password()
    {
        await Start();
        await NewUser();
        await Fail("alice", "10.0.0.1", AuthSettings.MaxFailuresPerLoginSource);

        var blocked = Assert.IsType<LoginOutcome.Throttled>(await Login("alice", Pw, "10.0.0.1"));   // same client, same account
        Assert.True(blocked.RetryAfter > TimeSpan.Zero && blocked.RetryAfter <= AuthSettings.Block);
        // The weakness fixed in Phase 2.3C-1: the same failures no longer lock the real user out from any other client.
        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, "10.0.0.2"));
    }

    [PostgresFact]
    public async Task Blocking_does_not_reveal_whether_the_account_exists()
    {
        await Start();
        await NewUser();
        await Fail("alice", "10.0.0.1", AuthSettings.MaxFailuresPerLoginSource);
        await Fail("ghost", "10.0.0.1", AuthSettings.MaxFailuresPerLoginSource);

        var real = Assert.IsType<LoginOutcome.Throttled>(await Login("alice", Pw, "10.0.0.1"));
        var ghost = Assert.IsType<LoginOutcome.Throttled>(await Login("ghost", Pw, "10.0.0.1"));
        Assert.Equal(real.RetryAfter, ghost.RetryAfter);
    }

    [PostgresFact]
    public async Task Account_is_usable_again_after_the_block_expires()
    {
        await Start();
        await NewUser();
        await Fail("alice", "10.0.0.1", AuthSettings.MaxFailuresPerLoginSource);
        Assert.IsType<LoginOutcome.Throttled>(await Login("alice", Pw, "10.0.0.1"));

        Clock.Advance(AuthSettings.Block + TimeSpan.FromSeconds(1));

        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, "10.0.0.1"));
    }

    [PostgresFact]
    public async Task Failures_below_the_limit_do_not_block_and_success_resets_the_counter()
    {
        await Start();
        await NewUser();
        await Fail("alice", "10.0.0.1", AuthSettings.MaxFailuresPerLoginSource - 1);
        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, "10.0.0.1"));
        await Fail("alice", "10.0.0.1", AuthSettings.MaxFailuresPerLoginSource - 1);   // would exceed the limit if the success had not reset it
        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, "10.0.0.1"));
    }

    [PostgresFact]
    public async Task Failure_window_expiry_resets_the_counter()
    {
        await Start();
        await NewUser();
        await Fail("alice", "10.0.0.1", AuthSettings.MaxFailuresPerLoginSource - 1);
        Clock.Advance(AuthSettings.FailureWindow + TimeSpan.FromSeconds(1));
        await Fail("alice", "10.0.0.1", AuthSettings.MaxFailuresPerLoginSource - 1);
        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, "10.0.0.1"));
    }

    [PostgresFact]
    public async Task Credential_stuffing_from_one_source_blocks_that_source_only()
    {
        await Start();
        await NewUser("alice");
        await NewUser("bob");
        // One source tries many different accounts, each only once (never tripping a per-account block).
        for (var i = 0; i < AuthSettings.MaxFailuresPerSource; i++)
            Assert.IsType<LoginOutcome.InvalidCredentials>(await Login($"victim{i}", "leaked-password-1", "203.0.113.5"));

        Assert.IsType<LoginOutcome.Throttled>(await Login("alice", Pw, "203.0.113.5"));       // even a valid login is refused from this source
        Assert.IsType<LoginOutcome.Success>(await Login("bob", Pw, "198.51.100.7"));          // other sources are unaffected
    }

    [PostgresFact]
    public async Task A_successful_login_does_not_reset_the_source_counter()
    {
        await Start();
        await NewUser("mine");
        for (var i = 0; i < AuthSettings.MaxFailuresPerSource - 1; i++)
        {
            await Login($"victim{i}", "x-leaked-password", "203.0.113.5");
            if (i % 5 == 0) Assert.IsType<LoginOutcome.Success>(await Login("mine", Pw, "203.0.113.5")); // attacker interleaves a valid login
        }
        await Login("victim-last", "x-leaked-password", "203.0.113.5");
        Assert.IsType<LoginOutcome.Throttled>(await Login("mine", Pw, "203.0.113.5"));
    }

    [PostgresFact]
    public async Task Existing_sessions_survive_a_brute_force_block_on_the_account()
    {
        await Start();
        await NewUser();
        var ok = await LoginOk();
        await Fail("alice", "10.0.0.66", AuthSettings.MaxFailuresPerLoginSource);   // an attacker cannot log the real user out
        Assert.NotNull(await Auth.AuthenticateAsync(ok.Token));
    }

    [PostgresFact]
    public async Task Throttle_keys_are_stored_as_digests_not_as_logins_or_addresses()
    {
        await Start();
        await NewUser();
        await Fail("alice", "10.0.0.1", 1);
        await Fail("not-a-real-user", "10.0.0.1", 1);
        var rows = await Scalar<long>($"SELECT count(*) FROM {T.Table}");
        Assert.True(rows >= 2);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {T.Table} WHERE octet_length({T.KeyHash}) <> 32"));
        var dump = await Scalar<string>($"SELECT string_agg(encode({T.KeyHash},'escape'), '|') FROM {T.Table}");
        Assert.DoesNotContain("alice", dump);
        Assert.DoesNotContain("10.0.0.1", dump);
    }

    [PostgresFact]
    public async Task Housekeeping_removes_stale_throttle_rows()
    {
        await Start();
        await Fail("ghost", "10.0.0.1", 1);
        Clock.Advance(TimeSpan.FromDays(2));
        Assert.True(await Store.PurgeAsync(TimeSpan.FromDays(7), TimeSpan.FromDays(1)) >= 2);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {T.Table}"));
    }

    [PostgresFact]
    public async Task Administrative_state_change_applies_through_the_service_and_protects_the_acting_administrator()
    {
        await Start();
        var admin = await NewUser("root", admin: true);
        var other = await NewUser("alice");

        Assert.Equal(AuthService.SetStateStatus.OwnAccount, await Auth.SetUserStateAsync(admin.Id, admin.Id, UserState.Disabled));
        Assert.Equal(AuthService.SetStateStatus.NotFound, await Auth.SetUserStateAsync(admin.Id, Guid.NewGuid(), UserState.Disabled));
        Assert.Equal(AuthService.SetStateStatus.Changed, await Auth.SetUserStateAsync(admin.Id, other.Id, UserState.Disabled));
        Assert.Equal(UserState.Disabled, (await Store.FindUserByIdAsync(other.Id))!.State);
        Assert.Equal(UserState.Active, (await Store.FindUserByIdAsync(admin.Id))!.State);
    }
}
