using Hasm.Infrastructure.Pricing;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.11 slice 1, independent-review follow-up: pinned v1 hash, snapshot tamper coverage, resolution integrity, exact limits and data validation.</summary>
public class PricingReviewTests
{
    private static readonly Guid Item = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Fruit = Guid.Parse("00000000-0000-0000-0000-0000000000b1"), Unit = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Cust = Guid.Parse("00000000-0000-0000-0000-0000000000d1"), Group = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly DateOnly D = new(2026, 6, 1), Jan = new(2026, 1, 1);
    private static readonly PricingPolicy Policy = new(2, 2);
    private static int n = 5000;

    private static PriceListRecord List(string code, int priority = 10, Guid? cust = null, Guid? group = null, DateOnly? from = null, DateOnly? to = null, bool active = true) =>
        new(Guid.Parse($"00000000-0000-0000-0002-{++n:D12}"), code, priority, "JOD", from ?? Jan, to, active, cust, group);
    private static PriceEntryRecord Entry(PriceListRecord l, decimal price, decimal min = 0) => new(l.Id, Item, null, Unit, min, price);
    private static DiscountRuleRecord Disc(string code, decimal percent, bool active = true) => new(Guid.Parse($"00000000-0000-0000-0003-{++n:D12}"), code, 10, Jan, null, active, percent);
    private static PricingRequest Req(decimal qty = 1, decimal? cost = null) => new(D, "JOD", Item, Fruit, Unit, qty, null, null, cost);
    private static ResolvedPricing Pick(PricingRequest r, PriceListRecord[] lists, PriceEntryRecord[] entries, params DiscountRuleRecord[] discounts) => PricingEngine.Resolve(r, lists, entries, discounts);
    private static string Refusal(Action a) => Assert.Throws<PricingException>(a).Code;

    private static PricingSnapshot Golden()
    {
        var listId = Guid.Parse("00000000-0000-0000-0000-00000000f001");
        var request = new PricingRequest(new DateOnly(2026, 6, 1), "JOD", Item, null, Unit, 3m, null, null, 40m);
        var resolved = new ResolvedPricing(100m, $"list:{listId:N}@GOLD;tier:0", 10m, $"discount:{listId:N}@D", [new TraceStep("price", "list", "selected"), new TraceStep("discount", "d", "selected")]);
        return PricingSnapshots.Create(new PricingPolicy(2, 2, 5m, MarginAction.Warn), request, resolved, new PriceOverride(null, 15m, "promo"));
    }

