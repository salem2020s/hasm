using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Configuration;
using Npgsql;

namespace Hasm.Infrastructure.Tests;

/// <summary>The pure parts of the PostgreSQL backup tool: the exact child environment, the locale clause and its source, and the CREATE DATABASE failure mapping. (Process wiring: <see cref="PgBackupToolProcessTests"/>.)</summary>
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
    [InlineData("UTF8", "en_US.utf8", "en_US.utf8", null, 14, " ENCODING 'UTF8' LC_COLLATE 'en_US.utf8' LC_CTYPE 'en_US.utf8'")]                    // PostgreSQL 14 target: no LOCALE_PROVIDER keyword
    [InlineData("UTF8", "en_US.utf8", "en_US.utf8", null, 16, " ENCODING 'UTF8' LOCALE_PROVIDER libc LC_COLLATE 'en_US.utf8' LC_CTYPE 'en_US.utf8'")] // a 14 source restored onto 15+: libc pinned
    [InlineData("UTF8", "C", "C", "c", 15, " ENCODING 'UTF8' LOCALE_PROVIDER libc LC_COLLATE 'C' LC_CTYPE 'C'")]
    [InlineData("UTF8", "C", "C", "c", 14, " ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C'")]
    [InlineData("WIN1252", "English_United States.1252", "English_United States.1252", "c", 17, " ENCODING 'WIN1252' LOCALE_PROVIDER libc LC_COLLATE 'English_United States.1252' LC_CTYPE 'English_United States.1252'")]
    [InlineData("UTF8", "Chinese (Simplified)_China.936", "Chinese (Simplified)_China.936", "c", 16, " ENCODING 'UTF8' LOCALE_PROVIDER libc LC_COLLATE 'Chinese (Simplified)_China.936' LC_CTYPE 'Chinese (Simplified)_China.936'")]
    public void The_locale_clause_reproduces_the_source_encoding_and_libc_locale_and_pins_the_libc_provider_on_15_or_later(string encoding, string? collate, string? ctype, string? provider, int targetMajor, string expected) =>
        Assert.Equal(expected, PgBackupTool.LocaleClause(new DatabaseLocale(encoding, collate, ctype, provider), targetMajor));

    [Theory]
    [InlineData("UTF8", "und-x-icu", "und-x-icu", "i")]        // ICU provider
    [InlineData("UTF8", "C", "C", "b")]                          // builtin provider
    [InlineData("UTF8", null, null, null)]                       // missing libc locale
    [InlineData("UTF8", "en'; DROP DATABASE x; --", "C", "c")]  // never placed in a statement
    [InlineData("UTF8", "en_US.utf8\n", "en_US.utf8", "c")]     // a trailing newline is refused (\z, not $)
    [InlineData("UTF8", "en_US.utf8", "en_US\\.utf8", "c")]      // no backslash
    [InlineData("UTF8\n", "C", "C", "c")]
    [InlineData("", "C", "C", "c")]
    public void An_unsupported_locale_is_refused_instead_of_silently_falling_back_to_the_template_default(string encoding, string? collate, string? ctype, string? provider) =>
        Assert.Equal("locale_unsupported", Assert.Throws<BackupException>(() => PgBackupTool.LocaleClause(new DatabaseLocale(encoding, collate, ctype, provider), 16)).Code);

    [Fact]
    public void The_locale_captured_at_dump_time_wins_the_live_source_is_the_fallback_and_without_either_the_restore_is_refused()
    {
        var captured = new DatabaseLocale("UTF8", "en_US.utf8", "en_US.utf8", "c");
        var live = new DatabaseLocale("SQL_ASCII", "C", "C", "c");
        Assert.Same(captured, PgBackupTool.ResolveLocale(captured, live));
        Assert.Same(captured, PgBackupTool.ResolveLocale(captured, null));       // disaster recovery: the source database no longer exists
        Assert.Same(live, PgBackupTool.ResolveLocale(null, live));               // an older set without a captured locale
        Assert.Equal("source_locale_unknown", Assert.Throws<BackupException>(() => PgBackupTool.ResolveLocale(null, null)).Code);   // never the template0 default
    }

    [Fact]
    public void Tools_run_in_a_fixed_system_directory()
    {
        Assert.True(Path.IsPathFullyQualified(PgBackupTool.ToolWorkingDirectory));
        Assert.True(Directory.Exists(PgBackupTool.ToolWorkingDirectory));
        Assert.Equal(OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/", PgBackupTool.ToolWorkingDirectory);
    }
}

