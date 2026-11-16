namespace Hasm.Database.IntegrationTests;

/// <summary>Always run (no database needed): prove the destructive-test gate refuses unsafe targets.</summary>
public class SafetyGateTests
{
    private const string Ack = PostgresTestEnvironment.DisposableAck;

    [Theory]
    [InlineData("Host=localhost;Username=u;Password=p;Database=postgres")]
    [InlineData("Host=127.0.0.1;Username=u;Password=p")]
    [InlineData("Host=::1;Username=u;Password=p")]
    public void Loopback_with_acknowledgement_opens_the_gate(string cs) =>
        Assert.Null(PostgresTestEnvironment.Evaluate(cs, Ack));

    [Theory]
    [InlineData("Host=db.prod.example.com;Username=u;Password=p")]
    [InlineData("Host=10.0.0.5;Username=u;Password=p")]
    [InlineData("Host=localhost.evil.example;Username=u;Password=p")]
    public void Non_loopback_hosts_are_refused_even_when_acknowledged(string cs) =>
        Assert.NotNull(PostgresTestEnvironment.Evaluate(cs, Ack));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    public void Missing_or_wrong_acknowledgement_is_refused(string? ack) =>
        Assert.NotNull(PostgresTestEnvironment.Evaluate("Host=localhost;Username=u;Password=p", ack));

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("not a connection string;;;=")]
    public void Missing_or_invalid_connection_is_refused(string? cs) =>
        Assert.NotNull(PostgresTestEnvironment.Evaluate(cs, Ack));

    [Fact]
    public void Gate_messages_do_not_echo_the_connection_string()
    {
        var reason = PostgresTestEnvironment.Evaluate("Host=db.prod.example.com;Username=u;Password=TopSecret", Ack);
        Assert.DoesNotContain("TopSecret", reason);
    }

    [Fact]
    public void Test_database_names_use_the_disposable_prefix()
    {
        var name = PostgresTestEnvironment.NewTestDatabaseName();
        Assert.StartsWith("hasm_test_", name);
        Assert.True(Hasm.Infrastructure.Naming.PhysicalIdentifier.IsValid(name));
    }

    private const string Admin = "Host=localhost;Username=hasm_test_admin;Password=p";

    [Fact]
    public void Role_gate_needs_the_ordinary_gate_and_a_loopback_role_admin_connection()
    {
        Assert.Null(PostgresRoleTestEnvironment.Evaluate(Admin, "Host=127.0.0.1;Username=hasm_test_roleadmin;Password=p", Ack));
        Assert.NotNull(PostgresRoleTestEnvironment.Evaluate(Admin, null, Ack));                                                    // no separately prepared account
        Assert.NotNull(PostgresRoleTestEnvironment.Evaluate(Admin, " ", Ack));
        Assert.NotNull(PostgresRoleTestEnvironment.Evaluate(Admin, "Host=db.prod.example.com;Username=r;Password=p", Ack));         // not loopback
        Assert.NotNull(PostgresRoleTestEnvironment.Evaluate(Admin, "Host=localhost;Username=r;Password=p", null));                 // not acknowledged
        Assert.NotNull(PostgresRoleTestEnvironment.Evaluate("Host=db.prod.example.com;Username=u;Password=p", "Host=localhost;Username=r;Password=p", Ack));
        Assert.NotNull(PostgresRoleTestEnvironment.Evaluate(null, "Host=localhost;Username=r;Password=p", Ack));
    }

    // ---- Cluster identity (positive verification of the disposable instance) ----
    private const string Fp = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static ClusterObservation Observed(int port = 5433, string? marker = Fp, params string[] extra) =>
        new(port, ["postgres", "template0", "template1", PostgresClusterIdentity.MarkerDatabase, "hasm_test_0123456789abcdef01234567", .. extra], marker);

    [Fact]
    public void Identity_accepts_the_disposable_local_cluster_only_with_the_matching_fingerprint() =>
        Assert.Null(PostgresClusterIdentity.Decide(5433, false, Fp, Observed()));

    [Fact]
    public void Identity_rejects_the_development_port_outside_a_github_job_even_with_a_valid_looking_cluster() =>
        Assert.NotNull(PostgresClusterIdentity.Decide(5432, false, Fp, Observed(5432)));

    [Fact]
    public void Identity_accepts_the_github_service_port_only_inside_a_github_job_and_still_needs_the_marker()
    {
        Assert.Null(PostgresClusterIdentity.Decide(5432, true, Fp, Observed(5432)));
        Assert.NotNull(PostgresClusterIdentity.Decide(5432, true, Fp, Observed(5432, marker: null)));
        Assert.NotNull(PostgresClusterIdentity.Decide(5432, true, Fp, Observed(5432, marker: "wrong" + Fp)));
    }

    // ---- the disposable cluster of a job on the self-hosted runner (private port 5439) ----

    [Fact]
    public void A_self_hosted_ci_job_may_use_exactly_its_private_port_after_proving_the_cluster_identity()
    {
        Assert.Null(PostgresClusterIdentity.Decide(5439, false, Fp, Observed(5439), selfHostedCiJob: true));
        Assert.NotNull(PostgresClusterIdentity.Decide(5439, false, Fp, Observed(5439)));                                                  // the port alone opens nothing: no job, no gate
        Assert.NotNull(PostgresClusterIdentity.Decide(5439, true, Fp, Observed(5439)));                                                   // a GitHub-hosted job has its own port
        Assert.NotNull(PostgresClusterIdentity.Decide(5439, false, Fp, Observed(5439, marker: null), selfHostedCiJob: true));
        Assert.NotNull(PostgresClusterIdentity.Decide(5439, false, Fp, Observed(5439, marker: "wrong" + Fp), selfHostedCiJob: true));
        Assert.NotNull(PostgresClusterIdentity.Decide(5439, false, Fp, Observed(5439, extra: "customer_data"), selfHostedCiJob: true));
        Assert.NotNull(PostgresClusterIdentity.Decide(5439, false, null, Observed(5439), selfHostedCiJob: true));
        Assert.NotNull(PostgresClusterIdentity.Decide(5439, false, Fp, Observed(5433), selfHostedCiJob: true));                          // the server must report the port it was reached on
    }

    [Theory]
    [InlineData(5432)]
    [InlineData(5431)]
    [InlineData(5434)]
    [InlineData(6543)]
    public void A_self_hosted_ci_job_never_opens_any_other_port_and_especially_not_the_development_cluster(int port) =>
        Assert.NotNull(PostgresClusterIdentity.Decide(port, false, Fp, Observed(port), selfHostedCiJob: true));

    [Theory]
    [InlineData("true", "123", "self-hosted", "C:\\work\\pg", true)]
    [InlineData("true", "123", "self-hosted", "", false)]
    [InlineData("true", "123", "self-hosted", null, false)]
    [InlineData("true", "123", "github-hosted", "C:\\work\\pg", false)]
    [InlineData("true", "", "self-hosted", "C:\\work\\pg", false)]
    [InlineData("false", "123", "self-hosted", "C:\\work\\pg", false)]
    [InlineData(null, "123", "self-hosted", "C:\\work\\pg", false)]
    public void Self_hosted_job_detection_needs_all_four_markers(string? actions, string? runId, string? runner, string? ciDir, bool expected)
    {
        string? Env(string k) => k switch { "GITHUB_ACTIONS" => actions, "GITHUB_RUN_ID" => runId, "RUNNER_ENVIRONMENT" => runner, "HASM_CI_PG_DIR" => ciDir, _ => null };
        Assert.Equal(expected, PostgresClusterIdentity.IsSelfHostedActionsJob(Env));
        Assert.False(PostgresClusterIdentity.IsGitHubActionsJob(Env) && runner == "self-hosted");                                          // the two job kinds never overlap
    }

    [Theory]
    [InlineData(5431)]
    [InlineData(5434)]
    [InlineData(6543)]
    public void Identity_rejects_other_ports_even_inside_a_github_job(int port) =>
        Assert.NotNull(PostgresClusterIdentity.Decide(port, true, Fp, Observed(port)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void Identity_rejects_a_missing_or_weak_expected_fingerprint(string? expected) =>
        Assert.NotNull(PostgresClusterIdentity.Decide(5433, false, expected, Observed()));

    [Fact]
    public void Identity_rejects_a_missing_marker_or_a_different_fingerprint()
    {
        Assert.NotNull(PostgresClusterIdentity.Decide(5433, false, Fp, Observed(marker: null)));
        Assert.NotNull(PostgresClusterIdentity.Decide(5433, false, Fp, Observed(marker: Fp.ToUpperInvariant())));
        Assert.NotNull(PostgresClusterIdentity.Decide(5433, false, Fp, Observed(marker: "")));
    }

    [Theory]
    [InlineData("Hasm_System")]
    [InlineData("d9551be6a359372439b8cd2db")]
    [InlineData("hasm_dev")]
    [InlineData("HASM_TEST_upper")]
    [InlineData("hasm_tes")]
    public void Identity_rejects_any_instance_holding_a_database_that_is_not_a_test_database(string foreign) =>
        Assert.NotNull(PostgresClusterIdentity.Decide(5433, false, Fp, Observed(extra: foreign)));

    [Fact]
    public void Identity_rejects_an_unreachable_or_misreporting_server()
    {
        Assert.NotNull(PostgresClusterIdentity.Decide(5433, false, Fp, null));
        Assert.NotNull(PostgresClusterIdentity.Decide(5433, false, Fp, Observed() with { ServerPort = 5432 }));
        Assert.NotNull(PostgresClusterIdentity.Decide(5433, false, Fp, Observed() with { ServerPort = null }));
    }

    [Fact]
    public void Identity_messages_do_not_echo_secrets_or_names()
    {
        var reasons = new[]
        {
            PostgresClusterIdentity.Decide(5433, false, Fp, Observed(marker: "OtherSecretValue" + Fp)),
            PostgresClusterIdentity.Decide(5433, false, Fp, Observed(extra: "ProdCompanyDb")),
            PostgresClusterIdentity.Decide(5432, false, Fp, Observed(5432)),
        };
        foreach (var r in reasons) { Assert.NotNull(r); Assert.DoesNotContain(Fp, r); Assert.DoesNotContain("OtherSecretValue", r); Assert.DoesNotContain("ProdCompanyDb", r); }
    }

    [Theory]
    [InlineData("Host=localhost;Port=1;Username=u;Password=p;Database=postgres")]                  // nothing listens: unreachable
    [InlineData("Host=127.0.0.1;Port=5599;Username=u;Password=TopSecret;Database=postgres")]
    [InlineData("not a connection string")]
    public void Verify_fails_closed_for_unreachable_or_malformed_connections(string cs)
    {
        var reason = PostgresClusterIdentity.Verify(cs);
        Assert.NotNull(reason);
        Assert.DoesNotContain("TopSecret", reason);
    }

    [Theory]
    [InlineData("true", "123", "github-hosted", true)]
    [InlineData("true", "123", "self-hosted", false)]
    [InlineData("true", "", "github-hosted", false)]
    [InlineData("true", null, "github-hosted", false)]
    [InlineData("yes", "123", "github-hosted", false)]
    [InlineData(null, "123", "github-hosted", false)]
    public void Github_job_detection_needs_all_three_runner_markers(string? actions, string? runId, string? runner, bool expected)
    {
        string? Env(string k) => k switch { "GITHUB_ACTIONS" => actions, "GITHUB_RUN_ID" => runId, "RUNNER_ENVIRONMENT" => runner, _ => null };
        Assert.Equal(expected, PostgresClusterIdentity.IsGitHubActionsJob(Env));
    }

    [Fact]
    public void Role_gate_messages_do_not_echo_connection_strings()
    {
        var reason = PostgresRoleTestEnvironment.Evaluate(Admin, "Host=db.prod.example.com;Username=r;Password=TopSecret", Ack);
        Assert.DoesNotContain("TopSecret", reason);
        Assert.DoesNotContain("db.prod.example.com", reason);
    }
    // ---- GitHub Actions bootstrap inventory (PR #13 regression: the image's default database was named after POSTGRES_USER) ----
    [Fact]
    public void Identity_accepts_the_exact_github_service_inventory_and_rejects_the_default_user_named_database()
    {
        var inventory = new[] { "postgres", "template0", "template1", "hasm_test_bootstrap", PostgresClusterIdentity.MarkerDatabase };
        Assert.Null(PostgresClusterIdentity.Decide(5432, true, Fp, new ClusterObservation(5432, inventory, Fp)));
        // The previous, failing inventory (extra database named after the bootstrap user) stays refused, also inside a GitHub job.
        Assert.NotNull(PostgresClusterIdentity.Decide(5432, true, Fp, new ClusterObservation(5432, [.. inventory, "ci_bootstrap"], Fp)));
        // Unexpected databases are refused in CI as well as locally.
        Assert.NotNull(PostgresClusterIdentity.Decide(5432, true, Fp, Observed(5432, extra: "customer_data")));
    }

    [Fact]
    public void Workflows_name_the_bootstrap_database_with_the_test_prefix()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "backend.yml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        // the Linux service container of linux-verification.yml names its bootstrap database explicitly
        var linux = File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", "linux-verification.yml"));
        var m = System.Text.RegularExpressions.Regex.Match(linux, @"^\s*POSTGRES_DB:\s*(\S+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(m.Success, "The CI PostgreSQL service must name its bootstrap database explicitly.");
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, m.Groups[1].Value, StringComparison.Ordinal);
        // the job on the self-hosted runner creates its own cluster: its bootstrap database carries the prefix too, only the identity marker and test databases may exist, and it never uses a standing cluster's port
        var win = File.ReadAllText(Path.Combine(dir.FullName, ".github", "workflows", "backend.yml"));
        Assert.Contains("CREATE DATABASE hasm_test_bootstrap", win, StringComparison.Ordinal);
        Assert.Contains("hasm_test_bootstrap hasm_test_cluster_identity postgres template0 template1", win, StringComparison.Ordinal);
        Assert.Contains($"HASM_CI_PG_PORT: \"{PostgresClusterIdentity.SelfHostedCiPort}\"", win, StringComparison.Ordinal);
        Assert.Contains("if ($port -in 5432, 5433)", win, StringComparison.Ordinal);
    }
}
