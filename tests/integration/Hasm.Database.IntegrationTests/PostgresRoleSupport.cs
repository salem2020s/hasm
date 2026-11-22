using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Extra gate for the tests that need REAL distinct PostgreSQL login identities (per-company runtime roles). They run only against a
/// separately prepared, isolated, disposable PostgreSQL service (the CI service container) that provides a role-administration account
/// with CREATEROLE (never superuser). Everything the ordinary gate requires still applies (loopback host, disposable acknowledgement,
/// hasm_test_ database names). The ordinary test role is never given CREATEROLE and the ordinary cleanup path never touches roles:
/// only the suites using this gate create roles, and they drop exactly the roles derived from their own hasm_test_ databases.
/// </summary>
public static class PostgresRoleTestEnvironment
{
    public const string RoleAdminConnectionVar = "HASM_TEST_PG_ROLE_ADMIN_CONNECTION";
    public const string RequireVar = "HASM_REQUIRE_PG_ROLE_TESTS";

    public static bool Required => Environment.GetEnvironmentVariable(RequireVar) == "1";

    public static string? Evaluate()
    {
        var roleAdmin = Environment.GetEnvironmentVariable(RoleAdminConnectionVar);
        var staticReason = Evaluate(Environment.GetEnvironmentVariable(PostgresTestEnvironment.ConnectionVar), roleAdmin,
            Environment.GetEnvironmentVariable(PostgresTestEnvironment.DisposableVar));
        if (staticReason is not null) return staticReason;
        var basic = PostgresTestEnvironment.Evaluate();   // identity of the ordinary connection
        if (basic is not null) return basic;
        var identity = PostgresClusterIdentity.Verify(roleAdmin!);
        return identity is null ? null : "Role-administration connection: " + identity;
    }

    /// <summary>Returns null when the role gate is open: the ordinary gate holds AND a role-administration connection on the same safe terms is supplied.</summary>
    public static string? Evaluate(string? adminConnection, string? roleAdminConnection, string? acknowledgement)
    {
        var basic = PostgresTestEnvironment.Evaluate(adminConnection, acknowledgement);
        if (basic is not null) return basic;
        if (string.IsNullOrWhiteSpace(roleAdminConnection)) return $"{RoleAdminConnectionVar} is not set (no separately prepared role-administration account).";
        // The role-admin connection must meet the same loopback + acknowledgement rules.
        var reason = PostgresTestEnvironment.Evaluate(roleAdminConnection, acknowledgement);
        return reason is null ? null : "Role-administration connection: " + reason;
    }
}

/// <summary>Skipped locally when no isolated role-capable PostgreSQL service is configured; a failure when HASM_REQUIRE_PG_ROLE_TESTS=1 (CI).</summary>
public sealed class PostgresRoleFactAttribute : FactAttribute
{
    public PostgresRoleFactAttribute()
    {
        var reason = PostgresRoleTestEnvironment.Evaluate();
        if (reason is not null && !PostgresRoleTestEnvironment.Required)
            Skip = $"Role-isolation test not run: {reason}";
    }
}

/// <summary>Company-runtime suite base: ordinary test database plus (optionally) the isolated role-administration account.</summary>
public abstract class CompanyRuntimeTestBase : IdentityTestBase
{
    protected CompanyRegistry Registry { get; private set; } = null!;
    protected InMemoryCompanyCredentialProvider Credentials { get; } = new();
    protected LogCapture Logs { get; } = new();
    private readonly string credentialRoot = Path.Combine(Path.GetTempPath(), "hasm-cred-it-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)));
    protected string CredentialDirectory => credentialRoot;
    /// <summary>True for suites that create PostgreSQL roles; they additionally require the isolated role-capable service.</summary>
    protected virtual bool RolesRequired => false;
    private readonly List<string> rolesToDrop = [];

    protected async Task StartCompanies()
    {
        if (RolesRequired)
        {
            var reason = PostgresRoleTestEnvironment.Evaluate();
            if (reason is not null) throw new InvalidOperationException($"Role-isolation gate is closed: {reason}");
        }
        await Start();
        Registry = new CompanyRegistry(Options.BuildSystemConnectionString(admin: true), NewTestName);
    }

    /// <summary>The test options plus the settings the company runtime needs. <paramref name="useRoleAdmin"/> adds the separate CREATEROLE account.</summary>
    protected HasmDatabaseOptions Configured(bool allowOverprivileged = false, bool useRoleAdmin = false, bool credentialDirectory = true)
    {
        NpgsqlConnectionStringBuilder? roleAdmin = useRoleAdmin
            ? new(Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar)) : null;
        return new HasmDatabaseOptions
        {
            Host = Options.Host, Port = Options.Port, SystemDatabaseName = Options.SystemDatabaseName, Username = Options.Username, Password = Options.Password,
            AdminUsername = Options.AdminUsername, AdminPassword = Options.AdminPassword, MaintenanceDatabase = Options.MaintenanceDatabase,
            SslMode = Options.SslMode, AllowInsecureTransport = true,
            RoleAdminUsername = roleAdmin?.Username, RoleAdminPassword = roleAdmin?.Password,
            CompanyCredentialDirectory = credentialDirectory ? credentialRoot : null,
            AllowOverprivilegedCompanyRuntime = allowOverprivileged,
        };
    }

