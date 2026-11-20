using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Security;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.3 restore drill on the disposable PostgreSQL server (never the development cluster): a company database and its attachment folder are backed up as one set and restored into a NEW isolated
/// database and a new folder, while another company database and the system database are proven untouched. Requires the PostgreSQL client tools (pg_dump, pg_restore) of the server's major version on PATH.
/// </summary>
public class BackupRestoreDrillTests : PostgresTestBase
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-drill-" + Guid.NewGuid().ToString("N"));

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private async Task CreateDatabase(string name)
    {
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(name)}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task Seed(string name, string tag)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(name));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE TABLE t (id int PRIMARY KEY, v text NOT NULL); INSERT INTO t SELECT g, '{tag}-' || g FROM generate_series(1, 50) g", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<string> Digest(string name) => DigestOf(Options.BuildCompanyAdminConnectionString(name));

    private string SystemConnection() => new NpgsqlConnectionStringBuilder(Options.BuildMaintenanceConnectionString()) { Database = Options.SystemDatabaseName }.ConnectionString;

    /// <summary>A catalog and content digest: every public table with its exact row count and a hash of its rows, so any change to tables or data shows.</summary>
    private static async Task<string> DigestOf(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1", conn))
        await using (var r = await list.ExecuteReaderAsync()) while (await r.ReadAsync()) tables.Add(r.GetString(0));
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            var q = new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var cmd = new NpgsqlCommand($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(x::text, ',' ORDER BY x::text)), '') FROM {q} x", conn);
            sb.Append(table).Append('=').Append((string)(await cmd.ExecuteScalarAsync())!).Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private async Task<bool> Exists(string name)
    {
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @n", conn);
        cmd.Parameters.AddWithValue("n", name);
        return (long)(await cmd.ExecuteScalarAsync())! == 1;
    }

    [PostgresFact]
    public async Task A_company_database_and_its_attachment_folder_are_restored_into_an_isolated_target_and_other_databases_are_untouched()
    {
        Directory.CreateDirectory(work);
        string a = NewTestName(), b = NewTestName(), restored = NewTestName();
        var system = Options.SystemDatabaseName!;
        await CreateDatabase(a); await CreateDatabase(b); await CreateDatabase(system);
        await Seed(a, "a"); await Seed(b, "b");
        await using (var sys = new NpgsqlConnection(SystemConnection()))
        {
            await sys.OpenAsync();
            await using var cmd = new NpgsqlCommand("CREATE TABLE registry_witness (id int PRIMARY KEY); INSERT INTO registry_witness VALUES (1), (2), (3)", sys);
            await cmd.ExecuteNonQueryAsync();
        }
        var digestB = await Digest(b);
        var digestSystem = await DigestOf(SystemConnection());
        var digestA = await Digest(a);

        var company = Guid.NewGuid();
        var att = Path.Combine(work, "att", company.ToString("N")); Directory.CreateDirectory(att);
        var ring = new InMemoryKekRing("k1");
        File.WriteAllBytes(Path.Combine(att, Guid.NewGuid().ToString("N") + ".att"), "%PDF-1.4 normal"u8.ToArray());
        File.WriteAllBytes(Path.Combine(att, Guid.NewGuid().ToString("N") + ".att"), new EnvelopeEncryptor(ring).Encrypt("secret"u8.ToArray(), "ctx"));

        var tool = new PgBackupTool(Options);
        var dump = Path.Combine(work, "a.dump");
        await tool.DumpAsync(a, dump);
        var set = Path.Combine(work, "set");
        var manifest = BackupSet.Create(company, dump, att, set, DateTimeOffset.UnixEpoch);
        Assert.True(BackupSet.Verify(set).IsClean);
        Assert.Equal(new[] { "k1" }, manifest.KeyIdsRequired);

        await tool.RestoreIntoNewDatabaseAsync(Path.Combine(set, BackupSet.DatabaseFileName), restored, a);
        var files = BackupSet.RestoreAttachments(set, Path.Combine(work, "restored-att", company.ToString("N")), company);

        Assert.Equal(2, files.Files);
        Assert.Equal(digestA, await Digest(restored));                                               // the restored copy equals the source
        Assert.Equal(digestA, await Digest(a));                                                      // the dump left the source unchanged
        Assert.Equal(digestB, await Digest(b));                                                      // another company untouched (tables, row counts and row hashes)
        Assert.Equal(digestSystem, await DigestOf(SystemConnection()));                              // the system database untouched
        Assert.Equal(50L, await Count(restored));
    }

    private async Task<long> Count(string name)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(name));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM t", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task Restore_refuses_an_existing_target_the_source_and_the_system_database_and_a_failed_restore_leaves_no_database()
    {
        Directory.CreateDirectory(work);
        string a = NewTestName(), existing = NewTestName(), fresh = NewTestName();
        await CreateDatabase(a); await CreateDatabase(existing); await Seed(a, "a"); await Seed(existing, "e");
        var digestExisting = await Digest(existing);
        var tool = new PgBackupTool(Options);
        var dump = Path.Combine(work, "a.dump");
        await tool.DumpAsync(a, dump);

        Assert.Equal("target_exists", (await Assert.ThrowsAsync<BackupException>(() => tool.RestoreIntoNewDatabaseAsync(dump, existing, a))).Code);
        Assert.Equal("target_is_source", (await Assert.ThrowsAsync<BackupException>(() => tool.RestoreIntoNewDatabaseAsync(dump, a, a))).Code);
        Assert.Equal("not_a_company_database", (await Assert.ThrowsAsync<BackupException>(() => tool.RestoreIntoNewDatabaseAsync(dump, Options.SystemDatabaseName!, a))).Code);
        foreach (var builtIn in new[] { "postgres", "template0", "template1", Options.MaintenanceDatabase })
            Assert.Equal("not_a_company_database", (await Assert.ThrowsAsync<BackupException>(() => tool.RestoreIntoNewDatabaseAsync(dump, builtIn, a))).Code);
        Assert.Equal("path_not_absolute", (await Assert.ThrowsAsync<BackupException>(() => tool.RestoreIntoNewDatabaseAsync("relative.dump", fresh, a))).Code);
        Assert.Equal(digestExisting, await Digest(existing));                                        // the refused target was not modified

        var garbage = Path.Combine(work, "garbage.dump");
        File.WriteAllBytes(garbage, RandomNumberGenerator.GetBytes(2048));
        Assert.Equal("restore_failed", (await Assert.ThrowsAsync<BackupException>(() => tool.RestoreIntoNewDatabaseAsync(garbage, fresh, a))).Code);
        Assert.False(await Exists(fresh));                                                           // the half-created target was dropped

        var missing = NewTestName();
        var none = Path.Combine(work, "none.dump");
        Assert.Equal("dump_failed", (await Assert.ThrowsAsync<BackupException>(() => tool.DumpAsync(missing, none))).Code);
        Assert.False(File.Exists(none));
        Assert.Equal("dump_file_exists", (await Assert.ThrowsAsync<BackupException>(() => tool.DumpAsync(a, dump))).Code);
        Assert.Equal("tool_unavailable", (await Assert.ThrowsAsync<BackupException>(() => new PgBackupTool(Options, Path.Combine(work, "no-bin")).DumpAsync(a, Path.Combine(work, "x.dump")))).Code);
    }
}
