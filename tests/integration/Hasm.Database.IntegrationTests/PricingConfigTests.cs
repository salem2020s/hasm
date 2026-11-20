using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Pricing;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using PV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PricingPolicyVersion;
using PL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceList;
using PE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceEntry;
using DR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DiscountRule;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.11 slice 2b: pricing configuration (company migration 0026, system migration 0027) against real PostgreSQL: the append-only policy versions with the company-configurable precedences (owner
/// decision P), price lists, entries and discount rules (revision-fenced, never removed, immutable identity), audit, authorization and the quote that feeds the pure engine and seals a snapshot. Disposable cluster only.
/// </summary>
public class PricingConfigTests : InventoryTestBase
{
    private static readonly DateOnly Jan = new(2026, 1, 1), Jun = new(2026, 6, 1);
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

    private Task<long> PricingEvents() => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1800 AND 1809");
    private async Task<(Guid Item, Guid Unit, Guid Group)> NewItem(string code)
    {
        var u = await Unit("PCS-" + code); var g = await Group("G-" + code, valuation: 1);
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, u.Id, code)));
        return (item.Id, u.Id, g.Id);
    }
    private async Task<Guid> NewPartyId(string code)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{id}', '{code}', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
        return id;
    }
    /// <summary>A local party WITH a customer role (a cash customer needs no control account): the only kind a price list or discount rule may name.</summary>
    private async Task<Guid> NewCustomerId(string code)
    {
        var id = await NewPartyId(code);
        Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(id, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null)));
        return id;
    }
    private static PriceListInput List(string code, int priority = 10, Guid? customer = null, string currency = "JOD", DateOnly? from = null, DateOnly? to = null, bool? active = null) =>
        new(code, "قائمة " + code, "List " + code, priority, currency, from ?? Jan, to, active, customer);
    private Task<PriceListView> NewList(string code, int priority = 10, Guid? customer = null) => Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List(code, priority, customer)).ContinueWith(t => Ok(t.Result));
    private Task<PriceEntryView> NewEntry(Guid list, Guid item, Guid unit, decimal price, decimal tier = 0) =>
        Pricing.CreateEntryAsync(Admin.Id, Company.Id, list, new PriceEntryInput(item, null, unit, tier, price, null)).ContinueWith(t => Ok(t.Result));
    private static PricingRequest Req(Guid item, Guid unit, Guid? group = null, Guid? customer = null, decimal qty = 1) => new(new DateOnly(2026, 6, 15), "JOD", item, group, unit, qty, customer);

    [PostgresFact]
    public async Task A_new_company_has_empty_pricing_tables_the_default_policy_and_only_the_company_administrator_holds_the_permissions()
    {
        await Prepare();
        foreach (var t in new[] { PV.Table, PL.Table, PE.Table, DR.Table }) Assert.Equal(0L, await Count(Company, t));
        var policy = Ok(await PricingMgmt.GetPolicyAsync(Admin.Id, Company.Id));
        Assert.Equal((0, true), (policy.Version, policy.IsDefault));
        Assert.True(policy.Policy.HasDefaultPrecedence);                                                                            // nothing is activated silently
        Assert.Empty(Ok(await PricingMgmt.ListPriceListsAsync(Admin.Id, Company.Id)));
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "view", "configure", "override" }) Assert.Contains($"company.pricing.{c}", admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.pricing."));
        Assert.Equal(new PricingCapabilities(true, true, true), await PricingMgmt.CapabilitiesAsync(Admin.Id, Company.Id));
    }

    [PostgresFact]
    public async Task The_policy_is_saved_as_append_only_versions_with_a_version_fence_validation_and_audit()
    {
        await Prepare();
        var input = new PricingPolicyInput(3, 2, 5m, 2, (int)PriceListPrecedence.PriorityThenScope, (int)DiscountPrecedence.PriorityThenSpecificity);
        Fails(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, null, input), ConfigStatus.PreconditionRequired, "precondition_required");
        var v1 = Ok(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 0, input));
        Assert.Equal((1, false, 3, 2, 5m, MarginAction.RequireApproval, PriceListPrecedence.PriorityThenScope, DiscountPrecedence.PriorityThenSpecificity),
            (v1.Version, v1.IsDefault, v1.Policy.PriceDecimals, v1.Policy.MoneyDecimals, v1.Policy.MinMarginPercent, v1.Policy.Action, v1.Policy.ListPrecedence, v1.Policy.DiscountPrecedence));
        var stale = await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 0, input);                                                    // looked at "no policy", but v1 exists now
        Fails(stale, ConfigStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(1L, stale.CurrentRevision);
        Invalid(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 1, input with { PriceDecimals = 7 }), "priceDecimals", "out_of_range");
        Invalid(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 1, input with { MarginAction = 9 }), "marginAction", "invalid_value");
        Invalid(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 1, input with { ListPrecedence = 0 }), "listPrecedence", "invalid_value");
        Invalid(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 1, input with { MinMarginPercent = 101m }), "minMarginPercent", "out_of_range");
        Invalid(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 1, input with { DiscountPrecedence = null }), "discountPrecedence", "required");
        var v2 = Ok(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 1, new PricingPolicyInput(2, 2, null, 1, 1, 1)));
        Assert.True(v2.Policy.HasDefaultPrecedence);
        Assert.Equal(2L, await Count(Company, PV.Table));                                                                            // history is kept
        Assert.Equal(2L, await PricingEvents());
        Assert.Equal("P0001", await Fails($"UPDATE {PV.Table} SET {PV.PriceDecimals} = 4"));                                         // append-only
        Assert.Equal("P0001", await Fails($"DELETE FROM {PV.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {PV.Table}"));
        Assert.Equal("P0001", await Fails($"INSERT INTO {PV.Table} ({PV.Id}, {PV.Version}, {PV.PriceDecimals}, {PV.MoneyDecimals}, {PV.MarginAction}, {PV.ListPrecedence}, {PV.DiscountPrecedence}, {PV.CreatedBy}, {PV.CreatedAtUtc}) VALUES (gen_random_uuid(), 9, 2, 2, 1, 1, 1, '{Admin.Id}', now())"));   // a gap is refused
        Assert.Equal("23514", await Fails($"INSERT INTO {PV.Table} ({PV.Id}, {PV.Version}, {PV.PriceDecimals}, {PV.MoneyDecimals}, {PV.MarginAction}, {PV.ListPrecedence}, {PV.DiscountPrecedence}, {PV.CreatedBy}, {PV.CreatedAtUtc}) VALUES (gen_random_uuid(), 3, 2, 2, 1, 5, 1, '{Admin.Id}', now())"));
    }

    [PostgresFact]
    public async Task Price_lists_and_entries_keep_their_identity_advance_by_revision_validate_and_are_never_removed()
    {
        await Prepare();
        var party = await NewCustomerId("C1");
        var list = Ok(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List(" retail ", 5, party)));
        Assert.Equal(("RETAIL", 1L, "JOD", true, party), (list.Code, list.Revision, list.Currency, list.IsActive, list.CustomerId));
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("RETAIL")), "code", "code_taken");
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("bad code")), "code", "code_invalid");
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("X1", currency: "jd")), "currency", "currency_invalid");
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("X2", to: new DateOnly(2025, 12, 31))), "validTo", "before_valid_from");
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("X3", priority: -1)), "priority", "out_of_range");
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("X4", customer: Guid.NewGuid())), "customerId", "customer_role_required");   // the customer must exist and be a customer
        Invalid(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, List("X5", customer: await NewPartyId("NOROLE"))), "customerId", "customer_role_required");   // a party without a customer role is refused
        Fails(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, null, List("RETAIL")), ConfigStatus.PreconditionRequired, "precondition_required");
        var updated = Ok(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, 1, List("RETAIL", 7, party, active: false)));
        Assert.Equal((2L, 7, false), (updated.Revision, updated.Priority, updated.IsActive));
        Fails(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, 1, List("RETAIL", 8, party)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, 2, List("OTHER", 7, party)), "code", "code_immutable");
        Invalid(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, 2, List("RETAIL", 7, party, currency: "USD")), "currency", "currency_immutable");
        Invalid(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, 2, List("RETAIL", 7, await NewCustomerId("C2"))), "customerId", "customer_immutable");
        Fails(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, List("RETAIL")), ConfigStatus.NotFound, "price_list_not_found");
        Assert.Equal("P0001", await Fails($"UPDATE {PL.Table} SET {PL.Currency} = 'USD', {PL.Revision} = {PL.Revision} + 1"));                                       // immutable in PostgreSQL too
        Assert.Equal("P0001", await Fails($"DELETE FROM {PL.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {PL.Table} CASCADE"));
        Assert.Equal("23514", await Fails($"UPDATE {PL.Table} SET {PL.Priority} = -5, {PL.Revision} = {PL.Revision} + 1"));

        var (item, unit, group) = await NewItem("A1");
        var general = await NewList("GEN");
        var e1 = await NewEntry(general.Id, item, unit, 10m, 0);
        var e2 = await NewEntry(general.Id, item, unit, 8m, 10);                                                                                                      // a quantity tier
        var byGroup = Ok(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, general.Id, new PriceEntryInput(null, group, unit, 0, 9m, null)));
        Assert.Equal(3, Ok(await PricingMgmt.ListEntriesAsync(Admin.Id, Company.Id, general.Id)).Count);
        Invalid(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, general.Id, new PriceEntryInput(item, null, unit, 0, 11m, null)), "minQuantity", "tier_exists");
        Invalid(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, general.Id, new PriceEntryInput(item, group, unit, 0, 1m, null)), "itemId", "item_or_group_required");
        Invalid(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, general.Id, new PriceEntryInput(null, null, unit, 0, 1m, null)), "itemId", "item_or_group_required");
        Invalid(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, general.Id, new PriceEntryInput(item, null, unit, 0, -1m, null)), "unitPrice", "out_of_range");
        Invalid(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, general.Id, new PriceEntryInput(item, null, unit, 0, 1.1234567m, null)), "unitPrice", "out_of_range");
        Invalid(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, general.Id, new PriceEntryInput(item, null, Guid.NewGuid(), 0, 1m, null)), "reference", "reference_not_found");
        Fails(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, Guid.NewGuid(), new PriceEntryInput(item, null, unit, 0, 1m, null)), ConfigStatus.NotFound, "price_list_not_found");
        var repriced = Ok(await Pricing.UpdateEntryAsync(Admin.Id, Company.Id, e1.Id, 1, new PriceEntryInput(null, null, null, null, 12.5m, null)));
        Assert.Equal((2L, 12.5m, 0m), (repriced.Revision, repriced.UnitPrice, repriced.MinQuantity));
        Fails(await Pricing.UpdateEntryAsync(Admin.Id, Company.Id, e1.Id, 1, new PriceEntryInput(null, null, null, null, 13m, null)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await Pricing.UpdateEntryAsync(Admin.Id, Company.Id, e1.Id, 2, new PriceEntryInput(null, null, null, 5m, 13m, null)), "minQuantity", "tier_immutable");
        Invalid(await Pricing.UpdateEntryAsync(Admin.Id, Company.Id, e1.Id, 2, new PriceEntryInput(null, null, Guid.NewGuid(), null, 13m, null)), "unitId", "unit_immutable");
        Assert.Equal("P0001", await Fails($"UPDATE {PE.Table} SET {PE.MinQuantity} = 3, {PE.Revision} = {PE.Revision} + 1 WHERE {PE.Id} = '{e2.Id}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {PE.Table}"));
        Assert.Equal("23514", await Fails($"INSERT INTO {PE.Table} ({PE.Id}, {PE.PriceListId}, {PE.ItemId}, {PE.ItemGroupId}, {PE.UnitId}, {PE.MinQuantity}, {PE.UnitPrice}, {PE.Revision}, {PE.CreatedBy}, {PE.CreatedAtUtc}, {PE.UpdatedAtUtc}) VALUES (gen_random_uuid(), '{general.Id}', '{item}', '{group}', '{unit}', 1, 1, 1, '{Admin.Id}', now(), now())"));   // item AND group
        _ = byGroup;
    }

    [PostgresFact]
    public async Task Discount_rules_keep_code_and_scope_advance_by_revision_validate_and_are_never_removed()
    {
        await Prepare();
        var (item, _, group) = await NewItem("D1");
        var party = await NewCustomerId("C9");
        DiscountRuleInput Rule(string code, decimal pct = 10m, int prio = 10, Guid? cust = null, Guid? it = null, Guid? grp = null, decimal? min = null) => new(code, "خصم " + code, "Discount " + code, prio, Jan, null, null, pct, min, cust, it, grp);
        var d = Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule(" promo ", 12.5m, 3, party, item, null, 5m)));
        Assert.Equal(("PROMO", 12.5m, 5m, party, item, 1L, true), (d.Code, d.Percent, d.MinQuantity, d.CustomerId, d.ItemId, d.Revision, d.IsActive));
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("PROMO")), "code", "code_taken");
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("D2", pct: 100.5m)), "percent", "out_of_range");
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("D3", pct: 5.12345m)), "percent", "out_of_range");
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("D4", it: item, grp: group)), "itemId", "item_or_group_not_both");
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("D5", min: -1m)), "minQuantity", "out_of_range");
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("D6", cust: Guid.NewGuid())), "customerId", "customer_role_required");
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("D7", cust: await NewPartyId("NOROLE2"))), "customerId", "customer_role_required");
        // two ACTIVE rules of the same scope, priority, minimum quantity and start would be ambiguous for the engine: refused, so one bad pair can never block pricing
        Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("G1", 5m, 7)));
        Invalid(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("G2", 6m, 7)), "priority", "ambiguous_rule");
        Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("G3", 6m, 8)));                                              // another priority is fine
        var inactive = Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, Rule("G4", 6m, 7) with { IsActive = false }));          // an inactive one is not evaluated, so it may share the key
        Invalid(await Pricing.UpdateDiscountAsync(Admin.Id, Company.Id, inactive.Id, 1, Rule("G4", 6m, 7) with { IsActive = true }), "priority", "ambiguous_rule");   // activating it would create the tie
        Fails(await Pricing.UpdateDiscountAsync(Admin.Id, Company.Id, d.Id, null, Rule("PROMO")), ConfigStatus.PreconditionRequired, "precondition_required");
        var updated = Ok(await Pricing.UpdateDiscountAsync(Admin.Id, Company.Id, d.Id, 1, Rule("PROMO", 15m, 4, null, null, null, 6m)));
        Assert.Equal((2L, 15m, 4, 6m), (updated.Revision, updated.Percent, updated.Priority, updated.MinQuantity));
        Assert.Equal((party, item), (updated.CustomerId, updated.ItemId));                                                            // the scope is kept when the update omits it
        Fails(await Pricing.UpdateDiscountAsync(Admin.Id, Company.Id, d.Id, 1, Rule("PROMO")), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await Pricing.UpdateDiscountAsync(Admin.Id, Company.Id, d.Id, 2, Rule("OTHER")), "code", "code_immutable");
        Invalid(await Pricing.UpdateDiscountAsync(Admin.Id, Company.Id, d.Id, 2, Rule("PROMO", grp: group)), "scope", "scope_immutable");
        Assert.Equal("P0001", await Fails($"UPDATE {DR.Table} SET {DR.ItemId} = NULL, {DR.Revision} = {DR.Revision} + 1"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {DR.Table}"));
        Assert.Equal("23514", await Fails($"UPDATE {DR.Table} SET {DR.Percent} = 150, {DR.Revision} = {DR.Revision} + 1"));
        Assert.Equal(5L, await PricingEvents());
    }

    [PostgresFact]
    public async Task A_quote_follows_the_company_policy_seals_a_snapshot_that_later_changes_cannot_alter_and_ignores_inactive_data()
    {
        await Prepare();
        var (item, unit, group) = await NewItem("Q1");
        var customer = await NewCustomerId("CQ");
        var special = await NewList("SPECIAL", priority: 99, customer: customer);
        var general = await NewList("GENERAL", priority: 1);
        await NewEntry(special.Id, item, unit, 8m);
        var geneEntry = await NewEntry(general.Id, item, unit, 10m);
        Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, new DiscountRuleInput("GEN-DISC", "خصم", null, 1, Jan, null, null, 20m, null, null, null, null)));
        Ok(await Pricing.CreateDiscountAsync(Admin.Id, Company.Id, new DiscountRuleInput("CUST-DISC", "خصم", null, 50, Jan, null, null, 5m, null, customer, null, null)));
        var request = Req(item, unit, group, customer, qty: 3);

        var byDefault = Ok(await Pricing.QuoteAsync(Company.Id, request, null));                                                      // no saved policy: the defaults of slice 1
        Assert.Equal(("pricing-snapshot-v1", 8m, 5m, 22.8m), (byDefault.Version, byDefault.Result.ListUnitPrice, byDefault.Result.DiscountPercent, byDefault.Result.LineNet));   // customer list and customer discount win; 8 x 0.95 = 7.60 x 3
        Assert.True(PricingSnapshots.Verify(byDefault));

        Ok(await Pricing.SavePolicyAsync(Admin.Id, Company.Id, 0, new PricingPolicyInput(2, 2, null, 1, (int)PriceListPrecedence.PriorityThenScope, (int)DiscountPrecedence.PriorityThenSpecificity)));
        var configured = Ok(await Pricing.QuoteAsync(Company.Id, request, null));                                                     // the company chose priority first
        Assert.Equal(("pricing-snapshot-v2", 10m, 20m, 24m), (configured.Version, configured.Result.ListUnitPrice, configured.Result.DiscountPercent, configured.Result.LineNet));  // general list (priority 1) and general discount (priority 1); 10 x 0.80 = 8.00 x 3
        Assert.Contains(configured.Resolved.Trace, t => t.Kind == "policy");
        Assert.True(PricingSnapshots.Verify(configured));

        // a later change of the configuration never alters a sealed snapshot
        Ok(await Pricing.UpdateEntryAsync(Admin.Id, Company.Id, geneEntry.Id, 1, new PriceEntryInput(null, null, null, null, 99m, null)));
        Assert.True(PricingSnapshots.Verify(configured));
        Assert.Equal(24m, configured.Result.LineNet);
        Assert.Equal(99m, Ok(await Pricing.QuoteAsync(Company.Id, request, null)).Result.ListUnitPrice);                             // while a new quote sees the new price

        // inactive data is never evaluated
        Ok(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, general.Id, 1, List("GENERAL", 1, active: false)));
        Assert.Equal(8m, Ok(await Pricing.QuoteAsync(Company.Id, request, null)).Result.ListUnitPrice);
        Ok(await Pricing.UpdatePriceListAsync(Admin.Id, Company.Id, special.Id, 1, List("SPECIAL", 99, customer, active: false)));
        var none = await Pricing.QuoteAsync(Company.Id, request, null);
        Assert.Equal(ConfigStatus.Invalid, none.Status);
        Assert.Contains(none.Fields!, f => f is { Field: "pricing", Code: "no_price" });                                              // never an implicit zero
        Assert.Equal(ConfigStatus.Invalid, (await Pricing.QuoteAsync(Company.Id, null, null)).Status);
    }

    [PostgresFact]
    public async Task Reads_and_quotes_need_the_view_permission_writes_the_configure_permission_and_an_override_its_own()
    {
        await Prepare();
        var (item, unit, group) = await NewItem("P1");
        var list = await NewList("L1");
        await NewEntry(list.Id, item, unit, 10m);
        var (viewer, _) = await Member([PermissionCatalog.Codes.PricingView], ScopeKind.Company, null);
        var (editor, _) = await Member([PermissionCatalog.Codes.PricingView, PermissionCatalog.Codes.PricingConfigure], ScopeKind.Company, null);
        var (overrider, _) = await Member([PermissionCatalog.Codes.PricingView, PermissionCatalog.Codes.PricingOverride], ScopeKind.Company, null);
        var (nobody, _) = await Member([PermissionCatalog.Codes.ItemsView], ScopeKind.Company, null);

        Assert.Single(Ok(await PricingMgmt.ListPriceListsAsync(viewer.Id, Company.Id)));
        Fails(await PricingMgmt.ListPriceListsAsync(nobody.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await PricingMgmt.GetPolicyAsync(nobody.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await PricingMgmt.CreatePriceListAsync(viewer.Id, Company.Id, List("NOPE")), ConfigStatus.Forbidden, "forbidden");
        Fails(await PricingMgmt.SavePolicyAsync(viewer.Id, Company.Id, 0, new PricingPolicyInput(2, 2, null, 1, 1, 1)), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal("L2", Ok(await PricingMgmt.CreatePriceListAsync(editor.Id, Company.Id, List("L2"))).Code);
        Fails(await PricingMgmt.CreateDiscountAsync(overrider.Id, Company.Id, new DiscountRuleInput("X", "x", null, 1, Jan, null, null, 1m, null, null, null, null)), ConfigStatus.Forbidden, "forbidden");

        var request = Req(item, unit, group);
        var over = new PriceOverride(7m, null, "negotiated");
        Ok(await PricingMgmt.QuoteAsync(viewer.Id, Company.Id, request, null));
        Fails(await PricingMgmt.QuoteAsync(viewer.Id, Company.Id, request, over), ConfigStatus.Forbidden, "forbidden");                  // an override needs its own permission
        Fails(await PricingMgmt.QuoteAsync(nobody.Id, Company.Id, request, null), ConfigStatus.Forbidden, "forbidden");
        var overridden = Ok(await PricingMgmt.QuoteAsync(overrider.Id, Company.Id, request, over));
        Assert.Equal((true, 7m, "negotiated"), (overridden.Result.Overridden, overridden.Result.NetUnitPrice, overridden.Result.OverrideReason));
        Assert.Equal(new PricingCapabilities(true, false, false), await PricingMgmt.CapabilitiesAsync(viewer.Id, Company.Id));
        Assert.Equal(new PricingCapabilities(true, true, false), await PricingMgmt.CapabilitiesAsync(editor.Id, Company.Id));
        Assert.Equal(new PricingCapabilities(false, false, false), await PricingMgmt.CapabilitiesAsync(nobody.Id, Company.Id));
    }
}
