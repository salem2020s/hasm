using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;

namespace Hasm.Infrastructure.CompanyDatabase;

public enum CompanyMigrationStatus
{
    /// <summary>This call applied one or more migrations.</summary>
    Migrated,
    /// <summary>The schema was already current; nothing changed.</summary>
    UpToDate,
    /// <summary>The company is not Ready (use provision-company); its database was not touched.</summary>
    NotReady,
    /// <summary>Another process is provisioning, migrating or granting this company; nothing was done.</summary>
    Busy,
    /// <summary>The migration failed; <see cref="CompanyMigrationOutcome.ErrorCode"/> classifies it. The database is left as it was (each migration is transactional) and is never dropped.</summary>
    Failed,
}

/// <param name="Detail">For a migration refusal only (a <see cref="MigrationException"/>): the message the runner wrote (it names switched-off triggers as table.trigger, never a database name or a secret).</param>
public sealed record CompanyMigrationOutcome(Guid CompanyId, CompanyMigrationStatus Status, IReadOnlyList<int> Applied, string? ErrorCode, string? Detail = null)
{
    /// <summary>The one line the administrative tool prints per company (never a database name).</summary>
    public string Line() => $"Company {CompanyId}: {Status}" + (Applied.Count > 0 ? $" (applied {string.Join(", ", Applied.Select(v => v.ToString("0000")))})" : "") + (ErrorCode is null ? "" : $" [{ErrorCode}]");
}

/// <summary>
/// Explicit administrative upgrade of company databases (<c>Hasm.Admin migrate-companies</c>). Uses the privileged account only; the
/// API runtime never migrates and never holds DDL rights. For each selected company, in registry order: the per-company session advisory lock
/// (the same one provisioning takes) guarantees one actor at a time; the database must carry the company's identity (a foreign database
/// is refused, never modified); forward-only checksummed migrations are applied each in its own transaction under the journal's
/// own advisory lock. A failure of one company is recorded and the others still run; the caller gets one outcome per company, so partial
/// failure is reported exactly. Nothing is ever dropped. Idempotent: a second run reports every company UpToDate.
/// </summary>
public sealed class CompanyMigrator(HasmDatabaseOptions options, CompanyRegistry registry, IReadOnlyList<MigrationScript>? companyScripts = null)
{
    private readonly IReadOnlyList<MigrationScript> scripts = companyScripts ?? MigrationScript.LoadEmbeddedCompany();

    /// <summary>Test seam: invoked for each company after its lock is taken and before the migration (e.g. to hold the lock or fail).</summary>
    internal Func<Guid, CancellationToken, Task>? BeforeMigrate { get; init; }

    /// <summary>TEST SEAM (can only add a refusal, see <see cref="MigrationAccountPolicy"/>): an additional "is a superuser" reading.</summary>
    internal Func<bool>? AdditionalSuperuserReading { get; init; }

    public async Task<IReadOnlyList<CompanyMigrationOutcome>> MigrateAsync(Guid? onlyCompany = null, CancellationToken ct = default)
    {
        options.EnsureValid(requireAdmin: true);
        MigrationScript.Validate(scripts);

        IReadOnlyList<CompanyRecord> companies = onlyCompany is { } id
            ? [await registry.GetAsync(id, ct) ?? throw new CompanyNotFoundException(id)]
            : await registry.ListAsync(ct);

        var outcomes = new List<CompanyMigrationOutcome>();
        foreach (var company in companies)
        {
            try { outcomes.Add(await MigrateOneAsync(company, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new CompanyMigrationOutcome(company.Id, CompanyMigrationStatus.Failed, [], CompanyProvisioner.ErrorCode(ex), ex is MigrationException ? ex.Message : null));
            }
        }
        return outcomes;
    }

    private async Task<CompanyMigrationOutcome> MigrateOneAsync(CompanyRecord listed, CancellationToken ct)
    {
        if (listed.State != ProvisioningState.Ready)
            return new CompanyMigrationOutcome(listed.Id, CompanyMigrationStatus.NotReady, [], null);

        await using var lockConn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await lockConn.OpenAsync(ct);
        if (!await CompanyProvisioner.AcquireLockAsync(lockConn, listed.Id, ct))
            return new CompanyMigrationOutcome(listed.Id, CompanyMigrationStatus.Busy, [], null);
        if (BeforeMigrate is not null) await BeforeMigrate(listed.Id, ct);

        // Re-read under the lock: the state may have changed since the listing.
        var company = await registry.GetAsync(listed.Id, ct) ?? throw new CompanyNotFoundException(listed.Id);
        if (company.State != ProvisioningState.Ready)
            return new CompanyMigrationOutcome(company.Id, CompanyMigrationStatus.NotReady, [], null);

        var provisioner = new CompanyProvisioner(options, registry, scripts);
        provisioner.EnsureSafeTarget(company.PhysicalDatabaseName);
        await provisioner.VerifyMappingAsync(company, ct);

        var result = await new MigrationRunner(options.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName)) { AllowSuperuser = options.AllowSuperuserMigrationAccount, AdditionalSuperuserReading = AdditionalSuperuserReading }.ApplyAsync(scripts, ct);
        return new CompanyMigrationOutcome(company.Id, result.Applied.Count == 0 ? CompanyMigrationStatus.UpToDate : CompanyMigrationStatus.Migrated, result.Applied, null);
    }
}

