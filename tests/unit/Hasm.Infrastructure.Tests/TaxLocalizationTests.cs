using Hasm.Infrastructure.Taxes;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.10 slice 4: the versioned localization extension point. It may only add classifications and rule templates and validate; it can never alter a resolution.</summary>
public class TaxLocalizationTests
{
    private sealed class Fake(string id, int version, TaxLocalizationContribution? add = null, Func<TaxCalculationContext, IReadOnlyList<TaxLocalizationProblem>>? validate = null) : ITaxLocalization
    {
        public string Id => id;
        public int Version => version;
        public TaxLocalizationContribution Contribute() => add ?? TaxLocalizationContribution.Empty;
        public IReadOnlyList<TaxLocalizationProblem> Validate(TaxCalculationContext c) => validate?.Invoke(c) ?? [];
    }

    private static TaxCalculationContext Ctx() => new("sales_invoice", "sale", new DateOnly(2026, 1, 1), null, null,
        new TaxResolution([new TaxComponent(Guid.NewGuid(), "VAT", TaxTreatment.Standard, 15m, 10)], new TaxPolicy(2, TaxRoundingMode.HalfEven, TaxRoundingLevel.PerLine), 1));

    [Fact]
    public void An_empty_host_is_valid_and_changes_nothing()
    {
        var host = new TaxLocalizationHost();
        Assert.Empty(host.Registered);
        Assert.Empty(host.Validate(Ctx()));
        var (classes, rules, rejected) = host.Additions(new HashSet<string>(), new HashSet<string>());
        Assert.Empty(classes); Assert.Empty(rules); Assert.Empty(rejected);
    }

    [Fact]
    public void Registrations_need_a_valid_id_a_positive_version_and_are_unique_and_ordered()
    {
        Assert.Throws<ArgumentException>(() => new TaxLocalizationHost([new Fake("X", 1)]));                 // uppercase / too short
        Assert.Throws<ArgumentException>(() => new TaxLocalizationHost([new Fake("xx-vat", 0)]));
        Assert.Throws<ArgumentException>(() => new TaxLocalizationHost([new Fake("xx-vat", 1), new Fake("xx-vat", 2)]));
        var host = new TaxLocalizationHost([new Fake("zz-tax", 3), new Fake("aa-tax", 1)]);
        Assert.Equal([("aa-tax", 1), ("zz-tax", 3)], host.Registered);
    }

    [Fact]
    public void Validation_problems_are_namespaced_and_refuse_but_a_throwing_localization_fails_closed()
    {
        var host = new TaxLocalizationHost([
            new Fake("aa-tax", 1, validate: _ => [new TaxLocalizationProblem("missing_registration", "partyId")]),
            new Fake("bb-tax", 1, validate: _ => throw new InvalidOperationException("boom")),
            new Fake("cc-tax", 1)]);
        Assert.Equal(["aa-tax:missing_registration", "bb-tax:validation_failed"], host.Validate(Ctx()));
    }

    [Fact]
    public void A_localization_cannot_alter_the_resolution_it_validates()
    {
        var ctx = Ctx();
        var host = new TaxLocalizationHost([new Fake("ev-il", 1, validate: c =>
        {
            ((List<TaxComponent>)c.Resolution.Components)[0] = c.Resolution.Components[0] with { RatePercent = 0m };   // mutates ITS copy only
            return [];
        })]);
        Assert.Empty(host.Validate(ctx));
        Assert.Equal(15m, ctx.Resolution.Components[0].RatePercent);                                                   // the engine input is untouched
    }

    [Fact]
    public void Additions_never_replace_an_existing_class_or_rule_and_malformed_proposals_are_reported()
    {
        var add = new TaxLocalizationContribution(
            [new TaxClassProposal(TaxClassKind.Item, "food", "غذاء", null), new TaxClassProposal(TaxClassKind.Item, "NEW", "جديد", "New"), new TaxClassProposal(TaxClassKind.Party, "", "x", null)],
            [new TaxRuleProposal("sales_invoice", "sale", "VAT", "NEW", null, 10), new TaxRuleProposal("", "sale", "VAT", null, null, 10)]);
        var host = new TaxLocalizationHost([new Fake("xx-vat", 1, add), new Fake("yy-vat", 1, new TaxLocalizationContribution([new TaxClassProposal(TaxClassKind.Item, "new", "مكرر", null)], []))]);
        var (classes, rules, rejected) = host.Additions(new HashSet<string> { "FOOD" }, new HashSet<string>());
        Assert.Equal(["NEW"], classes.Select(c => c.Code));
        Assert.Single(rules);
        Assert.Equal(["xx-vat:class_exists:food", "xx-vat:class_invalid", "xx-vat:rule_invalid", "yy-vat:class_exists:new"], rejected);
    }

    [Fact]
    public void A_throwing_contribution_is_isolated_and_the_extension_point_exposes_no_arithmetic()
    {
        var bad = new Fake("bad-one", 1);
        var host = new TaxLocalizationHost([new ThrowingContribution(), bad]);
        Assert.Equal(["throw-it:contribution_failed"], host.Additions(new HashSet<string>(), new HashSet<string>()).Rejected);
        // by construction: the interface returns only problems and proposals, never components, rates or amounts
        var members = typeof(ITaxLocalization).GetMethods().Select(m => m.ReturnType.Name).Concat(typeof(ITaxLocalization).GetProperties().Select(p => p.PropertyType.Name)).OrderBy(x => x);
        Assert.Equal(new[] { "IReadOnlyList`1", "Int32", "String", "TaxLocalizationContribution" }.OrderBy(x => x, StringComparer.Ordinal), members.Distinct().OrderBy(x => x, StringComparer.Ordinal));
    }

    private sealed class ThrowingContribution : ITaxLocalization
    {
        public string Id => "throw-it";
        public int Version => 1;
        public TaxLocalizationContribution Contribute() => throw new InvalidOperationException();
        public IReadOnlyList<TaxLocalizationProblem> Validate(TaxCalculationContext context) => [];
    }
}
