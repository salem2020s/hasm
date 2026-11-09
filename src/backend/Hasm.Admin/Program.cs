using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Configuration;

// Explicit administrative tool. Never started by the API; uses the privileged database account.
// Usage:
//   Hasm.Admin init-system-db
//   Hasm.Admin register-company              (registers a Pending company; prints its id only)
//   Hasm.Admin bootstrap-admin <login> [display name]  (creates the FIRST identity administrator; refuses if one exists)
//   Hasm.Admin provision-company <company-id> (creates + migrates its separate database; safe to repeat/resume)
// Configuration comes only from environment variables (Hasm__Database__*); see docs/DEVELOPMENT.md.
// Physical database names are never printed.

const int Ok = 0, Failed = 1, Usage = 2, BadConfig = 3, InProgress = 4, AdminExists = 5;

var command = args.Length > 0 ? args[0] : null;
Guid companyId = Guid.Empty;
var validUsage = command switch
{
    "init-system-db" or "register-company" => args.Length == 1,
    "bootstrap-admin" => args.Length is 2 or 3,
    "provision-company" => args.Length == 2 && Guid.TryParse(args[1], out companyId) && companyId != Guid.Empty,
    _ => false,
};
if (!validUsage)
{
    Console.Error.WriteLine("Usage: Hasm.Admin init-system-db | register-company | bootstrap-admin <login> [display name] | provision-company <company-id>");
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
        case "bootstrap-admin":
        {
            // The password is never accepted on the command line (process listings, shell history).
            var password = Environment.GetEnvironmentVariable("HASM_BOOTSTRAP_ADMIN_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("Set HASM_BOOTSTRAP_ADMIN_PASSWORD or run interactively to be prompted.");
                    return Usage;
                }
                password = ReadHidden("Password: ");
                if (password != ReadHidden("Repeat password: "))
                {
                    Console.Error.WriteLine("Passwords do not match.");
                    return Usage;
                }
            }

            AuthOptions authOptions;
            try { authOptions = AuthOptions.FromConfiguration(new ConfigurationBuilder().AddEnvironmentVariables().Build()); }
            catch (AuthConfigurationException ex) { Console.Error.WriteLine(ex.Message); return BadConfig; }

            // Administrative tool: the privileged account is acceptable here (never in the API runtime).
            await using var store = new IdentityStore(options.BuildSystemConnectionString(admin: true), TimeProvider.System);
            var auth = new AuthService(store, new PasswordService(authOptions.Pbkdf2Iterations), authOptions);
            var result = await auth.BootstrapAdministratorAsync(args[1], args.Length == 3 ? args[2] : args[1], password);
            if (result is null)
            {
                Console.Error.WriteLine("An identity administrator already exists; nothing was changed.");
                return AdminExists;
            }
            switch (result.Status)
            {
                case CreateUserStatus.Created:
                    Console.WriteLine($"Initial identity administrator created (id {result.User!.Id}).");
                    return Ok;
                case CreateUserStatus.WeakPassword:
                    Console.Error.WriteLine($"Password rejected: {string.Join(", ", result.PasswordProblems!)} (minimum {PasswordPolicy.MinLength} characters).");
                    return Usage;
                default:
                    Console.Error.WriteLine($"Not created: {result.Status}.");
                    return Usage;
            }
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

static string ReadHidden(string prompt)
{
    Console.Error.Write(prompt);
    var sb = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
        else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
    Console.Error.WriteLine();
    return sb.ToString();
}
