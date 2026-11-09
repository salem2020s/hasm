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

    public string Scrub(string? text) => SecretRedactor.Scrub(text, Password, AdminPassword);

    /// <summary>Safe to log: no credentials, no connection string.</summary>
    public override string ToString() =>
        $"HasmDatabaseOptions(Host={Host}, Port={Port}, SslMode={SslMode}, InsecureTransport={AllowInsecureTransport}, credentials=redacted)";
}
