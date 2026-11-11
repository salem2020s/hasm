using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;

namespace Hasm.Infrastructure.CompanyDatabase;

public enum ProvisioningOutcome
{
    /// <summary>This call created/migrated the company database and marked the company Ready.</summary>
    Provisioned,
    /// <summary>The company was already Ready and its database mapping was verified; nothing changed.</summary>
    AlreadyReady,
    /// <summary>Another process is provisioning this company right now; this call did nothing.</summary>
    InProgressElsewhere,
}

public sealed record ProvisioningResult(Guid CompanyId, ProvisioningOutcome Outcome, bool DatabaseCreated, IReadOnlyList<int> AppliedMigrations, int Attempt);

public enum ProvisioningStep { DatabaseEnsured, Migrated, IdentityBound, AccessRestricted }

public sealed class CompanyNotFoundException(Guid id) : Exception($"Company {id} is not registered.");

/// <summary>The company database does not match the company registry (foreign, wrong identity, or missing). Never auto-repaired.</summary>
public sealed class CompanyConsistencyException(string message) : Exception(message);

/// <summary>
/// Explicit administrative operation (never run at startup, never behind an unauthenticated API):
/// creates the separate PostgreSQL database of a registered company, applies its minimal schema and
/// marks the company Ready. Uses the privileged account only.
///
/// Concurrency and recovery: a session-level PostgreSQL advisory lock per company, held on a dedicated
/// connection to the system database for the whole operation, guarantees a single active provisioner.
/// If the process dies the connection closes and the lock is released, so a company left in
/// 'Provisioning' without a lock holder is by definition interrupted and is safely resumed. All steps are
/// idempotent. There is no distributed transaction: the system registry and the company database are
/// reconciled by re-running. A failure (or interruption) NEVER drops the company database.
/// Requires direct (non-transaction-pooled) PostgreSQL connections, as session advisory locks are used.
/// </summary>
public sealed class CompanyProvisioner(
    HasmDatabaseOptions options,
    CompanyRegistry registry,
    IReadOnlyList<MigrationScript>? companyScripts = null)
{
    private const int LockNamespace = 0x4850524F; // "HPRO": keeps company locks apart from the migration lock
    private static readonly string[] ReservedNames = ["postgres", "template0", "template1"];

    private readonly IReadOnlyList<MigrationScript> scripts = companyScripts ?? MigrationScript.LoadEmbeddedCompany();

    /// <summary>Test seam: invoked after each step. Throwing OperationCanceledException simulates an interrupted process.</summary>
    internal Func<ProvisioningStep, CancellationToken, Task>? AfterStep { get; init; }

    public async Task<ProvisioningResult> ProvisionAsync(Guid companyId, CancellationToken ct = default)
    {
        options.EnsureValid(requireAdmin: true);
        MigrationScript.Validate(scripts);

        await using var lockConn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await lockConn.OpenAsync(ct);
        if (!await TryLockAsync(lockConn, companyId, ct))
            return new ProvisioningResult(companyId, ProvisioningOutcome.InProgressElsewhere, false, [], 0);
        // The lock is released when lockConn is disposed (connections are not pooled); no explicit unlock needed.

        var company = await registry.GetAsync(companyId, ct) ?? throw new CompanyNotFoundException(companyId);
        EnsureSafeTarget(company.PhysicalDatabaseName);

        if (company.State == ProvisioningState.Ready)
        {
            await VerifyMappingAsync(company, ct);
            return new ProvisioningResult(companyId, ProvisioningOutcome.AlreadyReady, false, [], company.ProvisioningAttempts);
        }

        // Pending, Failed, or Provisioning with no lock holder (interrupted): start/resume.
        if (!await registry.BeginProvisioningAsync(companyId, ct))
            throw new InvalidOperationException("Company state changed unexpectedly during provisioning start.");
        var attempt = company.ProvisioningAttempts + 1;

        try
        {
            var created = await EnsureDatabaseAsync(company.PhysicalDatabaseName, ct);
            await Step(ProvisioningStep.DatabaseEnsured, ct);

            var migrated = await new MigrationRunner(options.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName))
                .ApplyAsync(scripts, ct);
            await Step(ProvisioningStep.Migrated, ct);

            await BindIdentityAsync(company, ct);
            await Step(ProvisioningStep.IdentityBound, ct);

            await RestrictAccessAsync(company.PhysicalDatabaseName, ct);
            await Step(ProvisioningStep.AccessRestricted, ct);

            await VerifyMappingAsync(company, ct);
            if (!await registry.CompleteProvisioningAsync(companyId, CancellationToken.None))
                throw new InvalidOperationException("Company state changed unexpectedly while completing provisioning.");
            return new ProvisioningResult(companyId, ProvisioningOutcome.Provisioned, created, migrated.Applied, attempt);
        }
        catch (OperationCanceledException)
        {
            // Interrupted, not failed: leave 'Provisioning' so the next run resumes. Nothing is dropped.
            throw;
        }
        catch (Exception ex)
        {
            await registry.FailProvisioningAsync(companyId, ErrorCode(ex), CancellationToken.None);
            throw;
        }
    }

    /// <summary>Checks both directions of the mapping: the database exists and carries this company's id.</summary>
    public async Task VerifyMappingAsync(CompanyRecord company, CancellationToken ct = default)
    {
        await using var conn = await OpenCompanyAsync(company.PhysicalDatabaseName, ct)
            ?? throw new CompanyConsistencyException("The company database does not exist.");
        var stored = await ReadIdentityAsync(conn, ct);
        if (stored != company.Id)
            throw new CompanyConsistencyException("The company database does not carry the identity of its registry entry.");
    }

    internal void EnsureSafeTarget(string name)
    {
        if (name == options.SystemDatabaseName || name == options.MaintenanceDatabase || ReservedNames.Contains(name))
            throw new CompanyConsistencyException("The registered database name collides with a protected database; refusing to touch it.");
    }

    private async Task Step(ProvisioningStep step, CancellationToken ct)
    {
        if (AfterStep is not null) await AfterStep(step, ct);
        ct.ThrowIfCancellationRequested();
    }

    internal static async Task<bool> TryLockAsync(NpgsqlConnection conn, Guid companyId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@ns, @key)", conn);
        cmd.Parameters.AddWithValue("ns", LockNamespace);
        cmd.Parameters.AddWithValue("key", BitConverter.ToInt32(companyId.ToByteArray(), 0));
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<bool> EnsureDatabaseAsync(string name, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString());
        await conn.OpenAsync(ct);

        if (await DatabaseExistsAsync(conn, name, ct))
        {
            await EnsureOwnedByCompanyAsync(name, ct);
            return false;
        }
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(name);
        try
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE {quoted}", conn);
            await create.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (SystemDatabaseInitializer.IsConcurrentCreation(ex))
        {
            // Created concurrently (e.g. by an unlocked foreign actor): treat as pre-existing, but verify.
            if (!await DatabaseExistsAsync(conn, name, ct)) throw;
            await EnsureOwnedByCompanyAsync(name, ct);
            return false;
        }
    }

    private static async Task<bool> DatabaseExistsAsync(NpgsqlConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn);
        cmd.Parameters.AddWithValue("n", name);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// A pre-existing database under this opaque name must be our own earlier (interrupted) work:
    /// empty, or containing only the HASM company schema. Anything else is foreign and is left untouched.
    /// The allowed set is the migration journal plus the tables created by this build's company scripts;
    /// a schema created by an earlier build is recognised through its identity table.
    /// </summary>
    private async Task EnsureOwnedByCompanyAsync(string name, CancellationToken ct)
    {
        await using var conn = await OpenCompanyAsync(name, ct)
            ?? throw new CompanyConsistencyException("The company database vanished during provisioning.");

        await using var identity = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn);
        identity.Parameters.AddWithValue("t", I.Table);
        if ((bool)(await identity.ExecuteScalarAsync(ct))!) return; // already a HASM company database

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE c.relkind IN ('r','p','v','m','S','f') AND n.nspname NOT IN ('pg_catalog','information_schema') " +
            "AND n.nspname NOT LIKE 'pg_toast%' AND c.relname <> @journal", conn);
        cmd.Parameters.AddWithValue("journal", SystemSchema.MigrationJournalTable);
        if ((long)(await cmd.ExecuteScalarAsync(ct))! > 0)
            throw new CompanyConsistencyException("A database with the registered name already exists and is not a HASM company database; refusing to modify it.");
    }

    private async Task BindIdentityAsync(CompanyRecord company, CancellationToken ct)
    {
        await using var conn = await OpenCompanyAsync(company.PhysicalDatabaseName, ct)
            ?? throw new CompanyConsistencyException("The company database vanished during provisioning.");
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {I.Table} ({I.Singleton}, {I.CompanyId}, {I.CreatedAtUtc}) VALUES (true, @id, @now) ON CONFLICT DO NOTHING", conn))
        {
            insert.Parameters.AddWithValue("id", company.Id);
            insert.Parameters.AddWithValue("now", DateTime.UtcNow);
            await insert.ExecuteNonQueryAsync(ct);
        }
        if (await ReadIdentityAsync(conn, ct) != company.Id)
            throw new CompanyConsistencyException("The company database already belongs to a different company.");
    }

    /// <summary>Company data is isolated: no role other than its owner (and superusers) may connect unless granted explicitly by the DBA.</summary>
    private async Task RestrictAccessAsync(string name, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString());
        await conn.OpenAsync(ct);
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(name);
        await using var cmd = new NpgsqlCommand($"REVOKE ALL ON DATABASE {quoted} FROM PUBLIC", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<NpgsqlConnection?> OpenCompanyAsync(string name, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(options.BuildCompanyAdminConnectionString(name));
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            await conn.DisposeAsync();
            return null;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static async Task<Guid?> ReadIdentityAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {I.CompanyId} FROM {I.Table}", conn);
        try { return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null; }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { return null; }
    }

    /// <summary>Short, non-sensitive failure classification stored centrally; never the exception message.</summary>
    internal static string ErrorCode(Exception ex) => ex switch
    {
        CompanyConsistencyException => "consistency",
        MigrationException => "migration",
        PostgresException pg => $"pg_{pg.SqlState}",
        NpgsqlException => "connection",
        _ => "error",
    };
}
