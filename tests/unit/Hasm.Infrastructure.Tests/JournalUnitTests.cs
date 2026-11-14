using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.2B S5: journal permissions, database-rule codes and the shape of the journal migration (no database needed).</summary>
public class JournalUnitTests
{
    [Fact]
    public void Journal_approval_is_never_in_the_administrator_preset_and_only_the_journal_approver_preset_holds_it()
    {
        var admin = PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions;
        Assert.DoesNotContain(PermissionCatalog.Codes.JournalApprove, admin);
        foreach (var code in new[] { PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalUpdate, PermissionCatalog.Codes.JournalSubmit,
                     PermissionCatalog.Codes.JournalPost, PermissionCatalog.Codes.JournalReverse, PermissionCatalog.Codes.JournalSoftClosedPost, PermissionCatalog.Codes.JournalRateOverride,
                     PermissionCatalog.Codes.LedgerSettingsConfigure, PermissionCatalog.Codes.LedgerReportsView, PermissionCatalog.Codes.LedgerReportsExport })
            Assert.Contains(code, admin);
        Assert.Equal([PermissionCatalog.Codes.JournalApprove, PermissionCatalog.Codes.JournalView], PresetRoles.Definitions[PresetRoles.JournalApproverId].Permissions.Order());
    }

    [Fact]
    public void Journal_and_report_permissions_are_branch_scoped_while_soft_closed_posting_the_rate_override_and_settings_are_company_wide()
    {
        PermissionResource Res(string key) => PermissionCatalog.Default.Resources.Single(r => r.Key == key);
        Assert.Equal(ScopeKind.Branch, Res("company.journal").Scope);
        Assert.Equal(ScopeKind.Company, Res("company.journalsoftclosed").Scope);
        Assert.Equal(ScopeKind.Company, Res("company.journalrate").Scope);
        Assert.Equal(ScopeKind.Company, Res("company.ledgersettings").Scope);
        Assert.Equal(7, Res("company.journal").Actions.Count);
    }

    [Theory]
    [InlineData("the entry is not balanced in base currency", "entry_not_balanced")]
    [InlineData("a journal entry needs at least two lines", "entry_needs_two_lines")]
    [InlineData("every account must be active and postable", "account_not_postable")]
    [InlineData("the required approvals for this revision are missing", "approval_required")]
    [InlineData("the fiscal period is final closed", "fiscal_period_final_closed")]
    [InlineData("the fiscal period is soft closed", "fiscal_period_soft_closed")]
    [InlineData("the entry date is outside every fiscal period", "no_fiscal_period")]
    [InlineData("the applied exchange rate is no longer the effective rate", "rate_changed")]
    [InlineData("allocations may only target active leaf cost centers", "cost_center_not_allocatable")]
    [InlineData("something the database said that the client must never see", "entry_invalid")]
    public void Database_rules_map_to_stable_codes_and_unknown_messages_never_leak(string message, string code) => Assert.Equal(code, JournalService.MapDatabaseRule(message));

    [Fact]
    public void The_journal_migration_part_has_no_floating_point_no_drops_and_makes_posted_data_immutable()
    {
        var sql = System.Text.RegularExpressions.Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 8).Sql, "--[^\n]*", "");
        Assert.DoesNotContain("double precision", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" real,", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"CREATE TABLE {CompanySchema.JournalEntry.Table}", sql);
        Assert.Contains($"CREATE TABLE {CompanySchema.JournalLine.Table}", sql);
        Assert.Contains($"CREATE TABLE {CompanySchema.JournalAllocation.Table}", sql);
        Assert.Equal(["INSERT", "SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.JournalEvent.Table).Privileges.Order());
        Assert.DoesNotContain("DELETE", CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.JournalEntry.Table).Privileges);
    }
}
