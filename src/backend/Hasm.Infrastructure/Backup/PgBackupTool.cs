using System.ComponentModel;
using System.Diagnostics;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Naming;
using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// Database half of the coordinated backup (SRS 16.1, 16.2, 16.4): a custom-format <c>pg_dump</c> of ONE company database and a <c>pg_restore</c> into a NEW, isolated database. Only the privileged administrative
/// account is used (the same one that provisions companies). Credentials travel in the child process environment (never on the command line, never logged); no tool output is ever copied into an exception.
/// The restore never touches an existing database: the target must not exist, must not be the system or maintenance database or the source, and is dropped again if the restore fails. Dumps are taken with
/// <c>--no-owner --no-privileges</c>, so a restored copy does not depend on the roles of the source cluster; the runtime role and its grants are re-applied by the existing <c>grant-company-runtime</c> procedure
/// before a cutover (the cutover itself and its approval are not part of this class).
/// </summary>
public sealed class PgBackupTool(HasmDatabaseOptions options, string? pgBinDirectory = null, TimeSpan? timeout = null)
{
    private readonly TimeSpan limit = timeout ?? TimeSpan.FromMinutes(30);

    /// <summary>Dumps <paramref name="sourceDatabase"/> (a company database) to <paramref name="dumpFile"/>, which must not exist. A partial file is removed on failure.</summary>
    public async Task DumpAsync(string sourceDatabase, string dumpFile, CancellationToken ct = default)
    {
        RequireCompanyDatabase(sourceDatabase);
        if (!Path.IsPathFullyQualified(dumpFile)) throw new BackupException("path_not_absolute");
        if (File.Exists(dumpFile)) throw new BackupException("dump_file_exists");
        try
        {
            await RunAsync("pg_dump", ["--format=custom", "--no-owner", "--no-privileges", "--file", dumpFile, "--dbname", sourceDatabase], ct);
            if (!File.Exists(dumpFile) || new FileInfo(dumpFile).Length == 0) throw new BackupException("dump_failed");
        }
        catch
        {
            try { if (File.Exists(dumpFile)) File.Delete(dumpFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
    }

    /// <summary>Creates <paramref name="targetDatabase"/> and restores the dump into it. Nothing else is touched; the new database is dropped when the restore fails.</summary>
    public async Task RestoreIntoNewDatabaseAsync(string dumpFile, string targetDatabase, string sourceDatabase, CancellationToken ct = default)
    {
        RequireCompanyDatabase(targetDatabase);
        RequireCompanyDatabase(sourceDatabase);
        if (string.Equals(targetDatabase, sourceDatabase, StringComparison.Ordinal)) throw new BackupException("target_is_source");
        if (!File.Exists(dumpFile)) throw new BackupException("database_dump_missing");

        await using (var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await conn.OpenAsync(ct);
            await using (var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @n", conn))
            {
                exists.Parameters.AddWithValue("n", targetDatabase);
                if ((long)(await exists.ExecuteScalarAsync(ct))! != 0) throw new BackupException("target_exists");
            }
            await using var create = new NpgsqlCommand($"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(targetDatabase)}", conn);
            await create.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await RunAsync("pg_restore", ["--no-owner", "--no-privileges", "--exit-on-error", "--dbname", targetDatabase, dumpFile], ct);
        }
        catch
        {
            await DropQuietlyAsync(targetDatabase);
            throw;
        }
    }

    private void RequireCompanyDatabase(string name)
    {
        PhysicalIdentifier.Validate(name, nameof(name));
        if (name == options.SystemDatabaseName || name == options.MaintenanceDatabase || name is "postgres" or "template0" or "template1") throw new BackupException("not_a_company_database");
    }

    private async Task DropQuietlyAsync(string database)
    {
        try
        {
            await using var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString());
            await conn.OpenAsync(CancellationToken.None);
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {new NpgsqlCommandBuilder().QuoteIdentifier(database)} WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { /* the failure is already being reported */ }
    }

    private async Task RunAsync(string tool, string[] arguments, CancellationToken ct)
    {
        var exe = pgBinDirectory is null ? tool : Path.Combine(pgBinDirectory, tool);
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--host"); psi.ArgumentList.Add(options.Host ?? throw new BackupException("database_not_configured"));
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--username"); psi.ArgumentList.Add(options.AdminUsername ?? throw new BackupException("database_not_configured"));
        psi.ArgumentList.Add("--no-password");
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["PGPASSWORD"] = options.AdminPassword ?? throw new BackupException("database_not_configured");
        psi.Environment["PGSSLMODE"] = options.SslMode.ToString().ToLowerInvariant() switch { "verifyfull" => "verify-full", "verifyca" => "verify-ca", var m => m };
        if (options.SslRootCertificate is not null) psi.Environment["PGSSLROOTCERT"] = options.SslRootCertificate;

        Process process;
        try { process = Process.Start(psi) ?? throw new BackupException("tool_unavailable"); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException) { throw new BackupException("tool_unavailable"); }
        using (process)
        {
            // drain both pipes without keeping their content (it may name objects); only the exit code is reported
            var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(ct), process.StandardError.ReadToEndAsync(ct));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            try { await process.WaitForExitAsync(cts.Token); await drain; }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { /* already gone */ }
                throw new BackupException(ct.IsCancellationRequested ? "cancelled" : "tool_timeout");
            }
            if (process.ExitCode != 0) throw new BackupException(tool == "pg_dump" ? "dump_failed" : "restore_failed");
        }
    }
}
