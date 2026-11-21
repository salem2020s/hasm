using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Configuration;
using Npgsql;

namespace Hasm.Infrastructure.Tests;

/// <summary>The pure parts of the PostgreSQL backup tool: the exact child environment and the CREATE DATABASE failure mapping. (Process launch, timeout and kill are exercised by the PostgreSQL drill only.)</summary>
public class PgBackupToolTests
{
    private static HasmDatabaseOptions Opts(SslMode mode, string? root = null) => new()
    {
        Host = "db.example", AdminUsername = "admin", AdminPassword = "pw-not-a-secret", SslMode = mode, SslRootCertificate = root, SystemDatabaseName = "hasm_sys",
    };

    private static string? Get(string name) => name switch { "PATH" => "/bin", "SystemRoot" => null, "HASM__DATABASE__PASSWORD" => "leak", _ => null };

    [Fact]
    public void The_environment_holds_only_path_the_credential_tls_settings_and_a_timeout_and_nothing_else_of_the_service_environment()
    {
        var env = PgBackupTool.BuildEnvironment(Opts(SslMode.VerifyFull, "/certs/ca.pem"), Get);
        Assert.Equal(new[] { "PATH", "PGCONNECT_TIMEOUT", "PGPASSWORD", "PGSSLMODE", "PGSSLROOTCERT" }, env.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("/certs/ca.pem", env["PGSSLROOTCERT"]);
        Assert.Equal("verify-full", env["PGSSLMODE"]);
        Assert.DoesNotContain("HASM__DATABASE__PASSWORD", env.Keys);
    }

    [Theory]
    [InlineData(SslMode.VerifyFull, "verify-full", "system")]
    [InlineData(SslMode.VerifyCA, "verify-ca", "system")]
    [InlineData(SslMode.Require, "require", null)]
    [InlineData(SslMode.Disable, "disable", null)]
    public void Without_a_configured_certificate_a_validating_mode_names_the_system_trust_store_and_other_modes_set_no_root_certificate(SslMode mode, string expectedMode, string? expectedRoot)
    {
        var env = PgBackupTool.BuildEnvironment(Opts(mode), Get);
        Assert.Equal(expectedMode, env["PGSSLMODE"]);
        Assert.Equal(expectedRoot, env.GetValueOrDefault("PGSSLROOTCERT"));
    }

    [Fact]
    public void A_missing_credential_is_refused_and_only_a_lost_creation_race_has_a_meaning()
    {
        var noPassword = new HasmDatabaseOptions { Host = "h", AdminUsername = "a" };
        Assert.Equal("database_not_configured", Assert.Throws<BackupException>(() => PgBackupTool.BuildEnvironment(noPassword, Get)).Code);
        Assert.Equal("target_exists", PgBackupTool.CreateFailureCode(PostgresErrorCodes.DuplicateDatabase));
        Assert.Equal("target_exists", PgBackupTool.CreateFailureCode("42P04"));
        Assert.Null(PgBackupTool.CreateFailureCode(PostgresErrorCodes.InsufficientPrivilege));
        Assert.Null(PgBackupTool.CreateFailureCode(null));
    }

    [Fact]
    public void A_backup_exception_can_carry_a_secondary_cause_code()
    {
        var e = new BackupException("restore_failed_cleanup_failed", "restore_failed");
        Assert.Equal("restore_failed_cleanup_failed", e.Code);
        Assert.Equal("restore_failed", e.Cause);
        Assert.Null(new BackupException("x").Cause);
    }
}
