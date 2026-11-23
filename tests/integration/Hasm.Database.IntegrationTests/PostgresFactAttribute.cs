using System.Net;
using System.Security.Cryptography;
using Hasm.Infrastructure.Configuration;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Safety gate for destructive real-PostgreSQL tests. All of the following must hold, otherwise the
/// tests do not touch any database:
///  1. HASM_TEST_PG_ADMIN_CONNECTION is set (connection to the maintenance database).
///  2. HASM_TEST_PG_DISPOSABLE equals the exact acknowledgement string below (operator states the server is disposable).
///  3. The host is a loopback address (CI service container); remote/production hosts are refused.
/// Tests only ever create and drop databases named "hasm_test_" + random hex that they created themselves.
/// If HASM_REQUIRE_PG_TESTS=1 (CI), a failed gate FAILS the test instead of skipping it.
/// </summary>
public static class PostgresTestEnvironment
{
    public const string ConnectionVar = "HASM_TEST_PG_ADMIN_CONNECTION";
    public const string DisposableVar = "HASM_TEST_PG_DISPOSABLE";
    public const string DisposableAck = "yes-this-server-is-disposable";
    public const string RequireVar = "HASM_REQUIRE_PG_TESTS";
    public const string DatabasePrefix = "hasm_test_";

    public static bool Required => Environment.GetEnvironmentVariable(RequireVar) == "1";

    /// <summary>Returns null when the gate is open, otherwise the reason it is closed.</summary>
    public static string? Evaluate()
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionVar);
        var staticReason = Evaluate(raw, Environment.GetEnvironmentVariable(DisposableVar));
        // The static checks are spoofable: the server itself must also prove it is the disposable instance (see PostgresClusterIdentity).
        return staticReason ?? PostgresClusterIdentity.Verify(raw!);
    }

    public static string? Evaluate(string? raw, string? acknowledgement)
    {
        if (string.IsNullOrWhiteSpace(raw)) return $"{ConnectionVar} is not set.";
        if (acknowledgement != DisposableAck)
            return $"{DisposableVar} is not set to the disposable-server acknowledgement.";
        string? host;
        try { host = new NpgsqlConnectionStringBuilder(raw).Host; }
        catch (Exception) { return $"{ConnectionVar} is not a valid connection string."; }
        if (!IsLoopback(host)) return "Test database host is not a loopback address; refusing to run destructive tests.";
        return null;
    }

    private static bool IsLoopback(string? host) =>
        host is not null && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)));

    public static string NewTestDatabaseName() =>
        DatabasePrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
}

/// <summary>Real-PostgreSQL test: skipped locally when the safety gate is closed, failed when HASM_REQUIRE_PG_TESTS=1.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        var reason = PostgresTestEnvironment.Evaluate();
        if (reason is not null && !PostgresTestEnvironment.Required)
            Skip = $"PostgreSQL integration test not run: {reason}";
    }
}

/// <summary>Theory variant of <see cref="PostgresFactAttribute"/>.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        var reason = PostgresTestEnvironment.Evaluate();
        if (reason is not null && !PostgresTestEnvironment.Required)
            Skip = $"PostgreSQL integration test not run: {reason}";
    }
}

/// <summary>Creates a throwaway database per test and drops only that database afterwards.</summary>
public abstract class PostgresTestBase : IAsyncLifetime
{
    protected string DatabaseName { get; } = PostgresTestEnvironment.NewTestDatabaseName();
    protected HasmDatabaseOptions Options { get; private set; } = null!;

    public Task InitializeAsync()
    {
        var reason = PostgresTestEnvironment.Evaluate();
        if (reason is not null)
        {
            if (PostgresTestEnvironment.Required)
                throw new InvalidOperationException($"HASM_REQUIRE_PG_TESTS=1 but the PostgreSQL test gate is closed: {reason}");
            return Task.CompletedTask; // test is skipped by PostgresFact
        }

        var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresTestEnvironment.ConnectionVar));
        Options = new HasmDatabaseOptions
        {
            Host = admin.Host,
            Port = admin.Port,
            SystemDatabaseName = DatabaseName,
            Username = admin.Username,
            Password = admin.Password,
            AdminUsername = admin.Username,
            AdminPassword = admin.Password,
            MaintenanceDatabase = admin.Database ?? "postgres",
            SslMode = admin.SslMode,
            // The disposable CI service container has no TLS; production code rejects this unless opted in.
            AllowInsecureTransport = true,
        };
        return Task.CompletedTask;
    }

    private readonly List<string> companyDatabases = [];

    /// <summary>Name generator for test companies: always the disposable prefix; the name is remembered and dropped on dispose.</summary>
    protected string NewTestName()
    {
        var name = PostgresTestEnvironment.NewTestDatabaseName();
        lock (companyDatabases) companyDatabases.Add(name);
        return name;
    }

    /// <summary>The company database names this test generated (all carry the disposable prefix); used by suites that must clean up related objects.</summary>
    protected IReadOnlyList<string> GeneratedDatabaseNames { get { lock (companyDatabases) return [.. companyDatabases]; } }

    public virtual async Task DisposeAsync()
    {
        if (Options is null) return;
        string[] names;
        lock (companyDatabases) names = [.. companyDatabases, DatabaseName];
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        foreach (var name in names)
        {
            // Only databases this test generated itself, and only with the disposable prefix, are ever dropped.
            if (!name.StartsWith(PostgresTestEnvironment.DatabasePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to drop a database that is not a test database.");
            // Through the product's drop helper: an autovacuum worker in the database refuses a bare forced drop with 42501 for a non-superuser; the helper's fallback does not need to terminate it.
            // The brief retry stays for what the helper does not cover (a session of a role the account does not hold that is just leaving). Still only ever drops the test's own, prefixed database.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await TestDatabaseDrop.ExecuteAsync(conn, name, ifExists: true);
                    break;
                }
                catch (PostgresException ex) when (ex.SqlState == "42501" && attempt < 10)
                {
                    await Task.Delay(300);
                }
            }
        }
    }

    protected async Task<T> Scalar<T>(string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    protected async Task Exec(string sql, params (string, object)[] ps)
    {
        await BeforeExecAsync(sql);
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
        await AfterExecAsync(sql);
    }

    /// <summary>Hook after a successful <see cref="Exec"/> (default: nothing). The identity suites re-tag users after a direct write to the user table, which stands for "the row was set by a legitimate writer".</summary>
    protected virtual Task AfterExecAsync(string sql) => Task.CompletedTask;

    /// <summary>Hook before <see cref="Exec"/> (default: nothing).</summary>
    protected virtual Task BeforeExecAsync(string sql) => Task.CompletedTask;
}
