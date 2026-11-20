using Hasm.Infrastructure.Pricing;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.11, owner decision P: company-configurable price-list and discount precedence. The defaults reproduce slice 1 exactly (and keep v1 snapshots and their hash); a non-default policy is traced and sealed (v2).</summary>
public class PricingPolicyTests
{
    private static readonly Guid Item = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Unit = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Cust = Guid.Parse("00000000-0000-0000-0000-0000000000d1"), Group = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly DateOnly D = new(2026, 6, 1), Jan = new(2026, 1, 1);
    private static int n = 9000;

    private static readonly PricingPolicy Default = new(2, 2);
    private static readonly PricingPolicy ByPriority = new(2, 2, ListPrecedence: PriceListPrecedence.PriorityThenScope, DiscountPrecedence: DiscountPrecedence.PriorityThenSpecificity);

    private static PriceListRecord List(string code, int priority, Guid? cust = null, Guid? group = null) =>
        new(Guid.Parse($"00000000-0000-0000-0004-{++n:D12}"), code, priority, "JOD", Jan, null, true, cust, group);
    private static PriceEntryRecord Entry(PriceListRecord l, decimal price) => new(l.Id, Item, null, Unit, 0, price);
    private static DiscountRuleRecord Disc(string code, int priority, decimal percent, Guid? cust = null, Guid? group = null) =>
        new(Guid.Parse($"00000000-0000-0000-0005-{++n:D12}"), code, priority, Jan, null, true, percent, 0, cust, group);
    private static PricingRequest Req() => new(D, "JOD", Item, null, Unit, 1m, Cust, Group);
    private static void Same(ResolvedPricing a, ResolvedPricing b)
    {
        Assert.Equal((a.ListUnitPrice, a.PriceRuleRef, a.DiscountPercent, a.DiscountRuleRef), (b.ListUnitPrice, b.PriceRuleRef, b.DiscountPercent, b.DiscountRuleRef));
        Assert.True(a.Trace.SequenceEqual(b.Trace));                                                          // the arrays differ by reference; the steps are records
    }

    private static string Refusal(Action a) => Assert.Throws<PricingException>(a).Code;

    [Fact]
    public void Price_list_precedence_default_is_scope_first_and_priority_first_is_configurable()
    {
        var customer = List("CUST", 99, cust: Cust);
        var general = List("GEN", 1);
        var entries = new[] { Entry(customer, 8m), Entry(general, 10m) };
        var byDefault = PricingEngine.Resolve(Req(), [customer, general], entries, [], Default);
        Assert.Equal(8m, byDefault.ListUnitPrice);                                                            // the customer list wins although its priority number is higher
        Same(byDefault, PricingEngine.Resolve(Req(), [customer, general], entries, []));              // no policy = the defaults of slice 1
        var byPriority = PricingEngine.Resolve(Req(), [customer, general], entries, [], ByPriority);
        Assert.Equal(10m, byPriority.ListUnitPrice);                                                          // priority first: the general list (priority 1) wins
        Assert.Contains(byPriority.Trace, t => t is { Kind: "policy", RuleRef: "list-precedence:priority-then-scope" });
        Assert.DoesNotContain(byDefault.Trace, t => t.Kind == "policy");

        // equal priority: scope breaks the tie in the priority-first mode
        var group = List("GRP", 5, group: Group); var general5 = List("GEN5", 5);
        var tie = PricingEngine.Resolve(Req(), [general5, group], [Entry(group, 7m), Entry(general5, 9m)], [], ByPriority);
        Assert.Equal(7m, tie.ListUnitPrice);
    }