/// <summary>
/// The process wiring of <see cref="PgBackupTool"/> with FAKE PostgreSQL tools (POSIX shell scripts in a temporary directory; skipped on Windows): the <c>--version</c> probe, the environment and working directory a started
/// tool really sees, the timeout, the exit-code mapping and the refusal ORDER (a configuration refusal comes before any database contact). The options point at 127.0.0.1:1, so a database contact would surface as a
/// connection error instead of the expected code; no real server is ever contacted.
/// </summary>
public sealed class PgBackupToolProcessTests : IDisposable
{
    private readonly string bin = Path.Combine(Path.GetTempPath(), "hasm-fakepg-" + Guid.NewGuid().ToString("N"));
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-fakepg-work-" + Guid.NewGuid().ToString("N"));

    public PgBackupToolProcessTests() { Directory.CreateDirectory(bin); Directory.CreateDirectory(work); }
    public void Dispose() { foreach (var d in new[] { bin, work }) try { Directory.Delete(d, true); } catch (IOException) { } }

    private static HasmDatabaseOptions Opts(SslMode mode, string? root = null) => new()
    {
        Host = "127.0.0.1", Port = 1, AdminUsername = "admin", AdminPassword = "pw-not-a-secret", SslMode = mode, SslRootCertificate = root, SystemDatabaseName = "hasm_sys",
    };

