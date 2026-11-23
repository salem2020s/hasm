using System.Net;
using System.Net.Http.Json;
using Hasm.Api.Auth;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TO = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTotp;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Owner decision D-235 (register item M2-TOTP) against real PostgreSQL with the ACTUAL restricted application role: a real LOGIN role created through the isolated role-administration account
/// and granted by the real <c>grant-system-runtime</c> routine. The MFA flows work under it; it cannot change secret material, delete, truncate or alter the store; no other role (a "reporting
/// role": every role other than the owner and the runtime account) reads any column of the store, and any such grant is a refused violation; the gate refuses the owner account and an over-granted
/// runtime account unless the opt-out is set; the grant routine converges, never touches the owner, never runs against a company database and is safe to re-run.
/// Role-capable disposable cluster only.
/// </summary>
public class SystemRuntimeLeastPrivilegeTests : CompanyRuntimeTestBase
{
    private const string RolePrefix = "hasm_srt_";
    private readonly InMemoryKekRing ring = new("k1", TestIdentityKeys.Master);
    private readonly List<string> testRoles = [];
    private string runtimeRole = null!, runtimePassword = null!;
    private HasmDatabaseOptions RuntimeOptions = null!;

    private static string Q(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    private static string RoleAdminConnection => Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar)!;

