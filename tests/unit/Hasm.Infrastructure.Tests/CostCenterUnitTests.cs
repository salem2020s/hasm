using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.2B S3: cost-center code and name validation, allocation eligibility and permissions (no database needed).</summary>
public class CostCenterUnitTests
{
    [Theory]
    [InlineData(" ops-1 ", "OPS-1")]
    [InlineData("٢٠٤", "204")]
    [InlineData("۱۲", "12")]
    [InlineData("a.b_c", "A.B_C")]
    public void Codes_are_normalised(string raw, string expected) => Assert.Equal(expected, CostCenterValidation.NormalizeCode(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-bad")]
    [InlineData("has space")]
    [InlineData("0123456789012345678901234567890")]
    public void Invalid_codes_are_rejected(string? raw) => Assert.Null(CostCenterValidation.NormalizeCode(raw));

    [Fact]
    public void A_name_in_either_language_is_enough_but_one_is_required()
    {
        Assert.True(CostCenterValidation.TryNormalize(new("A", null, "اسم", null, null, null), out _, out _, out _, out _, out _));
        Assert.True(CostCenterValidation.TryNormalize(new("A", null, null, "Name", null, null), out _, out _, out _, out _, out _));
        Assert.False(CostCenterValidation.TryNormalize(new("A", null, " ", null, null, null), out _, out _, out _, out _, out var e));
        Assert.Contains(e, x => x.Code == "name_required");
        Assert.False(CostCenterValidation.TryNormalize(new("A", null, new string('x', 201), null, null, null), out _, out _, out _, out _, out var e2));
        Assert.Contains(e2, x => x.Code == "too_long");
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]     // a group never receives allocations
    [InlineData(false, false, false)]   // an inactive center never receives new allocations
    public void Only_an_active_leaf_can_receive_allocations(bool active, bool group, bool expected) =>
        Assert.Equal(expected, new CostCenterView(Guid.NewGuid(), "A", "a", null, null, null, group, active, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false).CanReceiveAllocations);

    [Fact]
    public void The_six_permissions_are_company_wide_and_only_in_the_administrator_preset()
    {
        var codes = PermissionCatalog.Default.All.Select(p => p.Code).Where(c => c.StartsWith("company.costcenters")).Order();
        Assert.Equal(["company.costcenters.activate", "company.costcenters.create", "company.costcenters.recode", "company.costcenters.restructure", "company.costcenters.update", "company.costcenters.view"], codes);
        Assert.DoesNotContain(PresetRoles.Definitions.Where(d => d.Key != PresetRoles.CompanyAdministratorId).SelectMany(d => d.Value.Permissions), p => p.StartsWith("company.costcenters"));
    }
}
