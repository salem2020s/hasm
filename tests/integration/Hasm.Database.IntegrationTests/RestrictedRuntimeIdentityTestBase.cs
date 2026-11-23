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
using UT = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag;


namespace Hasm.Database.IntegrationTests;

/// <summary>The harness of the identity-integrity suites: a migrated system database, a REAL restricted runtime login role granted by the real <c>grant-system-runtime</c> routine, the API on the same key ring, and a writer that runs statements AS that role (the attacker of the identity plan). Role-capable disposable cluster only.</summary>
public abstract class RestrictedRuntimeIdentityTestBase : CompanyRuntimeTestBase
{
    protected const string RolePrefix = "hasm_sit_";
    protected readonly InMemoryKekRing ring = new("k1", TestIdentityKeys.Master);
    protected readonly List<string> testRoles = [];
    protected string runtimeRole = null!, runtimePassword = null!;
    protected HasmDatabaseOptions RuntimeOptions = null!;
    protected IdentityKeys? suiteKeys;
    protected IdentityKeys Keys => suiteKeys ??= new(ring);
    protected override IdentityKeys SessionKeys => Keys;                                                      // the users of this suite are tagged under the ring of its own API

    protected static string Q(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    protected static string RoleAdminConnection => Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar)!;

    protected async Task RoleAdmin(string sql)
    {
        await using var conn = new NpgsqlConnection(RoleAdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    protected HasmDatabaseOptions OptionsFor(string username, string password) => new()
    {
        Host = Options.Host, Port = Options.Port, SystemDatabaseName = Options.SystemDatabaseName, Username = username, Password = password,
        AdminUsername = Options.AdminUsername, AdminPassword = Options.AdminPassword, MaintenanceDatabase = Options.MaintenanceDatabase, SslMode = Options.SslMode, AllowInsecureTransport = true,
    };

    protected string RuntimeConnection => new NpgsqlConnectionStringBuilder(RuntimeOptions.BuildSystemConnectionString(admin: false)) { Pooling = false }.ConnectionString;

    /// <summary>A migrated system database, a real restricted runtime role and the real grant routine.</summary>
    protected async Task StartRestricted()
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

    protected IdentityStore RuntimeStore(IdentityKeys? keys = null) => new(() => RuntimeConnection, Clock, Audit, keys ?? Keys);
    protected AuthService RuntimeAuth(IdentityStore store) => new(store, Passwords, AuthSettings);
    protected MfaStore RuntimeMfa(IdentityKeys? keys = null) => new(() => RuntimeConnection, Clock, Audit, new TotpSecretProtector(ring), new SystemRuntimeGate(false, Clock) { CheckLifetime = TimeSpan.Zero }, keys ?? Keys);

    /// <summary>Runs one statement AS THE RESTRICTED RUNTIME ROLE: the attacker of vector A (the role holds INSERT and UPDATE on the session table).</summary>
    protected async Task<int> Attack(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(RuntimeConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    protected WebApplicationFactory<Program> Api(IEnumerable<string>? previousKeyIds = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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

    protected static HttpClient NewClient(WebApplicationFactory<Program> api) => api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    protected static async Task<(HttpStatusCode Status, string Body)> Send(HttpClient client, HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Hasm-Request", "1");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    protected static async Task<bool> IsAuthenticated(HttpClient client, string token)
    {
        var (status, body) = await Send(client, HttpMethod.Get, "/api/v1/auth/status", token);
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(body).RootElement.GetProperty("authenticated").GetBoolean();
    }

    protected async Task<long> Rejections() => await Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.EventType} = {MfaAuditEvents.IdentityRecordRejected}");
    protected async Task<bool> IsRevoked(Guid sessionId) => await Scalar<bool>($"SELECT {S.RevokedAtUtc} IS NOT NULL FROM {S.Table} WHERE {S.Id} = @i", ("i", sessionId));

    // ---------------------------------------------------------------- vector A: minted sessions


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
