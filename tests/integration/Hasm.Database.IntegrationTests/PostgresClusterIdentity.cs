using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>What was actually observed on the server a test connection string points at.</summary>
public sealed record ClusterObservation(int? ServerPort, IReadOnlyList<string> Databases, string? MarkerFingerprint);

/// <summary>
/// Positive identification of the disposable PostgreSQL instance. The static checks of <see cref="PostgresTestEnvironment"/> (loopback host,
/// acknowledgement string) are necessary but spoofable, so before ANY destructive test the server itself must prove it is the disposable one:
///  1. the port is the disposable test port (5433), or 5432 only inside a GitHub Actions job (its isolated service container);
///  2. the server reports the same port it was reached on;
///  3. every database on the instance is either a built-in one or carries the test prefix (a permanent development cluster fails here);
///  4. a marker database written only when the disposable cluster/container was created holds a secret fingerprint equal to
///     HASM_TEST_PG_CLUSTER_FINGERPRINT (a different cluster cannot produce it).
/// Every failure, including an unreachable server, refuses. Messages never contain connection strings, names or fingerprints.
/// </summary>
public static class PostgresClusterIdentity
{
    public const int LocalTestPort = 5433;
    public const int GitHubServicePort = 5432;
    /// <summary>The private loopback port of the disposable cluster that a job on the repository's SELF-HOSTED Windows runner creates for itself (never 5432 and never the standing 5433 cluster).</summary>
    public const int SelfHostedCiPort = 5439;
    public const string MarkerDatabase = "hasm_test_cluster_identity";
    public const string FingerprintVar = "HASM_TEST_PG_CLUSTER_FINGERPRINT";
    public const int MinimumFingerprintLength = 32;

    private const string NotInspectedReason = "The server could not be inspected; refusing to run destructive tests.";

    private static readonly string[] BuiltInDatabases = ["postgres", "template0", "template1"];

    /// <summary>True only inside a GitHub-hosted Actions job. This alone never opens the gate; the server must still prove its identity.</summary>
    public static bool IsGitHubActionsJob(Func<string, string?> env) =>
        env("GITHUB_ACTIONS") == "true" && !string.IsNullOrEmpty(env("GITHUB_RUN_ID")) && env("RUNNER_ENVIRONMENT") == "github-hosted";

    /// <summary>
    /// True only inside a GitHub Actions job on a SELF-HOSTED runner whose workflow step created its own disposable cluster (the step exports HASM_CI_PG_DIR). Like <see cref="IsGitHubActionsJob"/> this alone never opens the gate:
    /// the server must still prove its identity (marker database, per-run fingerprint, database inventory, loopback, acknowledgement) and the port must be exactly <see cref="SelfHostedCiPort"/>.
    /// </summary>
    public static bool IsSelfHostedActionsJob(Func<string, string?> env) =>
        env("GITHUB_ACTIONS") == "true" && !string.IsNullOrEmpty(env("GITHUB_RUN_ID")) && env("RUNNER_ENVIRONMENT") == "self-hosted" && !string.IsNullOrEmpty(env("HASM_CI_PG_DIR"));

    /// <summary>Pure decision. Returns null when the observed server is verified as the disposable instance, otherwise the reason.</summary>
    public static string? Decide(int connectionPort, bool githubJob, string? expectedFingerprint, ClusterObservation? observed, bool selfHostedCiJob = false)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint) || expectedFingerprint.Length < MinimumFingerprintLength)
            return $"{FingerprintVar} is not set to the disposable cluster fingerprint.";
        var portAllowed = connectionPort == LocalTestPort || (githubJob && connectionPort == GitHubServicePort) || (selfHostedCiJob && connectionPort == SelfHostedCiPort);
        if (!portAllowed) return "The server port is not the disposable test port; refusing to run destructive tests.";
        if (observed is null) return NotInspectedReason;
        if (observed.ServerPort != connectionPort) return "The server reports a different port than the one connected to; refusing.";
        foreach (var name in observed.Databases)
            if (!BuiltInDatabases.Contains(name, StringComparer.Ordinal) && !name.StartsWith(PostgresTestEnvironment.DatabasePrefix, StringComparison.Ordinal))
                return "The server holds databases that are not disposable test databases; refusing.";
        if (observed.MarkerFingerprint is null) return "The server does not carry the disposable-cluster identity marker; refusing.";
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(observed.MarkerFingerprint));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expectedFingerprint));
        return CryptographicOperations.FixedTimeEquals(a, b) ? null : "The disposable-cluster identity does not match; refusing.";
    }

    private static readonly ConcurrentDictionary<string, (DateTime At, string? Reason)> Cache = new();

    /// <summary>Connects (read-only queries) and verifies the instance behind <paramref name="connectionString"/>. Fails closed on any error.</summary>
    public static string? Verify(string connectionString)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionString + "|" + Environment.GetEnvironmentVariable(FingerprintVar))));
        if (Cache.TryGetValue(key, out var hit) && hit.Reason is null && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30)) return null;
        string? reason;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 5, CommandTimeout = 10 };
            var githubJob = IsGitHubActionsJob(Environment.GetEnvironmentVariable);
            var selfHostedJob = IsSelfHostedActionsJob(Environment.GetEnvironmentVariable);
            var expected = Environment.GetEnvironmentVariable(FingerprintVar);
            // Port and fingerprint presence are decided BEFORE any connection: a non-disposable port (e.g. the permanent development
            // cluster) is refused without ever being contacted.
            reason = Decide(builder.Port, githubJob, expected, null, selfHostedJob);
            if (reason == NotInspectedReason) reason = Decide(builder.Port, githubJob, expected, Observe(builder), selfHostedJob);
        }
        catch (Exception)
        {
            reason = "The server could not be verified as the disposable test instance; refusing to run destructive tests.";
        }
        Cache[key] = (DateTime.UtcNow, reason);
        return reason;
    }

    private static ClusterObservation? Observe(NpgsqlConnectionStringBuilder builder)
    {
        int? serverPort;
        var databases = new List<string>();
        using (var conn = new NpgsqlConnection(builder.ConnectionString))
        {
            conn.Open();
            using (var cmd = new NpgsqlCommand("SELECT current_setting('port')", conn))
                serverPort = int.TryParse(cmd.ExecuteScalar() as string, out var p) ? p : null;
            using var list = new NpgsqlCommand("SELECT datname FROM pg_database", conn);
            using var reader = list.ExecuteReader();
            while (reader.Read()) databases.Add(reader.GetString(0));
        }
        string? fingerprint = null;
        if (databases.Contains(MarkerDatabase, StringComparer.Ordinal))
        {
            var marker = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = MarkerDatabase };
            using var conn = new NpgsqlConnection(marker.ConnectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT fingerprint FROM cluster_identity WHERE purpose = 'hasm-disposable-test-cluster'", conn);
            fingerprint = cmd.ExecuteScalar() as string;
        }
        return new ClusterObservation(serverPort, databases, fingerprint);
    }
}
