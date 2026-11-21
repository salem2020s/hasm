using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
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
        services.AddSingleton(sp => new ExchangeRateService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ExchangeRateManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<ExchangeRateService>()));
        services.AddSingleton(sp => new CostCenterService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new CostCenterManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<CostCenterService>()));
        services.AddSingleton(sp => new ApprovalPolicyService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ApprovalPolicyManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<ApprovalPolicyService>()));
        services.AddSingleton(sp => new JournalService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("Hasm.Numbering")));
        services.AddSingleton(sp => new JournalManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<JournalService>()));
        services.AddSingleton(sp => new LedgerSettingsService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new LedgerSettingsManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<LedgerSettingsService>()));
        services.AddSingleton(sp => new LedgerReportService(sp.GetRequiredService<CompanyDatabaseResolver>()));
        services.AddSingleton(sp => new LedgerReportManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<LedgerReportService>(), sp.GetRequiredService<JournalService>(), sp.GetRequiredService<Hasm.Infrastructure.Reporting.ReportOptions>(), sp.GetRequiredService<FiscalCalendarService>()));
        services.AddSingleton(sp => Hasm.Infrastructure.Reporting.ReportOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<Hasm.Infrastructure.Reporting.IReportRenderer, Hasm.Infrastructure.Reporting.CsvReportRenderer>();
        services.AddSingleton<Hasm.Infrastructure.Reporting.IReportRenderer, Hasm.Infrastructure.Reporting.XlsxReportRenderer>();
        services.AddSingleton<Hasm.Infrastructure.Reporting.IReportRenderer, Hasm.Infrastructure.Reporting.PdfReportRenderer>();
        services.AddSingleton(sp => new Hasm.Infrastructure.Reporting.ReportExportService(sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<Hasm.Infrastructure.Reporting.ReportOptions>(), sp.GetServices<Hasm.Infrastructure.Reporting.IReportRenderer>()));
        services.AddSingleton(sp => new CostCenterPolicyService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new CostCenterPolicyManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<CostCenterPolicyService>()));
        services.AddSingleton(sp => new FiscalYearClosingService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<JournalService>(), sp.GetRequiredService<PeriodCloseGuard>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new FiscalYearClosingManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<FiscalYearClosingService>(), sp.GetRequiredService<JournalManagementService>()));
        return services;
    }
}
