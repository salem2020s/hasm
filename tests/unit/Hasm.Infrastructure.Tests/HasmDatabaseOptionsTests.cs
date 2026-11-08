using Hasm.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Hasm.Infrastructure.Tests;

public class HasmDatabaseOptionsTests
{
    private const string Secret = "S3cr3t-Pw!x";
    private const string AdminSecret = "Adm1n-Pw!y";

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(kv => $"Hasm:Database:{kv.Key}", kv => kv.Value)).Build();

    private static Dictionary<string, string?> Full() => new()
    {
        ["Host"] = "db.internal", ["SystemDatabaseName"] = "d1a2b3c4", ["Username"] = "app", ["Password"] = Secret,
        ["AdminUsername"] = "adm", ["AdminPassword"] = AdminSecret,
    };

    [Fact]
    public void Empty_configuration_reports_every_missing_setting_by_name()
    {
        var o = HasmDatabaseOptions.FromConfiguration(Config([]));
        var errors = o.Validate(requireAdmin: false);
        Assert.Contains(errors, e => e.Contains("Host"));
        Assert.Contains(errors, e => e.Contains("SystemDatabaseName"));
        Assert.Contains(errors, e => e.Contains("Username"));
        Assert.Contains(errors, e => e.Contains("Password"));
        Assert.Throws<DatabaseConfigurationException>(() => o.EnsureValid(false));
        Assert.Throws<DatabaseConfigurationException>(() => o.BuildSystemConnectionString());
    }

    [Fact]
    public void Admin_credentials_are_required_for_admin_use_only()
    {
        var v = Full(); v.Remove("AdminPassword");
        var o = HasmDatabaseOptions.FromConfiguration(Config(v));
        Assert.Empty(o.Validate(requireAdmin: false));
        Assert.Contains(o.Validate(requireAdmin: true), e => e.Contains("AdminPassword"));
    }

    [Theory]
    [InlineData("Port", "abc")]
    [InlineData("Port", "0")]
    [InlineData("Port", "70000")]
    [InlineData("SslMode", "bogus")]
    public void Invalid_values_are_rejected(string key, string value)
    {
        var v = Full(); v[key] = value;
        Assert.Throws<DatabaseConfigurationException>(() => HasmDatabaseOptions.FromConfiguration(Config(v)));
    }

    [Theory]
    [InlineData("Hasm System")]
    [InlineData("Hasm_System")]    // upper case: the logical name must not be used as the physical name
    [InlineData("x\"; DROP DATABASE y;--")]
    [InlineData("pg_catalog")]
    public void Invalid_physical_system_database_name_is_rejected(string name)
    {
        var v = Full(); v["SystemDatabaseName"] = name;
        var o = HasmDatabaseOptions.FromConfiguration(Config(v));
        Assert.Contains(o.Validate(false), e => e.Contains("SystemDatabaseName"));
    }

    [Theory]
    [InlineData("Disable")]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    [InlineData("Require")]   // Npgsql Require encrypts but does not validate the certificate
    public void Non_validating_ssl_modes_are_rejected_by_default(string mode)
    {
        var v = Full(); v["SslMode"] = mode;
        var o = HasmDatabaseOptions.FromConfiguration(Config(v));
        Assert.Contains(o.Validate(false), e => e.Contains("SslMode"));
        Assert.Throws<DatabaseConfigurationException>(() => o.BuildSystemConnectionString());
    }

    [Theory]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void Validating_ssl_modes_are_accepted(string mode)
    {
        var v = Full(); v["SslMode"] = mode;
        Assert.Empty(HasmDatabaseOptions.FromConfiguration(Config(v)).Validate(false));
    }

    [Fact]
    public void Insecure_transport_requires_explicit_opt_in_and_is_visible()
    {
        var v = Full(); v["SslMode"] = "Disable"; v["AllowInsecureTransport"] = "true";
        var o = HasmDatabaseOptions.FromConfiguration(Config(v));
        Assert.Empty(o.Validate(false));
        Assert.Contains("InsecureTransport=True", o.ToString());
        v["AllowInsecureTransport"] = "maybe";
        Assert.Throws<DatabaseConfigurationException>(() => HasmDatabaseOptions.FromConfiguration(Config(v)));
    }

    [Fact]
    public void Root_certificate_path_reaches_the_connection_string()
    {
        var v = Full(); v["SslRootCertificate"] = "/etc/hasm/ca.pem";
        var cs = new NpgsqlConnectionStringBuilder(HasmDatabaseOptions.FromConfiguration(Config(v)).BuildSystemConnectionString());
        Assert.Equal("/etc/hasm/ca.pem", cs.RootCertificate);
    }

    [Fact]
    public void Blank_values_count_as_missing()
    {
        var v = Full(); v["Password"] = "   ";
        var o = HasmDatabaseOptions.FromConfiguration(Config(v));
        Assert.Contains(o.Validate(false), e => e.Contains("Password"));
    }

    [Fact]
    public void Defaults_are_secure_and_sane()
    {
        var o = HasmDatabaseOptions.FromConfiguration(Config(Full()));
        Assert.Equal(5432, o.Port);
        Assert.Equal(SslMode.VerifyFull, o.SslMode);
        Assert.False(o.AllowInsecureTransport);
        Assert.Equal("postgres", o.MaintenanceDatabase);
    }

    [Fact]
    public void Connection_strings_use_the_right_account_and_database()
    {
        var o = HasmDatabaseOptions.FromConfiguration(Config(Full()));
        var runtime = new NpgsqlConnectionStringBuilder(o.BuildSystemConnectionString());
        var admin = new NpgsqlConnectionStringBuilder(o.BuildSystemConnectionString(admin: true));
        var maint = new NpgsqlConnectionStringBuilder(o.BuildMaintenanceConnectionString());
        Assert.Equal(("app", Secret, "d1a2b3c4"), (runtime.Username, runtime.Password, runtime.Database));
        Assert.Equal(("adm", AdminSecret, "d1a2b3c4"), (admin.Username, admin.Password, admin.Database));
        Assert.Equal(("adm", "postgres"), (maint.Username, maint.Database));
    }

    [Fact]
    public void ToString_and_scrubbing_never_reveal_secrets()
    {
        var o = HasmDatabaseOptions.FromConfiguration(Config(Full()));
        Assert.DoesNotContain(Secret, o.ToString());
        Assert.DoesNotContain(AdminSecret, o.ToString());
        var scrubbed = o.Scrub($"failed: Password={Secret} and {AdminSecret}");
        Assert.DoesNotContain(Secret, scrubbed);
        Assert.DoesNotContain(AdminSecret, scrubbed);
    }

    [Fact]
    public void Validation_errors_never_contain_secret_values()
    {
        var v = Full(); v["SystemDatabaseName"] = "Bad Name";
        var o = HasmDatabaseOptions.FromConfiguration(Config(v));
        var all = string.Join(" ", o.Validate(true));
        Assert.DoesNotContain(Secret, all);
        Assert.DoesNotContain(AdminSecret, all);
    }

    [Fact]
    public void Configuration_can_come_from_environment_variable_naming()
    {
        Environment.SetEnvironmentVariable("Hasm__Database__Host", "envhost");
        try
        {
            var cfg = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            Assert.Equal("envhost", HasmDatabaseOptions.FromConfiguration(cfg).Host);
        }
        finally { Environment.SetEnvironmentVariable("Hasm__Database__Host", null); }
    }
}
