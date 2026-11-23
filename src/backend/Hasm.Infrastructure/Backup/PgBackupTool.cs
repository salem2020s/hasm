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
    // locale names are placed inside a single-quoted literal: letters, digits and _ . @ space ( ) - only (no quote, no backslash, no control character; \z so a trailing newline is refused)
    private static readonly System.Text.RegularExpressions.Regex Safe = new(@"\A[A-Za-z0-9_.@ ()-]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly TimeSpan limit = timeout ?? TimeSpan.FromMinutes(30);

    /// <summary>
    /// Dumps <paramref name="sourceDatabase"/> (a company database) to <paramref name="dumpFile"/>, which must not exist. A partial file is removed on failure. Returns the encoding and locale of the source captured at dump time:
    /// the caller stores them with the backup (<see cref="BackupManifest.DatabaseLocale"/>) so a restore never depends on the source database still existing (decision D-053). A source that does not exist is <c>dump_failed</c>.
    /// </summary>
    public Task<DatabaseLocale> DumpAsync(string sourceDatabase, string dumpFile, CancellationToken ct = default)
    {
        RequireCompanyDatabase(sourceDatabase);
        return DumpCoreAsync(sourceDatabase, dumpFile, [], ct);
    }

    /// <summary>
    /// Dumps the configured SYSTEM database (never a caller-supplied name; slice 4, D-070) to <paramref name="dumpFile"/>, which must not exist, WITHOUT the data of <paramref name="excludeTableData"/> (physical
    /// table names of the secret-store tables, <see cref="BackupRunService.SystemDumpExclusions"/>): their structure is kept, their rows never reach an unencrypted set.
    /// </summary>
    public Task<DatabaseLocale> DumpSystemAsync(string dumpFile, IReadOnlyList<string> excludeTableData, CancellationToken ct = default)
    {
        var name = options.SystemDatabaseName ?? throw new BackupException("database_not_configured");
        PhysicalIdentifier.Validate(name, nameof(name));
        var args = new List<string>();
        foreach (var table in excludeTableData)
        {
            if (!PhysicalIdentifier.IsValid(table)) throw new BackupException("exclusion_invalid");
            args.Add("--exclude-table-data=public." + table);
        }
        return DumpCoreAsync(name, dumpFile, args, ct);
    }

    /// <summary>
    /// Checks that the archive's table of contents is readable (<c>pg_restore --list</c>; only the exit code is used, the output is discarded). This proves NOTHING about the data restoring: integrity is the SHA-256 in
    /// the manifest. A failure is <c>archive_unreadable</c>.
    /// </summary>
    public async Task VerifyArchiveAsync(string dumpFile, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(dumpFile)) throw new BackupException("path_not_absolute");
        if (!File.Exists(dumpFile)) throw new BackupException("database_dump_missing");
        var psi = await PrepareAsync("pg_restore", ["--list", dumpFile], ct);
        try { await RunAsync("pg_restore", psi, ct); }
        catch (BackupException ex) when (ex.Code == "restore_failed") { throw new BackupException("archive_unreadable"); }
    }

    /// <summary>Dumps a company database to <c>pg_dump</c>'s standard output, consumed by <paramref name="consume"/> (slice 5, D-091: piped into the encryptor, no plaintext at rest).</summary>
    public Task<DatabaseLocale> DumpCompanyToStreamAsync(string sourceDatabase, Func<Stream, CancellationToken, Task> consume, CancellationToken ct = default)
    {
        RequireCompanyDatabase(sourceDatabase);
        return DumpToStreamCoreAsync(sourceDatabase, [], consume, ct);
    }

    /// <summary>Dumps the configured SYSTEM database to standard output without the data of <paramref name="excludeTableData"/>, consumed by <paramref name="consume"/>.</summary>
    public Task<DatabaseLocale> DumpSystemToStreamAsync(IReadOnlyList<string> excludeTableData, Func<Stream, CancellationToken, Task> consume, CancellationToken ct = default)
    {
        var name = options.SystemDatabaseName ?? throw new BackupException("database_not_configured");
        PhysicalIdentifier.Validate(name, nameof(name));
        var args = new List<string>();
        foreach (var table in excludeTableData)
        {
            if (!PhysicalIdentifier.IsValid(table)) throw new BackupException("exclusion_invalid");
            args.Add("--exclude-table-data=public." + table);
        }
        return DumpToStreamCoreAsync(name, args, consume, ct);
    }

    private async Task<DatabaseLocale> DumpToStreamCoreAsync(string sourceDatabase, IReadOnlyList<string> extra, Func<Stream, CancellationToken, Task> consume, CancellationToken ct)
    {
        var psi = await PrepareAsync("pg_dump", ["--format=custom", "--no-owner", "--no-privileges", .. extra, "--dbname", sourceDatabase], ct);
        DatabaseLocale locale;
        await using (var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await conn.OpenAsync(ct);
            locale = await ReadLocaleAsync(conn, sourceDatabase, ct) ?? throw new BackupException("dump_failed");
        }
        await RunStreamingAsync("pg_dump", psi, p => consume(p.StandardOutput.BaseStream, ct), ct);
        return locale;
    }

    private async Task<DatabaseLocale> DumpCoreAsync(string sourceDatabase, string dumpFile, IReadOnlyList<string> extra, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(dumpFile)) throw new BackupException("path_not_absolute");
        if (File.Exists(dumpFile)) throw new BackupException("dump_file_exists");
        var psi = await PrepareAsync("pg_dump", ["--format=custom", "--no-owner", "--no-privileges", .. extra, "--file", dumpFile, "--dbname", sourceDatabase], ct);   // client version and TLS settled before any database contact
        DatabaseLocale locale;
        await using (var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await conn.OpenAsync(ct);
            locale = await ReadLocaleAsync(conn, sourceDatabase, ct) ?? throw new BackupException("dump_failed");
        }
        try
        {
            await RunAsync("pg_dump", psi, ct);
            if (!File.Exists(dumpFile) || new FileInfo(dumpFile).Length == 0) throw new BackupException("dump_failed");
        }
        catch
        {
            try { if (File.Exists(dumpFile)) File.Delete(dumpFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
        return locale;
    }

    /// <summary>
    /// Creates <paramref name="targetDatabase"/> and restores the dump into it. Nothing else is touched; the new database is dropped when the restore fails. <paramref name="sourceLocale"/> is the locale captured at dump time
    /// (from the manifest); when it is absent (an older backup set) the source database is read, and when that does not exist either the restore is refused with <c>source_locale_unknown</c> before anything is created.
    /// </summary>
    public async Task RestoreIntoNewDatabaseAsync(string dumpFile, string targetDatabase, string sourceDatabase, DatabaseLocale? sourceLocale = null, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(dumpFile)) throw new BackupException("path_not_absolute");
        if (!File.Exists(dumpFile)) throw new BackupException("database_dump_missing");
        await RestoreCoreAsync(dumpFile, null, targetDatabase, sourceDatabase, sourceLocale, ct);
    }

    /// <summary>
    /// As <see cref="RestoreIntoNewDatabaseAsync"/>, but the archive is STREAMED into <c>pg_restore</c>'s standard input by <paramref name="produce"/> (slice 5, D-091: decrypted, authenticated chunks;
    /// no plaintext file). A producer failure is a failed restore: the new database is dropped.
    /// </summary>
    public Task RestoreIntoNewDatabaseFromStreamAsync(Func<Stream, CancellationToken, Task> produce, string targetDatabase, string sourceDatabase, DatabaseLocale? sourceLocale, CancellationToken ct = default) =>
        RestoreCoreAsync(null, produce, targetDatabase, sourceDatabase, sourceLocale, ct);

    /// <summary>
    /// The recovery test (slice 5, D-085): a SYSTEM dump streamed into a NEW disposable database <paramref name="targetDatabase"/> (never the system, maintenance or a built-in database) with the locale
    /// captured at dump time. The live system database is never touched.
    /// </summary>
    public Task RestoreSystemCopyFromStreamAsync(Func<Stream, CancellationToken, Task> produce, string targetDatabase, DatabaseLocale capturedLocale, CancellationToken ct = default) =>
        RestoreCoreAsync(null, produce, targetDatabase, options.SystemDatabaseName ?? throw new BackupException("database_not_configured"), capturedLocale, ct, systemSource: true);

    private async Task RestoreCoreAsync(string? dumpFile, Func<Stream, CancellationToken, Task>? produce, string targetDatabase, string sourceDatabase, DatabaseLocale? sourceLocale, CancellationToken ct,
        bool systemSource = false)
    {
        RequireCompanyDatabase(targetDatabase);
        if (!systemSource) RequireCompanyDatabase(sourceDatabase);
        else if (sourceLocale is null) throw new BackupException("source_locale_unknown");
        if (string.Equals(targetDatabase, sourceDatabase, StringComparison.Ordinal)) throw new BackupException("target_is_source");
        // the client version probe and the environment (ssl_root_certificate_required) come BEFORE CREATE DATABASE, so a refusal never costs a create/drop round trip
        var psi = await PrepareAsync("pg_restore", dumpFile is null ? ["--no-owner", "--no-privileges", "--exit-on-error", "--dbname", targetDatabase] : ["--no-owner", "--no-privileges", "--exit-on-error", "--dbname", targetDatabase, dumpFile], ct);

        await using (var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await conn.OpenAsync(ct);
            await using (var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @n", conn))
            {
                exists.Parameters.AddWithValue("n", targetDatabase);
                if ((long)(await exists.ExecuteScalarAsync(ct))! != 0) throw new BackupException("target_exists");
            }
            // template0 (never the modifiable template1: nothing a local admin put there leaks in), with the encoding and libc locale captured at dump time (or, for an older set, read from the source)
            var locale = ResolveLocale(sourceLocale, sourceLocale is null ? await ReadLocaleAsync(conn, sourceDatabase, ct) : null);
            var clause = LocaleClause(locale, conn.PostgreSqlVersion.Major);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(targetDatabase)} TEMPLATE template0{clause}", conn);
            try { await create.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (CreateFailureCode(ex.SqlState) is not null) { throw new BackupException(CreateFailureCode(ex.SqlState)!); }   // lost a creation race
        }
        try
        {
            if (produce is null) await RunAsync("pg_restore", psi, ct);
            else
            {
                psi.RedirectStandardInput = true;
                await RunStreamingAsync("pg_restore", psi, async p =>
                {
                    try { await produce(p.StandardInput.BaseStream, ct); }
                    finally { p.StandardInput.Close(); }
                }, ct);
            }
        }
        catch (Exception ex)
        {
            // the half-restored target must not stay behind; when it cannot be dropped that is reported distinctly (the caller must remove it) and the original cause is kept
            if (!await TryDropAsync(targetDatabase)) throw new BackupException("restore_failed_cleanup_failed", ex is BackupException be ? be.Code : ex.GetType().Name);
            throw;
        }
    }

    /// <summary>The encoding, libc locale and locale provider of a database; null when it does not exist. <c>datlocprovider</c> exists only from PostgreSQL 15: it is read through to_jsonb so older servers work (absent means libc).</summary>
    internal static async Task<DatabaseLocale?> ReadLocaleAsync(NpgsqlConnection conn, string database, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_encoding_to_char(d.encoding), d.datcollate, d.datctype, to_jsonb(d)->>'datlocprovider' FROM pg_database d WHERE d.datname = @n", conn);
        cmd.Parameters.AddWithValue("n", database);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new DatabaseLocale(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    /// <summary>The locale the restore reproduces: the one captured at dump time wins; otherwise the live source; with neither the restore is refused (<c>source_locale_unknown</c>), never the template0 default.</summary>
    internal static DatabaseLocale ResolveLocale(DatabaseLocale? captured, DatabaseLocale? live) => captured ?? live ?? throw new BackupException("source_locale_unknown");

    /// <summary>Refuses (<c>locale_unsupported</c>) a locale a restore could not reproduce: the same rules as <see cref="LocaleClause"/>, checked at backup time (decision D-059).</summary>
    internal static void EnsureRestorable(DatabaseLocale locale) => _ = LocaleClause(locale, 15);

    /// <summary>The same database locale: encoding, collation and ctype exactly; a provider of null (PostgreSQL 14, libc only) and <c>c</c> are the same libc provider (decision D-059).</summary>
    internal static bool SameLocale(DatabaseLocale a, DatabaseLocale b) =>
        string.Equals(a.Encoding, b.Encoding, StringComparison.Ordinal) && string.Equals(a.Collate, b.Collate, StringComparison.Ordinal) && string.Equals(a.Ctype, b.Ctype, StringComparison.Ordinal)
        && string.Equals(a.Provider ?? "c", b.Provider ?? "c", StringComparison.Ordinal);

    /// <summary>
    /// The ENCODING / LOCALE_PROVIDER / LC_COLLATE / LC_CTYPE clause that reproduces the source database on template0. A source with an ICU or builtin locale provider, a missing locale or a name with unexpected characters is
    /// refused (<c>locale_unsupported</c>): the new database must never silently get a different collation than the source. The source is libc (provider <c>c</c>, or no provider column on PostgreSQL 14), so on a TARGET server of
    /// major version 15 or later <c>LOCALE_PROVIDER libc</c> is pinned (template0 may be ICU or builtin there); PostgreSQL 14 has no such keyword and only libc.
    /// </summary>
    internal static string LocaleClause(DatabaseLocale locale, int targetServerMajor)
    {
        if (locale.Provider is not (null or "c")) throw new BackupException("locale_unsupported");
        if (locale.Encoding is null || locale.Collate is null || locale.Ctype is null || !Safe.IsMatch(locale.Encoding) || !Safe.IsMatch(locale.Collate) || !Safe.IsMatch(locale.Ctype)) throw new BackupException("locale_unsupported");
        var provider = targetServerMajor >= 15 ? " LOCALE_PROVIDER libc" : "";
        return $" ENCODING '{locale.Encoding}'{provider} LC_COLLATE '{locale.Collate}' LC_CTYPE '{locale.Ctype}'";
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
            await DatabaseDrop.ExecuteAsync(conn, database, ifExists: true, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { return false; }
    }

    /// <summary>First major version of libpq that accepts <c>PGSSLROOTCERT=system</c>.</summary>
    internal const int SystemRootCertificateMinimumMajor = 16;

    /// <summary>
    /// The ONLY environment the PostgreSQL tools get: <c>PATH</c> and <c>SystemRoot</c> (when present), the credential, the TLS settings and a connect timeout. With the environment cleared there is no HOME, so libpq cannot find
    /// <c>~/.postgresql/root.crt</c>: in verify-ca/verify-full mode the configured certificate is passed. Without one, the system trust store is named explicitly (<c>PGSSLROOTCERT=system</c>) ONLY when the tool's client
    /// major version is at least 16 (older libpq does not know the value); for an older or unknown client the call is refused (<c>ssl_root_certificate_required</c>) after the <c>--version</c> probe and before the tool connects or any database is created: TLS validation is never downgraded.
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> clientMajors = new(StringComparer.Ordinal);

    /// <summary>The working directory of every tool process: a fixed system directory (never the inherited service directory, which could hold a planted library or configuration file).</summary>
    internal static string ToolWorkingDirectory => OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/";

    private string ToolPath(string tool) => pgBinDirectory is null ? tool : Path.Combine(pgBinDirectory, tool);

    /// <summary>The major version of the tool (its <c>--version</c> output), probed once per tool and instance; 0 when it cannot be read. Thread-safe: a concurrent first probe may run twice with the same result.</summary>
    internal async Task<int> ClientMajorAsync(string tool, CancellationToken ct)
    {
        if (clientMajors.TryGetValue(tool, out var known)) return known;
        var psi = new ProcessStartInfo(ToolPath(tool), "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = ToolWorkingDirectory };
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

    /// <summary>
    /// The complete start information of one tool run: connection arguments, the tool's own arguments, the fixed working directory and the cleared environment of <see cref="BuildEnvironment"/> (which needs the probed
    /// client version). Every refusal of the configuration (<c>database_not_configured</c>, <c>ssl_root_certificate_required</c>, <c>tool_unavailable</c>) happens here, before any database contact.
    /// </summary>
    internal async Task<ProcessStartInfo> PrepareAsync(string tool, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ToolPath(tool)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = ToolWorkingDirectory };
        psi.ArgumentList.Add("--host"); psi.ArgumentList.Add(options.Host ?? throw new BackupException("database_not_configured"));
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--username"); psi.ArgumentList.Add(options.AdminUsername ?? throw new BackupException("database_not_configured"));
        psi.ArgumentList.Add("--no-password");
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment.Clear();                                   // nothing of the service environment (connection strings, secrets) reaches the tool
        foreach (var (key, value) in BuildEnvironment(options, Environment.GetEnvironmentVariable, await ClientMajorAsync(tool, ct))) psi.Environment[key] = value;
        return psi;
    }

    /// <summary>The backup code of a CREATE DATABASE failure that has a meaning for the caller (a lost creation race); null for every other SQLSTATE.</summary>
    internal static string? CreateFailureCode(string? sqlState) => sqlState == PostgresErrorCodes.DuplicateDatabase ? "target_exists" : null;

    /// <summary>
    /// Runs a prepared tool while <paramref name="io"/> feeds its standard input or consumes its standard output (the other pipe and standard error go to a null stream). Within the same time limit; a
    /// failure of <paramref name="io"/> kills the tool and is rethrown; a non-zero exit is <c>dump_failed</c> / <c>restore_failed</c>.
    /// </summary>
    internal async Task RunStreamingAsync(string tool, ProcessStartInfo psi, Func<Process, Task> io, CancellationToken ct)
    {
        Process process;
        try { process = Process.Start(psi) ?? throw new BackupException("tool_unavailable"); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException) { throw new BackupException("tool_unavailable"); }
        using (process)
        {
            var drain = psi.RedirectStandardInput
                ? Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct), process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct))
                : process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            try
            {
                await io(process).WaitAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                await drain;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { /* already gone */ }
                throw new BackupException(ct.IsCancellationRequested ? "cancelled" : "tool_timeout");
            }
            catch (IOException) when (process.HasExited)
            {
                // the tool stopped first (for example pg_restore refused the archive) and closed its pipe: that is the tool's failure, never an unhandled pipe error
                throw new BackupException(tool == "pg_dump" ? "dump_failed" : "restore_failed");
            }
            catch
            {
                try { process.Kill(true); } catch (InvalidOperationException) { /* already gone */ }
                throw;
            }
            if (process.ExitCode != 0) throw new BackupException(tool == "pg_dump" ? "dump_failed" : "restore_failed");
        }
    }

    /// <summary>Runs a prepared tool to completion; only its exit code is reported (<c>dump_failed</c> / <c>restore_failed</c>, <c>tool_timeout</c>, <c>cancelled</c>, <c>tool_unavailable</c>).</summary>
    internal async Task RunAsync(string tool, ProcessStartInfo psi, CancellationToken ct)
    {
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
