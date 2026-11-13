using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.2B S4: approval-policy validation and permissions (no database needed).</summary>
public class ApprovalUnitTests
{
    private static ApprovalPolicyInput In(string? doc = "journal_entry", string? source = "manual", decimal? min = 100m, int? req = 2, string? ar = "س", string? en = null) => new(doc, source, null, min, req, ar, en);

    [Fact]
    public void A_valid_policy_is_normalised()
    {
        Assert.True(ApprovalValidation.TryNormalize(In(" Journal_Entry ", "MANUAL", 100.1234m), out var doc, out var src, out var min, out var req, out var ar, out var en, out var errors));
        Assert.Equal(("journal_entry", PolicySource.Manual, 100.1234m, 2, "س", null), (doc, src, min, req, ar, en));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(null, "manual", 1, 1, "docType", "required")]
    [InlineData("unknown_doc", "manual", 1, 1, "docType", "unknown_document_type")]
    [InlineData("journal_entry", null, 1, 1, "source", "required")]
    [InlineData("journal_entry", "sometimes", 1, 1, "source", "invalid_value")]
    [InlineData("journal_entry", "any", -1, 1, "minAmount", "must_not_be_negative")]
    [InlineData("journal_entry", "any", 1, 0, "requiredApprovals", "out_of_range")]
    [InlineData("journal_entry", "any", 1, 6, "requiredApprovals", "out_of_range")]
    public void Invalid_policies_are_rejected_with_a_field_error(string? doc, string? source, int min, int req, string field, string code)
    {
        Assert.False(ApprovalValidation.TryNormalize(In(doc, source, min, req), out _, out _, out _, out _, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Field == field && e.Code == code);
    }

    [Fact]
    public void The_threshold_has_at_most_four_decimals_and_is_never_rounded_silently()
    {
        Assert.False(ApprovalValidation.TryNormalize(In(min: 1.00001m), out _, out _, out _, out _, out _, out _, out var errors));
        Assert.Contains(errors, e => e is { Field: "minAmount", Code: "too_many_decimals" });
        Assert.True(ApprovalValidation.TryNormalize(In(min: 1.0000000m), out _, out _, out var m, out _, out _, out _, out _) && m == 1m);
    }

    [Fact]
    public void A_name_is_required_in_at_least_one_language()
    {
        Assert.False(ApprovalValidation.TryNormalize(In(ar: null, en: null), out _, out _, out _, out _, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Code == "name_required");
        Assert.True(ApprovalValidation.TryNormalize(In(ar: null, en: "Name"), out _, out _, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void The_four_policy_permissions_are_company_wide_and_only_in_the_administrator_preset()
    {
        var codes = PermissionCatalog.Default.All.Select(p => p.Code).Where(c => c.StartsWith("company.approvalpolicies")).Order();
        Assert.Equal(["company.approvalpolicies.activate", "company.approvalpolicies.create", "company.approvalpolicies.update", "company.approvalpolicies.view"], codes);
        Assert.DoesNotContain(PresetRoles.Definitions.Where(d => d.Key != PresetRoles.CompanyAdministratorId).SelectMany(d => d.Value.Permissions), p => p.StartsWith("company.approvalpolicies"));
    }
}
