using Hasm.Infrastructure.Taxes;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.10 slice 2a: the pure rule resolver (SRS 6.1/6.2): dated rates, classifications, specificity, priority, compound bases, overrides and every refusal.</summary>
public class TaxResolverTests
{
    private static readonly Guid Vat = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Lux = Guid.Parse("00000000-0000-0000-0000-0000000000a2"), Fee = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Medicine = Guid.Parse("00000000-0000-0000-0000-0000000000c1"), Food = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid Diplomat = Guid.Parse("00000000-0000-0000-0000-0000000000d1"), Retail = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    private static readonly DateOnly D20 = new(2026, 3, 1);

    private static TaxDefinitionRecord Def(Guid id, string code, bool active = true) => new(id, code, active);
    private static TaxRateRecord Rate(Guid tax, string from, decimal pct, TaxTreatment t = TaxTreatment.Standard) => new(tax, DateOnly.Parse(from), t, pct);
    private static int ruleSeed;
    private static TaxRuleRecord Rule(Guid tax, int priority, Guid? item = null, Guid? party = null, string doc = "invoice", string op = "sale", string from = "2020-01-01", string? to = null,
        bool active = true, TaxTreatment? over = null, params Guid[] includes) =>
        new(Guid.Parse($"00000000-0000-0000-0000-00000000{++ruleSeed:0000}"), doc, op, item, party, tax, priority, includes, DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to), active, over);
    private static TaxContext Ctx(Guid? item = null, Guid? party = null, string date = "2026-03-01", string doc = "invoice", string op = "sale") => new(doc, op, item, party, DateOnly.Parse(date));
    private static readonly TaxDefinitionRecord[] Defs = [Def(Vat, "VAT"), Def(Lux, "LUX"), Def(Fee, "FEE")];
    private static IReadOnlyList<TaxComponent> Resolve(TaxContext c, TaxRateRecord[] rates, params TaxRuleRecord[] rules) => TaxResolver.Resolve(c, Defs, rates, rules);
    private static string Refusal(TaxContext c, TaxRateRecord[] rates, params TaxRuleRecord[] rules) => Assert.Throws<TaxException>(() => TaxResolver.Resolve(c, Defs, rates, rules)).Code;

    [Fact]
    public void A_matching_rule_yields_the_component_with_the_dated_rate_and_a_traceable_reference()
    {
        var rule = Rule(Vat, 1);
        var component = Assert.Single(Resolve(Ctx(), [Rate(Vat, "2020-01-01", 15m)], rule));
        Assert.Equal((Vat, "VAT", TaxTreatment.Standard, 15m, 1), (component.TaxId, component.Code, component.Treatment, component.RatePercent, component.Priority));
        Assert.Equal($"rule:{rule.Id:N};rate:{Vat:N}@2020-01-01", component.RuleRef);
    }

    [Fact]
    public void The_rate_in_force_on_the_tax_date_is_used_and_a_date_before_the_first_version_is_refused()
    {
        var rates = new[] { Rate(Vat, "2026-01-01", 5m), Rate(Vat, "2026-07-01", 15m) };
        var rule = Rule(Vat, 1);
        Assert.Equal(5m, Resolve(Ctx(date: "2026-06-30"), rates, rule)[0].RatePercent);
        Assert.Equal(15m, Resolve(Ctx(date: "2026-07-01"), rates, rule)[0].RatePercent);
        Assert.Equal(15m, Resolve(Ctx(date: "2030-01-01"), rates, rule)[0].RatePercent);
        Assert.Equal("no_tax_rate", Refusal(Ctx(date: "2025-12-31"), rates, rule));
        Assert.Contains("@2026-01-01", Resolve(Ctx(date: "2026-06-30"), rates, rule)[0].RuleRef!);       // the reference names the version actually used
    }

    [Fact]
    public void Only_active_dated_rules_of_the_same_document_type_and_operation_apply()
    {
        var rates = new[] { Rate(Vat, "2020-01-01", 15m) };
        Assert.Empty(Resolve(Ctx(), rates, Rule(Vat, 1, active: false)));
        Assert.Empty(Resolve(Ctx(date: "2026-03-01"), rates, Rule(Vat, 1, to: "2026-02-28")));          // expired
        Assert.Empty(Resolve(Ctx(date: "2019-12-31"), rates, Rule(Vat, 1, from: "2020-01-01")));        // not yet valid
        Assert.Single(Resolve(Ctx(date: "2026-02-28"), rates, Rule(Vat, 1, to: "2026-02-28")));         // the end date is inclusive
        Assert.Empty(Resolve(Ctx(doc: "credit-note"), rates, Rule(Vat, 1)));
        Assert.Empty(Resolve(Ctx(op: "purchase"), rates, Rule(Vat, 1)));
    }

    [Fact]
    public void Item_and_party_classifications_narrow_a_rule_and_null_means_any()
    {
        var rates = new[] { Rate(Vat, "2020-01-01", 15m) };
        var rule = Rule(Vat, 1, item: Food);
        Assert.Single(Resolve(Ctx(item: Food), rates, rule));
        Assert.Empty(Resolve(Ctx(item: Medicine), rates, rule));
        Assert.Empty(Resolve(Ctx(item: null), rates, rule));                                            // an unclassified item does not match a classified rule
        Assert.Single(Resolve(Ctx(item: Medicine, party: Retail), rates, Rule(Vat, 1)));                // an unconstrained rule matches anything
    }

    [Fact]
    public void The_most_specific_rule_of_a_tax_wins_and_an_override_models_an_exemption_or_zero_rating()
    {
        var rates = new[] { Rate(Vat, "2020-01-01", 15m) };
        var general = Rule(Vat, 1);
        var medicine = Rule(Vat, 1, item: Medicine, over: TaxTreatment.ZeroRated);
        var diplomatMedicine = Rule(Vat, 1, item: Medicine, party: Diplomat, over: TaxTreatment.Exempt);
        Assert.Equal((TaxTreatment.Standard, 15m), Pair(Resolve(Ctx(item: Food), rates, general, medicine, diplomatMedicine)));
        Assert.Equal((TaxTreatment.ZeroRated, 0m), Pair(Resolve(Ctx(item: Medicine, party: Retail), rates, general, medicine, diplomatMedicine)));
        Assert.Equal((TaxTreatment.Exempt, 0m), Pair(Resolve(Ctx(item: Medicine, party: Diplomat), rates, general, medicine, diplomatMedicine)));   // two constrained dimensions beat one
        static (TaxTreatment, decimal) Pair(IReadOnlyList<TaxComponent> c) => (c[0].Treatment, c[0].RatePercent);
    }

    [Fact]
    public void Equally_specific_candidates_for_one_tax_are_ambiguous_and_never_guessed()
    {
        var rates = new[] { Rate(Vat, "2020-01-01", 15m) };
        Assert.Equal("ambiguous_tax_rule", Refusal(Ctx(item: Food, party: Retail), rates, Rule(Vat, 1, item: Food), Rule(Vat, 1, party: Retail)));   // item-only against party-only
        Assert.Equal("ambiguous_tax_rule", Refusal(Ctx(), rates, Rule(Vat, 1), Rule(Vat, 2)));                                                       // two general rules
        Assert.Single(Resolve(Ctx(item: Food), rates, Rule(Vat, 1, item: Food), Rule(Vat, 1, party: Retail)));                                       // the party rule does not match here, so no tie
    }

    [Fact]
    public void Several_taxes_are_ordered_by_priority_and_a_compound_base_names_earlier_applicable_taxes_only()
    {
        var rates = new[] { Rate(Vat, "2020-01-01", 15m), Rate(Lux, "2020-01-01", 10m), Rate(Fee, "2020-01-01", 2m) };
        var rules = new[] { Rule(Fee, 3, includes: [Vat, Lux]), Rule(Vat, 2, includes: [Lux]), Rule(Lux, 1, item: Food) };
        var food = Resolve(Ctx(item: Food), rates, rules);
        Assert.Equal(new[] { "LUX", "VAT", "FEE" }, food.Select(c => c.Code));
        Assert.Equal(new[] { Lux }, food[1].IncludesInBase);
        Assert.Equal(new[] { Vat, Lux }, food[2].IncludesInBase);
        var other = Resolve(Ctx(item: Medicine), rates, rules);                                         // Lux does not apply: it is simply not part of the base
        Assert.Equal(new[] { "VAT", "FEE" }, other.Select(c => c.Code));
        Assert.Empty(other[0].IncludesInBase!); Assert.Equal(new[] { Vat }, other[1].IncludesInBase);
    }

    [Fact]
    public void A_base_reference_to_a_later_or_the_same_tax_is_refused_and_the_result_feeds_the_engine()
    {
        var rates = new[] { Rate(Vat, "2020-01-01", 15m), Rate(Lux, "2020-01-01", 10m) };
        Assert.Equal("invalid_base_reference", Refusal(Ctx(), rates, Rule(Vat, 1, includes: [Lux]), Rule(Lux, 2)));      // Lux is applied AFTER Vat
        Assert.Equal("invalid_base_reference", Refusal(Ctx(), rates, Rule(Vat, 1, includes: [Vat])));
        // resolved components are valid engine input: net 100, Lux 10 %, Vat 15 % on net + Lux
        var components = Resolve(Ctx(), rates, Rule(Lux, 1), Rule(Vat, 2, includes: [Lux]));
        var result = TaxEngine.Calculate(new TaxPolicy(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerLine), [new TaxLineInput(1, 1m, 100m, 0m, false, components)]);
        Assert.Equal(new[] { 10m, 16.5m }, result.Lines[0].Taxes.Select(t => t.Amount));
        var snapshot = TaxSnapshots.Create(new TaxPolicy(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerLine), [new TaxLineInput(1, 1m, 100m, 0m, false, components)]);
        Assert.True(TaxSnapshots.Verify(snapshot));
        Assert.All(snapshot.Lines[0].Taxes, t => Assert.StartsWith("rule:", t.RuleRef));                    // the rule/rate versions are sealed in the snapshot
    }

    [Fact]
    public void Inconsistent_company_data_is_refused()
    {
        var rates = new[] { Rate(Vat, "2020-01-01", 15m) };
        Assert.Equal("tax_inactive", Assert.Throws<TaxException>(() => TaxResolver.Resolve(Ctx(), [Def(Vat, "VAT", active: false)], rates, [Rule(Vat, 1)])).Code);
        Assert.Equal("unknown_tax", Assert.Throws<TaxException>(() => TaxResolver.Resolve(Ctx(), [], rates, [Rule(Vat, 1)])).Code);
        Assert.Equal("invalid_tax_data", Refusal(Ctx(), [Rate(Vat, "2020-01-01", 15m), Rate(Vat, "2020-01-01", 16m)], Rule(Vat, 1)));      // two versions from the same date
        Assert.Equal("invalid_tax_data", Refusal(Ctx(), rates, Rule(Vat, 1, over: TaxTreatment.Standard)));                                 // Standard is not an override
        Assert.Equal("duplicate_priority", Refusal(Ctx(), [Rate(Vat, "2020-01-01", 15m), Rate(Lux, "2020-01-01", 5m)], Rule(Vat, 1), Rule(Lux, 1)));
        Assert.Equal("invalid_tax_context", Assert.Throws<TaxException>(() => TaxResolver.Resolve(Ctx(doc: " "), Defs, rates, [Rule(Vat, 1)])).Code);
        Assert.Equal("invalid_tax_data", Assert.Throws<TaxException>(() => TaxResolver.Resolve(Ctx(), [Def(Vat, "VAT"), Def(Vat, "VAT")], rates, [Rule(Vat, 1)])).Code);
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_of_the_input_collections()
    {
        var rates = new[] { Rate(Vat, "2020-01-01", 15m), Rate(Lux, "2020-01-01", 10m), Rate(Vat, "2026-02-01", 16m) };
        var rules = new[] { Rule(Vat, 2, includes: [Lux]), Rule(Lux, 1), Rule(Vat, 2, item: Medicine, over: TaxTreatment.Exempt, includes: [Lux]) };
        var forward = Resolve(Ctx(item: Medicine), rates, rules);
        var reversed = Resolve(Ctx(item: Medicine), rates.Reverse().ToArray(), rules.Reverse().ToArray());
        Assert.Equal(forward.Select(c => (c.TaxId, c.Treatment, c.RatePercent, c.Priority, c.RuleRef)), reversed.Select(c => (c.TaxId, c.Treatment, c.RatePercent, c.Priority, c.RuleRef)));
        Assert.Equal(TaxTreatment.Exempt, forward[1].Treatment);
    }
}