    /// <summary>A fake tool: prints <c>&lt;tool&gt; (PostgreSQL) &lt;major&gt;.1</c> for <c>--version</c>, otherwise runs <paramref name="body"/>.</summary>
    private void Fake(string tool, string major, string body)
    {
        var path = Path.Combine(bin, tool);
        File.WriteAllText(path, $"#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo \"{tool} (PostgreSQL) {major}.1\"; exit 0; fi\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task A_started_tool_sees_only_the_built_environment_the_fixed_working_directory_and_no_credential_on_its_command_line()
    {
        if (OperatingSystem.IsWindows()) return;
        Fake("pg_dump", "16", "out=\"\"\nfor a in \"$@\"; do if [ \"$prev\" = \"--file\" ]; then out=\"$a\"; fi; prev=\"$a\"; done\n{ echo \"cwd=$(pwd)\"; echo \"args=$*\"; env; } > \"$out\"");
        var sentinel = "HASM_TEST_SENTINEL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sentinel, "leak");
        try
        {
            var tool = new PgBackupTool(Opts(SslMode.VerifyFull), bin);
            var report = Path.Combine(work, "report.txt");
            var psi = await tool.PrepareAsync("pg_dump", ["--file", report], CancellationToken.None);
            await tool.RunAsync("pg_dump", psi, CancellationToken.None);
            var lines = File.ReadAllLines(report);
            Assert.Contains("cwd=/", lines);
            var args = Assert.Single(lines, l => l.StartsWith("args=", StringComparison.Ordinal));
            Assert.Contains("--no-password", args);
            Assert.DoesNotContain("pw-not-a-secret", args);
            Assert.Contains("PGPASSWORD=pw-not-a-secret", lines);
            Assert.Contains("PGSSLMODE=verify-full", lines);
            Assert.Contains("PGSSLROOTCERT=system", lines);                                                 // client 16: the system trust store
            Assert.Contains("PGCONNECT_TIMEOUT=30", lines);
            var names = lines.Where(l => !l.StartsWith("cwd=", StringComparison.Ordinal) && !l.StartsWith("args=", StringComparison.Ordinal) && l.Contains('='))
                .Select(l => l[..l.IndexOf('=')]).Where(n => n is not ("PWD" or "OLDPWD" or "SHLVL" or "_")).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "PATH", "PGCONNECT_TIMEOUT", "PGPASSWORD", "PGSSLMODE", "PGSSLROOTCERT" }, names);
            Assert.DoesNotContain(lines, l => l.StartsWith(sentinel, StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(sentinel, null); }
    }

    [Fact]
    public async Task An_old_client_in_a_validating_mode_without_a_certificate_is_refused_before_any_database_contact_and_no_file_is_left()
    {
        if (OperatingSystem.IsWindows()) return;
        Fake("pg_dump", "14", "exit 0");
        Fake("pg_restore", "15", "exit 0");
        var tool = new PgBackupTool(Opts(SslMode.VerifyFull), bin);
        var dump = Path.Combine(work, "a.dump");
        Assert.Equal("ssl_root_certificate_required", (await Assert.ThrowsAsync<BackupException>(() => tool.DumpAsync("hasm_c_a", dump))).Code);
        Assert.False(File.Exists(dump));
        File.WriteAllBytes(dump, [1, 2, 3]);
        Assert.Equal("ssl_root_certificate_required", (await Assert.ThrowsAsync<BackupException>(() => tool.RestoreIntoNewDatabaseAsync(dump, "hasm_c_b", "hasm_c_a"))).Code);
        // with a configured certificate the old client is accepted and the next step is the database (here: a connection error, proving the order)
        await Assert.ThrowsAnyAsync<Npgsql.NpgsqlException>(() => new PgBackupTool(Opts(SslMode.VerifyFull, "/certs/ca.pem"), bin).RestoreIntoNewDatabaseAsync(dump, "hasm_c_b", "hasm_c_a"));
    }

    [Fact]
    public async Task Unreadable_version_output_counts_as_an_old_client_and_the_probe_result_is_cached_per_tool()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(bin, "pg_dump");
        File.WriteAllText(path, "#!/bin/sh\necho garbage\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tool = new PgBackupTool(Opts(SslMode.VerifyCA), bin);
        Assert.Equal("ssl_root_certificate_required", (await Assert.ThrowsAsync<BackupException>(() => tool.PrepareAsync("pg_dump", [], CancellationToken.None))).Code);
        Fake("pg_dump", "17", "exit 0");                                                                   // the cached probe result of this instance is kept
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Assert.ThrowsAsync<BackupException>(() => tool.PrepareAsync("pg_dump", [], CancellationToken.None))));
        Assert.All(results, e => Assert.Equal("ssl_root_certificate_required", e.Code));
        Assert.Equal("system", (await new PgBackupTool(Opts(SslMode.VerifyCA), bin).PrepareAsync("pg_dump", [], CancellationToken.None)).Environment["PGSSLROOTCERT"]);   // a new instance probes again
    }

    [Fact]
    public async Task A_failing_tool_maps_to_its_code_and_a_hanging_tool_is_killed_at_the_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        Fake("pg_dump", "16", "exit 3");
        Fake("pg_restore", "16", "exec sleep 60");
        var tool = new PgBackupTool(Opts(SslMode.Require), bin, TimeSpan.FromSeconds(1));
        Assert.Equal("dump_failed", (await Assert.ThrowsAsync<BackupException>(async () => await tool.RunAsync("pg_dump", await tool.PrepareAsync("pg_dump", [], CancellationToken.None), CancellationToken.None))).Code);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("tool_timeout", (await Assert.ThrowsAsync<BackupException>(async () => await tool.RunAsync("pg_restore", await tool.PrepareAsync("pg_restore", [], CancellationToken.None), CancellationToken.None))).Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal("tool_unavailable", (await Assert.ThrowsAsync<BackupException>(() => new PgBackupTool(Opts(SslMode.Require), Path.Combine(work, "no-bin")).PrepareAsync("pg_dump", [], CancellationToken.None))).Code);
    }
}
