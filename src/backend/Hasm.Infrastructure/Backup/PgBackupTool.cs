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
    private static readonly System.Text.RegularExpressions.Regex Safe = new("^[A-Za-z0-9_.@ -]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);
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
        if (!Path.IsPathFullyQualified(dumpFile)) throw new BackupException("path_not_absolute");
        if (!File.Exists(dumpFile)) throw new BackupException("database_dump_missing");

        await using (var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await conn.OpenAsync(ct);
            await using (var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @n", conn))
            {
                exists.Parameters.AddWithValue("n", targetDatabase);
                if ((long)(await exists.ExecuteScalarAsync(ct))! != 0) throw new BackupException("target_exists");
            }
            // template0 (never the modifiable template1: nothing a local admin put there leaks in), with the encoding of the source database when it can be read
            string? encoding = null, collate = null, ctype = null, provider = null;
            // datlocprovider exists only from PostgreSQL 15: read it through to_jsonb so older servers work (absent or null means libc)
            await using (var enc = new NpgsqlCommand("SELECT pg_encoding_to_char(d.encoding), d.datcollate, d.datctype, to_jsonb(d)->>'datlocprovider' FROM pg_database d WHERE d.datname = @n", conn))
            {
                enc.Parameters.AddWithValue("n", sourceDatabase);
                await using var r = await enc.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    encoding = r.GetString(0);
                    collate = r.IsDBNull(1) ? null : r.GetString(1);
                    ctype = r.IsDBNull(2) ? null : r.GetString(2);
                    provider = r.IsDBNull(3) ? null : r.GetString(3);
                }
            }
            var clause = LocaleClause(encoding, collate, ctype, provider);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(targetDatabase)} TEMPLATE template0{clause}", conn);
            try { await create.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (CreateFailureCode(ex.SqlState) is not null) { throw new BackupException(CreateFailureCode(ex.SqlState)!); }   // lost a creation race
        }
        try
        {
            await RunAsync("pg_restore", ["--no-owner", "--no-privileges", "--exit-on-error", "--dbname", targetDatabase, dumpFile], ct);
        }
        catch (Exception ex)
        {
            // the half-restored target must not stay behind; when it cannot be dropped that is reported distinctly (the caller must remove it) and the original cause is kept
            if (!await TryDropAsync(targetDatabase)) throw new BackupException("restore_failed_cleanup_failed", ex is BackupException be ? be.Code : ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// The ENCODING / LC_COLLATE / LC_CTYPE clause that reproduces the source database on template0. A source with an ICU or builtin locale provider, a missing locale or a name with unexpected characters is refused
    /// (<c>locale_unsupported</c>): the new database must never silently get a different collation than the source. A source that does not exist yields no clause (the restore itself then fails on the dump).
    /// </summary>
    internal static string LocaleClause(string? encoding, string? collate, string? ctype, string? provider)
    {
        if (encoding is null) return "";
        if (provider is not (null or "c")) throw new BackupException("locale_unsupported");
        if (collate is null || ctype is null || !Safe.IsMatch(encoding) || !Safe.IsMatch(collate) || !Safe.IsMatch(ctype)) throw new BackupException("locale_unsupported");
        return $" ENCODING '{encoding}' LC_COLLATE '{collate}' LC_CTYPE '{ctype}'";
    }

    private void RequireCompanyDatabase(string name)
    {
        PhysicalIdentifier.Validate(name, nameof(name));
        if (name == options.SystemDatabaseName || name == options.MaintenanceDatabase || name is "postgres" or "template0" or "template1") throw new BackupException("not_a_company_database");
    }

    private async Task<bool> TryDropAsync(string database)
    {
        try
        {
            await using var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString());
            await conn.OpenAsync(CancellationToken.None);
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {new NpgsqlCommandBuilder().QuoteIdentifier(database)} WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { return false; }
    }

    /// <summary>First major version of libpq that accepts <c>PGSSLROOTCERT=system</c>.</summary>
    internal const int SystemRootCertificateMinimumMajor = 16;

    /// <summary>
    /// The ONLY environment the PostgreSQL tools get: <c>PATH</c> and <c>SystemRoot</c> (when present), the credential, the TLS settings and a connect timeout. With the environment cleared there is no HOME, so libpq cannot find
    /// <c>~/.postgresql/root.crt</c>: in verify-ca/verify-full mode the configured certificate is passed. Without one, the system trust store is named explicitly (<c>PGSSLROOTCERT=system</c>) ONLY when the tool's client
    /// major version is at least 16 (older libpq does not know the value); for an older or unknown client the call is refused BEFORE any tool is launched (<c>ssl_root_certificate_required</c>): TLS validation is never downgraded.
    /// </summary>
    internal static Dictionary<string, string> BuildEnvironment(HasmDatabaseOptions options, Func<string, string?> getVariable, int clientMajor)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var keep in new[] { "PATH", "SystemRoot" }) if (getVariable(keep) is { } value) env[keep] = value;
        env["PGPASSWORD"] = options.AdminPassword ?? throw new BackupException("database_not_configured");
        var mode = options.SslMode.ToString().ToLowerInvariant() switch { "verifyfull" => "verify-full", "verifyca" => "verify-ca", var m => m };
        env["PGSSLMODE"] = mode;
        if (options.SslRootCertificate is not null) env["PGSSLROOTCERT"] = options.SslRootCertificate;
        else if (mode is "verify-full" or "verify-ca")
        {
            if (clientMajor < SystemRootCertificateMinimumMajor) throw new BackupException("ssl_root_certificate_required");
            env["PGSSLROOTCERT"] = "system";
        }
        env["PGCONNECT_TIMEOUT"] = "30";
        return env;
    }

    /// <summary>The major version in the output of <c>pg_dump --version</c> (for example <c>pg_dump (PostgreSQL) 16.4</c>); 0 when it cannot be read (treated as too old for the system trust store).</summary>
    internal static int ParseClientMajor(string? versionOutput)
    {
        var m = versionOutput is null ? null : System.Text.RegularExpressions.Regex.Match(versionOutput, @"\(PostgreSQL\)\s+(\d+)");
        return m is { Success: true } && int.TryParse(m.Groups[1].Value, out var major) ? major : 0;
    }

    private readonly Dictionary<string, int> clientMajors = [];

    private async Task<int> ClientMajorAsync(string tool, CancellationToken ct)
    {
        if (clientMajors.TryGetValue(tool, out var known)) return known;
        var exe = pgBinDirectory is null ? tool : Path.Combine(pgBinDirectory, tool);
        var psi = new ProcessStartInfo(exe, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment.Clear();
        foreach (var keep in new[] { "PATH", "SystemRoot" }) if (Environment.GetEnvironmentVariable(keep) is { } value) psi.Environment[keep] = value;
        Process process;
        try { process = Process.Start(psi) ?? throw new BackupException("tool_unavailable"); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException) { throw new BackupException("tool_unavailable"); }
        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            string output;
            try
            {
                var read = process.StandardOutput.ReadToEndAsync(cts.Token);
                _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cts.Token);
                await process.WaitForExitAsync(cts.Token);
                output = await read;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { /* already gone */ }
                throw new BackupException(ct.IsCancellationRequested ? "cancelled" : "tool_timeout");
            }
            return clientMajors[tool] = process.ExitCode == 0 ? ParseClientMajor(output) : 0;
        }
    }

    /// <summary>The backup code of a CREATE DATABASE failure that has a meaning for the caller (a lost creation race); null for every other SQLSTATE.</summary>
    internal static string? CreateFailureCode(string? sqlState) => sqlState == PostgresErrorCodes.DuplicateDatabase ? "target_exists" : null;

    private async Task RunAsync(string tool, string[] arguments, CancellationToken ct)
    {
        var exe = pgBinDirectory is null ? tool : Path.Combine(pgBinDirectory, tool);
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--host"); psi.ArgumentList.Add(options.Host ?? throw new BackupException("database_not_configured"));
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--username"); psi.ArgumentList.Add(options.AdminUsername ?? throw new BackupException("database_not_configured"));
        psi.ArgumentList.Add("--no-password");
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment.Clear();                                   // nothing of the service environment (connection strings, secrets) reaches the tool
        foreach (var (key, value) in BuildEnvironment(options, Environment.GetEnvironmentVariable, await ClientMajorAsync(tool, ct))) psi.Environment[key] = value;

        Process process;
        try { process = Process.Start(psi) ?? throw new BackupException("tool_unavailable"); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException) { throw new BackupException("tool_unavailable"); }
        using (process)
        {
            // copy both pipes to a null stream: the content (it may name objects) is neither kept nor buffered; only the exit code is reported
            var drain = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct), process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct));
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
