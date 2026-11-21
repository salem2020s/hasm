using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.3 slice 4 on the disposable PostgreSQL server (D-069..D-073): one backup run of the whole installation (system database first, without the data of the secret-store tables, then every Ready
/// company), its report, monitoring status and retention preview, and the real Hasm.Admin commands. Only disposable-prefixed databases are created. Needs pg_dump/pg_restore of the server's major version.
/// </summary>
public class BackupRunTests : PostgresTestBase
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-brt-" + Guid.NewGuid().ToString("N"));
    private string Storage => Path.Combine(work, "storage");
    private string Keys => Path.Combine(work, "keys");
    private string Root => Path.Combine(work, "runs");
    private CompanyRegistry registry = null!;

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private EncryptionOptions Encryption => new() { KeyDirectory = Keys, ActiveKeyId = "k1", AllowInsecureFilePermissions = true };
    private BackupRunService Service(string? storage = "") => new(Options, new AttachmentOptions { StorageDirectory = storage == "" ? Storage : storage }, Encryption);

    private async Task Init()
    {
        Directory.CreateDirectory(work); Directory.CreateDirectory(Storage);
        FileKekRing.Generate(Keys, "k1");
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        registry = new CompanyRegistry(Options.BuildSystemConnectionString(admin: true), NewTestName);
    }

    private async Task<CompanyRecord> NewCompany()
    {
        var c = await registry.RegisterPendingAsync();
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, c.PhysicalDatabaseName);
        await new CompanyProvisioner(Options, registry).ProvisionAsync(c.Id);
        return (await registry.GetAsync(c.Id))!;
    }

    private string SystemConnection => Options.BuildSystemConnectionString(admin: true);
    private string Db(string name) => Options.BuildCompanyAdminConnectionString(name);

    private static async Task Exec(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> Digest(string connection)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1", conn))
        await using (var r = await list.ExecuteReaderAsync()) while (await r.ReadAsync()) tables.Add(r.GetString(0));
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(x::text, ',' ORDER BY x::text)), '') FROM {new NpgsqlCommandBuilder().QuoteIdentifier(table)} x", conn);
            sb.Append(table).Append('=').Append((string)(await cmd.ExecuteScalarAsync())!).Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private async Task Party(CompanyRecord c, string code) =>
        await Exec(Db(c.PhysicalDatabaseName), $"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) " +
                                               $"VALUES ('{Guid.NewGuid()}', '{code}', 'p', true, 1, 1, '{Guid.NewGuid()}', now(), now())");

    private static async Task<string> TableOfContents(string dump)
    {
        var psi = new ProcessStartInfo("pg_restore") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--list"); psi.ArgumentList.Add(dump);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        return output;
    }

    // ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_run_writes_the_system_set_and_one_verified_set_per_ready_company_and_changes_nothing()
    {
        await Init();
        var a = await NewCompany(); var b = await NewCompany();
        var pending = await registry.RegisterPendingAsync();
        await Party(a, "A1"); await Party(b, "B1");
        var other = NewTestName();
        await Exec(Options.BuildMaintenanceConnectionString(), $"CREATE DATABASE {other}");
        await Exec(Db(other), "CREATE TABLE t (id int PRIMARY KEY); INSERT INTO t VALUES (1), (2)");
        var before = (await Digest(Db(a.PhysicalDatabaseName)), await Digest(Db(b.PhysicalDatabaseName)), await Digest(Db(other)), await Digest(SystemConnection));

        var (report, runDir) = await Service().RunAsync(Root);

        Assert.Equal("ok", report.Verdict);
        Assert.Equal(1, report.Skipped);
        Assert.False(report.Encrypted);
        Assert.Equal(new[] { "session", "login_throttle" }, report.Excluded);
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), report.Companies.Select(c => c.CompanyId).OrderBy(x => x));
        Assert.All(report.Companies, c => { Assert.Equal("ok", c.Outcome); Assert.True(c.DumpBytes > 0); Assert.True(c.FinishedUtc >= c.StartedUtc); });
        Assert.True(report.Companies.All(c => c.StartedUtc >= report.System.FinishedUtc));                     // the system database is dumped first
        Assert.True(SystemBackupSet.Verify(Path.Combine(runDir, BackupRunFiles.SystemFolder)));
        foreach (var c in new[] { a, b }) Assert.True(BackupSet.Verify(Path.Combine(runDir, BackupRunFiles.CompaniesFolder, c.Id.ToString("N"))).IsClean);
        Assert.False(Directory.Exists(Path.Combine(runDir, BackupRunFiles.CompaniesFolder, pending.Id.ToString("N"))));
        var json = File.ReadAllText(Path.Combine(runDir, BackupRunFiles.ReportFileName));
        Assert.DoesNotContain(a.PhysicalDatabaseName, json); Assert.DoesNotContain(b.PhysicalDatabaseName, json); Assert.DoesNotContain(Options.SystemDatabaseName!, json);
        Assert.Equal(report.RunId, BackupRunFiles.ReadReport(runDir)!.RunId);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(runDir));
        Assert.Equal(before, (await Digest(Db(a.PhysicalDatabaseName)), await Digest(Db(b.PhysicalDatabaseName)), await Digest(Db(other)), await Digest(SystemConnection)));
    }

    [PostgresFact]
    public async Task A_lost_company_database_fails_only_that_company_and_the_run_is_partial()
    {
        await Init();
        var a = await NewCompany(); var b = await NewCompany();
        await Exec(Options.BuildMaintenanceConnectionString(), $"DROP DATABASE {b.PhysicalDatabaseName} WITH (FORCE)");

        var (report, _) = await Service().RunAsync(Root);

        Assert.Equal("partial", report.Verdict);
        Assert.Equal("ok", report.Companies.Single(c => c.CompanyId == a.Id).Outcome);
        var lost = report.Companies.Single(c => c.CompanyId == b.Id);
        Assert.Equal(("failed", "company_database_missing"), (lost.Outcome, lost.Code));
        Assert.Equal("ok", report.System.Outcome);
    }

    [PostgresFact]
    public async Task A_busy_company_is_reported_busy_and_a_second_concurrent_run_is_refused()
    {
        await Init();
        var a = await NewCompany(); var b = await NewCompany();
        await using (var holder = new NpgsqlConnection(SystemConnection))
        {
            await holder.OpenAsync();
            var locked = false;
            for (var i = 0; i < 10 && !(locked = await CompanyProvisioner.TryLockAsync(holder, a.Id, default)); i++) await Task.Delay(300);
            Assert.True(locked);
            var (report, _) = await Service().RunAsync(Root);
            Assert.Equal("partial", report.Verdict);
            Assert.Equal(("busy", "company_busy"), report.Companies.Single(c => c.CompanyId == a.Id) is var x ? (x.Outcome, x.Code) : default);
            Assert.Equal("ok", report.Companies.Single(c => c.CompanyId == b.Id).Outcome);
        }

        await using var runHolder = new NpgsqlConnection(SystemConnection);
        await runHolder.OpenAsync();
        await using (var lk = new NpgsqlCommand("SELECT pg_try_advisory_lock(@ns, 0)", runHolder))
        {
            lk.Parameters.AddWithValue("ns", BackupRunService.RunLockNamespace);
            Assert.True((bool)(await lk.ExecuteScalarAsync())!);
        }
        var runsBefore = Directory.GetDirectories(Root).Length;
        Assert.Equal("run_in_progress", (await Assert.ThrowsAsync<BackupException>(() => Service().RunAsync(Root))).Code);
        Assert.Equal(runsBefore, Directory.GetDirectories(Root).Length);                                         // nothing was written
    }

    [PostgresFact]
    public async Task The_system_dump_holds_the_secret_store_tables_without_their_data_and_its_manifest_detects_alteration()
    {
        await Init();
        await NewCompany();
        var (report, runDir) = await Service().RunAsync(Root);
        Assert.Equal("ok", report.System.Outcome);
        var dir = Path.Combine(runDir, BackupRunFiles.SystemFolder);
        var dump = Path.Combine(dir, BackupSet.DatabaseFileName);
        var toc = await TableOfContents(dump);
        foreach (var t in new[] { SystemSchema.Session.Table, SystemSchema.LoginThrottle.Table })
        {
            Assert.Contains($"TABLE public {t} ", toc);
            Assert.DoesNotContain($"TABLE DATA public {t} ", toc);
        }
        Assert.Contains($"TABLE DATA public {SystemSchema.Company.Table} ", toc);                                   // other tables keep their data
        var manifest = SystemBackupSet.ReadManifest(dir)!;
        Assert.Equal(new[] { "session", "login_throttle" }, manifest.Excluded);
        Assert.Equal(MigrationScript.LoadEmbedded().Max(s => s.Version), manifest.SystemMigrationVersion);
        Assert.True(SystemBackupSet.Verify(dir));
        var bytes = File.ReadAllBytes(dump); bytes[^20] ^= 0xFF; File.WriteAllBytes(dump, bytes);
        Assert.False(SystemBackupSet.Verify(dir));
    }

    [PostgresFact]
    public async Task Backup_status_reports_ok_partial_stale_missing_and_an_incomplete_newest_folder()
    {
        await Init();
        Assert.Equal((1, "backup_missing"), Code(BackupRunStatus.Evaluate(BackupRunStatus.List(Root), DateTimeOffset.UtcNow, BackupRunStatus.DefaultMaxAge)));
        var a = await NewCompany();
        await Service().RunAsync(Root);
        Assert.Equal((0, "ok"), Code(BackupRunStatus.Evaluate(BackupRunStatus.List(Root), DateTimeOffset.UtcNow, BackupRunStatus.DefaultMaxAge)));
        Assert.Equal((1, "backup_stale"), Code(BackupRunStatus.Evaluate(BackupRunStatus.List(Root), DateTimeOffset.UtcNow.AddHours(30), BackupRunStatus.DefaultMaxAge)));

        var b = await NewCompany();
        await Exec(Options.BuildMaintenanceConnectionString(), $"DROP DATABASE {b.PhysicalDatabaseName} WITH (FORCE)");
        await Task.Delay(1100);                                                                                  // a later run id
        var (partial, _) = await Service().RunAsync(Root);
        Assert.Equal("partial", partial.Verdict);
        Assert.Equal((8, "backup_partial"), Code(BackupRunStatus.Evaluate(BackupRunStatus.List(Root), DateTimeOffset.UtcNow, BackupRunStatus.DefaultMaxAge)));

        var incomplete = Path.Combine(Root, BackupRunFiles.NewRunId(DateTimeOffset.UtcNow.AddHours(1)));
        Directory.CreateDirectory(incomplete);
        Directory.SetLastWriteTimeUtc(incomplete, DateTime.UtcNow.AddHours(-25));
        var status = BackupRunStatus.Evaluate(BackupRunStatus.List(Root), DateTimeOffset.UtcNow, BackupRunStatus.DefaultMaxAge);
        Assert.Equal((8, "backup_partial"), Code(status));
        Assert.Equal(partial.RunId, status.Latest!.RunId);
        Assert.Equal(1, status.IncompleteRuns);
        Assert.True(Directory.Exists(incomplete));                                                               // never deleted
        Assert.Equal(a.Id, status.Latest.Companies.Single(c => c.Outcome == "ok").CompanyId);
    }

    private static (int, string) Code(BackupStatusResult r) => (r.ExitCode, r.Code);

    [PostgresFact]
    public async Task The_admin_commands_run_report_and_preview_without_deleting_and_print_neither_names_nor_secrets()
    {
        await Init();
        var c = await NewCompany();
        var env = new Dictionary<string, string?>
        {
            ["Hasm__Attachments__StorageDirectory"] = Storage, ["Hasm__Encryption__KeyDirectory"] = Keys, ["Hasm__Encryption__ActiveKeyId"] = "k1", ["Hasm__Encryption__AllowInsecureFilePermissions"] = "true",
        };
        var outputs = new List<string>();
        var run1 = await AdminTool.RunWith(Options, env, "backup-run", Root); outputs.Add(run1.All);
        Assert.True(run1.Exit == 0, run1.All);
        await Task.Delay(1100);
        var run2 = await AdminTool.RunWith(Options, env, "backup-run", Root); outputs.Add(run2.All);
        Assert.True(run2.Exit == 0, run2.All);
        var status = await AdminTool.RunWith(Options, env, "backup-status", Root); outputs.Add(status.All);
        Assert.True(status.Exit == 0, status.All);
        var withAge = await AdminTool.RunWith(Options, env, "backup-status", Root, "--max-age-hours", "1"); outputs.Add(withAge.All);
        Assert.True(withAge.Exit == 0, withAge.All);
        var runs = Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, runs.Length);
        var preview = await AdminTool.RunWith(Options, env, "backup-retention-preview", Root, "--keep-last", "1"); outputs.Add(preview.All);
        Assert.True(preview.Exit == 0, preview.All);
        Assert.Contains("would remove: " + Path.GetFileName(runs[0]), preview.Out);
        Assert.DoesNotContain("would remove: " + Path.GetFileName(runs[1]), preview.Out);
        Assert.All(runs, d => Assert.True(Directory.Exists(d)));                                               // nothing deleted

        Assert.Equal(2, (await AdminTool.RunWith(Options, env, "backup-run", "relative")).Exit);
        Assert.Equal(2, (await AdminTool.RunWith(Options, env, "backup-status", Root, "--max-age-hours", "0")).Exit);
        Assert.Equal(2, (await AdminTool.RunWith(Options, env, "backup-retention-preview", Root, "--keep-last", "0")).Exit);
        var noStorage = new Dictionary<string, string?>(env) { ["Hasm__Attachments__StorageDirectory"] = null };
        var unconfigured = await AdminTool.RunWith(Options, noStorage, "backup-run", Path.Combine(work, "runs2")); outputs.Add(unconfigured.All);
        Assert.Equal(3, unconfigured.Exit);

        foreach (var o in outputs)
        {
            Assert.DoesNotContain(Options.AdminPassword!, o);
            Assert.DoesNotContain(c.PhysicalDatabaseName, o);
            Assert.DoesNotContain(Options.SystemDatabaseName!, o);
            Assert.DoesNotContain("pg_dump:", o); Assert.DoesNotContain("pg_restore:", o);
        }
    }
}