    [Fact]
    public void The_v1_hash_is_pinned_by_a_golden_value_and_independent_of_the_culture()
    {
        const string golden = "80f21fef2effd2bb33660754ab6e010709dbb3fea04dc5ec9549de654a05f4ad";
        Assert.Equal(golden, Golden().ContentHash);
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            foreach (var name in new[] { "ar-SA", "de-DE", "fa-IR" }) { Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(name); Assert.Equal(golden, Golden().ContentHash); }
        }
        finally { Thread.CurrentThread.CurrentCulture = before; }
    }

    [Fact]
    public void Every_part_of_a_snapshot_is_covered_and_a_malformed_snapshot_is_invalid_evidence_never_a_crash()
    {
        var s = Golden();
        Assert.True(PricingSnapshots.Verify(s));
        Assert.False(PricingSnapshots.Verify(s with { Policy = s.Policy with { PriceDecimals = 3 } }));
        Assert.False(PricingSnapshots.Verify(s with { Request = s.Request with { Quantity = 4m } }));
        Assert.False(PricingSnapshots.Verify(s with { Override = new PriceOverride(null, 15m, "changed reason") }));
        Assert.False(PricingSnapshots.Verify(s with { Override = null }));
        Assert.False(PricingSnapshots.Verify(s with { Resolved = s.Resolved with { Trace = [new TraceStep("price", "list", "forged")] } }));
        Assert.False(PricingSnapshots.Verify(s with { Result = s.Result with { Trace = [] } }));
        Assert.False(PricingSnapshots.Verify(s with { Resolved = s.Resolved with { Trace = null! } }));
        Assert.False(PricingSnapshots.Verify(s with { Result = s.Result with { Trace = null! } }));
        Assert.False(PricingSnapshots.Verify(s with { Resolved = s.Resolved with { PriceRuleRef = "" } }));
        Assert.False(PricingSnapshots.Verify(s with { Policy = null! }));
    }

    [Fact]
    public void A_resolution_must_name_its_rules_and_an_override_records_what_it_replaced()
    {
        var l = List("REF"); var good = Pick(Req(), [l], [Entry(l, 100m)], Disc("D", 10m));
        Assert.Equal("invalid_resolution", Refusal(() => PricingEngine.Calculate(Policy, Req(), good with { PriceRuleRef = "" })));
        Assert.Equal("invalid_resolution", Refusal(() => PricingEngine.Calculate(Policy, Req(), good with { DiscountRuleRef = null })));
        Assert.Equal("invalid_resolution", Refusal(() => PricingEngine.Calculate(Policy, Req(), good with { Trace = null! })));
        var r = PricingEngine.Calculate(Policy, Req(), good, new PriceOverride(80m, 20m, "why"));
        Assert.Equal(("manual-override", "manual-override"), (r.PriceRuleRef, r.DiscountRuleRef));                          // the result never claims the replaced rules
        var step = Assert.Single(r.Trace, t => t.Kind == "override");
        Assert.Contains("replaced " + good.PriceRuleRef + "=100", step.Outcome);
        Assert.Contains("replaced " + good.DiscountRuleRef + "=10", step.Outcome);
        var priceOnly = PricingEngine.Calculate(Policy, Req(), good, new PriceOverride(80m, null, "why"));
        Assert.Equal(("manual-override", good.DiscountRuleRef), (priceOnly.PriceRuleRef, priceOnly.DiscountRuleRef));
    }

    [Fact]
    public void Reasons_margins_the_exact_line_amount_and_bad_data_have_exact_edges()
    {
        var l = List("E"); var resolved = Pick(Req(), [l], [Entry(l, 100m)]);
        PricingEngine.Calculate(Policy, Req(), resolved, new PriceOverride(90m, null, new string('x', 500)));
        Assert.Equal("override_reason_invalid", Refusal(() => PricingEngine.Calculate(Policy, Req(), resolved, new PriceOverride(90m, null, new string('x', 501)))));
        Assert.Equal(0m, PricingEngine.Calculate(Policy, Req(), resolved, new PriceOverride(null, 100m, "free")).NetUnitPrice);          // 100 % is allowed
        var atMin = PricingEngine.Calculate(new PricingPolicy(2, 2, 10m, MarginAction.Block), Req(cost: 90m), resolved);
        Assert.Equal((10m, MarginStatus.Ok), (atMin.MarginPercent, atMin.Margin));                                                       // exactly the minimum is acceptable
        Assert.Equal("invalid_policy", Refusal(() => PricingEngine.Calculate(new PricingPolicy(2, 2, 101m), Req(), resolved)));
        // the line amount is computed on integers: no overflow, no hidden double rounding
        var big = List("BIG");
        var huge = new PricingRequest(D, "JOD", Item, Fruit, Unit, 999_999_999m);
        var hugeLine = PricingEngine.Calculate(new PricingPolicy(6, 6), huge, Pick(huge, [big], [Entry(big, 999_999_999_999m)])).LineNet;
        Assert.Equal((decimal)(new System.Numerics.BigInteger(999_999_999_999m) * 999_999_999), hugeLine);                                // the largest product is exact, not an overflow
        var tiny = PricingEngine.Calculate(new PricingPolicy(6, 2), Req(0.0000000003m), Pick(Req(0.0000000003m), [big], [Entry(big, 1.5m)]));
        Assert.Equal(0m, tiny.LineNet);
        var half = PricingEngine.Calculate(new PricingPolicy(6, 2), Req(0.5m), Pick(Req(0.5m), [big], [Entry(big, 0.01m)]));
        Assert.Equal(0.01m, half.LineNet);                                                                                               // 0.005 rounds half away from zero
        Assert.Equal(-0.01m, PricingEngine.Calculate(new PricingPolicy(6, 2), Req(0.5m), Pick(Req(0.5m), [big], [Entry(big, 0.01m)]), new PriceOverride(null, 0m, "r")).LineNet * -1m);
        var exact = PricingEngine.Calculate(new PricingPolicy(6, 6), Req(1234567.1234567891m), Pick(Req(1234567.1234567891m), [big], [Entry(big, 9876.543211m)]));
        Assert.Equal(12193255541.700949m, exact.LineNet);                                                              // 9876.543211 x 1234567.1234567891 rounded once
    }

    [Fact]
    public void Tied_lists_break_by_code_inactive_data_never_blocks_and_malformed_entries_are_refused()
    {
        var t1 = List("AAA", 5); var t2 = List("BBB", 5);
        Assert.Equal(1m, Pick(Req(), [t2, t1], [Entry(t1, 1m), Entry(t2, 2m)]).ListUnitPrice);
        var dead = List("DEAD", 0, cust: Cust, group: Group, active: false);
        Assert.Equal(1m, Pick(Req(), [dead, t1], [Entry(t1, 1m)], Disc("BAD", 500m, active: false)).ListUnitPrice);
        Assert.Equal("invalid_price", Refusal(() => Pick(Req(), [t1], [new PriceEntryRecord(t1.Id, Item, Fruit, Unit, 0, 1m)])));
        Assert.Equal("invalid_price", Refusal(() => Pick(Req(), [t1], [new PriceEntryRecord(t1.Id, null, null, Unit, 0, 1m)])));
        Assert.Equal("invalid_price", Refusal(() => Pick(Req(), [t1], [Entry(t1, 1m, min: -1)])));
        Assert.Equal("invalid_price_list", Refusal(() => Pick(Req(), [List("RNG", from: new DateOnly(2026, 6, 2), to: new DateOnly(2026, 6, 1))], [])));
        Assert.Equal("invalid_request", Refusal(() => PricingEngine.Resolve(Req(), null!, [], [])));
    }
}