public enum RuntimeGrantOutcome { Applied, InProgressElsewhere }

/// <param name="RoleCreated">The login role did not exist and was created by this call.</param>
/// <param name="CredentialWritten">A new password was generated and its credential file installed (creation or rotation).</param>
/// <param name="CredentialPresent">A credential file exists after the call. False means the role pre-existed and the credential must be installed or rotated.</param>
/// <param name="LoginVerified">The API's own checks (login, schema gate, least privilege) passed with the installed credential.</param>
public sealed record RuntimeGrantResult(Guid CompanyId, RuntimeGrantOutcome Outcome, bool RoleCreated, bool CredentialWritten, bool CredentialPresent, bool LoginVerified);

/// <summary>A failure of the runtime provisioning with a short safe code (never a database name, role name or secret).</summary>
public sealed class CompanyRuntimeProvisioningException(string code, string message) : Exception(message)
{
    public const string NotReady = "company_not_ready";
    public const string SchemaUnmigrated = "schema_unmigrated";
    public const string RolePrivilegeRequired = "role_privilege_required";
    public const string RoleUnsafe = "role_unsafe";
    public const string PrivilegeVerificationFailed = "privilege_verification_failed";
    public const string LoginVerificationFailed = "runtime_login_failed";
    public const string CredentialInstallFailed = "credential_install_failed";

    public string Code { get; } = code;
}

/// <summary>
/// Explicit administrative provisioning of ONE company's runtime access (<c>Hasm.Admin grant-company-runtime</c>). Idempotent and scoped to the
/// selected company: it never touches another company's role, database or grants, never grants ownership, DDL, audit modification or
/// any attribute (no superuser, createdb, createrole, replication, bypassrls, memberships), never drops anything, and never modifies
/// PostgreSQL global settings.
///
/// Privilege separation: the role is created by the role-administration connection (CREATEROLE, see <see cref="HasmDatabaseOptions.RoleAdminUsername"/>;
/// falls back to the admin account), the grants are issued by the database/object owner (the admin account that provisioned the database).
/// The password is generated here (256-bit CSPRNG), handed to PostgreSQL only as a SCRAM-SHA-256 verifier (so it never appears in
/// the server log) and written once to an owner-only credential file; it is never printed, logged or stored in a table. After granting,
/// the catalog is re-read to prove the role holds exactly the intended privileges, then the API's own login and schema gate are exercised.
/// </summary>
public sealed class CompanyRuntimeProvisioner(HasmDatabaseOptions options, CompanyRegistry registry, IReadOnlyList<MigrationScript>? companyScripts = null)
{
    private readonly IReadOnlyList<MigrationScript> scripts = companyScripts ?? MigrationScript.LoadEmbeddedCompany();

    private sealed record RoleInfo(bool CanLogin, bool Unsafe, int ConnectionLimit);

    /// <summary>Test seam: runs just before the credential file is installed (throwing CompanyCredentialException simulates an unwritable credential store).</summary>
    internal Action? BeforeCredentialInstall { get; init; }

