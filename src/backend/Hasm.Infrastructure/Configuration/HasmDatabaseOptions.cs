using Hasm.Infrastructure.Naming;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Hasm.Infrastructure.Configuration;

public sealed class DatabaseConfigurationException(string message) : Exception(message);

/// <summary>
/// PostgreSQL settings, read from configuration section "Hasm:Database" (environment variables
/// such as Hasm__Database__Password). No defaults for credentials; nothing is hardcoded.
/// </summary>
public sealed class HasmDatabaseOptions
{
    public const string SectionName = "Hasm:Database";

    public string? Host { get; init; }
    public int Port { get; init; } = 5432;
    /// <summary>Physical name of the system database (the logical "Hasm_System"). Must be opaque.</summary>
    public string? SystemDatabaseName { get; init; }
    /// <summary>Runtime (least-privilege) account used by the Backend.</summary>
    public string? Username { get; init; }
    public string? Password { get; init; }
    /// <summary>Privileged account used only by the explicit administrative tool (create DB + migrate).</summary>
    public string? AdminUsername { get; init; }
    public string? AdminPassword { get; init; }
    /// <summary>
    /// Optional separate account that holds CREATEROLE (never superuser) and is used ONLY by <c>grant-company-runtime</c> to create the
    /// per-company runtime login role and set its password. Falls back to the admin account when unset (which then needs CREATEROLE).
    /// </summary>
    public string? RoleAdminUsername { get; init; }
    public string? RoleAdminPassword { get; init; }
    /// <summary>
    /// Directory holding one owner-only credential file per company runtime role (written by <c>grant-company-runtime</c>, read by the API).
    /// Credentials are never stored in application tables. May be a mounted secret store.
    /// </summary>
    public string? CompanyCredentialDirectory { get; init; }
    /// <summary>Upper bound of pooled connections per company (the runtime role's PostgreSQL CONNECTION LIMIT is derived from it).</summary>
    public int CompanyMaxPoolSize { get; init; } = DefaultCompanyMaxPoolSize;
    /// <summary>
    /// Explicit opt-in to run company databases with a runtime role that is over-privileged (owner/DDL/audit-modifying). Defaults to false:
    /// the API refuses such a database. Only for disposable single-account test environments.
    /// </summary>
    public bool AllowOverprivilegedCompanyRuntime { get; init; }
    /// <summary>
    /// Explicit opt-in to run with a SYSTEM database runtime account that is not least-privilege for the TOTP secret store (owner decision D-235): the administrative account itself, a superuser,
    /// an over-granted account, or a store readable by another role. Defaults to false: the API refuses to start and the MFA store refuses to work. When true the refusal becomes a critical log
    /// line at start-up and on every check. Only for disposable single-account test environments.
    /// </summary>
    public bool AllowOverprivilegedSystemRuntime { get; init; }

    public const int DefaultCompanyMaxPoolSize = 10;
    public const int MaxCompanyMaxPoolSize = 50;

    public string MaintenanceDatabase { get; init; } = "postgres";
    /// <summary>
    /// Defaults to VerifyFull. Npgsql's Require/Prefer encrypt but do NOT validate the server
    /// certificate (man-in-the-middle exposure), so only VerifyCA/VerifyFull are accepted unless
    /// <see cref="AllowInsecureTransport"/> is explicitly enabled (disposable test environments only).
    /// </summary>
    public SslMode SslMode { get; init; } = SslMode.VerifyFull;
    /// <summary>Optional PEM/PFX path of the CA that signed the PostgreSQL server certificate (private CA deployments).</summary>
    public string? SslRootCertificate { get; init; }
    /// <summary>Explicit opt-in to non-validating transport. Never enable outside a disposable test environment.</summary>
    public bool AllowInsecureTransport { get; init; }

