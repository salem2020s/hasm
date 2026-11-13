using System.Collections.Concurrent;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Logging;
using Npgsql;
using CoIdentity = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;

namespace Hasm.Infrastructure.CompanyDatabase;

/// <summary>
/// A company database cannot be used right now. The message and <see cref="Code"/> are safe to log and to show: they never contain
/// a database name, role name, host, credential, connection string or SQL. Callers map it to a generic 503.
/// </summary>
public sealed class CompanyDatabaseException(string code) : Exception("The company database is not available.")
{
    public const string NotFound = "company_not_found";
    public const string NotReady = "company_not_ready";
    public const string Unavailable = "company_database_unavailable";
    public const string RuntimeNotProvisioned = "company_runtime_not_provisioned";
    public const string SchemaUnmigrated = "company_schema_unmigrated";
    public const string SchemaIncompatible = "company_schema_incompatible";
    public const string RuntimeOverprivileged = "company_runtime_overprivileged";

    public string Code { get; } = code;
}

/// <summary>
/// Runtime check that a company database matches the running backend: it carries this company's identity, every migration the backend
/// knows has been applied unchanged, and the database knows no migration the backend does not (a newer database is refused so an old
/// backend never writes into a schema it does not understand). It also confirms the runtime role can do its job and, unless explicitly
/// relaxed, that it holds no more than <see cref="CompanyRuntimePrivileges"/> allows. Every failure is a code, never SQL or a name.
/// </summary>
public static class CompanySchemaGate
{
    public static async Task EnsureCompatibleAsync(NpgsqlConnection conn, Guid companyId, IReadOnlyList<MigrationScript> scripts,
        bool allowOverprivileged, CancellationToken ct = default)
    {
        var applied = new Dictionary<int, string>();
        try
        {
            await using (var cmd = new NpgsqlCommand($"SELECT version, checksum FROM {SystemSchema.MigrationJournalTable}", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) applied[r.GetInt32(0)] = r.GetString(1);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { throw new CompanyDatabaseException(CompanyDatabaseException.SchemaUnmigrated); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege) { throw new CompanyDatabaseException(CompanyDatabaseException.RuntimeNotProvisioned); }

        foreach (var (version, checksum) in applied)
        {
            var known = scripts.FirstOrDefault(s => s.Version == version);
            if (known is null || known.Checksum != checksum) throw new CompanyDatabaseException(CompanyDatabaseException.SchemaIncompatible);
        }
        if (scripts.Any(s => !applied.ContainsKey(s.Version))) throw new CompanyDatabaseException(CompanyDatabaseException.SchemaUnmigrated);

        Guid? stored;
        try
        {
            await using var cmd = new NpgsqlCommand($"SELECT {CoIdentity.CompanyId} FROM {CoIdentity.Table}", conn);
            stored = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege) { throw new CompanyDatabaseException(CompanyDatabaseException.RuntimeNotProvisioned); }
        if (stored != companyId) throw new CompanyDatabaseException(CompanyDatabaseException.SchemaIncompatible);

        // The principal actually connected (not a name the caller expects) is what gets checked.
        string principal;
        await using (var who = new NpgsqlCommand("SELECT current_user::text", conn)) principal = (string)(await who.ExecuteScalarAsync(ct))!;
        if (!await CompanyRuntimePrivileges.HasRequiredAccessAsync(conn, principal, ct))
            throw new CompanyDatabaseException(CompanyDatabaseException.RuntimeNotProvisioned);
        if (!allowOverprivileged && (await CompanyRuntimePrivileges.FindViolationsAsync(conn, principal, ct)).Count > 0)
            throw new CompanyDatabaseException(CompanyDatabaseException.RuntimeOverprivileged);

        // Branch invariant (migration 0004): a company is operational only with exactly one active main branch. PostgreSQL enforces this at
        // every commit; the gate is the last, fail-closed line (a database altered outside the application is refused, never repaired here).
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn);
        exists.Parameters.AddWithValue("t", CompanySchema.Branch.Table);
        if (await exists.ExecuteScalarAsync(ct) is true)   // absent only for a database/scripts pair that predates migration 0004
        {
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM {CompanySchema.Branch.Table} WHERE {CompanySchema.Branch.IsMain} AND {CompanySchema.Branch.IsActive}", conn);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) != 1)
                throw new CompanyDatabaseException(CompanyDatabaseException.SchemaIncompatible);
        }
    }
}

/// <summary>
/// Opens the database of one company for the API, and only that company's. The company is resolved through the central registry
/// (system database, runtime account), must be Ready, and is reached with that company's OWN least-privilege PostgreSQL role (credential
/// from <see cref="ICompanyCredentialProvider"/>), so PostgreSQL itself refuses any other company database. Connections come from a
/// bounded per-company pool. Failures are <see cref="CompanyDatabaseException"/> with a short code; physical names and secrets
/// never leave this class (not in exceptions, not in logs).
/// </summary>
public sealed class CompanyDatabaseResolver
{
    private sealed record Resolved(string ConnectionString, DateTimeOffset ExpiresAt);

    /// <summary>Test seam only: replaces the connection string (physical name, role, password) so single-principal test servers can be used.</summary>
    internal Func<string, string, string, string>? ConnectionStringOverride { get; init; }

