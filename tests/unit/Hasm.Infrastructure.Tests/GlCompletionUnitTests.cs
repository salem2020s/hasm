using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.2C S2 and S3: approval policy validation (risk conditions, ordered stages), the year-end permissions and the shape of the migration (no database needed).</summary>
public class GlCompletionUnitTests
{
    private static ApprovalPolicyInput In(int? required = 1, string? jtype = null, bool? rate = null, bool? soft = null, string? closing = null, IReadOnlyList<ApprovalStageInput>? stages = null) =>
        new("journal_entry", "any", null, 10m, required, "اسم", "Name", jtype, rate, soft, stages, closing);

    [Fact]
    public void A_classic_policy_still_needs_its_count_and_a_staged_policy_ignores_it()
    {
        Assert.True(ApprovalValidation.TryNormalizeFull(In(2), out var classic, out _));
        Assert.Equal((2, 2, 0), (classic.Required, classic.Total, classic.Stages.Count));
        Assert.False(ApprovalValidation.TryNormalizeFull(In(null), out _, out var errors));
        Assert.Contains(errors, e => e is { Field: "requiredApprovals", Code: "required" });
        Assert.False(ApprovalValidation.TryNormalizeFull(In(9), out _, out _));                                           // classic 1..5 only
        Assert.True(ApprovalValidation.TryNormalizeFull(In(null, stages: [new(2, null), new(3, null)]), out var staged, out _));
        Assert.Equal((1, 5, 2), (staged.Required, staged.Total, staged.Stages.Count));                                    // the count column is a marker; the total is the sum of the stages
        Assert.Equal([1, 2], staged.Stages.Select(s => s.StageNo));
    }

    [Fact]
    public void Stages_are_validated_numbered_in_order_and_have_no_business_cap_on_their_number()
    {
        var role = Guid.NewGuid();
        Assert.True(ApprovalValidation.TryNormalizeFull(In(null, stages: [new(1, role), new(1, Guid.Empty)]), out var p, out _));
        Assert.Equal([role, null], p.Stages.Select(s => s.RoleId));                                                       // an empty id means "any approver"
        Assert.False(ApprovalValidation.TryNormalizeFull(In(null, stages: [new(0, null)]), out _, out var e1));
        Assert.Contains(e1, e => e is { Field: "stages[0].required", Code: "out_of_range" });
        Assert.False(ApprovalValidation.TryNormalizeFull(In(null, stages: [new(1, null), new(ApprovalValidation.MaxApproversPerStage + 1, null)]), out _, out var e2));
        Assert.Contains(e2, e => e.Field == "stages[1].required");
        Assert.False(ApprovalValidation.TryNormalizeFull(In(null, stages: [new(null, null)]), out _, out var e3));
        Assert.Contains(e3, e => e is { Field: "stages[0].required", Code: "required" });
        Assert.True(ApprovalValidation.TryNormalizeFull(In(null, stages: [.. Enumerable.Range(0, 40).Select(_ => new ApprovalStageInput(1, null))]), out var many, out _));
        Assert.Equal(40, many.Stages.Count);                                                                              // any number of levels
        Assert.Equal(40, many.Total);
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("any", true, null)]
    [InlineData("normal", true, (short)1)]
    [InlineData(" Reversal ", true, (short)2)]
    [InlineData("adjusting", true, (short)3)]
    [InlineData("closing", false, null)]
    public void The_journal_type_condition_is_parsed_strictly(string? raw, bool ok, short? expected)
    {
        Assert.Equal(ok, ApprovalValidation.TryParseJournalType(raw, out var t));
        Assert.Equal(expected, t);
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("any", true, null)]
    [InlineData("closing", true, (short)1)]
    [InlineData("Opening", true, (short)2)]
    [InlineData("reversal", false, null)]
    public void The_year_end_condition_is_parsed_strictly(string? raw, bool ok, short? expected)
    {
        Assert.Equal(ok, ApprovalValidation.TryParseClosingKind(raw, out var k));
        Assert.Equal(expected, k);
    }

