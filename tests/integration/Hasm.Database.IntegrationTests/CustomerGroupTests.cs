using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Pricing;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using PL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceList;
using PLG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceListGroupScope;
using DR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DiscountRule;
using DRG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DiscountRuleGroupScope;
using CG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CustomerGroup;
using CGM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CustomerGroupMember;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.11 customer groups (company migration 0027) against real PostgreSQL: the group master, the one-group-per-customer assignment, the customer-OR-group scope of price lists and discount rules (service
/// and database), the regrouping concurrency fence, authorization, audit, and a quote that derives the customer's group SERVER-SIDE from the stored assignment. Disposable cluster only.
/// </summary>
public class CustomerGroupTests : InventoryTestBase
{
    private static readonly DateOnly Jan = new(2026, 1, 1);
    private PricingConfigService Pricing = null!;
    private PricingManagementService PricingMgmt = null!;
    private PartyRoleService Roles = null!;

    private async Task Prepare()
    {
        await PrepareInventory();
        Pricing = new PricingConfigService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        PricingMgmt = new PricingManagementService(Rbac, Pricing);
        Roles = new PartyRoleService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
    }

    private Task<long> Events(int type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");
    private static CustomerGroupInput Grp(string code, bool? active = null) => new(code, "مجموعة " + code, "Group " + code, active);
    private async Task<CustomerGroupView> NewGroup(string code) => Ok(await Pricing.CreateCustomerGroupAsync(Admin.Id, Company.Id, Grp(code)));
    private async Task<Guid> NewPartyId(string code)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{id}', '{code}', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
        return id;
    }
    private async Task<Guid> NewCustomerId(string code)
    {
        var id = await NewPartyId(code);
        Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(id, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null)));
        return id;
    }
    private async Task<(Guid Item, Guid Unit)> NewItemAndUnit(string code)
    {
        var u = await Unit("PCS-" + code); var g = await Group("G-" + code, valuation: 1);
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, u.Id, code)));
        return (item.Id, u.Id);
    }
    private static PriceListInput List(string code, int priority = 10, Guid? customer = null, Guid? group = null, bool? active = null) =>
        new(code, "قائمة " + code, "List " + code, priority, "JOD", Jan, null, active, customer, group);
    private async Task<PriceListView> NewList(string code, int priority, Guid? customer = null, Guid? group = null) => Ok(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List(code, priority, customer, group)));
    private async Task NewEntry(Guid list, Guid item, Guid unit, decimal price) => Ok(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, list, new PriceEntryInput(item, null, unit, 0m, price, null)));
    private static DiscountRuleInput Disc(string code, int priority, decimal percent, Guid? customer = null, Guid? group = null) =>
        new(code, "خصم", null, priority, Jan, null, null, percent, 0m, customer, null, null, group);

    [PostgresFact]
    public async Task The_customer_group_master_is_validated_revision_fenced_audited_never_removed_and_guarded_in_postgresql()
    {
        await Prepare();
        Assert.Equal(0L, await Count(Company, CG.Table));
        var g = Ok(await Pricing.CreateCustomerGroupAsync(Admin.Id, Company.Id, Grp(" vip ")));
        Assert.Equal(("VIP", 1L, true), (g.Code, g.Revision, g.IsActive));
        Invalid(await Pricing.CreateCustomerGroupAsync(Admin.Id, Company.Id, Grp("VIP")), "code", "code_taken");
        Invalid(await Pricing.CreateCustomerGroupAsync(Admin.Id, Company.Id, Grp("bad code")), "code", "code_invalid");
        Invalid(await Pricing.CreateCustomerGroupAsync(Admin.Id, Company.Id, new CustomerGroupInput("NONAME", "  ", null, null)), "nameAr", "name_invalid");
        Fails(await Pricing.UpdateCustomerGroupAsync(Admin.Id, Company.Id, g.Id, null, Grp("VIP")), ConfigStatus.PreconditionRequired, "precondition_required");
        var off = Ok(await Pricing.UpdateCustomerGroupAsync(Admin.Id, Company.Id, g.Id, 1, Grp("VIP", active: false)));
        Assert.Equal((2L, false), (off.Revision, off.IsActive));
        Fails(await Pricing.UpdateCustomerGroupAsync(Admin.Id, Company.Id, g.Id, 1, Grp("VIP")), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await Pricing.UpdateCustomerGroupAsync(Admin.Id, Company.Id, g.Id, 2, Grp("OTHER")), "code", "code_immutable");
        Fails(await Pricing.UpdateCustomerGroupAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, Grp("VIP")), ConfigStatus.NotFound, "customer_group_not_found");
        Assert.Equal(1L, await Events(PricingAuditEvents.CustomerGroupCreated));
        Assert.Equal(1L, await Events(PricingAuditEvents.CustomerGroupUpdated));

        Assert.Equal("P0001", await Fails($"UPDATE {CG.Table} SET {CG.Code} = 'ZZ', {CG.Revision} = {CG.Revision} + 1"));       // the code is immutable in PostgreSQL
        Assert.Equal("P0001", await Fails($"UPDATE {CG.Table} SET {CG.NameAr} = 'x'"));                                          // a change must advance the revision by one
        Assert.Equal("P0001", await Fails($"DELETE FROM {CG.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {CG.Table} CASCADE"));
        Assert.Equal("23514", await Fails($"UPDATE {CG.Table} SET {CG.NameAr} = ' ', {CG.Revision} = {CG.Revision} + 1"));
        Assert.Equal("P0001", await Fails($"INSERT INTO {CG.Table} ({CG.Id}, {CG.Code}, {CG.NameAr}, {CG.Revision}, {CG.CreatedBy}, {CG.CreatedAtUtc}, {CG.UpdatedAtUtc}) VALUES (gen_random_uuid(), 'R9', 'x', 5, '{Admin.Id}', now(), now())"));

        // authorization: the pricing permissions govern groups too
        var (viewer, _) = await Member([PermissionCatalog.Codes.PricingView], ScopeKind.Company, null);
        var (editor, _) = await Member([PermissionCatalog.Codes.PricingView, PermissionCatalog.Codes.PricingConfigure], ScopeKind.Company, null);
        var (nobody, _) = await Member([PermissionCatalog.Codes.ItemsView], ScopeKind.Company, null);
        Assert.Single(Ok(await PricingMgmt.ListCustomerGroupsAsync(viewer.Id, Company.Id)));
        Fails(await PricingMgmt.ListCustomerGroupsAsync(nobody.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await PricingMgmt.ListAssignmentsAsync(nobody.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await PricingMgmt.CreateCustomerGroupAsync(viewer.Id, Company.Id, Grp("NOPE")), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal("G2", Ok(await PricingMgmt.CreateCustomerGroupAsync(editor.Id, Company.Id, Grp("G2"))).Code);
    }

    [PostgresFact]
    public async Task A_customer_belongs_to_at_most_one_active_group_only_a_customer_can_be_assigned_and_regrouping_is_fenced_and_audited()
    {
        await Prepare();
        var a = await NewGroup("A"); var b = await NewGroup("B");
        var inactive = Ok(await Pricing.CreateCustomerGroupAsync(Admin.Id, Company.Id, Grp("OFF", active: false)));
        var customer = await NewCustomerId("C1");
        var plain = await NewPartyId("NOROLE");
        Assert.Equal(customer, Assert.Single(Ok(await Pricing.ListAssignmentsAsync(Company.Id))).PartyId);          // only customer parties are listed
        Assert.Equal(0L, Assert.Single(Ok(await Pricing.ListAssignmentsAsync(Company.Id))).Revision);

        Fails(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, null, new CustomerGroupAssignmentInput(a.Id)), ConfigStatus.PreconditionRequired, "precondition_required");
        Invalid(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, plain, 0, new CustomerGroupAssignmentInput(a.Id)), "customerId", "customer_role_required");   // a non-customer is refused by the service
        Invalid(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, Guid.NewGuid(), 0, new CustomerGroupAssignmentInput(a.Id)), "customerId", "customer_role_required");
        Invalid(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 0, new CustomerGroupAssignmentInput(inactive.Id)), "customerGroupId", "customer_group_inactive");
        Invalid(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 0, new CustomerGroupAssignmentInput(Guid.NewGuid())), "customerGroupId", "customer_group_not_found");
        Fails(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 5, new CustomerGroupAssignmentInput(a.Id)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(0L, await Count(Company, CGM.Table));

        var first = Ok(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 0, new CustomerGroupAssignmentInput(a.Id)));
        Assert.Equal(a.Id, first.CustomerGroupId); Assert.Equal("A", first.CustomerGroupCode); Assert.Equal(1L, first.Revision);
        Fails(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 0, new CustomerGroupAssignmentInput(b.Id)), ConfigStatus.PreconditionFailed, "revision_conflict");   // stale: nothing changed
        var moved = Ok(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 1, new CustomerGroupAssignmentInput(b.Id)));
        Assert.Equal(b.Id, moved.CustomerGroupId); Assert.Equal(2L, moved.Revision);
        var cleared = Ok(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 2, new CustomerGroupAssignmentInput(null)));
        Assert.Null(cleared.CustomerGroupId); Assert.Equal(3L, cleared.Revision);
        Assert.Equal(1L, await Count(Company, CGM.Table));                                                                       // one row per party: never two groups at a time
        Assert.Equal(3L, await Events(PricingAuditEvents.CustomerGroupAssigned));

        Assert.Equal("23505", await Fails($"INSERT INTO {CGM.Table} ({CGM.PartyId}, {CGM.CustomerGroupId}, {CGM.Revision}, {CGM.CreatedBy}, {CGM.CreatedAtUtc}, {CGM.UpdatedAtUtc}) VALUES ('{customer}', '{a.Id}', 1, '{Admin.Id}', now(), now())"));
        Assert.Equal("P0001", await Fails($"UPDATE {CGM.Table} SET {CGM.PartyId} = '{plain}', {CGM.Revision} = {CGM.Revision} + 1"));   // the party never changes
        Assert.Equal("P0001", await Fails($"UPDATE {CGM.Table} SET {CGM.CustomerGroupId} = '{a.Id}'"));                         // revision must advance
        Assert.Equal("P0001", await Fails($"DELETE FROM {CGM.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {CGM.Table}"));
        Assert.Equal("23503", await Fails($"INSERT INTO {CGM.Table} ({CGM.PartyId}, {CGM.CustomerGroupId}, {CGM.Revision}, {CGM.CreatedBy}, {CGM.CreatedAtUtc}, {CGM.UpdatedAtUtc}) VALUES ('{Guid.NewGuid()}', null, 1, '{Admin.Id}', now(), now())"));   // an unknown party
    }

    [PostgresFact]
    public async Task Two_concurrent_regroupings_of_one_customer_cannot_both_win()
    {
        await Prepare();
        var a = await NewGroup("A"); var b = await NewGroup("B"); var c = await NewGroup("C");
        var customer = await NewCustomerId("C1");
        // first assignment race (both read "never assigned")
        var firstRace = await Task.WhenAll(
            Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 0, new CustomerGroupAssignmentInput(a.Id)),
            Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 0, new CustomerGroupAssignmentInput(b.Id)));
        Assert.Equal(1, firstRace.Count(r => r.IsOk));
        Assert.Equal(1, firstRace.Count(r => r.Status == ConfigStatus.PreconditionFailed));
        Assert.Equal(1L, await Count(Company, CGM.Table));
        // regroup race at revision 1
        var race = await Task.WhenAll(
            Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 1, new CustomerGroupAssignmentInput(b.Id)),
            Pricing.SetAssignmentAsync(Admin.Id, Company.Id, customer, 1, new CustomerGroupAssignmentInput(c.Id)));
        Assert.Equal(1, race.Count(r => r.IsOk));
        Assert.Equal(1, race.Count(r => r.Status == ConfigStatus.PreconditionFailed));
        Assert.Equal(2L, Assert.Single(Ok(await Pricing.ListAssignmentsAsync(Company.Id))).Revision);                  // exactly one change was applied
    }

    [PostgresFact]
    public async Task A_price_list_or_discount_is_for_a_customer_or_a_group_never_both_with_an_immutable_group_and_no_ambiguous_group_rules()
    {
        await Prepare();
        var a = await NewGroup("A"); var b = await NewGroup("B");
        var off = Ok(await Pricing.CreateCustomerGroupAsync(Admin.Id, Company.Id, Grp("OFF", active: false)));
        var customer = await NewCustomerId("C1");

        var list = await NewList("GL", 5, group: a.Id);
        Assert.Equal(a.Id, list.CustomerGroupId); Assert.Null(list.CustomerId);
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("BOTH", 5, customer, a.Id)), "customerGroupId", "customer_or_group_not_both");
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("NOGRP", 5, group: Guid.NewGuid())), "customerGroupId", "customer_group_not_found");
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("OFFGRP", 5, group: off.Id)), "customerGroupId", "customer_group_inactive");
        Invalid(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, 1, List("GL", 5, group: b.Id)), "customerGroupId", "customer_group_immutable");
        Assert.Equal(2L, Ok(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, 1, List("GL", 6, group: a.Id, active: false))).Revision);   // same group: allowed
        Assert.Equal("P0001", await Fails($"UPDATE {PL.Table} SET {PLG.CustomerGroupId} = '{b.Id}', {PL.Revision} = {PL.Revision} + 1"));               // immutable in PostgreSQL
        Assert.Equal("23514", await Fails($"INSERT INTO {PL.Table} ({PL.Id}, {PL.Code}, {PL.NameAr}, {PL.Priority}, {PL.Currency}, {PL.ValidFrom}, {PL.CustomerId}, {PLG.CustomerGroupId}, {PL.Revision}, {PL.CreatedBy}, {PL.CreatedAtUtc}, {PL.UpdatedAtUtc}) " +
            $"VALUES (gen_random_uuid(), 'RAW', 'x', 1, 'JOD', '2026-01-01', '{customer}', '{a.Id}', 1, '{Admin.Id}', now(), now())"));                     // the database refuses both scopes too

        var rule = Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Disc("GD", 1, 10m, group: a.Id)));
        Assert.Equal(a.Id, rule.CustomerGroupId); Assert.Null(rule.CustomerId);
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Disc("BOTHD", 1, 5m, customer, a.Id)), "customerGroupId", "customer_or_group_not_both");
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Disc("OFFD", 1, 5m, group: off.Id)), "customerGroupId", "customer_group_inactive");
        Invalid(await Pricing.UpdateDiscountAsync(Admin.Id, Company.Id, rule.Id, 1, Disc("GD", 1, 10m, group: b.Id)), "scope", "scope_immutable");
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Disc("GD2", 1, 20m, group: a.Id)), "priority", "ambiguous_rule");            // the same group, priority, quantity and start tie
        Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Disc("GD3", 1, 20m, group: b.Id)));                                              // another group never ties
        Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Disc("CD", 1, 20m, customer)));                                                  // nor does a customer rule
        Assert.Equal("P0001", await Fails($"UPDATE {DR.Table} SET {DRG.CustomerGroupId} = '{b.Id}', {DR.Revision} = {DR.Revision} + 1 WHERE {DR.Code} = 'GD'"));
        Assert.Equal("23514", await Fails($"INSERT INTO {DR.Table} ({DR.Id}, {DR.Code}, {DR.NameAr}, {DR.Priority}, {DR.ValidFrom}, {DR.Percent}, {DR.CustomerId}, {DRG.CustomerGroupId}, {DR.Revision}, {DR.CreatedBy}, {DR.CreatedAtUtc}, {DR.UpdatedAtUtc}) " +
            $"VALUES (gen_random_uuid(), 'RAWD', 'x', 9, '2026-01-01', 5, '{customer}', '{a.Id}', 1, '{Admin.Id}', now(), now())"));
        var edited = Ok(await Pricing.UpdateDiscountAsync(Admin.Id, Company.Id, rule.Id, 1, Disc("GD", 1, 11m, group: a.Id)));
        Assert.Equal(a.Id, edited.CustomerGroupId); Assert.Equal(2L, edited.Revision);
    }

    [PostgresFact]
    public async Task A_quote_derives_the_customers_group_from_the_stored_assignment_and_never_trusts_a_group_supplied_by_the_caller()
    {
        await Prepare();
        var (item, unit) = await NewItemAndUnit("QG");
        var vip = await NewGroup("VIP");
        var member = await NewCustomerId("MEMBER");
        var outsider = await NewCustomerId("OUTSIDER");
        var general = await NewList("GENERAL", 1); await NewEntry(general.Id, item, unit, 10m);
        var groupList = await NewList("VIPLIST", 50, group: vip.Id); await NewEntry(groupList.Id, item, unit, 8m);
        Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Disc("VIPDISC", 1, 10m, group: vip.Id)));
        Ok(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, member, 0, new CustomerGroupAssignmentInput(vip.Id)));
        PricingRequest Req(Guid? customer, Guid? claimedGroup = null) => new(new DateOnly(2026, 6, 15), "JOD", item, null, unit, 1m, customer, claimedGroup);

        var asMember = Ok(await Pricing.QuoteAsync(Company.Id, Req(member), null));                                                // group list beats the general one by scope; 8 x 0.90
        Assert.Equal((8m, 10m, 7.2m), (asMember.Result.ListUnitPrice, asMember.Result.DiscountPercent, asMember.Result.NetUnitPrice));
        Assert.True(PricingSnapshots.Verify(asMember));
        var asOutsider = Ok(await Pricing.QuoteAsync(Company.Id, Req(outsider), null));
        Assert.Equal((10m, 0m), (asOutsider.Result.ListUnitPrice, asOutsider.Result.DiscountPercent));                             // no group, no group price
        var claimed = Ok(await Pricing.QuoteAsync(Company.Id, Req(outsider, vip.Id), null));                                       // a claimed group is ignored: the stored assignment decides
        Assert.Equal((10m, 0m), (claimed.Result.ListUnitPrice, claimed.Result.DiscountPercent));
        var anonymousClaim = Ok(await Pricing.QuoteAsync(Company.Id, Req(null, vip.Id), null));                                    // and so is a group with no customer at all
        Assert.Equal(10m, anonymousClaim.Result.ListUnitPrice);

        var customerList = await NewList("OWN", 99, customer: member); await NewEntry(customerList.Id, item, unit, 7m);
        Assert.Equal(7m, Ok(await Pricing.QuoteAsync(Company.Id, Req(member), null)).Result.ListUnitPrice);                         // the customer list is more specific than the group list

        // regrouping changes new quotes only; a sealed snapshot is untouched
        Ok(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, customerList.Id, 1, List("OWN", 99, member, active: false)));
        Ok(await Pricing.SetAssignmentAsync(Admin.Id, Company.Id, member, 1, new CustomerGroupAssignmentInput(null)));
        Assert.Equal(10m, Ok(await Pricing.QuoteAsync(Company.Id, Req(member), null)).Result.ListUnitPrice);
        Assert.True(PricingSnapshots.Verify(asMember));
        Assert.Equal(7.2m, asMember.Result.NetUnitPrice);

        // the server also derives the item group, never the caller's
        var itemGroupClaim = Ok(await Pricing.QuoteAsync(Company.Id, Req(null) with { ItemGroupId = Guid.NewGuid() }, null));
        Assert.Equal(10m, itemGroupClaim.Result.ListUnitPrice);
        var unknownItem = await Pricing.QuoteAsync(Company.Id, Req(null) with { ItemId = Guid.NewGuid() }, null);
        Invalid(unknownItem, "itemId", "item_not_found");
    }
}