    private readonly HasmDatabaseOptions options;
    private readonly CompanyRegistry registry;
    private readonly ICompanyCredentialProvider credentials;
    private readonly IReadOnlyList<MigrationScript> scripts;
    private readonly TimeProvider clock;
    private readonly ILogger? logger;
    private readonly ConcurrentDictionary<Guid, Resolved> resolved = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> verifiedUntil = new();

    /// <summary>How long a successful registry/credential lookup is reused (a state or credential change takes effect after this).</summary>
    public TimeSpan ResolutionLifetime { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>How long a successful schema check is reused.</summary>
    public TimeSpan SchemaCheckLifetime { get; init; } = TimeSpan.FromSeconds(60);

    public CompanyDatabaseResolver(HasmDatabaseOptions options, CompanyRegistry registry, ICompanyCredentialProvider credentials,
        TimeProvider clock, ILogger? logger = null, IReadOnlyList<MigrationScript>? companyScripts = null)
    {
        this.options = options;
        this.registry = registry;
        this.credentials = credentials;
        this.clock = clock;
        this.logger = logger;
        scripts = companyScripts ?? MigrationScript.LoadEmbeddedCompany();
    }

    /// <summary>Opens a pooled connection to the company's database after all checks. Dispose it to return it to the pool.</summary>
    public async Task<NpgsqlConnection> OpenAsync(Guid companyId, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var target = await ResolveAsync(companyId, ct);
            var conn = new NpgsqlConnection(target.ConnectionString);
            try
            {
                await conn.OpenAsync(ct);
                if (!verifiedUntil.TryGetValue(companyId, out var until) || until <= clock.GetUtcNow())
                {
                    await CompanySchemaGate.EnsureCompatibleAsync(conn, companyId, scripts, options.AllowOverprivilegedCompanyRuntime, ct);
                    verifiedUntil[companyId] = clock.GetUtcNow() + SchemaCheckLifetime;
                }
                return conn;
            }
            catch (Exception ex)
            {
                await conn.DisposeAsync();
                if (ex is OperationCanceledException) throw;
                // Rotated or removed credentials: forget the cached resolution once and retry with a fresh read.
                if (ex is PostgresException { SqlState: PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification } && attempt == 0)
                {
                    Forget(companyId);
                    continue;
                }
                var mapped = Map(ex);
                if (mapped is not CompanyDatabaseException { Code: CompanyDatabaseException.SchemaUnmigrated or CompanyDatabaseException.SchemaIncompatible or CompanyDatabaseException.RuntimeNotProvisioned or CompanyDatabaseException.RuntimeOverprivileged })
                    Forget(companyId);
                verifiedUntil.TryRemove(companyId, out _);
                logger?.LogError("company.database_unavailable company={CompanyId} code={Code}", companyId, (mapped as CompanyDatabaseException)?.Code ?? CompanyDatabaseException.Unavailable);
                throw mapped;
            }
        }
    }

    /// <summary>Drops cached resolution for a company (tests, rotation).</summary>
    public void Forget(Guid companyId)
    {
        resolved.TryRemove(companyId, out _);
        verifiedUntil.TryRemove(companyId, out _);
    }

    private async Task<Resolved> ResolveAsync(Guid companyId, CancellationToken ct)
    {
        if (resolved.TryGetValue(companyId, out var cached) && cached.ExpiresAt > clock.GetUtcNow()) return cached;
        try
        {
            var company = await registry.GetAsync(companyId, ct) ?? throw new CompanyDatabaseException(CompanyDatabaseException.NotFound);
            if (company.State != ProvisioningState.Ready) throw new CompanyDatabaseException(CompanyDatabaseException.NotReady);

            var credential = await credentials.GetAsync(companyId, ct);
            var role = CompanyRuntimeRole.NameFor(company.PhysicalDatabaseName);
            // A credential of another company's role (misplaced file) is refused here, before any connection attempt.
            if (credential is null || credential.Username != role) throw new CompanyDatabaseException(CompanyDatabaseException.RuntimeNotProvisioned);

            var connectionString = options.BuildCompanyRuntimeConnectionString(company.PhysicalDatabaseName, role, credential.Password);
            if (ConnectionStringOverride is not null) connectionString = ConnectionStringOverride(company.PhysicalDatabaseName, role, credential.Password);
            var result = new Resolved(connectionString, clock.GetUtcNow() + ResolutionLifetime);
            resolved[companyId] = result;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var mapped = Map(ex);
            logger?.LogError("company.database_unavailable company={CompanyId} code={Code}", companyId, (mapped as CompanyDatabaseException)?.Code ?? CompanyDatabaseException.Unavailable);
            throw mapped;
        }
    }

    /// <summary>Converts any failure into a safe exception; the original (which may name hosts or databases) is dropped.</summary>
    private static CompanyDatabaseException Map(Exception ex) => ex switch
    {
        CompanyDatabaseException c => c,
        CompanyCredentialException => new CompanyDatabaseException(CompanyDatabaseException.RuntimeNotProvisioned),
        _ => new CompanyDatabaseException(CompanyDatabaseException.Unavailable),
    };
}
