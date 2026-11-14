using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Security;

namespace Hasm.Api.Company;

/// <summary>Registers the generic import framework (Phase 3.2D) and the file-encryption service it uses. Adapters register themselves as <see cref="IImportAdapter"/>.</summary>
public static class ImportSetup
{
    public static IServiceCollection AddHasmImports(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(EncryptionOptions.FromConfiguration(configuration));
        services.AddSingleton<KekRing>(sp => new FileKekRing(sp.GetRequiredService<EncryptionOptions>()));
        services.AddSingleton(sp => new EnvelopeEncryptor(sp.GetRequiredService<KekRing>()));
        services.AddSingleton(ImportOptions.FromConfiguration(configuration));
        services.AddSingleton(sp => new EncryptedFileStore(sp.GetRequiredService<ImportOptions>(), sp.GetRequiredService<EncryptionOptions>()));
        services.AddSingleton<IImportAdapter>(sp => new ChartOfAccountsImportAdapter(sp.GetRequiredService<ChartOfAccountsService>()));
        services.AddSingleton(sp => new ImportAdapterRegistry(sp.GetServices<IImportAdapter>()));
        services.AddSingleton(sp => new ImportService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<EnvelopeEncryptor>(),
            sp.GetRequiredService<EncryptedFileStore>(), sp.GetRequiredService<ImportOptions>(), sp.GetRequiredService<ImportAdapterRegistry>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ImportManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<ImportService>(), sp.GetRequiredService<ImportAdapterRegistry>()));
        services.AddHostedService(sp => new ImportMaintenanceHostedService(configuration, sp, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Hasm.Imports")));
        return services;
    }
}

/// <summary>
/// Hourly retention pass over every Ready company: expires pending batches, purges finished batches whose retention is over (never an applying or failed one). Each step is audited by the service. A
/// company that is unavailable (no credential, database down, no encryption configured) is skipped and logged by error type only; nothing here can lose data.
/// </summary>
public sealed class ImportMaintenanceHostedService(IConfiguration configuration, IServiceProvider services, ILogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var imports = services.GetRequiredService<ImportService>();     // resolved lazily: an unconfigured deployment still starts and anonymous endpoints work
                    var options = HasmDatabaseOptions.FromConfiguration(configuration);
                    options.EnsureValid(requireAdmin: false);
                    var registry = new CompanyRegistry(options.BuildSystemConnectionString(admin: false));
                    foreach (var company in await registry.ListAsync(stoppingToken))
                    {
                        if (company.State != ProvisioningState.Ready) continue;
                        try { await imports.MaintainAsync(null, company.Id, stoppingToken); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Import maintenance skipped a company ({ErrorType}).", ex.GetType().Name); }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Import maintenance failed ({ErrorType}).", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