    public async Task<RuntimeGrantResult> GrantAsync(Guid companyId, bool rotateCredential = false, CancellationToken ct = default)
    {
        options.EnsureValid(requireAdmin: true);
        if (options.CompanyCredentialDirectory is null)
            throw new DatabaseConfigurationException($"Invalid database configuration: {HasmDatabaseOptions.SectionName}:CompanyCredentialDirectory is required.");
        MigrationScript.Validate(scripts);
        var store = new FileCompanyCredentialStore(options.CompanyCredentialDirectory);

        await using var lockConn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await lockConn.OpenAsync(ct);
        if (!await CompanyProvisioner.AcquireLockAsync(lockConn, companyId, ct))
            return new RuntimeGrantResult(companyId, RuntimeGrantOutcome.InProgressElsewhere, false, false, store.Exists(companyId), false);

        var company = await registry.GetAsync(companyId, ct) ?? throw new CompanyNotFoundException(companyId);
        var provisioner = new CompanyProvisioner(options, registry, scripts);
        provisioner.EnsureSafeTarget(company.PhysicalDatabaseName);
        if (company.State != ProvisioningState.Ready)
            throw new CompanyRuntimeProvisioningException(CompanyRuntimeProvisioningException.NotReady, "The company is not Ready.");
        await provisioner.VerifyMappingAsync(company, ct);
        await EnsureMigratedAsync(company, ct);

        var role = CompanyRuntimeRole.NameFor(company.PhysicalDatabaseName);
        var roleCreated = false;
        var credentialWritten = false;
        string? staged = null;
        CompanyCredential? previous = null;
        try
        {
            await using (var roleConn = new NpgsqlConnection(options.BuildRoleAdminConnectionString()))
            {
                await roleConn.OpenAsync(ct);
                var info = await ReadRoleAsync(roleConn, role, ct);
                if (info is { Unsafe: true })
                    throw new CompanyRuntimeProvisioningException(CompanyRuntimeProvisioningException.RoleUnsafe,
                        "A role with the expected name exists but has attributes or memberships a runtime role must not have; refusing to adopt it.");

                var newPassword = info is null || rotateCredential;
                // Remembered so that a failed installation can put the previous (still installed) password back.
                if (info is not null && rotateCredential)
                {
                    try { previous = await store.GetAsync(companyId, ct); } catch (CompanyCredentialException) { previous = null; }
                }
                string? verifier = null;
                if (newPassword)
                {
                    var password = CompanyRuntimeRole.GeneratePassword();
                    staged = store.Stage(companyId, new CompanyCredential(role, password));
                    verifier = CompanyRuntimeRole.ScramVerifier(password);
                }
                var limit = options.CompanyMaxPoolSize + 2;
                if (info is null)
                {
                    await CreateRoleAsync(roleConn, role, verifier!, limit, ct);
                    roleCreated = true;
                }
                else
                {
                    var alter = new List<string>();
                    if (!info.CanLogin) alter.Add("LOGIN");
                    if (info.ConnectionLimit != limit) alter.Add($"CONNECTION LIMIT {limit}");
                    if (verifier is not null) alter.Add($"PASSWORD '{verifier}'");
                    if (alter.Count > 0) await ExecuteRoleDdlAsync(roleConn, $"ALTER ROLE {Q(role)} {string.Join(' ', alter)}", ct);
                }
            }

            // The role now has the staged password: install the credential before anything else can fail, so it is never lost.
            if (staged is not null)
            {
                try
                {
                    BeforeCredentialInstall?.Invoke();
                    store.Commit(companyId, staged);
                }
                catch (CompanyCredentialException)
                {
                    // PostgreSQL already accepted the new password but the file could not be replaced: never leave that silently unusable.
                    // Put the previous password back when it is known (the installed credential then stays valid); otherwise say exactly how to recover.
                    var restored = await TryRestorePreviousPasswordAsync(role, previous, ct);
                    throw new CompanyRuntimeProvisioningException(CompanyRuntimeProvisioningException.CredentialInstallFailed, restored
                        ? "The new credential could not be installed; the previous password was restored and the installed credential remains valid. Fix the credential directory and run again."
                        : "The role password was changed but the new credential could not be installed and the previous password could not be restored: the API cannot log in for this company. Fix the credential directory and run grant-company-runtime with --rotate-credential.");
                }
                staged = null;
                credentialWritten = true;
            }

            await ApplyGrantsAsync(company.PhysicalDatabaseName, role, ct);
        }
        finally
        {
            if (staged is not null) FileCompanyCredentialStore.Discard(staged);
        }

        var present = store.Exists(companyId);
        var verified = present && await VerifyLoginAsync(store, company, role, ct);
        return new RuntimeGrantResult(companyId, RuntimeGrantOutcome.Applied, roleCreated, credentialWritten, present, verified);
    }

