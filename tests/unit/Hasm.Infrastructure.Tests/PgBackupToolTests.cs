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
        var env = PgBackupTool.BuildEnvironment(Opts(SslMode.VerifyFull, "/certs/ca.pem"), Get, 14);
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
        var env = PgBackupTool.BuildEnvironment(Opts(mode), Get, 16);
        Assert.Equal(expectedMode, env["PGSSLMODE"]);
        Assert.Equal(expectedRoot, env.GetValueOrDefault("PGSSLROOTCERT"));
    }

    [Fact]
    public void A_missing_credential_is_refused_and_only_a_lost_creation_race_has_a_meaning()
    {
        var noPassword = new HasmDatabaseOptions { Host = "h", AdminUsername = "a" };
        Assert.Equal("database_not_configured", Assert.Throws<BackupException>(() => PgBackupTool.BuildEnvironment(noPassword, Get, 16)).Code);
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

    [Theory]
    [InlineData(SslMode.VerifyFull, 15, true)]
    [InlineData(SslMode.VerifyCA, 14, true)]
    [InlineData(SslMode.VerifyFull, 0, true)]          // an unknown client version is treated as too old
    [InlineData(SslMode.VerifyFull, 16, false)]
    [InlineData(SslMode.VerifyFull, 18, false)]
    [InlineData(SslMode.Require, 14, false)]            // a non-validating mode needs no root certificate
    [InlineData(SslMode.Disable, 0, false)]
    public void A_client_older_than_16_without_a_configured_certificate_is_refused_before_any_tool_runs_in_validating_modes_and_never_downgraded(SslMode mode, int clientMajor, bool refused)
    {
        if (refused) Assert.Equal("ssl_root_certificate_required", Assert.Throws<BackupException>(() => PgBackupTool.BuildEnvironment(Opts(mode), Get, clientMajor)).Code);
        else Assert.Equal(mode == SslMode.VerifyFull && clientMajor >= 16 ? "system" : null, PgBackupTool.BuildEnvironment(Opts(mode), Get, clientMajor).GetValueOrDefault("PGSSLROOTCERT"));
    }

    [Fact]
    public void An_old_client_with_a_configured_certificate_is_accepted_and_the_version_output_is_parsed()
    {
        Assert.Equal("/c.pem", PgBackupTool.BuildEnvironment(Opts(SslMode.VerifyFull, "/c.pem"), Get, 14)["PGSSLROOTCERT"]);
        Assert.Equal(16, PgBackupTool.ParseClientMajor("pg_dump (PostgreSQL) 16.4 (Ubuntu 16.4-1)"));
        Assert.Equal(18, PgBackupTool.ParseClientMajor("pg_restore (PostgreSQL) 18.0"));
        Assert.Equal(0, PgBackupTool.ParseClientMajor("garbage"));
        Assert.Equal(0, PgBackupTool.ParseClientMajor(null));
    }

    [Theory]
    [InlineData("UTF8", "en_US.utf8", "en_US.utf8", null, " ENCODING 'UTF8' LC_COLLATE 'en_US.utf8' LC_CTYPE 'en_US.utf8'")]
    [InlineData("UTF8", "C", "C", "c", " ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C'")]
    [InlineData("WIN1252", "English_United States.1252", "English_United States.1252", "c", " ENCODING 'WIN1252' LC_COLLATE 'English_United States.1252' LC_CTYPE 'English_United States.1252'")]
    [InlineData(null, null, null, null, "")]
    public void The_locale_clause_reproduces_the_source_encoding_and_libc_locale(string? encoding, string? collate, string? ctype, string? provider, string expected) =>
        Assert.Equal(expected, PgBackupTool.LocaleClause(encoding, collate, ctype, provider));

    [Theory]
    [InlineData("UTF8", "und-x-icu", "und-x-icu", "i")]        // ICU provider
    [InlineData("UTF8", "C", "C", "b")]                          // builtin provider
    [InlineData("UTF8", null, null, null)]                       // missing libc locale
    [InlineData("UTF8", "en'; DROP DATABASE x; --", "C", "c")]  // never placed in a statement
    public void An_unsupported_locale_is_refused_instead_of_silently_falling_back_to_the_template_default(string encoding, string? collate, string? ctype, string? provider) =>
        Assert.Equal("locale_unsupported", Assert.Throws<BackupException>(() => PgBackupTool.LocaleClause(encoding, collate, ctype, provider)).Code);
}
