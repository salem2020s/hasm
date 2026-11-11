using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;

namespace Hasm.Api.Company;

public static class CompanyRuntimeSetup
{
    /// <summary>
    /// Registers company database access (resolver, per-company runtime credentials, audited business transactions). Everything is built
    /// lazily on first use, so the API starts and anonymous endpoints work without a database; a missing or unusable configuration then
    /// fails closed with a safe 503. This registers NO endpoint: business endpoints arrive with later phases, behind authentication.
    /// </summary>
    public static IServiceCollection AddHasmCompanyRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(sp =>
        {
            var options = HasmDatabaseOptions.FromConfiguration(configuration);
            options.EnsureValid(requireAdmin: false);
            if (options.CompanyCredentialDirectory is null)
                throw new DatabaseConfigurationException($"Invalid database configuration: {HasmDatabaseOptions.SectionName}:CompanyCredentialDirectory is required.");
            return new CompanyDatabaseResolver(
                options,
                new CompanyRegistry(options.BuildSystemConnectionString(admin: false)),
                new FileCompanyCredentialStore(options.CompanyCredentialDirectory),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Hasm.Company"));
        });
        services.AddSingleton(sp => new CompanyAuditedTransactions(
            sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<AuditTrail>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
