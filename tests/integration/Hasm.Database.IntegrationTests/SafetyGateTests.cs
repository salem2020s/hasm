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

    [Fact]
    public void Role_gate_messages_do_not_echo_connection_strings()
    {
        var reason = PostgresRoleTestEnvironment.Evaluate(Admin, "Host=db.prod.example.com;Username=r;Password=TopSecret", Ack);
        Assert.DoesNotContain("TopSecret", reason);
        Assert.DoesNotContain("db.prod.example.com", reason);
    }
}