    private async Task RoleAdmin(string sql)
    {
        await using var conn = new NpgsqlConnection(RoleAdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(string Role, string Password)> NewLoginRole()
    {
        var role = RolePrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        lock (testRoles) testRoles.Add(role);
        await RoleAdmin($"CREATE ROLE {Q(role)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}'");
        return (role, password);
    }

    private HasmDatabaseOptions OptionsFor(string username, string password, string? database = null) => new()
    {
        Host = Options.Host, Port = Options.Port, SystemDatabaseName = database ?? Options.SystemDatabaseName, Username = username, Password = password,
        AdminUsername = Options.AdminUsername, AdminPassword = Options.AdminPassword, MaintenanceDatabase = Options.MaintenanceDatabase, SslMode = Options.SslMode, AllowInsecureTransport = true,
    };

    /// <summary>A migrated system database, a real restricted runtime role and the real grant routine.</summary>
    private async Task StartRestricted()
    {
        var reason = PostgresRoleTestEnvironment.Evaluate();
        if (reason is not null) throw new InvalidOperationException($"Role-isolation gate is closed: {reason}");
        await StartCompanies();
        (runtimeRole, runtimePassword) = await NewLoginRole();
        RuntimeOptions = OptionsFor(runtimeRole, runtimePassword);
        var granted = await SystemRuntimeAdministration.GrantAsync(RuntimeOptions);
        Assert.Empty(granted.Findings.Violations);
        Assert.Empty(granted.Findings.Notes);
    }

    private string RuntimeConnection => new NpgsqlConnectionStringBuilder(RuntimeOptions.BuildSystemConnectionString(admin: false)) { Pooling = false }.ConnectionString;

    private async Task<NpgsqlConnection> As(HasmDatabaseOptions options)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(options.BuildSystemConnectionString(admin: false)) { Pooling = false }.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task Denied(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null)
    {
        if (tx is not null) { await using var sp = new NpgsqlCommand("SAVEPOINT d", conn, tx); await sp.ExecuteNonQueryAsync(); }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        if (tx is not null) { await using var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT d", conn, tx); await rb.ExecuteNonQueryAsync(); }
    }

    private async Task<SystemRuntimeFindings> Findings(string? role = null, string? readAll = null, string? writeAll = null)
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        return readAll is null && writeAll is null
            ? await SystemRuntimePrivileges.FindAsync(conn, role ?? runtimeRole)
            : await SystemRuntimePrivileges.FindAsync(conn, role ?? runtimeRole, readAll ?? SystemRuntimePrivileges.PredefinedReader, writeAll ?? SystemRuntimePrivileges.PredefinedWriter);
    }

    private MfaStore RuntimeMfa(SystemRuntimeGate? gate = null) => new(() => RuntimeConnection, Clock, Audit, new TotpSecretProtector(ring), gate ?? new SystemRuntimeGate(false, Clock) { CheckLifetime = TimeSpan.Zero }, TestIdentityKeys.Instance);
    private long Step => Totp.StepFor(Clock.GetUtcNow());

    /// <summary>Everything the runtime role holds in the schema, table and column level, as one comparable text.</summary>
    private Task<string> AclOf(string role) => Scalar<string>("""
        SELECT COALESCE(string_agg(line, E'\n' ORDER BY line), '') FROM (
          SELECT c.relname || ':' || x.privilege_type AS line FROM pg_class c, aclexplode(c.relacl) x JOIN pg_roles r ON r.oid = x.grantee WHERE c.relnamespace = 'public'::regnamespace AND r.rolname = @r
          UNION ALL SELECT c.relname || '.' || a.attname || ':' || x.privilege_type FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid, aclexplode(a.attacl) x JOIN pg_roles r ON r.oid = x.grantee
              WHERE c.relnamespace = 'public'::regnamespace AND r.rolname = @r) v
        """, ("r", role));

    private static string ExpectedAcl() => string.Join("\n", SystemRuntimePrivileges.Tables.SelectMany(t =>
        t.Privileges.Select(p => $"{t.Table}:{p}").Concat((t.UpdateColumns ?? []).Select(c => $"{t.Table}.{c}:UPDATE"))).Order(StringComparer.Ordinal));

    [PostgresRoleFact]
    public async Task The_MFA_flows_work_under_the_actual_restricted_runtime_role_and_the_gate_accepts_it()
    {
        await StartRestricted();
        await using var store = new IdentityStore(RuntimeConnection, Clock, Audit, keys: TestIdentityKeys.Instance);                              // the identity store under the restricted role too, as in the API
        var auth = new AuthService(store, Passwords, AuthSettings);
        var created = await auth.CreateUserAsync("alice", "Alice", Pw, false);
        Assert.Equal(CreateUserStatus.Created, created.Status);
        var user = created.User!;
        var login = Assert.IsType<LoginOutcome.Success>(await auth.LoginAsync("alice", Pw, SessionClient.Web, "10.0.0.1"));   // a session issued under the restricted role (before enrolment: this plain AuthService has no second step)

        await using var mfa = RuntimeMfa();
        var secret = Totp.GenerateSecret();
        var id = await mfa.CreatePendingAsync(user.Id, secret, null);
        Assert.True(await mfa.ActivatePendingAsync(user.Id, id, Step, null));
        Assert.Equal(MfaState.Enrolled, await mfa.GetStateAsync(user.Id));
        using (var active = (await mfa.FindActiveSecretAsync(user.Id, default))!) Assert.Equal(secret, active.Secret);   // the runtime role reads and decrypts its users' secrets: that is its job
        Assert.True(await mfa.TryAdvanceLastStepAsync(id, Step + 1, default));
        Assert.False(await mfa.TryAdvanceLastStepAsync(id, Step + 1, default));                                  // replay still refused: enforcement is unchanged
        var stepUpSession = Guid.NewGuid();                                                                      // the enrolment changed the user (revision +1): the earlier session is stale, a session issued now takes the step-up
        await store.CreateSessionAsync(stepUpSession, user.Id, SessionToken.Generate(), SessionClient.Web, Clock.GetUtcNow().AddMinutes(5), Clock.GetUtcNow().AddHours(1));
        Assert.NotNull(await mfa.RecordStepUpAsync(stepUpSession, user.Id));
        var second = await mfa.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null);                         // re-enrolment: revoke the old pending, insert, activate, revoke the old active
        Assert.True(await mfa.ActivatePendingAsync(user.Id, second, Step + 2, null));
        Assert.Equal(1, await mfa.RevokeAllAsync(user.Id, null, "test"));

        Assert.True(await Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.EventType} BETWEEN 1330 AND 1337") >= 3);   // the audit rows were written by the restricted role, in the same transactions

        // the login challenges under the restricted role: issue, count an attempt, consume (the two UPDATE columns), and the housekeeping purge of an expired one (the DELETE of the catalog)
        await using var challenges = new MfaChallengeStore(() => RuntimeConnection, Clock);
        var issued = await challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1");
        var info = await challenges.TryBeginAttemptAsync(issued.Token, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1");
        Assert.NotNull(info);
        Assert.True(await challenges.ConsumeAsync(info!.Id));
        var stale = await challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1");
        Assert.Equal(2L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MfaChallenge.Table}"));
        Clock.Advance(TimeSpan.FromDays(3));                                                                     // both are long expired for the purge (a challenge's expiry is immutable, so time moves, not the row)
        await new IdentityHousekeepingService(store, challenges, Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityHousekeepingService>.Instance).RunOnceAsync(default);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MfaChallenge.Table}"));                        // the restricted role was allowed to DELETE them: wired, not dead code
        await using var conn = await As(RuntimeOptions);
        Assert.Equal(runtimeRole, await new NpgsqlCommand("SELECT current_user::text", conn).ExecuteScalarAsync());
        Assert.True(await SystemRuntimePrivileges.HasRequiredAccessAsync(conn, runtimeRole));
        var seen = await SystemRuntimePrivileges.FindAsync(conn, runtimeRole);                                   // the runtime role itself can run the check (what the API gate does)
        Assert.Empty(seen.Violations);
        Assert.Empty(seen.Notes);
        Assert.Equal(ExpectedAcl(), await AclOf(runtimeRole));                                                   // exactly the catalog, nothing else
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_cannot_change_secret_material_delete_truncate_or_alter_the_store_even_with_the_rewrap_flag_set()
    {
        await StartRestricted();
        var user = await NewUser();
        await using var mfa = RuntimeMfa();
        var secret = Totp.GenerateSecret();
        var id = await mfa.CreatePendingAsync(user.Id, secret, null);
        Assert.True(await mfa.ActivatePendingAsync(user.Id, id, Step, null));
        var before = await Scalar<string>($"SELECT encode({TO.Cipher} || {TO.Nonce} || {TO.Tag}, 'hex') || {TO.KekId} FROM {TO.Table} WHERE {TO.SecretId} = @i", ("i", id));

        await using (var conn = await As(RuntimeOptions))
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using (var flag = new NpgsqlCommand("SELECT set_config('hasm.totp_rewrap', 'on', true)", conn, tx)) await flag.ExecuteNonQueryAsync();   // any session can set the flag: it must not matter
            foreach (var (column, value) in new[] { (TO.Cipher, "decode(repeat('00', 20), 'hex')"), (TO.Nonce, "decode(repeat('00', 12), 'hex')"), (TO.Tag, "decode(repeat('00', 16), 'hex')"),
                         (TO.KekId, "'k9'"), (TO.UserId, "gen_random_uuid()"), (TO.SecretId, "gen_random_uuid()"), (TO.CreatedAtUtc, "now()") })
                await Denied(conn, $"UPDATE {TO.Table} SET {column} = {value}", tx);
            await Denied(conn, $"UPDATE {TO.Table} SET {TO.State} = 2, {TO.Cipher} = {TO.Cipher}", tx);         // a permitted column does not carry a forbidden one
            await Denied(conn, $"DELETE FROM {TO.Table}", tx);
            await Denied(conn, $"TRUNCATE {TO.Table}", tx);
            await Denied(conn, $"ALTER TABLE {TO.Table} DISABLE TRIGGER ALL", tx);
            await Denied(conn, $"ALTER TABLE {TO.Table} ADD COLUMN x int", tx);
            await Denied(conn, $"DROP TABLE {TO.Table}", tx);
            await Denied(conn, $"CREATE TABLE x_copy AS SELECT 1 AS a", tx);
            await Denied(conn, $"SELECT * FROM {SystemSchema.BackupLegalHold.Table}", tx);                       // a table the API never uses
            await Denied(conn, $"UPDATE {SystemSchema.MfaChallenge.Table} SET {SystemSchema.MfaChallenge.TokenHash} = {SystemSchema.MfaChallenge.TokenHash}", tx);   // only the attempt counter and the consumption time may change
            await Denied(conn, $"UPDATE {SystemSchema.CostCenterAssignment.Table} SET {SystemSchema.CostCenterAssignment.RoleId} = {SystemSchema.CostCenterAssignment.RoleId}", tx);   // only the two revocation columns
            await Denied(conn, $"DELETE FROM {U.Table}", tx);
            await Denied(conn, $"UPDATE {SystemSchema.Company.Table} SET {SystemSchema.Company.ProvisioningState} = {SystemSchema.Company.ProvisioningState}", tx);
        }

        // rewrap is an owner-account operation: the runtime role is refused by PostgreSQL, the owner succeeds, and the runtime role still decrypts afterwards
        ring.Add("k2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        ring.Activate("k2");
        var refused = await Assert.ThrowsAsync<PostgresException>(() => mfa.RewrapAsync("k2", 10, null));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Equal(before, await Scalar<string>($"SELECT encode({TO.Cipher} || {TO.Nonce} || {TO.Tag}, 'hex') || {TO.KekId} FROM {TO.Table} WHERE {TO.SecretId} = @i", ("i", id)));
        await using var owner = new MfaStore(Options.BuildSystemConnectionString(admin: true), Clock, Audit, new TotpSecretProtector(ring), keys: TestIdentityKeys.Instance);
        Assert.Equal((1, 0), ((await owner.RewrapAsync("k2", 10, null)) is var done ? (done.Rewrapped, done.Remaining) : default));
        using var active = (await mfa.FindActiveSecretAsync(user.Id, default))!;
        Assert.Equal(secret, active.Secret);
    }

    [PostgresRoleFact]
    public async Task A_reporting_role_reads_no_column_of_the_secret_store_and_any_access_given_to_another_role_is_a_refused_violation()
    {
        await StartRestricted();
        var user = await NewUser();
        await using var mfa = RuntimeMfa();
        var id = await mfa.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null);
        Assert.True(await mfa.ActivatePendingAsync(user.Id, id, Step, null));

        // a reporting role as an operator might create one: it may read the user table, and nothing of the secret store
        var (reporting, reportingPassword) = await NewLoginRole();
        await Exec($"GRANT USAGE ON SCHEMA public TO {Q(reporting)}");
        await Exec($"GRANT CONNECT ON DATABASE {Q(Options.SystemDatabaseName!)} TO {Q(reporting)}");   // PUBLIC no longer holds CONNECT: an operator's reporting role is granted it explicitly
        await Exec($"GRANT SELECT ON TABLE {U.Table} TO {Q(reporting)}");
        var reportingOptions = OptionsFor(reporting, reportingPassword);
        await using (var conn = await As(reportingOptions))
        {
            Assert.Equal(1L, await new NpgsqlCommand($"SELECT count(*) FROM {U.Table}", conn).ExecuteScalarAsync());
            await Denied(conn, $"SELECT * FROM {TO.Table}");
            await Denied(conn, $"SELECT count(*) FROM {TO.Table}");
            foreach (var column in new[] { TO.SecretId, TO.UserId, TO.Cipher, TO.Nonce, TO.Tag, TO.KekId, TO.State, TO.CreatedAtUtc, TO.ConfirmedAtUtc, TO.LastStep, TO.FailedConfirmations })
                await Denied(conn, $"SELECT {column} FROM {TO.Table}");
            await Denied(conn, $"SELECT u.{U.Id} FROM {U.Table} u JOIN {TO.Table} t ON t.{TO.UserId} = u.{U.Id}");
            await Denied(conn, $"COPY {TO.Table} TO STDOUT");
            await Denied(conn, $"UPDATE {TO.Table} SET {TO.State} = 2");
        }
        Assert.Empty((await Findings()).Violations);

        // any access given to another role is found by the check (table level, one column, PUBLIC, membership) and refused by the gate that the runtime role itself runs
        async Task Refused(string grant, string revoke, string code, bool asRoleAdmin = false)
        {
            if (asRoleAdmin) await RoleAdmin(grant); else await Exec(grant);
            Assert.Contains(code, (await Findings()).Violations);
            var ex = await Assert.ThrowsAsync<SystemRuntimePrivilegeException>(() => mfa.FindActiveSecretAsync(user.Id, default));
            Assert.Contains(code, ex.Codes);
            Assert.DoesNotContain(TO.Table, ex.Message);
            Assert.DoesNotContain(reporting, ex.Message);
            if (asRoleAdmin) await RoleAdmin(revoke); else await Exec(revoke);
            Assert.Empty((await Findings()).Violations);
        }
        await Refused($"GRANT SELECT ON TABLE {TO.Table} TO {Q(reporting)}", $"REVOKE ALL ON TABLE {TO.Table} FROM {Q(reporting)}", "totp_other_grantee");
        await Refused($"GRANT SELECT ({TO.Cipher}) ON TABLE {TO.Table} TO {Q(reporting)}", $"REVOKE ALL ON TABLE {TO.Table} FROM {Q(reporting)}", "totp_other_grantee");
        await Refused($"GRANT SELECT ({TO.KekId}) ON TABLE {TO.Table} TO PUBLIC", $"REVOKE ALL ON TABLE {TO.Table} FROM PUBLIC", "totp_other_grantee");
        await Refused($"GRANT {Q(runtimeRole)} TO {Q(reporting)}", $"REVOKE {Q(runtimeRole)} FROM {Q(reporting)}", "totp_reader_by_membership", asRoleAdmin: true);

        // the grant routine removes what PUBLIC holds, and REPORTS (it does not hide) what another role still holds
        await Exec($"GRANT SELECT ON TABLE {TO.Table} TO PUBLIC");
        await Exec($"GRANT SELECT ON TABLE {TO.Table} TO {Q(reporting)}");
        var again = await SystemRuntimeAdministration.GrantAsync(RuntimeOptions);
        Assert.Equal(["totp_other_grantee"], again.Findings.Violations);
        await using (var conn = await As(reportingOptions)) Assert.Equal(1L, await new NpgsqlCommand($"SELECT count(*) FROM {TO.Table}", conn).ExecuteScalarAsync());   // it is the operator's grant: still there, still reported
        await Exec($"REVOKE ALL ON TABLE {TO.Table} FROM {Q(reporting)}");
        Assert.Empty((await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);
        await using (var conn = await As(reportingOptions)) await Denied(conn, $"SELECT count(*) FROM {TO.Table}");
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_holds_CONNECT_only_on_the_system_database_and_a_regrant_of_CREATE_or_TEMPORARY_is_found_refused_and_removed_by_the_grant_routine()
    {
        await StartRestricted();
        var db = Q(Options.SystemDatabaseName!);
        var role = Q(runtimeRole);
        Assert.Equal("CONNECT", await Scalar<string>("SELECT COALESCE(string_agg(x.privilege_type, ',' ORDER BY x.privilege_type), '') FROM pg_database d, aclexplode(d.datacl) x JOIN pg_roles r ON r.oid = x.grantee WHERE d.datname = current_database() AND r.rolname = @r", ("r", runtimeRole)));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_database d, aclexplode(d.datacl) x WHERE d.datname = current_database() AND x.grantee = 0"));   // PUBLIC holds nothing on the database

        // each way back in is found, refused by the gate the runtime role itself runs, and removed again by the grant routine
        foreach (var (grant, viaPublic) in new[] { ($"GRANT TEMPORARY ON DATABASE {db} TO {role}", false), ($"GRANT CREATE ON DATABASE {db} TO {role}", false), ($"GRANT TEMPORARY ON DATABASE {db} TO PUBLIC", true), ($"GRANT CREATE ON DATABASE {db} TO PUBLIC", true) })
        {
            await Exec(grant);
            Assert.Equal(["database_create_or_temp"], (await Findings()).Violations);
            await using (var mfa = RuntimeMfa())
            {
                var ex = await Assert.ThrowsAsync<SystemRuntimePrivilegeException>(() => mfa.FindActiveSecretAsync(Guid.NewGuid(), default));
                Assert.Contains("database_create_or_temp", ex.Codes);
            }
            Assert.Empty((await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);
            Assert.False(viaPublic && await Scalar<bool>("SELECT has_database_privilege(@r::name, current_database(), 'TEMPORARY')", ("r", runtimeRole)));
            Assert.Empty((await Findings()).Violations);
        }
        await using (var rt = await As(RuntimeOptions)) Assert.Equal(1, await new NpgsqlCommand("SELECT 1", rt).ExecuteScalarAsync());   // CONNECT is kept
    }

    [PostgresRoleFact]
    public async Task The_restricted_runtime_role_cannot_create_a_temporary_table_or_a_pg_temp_object_that_would_shadow_a_system_table_such_as_the_audit_table()
    {
        await StartRestricted();
        await using var rt = await As(RuntimeOptions);
        await Denied(rt, $"CREATE TEMP TABLE {Ev.Table} (LIKE {Ev.Table})");                    // the shadowing attempt: an unqualified reference to the audit table would resolve to pg_temp first
        await Denied(rt, $"CREATE TEMPORARY TABLE {Ev.Table} (x int)");
        await Denied(rt, "CREATE TEMP TABLE zz_any_temp (x int)");
        await Denied(rt, "CREATE TEMP VIEW zz_any_view AS SELECT 1 AS x");
        await Denied(rt, "CREATE TEMP SEQUENCE zz_any_seq");
        await Denied(rt, "CREATE DOMAIN pg_temp.x AS int");
        await Denied(rt, "CREATE TYPE pg_temp.zz_t AS (a int)");
        await Denied(rt, "CREATE FUNCTION pg_temp.zz_f() RETURNS int LANGUAGE sql AS 'SELECT 1'");
        await Denied(rt, "CREATE SCHEMA zz_schema");                                             // and no new schema either (database-level CREATE)
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_class WHERE relname IN ('zz_any_temp', 'zz_any_view', 'zz_any_seq') OR (relname = @a AND relnamespace <> 'public'::regnamespace)", ("a", Ev.Table)));
    }

    [PostgresRoleFact]
    public async Task The_gate_refuses_the_owner_account_and_an_over_granted_runtime_role_and_the_opt_out_warns_loudly_instead_of_refusing()
    {
        await StartRestricted();
        var user = await NewUser();

        // the administrative (owner) account as the runtime account: refused, and the MFA store never touches a secret
        var ownerGate = new SystemRuntimeGate(false, Clock, Logs.CreateLogger("gate")) { CheckLifetime = TimeSpan.Zero };
        await using (var asOwner = new MfaStore(() => Options.BuildSystemConnectionString(admin: true), Clock, Audit, new TotpSecretProtector(ring), ownerGate, keys: TestIdentityKeys.Instance))
        {
            var ex = await Assert.ThrowsAsync<SystemRuntimePrivilegeException>(() => asOwner.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null));
            Assert.Contains("totp_owned_by_runtime", ex.Codes);
            Assert.Contains("totp_runtime_overgrant", ex.Codes);
            Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {TO.Table}"));
            Assert.Contains(Logs.Entries, e => e.Contains("REFUSED") && e.Contains("totp_owned_by_runtime"));
            Assert.Contains(Logs.Entries, e => e.Contains("overgrant:User"));                                   // other tables: reported
        }

        // the explicit opt-out: works, and says so at critical level on every check (never silent)
        var optOut = new LogCapture();
        await using (var allowed = new MfaStore(() => Options.BuildSystemConnectionString(admin: true), Clock, Audit, new TotpSecretProtector(ring),
                         new SystemRuntimeGate(true, Clock, optOut.CreateLogger("gate")) { CheckLifetime = TimeSpan.Zero }, keys: TestIdentityKeys.Instance))
        {
            await allowed.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null);
            Assert.Contains(optOut.Entries, e => e.Contains("UNSAFE") && e.Contains("AllowOverprivilegedSystemRuntime") && e.Contains("totp_owned_by_runtime"));
        }

        // the restricted role with more than the catalog on the secret store: every kind is refused; on another table it is reported and not refused
        await using var mfa = RuntimeMfa();
        Assert.Equal(MfaState.None, await mfa.GetStateAsync(user.Id));
        foreach (var extra in new[] { $"GRANT UPDATE ON TABLE {TO.Table} TO", $"GRANT DELETE ON TABLE {TO.Table} TO", $"GRANT TRUNCATE ON TABLE {TO.Table} TO", $"GRANT TRIGGER ON TABLE {TO.Table} TO",
                     $"GRANT REFERENCES ON TABLE {TO.Table} TO", $"GRANT UPDATE ({TO.Cipher}) ON TABLE {TO.Table} TO", $"GRANT UPDATE ({TO.KekId}) ON TABLE {TO.Table} TO", $"GRANT REFERENCES ({TO.UserId}) ON TABLE {TO.Table} TO" })
        {
            await Exec($"{extra} {Q(runtimeRole)}");
            var ex = await Assert.ThrowsAsync<SystemRuntimePrivilegeException>(() => mfa.GetStateAsync(user.Id));
            Assert.Equal(["totp_runtime_overgrant"], ex.Codes);
            Assert.Empty((await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);    // the routine converges back
            Assert.Equal(MfaState.None, await mfa.GetStateAsync(user.Id));
        }
        await Exec($"GRANT DELETE ON TABLE {U.Table} TO {Q(runtimeRole)}");
        await Exec($"GRANT SELECT ON TABLE {SystemSchema.BackupLegalHold.Table} TO {Q(runtimeRole)}");
        await Exec($"GRANT UPDATE ({SystemSchema.Company.ProvisioningState}) ON TABLE {SystemSchema.Company.Table} TO {Q(runtimeRole)}");
        var found = await Findings();
        Assert.Empty(found.Violations);
        Assert.Equal(["overgrant:BackupLegalHold", "overgrant:Company", "overgrant:User"], found.Notes);
        Assert.Equal(MfaState.None, await mfa.GetStateAsync(user.Id));                                           // reported, not refused

        // a role the database does not know is never accepted
        Assert.Contains("runtime_account_unknown", (await Findings("hasm_srt_no_such_role")).Violations);
    }

    [PostgresRoleFact]
    public async Task The_grant_routine_converges_on_the_catalog_never_touches_the_owner_never_runs_against_a_company_database_and_is_safe_to_rerun()
    {
        await StartRestricted();
        var owner = Options.AdminUsername!;
        Task<string> OwnerState() => Scalar<string>("""
            SELECT string_agg(c.relname || ':' || r.rolname || ':' || (SELECT string_agg(p, ',' ORDER BY p) FROM unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) p
                       WHERE has_table_privilege(@o::name, c.oid, p)), E'\n' ORDER BY c.relname)
            FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'r'
            """, ("o", owner));
        var ownerBefore = await OwnerState();
        Assert.Contains($"{TO.Table}:{owner}:DELETE,INSERT,REFERENCES,SELECT,TRIGGER,TRUNCATE,UPDATE", ownerBefore);
        Assert.Equal(ExpectedAcl(), await AclOf(runtimeRole));

        // whatever an earlier build or a manual step granted, the routine ends on exactly the catalog; a second run changes nothing
        await Exec($"GRANT ALL ON ALL TABLES IN SCHEMA public TO {Q(runtimeRole)}");
        await Exec($"GRANT SELECT ON ALL TABLES IN SCHEMA public TO PUBLIC");
        await Exec($"GRANT UPDATE ({TO.Cipher}, {TO.Nonce}) ON TABLE {TO.Table} TO {Q(runtimeRole)}");
        Assert.NotEmpty((await Findings()).Violations);
        Assert.Empty((await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);
        Assert.Equal(ExpectedAcl(), await AclOf(runtimeRole));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_class c, aclexplode(c.relacl) x WHERE c.relnamespace = 'public'::regnamespace AND x.grantee = 0"));
        var full = await Scalar<string>("SELECT string_agg(c.relname || c.relacl::text, ',' ORDER BY c.relname) FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relacl IS NOT NULL");
        var again = await SystemRuntimeAdministration.GrantAsync(RuntimeOptions);
        Assert.Empty(again.Findings.Violations);
        Assert.Empty(again.Findings.Notes);
        Assert.Equal(full, await Scalar<string>("SELECT string_agg(c.relname || c.relacl::text, ',' ORDER BY c.relname) FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relacl IS NOT NULL"));
        Assert.Equal(ownerBefore, await OwnerState());                                                           // the owner still owns and holds everything

        // refusals change nothing: the administrative account as the runtime account, an unknown account, and a company database
        var aclBefore = await AclOf(runtimeRole);
        Assert.Equal("runtime_account_is_admin_account", (await Assert.ThrowsAsync<SystemRuntimeGrantException>(() => SystemRuntimeAdministration.GrantAsync(Options))).Code);
        Assert.Equal("runtime_account_unknown", (await Assert.ThrowsAsync<SystemRuntimeGrantException>(() => SystemRuntimeAdministration.GrantAsync(OptionsFor("hasm_srt_no_such_role", "x")))).Code);
        var company = await ReadyCompany();
        var companyAclBefore = await InCompany<string>(company.PhysicalDatabaseName, "SELECT COALESCE(string_agg(c.relname || COALESCE(c.relacl::text, '-'), ',' ORDER BY c.relname), '') FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace");
        var onCompany = await Assert.ThrowsAsync<SystemRuntimeGrantException>(() => SystemRuntimeAdministration.GrantAsync(OptionsFor(runtimeRole, runtimePassword, company.PhysicalDatabaseName)));
        Assert.Equal("not_a_system_database", onCompany.Code);
        Assert.DoesNotContain(company.PhysicalDatabaseName, onCompany.Message);
        Assert.Equal(companyAclBefore, await InCompany<string>(company.PhysicalDatabaseName, "SELECT COALESCE(string_agg(c.relname || COALESCE(c.relacl::text, '-'), ',' ORDER BY c.relname), '') FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace"));
        Assert.Equal(aclBefore, await AclOf(runtimeRole));
        Assert.Equal(ownerBefore, await OwnerState());
    }

    [PostgresRoleFact]
    public async Task Review_findings_a_runtime_membership_database_wide_roles_grantable_privileges_and_schema_create_are_refused_and_the_command_converges()
    {
        await StartRestricted();
        var user = await NewUser();
        await using var mfa = RuntimeMfa();
        var id = await mfa.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null);
        Assert.True(await mfa.ActivatePendingAsync(user.Id, id, Step, null));
        var catalog = await AclOf(runtimeRole);
        var (reporting, reportingPassword) = await NewLoginRole();
        await Exec($"GRANT USAGE ON SCHEMA public TO {Q(reporting)}");
        await Exec($"GRANT CONNECT ON DATABASE {Q(Options.SystemDatabaseName!)} TO {Q(reporting)}");   // PUBLIC no longer holds CONNECT: an operator's reporting role is granted it explicitly
        var reportingOptions = OptionsFor(reporting, reportingPassword);

        // M1: ANY membership of the runtime account is refused (it can SET ROLE to the role, or inherit from it: for a superuser role that is every table). The cluster of these tests cannot create a superuser or a
        // BYPASSRLS role, so the rule is shown with an ordinary role: the decision is "any membership", not "a membership of a role that is a superuser", which is what makes it safe for the real case.
        var (group, _) = await NewLoginRole();
        await RoleAdmin($"GRANT {Q(group)} TO {Q(runtimeRole)}");
        Assert.Contains("runtime_role_membership", (await Findings()).Violations);
        var gated = await Assert.ThrowsAsync<SystemRuntimePrivilegeException>(() => mfa.FindActiveSecretAsync(user.Id, default));
        Assert.Contains("runtime_role_membership", gated.Codes);
        Assert.Equal("runtime_account_has_role_membership", (await Assert.ThrowsAsync<SystemRuntimeGrantException>(() => SystemRuntimeAdministration.GrantAsync(RuntimeOptions))).Code);
        Assert.Equal(catalog, await AclOf(runtimeRole));                                                         // the refusal changed nothing
        await RoleAdmin($"REVOKE {Q(group)} FROM {Q(runtimeRole)}");
        Assert.Empty((await Findings()).Violations);

        // M2: another role that reads (or writes) EVERY table through pg_read_all_data (pg_write_all_data) is refused, not just noted. The role administrator of these tests cannot grant a predefined role,
        // so two stand-in roles take their place in the same decision (the production call names the predefined ones); a real predefined-role member is NOT tested at database level.
        var (readAll, _) = await NewLoginRole(); var (writeAll, _) = await NewLoginRole();
        Assert.Empty((await Findings(readAll: readAll, writeAll: writeAll)).Violations);
        await RoleAdmin($"GRANT {Q(readAll)} TO {Q(reporting)}");
        Assert.Equal(["totp_reader_all_data"], (await Findings(readAll: readAll, writeAll: writeAll)).Violations);
        Assert.Empty((await Findings()).Violations);                                                             // with the predefined names the stand-in means nothing: the SQL names its roles by parameter only
        await RoleAdmin($"GRANT {Q(writeAll)} TO {Q(reporting)}");
        Assert.Equal(["totp_reader_all_data", "totp_writer_all_data"], (await Findings(readAll: readAll, writeAll: writeAll)).Violations);
        await RoleAdmin($"REVOKE {Q(readAll)} FROM {Q(reporting)}"); await RoleAdmin($"REVOKE {Q(writeAll)} FROM {Q(reporting)}");
        await RoleAdmin($"GRANT {Q(writeAll)} TO {Q(runtimeRole)}");
        Assert.Contains("runtime_reads_all_data", (await Findings(readAll: readAll, writeAll: writeAll)).Violations);                 // the runtime account itself
        await RoleAdmin($"REVOKE {Q(writeAll)} FROM {Q(runtimeRole)}");
        Assert.Empty((await Findings(readAll: readAll, writeAll: writeAll)).Violations);

        // L1: a privilege the runtime account holds WITH GRANT OPTION is an over-grant (table level and column level); the account can pass the store on, the command takes the passed-on grant away with it
        await Exec($"GRANT SELECT ON TABLE {TO.Table} TO {Q(runtimeRole)} WITH GRANT OPTION");
        Assert.Equal(["totp_runtime_overgrant"], (await Findings()).Violations);
        await using (var rt = await As(RuntimeOptions)) await new NpgsqlCommand($"GRANT SELECT ON TABLE {TO.Table} TO {Q(reporting)}", rt).ExecuteNonQueryAsync();
        await using (var conn = await As(reportingOptions)) Assert.Equal(1L, await new NpgsqlCommand($"SELECT count(*) FROM {TO.Table}", conn).ExecuteScalarAsync());   // the leak is real
        Assert.Contains("totp_other_grantee", (await Findings()).Violations);
        Assert.Empty((await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);        // converges (no "dependent privileges exist"), and the passed-on grant is gone
        await using (var conn = await As(reportingOptions)) await Denied(conn, $"SELECT count(*) FROM {TO.Table}");
        Assert.Equal(catalog, await AclOf(runtimeRole));
        await Exec($"GRANT SELECT ({TO.Cipher}) ON TABLE {TO.Table} TO {Q(runtimeRole)} WITH GRANT OPTION");
        Assert.Equal(["totp_runtime_overgrant"], (await Findings()).Violations);
        Assert.Empty((await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);
        Assert.Equal(catalog, await AclOf(runtimeRole));

        // L2: CREATE on the schema, for the runtime account or for PUBLIC (a database from before PostgreSQL 15), is refused; the command removes both
        await Exec($"GRANT CREATE ON SCHEMA public TO {Q(runtimeRole)}");
        Assert.Equal(["runtime_schema_create"], (await Findings()).Violations);
        await Exec("GRANT CREATE ON SCHEMA public TO PUBLIC");
        Assert.Empty((await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_namespace n, aclexplode(n.nspacl) x WHERE n.nspname = 'public' AND x.grantee = 0"));   // PUBLIC holds nothing on the schema
        await using (var rt = await As(RuntimeOptions)) await Denied(rt, "CREATE FUNCTION zz_trojan() RETURNS int LANGUAGE sql AS 'SELECT 1'");
        // role attributes: CREATEROLE is refused (the role administrator may set this one; CREATEDB and REPLICATION it cannot grant, they follow the same SQL)
        await RoleAdmin($"ALTER ROLE {Q(runtimeRole)} CREATEROLE");
        Assert.Equal(["runtime_role_attributes"], (await Findings()).Violations);
        await RoleAdmin($"ALTER ROLE {Q(runtimeRole)} NOCREATEROLE");
        Assert.Empty((await Findings()).Violations);
    }

    [PostgresRoleFact]
    public async Task The_company_MFA_policy_service_sets_reads_and_evaluates_under_the_restricted_runtime_role()
    {
        await StartRestricted();
        // fixtures through the owner connection (a company, its administrator and a plain member); the services under test run on the RUNTIME role only
        var company = Guid.NewGuid();
        await Exec($"INSERT INTO {SystemSchema.Company.Table} ({SystemSchema.Company.Id},{SystemSchema.Company.PhysicalDatabaseName},{SystemSchema.Company.ProvisioningState},{SystemSchema.Company.CreatedAtUtc},{SystemSchema.Company.StateChangedAtUtc}) VALUES (@i,@n,@s,now(),now())",
            ("i", company), ("n", Hasm.Infrastructure.Naming.PhysicalIdentifier.GenerateDatabaseName()), ("s", (short)Hasm.Infrastructure.Companies.ProvisioningState.Ready));
        var validator = new KnownScopeValidator();
        await using var ownerAccess = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, PermissionCatalog.Default, Audit, validator, keys: TestIdentityKeys.Instance);
        var admin = await NewUser("poladmin");
        Assert.Equal(AccessService.BootstrapStatus.Granted, await ownerAccess.BootstrapCompanyAdministratorAsync(admin.Login, company));
        var plain = await NewUser("polplain");
        Assert.True((await ownerAccess.GrantMembershipAsync(admin.Id, company, plain.Login)).IsOk);
        await Exec($"UPDATE {U.Table} SET {SystemSchema.UserMfa.State} = 2 WHERE {U.Id} = @i", ("i", admin.Id));      // the administrator has an authenticator (the lock-out guard of the policy)

        await using var access = new AccessService(RuntimeConnection, Clock, PermissionCatalog.Default, Audit, validator, keys: TestIdentityKeys.Instance);
        await using var identity = new IdentityStore(RuntimeConnection, Clock, Audit, keys: TestIdentityKeys.Instance);
        await using var policies = new MfaPolicyService(RuntimeConnection, Clock, Audit, access, identity);

        var none = (await policies.GetAsync(admin.Id, company)).Value!;                                          // no row yet: Off at revision 0
        Assert.Equal((CompanyMfaPolicyKind.Off, 0L), (none.Kind, none.Revision));
        var all = (await policies.SetAsync(admin.Id, company, 0, CompanyMfaPolicyKind.RequiredForAll, null)).Value!;      // the first row: INSERT ... ON CONFLICT DO NOTHING
        Assert.Equal((CompanyMfaPolicyKind.RequiredForAll, 1L), (all.Kind, all.Revision));
        Assert.True(await policies.RequiresMfaAsync(plain.Id, company));                                           // the evaluation of the login path
        var roles = (await policies.SetAsync(admin.Id, company, 1, CompanyMfaPolicyKind.RequiredForRoles, [PresetRoles.CompanyAdministratorId])).Value!;   // UPDATE of the four columns (after SELECT ... FOR UPDATE), DELETE and INSERT of the role list
        Assert.Equal((CompanyMfaPolicyKind.RequiredForRoles, 2L, 1), (roles.Kind, roles.Revision, roles.RoleIds.Count));
        Assert.True(await policies.RequiresMfaAsync(admin.Id, company));
        Assert.False(await policies.RequiresMfaAsync(plain.Id, company));
        var off = (await policies.SetAsync(admin.Id, company, 2, CompanyMfaPolicyKind.Off, null)).Value!;       // the role list is emptied again (DELETE)
        Assert.Equal((CompanyMfaPolicyKind.Off, 3L, 0), (off.Kind, off.Revision, off.RoleIds.Count));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MfaPolicyRole.Table}"));
        Assert.Equal(3L, await Scalar<long>($"SELECT count(*) FROM {U.Table} u JOIN {Ev.Table} e ON e.{Ev.EventType} = {MfaAuditEvents.PolicyChanged} WHERE u.{U.Id} = @i", ("i", admin.Id)));   // one audit event per change, written by the restricted role

        // and nothing beyond that: the key, the history and the table itself are out of reach of the runtime role
        await using var conn = await As(RuntimeOptions);
        await Denied(conn, $"UPDATE {SystemSchema.MfaPolicy.Table} SET {SystemSchema.MfaPolicy.CompanyId} = gen_random_uuid()");
        await Denied(conn, $"DELETE FROM {SystemSchema.MfaPolicy.Table}");
        await Denied(conn, $"TRUNCATE {SystemSchema.MfaPolicy.Table}");
        await Denied(conn, $"UPDATE {SystemSchema.MfaPolicyRole.Table} SET {SystemSchema.MfaPolicyRole.RoleId} = gen_random_uuid()");
        Assert.Empty((await SystemRuntimePrivileges.FindAsync(conn, runtimeRole)).Violations);
    }

    [PostgresRoleFact]
    public async Task The_owner_only_trigger_refuses_a_secret_change_by_any_other_role_even_with_the_update_grant_and_the_rewrap_flag_but_the_owner_and_the_progress_columns_work()
    {
        await StartRestricted();
        var user = await NewUser();
        await using var mfa = RuntimeMfa();
        var secret = Totp.GenerateSecret();
        var id = await mfa.CreatePendingAsync(user.Id, secret, null);
        Assert.True(await mfa.ActivatePendingAsync(user.Id, id, Step, null));
        string Material() => $"SELECT encode({TO.Cipher} || {TO.Nonce} || {TO.Tag}, 'hex') || {TO.KekId} FROM {TO.Table} WHERE {TO.SecretId} = '{id}'";
        var before = await Scalar<string>(Material());
        Assert.True(await mfa.TryAdvanceLastStepAsync(id, Step + 6, default));                                  // the verifier's own write, as in the API (before the mis-grant below: the gate would refuse a mis-granted account)

        // the runtime role is WRONGLY granted UPDATE on the four secret columns (the least-privilege check calls this a violation, but a mis-grant must not be enough to change a secret)
        await Exec($"GRANT UPDATE ({TO.Cipher}, {TO.Nonce}, {TO.Tag}, {TO.KekId}) ON TABLE {TO.Table} TO {Q(runtimeRole)}");
        Assert.Contains("totp_runtime_overgrant", (await Findings()).Violations);
        var (otherRole, otherPassword) = await NewLoginRole();                                                   // and so is another role (an operator's reporting account)
        await Exec($"GRANT USAGE ON SCHEMA public TO {Q(otherRole)}");
        await Exec($"GRANT CONNECT ON DATABASE {Q(Options.SystemDatabaseName!)} TO {Q(otherRole)}");
        await Exec($"GRANT SELECT, UPDATE ON TABLE {TO.Table} TO {Q(otherRole)}");
        foreach (var options in new[] { RuntimeOptions, OptionsFor(otherRole, otherPassword) })
        {
            await using var conn = await As(options);
            foreach (var (column, value) in new[] { (TO.Cipher, "decode(repeat('00', 20), 'hex')"), (TO.Nonce, "decode(repeat('00', 12), 'hex')"), (TO.Tag, "decode(repeat('00', 16), 'hex')"), (TO.KekId, "'k9'") })
            {
                await using var tx = await conn.BeginTransactionAsync();
                await using (var flag = new NpgsqlCommand("SELECT set_config('hasm.totp_rewrap', 'on', true)", conn, tx)) await flag.ExecuteNonQueryAsync();   // the rewrap flag is on: the 0031 layer alone would allow it
                await using var cmd = new NpgsqlCommand($"UPDATE {TO.Table} SET {column} = {value} WHERE {TO.SecretId} = '{id}'", conn, tx);
                var refused = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.RaiseException, refused.SqlState);
                Assert.Contains("only by the table owner", refused.MessageText);
            }
        }
        Assert.Equal(before, await Scalar<string>(Material()));                                                  // nothing changed

        // the progress columns, and a statement that does not change the material, still work for the runtime role (the guard looks at the four columns only)
        await using (var conn = await As(RuntimeOptions))
        {
            await using var ok = new NpgsqlCommand($"UPDATE {TO.Table} SET {TO.FailedConfirmations} = {TO.FailedConfirmations} + 1, {TO.LastStep} = {Step + 7} WHERE {TO.SecretId} = '{id}'", conn);
            Assert.Equal(1, await ok.ExecuteNonQueryAsync());
            await using var same = new NpgsqlCommand($"UPDATE {TO.Table} SET {TO.Cipher} = {TO.Cipher} WHERE {TO.SecretId} = '{id}'", conn);
            Assert.Equal(1, await same.ExecuteNonQueryAsync());                                                  // an unchanged value is no change
        }

        // the owner (the rewrap account) changes it, with the flag; without the flag the 0031 layer still refuses even the owner
        await using (var owner = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await owner.OpenAsync();
            await using (var noFlag = await owner.BeginTransactionAsync())
            {
                await using var cmd = new NpgsqlCommand($"UPDATE {TO.Table} SET {TO.KekId} = 'k8' WHERE {TO.SecretId} = '{id}'", owner, noFlag);
                var refused = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Contains("only through rewrap", refused.MessageText);
            }
            await using var tx = await owner.BeginTransactionAsync();
            await using (var flag = new NpgsqlCommand("SELECT set_config('hasm.totp_rewrap', 'on', true)", owner, tx)) await flag.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand($"UPDATE {TO.Table} SET {TO.KekId} = 'k8' WHERE {TO.SecretId} = '{id}'", owner, tx)) Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            await tx.RollbackAsync();
        }
        Assert.Equal(["totp_other_grantee"], (await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);   // the command takes the runtime role's mis-grant away; the other role's grant is the operator's: it stays and is reported
        await Exec($"REVOKE ALL ON TABLE {TO.Table} FROM {Q(otherRole)}");
        Assert.Empty((await Findings()).Violations);
    }

    [PostgresRoleFact]
    public async Task A_role_holding_TRIGGER_with_grant_option_cannot_pass_as_the_owner_by_granting_TRIGGER_to_itself()
    {
        await StartRestricted();
        var user = await NewUser();
        await using var mfa = RuntimeMfa();
        var id = await mfa.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null);
        Assert.True(await mfa.ActivatePendingAsync(user.Id, id, Step, null));
        string Material() => $"SELECT encode({TO.Cipher} || {TO.Nonce} || {TO.Tag}, 'hex') || {TO.KekId} FROM {TO.Table} WHERE {TO.SecretId} = '{id}'";
        var before = await Scalar<string>(Material());

        // a broad grant by an operator: SELECT, UPDATE and TRIGGER WITH GRANT OPTION to an ordinary login role (review of PR 210, M1)
        var (attacker, attackerPassword) = await NewLoginRole();
        await Exec($"GRANT USAGE ON SCHEMA public TO {Q(attacker)}");
        await Exec($"GRANT CONNECT ON DATABASE {Q(Options.SystemDatabaseName!)} TO {Q(attacker)}");
        await Exec($"GRANT SELECT, UPDATE ON TABLE {TO.Table} TO {Q(attacker)}");
        await Exec($"GRANT TRIGGER ON TABLE {TO.Table} TO {Q(attacker)} WITH GRANT OPTION");
        await using var conn = await As(OptionsFor(attacker, attackerPassword));
        await using (var self = new NpgsqlCommand($"GRANT TRIGGER ON TABLE {TO.Table} TO {Q(attacker)}", conn)) await self.ExecuteNonQueryAsync();   // a self-grant: accepted by PostgreSQL, creates a row granted by the role to itself (not grantable)
        await using (var seen = new NpgsqlCommand($"SELECT count(*) FROM information_schema.table_privileges WHERE table_name = '{TO.Table}' AND grantor = grantee AND privilege_type = 'TRIGGER' AND is_grantable = 'NO'", conn))
            Assert.Equal(1L, await seen.ExecuteScalarAsync());                                                   // the attack row exists, and the attacker sees it (the owner's row is grantable)
        foreach (var (column, value) in new[] { (TO.Cipher, "decode(repeat('00', 20), 'hex')"), (TO.Nonce, "decode(repeat('00', 12), 'hex')"), (TO.Tag, "decode(repeat('00', 16), 'hex')"), (TO.KekId, "'k9'") })
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using (var flag = new NpgsqlCommand("SELECT set_config('hasm.totp_rewrap', 'on', true)", conn, tx)) await flag.ExecuteNonQueryAsync();   // the rewrap flag: open to any role
            await using var cmd = new NpgsqlCommand($"UPDATE {TO.Table} SET {column} = {value} WHERE {TO.SecretId} = '{id}'", conn, tx);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, refused.SqlState);
            Assert.Contains("only by the table owner", refused.MessageText);
        }
        Assert.Equal(before, await Scalar<string>(Material()));                                                  // the material is unchanged
    }

    [PostgresRoleFact]
    public async Task The_owner_only_migration_adds_one_function_and_one_trigger_and_changes_nothing_else_in_the_catalog()
    {
        await StartRestricted();
        // the catalog as one comparable set of text rows: functions, triggers, tables with owner and ACL, columns with ACL, constraints, indexes, rules, policies, event triggers, schema and default ACLs
        const string Snapshot = """
            SELECT 'proc:' || p.proname || ':' || p.prosecdef::text || ':' || p.proowner::regrole::text || ':' || coalesce(p.proacl::text, '') || ':' || md5(p.prosrc) FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace
            UNION ALL SELECT 'trigger:' || t.tgname || ':' || t.tgrelid::regclass::text || ':' || t.tgenabled::text || ':' || pg_get_triggerdef(t.oid) FROM pg_trigger t WHERE NOT t.tgisinternal
            UNION ALL SELECT 'class:' || c.relname || ':' || c.relkind::text || ':' || c.relowner::regrole::text || ':' || coalesce(c.relacl::text, '') || ':' || coalesce(c.reloptions::text, '') || ':' || c.relrowsecurity::text FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace
            UNION ALL SELECT 'column:' || a.attrelid::regclass::text || ':' || a.attname || ':' || a.atttypid::regtype::text || ':' || a.attnotnull::text || ':' || coalesce(a.attacl::text, '') FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid WHERE c.relnamespace = 'public'::regnamespace AND a.attnum > 0 AND NOT a.attisdropped
            UNION ALL SELECT 'constraint:' || o.conname || ':' || o.conrelid::regclass::text || ':' || pg_get_constraintdef(o.oid) FROM pg_constraint o WHERE o.connamespace = 'public'::regnamespace
            UNION ALL SELECT 'index:' || i.indexrelid::regclass::text || ':' || pg_get_indexdef(i.indexrelid) FROM pg_index i JOIN pg_class c ON c.oid = i.indrelid WHERE c.relnamespace = 'public'::regnamespace
            UNION ALL SELECT 'rule:' || r.rulename || ':' || r.ev_class::regclass::text FROM pg_rewrite r JOIN pg_class c ON c.oid = r.ev_class WHERE c.relnamespace = 'public'::regnamespace
            UNION ALL SELECT 'policy:' || y.polname FROM pg_policy y
            UNION ALL SELECT 'eventtrigger:' || e.evtname FROM pg_event_trigger e
            UNION ALL SELECT 'schema:' || n.nspname || ':' || coalesce(n.nspacl::text, '') FROM pg_namespace n WHERE n.nspname = 'public'
            UNION ALL SELECT 'defaultacl:' || d.defaclobjtype::text || ':' || d.defaclacl::text FROM pg_default_acl d
            """;
        async Task<HashSet<string>> Take(NpgsqlConnection conn, NpgsqlTransaction tx)
        {
            var rows = new HashSet<string>(StringComparer.Ordinal);
            await using var cmd = new NpgsqlCommand(Snapshot, conn, tx);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) rows.Add(r.GetString(0));
            return rows;
        }
        await using var owner = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await owner.OpenAsync();
        await using var tx = await owner.BeginTransactionAsync();
        var with = await Take(owner, tx);
        // take the two objects of the migration away INSIDE a transaction that is rolled back: what is left is the catalog as it was before the migration
        foreach (var sql in new[] { $"DROP TRIGGER {SystemSchema.TotpGuard.TrOwnerOnly} ON {TO.Table}", $"DROP FUNCTION {SystemSchema.TotpGuard.FnOwnerOnly}()" })
            await using (var cmd = new NpgsqlCommand(sql, owner, tx)) await cmd.ExecuteNonQueryAsync();
        var without = await Take(owner, tx);
        await tx.RollbackAsync();
        Assert.Empty(without.Except(with));                                                                      // nothing the migration touched existed before in another form: additions only
        var added = with.Except(without).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, added.Count);
        Assert.StartsWith($"proc:{SystemSchema.TotpGuard.FnOwnerOnly}:false:", added.Single(a => a.StartsWith("proc:")));         // not SECURITY DEFINER
        Assert.Contains($"trigger:{SystemSchema.TotpGuard.TrOwnerOnly}:{TO.Table}:O:", added.Single(a => a.StartsWith("trigger:")));   // enabled, on the TOTP table
        Assert.Contains(with, r => r.StartsWith("trigger:") && r.Contains(" BEFORE DELETE OR UPDATE ON ") && r.Contains(TO.Table));    // the 0031 guard is still there, untouched
        Assert.Contains(with, r => r.StartsWith("proc:") && r.Contains(":false:") && r.Contains("f_55ca2d2a"));                            // and so is its function
    }

    private WebApplicationFactory<Program> Api(HasmDatabaseOptions runtime, bool? optOut, LogCapture logs) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Production");
        b.UseSetting("Hasm:Database:Host", runtime.Host);
        b.UseSetting("Hasm:Database:Port", runtime.Port.ToString());
        b.UseSetting("Hasm:Database:SystemDatabaseName", runtime.SystemDatabaseName);
        b.UseSetting("Hasm:Database:Username", runtime.Username);
        b.UseSetting("Hasm:Database:Password", runtime.Password);
        b.UseSetting("Hasm:Database:SslMode", runtime.SslMode.ToString());
        b.UseSetting("Hasm:Database:AllowInsecureTransport", "true");                                            // disposable loopback test server only
        b.UseSetting("Hasm:Database:AllowOverprivilegedSystemRuntime", optOut is null ? "false" : optOut.Value ? "true" : "false");   // always explicit here: the assembly default (see TestAssemblyDefaults) never decides these tests
        b.UseSetting("Hasm:Auth:Pbkdf2Iterations", "100000");
        b.ConfigureLogging(l => l.AddProvider(logs));
        b.ConfigureServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.AddSingleton(_ => Audit);
            s.AddSingleton<KekRing>(ring);
        });
    });

    private static async Task<(HttpStatusCode Status, string Body, bool Cookie)> Post(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Hasm-Request", "1");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.TryGetValues("Set-Cookie", out var v) && v.Any(c => c.StartsWith("__Host-hasm_session=")));
    }

    [PostgresRoleFact]
    public async Task The_real_api_signs_an_enrolled_user_in_with_the_second_factor_under_the_restricted_runtime_role_with_the_gate_on()
    {
        await StartRestricted();
        var user = await NewUser("enrolled");
        var secret = Totp.GenerateSecret();
        await using (var seed = new MfaStore(Options.BuildSystemConnectionString(admin: true), Clock, Audit, new TotpSecretProtector(ring), keys: TestIdentityKeys.Instance))
        {
            var id = await seed.CreatePendingAsync(user.Id, secret, null);
            Assert.True(await seed.ActivatePendingAsync(user.Id, id, Totp.StepFor(Clock.GetUtcNow()) - 5, null));
        }
        var logs = new LogCapture();
        using var api = Api(RuntimeOptions, optOut: false, logs);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

        var login = await Post(client, "/api/v1/auth/login", new { login = "enrolled", password = Pw, client = "web" });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.False(login.Cookie);                                                                              // enforcement unchanged: no password-only session for an enrolled user
        Assert.Contains("\"status\":\"mfa_required\"", login.Body);
        var challenge = System.Text.Json.JsonDocument.Parse(login.Body).RootElement.GetProperty("challenge").GetString();
        var wrong = await Post(client, "/api/v1/auth/mfa/verify", new { challenge, code = "000000", client = "web" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.False(wrong.Cookie);

        var again = await Post(client, "/api/v1/auth/login", new { login = "enrolled", password = Pw, client = "web" });
        challenge = System.Text.Json.JsonDocument.Parse(again.Body).RootElement.GetProperty("challenge").GetString();
        var code = Totp.Compute(secret, Totp.StepFor(Clock.GetUtcNow()));
        var verified = await Post(client, "/api/v1/auth/mfa/verify", new { challenge, code, client = "web" });
        Assert.Equal(HttpStatusCode.OK, verified.Status);
        Assert.True(verified.Cookie);                                                                            // the restricted role read the secret, advanced the step, issued the session and wrote the audit
        Assert.Equal(Totp.StepFor(Clock.GetUtcNow()), await Scalar<long>($"SELECT {TO.LastStep} FROM {TO.Table} WHERE {TO.UserId} = @u AND {TO.State} = 1", ("u", user.Id)));
        Assert.DoesNotContain(logs.Entries, e => e.Contains("REFUSED") || e.Contains("UNSAFE") || e.Contains("reported, not refused"));
    }

    [PostgresRoleFact]
    public async Task The_real_api_does_not_start_with_the_administrative_account_as_its_runtime_account_unless_the_opt_out_is_set_and_then_says_so()
    {
        await StartRestricted();
        await NewUser("plain");
        var ownerAsRuntime = OptionsFor(Options.AdminUsername!, Options.AdminPassword!);

        var refusedLogs = new LogCapture();
        using (var refused = Api(ownerAsRuntime, optOut: false, refusedLogs))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(async () => { using var c = refused.CreateClient(); await c.GetAsync("/api/v1/health"); });
            Assert.Contains(Flatten(ex), e => e is SystemRuntimePrivilegeException p && p.Codes.Contains("totp_owned_by_runtime"));
            Assert.Contains(refusedLogs.Entries, e => e.Contains("REFUSED") && e.Contains("totp_owned_by_runtime"));
            Assert.DoesNotContain(refusedLogs.Entries, e => e.Contains(TO.Table) || e.Contains(Options.AdminPassword!));
        }

        var optOutLogs = new LogCapture();
        using (var allowed = Api(ownerAsRuntime, optOut: true, optOutLogs))
        using (var client = allowed.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false }))
        {
            var login = await Post(client, "/api/v1/auth/login", new { login = "plain", password = Pw, client = "web" });
            Assert.Equal(HttpStatusCode.OK, login.Status);
            Assert.Contains(optOutLogs.Entries, e => e.Contains("UNSAFE") && e.Contains("AllowOverprivilegedSystemRuntime"));   // at start-up, critical, with the codes
        }

        // the restricted role with a later over-grant on the secret store: the API starts only while the store is least-privilege
        await Exec($"GRANT DELETE ON TABLE {TO.Table} TO {Q(runtimeRole)}");
        using (var overGranted = Api(RuntimeOptions, optOut: false, new LogCapture()))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(async () => { using var c = overGranted.CreateClient(); await c.GetAsync("/api/v1/health"); });
            Assert.Contains(Flatten(ex), e => e is SystemRuntimePrivilegeException p && p.Codes.SequenceEqual(["totp_runtime_overgrant"]));
        }
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException agg) foreach (var inner in agg.InnerExceptions.SelectMany(Flatten)) yield return inner;
        }
    }

    public override async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await base.DisposeAsync();                                                                               // drops this test's databases first, and with them every grant to the roles
        if (Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar) is not { Length: > 0 }) return;
        string[] roles;
        lock (testRoles) roles = [.. testRoles];
        foreach (var role in roles)
        {
            if (!role.StartsWith(RolePrefix, StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to drop a role this suite did not create.");
            await RoleAdmin($"DROP ROLE IF EXISTS {Q(role)}");
        }
    }
}
