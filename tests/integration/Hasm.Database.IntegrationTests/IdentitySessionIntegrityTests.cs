using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Hasm.Api.Auth;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SI = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionIntegrity;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using UI = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Identity integrity slice S-1 (docs/IDENTITY-INTEGRITY-PLAN.md, register item SEC-RT-SESSION, vector A) against real PostgreSQL with the ACTUAL restricted application role (a real LOGIN role granted by the
/// real <c>grant-system-runtime</c> routine, the role that holds INSERT and UPDATE on the session table) and the REAL API: a session row written by that role (minted, or an existing one changed) is not a
/// session; a password change or a revoke-all cannot be undone by restoring the old row; a missing key is a 503 and nothing is written; a key rotation honours the previous key only while it is listed; the
/// legitimate flows still work under the restricted role. The known single-logout residual (owner decision OD-5) is documented AND tested here: the test passes only while the residual exists as documented.
/// Role-capable disposable cluster only.
/// </summary>
public class IdentitySessionIntegrityTests : CompanyRuntimeTestBase
{
    private const string RolePrefix = "hasm_sit_";
    private readonly InMemoryKekRing ring = new("k1");
    private readonly List<string> testRoles = [];
    private string runtimeRole = null!, runtimePassword = null!;
    private HasmDatabaseOptions RuntimeOptions = null!;
    private IdentityKeys Keys => new(ring);

    private static string Q(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    private static string RoleAdminConnection => Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar)!;

    private async Task RoleAdmin(string sql)
    {
        await using var conn = new NpgsqlConnection(RoleAdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private HasmDatabaseOptions OptionsFor(string username, string password) => new()
    {
        Host = Options.Host, Port = Options.Port, SystemDatabaseName = Options.SystemDatabaseName, Username = username, Password = password,
        AdminUsername = Options.AdminUsername, AdminPassword = Options.AdminPassword, MaintenanceDatabase = Options.MaintenanceDatabase, SslMode = Options.SslMode, AllowInsecureTransport = true,
    };

    private string RuntimeConnection => new NpgsqlConnectionStringBuilder(RuntimeOptions.BuildSystemConnectionString(admin: false)) { Pooling = false }.ConnectionString;

    /// <summary>A migrated system database, a real restricted runtime role and the real grant routine.</summary>
    private async Task StartRestricted()
    {
        var reason = PostgresRoleTestEnvironment.Evaluate();
        if (reason is not null) throw new InvalidOperationException($"Role-isolation gate is closed: {reason}");
        await StartCompanies();
        runtimeRole = RolePrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        runtimePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        lock (testRoles) testRoles.Add(runtimeRole);
        await RoleAdmin($"CREATE ROLE {Q(runtimeRole)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{runtimePassword}'");
        RuntimeOptions = OptionsFor(runtimeRole, runtimePassword);
        var granted = await SystemRuntimeAdministration.GrantAsync(RuntimeOptions);
        Assert.Empty(granted.Findings.Violations);
    }

    private IdentityStore RuntimeStore(IdentityKeys? keys = null) => new(() => RuntimeConnection, Clock, Audit, keys ?? Keys);
    private AuthService RuntimeAuth(IdentityStore store) => new(store, Passwords, AuthSettings);
    private MfaStore RuntimeMfa(IdentityKeys? keys = null) => new(() => RuntimeConnection, Clock, Audit, new TotpSecretProtector(ring), new SystemRuntimeGate(false, Clock) { CheckLifetime = TimeSpan.Zero }, keys ?? Keys);

    /// <summary>Runs one statement AS THE RESTRICTED RUNTIME ROLE: the attacker of vector A (the role holds INSERT and UPDATE on the session table).</summary>
    private async Task<int> Attack(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(RuntimeConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private WebApplicationFactory<Program> Api(IEnumerable<string>? previousKeyIds = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Production");
        b.UseSetting("Hasm:Database:Host", RuntimeOptions.Host);
        b.UseSetting("Hasm:Database:Port", RuntimeOptions.Port.ToString());
        b.UseSetting("Hasm:Database:SystemDatabaseName", RuntimeOptions.SystemDatabaseName);
        b.UseSetting("Hasm:Database:Username", RuntimeOptions.Username);
        b.UseSetting("Hasm:Database:Password", RuntimeOptions.Password);
        b.UseSetting("Hasm:Database:SslMode", RuntimeOptions.SslMode.ToString());
        b.UseSetting("Hasm:Database:AllowInsecureTransport", "true");                                            // disposable loopback test server only
        b.UseSetting("Hasm:Database:AllowOverprivilegedSystemRuntime", "false");                                // the real restricted role, the gate on
        b.UseSetting("Hasm:Auth:Pbkdf2Iterations", "100000");
        if (previousKeyIds is not null) b.UseSetting("Hasm:Encryption:IdentityPreviousKeyIds", string.Join(",", previousKeyIds));
        b.ConfigureServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.AddSingleton(_ => Audit);
            s.AddSingleton<KekRing>(ring);
        });
    });

    private static HttpClient NewClient(WebApplicationFactory<Program> api) => api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    private static async Task<(HttpStatusCode Status, string Body)> Send(HttpClient client, HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Hasm-Request", "1");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<bool> IsAuthenticated(HttpClient client, string token)
    {
        var (status, body) = await Send(client, HttpMethod.Get, "/api/v1/auth/status", token);
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(body).RootElement.GetProperty("authenticated").GetBoolean();
    }

    private async Task<long> Rejections() => await Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.EventType} = {MfaAuditEvents.IdentityRecordRejected}");
    private async Task<bool> IsRevoked(Guid sessionId) => await Scalar<bool>($"SELECT {S.RevokedAtUtc} IS NOT NULL FROM {S.Table} WHERE {S.Id} = @i", ("i", sessionId));

    // ---------------------------------------------------------------- vector A: minted sessions

    [PostgresRoleFact]
    public async Task A_session_row_minted_by_the_runtime_role_is_refused_by_the_real_api()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        var admin = await NewUser("root", admin: true);
        await using var store = RuntimeStore();
        var legit = Assert.IsType<LoginOutcome.Success>(await RuntimeAuth(store).LoginAsync("victim", Pw, SessionClient.Web, "10.0.0.1"));
        using var api = Api();
        using var client = NewClient(api);
        Assert.True(await IsAuthenticated(client, legit.Token));                                                 // the harness works: a legitimate session is accepted through the real API

        var now = Clock.GetUtcNow();
        foreach (var (target, name) in new[] { (user.Id, "own user"), (admin.Id, "identity administrator") })
        {
            // (1) the old scheme: a chosen token, its plain SHA-256, no tag
            var chosen = SessionToken.Generate();
            Assert.Equal(1, await Attack($"INSERT INTO {S.Table} ({S.Id},{S.UserId},{S.TokenHash},{S.CreatedAtUtc},{S.LastSeenAtUtc},{S.IdleExpiresAtUtc},{S.AbsoluteExpiresAtUtc},{S.ClientKind}) VALUES (@i,@u,@h,@n,@n,@e,@e,0)",
                ("i", Guid.NewGuid()), ("u", target), ("h", SessionToken.Hash(chosen)), ("n", now), ("e", now.AddDays(1))));
            Assert.False(await IsAuthenticated(client, chosen), name);
            // (2) a forged key id and a random tag (the attacker holds no key, so this is the best he can write)
            var forged = SessionToken.Generate();
            Assert.Equal(1, await Attack($"INSERT INTO {S.Table} ({S.Id},{S.UserId},{S.TokenHash},{S.CreatedAtUtc},{S.LastSeenAtUtc},{S.IdleExpiresAtUtc},{S.AbsoluteExpiresAtUtc},{S.ClientKind},{SM.AuthMethods},{SM.VerifiedAtUtc},{SI.UserRevisionAtIssue},{SI.KeyId},{SI.Tag}) VALUES (@i,@u,@h,@n,@n,@e,@e,0,3,@n,0,'k1',@t)",
                ("i", Guid.NewGuid()), ("u", target), ("h", SessionToken.Hash(forged)), ("n", now), ("e", now.AddDays(1)), ("t", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
            Assert.False(await IsAuthenticated(client, forged), name);
            // (3) a copy of the legitimate session's row (valid tag and all) under a chosen token
            var copied = SessionToken.Generate();
            Assert.Equal(1, await Attack($"INSERT INTO {S.Table} ({S.Id},{S.UserId},{S.TokenHash},{S.CreatedAtUtc},{S.LastSeenAtUtc},{S.IdleExpiresAtUtc},{S.AbsoluteExpiresAtUtc},{S.ClientKind},{SM.AuthMethods},{SM.VerifiedAtUtc},{SI.UserRevisionAtIssue},{SI.KeyId},{SI.Tag}) " +
                $"SELECT @i,{S.UserId},@h,{S.CreatedAtUtc},{S.LastSeenAtUtc},{S.IdleExpiresAtUtc},{S.AbsoluteExpiresAtUtc},{S.ClientKind},{SM.AuthMethods},{SM.VerifiedAtUtc},{SI.UserRevisionAtIssue},{SI.KeyId},{SI.Tag} FROM {S.Table} WHERE {S.Id} = @src",
                ("i", Guid.NewGuid()), ("h", SessionToken.Hash(copied)), ("src", legit.SessionId)));
            Assert.False(await IsAuthenticated(client, copied), name);
        }
        Assert.True(await IsAuthenticated(client, legit.Token));                                                 // none of it disturbed the real session
    }

    // ---------------------------------------------------------------- vector A: tampered fields

    [PostgresRoleFact]
    public async Task Every_tampered_security_field_makes_the_session_refused_revoked_and_audited_without_crypto_detail()
    {
        await StartRestricted();
        await NewUser("victim");
        var other = await NewUser("other");
        var company = await Registry.RegisterPendingAsync();
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        using var api = Api();
        using var client = NewClient(api);
        var future = Clock.GetUtcNow().AddDays(30);
        var otherTag = await Scalar<byte[]>($"SELECT {SI.Tag} FROM {S.Table} WHERE {S.UserId} = @u LIMIT 1", ("u", (await Login(auth, "other")).User.Id));

        var cases = new (string Name, string Set, (string, object?)[] Params)[]
        {
            ("user id", $"{S.UserId} = @v", [("v", other.Id)]),
            ("auth methods and verified at (step-up bypass)", $"{SM.AuthMethods} = 3, {SM.VerifiedAtUtc} = @v", [("v", Clock.GetUtcNow())]),
            ("emergency flag", $"{SM.Emergency} = true", []),
            ("selected company", $"{S.SelectedCompanyId} = @v", [("v", company.Id)]),
            ("absolute expiry", $"{S.AbsoluteExpiresAtUtc} = @v", [("v", future)]),
            ("created at", $"{S.CreatedAtUtc} = @v", [("v", Clock.GetUtcNow().AddDays(-1))]),
            ("client kind", $"{S.ClientKind} = 1", []),
            ("key id", $"{SI.KeyId} = 'k2'", []),
            ("tag copied from another session", $"{SI.Tag} = @v", [("v", otherTag)]),
            ("revision at issue raised", $"{SI.UserRevisionAtIssue} = 5", []),
            ("tag removed", $"{SI.Tag} = NULL, {SI.KeyId} = NULL, {SI.UserRevisionAtIssue} = NULL", []),
        };
        foreach (var (name, set, parameters) in cases)
        {
            var login = await Login(auth, "victim");
            Assert.True(await IsAuthenticated(client, login.Token), name + " (baseline)");
            var before = await Rejections();
            var args = parameters.Append(("id", (object?)login.SessionId)).ToArray();
            Assert.Equal(1, await Attack($"UPDATE {S.Table} SET {set} WHERE {S.Id} = @id", args));
            var (status, body) = await Send(client, HttpMethod.Get, "/api/v1/auth/status", login.Token);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("{\"authenticated\":false}", body.Replace(" ", ""));                                    // the neutral answer: no cryptographic detail
            Assert.True(await IsRevoked(login.SessionId), name);                                                 // an invalid row is revoked unconditionally
            Assert.Equal(before + 1, await Rejections());                                                        // and the operator can see it
            Assert.False(await IsAuthenticated(client, login.Token), name);
        }
        Assert.Contains("rejected: tag", await Scalar<string>($"SELECT {Ev.Detail} FROM {Ev.Table} WHERE {Ev.EventType} = {MfaAuditEvents.IdentityRecordRejected} ORDER BY {Ev.Seq} DESC LIMIT 1"));
    }

    private static async Task<LoginOutcome.Success> Login(AuthService auth, string login) =>
        Assert.IsType<LoginOutcome.Success>(await auth.LoginAsync(login, Pw, SessionClient.Web, "10.0.0.1"));

    // ---------------------------------------------------------------- vector A: replay after revocation

    [PostgresRoleFact]
    public async Task A_password_change_and_a_revoke_all_cannot_be_undone_by_restoring_the_old_rows_and_the_revision_never_decreases()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        using var api = Api();
        using var client = NewClient(api);

        var changer = await Login(auth, "victim");
        var victim = await Login(auth, "victim");
        var oldTag = await Scalar<byte[]>($"SELECT {SI.Tag} FROM {S.Table} WHERE {S.Id} = @i", ("i", changer.SessionId));
        Assert.Equal(0L, await Scalar<long>($"SELECT {UI.SecurityRevision} FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id)));

        var session = (await auth.AuthenticateAsync(changer.Token))!;
        Assert.Equal(ChangePasswordStatus.Changed, (await auth.ChangePasswordAsync(session, Pw, "Replacement-Passphrase-3")).Status);
        Assert.Equal(1L, await Scalar<long>($"SELECT {UI.SecurityRevision} FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id)));
        Assert.True(await IsAuthenticated(client, changer.Token));                                               // the session that changed the password stays (re-tagged under the new revision)
        Assert.False(await IsAuthenticated(client, victim.Token));

        // the attacker restores the revoked victim row (clears the revocation): the revision at issue no longer matches
        Assert.Equal(1, await Attack($"UPDATE {S.Table} SET {S.RevokedAtUtc} = NULL, {S.RevokedReason} = NULL WHERE {S.Id} = @i", ("i", victim.SessionId)));
        Assert.False(await IsAuthenticated(client, victim.Token));
        Assert.True(await IsRevoked(victim.SessionId));                                                          // and it is revoked again, with its audit event
        // he restores the pre-change row of the changing session (old, otherwise valid tag and revision 0)
        Assert.Equal(1, await Attack($"UPDATE {S.Table} SET {SI.Tag} = @t, {SI.UserRevisionAtIssue} = 0 WHERE {S.Id} = @i", ("t", oldTag), ("i", changer.SessionId)));
        Assert.False(await IsAuthenticated(client, changer.Token));

        // revoke-all (an administrative revocation) raises the revision again; restoring a row does not help
        var again = (await auth.LoginAsync("victim", "Replacement-Passphrase-3", SessionClient.Web, "10.0.0.2")) as LoginOutcome.Success ?? throw new InvalidOperationException();
        Assert.True(await IsAuthenticated(client, again.Token));
        await store.RevokeUserSessionsAsync(user.Id, SessionRevocation.Administrative);
        Assert.Equal(2L, await Scalar<long>($"SELECT {UI.SecurityRevision} FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id)));
        Assert.Equal(1, await Attack($"UPDATE {S.Table} SET {S.RevokedAtUtc} = NULL, {S.RevokedReason} = NULL WHERE {S.Id} = @i", ("i", again.SessionId)));
        Assert.False(await IsAuthenticated(client, again.Token));

        // the database refuses to lower a revision (the way back to an old row's revision)
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Attack($"UPDATE {U.Table} SET {UI.SecurityRevision} = 0 WHERE {U.Id} = @u", ("u", user.Id)));
        Assert.Contains("never decreases", ex.MessageText);
        Assert.Equal(2L, await Scalar<long>($"SELECT {UI.SecurityRevision} FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id)));
    }

    // ---------------------------------------------------------------- OD-5: the documented residual

    [PostgresRoleFact]
    public async Task KNOWN_RESIDUAL_OD5_a_single_logout_can_be_undone_by_the_runtime_role_until_the_logout_record_slice_exists()
    {
        // Owner decision OD-5: a SINGLE logout is revoked by the revocation column, which the tag does not cover (it moves for every logout and revoke-all, and the plan's narrow logout record is a
        // later slice). A runtime role that clears the revocation of a logged-out session therefore revives it, for as long as its absolute expiry, with its ORIGINAL (untouched, valid) tag and
        // revision. This test pins that residual so it cannot be forgotten or silently claimed fixed; when the logout record exists, this test changes to expect refusal.
        await StartRestricted();
        await NewUser("victim");
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        using var api = Api();
        using var client = NewClient(api);

        var login = await Login(auth, "victim");
        Assert.True(await IsAuthenticated(client, login.Token));
        await auth.LogoutAsync(login.SessionId);
        Assert.False(await IsAuthenticated(client, login.Token));

        Assert.Equal(1, await Attack($"UPDATE {S.Table} SET {S.RevokedAtUtc} = NULL, {S.RevokedReason} = NULL WHERE {S.Id} = @i", ("i", login.SessionId)));
        Assert.True(await IsAuthenticated(client, login.Token));                                                 // THE RESIDUAL: the logged-out session lives again
        // ... but it is bounded: only a session the owner of the token already held, never a new or changed one, and only until its absolute expiry
        Clock.Advance(AuthSettings.SessionAbsolute + TimeSpan.FromMinutes(1));
        Assert.False(await IsAuthenticated(client, login.Token));
    }

    // ---------------------------------------------------------------- OD-1/OD-3: fail closed, rotation

    [PostgresRoleFact]
    public async Task A_missing_identity_key_is_service_unavailable_and_nothing_is_written_and_nothing_is_revoked()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        using var api = Api();
        using var client = NewClient(api);
        var login = await Login(auth, "victim");
        var sessions = await Scalar<long>($"SELECT count(*) FROM {S.Table}");

        ring.Unavailable = true;
        var status = await Send(client, HttpMethod.Get, "/api/v1/auth/status", login.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status.Status);
        Assert.Contains("encryption_unavailable", status.Body);
        var attempt = await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "victim", password = Pw, client = "web" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, attempt.Status);
        Assert.Equal(sessions, await Scalar<long>($"SELECT count(*) FROM {S.Table}"));                           // no session was minted without the key
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {S.Table} WHERE {S.RevokedAtUtc} IS NOT NULL"));    // and the outage revoked nothing
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => store.CreateSessionAsync(Guid.NewGuid(), user.Id, SessionToken.Generate(), SessionClient.Web, Clock.GetUtcNow().AddMinutes(5), Clock.GetUtcNow().AddHours(1)));

        ring.Unavailable = false;
        Assert.True(await IsAuthenticated(client, login.Token));                                                 // and the key coming back restores service
        await using var keyless = new IdentityStore(() => RuntimeConnection, Clock, Audit);                      // a store built without any key never issues or accepts a session
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keyless.FindActiveSessionAsync(login.Token));
    }

    [PostgresRoleFact]
    public async Task Key_rotation_accepts_the_previous_key_only_while_it_is_listed_and_rewrites_under_the_active_key()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        var session1 = await Login(auth, "victim");
        var session2 = await Login(auth, "victim");
        Assert.Equal("k1", await Scalar<string>($"SELECT {SI.KeyId} FROM {S.Table} WHERE {S.Id} = @i", ("i", session1.SessionId)));

        ring.Add("k2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        ring.Activate("k2");
        await using var rotating = RuntimeStore(new IdentityKeys(ring, ["k1"]));
        Assert.NotNull(await rotating.FindActiveSessionAsync(session1.Token));                                    // the previous key is honoured during the grace period
        var fresh = await RuntimeAuth(rotating).LoginAsync("victim", Pw, SessionClient.Web, "10.0.0.3") as LoginOutcome.Success;
        Assert.Equal("k2", await Scalar<string>($"SELECT {SI.KeyId} FROM {S.Table} WHERE {S.Id} = @i", ("i", fresh!.SessionId)));   // new sessions are written under the active key
        await using (var mfa = RuntimeMfa(new IdentityKeys(ring, ["k1"])))
            Assert.NotNull(await mfa.RecordStepUpAsync(session1.SessionId, user.Id));                            // a changed row is re-tagged under the active key
        Assert.Equal("k2", await Scalar<string>($"SELECT {SI.KeyId} FROM {S.Table} WHERE {S.Id} = @i", ("i", session1.SessionId)));
        Assert.NotNull(await rotating.FindActiveSessionAsync(session1.Token));                                    // and still verifies, now under k2 (token hash: still under k1, so the lookup needs k1 listed)

        await using var strict = RuntimeStore(new IdentityKeys(ring));                                            // the grace period ended: k1 is no longer listed
        Assert.Null(await strict.FindActiveSessionAsync(session2.Token));                                         // a k1 session is not honoured ...
        Assert.NotNull(await strict.FindActiveSessionAsync(fresh.Token));                                         // ... a k2 session is
    }

    // ---------------------------------------------------------------- legitimate flows under the restricted role, CAS

    [PostgresRoleFact]
    public async Task The_legitimate_flows_work_under_the_restricted_role_step_up_company_selection_logout_and_the_cas_refuses_a_stale_writer()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        var company = await Registry.RegisterPendingAsync();
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        await using var mfa = RuntimeMfa();

        var login = await Login(auth, "victim");
        Assert.NotNull(await auth.AuthenticateAsync(login.Token));
        Clock.Advance(TimeSpan.FromMinutes(1));
        var stamp = await mfa.RecordStepUpAsync(login.SessionId, user.Id);                                       // step-up: a tagged field written by compare-and-swap
        Assert.NotNull(stamp);
        var verified = (await auth.AuthenticateAsync(login.Token))!;
        Assert.Equal(stamp, verified.MfaVerifiedAtUtc);
        Assert.Null(await mfa.RecordStepUpAsync(login.SessionId, Guid.NewGuid()));                               // not the user's session: nothing written

        await using (var conn = new NpgsqlConnection(RuntimeConnection))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            Assert.True(await SessionRow.SelectCompanyAsync(conn, tx, Keys, login.SessionId, user.Id, company.Id, default));
            await tx.CommitAsync();
        }
        Assert.Equal(company.Id, (await auth.AuthenticateAsync(login.Token))!.SelectedCompanyId);
        await using (var conn = new NpgsqlConnection(RuntimeConnection))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await SessionRow.ClearCompanyAsync(conn, tx, Keys, user.Id, company.Id, Clock.GetUtcNow(), default);
            await tx.CommitAsync();
        }
        Assert.Null((await auth.AuthenticateAsync(login.Token))!.SelectedCompanyId);                              // membership ended: cleared and re-tagged, the session lives

        // compare-and-swap: a writer holding a stale copy of the row changes nothing
        await using (var conn = new NpgsqlConnection(RuntimeConnection))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var stale = (await SessionRow.LoadForUpdateAsync(conn, tx, login.SessionId, default))!;
            await tx.RollbackAsync();
            Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.NotNull(await mfa.RecordStepUpAsync(login.SessionId, user.Id));                               // somebody else changes the row after the stale copy was read
            await using var tx2 = await conn.BeginTransactionAsync();
            Assert.False(await SessionRow.UpdateAsync(conn, tx2, Keys, stale, stale with { SelectedCompanyId = company.Id }, default));
            await tx2.RollbackAsync();
        }
        Assert.Null((await auth.AuthenticateAsync(login.Token))!.SelectedCompanyId);

        await auth.LogoutAsync(login.SessionId);
        Assert.Null(await auth.AuthenticateAsync(login.Token));
    }

    [PostgresRoleFact]
    public async Task A_concurrent_step_up_and_revoke_all_never_leave_a_live_session()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        await using var mfa = RuntimeMfa();
        for (var i = 0; i < 12; i++)
        {
            var login = await Login(auth, "victim");
            Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAll(mfa.RecordStepUpAsync(login.SessionId, user.Id), store.RevokeUserSessionsAsync(user.Id, SessionRevocation.Administrative));
            Assert.Null(await store.FindActiveSessionAsync(login.Token));                                         // whichever won, the session is not live afterwards
        }
    }
}