    /// <summary>The company migration scripts a test company is provisioned and gated with when the test names none. Null (the default) means every embedded script; a compatibility test overrides it with the scripts of an older
    /// schema (a list it may extend after a migration, because the resolver reads the list at every schema check).</summary>
    protected virtual IReadOnlyList<MigrationScript>? CompanyScripts => null;

    /// <summary>Called once a test company has been provisioned and before anything uses it (default: nothing).</summary>
    protected virtual Task OnCompanyProvisioned(CompanyRecord company) => Task.CompletedTask;

    protected async Task<CompanyRecord> ReadyCompany(IReadOnlyList<MigrationScript>? scripts = null)
    {
        var c = await Registry.RegisterPendingAsync();
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, c.PhysicalDatabaseName);          // safety: only disposable test databases
        lock (rolesToDrop) rolesToDrop.Add(CompanyRuntimeRole.NameFor(c.PhysicalDatabaseName));
        await new CompanyProvisioner(Options, Registry, scripts ?? CompanyScripts).ProvisionAsync(c.Id);
        var ready = (await Registry.GetAsync(c.Id))!;
        await OnCompanyProvisioned(ready);
        return ready;
    }

    /// <summary>Resolver that reaches the company database with the single test principal (no per-company role exists on an ordinary test server).</summary>
    protected CompanyDatabaseResolver SinglePrincipalResolver(CompanyRecord? withCredentialFor = null, bool allowOverprivileged = true,
        IReadOnlyList<MigrationScript>? scripts = null)
    {
        if (withCredentialFor is not null)
            Credentials.Set(withCredentialFor.Id, new CompanyCredential(CompanyRuntimeRole.NameFor(withCredentialFor.PhysicalDatabaseName), "unused-in-single-principal-tests"));
        return new CompanyDatabaseResolver(Configured(allowOverprivileged), new CompanyRegistry(Options.BuildSystemConnectionString(admin: true)), Credentials, Clock,
            Logs.CreateLogger("Hasm.Company"), scripts ?? CompanyScripts)
        {
            ResolutionLifetime = TimeSpan.Zero,
            SchemaCheckLifetime = TimeSpan.Zero,
            ConnectionStringOverride = (db, _, _) => Options.BuildCompanyAdminConnectionString(db),
        };
    }

    /// <summary>Resolver that uses the real per-company role and the real credential file (role-capable suites only).</summary>
    protected CompanyDatabaseResolver RealResolver(bool allowOverprivileged = false) =>
        new(Configured(allowOverprivileged), new CompanyRegistry(Options.BuildSystemConnectionString(admin: true)),
            new FileCompanyCredentialStore(credentialRoot), Clock, Logs.CreateLogger("Hasm.Company"))
        { ResolutionLifetime = TimeSpan.Zero, SchemaCheckLifetime = TimeSpan.Zero };

    protected async Task<T> InCompany<T>(string database, string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    protected async Task InCompany(string database, string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    protected static string RoleOf(CompanyRecord c) => CompanyRuntimeRole.NameFor(c.PhysicalDatabaseName);

    /// <summary>Fails when any text mentions a physical name, a credential or a connection detail.</summary>
    protected void AssertNoLeak(IEnumerable<string?> texts, params CompanyRecord[] companies)
    {
        var secrets = new List<string> { Options.Password!, Options.AdminPassword!, "Host=", "Password=", "Username=", Options.SystemDatabaseName! };
        foreach (var c in companies) { secrets.Add(c.PhysicalDatabaseName); secrets.Add(RoleOf(c)); }
        foreach (var t in texts.Where(t => t is not null))
            foreach (var secret in secrets.Where(x => !string.IsNullOrEmpty(x)))
                Assert.DoesNotContain(secret, t!, StringComparison.OrdinalIgnoreCase);
    }

    public override async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();                                // pooled sessions of a runtime role would block DROP DATABASE
        await base.DisposeAsync();                                       // drops the generated hasm_test_ databases first
        try { if (Directory.Exists(credentialRoot)) Directory.Delete(credentialRoot, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!RolesRequired || Options is null) return;
        // Only the roles derived from this test's own hasm_test_ databases (hasm_rt_<hash>) are dropped, through the isolated role-admin account.
        var raw = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar));
        string[] roles;
        lock (rolesToDrop) roles = [.. rolesToDrop];
        await using var conn = new NpgsqlConnection(raw.ConnectionString);
        await conn.OpenAsync();
        foreach (var role in roles.Distinct())
        {
            if (!CompanyRuntimeRole.IsRuntimeRoleName(role)) throw new InvalidOperationException("Refusing to drop a role that is not a company runtime role.");
            await using var cmd = new NpgsqlCommand($"DROP ROLE IF EXISTS {new NpgsqlCommandBuilder().QuoteIdentifier(role)}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