    public static HasmDatabaseOptions FromConfiguration(IConfiguration configuration)
    {
        string? Get(string key) => Normalize(configuration[$"{SectionName}:{key}"]);

        var errors = new List<string>();
        int port = 5432;
        var portText = Get("Port");
        if (portText is not null && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
            errors.Add($"{SectionName}:Port must be an integer between 1 and 65535.");

        var sslMode = SslMode.VerifyFull;
        var sslText = Get("SslMode");
        if (sslText is not null && !Enum.TryParse(sslText, true, out sslMode))
            errors.Add($"{SectionName}:SslMode is not a valid value.");
        var insecureText = Get("AllowInsecureTransport");
        var allowInsecure = false;
        if (insecureText is not null && !bool.TryParse(insecureText, out allowInsecure))
            errors.Add($"{SectionName}:AllowInsecureTransport must be true or false.");
        var poolSize = DefaultCompanyMaxPoolSize;
        var poolText = Get("CompanyMaxPoolSize");
        if (poolText is not null && (!int.TryParse(poolText, out poolSize) || poolSize is < 1 or > MaxCompanyMaxPoolSize))
            errors.Add($"{SectionName}:CompanyMaxPoolSize must be an integer between 1 and {MaxCompanyMaxPoolSize}.");
        var overText = Get("AllowOverprivilegedCompanyRuntime");
        var allowOver = false;
        if (overText is not null && !bool.TryParse(overText, out allowOver))
            errors.Add($"{SectionName}:AllowOverprivilegedCompanyRuntime must be true or false.");
        var overSystemText = Get("AllowOverprivilegedSystemRuntime");
        var allowOverSystem = false;
        if (overSystemText is not null && !bool.TryParse(overSystemText, out allowOverSystem))
            errors.Add($"{SectionName}:AllowOverprivilegedSystemRuntime must be true or false.");
        if (errors.Count > 0)
            throw new DatabaseConfigurationException(Format(errors));

        return new HasmDatabaseOptions
        {
            Host = Get("Host"),
            Port = port,
            SystemDatabaseName = Get("SystemDatabaseName"),
            Username = Get("Username"),
            Password = Get("Password"),
            AdminUsername = Get("AdminUsername"),
            AdminPassword = Get("AdminPassword"),
            RoleAdminUsername = Get("RoleAdminUsername"),
            RoleAdminPassword = Get("RoleAdminPassword"),
            CompanyCredentialDirectory = Get("CompanyCredentialDirectory"),
            CompanyMaxPoolSize = poolSize,
            AllowOverprivilegedCompanyRuntime = allowOver,
            AllowOverprivilegedSystemRuntime = allowOverSystem,
            MaintenanceDatabase = Get("MaintenanceDatabase") ?? "postgres",
            SslMode = sslMode,
            SslRootCertificate = Get("SslRootCertificate"),
            AllowInsecureTransport = allowInsecure,
        };
    }

    private static string? Normalize(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    /// <summary>Returns problems naming only the setting, never its value.</summary>
    public IReadOnlyList<string> Validate(bool requireAdmin)
    {
        var errors = new List<string>();
        if (Host is null) errors.Add($"{SectionName}:Host is required.");
        if (Port is < 1 or > 65535) errors.Add($"{SectionName}:Port is out of range.");
        if (SystemDatabaseName is null) errors.Add($"{SectionName}:SystemDatabaseName is required.");
        else if (!PhysicalIdentifier.IsValid(SystemDatabaseName, out var e))
            errors.Add($"{SectionName}:SystemDatabaseName is invalid: {e}");
        if (CompanyMaxPoolSize is < 1 or > MaxCompanyMaxPoolSize) errors.Add($"{SectionName}:CompanyMaxPoolSize is out of range.");
        if (!PhysicalIdentifier.IsValid(MaintenanceDatabase))
            errors.Add($"{SectionName}:MaintenanceDatabase is invalid.");

        if (SslMode is not (SslMode.VerifyCA or SslMode.VerifyFull) && !AllowInsecureTransport)
            errors.Add($"{SectionName}:SslMode must be VerifyCA or VerifyFull (certificate validation) unless {SectionName}:AllowInsecureTransport is true.");

        if (requireAdmin)
        {
            if (AdminUsername is null) errors.Add($"{SectionName}:AdminUsername is required.");
            if (AdminPassword is null) errors.Add($"{SectionName}:AdminPassword is required.");
        }
        else
        {
            if (Username is null) errors.Add($"{SectionName}:Username is required.");
            if (Password is null) errors.Add($"{SectionName}:Password is required.");
        }
        return errors;
    }

    public void EnsureValid(bool requireAdmin)
    {
        var errors = Validate(requireAdmin);
        if (errors.Count > 0) throw new DatabaseConfigurationException(Format(errors));
    }

    private static string Format(IEnumerable<string> errors) =>
        "Invalid database configuration: " + string.Join(" ", errors);

    /// <summary>Connection string for the system database. Treat the result as a secret.</summary>
    public string BuildSystemConnectionString(bool admin = false) => Build(SystemDatabaseName!, admin);

    /// <summary>Connection string for the maintenance database (admin only). Treat as a secret.</summary>
    public string BuildMaintenanceConnectionString() => Build(MaintenanceDatabase, admin: true);

    /// <summary>
    /// Connection string for one company database using the privileged account (provisioning only).
    /// The name must be a valid opaque identifier and never the system or maintenance database. Treat the result as a secret.
    /// </summary>
    public string BuildCompanyAdminConnectionString(string physicalDatabaseName)
    {
        PhysicalIdentifier.Validate(physicalDatabaseName, nameof(physicalDatabaseName));
        if (physicalDatabaseName == SystemDatabaseName || physicalDatabaseName == MaintenanceDatabase)
            throw new ArgumentException("Not a company database.", nameof(physicalDatabaseName));
        return Build(physicalDatabaseName, admin: true);
    }

    /// <summary>
    /// Connection string for one company database with that company's OWN runtime role (never the shared runtime or the admin account).
    /// Pooling is bounded per company. The name must be a valid opaque identifier and never the system or maintenance database.
    /// Treat the result as a secret.
    /// </summary>
    public string BuildCompanyRuntimeConnectionString(string physicalDatabaseName, string roleName, string rolePassword)
    {
        PhysicalIdentifier.Validate(physicalDatabaseName, nameof(physicalDatabaseName));
        PhysicalIdentifier.Validate(roleName, nameof(roleName));
        if (physicalDatabaseName == SystemDatabaseName || physicalDatabaseName == MaintenanceDatabase)
            throw new ArgumentException("Not a company database.", nameof(physicalDatabaseName));
        if (string.IsNullOrEmpty(rolePassword)) throw new ArgumentException("A role password is required.", nameof(rolePassword));
        var errors = Validate(requireAdmin: false).Where(e => !e.Contains(":Username") && !e.Contains(":Password")).ToList();
        if (errors.Count > 0) throw new DatabaseConfigurationException(Format(errors));
        return new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = physicalDatabaseName,
            Username = roleName,
            Password = rolePassword,
            SslMode = SslMode,
            RootCertificate = SslRootCertificate,
            ApplicationName = "hasm-company",
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = CompanyMaxPoolSize,
            ConnectionIdleLifetime = 60,
            Timeout = 5,
            CommandTimeout = 30,
        }.ConnectionString;
    }

    /// <summary>
    /// Connection string (maintenance database) of the account that creates company runtime roles: the optional role-admin account,
    /// otherwise the admin account. Treat as a secret.
    /// </summary>
    public string BuildRoleAdminConnectionString()
    {
        EnsureValid(requireAdmin: true);
        var useRoleAdmin = RoleAdminUsername is not null && RoleAdminPassword is not null;
        if ((RoleAdminUsername is null) != (RoleAdminPassword is null))
            throw new DatabaseConfigurationException($"Invalid database configuration: {SectionName}:RoleAdminUsername and {SectionName}:RoleAdminPassword must be set together.");
        return new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = MaintenanceDatabase,
            Username = useRoleAdmin ? RoleAdminUsername : AdminUsername,
            Password = useRoleAdmin ? RoleAdminPassword : AdminPassword,
            SslMode = SslMode,
            RootCertificate = SslRootCertificate,
            ApplicationName = "hasm",
            Pooling = false,
        }.ConnectionString;
    }

    private string Build(string database, bool admin)
    {
        EnsureValid(admin);
        return new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = database,
            Username = admin ? AdminUsername : Username,
            Password = admin ? AdminPassword : Password,
            SslMode = SslMode,
            RootCertificate = SslRootCertificate,
            ApplicationName = "hasm",
            Pooling = false,
        }.ConnectionString;
    }

    public string Scrub(string? text) => SecretRedactor.Scrub(text, Password, AdminPassword, RoleAdminPassword);

    /// <summary>Safe to log: no credentials, no connection string.</summary>
    public override string ToString() =>
        $"HasmDatabaseOptions(Host={Host}, Port={Port}, SslMode={SslMode}, InsecureTransport={AllowInsecureTransport}, credentials=redacted)";
}
