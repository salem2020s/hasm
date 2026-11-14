using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.2B S6: report permissions and export safety (no database needed).</summary>
public class LedgerReportUnitTests
{
    [Fact]
    public void Report_viewing_and_exporting_are_branch_scoped_separate_permissions_held_by_the_administrator_preset()
    {
        Assert.Equal(ScopeKind.Branch, PermissionCatalog.Default.Resources.Single(r => r.Key == "company.ledgerreports").Scope);
        var admin = PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions;
        Assert.Contains(PermissionCatalog.Codes.LedgerReportsView, admin);
        Assert.Contains(PermissionCatalog.Codes.LedgerReportsExport, admin);
        Assert.DoesNotContain(PresetRoles.Definitions.Where(d => d.Key != PresetRoles.CompanyAdministratorId).SelectMany(d => d.Value.Permissions), p => p.StartsWith("company.ledgerreports"));
    }

    [Fact]
    public void The_trial_balance_csv_is_utf8_with_a_bom_exact_decimals_and_neutralised_formulas()
    {
        var rows = new[]
        {
            new TrialBalanceRow(Guid.NewGuid(), "1101", "نقد", "=cmd|' /C calc'!A0", null, 0, false, 1, 1, 0m, 1234.5m, 0m, 1234.5m),
            new TrialBalanceRow(Guid.NewGuid(), "2", "خصوم", "Liab, \"quoted\"", null, 0, false, 2, 2, 0m, 0m, 1234.5m, -1234.5m),
        };
        var bytes = LedgerReportService.TrialBalanceCsv(new TrialBalanceView(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "JOD", rows, 0m, 1234.5m, 1234.5m, 0m, true));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("1101,نقد,\"'=cmd|' /C calc'!A0\",0,no,0.0000,1234.5000,0.0000,1234.5000", text);
        Assert.Contains("2,خصوم,\"Liab, \"\"quoted\"\"\",0,no,0.0000,0.0000,1234.5000,-1234.5000", text);
        Assert.Contains("TOTAL,,,,,0.0000,1234.5000,1234.5000,0.0000", text);
    }

}