    private static string Q(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    private async Task<bool> TryRestorePreviousPasswordAsync(string role, CompanyCredential? previous, CancellationToken ct)
    {
        if (previous is null || previous.Username != role) return false;
        try
        {
            await using var conn = new NpgsqlConnection(options.BuildRoleAdminConnectionString());
            await conn.OpenAsync(CancellationToken.None);
            await ExecuteRoleDdlAsync(conn, $"ALTER ROLE {Q(role)} PASSWORD '{CompanyRuntimeRole.ScramVerifier(previous.Password)}'", CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or CompanyRuntimeProvisioningException) { return false; }
    }

    private async Task EnsureMigratedAsync(CompanyRecord company, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(options.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName));
        await conn.OpenAsync(ct);
        var applied = new HashSet<int>();
        try
        {
            await using var cmd = new NpgsqlCommand($"SELECT version FROM {SystemSchema.MigrationJournalTable}", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { /* nothing applied */ }
        if (scripts.Any(s => !applied.Contains(s.Version)))
            throw new CompanyRuntimeProvisioningException(CompanyRuntimeProvisioningException.SchemaUnmigrated,
                "The company database is not fully migrated; run migrate-companies first.");
    }

    private static async Task<RoleInfo?> ReadRoleAsync(NpgsqlConnection conn, string role, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT rolcanlogin, (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls " +
            "OR EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = r.oid)), rolconnlimit FROM pg_roles r WHERE rolname = @r", conn);
        cmd.Parameters.AddWithValue("r", role);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new RoleInfo(rd.GetBoolean(0), rd.GetBoolean(1), rd.GetInt32(2)) : null;
    }

    private static Task CreateRoleAsync(NpgsqlConnection conn, string role, string verifier, int limit, CancellationToken ct) =>
        ExecuteRoleDdlAsync(conn,
            $"CREATE ROLE {Q(role)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT NOBYPASSRLS CONNECTION LIMIT {limit} PASSWORD '{verifier}'", ct);

    private static async Task ExecuteRoleDdlAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        // The verifier is base64 plus '$', ':' and '=' only; a quote can never occur. Checked, not assumed.
        if (sql.Contains("PASSWORD '", StringComparison.Ordinal) && sql.Count(c => c == '\'') != 2)
            throw new InvalidOperationException("Unexpected character in password verifier.");
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new CompanyRuntimeProvisioningException(CompanyRuntimeProvisioningException.RolePrivilegeRequired,
                "Creating or changing the runtime login role needs CREATEROLE: configure Hasm:Database:RoleAdminUsername/RoleAdminPassword (a CREATEROLE account that is not a superuser) or have the DBA pre-create the role.");
        }
    }

    private async Task ApplyGrantsAsync(string database, string role, CancellationToken ct)
    {
        await using (var maintenance = new NpgsqlConnection(options.BuildMaintenanceConnectionString()))
        {
            await maintenance.OpenAsync(ct);
            await using var tx = await maintenance.BeginTransactionAsync(ct);
            foreach (var sql in CompanyRuntimePrivileges.DatabaseStatements(database, role))
                await using (var cmd = new NpgsqlCommand(sql, maintenance, tx)) await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }

        await using var conn = new NpgsqlConnection(options.BuildCompanyAdminConnectionString(database));
        await conn.OpenAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            foreach (var sql in CompanyRuntimePrivileges.SchemaStatements(role))
                await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }

        var violations = await CompanyRuntimePrivileges.FindViolationsAsync(conn, role, ct);
        if (violations.Count > 0)
            throw new CompanyRuntimeProvisioningException(CompanyRuntimeProvisioningException.PrivilegeVerificationFailed,
                $"The runtime role holds more than the allowed privileges ({string.Join(", ", violations)}); it must not be used.");
        if (!await CompanyRuntimePrivileges.HasRequiredAccessAsync(conn, role, ct))
            throw new CompanyRuntimeProvisioningException(CompanyRuntimeProvisioningException.PrivilegeVerificationFailed,
                "The runtime role does not hold the privileges it needs after granting.");
    }

    /// <summary>Exercises exactly what the API does with the installed credential: login, schema gate, strict least-privilege check.</summary>
    private async Task<bool> VerifyLoginAsync(FileCompanyCredentialStore store, CompanyRecord company, string role, CancellationToken ct)
    {
        var credential = await store.GetAsync(company.Id, ct);
        if (credential is null || credential.Username != role) return false;
        var connectionString = options.BuildCompanyRuntimeConnectionString(company.PhysicalDatabaseName, role, credential.Password);
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await CompanySchemaGate.EnsureCompatibleAsync(conn, company.Id, scripts, allowOverprivileged: false, ct);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or CompanyDatabaseException)
        {
            throw new CompanyRuntimeProvisioningException(CompanyRuntimeProvisioningException.LoginVerificationFailed,
                "The runtime role was provisioned but the API's login and schema checks failed with it (check pg_hba.conf, TLS settings and the credential).");
        }
        finally { NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString)); }
    }
}
