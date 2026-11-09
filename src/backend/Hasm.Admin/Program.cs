using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Configuration;

// Explicit administrative tool. Never started by the API; uses the privileged database account.
// Usage:
//   Hasm.Admin init-system-db
//   Hasm.Admin register-company              (registers a Pending company; prints its id only)
//   Hasm.Admin provision-company <company-id> (creates + migrates its separate database; safe to repeat/resume)
// Configuration comes only from environment variables (Hasm__Database__*); see docs/DEVELOPMENT.md.
// Physical database names are never printed.

const int Ok = 0, Failed = 1, Usage = 2, BadConfig = 3, InProgress = 4;

var command = args.Length > 0 ? args[0] : null;
Guid companyId = Guid.Empty;
var validUsage = command switch
{
    "init-system-db" or "register-company" => args.Length == 1,
    "provision-company" => args.Length == 2 && Guid.TryParse(args[1], out companyId) && companyId != Guid.Empty,
    _ => false,
};
if (!validUsage)
{
    Console.Error.WriteLine("Usage: Hasm.Admin init-system-db | register-company | provision-company <company-id>");
    return Usage;
}

HasmDatabaseOptions options;
try
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    options = HasmDatabaseOptions.FromConfiguration(configuration);
    options.EnsureValid(requireAdmin: true);
}
catch (DatabaseConfigurationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return BadConfig;
}

try
{
    switch (command)
    {
        case "init-system-db":
        {
            Console.WriteLine($"Initializing system database ({options}).");
            var result = await new SystemDatabaseInitializer(options).InitializeAsync();
            Console.WriteLine(result.DatabaseCreated ? "System database created." : "System database already exists (left unchanged).");
            Console.WriteLine(result.AppliedMigrations.Count == 0
                ? $"Schema up to date; {result.AlreadyApplied} migration(s) already applied."
                : $"Applied migration(s): {string.Join(", ", result.AppliedMigrations.Select(v => v.ToString("0000")))}.");
            return Ok;
        }
        case "register-company":
        {
            var company = await new CompanyRegistry(options.BuildSystemConnectionString(admin: true)).RegisterPendingAsync();
            Console.WriteLine($"Registered company {company.Id} (Pending). Run: provision-company {company.Id}");
            return Ok;
        }
        default: // provision-company
        {
            Console.WriteLine($"Provisioning company {companyId} ({options}).");
            var registry = new CompanyRegistry(options.BuildSystemConnectionString(admin: true));
            var result = await new CompanyProvisioner(options, registry).ProvisionAsync(companyId);
            switch (result.Outcome)
            {
                case ProvisioningOutcome.InProgressElsewhere:
                    Console.Error.WriteLine("Another process is provisioning this company; nothing was done.");
                    return InProgress;
                case ProvisioningOutcome.AlreadyReady:
                    Console.WriteLine("Company is already Ready; mapping verified, nothing changed.");
                    return Ok;
                default:
                    Console.WriteLine(result.DatabaseCreated ? "Company database created." : "Company database already existed (resumed).");
                    Console.WriteLine($"Company is Ready (attempt {result.Attempt}); applied migration(s): {(result.AppliedMigrations.Count == 0 ? "none" : string.Join(", ", result.AppliedMigrations.Select(v => v.ToString("0000"))))}.");
                    return Ok;
            }
        }
    }
}
catch (Exception ex)
{
    // Never print connection strings or secrets; scrub the message and omit stack traces.
    // For provisioning failures the company is marked Failed centrally; its database is never dropped.
    Console.Error.WriteLine($"{command} failed: {ex.GetType().Name}: {options.Scrub(ex.Message)}");
    return Failed;
}
