using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Api.Company;

/// <summary>Registers the General Ledger foundation services (Phase 3.2B). Grows slice by slice.</summary>
public static class LedgerSetup
{
    public static IServiceCollection AddHasmLedger(this IServiceCollection services)
    {
        services.AddSingleton(sp => new NumberingService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new NumberingManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<NumberingService>()));
        return services;
    }
}