    [Fact]
    public void Conditions_are_normalized_with_the_policy_and_unknown_values_are_refused_without_guessing()
    {
        Assert.True(ApprovalValidation.TryNormalizeFull(In(1, "reversal", true, false, "opening"), out var p, out _));
        Assert.Equal(((short?)2, (bool?)true, (bool?)false, (short?)2), (p.JournalType, p.RateOverride, p.SoftClosed, p.ClosingKind));
        Assert.False(ApprovalValidation.TryNormalizeFull(In(1, "bogus"), out _, out var e1));
        Assert.Contains(e1, e => e is { Field: "journalType", Code: "invalid_value" });
        Assert.False(ApprovalValidation.TryNormalizeFull(In(1, closing: "bogus"), out _, out var e2));
        Assert.Contains(e2, e => e is { Field: "closingKind", Code: "invalid_value" });
        // Details (an update) follow the same rules and never change the document type.
        Assert.True(ApprovalValidation.TryNormalizeDetailsFull(new ApprovalPolicyDetailsInput("manual", null, 5m, null, null, "Only", Stages: [new(2, null)]), "journal_entry", out var d, out _));
        Assert.Equal(("journal_entry", PolicySource.Manual, 2), (d.DocType, d.Source, d.Total));
    }

    [Fact]
    public void The_year_end_permissions_are_company_wide_in_the_administrator_preset_and_never_reach_the_journal_approver()
    {
        string[] codes = [PermissionCatalog.Codes.FiscalClosingView, PermissionCatalog.Codes.FiscalClosingConfigure, PermissionCatalog.Codes.FiscalClosingCreate, PermissionCatalog.Codes.FiscalClosingReverse];
        foreach (var c in codes)
        {
            Assert.Equal(ScopeKind.Company, PermissionCatalog.Default.Get(c).Resource.Scope);
            Assert.False(PermissionCatalog.Default.Get(c).Resource.AcceptsCostCenter);
            Assert.Contains(c, PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions);
            Assert.DoesNotContain(c, PresetRoles.Definitions[PresetRoles.JournalApproverId].Permissions);
        }
        Assert.Equal(codes.Order(), PermissionCatalog.Default.All.Select(p => p.Code).Where(c => c.StartsWith("company.fiscalclosing.")).Order());
    }

    [Fact]
    public void The_migration_extends_the_approval_functions_without_a_drop_and_protects_the_new_tables()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 9).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        // The classic function is REPLACED (same signature), the new functions exist, and the gate is a trigger on the entry itself (for every writer).
        Assert.Contains($"CREATE OR REPLACE FUNCTION {CompanySchema.ApprovalPolicy.RequiredFunction}(", sql);
        Assert.Contains($"CREATE FUNCTION {CompanySchema.ApprovalStaging.PlanFunction}(", sql);
        Assert.Contains($"CREATE OR REPLACE FUNCTION {CompanySchema.ApprovalStaging.PlanFunction}(", sql);          // extended by the year-end floor
        Assert.Contains($"CREATE TRIGGER {CompanySchema.ApprovalStaging.GateTrigger} BEFORE UPDATE ON {CompanySchema.JournalEntry.Table}", sql);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.ApprovalStaging.OrderTrigger} BEFORE INSERT ON {CompanySchema.ApprovalDecision.Table}", sql);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.ClosingLogic.PostTrigger} BEFORE UPDATE ON {CompanySchema.JournalEntry.Table}", sql);
        foreach (var table in new[] { CompanySchema.ApprovalPolicyStage.Table, CompanySchema.DocumentApprovalBinding.Table, CompanySchema.ClosingSettings.Table, CompanySchema.FiscalYearClosing.Table, CompanySchema.FiscalYearCarryforward.Table })
        {
            Assert.Contains($"CREATE TABLE {table}", sql);
            Assert.Contains($"REVOKE ALL ON TABLE {table} FROM PUBLIC", sql);
        }
        // Stages, bindings and year-end records are append-only for the runtime role: no UPDATE or DELETE privilege on them.
        foreach (var table in new[] { CompanySchema.ApprovalPolicyStage.Table, CompanySchema.DocumentApprovalBinding.Table, CompanySchema.FiscalYearClosing.Table, CompanySchema.FiscalYearCarryforward.Table })
            Assert.Equal(["INSERT", "SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges.Order());
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.ClosingSettings.Table).Privileges.Order());
        Assert.DoesNotContain("DELETE", CompanyRuntimePrivileges.Tables.Where(t => t.Table != CompanySchema.JournalLine.Table && t.Table != CompanySchema.JournalAllocation.Table).SelectMany(t => t.Privileges));
    }

    [Theory]
    [InlineData("the year-end closing no longer matches the ledger; regenerate it", "year_end_stale")]
    [InlineData("the opening balances do not match the balance sheet at the end of the closed year; regenerate them", "year_end_stale")]
    [InlineData("reverse the opening balances carried forward by this closing first", "opening_balances_not_reversed")]
    [InlineData("the required approvals for this revision are missing", "approval_required")]
    public void Year_end_and_approval_rules_map_to_stable_codes(string message, string code) => Assert.Equal(code, JournalService.MapDatabaseRule(message));
}
