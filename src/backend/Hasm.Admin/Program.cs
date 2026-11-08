using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Configuration;

// Explicit administrative tool. Never started by the API.
// Usage: dotnet run --project src/backend/Hasm.Admin -- init-system-db
// Configuration comes only from environment variables (Hasm__Database__*); see docs/DEVELOPMENT.md.

const int Ok = 0, Failed = 1, Usage = 2, BadConfig = 3;

if (args.Length != 1 || args[0] != "init-system-db")
{
    Console.Error.WriteLine("Usage: Hasm.Admin init-system-db");
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
    Console.WriteLine($"Initializing system database ({options}).");
    var result = await new SystemDatabaseInitializer(options).InitializeAsync();
    Console.WriteLine(result.DatabaseCreated ? "System database created." : "System database already exists (left unchanged).");
    Console.WriteLine(result.AppliedMigrations.Count == 0
        ? $"Schema up to date; {result.AlreadyApplied} migration(s) already applied."
        : $"Applied migration(s): {string.Join(", ", result.AppliedMigrations.Select(v => v.ToString("0000")))}.");
    return Ok;
}
catch (Exception ex)
{
    // Never print connection strings or secrets; scrub the message and omit stack traces.
    Console.Error.WriteLine($"Initialization failed: {ex.GetType().Name}: {options.Scrub(ex.Message)}");
    return Failed;
}