    [Fact]
    public void Discount_precedence_default_is_specificity_first_and_priority_first_is_configurable_and_never_stacks()
    {
        var list = List("L", 1); var entries = new[] { Entry(list, 100m) };
        var customerRule = Disc("CUSTOMER-RULE", 50, 5m, cust: Cust);
        var generalRule = Disc("GENERAL-RULE", 1, 20m);
        var byDefault = PricingEngine.Resolve(Req(), [list], entries, [generalRule, customerRule], Default);
        Assert.Equal(5m, byDefault.DiscountPercent);                                                          // customer specificity before priority
        var byPriority = PricingEngine.Resolve(Req(), [list], entries, [generalRule, customerRule], ByPriority);
        Assert.Equal(20m, byPriority.DiscountPercent);                                                        // priority first
        Assert.Single(byPriority.Trace, t => t is { Kind: "discount", Outcome: "selected" });                  // exactly one discount in either mode
        Assert.Contains(byPriority.Trace, t => t is { Kind: "policy", RuleRef: "discount-precedence:priority-then-specificity" });
        // identical in every key before the code stays ambiguous in both modes
        var a = Disc("A", 3, 5m); var b = Disc("B", 3, 6m);
        Assert.Equal("ambiguous_discount", Refusal(() => PricingEngine.Resolve(Req(), [list], entries, [a, b], Default)));
        Assert.Equal("ambiguous_discount", Refusal(() => PricingEngine.Resolve(Req(), [list], entries, [a, b], ByPriority)));
    }

    [Fact]
    public void An_unknown_precedence_is_refused_and_input_order_never_matters()
    {
        var bad = Default with { ListPrecedence = (PriceListPrecedence)9 };
        Assert.Equal("invalid_policy", Refusal(() => PricingEngine.ValidatePolicy(bad)));
        Assert.Equal("invalid_policy", Refusal(() => PricingEngine.ValidatePolicy(Default with { DiscountPrecedence = (DiscountPrecedence)0 })));
        var list = List("L", 1);
        Assert.Equal("invalid_policy", Refusal(() => PricingEngine.Resolve(Req(), [list], [Entry(list, 1m)], [], bad)));
        var l1 = List("A", 2); var l2 = List("B", 1, cust: Cust); var l3 = List("C", 1, group: Group);
        var entries = new[] { Entry(l1, 1m), Entry(l2, 2m), Entry(l3, 3m) };
        var expected = PricingEngine.Resolve(Req(), [l1, l2, l3], entries, [], ByPriority);
        foreach (var order in new[] { new[] { l3, l1, l2 }, new[] { l2, l3, l1 }, new[] { l1, l3, l2 } })
            Same(expected, PricingEngine.Resolve(Req(), order, entries, [], ByPriority));             // priority 1: customer (scope) before group
        Assert.Equal(2m, expected.ListUnitPrice);
    }

    [Fact]
    public void Default_precedence_keeps_the_v1_snapshot_and_a_non_default_one_is_sealed_as_v2_with_its_precedences()
    {
        var list = List("L", 1);
        var resolved = PricingEngine.Resolve(Req(), [list], [Entry(list, 100m)], [], Default);
        var v1 = PricingSnapshots.Create(Default, Req(), resolved);
        Assert.Equal("pricing-snapshot-v1", v1.Version);
        Assert.True(PricingSnapshots.Verify(v1));

        var resolved2 = PricingEngine.Resolve(Req(), [list], [Entry(list, 100m)], [], ByPriority);
        var v2 = PricingSnapshots.Create(ByPriority, Req(), resolved2);
        Assert.Equal("pricing-snapshot-v2", v2.Version);
        Assert.True(PricingSnapshots.Verify(v2));
        Assert.NotEqual(v1.ContentHash, v2.ContentHash);
        // the precedences are sealed content: changing one, or mislabelling the version, is invalid evidence
        Assert.False(PricingSnapshots.Verify(v2 with { Policy = v2.Policy with { ListPrecedence = PriceListPrecedence.ScopeThenPriority } }));
        Assert.False(PricingSnapshots.Verify(v2 with { Policy = v2.Policy with { DiscountPrecedence = DiscountPrecedence.SpecificityThenPriority } }));
        Assert.False(PricingSnapshots.Verify(v2 with { Version = "pricing-snapshot-v1" }));
        Assert.False(PricingSnapshots.Verify(v1 with { Version = "pricing-snapshot-v2" }));
        Assert.False(PricingSnapshots.Verify(v1 with { Policy = ByPriority }));                               // a v1 snapshot cannot carry a non-default precedence
    }
}
