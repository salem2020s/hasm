using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;

namespace Hasm.Infrastructure.Tests;

public class CompanyProvisioningUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    private static HasmDatabaseOptions Options() => new()
    {
        Host = "h", SystemDatabaseName = "dsystem", AdminUsername = "a", AdminPassword = "pw-secret",
        SslMode = SslMode.VerifyFull,
    };

    [Fact]
    public void Company_connection_string_rejects_system_maintenance_and_invalid_names()
    {
        var o = Options();
        Assert.Throws<ArgumentException>(() => o.BuildCompanyAdminConnectionString("dsystem"));
        Assert.Throws<ArgumentException>(() => o.BuildCompanyAdminConnectionString("postgres"));
        Assert.Throws<ArgumentException>(() => o.BuildCompanyAdminConnectionString("Bad Name; DROP"));
        Assert.Throws<ArgumentException>(() => o.BuildCompanyAdminConnectionString("pg_x"));
        Assert.Contains("Database=dabc", o.BuildCompanyAdminConnectionString("dabc"));
    }

    [Fact]
    public void Pending_company_has_no_attempts_and_no_error()
    {
        var c = CompanyRecord.CreatePending(T0);
        Assert.Equal(0, c.ProvisioningAttempts);
        Assert.Null(c.LastErrorCode);
    }

    [Fact]
    public void Name_generator_is_used_for_the_physical_name() =>
        Assert.Equal("dfixed", CompanyRecord.CreatePending(T0, () => "dfixed").PhysicalDatabaseName);

    [Fact]
    public void Negative_attempts_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompanyRecord(Guid.NewGuid(), "d1", ProvisioningState.Pending, T0, T0, -1));

    [Fact]
    public void Transition_keeps_attempts_and_error()
    {
        var failed = new CompanyRecord(Guid.NewGuid(), "d1", ProvisioningState.Failed, T0, T0, 2, "migration");
        var again = failed.TransitionTo(ProvisioningState.Provisioning, T0.AddSeconds(1));
        Assert.Equal(2, again.ProvisioningAttempts);
        Assert.Equal("migration", again.LastErrorCode);
    }

    [Fact]
    public void Ready_is_terminal() =>
        Assert.All(Enum.GetValues<ProvisioningState>(), s => Assert.False(CompanyRecord.CanTransition(ProvisioningState.Ready, s)));

    [Fact]
    public void Error_codes_are_short_and_never_contain_messages()
    {
        var secret = new MigrationException("password=hunter2 leaked");
        var code = CompanyProvisioner.ErrorCode(secret);
        Assert.Equal("migration", code);
        Assert.Equal("consistency", CompanyProvisioner.ErrorCode(new CompanyConsistencyException("x")));
        Assert.Equal("error", CompanyProvisioner.ErrorCode(new InvalidOperationException("secret")));
        Assert.All(new[] { code, "pg_23505", "connection" }, c => Assert.True(c.Length <= 64));
    }

    [Fact]
    public async Task Provisioning_requires_admin_credentials_before_touching_anything()
    {
        var runtimeOnly = new HasmDatabaseOptions { Host = "h", SystemDatabaseName = "dsystem", Username = "u", Password = "p", SslMode = SslMode.VerifyFull };
        var provisioner = new CompanyProvisioner(runtimeOnly, new CompanyRegistry("Host=invalid"));
        await Assert.ThrowsAsync<DatabaseConfigurationException>(() => provisioner.ProvisionAsync(Guid.NewGuid()));
    }
}
