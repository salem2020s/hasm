using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The stage authority a caller brings to an approval or a rejection (owner decision of 2026-11-21): a role counts for a role-bound stage only when it is held through an assignment that covers the document,
/// never because it is held somewhere else in the company.
/// </summary>
public class StageAuthorityTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), Role = Guid.NewGuid(), Other = Guid.NewGuid(), Cc = Guid.NewGuid(), Child = Guid.NewGuid(), Outside = Guid.NewGuid();
    private static readonly HashSet<string> None = [];

    private static EffectivePermissions With(params (Guid Role, ScopeKind Scope, Guid? Id)[] grants) =>
        new(PermissionCatalog.Default, [.. grants.Select(g => new GrantedRole(Guid.NewGuid(), g.Role, g.Scope, g.Id, None))]);

    [Fact]
    public void A_commercial_stage_role_counts_company_wide_or_for_the_documents_branch_only()
    {
        Assert.Contains(Role, CommercialDocumentManagementService.StageRolesOnBranch(With((Role, ScopeKind.Company, null)), A));
        Assert.Contains(Role, CommercialDocumentManagementService.StageRolesOnBranch(With((Role, ScopeKind.Branch, A)), A));
        Assert.DoesNotContain(Role, CommercialDocumentManagementService.StageRolesOnBranch(With((Role, ScopeKind.Branch, B)), A));                    // the role for ANOTHER branch
        Assert.DoesNotContain(Role, CommercialDocumentManagementService.StageRolesOnBranch(With((Role, ScopeKind.Warehouse, A)), A));                 // a warehouse assignment is not a branch assignment, whatever its id
        Assert.DoesNotContain(Role, CommercialDocumentManagementService.StageRolesOnBranch(With((Role, ScopeKind.CostCenter, A)), A));
        // another role covering the branch never lends its scope to the stage role
        var mixed = CommercialDocumentManagementService.StageRolesOnBranch(With((Other, ScopeKind.Company, null), (Role, ScopeKind.Branch, B)), A);
        Assert.Equal([Other], mixed);
        Assert.Empty(CommercialDocumentManagementService.StageRolesOnBranch(With(), A));
    }

    private static Task<IReadOnlySet<Guid>> Journal(EffectivePermissions eff, Guid[] branches, params Guid[][] lines) =>
        JournalManagementService.StageRolesAsync(eff, branches, lines, roots => Task.FromResult(roots.Contains(Cc) ? new HashSet<Guid> { Cc, Child } : [.. roots]));

    [Fact]
    public async Task A_journal_stage_role_counts_only_through_assignments_that_cover_everything_the_entry_touches()
    {
        Assert.Contains(Role, await Journal(With((Role, ScopeKind.Company, null)), [A, B]));
        Assert.Contains(Role, await Journal(With((Role, ScopeKind.Branch, A), (Role, ScopeKind.Branch, B)), [A, B, A]));
        Assert.DoesNotContain(Role, await Journal(With((Role, ScopeKind.Branch, A)), [A, B]));                                                   // one touched branch is not covered
        Assert.DoesNotContain(Role, await Journal(With((Role, ScopeKind.Branch, B)), [A]));                                                      // the role for ANOTHER branch
        Assert.DoesNotContain(Role, await Journal(With((Role, ScopeKind.Branch, A), (Other, ScopeKind.Branch, B)), [A, B]));                     // two roles never add up to one
        // cost-center path: every line allocated, and every allocation inside the granted cost centers (with descendants)
        Assert.Contains(Role, await Journal(With((Role, ScopeKind.CostCenter, Cc)), [A], [Cc], [Child]));
        Assert.DoesNotContain(Role, await Journal(With((Role, ScopeKind.CostCenter, Cc)), [A], [Cc], [Outside]));
        Assert.DoesNotContain(Role, await Journal(With((Role, ScopeKind.CostCenter, Cc)), [A], [Cc], []));                                      // an unallocated line
        Assert.DoesNotContain(Role, await Journal(With((Role, ScopeKind.CostCenter, Cc)), [A]));                                                // no lines at all
        Assert.Equal([Other], await Journal(With((Other, ScopeKind.Company, null), (Role, ScopeKind.Warehouse, A)), [A]));
    }
}
