using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Taxes;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using TD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxDefinition;
using TR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxRateVersion;
using TC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxClass;
using TI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxItemClass;
using TP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxPartyClass;
using TU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxRule;
using TB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxRuleBase;
using TV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxPolicyVersion;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.10 slice 2b: tax engine configuration (company migration 0022, system migration 0024) against real PostgreSQL: taxes, append-only dated rate versions, item and party classes and
/// their assignments, rules with base references, the versioned rounding policy, audit, authorization and the resolution that feeds the pure engine. Disposable cluster only.
/// </summary>
public class TaxConfigTests : InventoryTestBase
{
    private const string Sales = "sales_invoice", Sell = "sale";
    private static readonly DateOnly Jan = new(2026, 1, 1), Jun = new(2026, 6, 1);
    private TaxConfigService Tax = null!;
    private TaxManagementService TaxMgmt = null!;

    private async Task Prepare()
    {
        await PrepareInventory();
        Tax = new TaxConfigService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        TaxMgmt = new TaxManagementService(Rbac, Tax);
    }

    private Task<long> TaxEvents() => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1400 AND 1409");
    private async Task<TaxView> NewTax(string code, params (DateOnly From, TaxTreatment T, decimal Rate)[] rates)
    {
        var t = Ok(await Tax.CreateTaxAsync(Admin.Id, Company.Id, new TaxInput(code, "ضريبة " + code, "Tax " + code)));
        foreach (var r in rates) t = Ok(await Tax.AddRateAsync(Admin.Id, Company.Id, t.Id, new TaxRateInput(r.From, r.T, r.Rate)));
        return t;
    }
    private Task<TaxClassView> NewClass(TaxClassKind kind, string code) => Tax.CreateClassAsync(Admin.Id, Company.Id, new TaxClassInput(kind, code, "فئة " + code, null)).ContinueWith(t => Ok(t.Result));
    private static TaxRuleInput Rule(Guid tax, int priority = 10, Guid? itemClass = null, Guid? partyClass = null, DateOnly? from = null, DateOnly? to = null, TaxTreatment? over = null, Guid[]? includes = null) =>
        new(Sales, Sell, itemClass, partyClass, tax, priority, includes ?? [], from ?? Jan, to, true, over);
    private async Task<Guid> NewItemId(string code)
    {
        var u = await Unit("PCS-" + code); var g = await Group("G-" + code, valuation: 1);
        return Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, u.Id, code))).Id;
    }
    private async Task<Guid> NewPartyId(string code)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{id}', '{code}', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
        return id;
    }

    [PostgresFact]
    public async Task A_new_company_has_empty_tax_tables_and_only_the_company_administrator_holds_the_permissions()
    {
        await Prepare();
        foreach (var t in new[] { TD.Table, TR.Table, TC.Table, TI.Table, TP.Table, TU.Table, TB.Table, TV.Table }) Assert.Equal(0L, await Count(Company, t));
        Assert.Empty(Ok(await TaxMgmt.ListTaxesAsync(Admin.Id, Company.Id)));
        Assert.Null(Ok(await TaxMgmt.GetPolicyAsync(Admin.Id, Company.Id)));
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        Assert.Contains("company.tax.view", admin.Permissions);
        Assert.Contains("company.tax.configure", admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.tax."));
        Assert.Equal(new TaxCapabilities(true, true), await TaxMgmt.CapabilitiesAsync(Admin.Id, Company.Id));
    }

    [PostgresFact]
    public async Task Taxes_keep_code_and_identity_advance_by_revision_and_are_audited_and_never_removed()
    {
        await Prepare();
        var vat = Ok(await Tax.CreateTaxAsync(Admin.Id, Company.Id, new TaxInput(" vat ", "ضريبة القيمة المضافة", null)));
        Assert.Equal(("VAT", 1L, true), (vat.Code, vat.Revision, vat.IsActive));
        Invalid(await Tax.CreateTaxAsync(Admin.Id, Company.Id, new TaxInput("VAT", "مكررة", null)), "code", "code_taken");
        Invalid(await Tax.CreateTaxAsync(Admin.Id, Company.Id, new TaxInput("bad code", "x", null)), "code", "code_invalid");
        Invalid(await Tax.CreateTaxAsync(Admin.Id, Company.Id, new TaxInput("X", "  ", null)), "nameAr", "name_invalid");

        Fails(await Tax.UpdateTaxAsync(Admin.Id, Company.Id, vat.Id, null, new TaxInput(null, "جديد", null)), ConfigStatus.PreconditionRequired, "precondition_required");
        var updated = Ok(await Tax.UpdateTaxAsync(Admin.Id, Company.Id, vat.Id, 1, new TaxInput(null, "جديد", "New", false)));
        Assert.Equal((2L, false, "جديد"), (updated.Revision, updated.IsActive, updated.NameAr));
        var stale = await Tax.UpdateTaxAsync(Admin.Id, Company.Id, vat.Id, 1, new TaxInput(null, "قديم", null));
        Fails(stale, ConfigStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(2L, stale.CurrentRevision);
        Invalid(await Tax.UpdateTaxAsync(Admin.Id, Company.Id, vat.Id, 2, new TaxInput("OTHER", "جديد", null)), "code", "code_immutable");
        Fails(await Tax.UpdateTaxAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, new TaxInput(null, "x", null)), ConfigStatus.NotFound, "tax_not_found");

        Assert.Equal(2L, await TaxEvents());                                                       // created + updated; refusals wrote nothing
        Assert.Equal("P0001", await Fails($"UPDATE {TD.Table} SET {TD.Code} = 'ZZ', {TD.Revision} = 3"));   // the code never changes (trigger)
        Assert.Equal("P0001", await Fails($"DELETE FROM {TD.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {TD.Table} CASCADE"));
        Assert.Equal("P0001", await Fails($"UPDATE {TD.Table} SET {TD.Revision} = 9"));            // revision advances by exactly one
    }

    [PostgresFact]
    public async Task Rate_versions_are_dated_strictly_later_validated_and_append_only()
    {
        await Prepare();
        var vat = await NewTax("VAT", (Jan, TaxTreatment.Standard, 15m));
        var after = Ok(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(Jun, TaxTreatment.Standard, 5m)));
        Assert.Equal([15m, 5m], after.Rates.Select(r => r.RatePercent));
        Assert.Equal([Jan, Jun], after.Rates.Select(r => r.EffectiveFrom));

        Invalid(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(Jun, TaxTreatment.Standard, 6m)), "effectiveFrom", "rate_not_later");                  // same date
        Invalid(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(new DateOnly(2026, 3, 1), TaxTreatment.Standard, 6m)), "effectiveFrom", "rate_not_later");   // between the existing versions: history is never rewritten
        Invalid(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(new DateOnly(2027, 1, 1), TaxTreatment.Exempt, 5m)), "ratePercent", "treatment_rate_mismatch");
        Invalid(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(new DateOnly(2027, 1, 1), TaxTreatment.Standard, 5.1234567m)), "ratePercent", "rate_invalid");
        Invalid(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(new DateOnly(2027, 1, 1), TaxTreatment.Standard, 1000.01m)), "ratePercent", "rate_invalid");
        Invalid(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(new DateOnly(2027, 1, 1), (TaxTreatment)9, 0m)), "treatment", "treatment_invalid");
        Invalid(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(new DateOnly(2062, 1, 1), TaxTreatment.Standard, 5m)), "effectiveFrom", "rate_too_far");   // a typo years ahead would block every later version for good
        Fails(await Tax.AddRateAsync(Admin.Id, Company.Id, Guid.NewGuid(), new TaxRateInput(Jan, TaxTreatment.Standard, 1m)), ConfigStatus.NotFound, "tax_not_found");
        var exempt = Ok(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(new DateOnly(2027, 1, 1), TaxTreatment.Exempt, 0m)));
        Assert.Equal(3, exempt.Rates.Count);

        // PostgreSQL itself: append-only, strictly later, consistent
        Assert.Equal("P0001", await Fails($"UPDATE {TR.Table} SET {TR.RatePercent} = 1"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {TR.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {TR.Table}"));
        Assert.Equal("P0001", await Fails($"INSERT INTO {TR.Table} VALUES (gen_random_uuid(), '{vat.Id}', '2025-01-01', 1, 1, '{Admin.Id}', now())"));          // earlier than an existing version
        Assert.Equal("23514", await Fails($"INSERT INTO {TR.Table} VALUES (gen_random_uuid(), '{vat.Id}', '2030-01-01', 3, 5, '{Admin.Id}', now())"));          // exempt with a rate
        Assert.Equal("23514", await Fails($"INSERT INTO {TR.Table} VALUES (gen_random_uuid(), '{vat.Id}', '2030-01-01', 1, 1000.5, '{Admin.Id}', now())"));
        Assert.Equal(3L, await Count(Company, TR.Table));
    }

    [PostgresFact]
    public async Task Classes_are_separate_by_kind_assignments_replace_and_wrong_kinds_are_refused_by_service_and_database()
    {
        await Prepare();
        var food = await NewClass(TaxClassKind.Item, "food");
        var exporter = await NewClass(TaxClassKind.Party, "exporter");
        Assert.Equal(("FOOD", TaxClassKind.Item, 1L), (food.Code, food.Kind, food.Revision));
        Invalid(await Tax.CreateClassAsync(Admin.Id, Company.Id, new TaxClassInput(TaxClassKind.Item, "FOOD", "مكرر", null)), "code", "code_taken");
        var sameCodeOtherKind = await NewClass(TaxClassKind.Party, "food");                       // codes are unique per kind
        Assert.Equal(TaxClassKind.Party, sameCodeOtherKind.Kind);
        Invalid(await Tax.CreateClassAsync(Admin.Id, Company.Id, new TaxClassInput((TaxClassKind)7, "ZZ", "x", null)), "kind", "kind_invalid");

        var item = await NewItemId("A1");
        var party = await NewPartyId("P1");
        Ok(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, food.Id));
        Ok(await Tax.AssignPartyClassAsync(Admin.Id, Company.Id, party, exporter.Id));
        Invalid(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, exporter.Id), "classId", "class_kind_mismatch");
        Invalid(await Tax.AssignPartyClassAsync(Admin.Id, Company.Id, party, food.Id), "classId", "class_kind_mismatch");
        Fails(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, Guid.NewGuid()), ConfigStatus.NotFound, "class_not_found");
        Fails(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, Guid.NewGuid(), food.Id), ConfigStatus.NotFound, "item_not_found");

        var medicine = await NewClass(TaxClassKind.Item, "medicine");
        Ok(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, medicine.Id));                // replaced, still one row
        Assert.Equal(1L, await Count(Company, TI.Table));
        Assert.Equal(medicine.Id, await Sql<Guid>($"SELECT {TI.ClassId} FROM {TI.Table}"));
        var retired = Ok(await Tax.UpdateClassAsync(Admin.Id, Company.Id, medicine.Id, 1, new TaxClassInput(TaxClassKind.Item, null, "فئة", null, false)));
        Assert.Equal((2L, false), (retired.Revision, retired.IsActive));
        Invalid(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, await NewItemId("A2"), medicine.Id), "classId", "class_inactive");
        Invalid(await Tax.UpdateClassAsync(Admin.Id, Company.Id, medicine.Id, 2, new TaxClassInput(TaxClassKind.Party, null, "فئة", null)), "kind", "kind_immutable");
        Fails(await Tax.UpdateClassAsync(Admin.Id, Company.Id, medicine.Id, 1, new TaxClassInput(TaxClassKind.Item, null, "x", null)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Ok(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, null));
        Assert.Equal(0L, await Count(Company, TI.Table));
        Fails(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, null), ConfigStatus.NotFound, "assignment_not_found");

        // PostgreSQL: a party class cannot be put on an item by any path, and a class kind never changes
        Assert.Equal("23503", await Fails($"INSERT INTO {TI.Table} ({TI.ItemId}, {TI.ClassId}, {TI.AssignedBy}, {TI.AssignedAtUtc}) VALUES ('{item}', '{exporter.Id}', '{Admin.Id}', now())"));
        Assert.Equal("23514", await Fails($"INSERT INTO {TI.Table} ({TI.ItemId}, {TI.ClassId}, {TI.ClassKind}, {TI.AssignedBy}, {TI.AssignedAtUtc}) VALUES ('{item}', '{exporter.Id}', 2, '{Admin.Id}', now())"));
        Assert.Equal("P0001", await Fails($"UPDATE {TC.Table} SET {TC.Kind} = 2, {TC.Revision} = {TC.Revision} + 1 WHERE {TC.Id} = '{food.Id}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {TC.Table}"));
    }

    [PostgresFact]
    public async Task Rules_are_validated_refuse_overlaps_replace_their_base_on_update_and_are_never_removed()
    {
        await Prepare();
        var vat = await NewTax("VAT", (Jan, TaxTreatment.Standard, 15m));
        var excise = await NewTax("EXC", (Jan, TaxTreatment.Standard, 50m));
        var food = await NewClass(TaxClassKind.Item, "food");
        var exporter = await NewClass(TaxClassKind.Party, "exporter");

        var r1 = Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 20, to: new DateOnly(2026, 12, 31), includes: [excise.Id])));
        Assert.Equal((1L, excise.Id), (r1.Revision, Assert.Single(r1.Rule.IncludesTaxIds!)));
        Fails(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 21, from: new DateOnly(2026, 12, 1))), ConfigStatus.Conflict, "rule_overlap");   // same tax and classification, overlapping dates
        Fails(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(excise.Id, 20)), ConfigStatus.Conflict, "duplicate_priority");                            // two taxes could apply to one line at one priority
        Fails(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(excise.Id, 25)), ConfigStatus.Conflict, "invalid_base_reference");                       // VAT (20) includes excise, so excise must come earlier
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 20, from: new DateOnly(2027, 1, 1))));                                               // the next period is fine
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 20, itemClass: food.Id, over: TaxTreatment.ZeroRated)));                             // another classification is fine

        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 0)), "priority", "priority_invalid");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 1001)), "priority", "priority_invalid");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 5, from: Jun, to: Jan)), "validTo", "range_invalid");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 5, over: TaxTreatment.Standard)), "treatmentOverride", "treatment_invalid");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 5, includes: [vat.Id])), "includesTaxIds", "base_invalid");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 5, includes: [excise.Id, excise.Id])), "includesTaxIds", "base_invalid");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 5, includes: [Guid.NewGuid()])), "includesTaxIds", "tax_not_found");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(Guid.NewGuid(), 5)), "taxId", "tax_not_found");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 5, itemClass: exporter.Id)), "itemClassId", "class_kind_mismatch");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 5, partyClass: food.Id)), "partyClassId", "class_kind_mismatch");
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, new TaxRuleInput("Bad Type", Sell, null, null, vat.Id, 5, [], Jan, null)), "documentType", "document_type_invalid");
        Ok(await Tax.UpdateTaxAsync(Admin.Id, Company.Id, excise.Id, 1, new TaxInput(null, "مختلفة", null, false)));
        Invalid(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(excise.Id, 5)), "taxId", "tax_inactive");

        // update: the base set is replaced, the revision is fenced, an update may not overlap another rule
        var upd = Ok(await Tax.UpdateRuleAsync(Admin.Id, Company.Id, r1.Rule.Id, 1, Rule(vat.Id, 30, to: new DateOnly(2026, 12, 31))));
        Assert.Equal((2L, 30, 0), (upd.Revision, upd.Rule.Priority, upd.Rule.IncludesTaxIds!.Count));
        Fails(await Tax.UpdateRuleAsync(Admin.Id, Company.Id, r1.Rule.Id, 1, Rule(vat.Id, 31, to: new DateOnly(2026, 12, 31))), ConfigStatus.PreconditionFailed, "revision_conflict");
        Fails(await Tax.UpdateRuleAsync(Admin.Id, Company.Id, r1.Rule.Id, 2, Rule(vat.Id, 30, to: new DateOnly(2027, 6, 1))), ConfigStatus.Conflict, "rule_overlap");
        Fails(await Tax.UpdateRuleAsync(Admin.Id, Company.Id, r1.Rule.Id, null, Rule(vat.Id, 30)), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Tax.UpdateRuleAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, Rule(vat.Id, 30)), ConfigStatus.NotFound, "rule_not_found");
        Assert.Equal(3, Ok(await TaxMgmt.ListRulesAsync(Admin.Id, Company.Id)).Count);
        Assert.Equal("P0001", await Fails($"DELETE FROM {TU.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {TU.Table} SET {TU.Id} = gen_random_uuid(), {TU.Revision} = {TU.Revision} + 1"));
        Assert.Equal("23514", await Fails($"UPDATE {TU.Table} SET {TU.TreatmentOverride} = 1, {TU.Revision} = {TU.Revision} + 1"));
        Assert.Equal("23503", await Fails($"INSERT INTO {TU.Table} ({TU.Id}, {TU.DocumentType}, {TU.Operation}, {TU.TaxId}, {TU.Priority}, {TU.ValidFrom}, {TU.CreatedBy}, {TU.CreatedAtUtc}, {TU.UpdatedAtUtc}) VALUES (gen_random_uuid(), 'sales_invoice', 'sale', gen_random_uuid(), 1, '2026-01-01', '{Admin.Id}', now(), now())"));
    }

    [PostgresFact]
    public async Task Rounding_policy_versions_are_append_only_and_validated()
    {
        await Prepare();
        Invalid(await Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(7, TaxRoundingMode.HalfEven, TaxRoundingLevel.PerLine)), "moneyDecimals", "decimals_invalid");
        Invalid(await Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, (TaxRoundingMode)5, TaxRoundingLevel.PerLine)), "mode", "mode_invalid");
        Invalid(await Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfEven, (TaxRoundingLevel)0)), "level", "level_invalid");
        var v1 = Ok(await Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerLine)));
        var v2 = Ok(await Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(3, TaxRoundingMode.HalfEven, TaxRoundingLevel.PerDocument)));
        Assert.Equal((1, 2), (v1.Version, v2.Version));
        Assert.Equal(v2, Ok(await TaxMgmt.GetPolicyAsync(Admin.Id, Company.Id)));
        Assert.Equal(new TaxPolicy(3, TaxRoundingMode.HalfEven, TaxRoundingLevel.PerDocument), v2.Policy);
        Assert.Equal(2L, await Count(Company, TV.Table));
        Assert.Equal("P0001", await Fails($"UPDATE {TV.Table} SET {TV.MoneyDecimals} = 0"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {TV.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {TV.Table}"));
        Assert.Equal("23514", await Fails($"INSERT INTO {TV.Table} VALUES (gen_random_uuid(), 9, 2, 3, 1, '{Admin.Id}', now())"));
        Assert.Equal("23505", await Fails($"INSERT INTO {TV.Table} VALUES (gen_random_uuid(), 2, 2, 1, 1, '{Admin.Id}', now())"));                       // a version number is unique
    }

    [PostgresFact]
    public async Task Resolution_applies_dated_rates_classifications_overrides_and_compound_bases_and_feeds_the_engine()
    {
        await Prepare();
        var vat = await NewTax("VAT", (Jan, TaxTreatment.Standard, 15m), (Jun, TaxTreatment.Standard, 5m));
        var excise = await NewTax("EXC", (Jan, TaxTreatment.Standard, 10m));
        var food = await NewClass(TaxClassKind.Item, "food");
        var exporter = await NewClass(TaxClassKind.Party, "exporter");
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(excise.Id, 10)));
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 20, includes: [excise.Id])));                                // compound: VAT on net + excise
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 20, partyClass: exporter.Id, over: TaxTreatment.ZeroRated)));
        var plain = await NewItemId("PLAIN");
        var taxedFood = await NewItemId("FOOD");
        Ok(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, taxedFood, food.Id));
        var customer = await NewPartyId("CUST");
        var export = await NewPartyId("EXPORT");
        Ok(await Tax.AssignPartyClassAsync(Admin.Id, Company.Id, export, exporter.Id));

        Fails(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, plain, customer, Jan), ConfigStatus.Conflict, "no_tax_policy");
        Ok(await Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerLine)));

        var early = Ok(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, plain, customer, new DateOnly(2026, 3, 15)));
        Assert.Equal(1, early.PolicyVersion);
        Assert.Equal([("EXC", 10m, 0), ("VAT", 15m, 1)], early.Components.Select(c => (c.Code, c.RatePercent, c.IncludesInBase!.Count)));
        var late = Ok(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, plain, customer, new DateOnly(2026, 7, 1)));
        Assert.Equal(5m, late.Components.Single(c => c.Code == "VAT").RatePercent);                                                 // the dated version in force
        Assert.Equal(15m, Ok(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, plain, null, new DateOnly(2026, 5, 31))).Components.Single(c => c.Code == "VAT").RatePercent);

        var zero = Ok(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, plain, export, new DateOnly(2026, 7, 1)));
        var zeroVat = zero.Components.Single(c => c.Code == "VAT");
        Assert.Equal((TaxTreatment.ZeroRated, 0m), (zeroVat.Treatment, zeroVat.RatePercent));
        Assert.StartsWith("rule:", zeroVat.RuleRef);

        // the engine takes what the resolver returned: net 100.00 + excise 10.00 + VAT 15% of 110.00 = 16.50
        var result = TaxEngine.Calculate(early.Policy, [new TaxLineInput(1, 1m, 100m, 0m, false, early.Components)]);
        Assert.Equal([("EXC", 10.00m), ("VAT", 16.50m)], result.Lines[0].Taxes.Select(t => (t.Code, t.Amount)));
        Assert.Equal(126.50m, result.Lines[0].Gross);

        // different document type or operation: no rule, no tax
        Assert.Empty(Ok(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, "purchase_invoice", Sell, plain, customer, Jan)).Components);
        Fails(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, "BAD TYPE", Sell, plain, customer, Jan), ConfigStatus.Invalid, "invalid_tax_context");
        // before the rules apply nothing is taxed; a rule that applies before the first dated rate of its tax is refused, not guessed
        Assert.Empty(Ok(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, plain, customer, new DateOnly(2025, 12, 31))).Components);
        var late2 = await NewTax("LATE", (Jan, TaxTreatment.Standard, 1m));
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(late2.Id, 5, from: new DateOnly(2025, 1, 1))));
        Fails(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, plain, customer, new DateOnly(2025, 12, 31)), ConfigStatus.Conflict, "no_tax_rate");
        _ = taxedFood;
    }

    [PostgresFact]
    public async Task Resolution_refuses_an_ambiguous_rule_set_and_an_inactive_tax_instead_of_guessing()
    {
        await Prepare();
        var vat = await NewTax("VAT", (Jan, TaxTreatment.Standard, 15m));
        var food = await NewClass(TaxClassKind.Item, "food");
        var exporter = await NewClass(TaxClassKind.Party, "exporter");
        Ok(await Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfEven, TaxRoundingLevel.PerLine)));
        var item = await NewItemId("AMB"); var party = await NewPartyId("AMB");
        Ok(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, food.Id));
        Ok(await Tax.AssignPartyClassAsync(Admin.Id, Company.Id, party, exporter.Id));
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 10, itemClass: food.Id)));
        // an item-only and a party-only rule for the same tax are equally specific: refused when written, so no document can ever hit the ambiguity
        Fails(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 10, partyClass: exporter.Id, over: TaxTreatment.Exempt)), ConfigStatus.Conflict, "rule_ambiguous");
        Assert.Single(Ok(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, item, party, Jan)).Components);
        var inactive = Ok(await Tax.UpdateTaxAsync(Admin.Id, Company.Id, vat.Id, 1, new TaxInput(null, "ضريبة", null, false)));
        Assert.False(inactive.IsActive);
        Ok(await Tax.UpdateTaxAsync(Admin.Id, Company.Id, vat.Id, 2, new TaxInput(null, "ضريبة ٢", null)));                          // an omitted flag never reactivates
        Assert.False((await Tax.GetTaxAsync(Company.Id, vat.Id))!.IsActive);
        Fails(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, item, null, Jan), ConfigStatus.Conflict, "tax_inactive");
        // the way out: the rule of the inactive tax can be switched off (but not newly activated)
        var rule = Assert.Single(Ok(await TaxMgmt.ListRulesAsync(Admin.Id, Company.Id)));
        Invalid(await Tax.UpdateRuleAsync(Admin.Id, Company.Id, rule.Rule.Id, rule.Revision, Rule(vat.Id, 10, itemClass: food.Id)), "taxId", "tax_inactive");
        Ok(await Tax.UpdateRuleAsync(Admin.Id, Company.Id, rule.Rule.Id, rule.Revision, Rule(vat.Id, 10, itemClass: food.Id) with { IsActive = false }));
        Assert.Empty(Ok(await TaxMgmt.ResolveAsync(Admin.Id, Company.Id, Sales, Sell, item, null, Jan)).Components);
    }

    [PostgresFact]
    public async Task Without_the_permission_nothing_is_read_or_written_and_nothing_is_audited()
    {
        await Prepare();
        var outsider = await NewUser("taxoutsider");
        var before = await TaxEvents();
        Fails(await TaxMgmt.ListTaxesAsync(outsider.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await TaxMgmt.ListRulesAsync(outsider.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await TaxMgmt.GetPolicyAsync(outsider.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await TaxMgmt.CreateTaxAsync(outsider.Id, Company.Id, new TaxInput("VAT", "x", null)), ConfigStatus.Forbidden, "forbidden");
        Fails(await TaxMgmt.AddPolicyAsync(outsider.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfEven, TaxRoundingLevel.PerLine)), ConfigStatus.Forbidden, "forbidden");
        Fails(await TaxMgmt.ResolveAsync(outsider.Id, Company.Id, Sales, Sell, null, null, Jan), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(new TaxCapabilities(false, false), await TaxMgmt.CapabilitiesAsync(outsider.Id, Company.Id));
        Assert.Equal((0L, before), (await Count(Company, TD.Table), await TaxEvents()));
    }

    [PostgresFact]
    public async Task Concurrent_rate_versions_for_one_tax_are_ordered_and_a_policy_race_never_duplicates_a_version()
    {
        await Prepare();
        var vat = await NewTax("VAT", (Jan, TaxTreatment.Standard, 15m));
        var starts = new[] { new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 1) };
        var results = await Task.WhenAll(starts.Select(d => Task.Run(() => Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(d, TaxTreatment.Standard, 10m)))));
        Assert.All(results, r => Assert.True(r.IsOk || r.Fields!.Any(f => f.Code == "rate_not_later")));                                                // each either wins its turn or is refused as not later
        var rates = (await Tax.GetTaxAsync(Company.Id, vat.Id))!.Rates;
        Assert.Equal(rates.OrderBy(r => r.EffectiveFrom).Select(r => r.EffectiveFrom), rates.Select(r => r.EffectiveFrom));
        Assert.Equal(rates.Count, rates.Select(r => r.EffectiveFrom).Distinct().Count());
        Assert.Equal(1 + results.Count(r => r.IsOk), rates.Count);

        var policies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfEven, TaxRoundingLevel.PerLine)))));
        Assert.All(policies, r => Assert.True(r.IsOk || r.Code == "version_conflict"));
        var versions = await Sql<long>($"SELECT count(DISTINCT {TV.Version}) FROM {TV.Table}");
        Assert.Equal(await Count(Company, TV.Table), versions);
        Assert.Equal(policies.Count(r => r.IsOk), (int)versions);
    }

    [PostgresFact]
    public async Task A_rule_base_changes_only_with_its_rule_removals_record_the_previous_class_and_the_rate_lead_is_configurable()
    {
        await Prepare();
        var vat = await NewTax("VAT", (Jan, TaxTreatment.Standard, 15m));
        var excise = await NewTax("EXC", (Jan, TaxTreatment.Standard, 5m));
        var rule = Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, Rule(vat.Id, 20, includes: [excise.Id])));
        Assert.Equal(1L, await Count(Company, TB.Table));
        // outside the service nobody can edit a base behind the revision fence: the rule row was not written by that transaction
        Assert.Equal("P0001", await Fails($"DELETE FROM {TB.Table} WHERE {TB.RuleId} = '{rule.Rule.Id}'"));
        Assert.Equal("P0001", await Fails($"INSERT INTO {TB.Table} ({TB.RuleId}, {TB.IncludedTaxId}) VALUES ('{rule.Rule.Id}', '{vat.Id}')"));
        Assert.Equal(1L, await Count(Company, TB.Table));
        // through the service the base is replaced together with the revision advance, and the audit names both bases
        var updated = Ok(await Tax.UpdateRuleAsync(Admin.Id, Company.Id, rule.Rule.Id, rule.Revision, Rule(vat.Id, 20)));
        Assert.Equal((2L, 0), (updated.Revision, updated.Rule.IncludesTaxIds.Count));
        var detail = await Sql<string>($"SELECT {Audit_.Detail} FROM {Audit_.Table} WHERE {Audit_.EventType} = {TaxAuditEvents.RuleUpdated} ORDER BY {Audit_.Seq} DESC LIMIT 1");
        Assert.Contains("was=" + excise.Id.ToString("N")[..8], detail);
        Assert.Contains("base= was=", detail);

        // removing or replacing an assignment audits the class it had
        var food = await NewClass(TaxClassKind.Item, "food");
        var item = await NewItemId("B1");
        Ok(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, food.Id));
        Ok(await Tax.AssignItemClassAsync(Admin.Id, Company.Id, item, null));
        var removed = await Sql<string>($"SELECT {Audit_.Detail} FROM {Audit_.Table} WHERE {Audit_.EventType} = {TaxAuditEvents.ItemClassAssigned} ORDER BY {Audit_.Seq} DESC LIMIT 1");
        Assert.Contains("removed", removed);
        Assert.Contains("previous=" + food.Id.ToString("N"), removed);

        // the lead limit is a deployment policy: default one year, configurable, 0 = none
        var far = new DateOnly(2062, 1, 1);
        Invalid(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(far, TaxTreatment.Standard, 5m)), "effectiveFrom", "rate_too_far");
        var wide = new TaxConfigService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock, new TaxConfigOptions(0));
        Ok(await wide.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(far, TaxTreatment.Standard, 5m)));
        var narrow = new TaxConfigService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock, new TaxConfigOptions(30));
        Invalid(await narrow.AddRateAsync(Admin.Id, Company.Id, excise.Id, new TaxRateInput(DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime).AddDays(60), TaxTreatment.Standard, 5m)), "effectiveFrom", "rate_too_far");
    }
}
