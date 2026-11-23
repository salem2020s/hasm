using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// X4-PRESET-ROLE (owner Option A, D-254; plan docs/X4-PRESET-ROLES-PLAN.md section 3.3): the dedicated short-close preset Commercial Document Closer (D-201). It holds exactly view and close, is not privileged,
/// does not widen Company Administrator, shares no action with the Commercial Approver, and the seed of system migration 0041 equals the code definition (also pinned by AccessModelTests).
/// The short-close type registry is fail-closed: a type is closable only when it is registered AND named closable.
/// </summary>
public class CommercialDocumentCloserTests
{
    private static IReadOnlySet<string> Of(Guid id) => PresetRoles.Definitions[id].Permissions;

    [Fact]
    public void The_closer_holds_exactly_view_and_close_and_its_id_follows_the_series()
    {
        Assert.Equal("5c1b0f6e-0000-4a00-8000-00000000000c", PresetRoles.CommercialDocumentCloserId.ToString());
        Assert.Equal("commercial-document-closer", PresetRoles.Definitions[PresetRoles.CommercialDocumentCloserId].Key);
        Assert.Equal(["company.commercialdocs.close", "company.commercialdocs.view"], Of(PresetRoles.CommercialDocumentCloserId).Order(StringComparer.Ordinal));
        Assert.Equal(PresetRoles.Definitions.Count, PresetRoles.Definitions.Keys.Distinct().Count());
        Assert.Equal(PresetRoles.Definitions.Count, PresetRoles.Definitions.Values.Select(v => v.Key).Distinct().Count());
    }

    [Fact]
    public void The_close_code_is_branch_scoped_is_not_an_approval_and_the_closer_is_not_privileged()
    {
        Assert.True(PermissionCatalog.Default.TryGet(PermissionCatalog.Codes.CommercialClose, out var close));
        Assert.Equal(ScopeKind.Branch, close.Resource.Scope);
        Assert.Equal(PermissionAction.Close, close.Action);
        Assert.False(PrivilegedAccess.IsPrivilegedRole(PresetRoles.CommercialDocumentCloserId, Of(PresetRoles.CommercialDocumentCloserId)));
        // control: the classification is real (the Commercial Approver, which holds an approve code, IS privileged)
        Assert.True(PrivilegedAccess.IsPrivilegedRole(PresetRoles.CommercialApproverId, Of(PresetRoles.CommercialApproverId)));
    }

    [Fact]
    public void Company_Administrator_is_not_widened_and_only_the_closer_holds_the_close_code()
    {
        Assert.DoesNotContain(PermissionCatalog.Codes.CommercialClose, Of(PresetRoles.CompanyAdministratorId));
        Assert.Equal(PresetRoles.CommercialDocumentCloserId, Assert.Single(PresetRoles.Definitions, d => d.Value.Permissions.Contains(PermissionCatalog.Codes.CommercialClose)).Key);
    }

    [Fact]
    public void Segregation_of_duties_the_closer_and_the_approver_hold_none_of_each_others_actions()
    {
        var closer = Of(PresetRoles.CommercialDocumentCloserId);
        Assert.DoesNotContain(closer, c => c is "company.commercialdocs.approve" or "company.commercialdocs.create" or "company.commercialdocs.update" or "company.commercialdocs.submit" or "company.commercialdocs.cancel");
        Assert.DoesNotContain(PermissionCatalog.Codes.CommercialClose, Of(PresetRoles.CommercialApproverId));
    }

    [Fact]
    public void The_migration_seeds_exactly_the_definition_assigns_nobody_and_touches_no_other_role()
    {
        var script = MigrationScript.LoadEmbedded().Single(s => s.Version == 41);
        Assert.Equal("0041_commercial_document_closer.sql", script.Name);
        var sql = Regex.Replace(script.Sql, "--[^\n]*", "");
        var id = PresetRoles.CommercialDocumentCloserId.ToString();
        var seeded = Regex.Matches(sql, @"\('([0-9a-f-]{36})', '([a-z.]+)'\)").Select(m => (Role: m.Groups[1].Value, Code: m.Groups[2].Value)).ToList();
        Assert.All(seeded, s => Assert.Equal(id, s.Role));                                            // no other role gains a permission
        Assert.Equal(Of(PresetRoles.CommercialDocumentCloserId).Order(StringComparer.Ordinal), seeded.Select(s => s.Code).Order(StringComparer.Ordinal));
        Assert.Equal(2, Regex.Matches(sql, @"\bINSERT\s+INTO\b").Count);                             // the role row and its permission rows: no assignment, no membership
        Assert.Single(Regex.Matches(sql, Regex.Escape(SystemSchema.Role.Table) + @" \("));
        Assert.DoesNotMatch(@"\b(UPDATE|DELETE|DROP|GRANT|CREATE)\b", sql);
        Assert.Contains("'commercial-document-closer', 'Commercial Document Closer', true", sql);
        // both pairs are matched: each trigger paused once and re-enabled once
        Assert.Equal(2, Regex.Matches(sql, @"\bDISABLE TRIGGER\b").Count);
        Assert.Equal(2, Regex.Matches(sql, @"\bENABLE TRIGGER\b").Count);
        var entry = Assert.Single(MigrationGuard.ApprovedTriggerSwitches, a => a.Version == 41);
        Assert.Equal((MigrationSet.System, script.Name, script.Checksum), (entry.Set, entry.Name, entry.Checksum));
    }

    [Fact]
    public void The_short_close_type_registry_is_fail_closed()
    {
        var none = new CommercialDocumentTypeRegistry(["order"]);
        Assert.False(none.IsShortCloseAllowed("order"));                                             // registered, but not named closable
        Assert.Empty(none.ShortCloseTypes);
        var some = new CommercialDocumentTypeRegistry(["order", "invoice"], null, ["order", "ghost"]);
        Assert.True(some.IsShortCloseAllowed("order"));
        Assert.False(some.IsShortCloseAllowed("invoice"));
        Assert.False(some.IsShortCloseAllowed("ghost"));                                             // named closable, but not a registered type
        Assert.False(some.IsShortCloseAllowed("ORDER"));                                             // ordinal: no case folding
        Assert.Equal(["order"], some.ShortCloseTypes);
    }

    [Fact]
    public void The_open_quantity_of_a_line_is_ordered_minus_the_larger_of_fulfilled_and_invoiced_minus_closed()
    {
        var line = new LineQuantityView(1, 10m, 4m, 6m, 0m, 3m);
        Assert.Equal((3m, 1m, 1m), (line.RemainingToFulfil, line.RemainingToInvoice, line.Open));
        Assert.Equal(0m, new LineQuantityView(1, 10m, 10m, 2m, 0m).Open);
        Assert.Equal(10m, new LineQuantityView(1, 10m, 0m, 0m, 0m).Open);
    }
}
