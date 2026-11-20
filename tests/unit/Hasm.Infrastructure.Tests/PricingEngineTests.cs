using Hasm.Infrastructure.Pricing;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.11 slice 1 (SRS 8.1-8.4): the pure pricing engine - deterministic priority matrix, rule trace, overrides with a reason, margin control and the verifiable snapshot.</summary>
public class PricingEngineTests
{
    private static readonly Guid Item = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Fruit = Guid.Parse("00000000-0000-0000-0000-0000000000b1"), Unit = Guid.Parse("00000000-0000-0000-0000-0000000000c1"), Box = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid Cust = Guid.Parse("00000000-0000-0000-0000-0000000000d1"), Group = Guid.Parse("00000000-0000-0000-0000-0000000000e1"), OtherCust = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    private static readonly DateOnly D = new(2026, 6, 1), Jan = new(2026, 1, 1);
    private static readonly PricingPolicy Policy = new(2, 2);
    private static int n;

    private static PriceListRecord List(string code, int priority = 10, Guid? cust = null, Guid? group = null, string cur = "JOD", DateOnly? from = null, DateOnly? to = null, bool active = true) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{++n:D12}"), code, priority, cur, from ?? Jan, to, active, cust, group);
    private static PriceEntryRecord Entry(PriceListRecord l, decimal price, decimal min = 0, Guid? item = null, Guid? itemGroup = null, Guid? unit = null) =>
        new(l.Id, item ?? (itemGroup is null ? Item : null), itemGroup, unit ?? Unit, min, price);
    private static DiscountRuleRecord Disc(string code, decimal percent, int priority = 10, decimal min = 0, Guid? cust = null, Guid? group = null, Guid? item = null, Guid? itemGroup = null, DateOnly? from = null, DateOnly? to = null, bool active = true) =>
        new(Guid.Parse($"00000000-0000-0000-0001-{++n:D12}"), code, priority, from ?? Jan, to, active, percent, min, cust, group, item, itemGroup);
    private static PricingRequest Req(decimal qty = 1, Guid? cust = null, Guid? group = null, string cur = "JOD", Guid? unit = null, decimal? cost = null, DateOnly? date = null) =>
        new(date ?? D, cur, Item, Fruit, unit ?? Unit, qty, cust, group, cost);
    private static ResolvedPricing Pick(PricingRequest r, PriceListRecord[] lists, PriceEntryRecord[] entries, params DiscountRuleRecord[] discounts) => PricingEngine.Resolve(r, lists, entries, discounts);
    private static string Refusal(Action a) => Assert.Throws<PricingException>(a).Code;

    [Fact]
    public void A_single_list_prices_the_line_with_one_rounding_and_a_rule_trace()
    {
        var l = List("RETAIL");
        var resolved = Pick(Req(3), [l], [Entry(l, 10.255m)]);
        var r = PricingEngine.Calculate(Policy, Req(3), resolved);
        Assert.Equal((10.255m, 0m, 10.26m, 30.78m), (r.ListUnitPrice, r.DiscountPercent, r.NetUnitPrice, r.LineNet));     // 10.255 -> 10.26 (half away), 10.26 x 3
        Assert.Contains("list:" + l.Id.ToString("N") + "@RETAIL", r.PriceRuleRef);
        Assert.Null(r.DiscountRuleRef);
        Assert.Contains(r.Trace, t => t.Kind == "price" && t.Outcome == "selected");
        Assert.Contains(r.Trace, t => t.Kind == "discount" && t.Outcome == "no_applicable_rule");
    }

    [Fact]
    public void List_selection_orders_by_scope_then_priority_then_later_start_then_code()
    {
        var general = List("GEN", priority: 1); var grp = List("GRP", priority: 50, group: Group); var cust = List("CUS", priority: 99, cust: Cust);
        var entries = new[] { Entry(general, 10m), Entry(grp, 9m), Entry(cust, 8m) };
        Assert.Equal(8m, Pick(Req(cust: Cust, group: Group), [general, grp, cust], entries).ListUnitPrice);              // a customer list beats a group list beats a general one even with a worse priority
        Assert.Equal(9m, Pick(Req(cust: OtherCust, group: Group), [general, grp, cust], entries).ListUnitPrice);          // the customer list is not this customer's
        Assert.Equal(10m, Pick(Req(), [general, grp, cust], entries).ListUnitPrice);
        // equal scope: lower priority number first, then the later start, then the code
        var a = List("A", priority: 5); var b = List("B", priority: 3); var c = List("C", priority: 3, from: new DateOnly(2026, 3, 1)); var z = List("Z", priority: 3, from: new DateOnly(2026, 3, 1));
        var es = new[] { Entry(a, 1m), Entry(b, 2m), Entry(c, 3m), Entry(z, 4m) };
        Assert.Equal(3m, Pick(Req(), [a, b, z, c], es).ListUnitPrice);
        Assert.Equal(3m, Pick(Req(), [z, c, b, a], es).ListUnitPrice);                                                   // input order never matters
    }

    [Fact]
    public void A_list_that_cannot_price_the_line_is_skipped_and_the_skip_is_traced_and_ineligible_lists_never_apply()
    {
        var empty = List("EMPTY", priority: 1); var wrongUnit = List("UNIT", priority: 2); var good = List("GOOD", priority: 9);
        var entries = new[] { Entry(wrongUnit, 5m, unit: Box), Entry(good, 7m) };
        var r = Pick(Req(), [empty, wrongUnit, good], entries);
        Assert.Equal(7m, r.ListUnitPrice);
        Assert.Equal(2, r.Trace.Count(t => t.Kind == "price" && t.Outcome == "skipped:no_entry"));
        var off = List("OFF", priority: 0, active: false); var expired = List("OLD", priority: 0, to: new DateOnly(2026, 5, 31)); var future = List("NEW", priority: 0, from: new DateOnly(2026, 7, 1)); var usd = List("USD", priority: 0, cur: "USD");
        var bad = new[] { off, expired, future, usd }.Select(x => Entry(x, 1m)).Append(Entry(good, 7m)).ToArray();
        Assert.Equal(7m, Pick(Req(), [off, expired, future, usd, good], bad).ListUnitPrice);
        Assert.Equal(1m, Pick(Req(cur: "usd"), [off, expired, future, usd, good], bad).ListUnitPrice);                    // currency compares case-insensitively
        Assert.Equal(1m, Pick(Req(date: new DateOnly(2026, 5, 31)), [off, expired, future, usd, good], bad).ListUnitPrice);   // the last valid day is included
    }

    [Fact]
    public void Quantity_tiers_and_item_before_item_group_are_resolved_inside_the_list()
    {
        var l = List("TIER");
        var entries = new[] { Entry(l, 10m), Entry(l, 9m, min: 10), Entry(l, 8m, min: 100), Entry(l, 5m, itemGroup: Fruit), Entry(l, 4m, min: 50, itemGroup: Fruit) };
        Assert.Equal(10m, Pick(Req(9), [l], entries).ListUnitPrice);
        Assert.Equal(9m, Pick(Req(10), [l], entries).ListUnitPrice);                                                      // the tier starts AT its minimum
        Assert.Equal(9m, Pick(Req(99.9999m), [l], entries).ListUnitPrice);
        Assert.Equal(8m, Pick(Req(100), [l], entries).ListUnitPrice);
        // an item entry beats a group entry even when the group's tier is higher
        Assert.Equal(9m, Pick(Req(60), [l], entries).ListUnitPrice);
        // group entries serve an item without its own entry
        var other = new PricingRequest(D, "JOD", Guid.NewGuid(), Fruit, Unit, 60);
        Assert.Equal(4m, PricingEngine.Resolve(other, [l], entries, []).ListUnitPrice);
        Assert.Contains("tier:50", PricingEngine.Resolve(other, [l], entries, []).PriceRuleRef);
    }

    [Fact]
    public void Missing_or_ambiguous_prices_are_refused_never_defaulted()
    {
        var l = List("X");
        Assert.Equal("no_price", Refusal(() => Pick(Req(), [], [])));
        Assert.Equal("no_price", Refusal(() => Pick(Req(), [l], [Entry(l, 5m, min: 10)])));                              // the quantity is below the first tier
        Assert.Equal("ambiguous_price", Refusal(() => Pick(Req(), [l], [Entry(l, 5m), Entry(l, 6m)])));                  // two entries for the same tier
        Assert.Equal("invalid_price_list", Refusal(() => Pick(Req(), [List("BOTH", cust: Cust, group: Group)], [])));
        Assert.Equal("invalid_price", Refusal(() => Pick(Req(), [l], [Entry(l, -1m)])));
        Assert.Equal("invalid_price", Refusal(() => Pick(Req(), [l], [Entry(l, 1.1234567m)])));
        Assert.Equal("invalid_quantity", Refusal(() => Pick(Req(0), [l], [Entry(l, 1m)])));
        Assert.Equal("invalid_quantity", Refusal(() => Pick(Req(1.12345678901m), [l], [Entry(l, 1m)])));
        Assert.Equal("invalid_currency", Refusal(() => Pick(Req(cur: "JODX"), [l], [Entry(l, 1m)])));
        Assert.Equal("invalid_cost", Refusal(() => Pick(Req(cost: -1m), [l], [Entry(l, 1m)])));
    }

    [Fact]
    public void Discounts_never_stack_and_the_most_specific_rule_wins_before_priority()
    {
        var l = List("P"); var e = new[] { Entry(l, 100m) };
        var general = Disc("GEN", 5m, priority: 1); var group = Disc("GRP", 8m, priority: 9, group: Group); var cust = Disc("CUS", 12m, priority: 50, cust: Cust);
        var itemRule = Disc("ITEM", 20m, priority: 99, item: Item);
        Assert.Equal(12m, Pick(Req(cust: Cust, group: Group), [l], e, general, group, cust).DiscountPercent);            // customer beats group beats general
        Assert.Equal(8m, Pick(Req(cust: OtherCust, group: Group), [l], e, general, group, cust).DiscountPercent);
        Assert.Equal(5m, Pick(Req(), [l], e, general, group, cust).DiscountPercent);
        Assert.Equal(12m, Pick(Req(cust: Cust), [l], e, general, cust, itemRule).DiscountPercent);                       // customer specificity is compared before item specificity
        Assert.Equal(20m, Pick(Req(), [l], e, general, itemRule).DiscountPercent);                                       // item beats general
        var groupItem = Disc("GI", 15m, itemGroup: Fruit);
        Assert.Equal(20m, Pick(Req(), [l], e, groupItem, itemRule).DiscountPercent);
        Assert.Equal(15m, Pick(Req(), [l], e, general, groupItem).DiscountPercent);
        // same specificity: lower priority number, then the higher minimum quantity
        var p1 = Disc("P1", 3m, priority: 1); var p2 = Disc("P2", 4m, priority: 2);
        Assert.Equal(3m, Pick(Req(), [l], e, p2, p1).DiscountPercent);
        var q10 = Disc("Q10", 6m, min: 10); var q50 = Disc("Q50", 7m, min: 50);
        Assert.Equal(6m, Pick(Req(49), [l], e, q10, q50).DiscountPercent);
        Assert.Equal(7m, Pick(Req(50), [l], e, q10, q50).DiscountPercent);
        var r = Pick(Req(cust: Cust, group: Group), [l], e, general, group, cust);
        Assert.Equal(2, r.Trace.Count(t => t.Kind == "discount" && t.Outcome == "not_selected:lower_precedence"));      // the losers are traced
    }

    [Fact]
    public void Inactive_expired_and_future_discounts_do_not_apply_and_ambiguity_or_bad_data_is_refused()
    {
        var l = List("P"); var e = new[] { Entry(l, 100m) };
        Assert.Equal(0m, Pick(Req(), [l], e, Disc("OFF", 9m, active: false), Disc("OLD", 9m, to: new DateOnly(2026, 5, 31)), Disc("NEW", 9m, from: new DateOnly(2026, 6, 2))).DiscountPercent);
        Assert.Equal(0m, Pick(Req(), [l], e, Disc("BIG", 9m, min: 10)).DiscountPercent);
        Assert.Equal("ambiguous_discount", Refusal(() => Pick(Req(), [l], e, Disc("A", 5m), Disc("B", 6m))));
        Assert.Equal("invalid_discount", Refusal(() => Pick(Req(), [l], e, Disc("C", 101m))));
        Assert.Equal("invalid_discount", Refusal(() => Pick(Req(), [l], e, Disc("D", 1.23456m))));
        Assert.Equal("invalid_discount", Refusal(() => Pick(Req(), [l], e, Disc("E", 5m, cust: Cust, group: Group))));
        Assert.Equal("invalid_discount", Refusal(() => Pick(Req(), [l], e, Disc("F", 5m, item: Item, itemGroup: Fruit))));
    }

    [Fact]
    public void The_net_price_and_line_amount_round_half_away_from_zero_once_each()
    {
        var l = List("R");
        var resolved = Pick(Req(3), [l], [Entry(l, 9.99m)], Disc("D", 12.5m));
        var r = PricingEngine.Calculate(new PricingPolicy(2, 2), Req(3), resolved);
        Assert.Equal(8.74m, r.NetUnitPrice);                                                                            // 9.99 x 0.875 = 8.74125 -> 8.74
        Assert.Equal(26.22m, r.LineNet);                                                                                // 8.74 x 3 (the rounded unit price is what is printed)
        var whole = PricingEngine.Calculate(new PricingPolicy(0, 0), Req(3), Pick(Req(3), [l], [Entry(l, 2.5m)]));
        Assert.Equal((3m, 9m), (whole.NetUnitPrice, whole.LineNet));                                                    // 2.5 -> 3 (half away from zero)
        var fine = PricingEngine.Calculate(new PricingPolicy(6, 3), Req(0.5m), Pick(Req(0.5m), [l], [Entry(l, 1.234567m)]));
        Assert.Equal((1.234567m, 0.617m), (fine.NetUnitPrice, fine.LineNet));
        Assert.Equal("invalid_policy", Refusal(() => PricingEngine.Calculate(new PricingPolicy(7, 2), Req(), resolved)));
        Assert.Equal("invalid_policy", Refusal(() => PricingEngine.Calculate(new PricingPolicy(2, -1), Req(), resolved)));
    }

    [Fact]
    public void An_override_needs_a_reason_replaces_only_what_it_names_and_keeps_the_rule_in_the_trace()
    {
        var l = List("O"); var resolved = Pick(Req(2), [l], [Entry(l, 100m)], Disc("D", 10m));
        Assert.Equal("override_reason_required", Refusal(() => PricingEngine.Calculate(Policy, Req(2), resolved, new PriceOverride(90m, null, "  "))));
        Assert.Equal("override_empty", Refusal(() => PricingEngine.Calculate(Policy, Req(2), resolved, new PriceOverride(null, null, "why"))));
        Assert.Equal("invalid_override", Refusal(() => PricingEngine.Calculate(Policy, Req(2), resolved, new PriceOverride(null, 150m, "why"))));
        Assert.Equal("invalid_override", Refusal(() => PricingEngine.Calculate(Policy, Req(2), resolved, new PriceOverride(-5m, null, "why"))));
        var onlyDiscount = PricingEngine.Calculate(Policy, Req(2), resolved, new PriceOverride(null, 25m, " loyal customer "));
        Assert.Equal((100m, 25m, 75m, 150m, true, "loyal customer"), (onlyDiscount.ListUnitPrice, onlyDiscount.DiscountPercent, onlyDiscount.NetUnitPrice, onlyDiscount.LineNet, onlyDiscount.Overridden, onlyDiscount.OverrideReason));
        var onlyPrice = PricingEngine.Calculate(Policy, Req(2), resolved, new PriceOverride(80m, null, "price match"));
        Assert.Equal((80m, 10m, 72m), (onlyPrice.ListUnitPrice, onlyPrice.DiscountPercent, onlyPrice.NetUnitPrice));       // the rule discount still applies
        Assert.Contains(onlyPrice.Trace, t => t.Kind == "override");
        Assert.Contains(onlyPrice.Trace, t => t.Kind == "price" && t.Outcome == "selected");                              // what the rules chose stays on record
        Assert.Equal(90m, PricingEngine.Calculate(Policy, Req(2), resolved).NetUnitPrice);
    }

    [Fact]
    public void The_margin_control_warns_asks_for_approval_or_blocks_and_never_silently_skips_itself()
    {
        var l = List("M"); var r1 = Pick(Req(1, cost: 90m), [l], [Entry(l, 100m)]);
        var ok = PricingEngine.Calculate(new PricingPolicy(2, 2, 5m, MarginAction.Block), Req(1, cost: 90m), r1);        // margin (100-90)/100 = 10%
        Assert.Equal((10m, MarginStatus.Ok, false), (ok.MarginPercent, ok.Margin, ok.RequiresApproval));
        var low = Req(1, cost: 98m);
        var warn = PricingEngine.Calculate(new PricingPolicy(2, 2, 5m, MarginAction.Warn), low, r1);
        Assert.Equal((2m, MarginStatus.BelowMinimum, false), (warn.MarginPercent, warn.Margin, warn.RequiresApproval));
        var ask = PricingEngine.Calculate(new PricingPolicy(2, 2, 5m, MarginAction.RequireApproval), low, r1);
        Assert.True(ask.RequiresApproval);
        Assert.Equal("margin_below_minimum", Refusal(() => PricingEngine.Calculate(new PricingPolicy(2, 2, 5m, MarginAction.Block), low, r1)));
        // an override cannot bypass a block: the margin is computed on the overridden price
        Assert.Equal("margin_below_minimum", Refusal(() => PricingEngine.Calculate(new PricingPolicy(2, 2, 5m, MarginAction.Block), Req(1, cost: 90m), r1, new PriceOverride(91m, null, "close the deal"))));
        // without a cost: warn reports it, the stricter actions refuse
        var noCost = Pick(Req(), [l], [Entry(l, 100m)]);
        Assert.Equal(MarginStatus.NotEvaluated, PricingEngine.Calculate(new PricingPolicy(2, 2, 5m, MarginAction.Warn), Req(), noCost).Margin);
        Assert.Equal("cost_unavailable", Refusal(() => PricingEngine.Calculate(new PricingPolicy(2, 2, 5m, MarginAction.RequireApproval), Req(), noCost)));
        Assert.Equal(MarginStatus.NotEvaluated, PricingEngine.Calculate(Policy, Req(1, cost: 1m), noCost).Margin);          // no minimum, no control
        var free = PricingEngine.Calculate(new PricingPolicy(2, 2, 0m, MarginAction.RequireApproval), Req(1, cost: 5m), Pick(Req(1, cost: 5m), [l], [Entry(l, 0m)]));
        Assert.Equal((-100m, true), (free.MarginPercent, free.RequiresApproval));                                         // a free line with a cost is a total loss
    }

    [Fact]
    public void The_snapshot_is_sealed_verifiable_independent_of_later_changes_and_detects_tampering()
    {
        var l = List("S"); var d = Disc("D", 10m);
        var resolved = Pick(Req(2, cost: 40m), [l], [Entry(l, 100m)], d);
        var policy = new PricingPolicy(2, 2, 5m, MarginAction.Warn);
        var snap = PricingSnapshots.Create(policy, Req(2, cost: 40m), resolved, new PriceOverride(null, 15m, "promo"));
        Assert.True(PricingSnapshots.Verify(snap));
        Assert.Equal(PricingSnapshots.Create(policy, Req(2, cost: 40m), resolved, new PriceOverride(null, 15m, "promo")).ContentHash, snap.ContentHash);   // deterministic
        Assert.Equal(64, snap.ContentHash.Length);
        // a later change of the price list or the rules does not touch what was sealed: the snapshot holds values
        Assert.Equal(85m, snap.Result.NetUnitPrice);
        Assert.NotEqual(snap.ContentHash, PricingSnapshots.Create(policy, Req(2, cost: 40m), resolved).ContentHash);
        // scale-independent: 100 and 100.00 are the same evidence
        var same = PricingSnapshots.Create(policy, Req(2, cost: 40m), resolved with { ListUnitPrice = 100.000m }, new PriceOverride(null, 15.0m, "promo"));
        Assert.Equal(snap.ContentHash, same.ContentHash);
        // tampering with the result, the hash, the trace or the version fails verification
        Assert.False(PricingSnapshots.Verify(snap with { Result = snap.Result with { LineNet = 1m } }));
        Assert.False(PricingSnapshots.Verify(snap with { ContentHash = new string('0', 64) }));
        Assert.False(PricingSnapshots.Verify(snap with { Resolved = snap.Resolved with { ListUnitPrice = 1m } }));
        Assert.False(PricingSnapshots.Verify(snap with { Version = "pricing-snapshot-v0" }));
        Assert.False(PricingSnapshots.Verify(null!));
        // a snapshot whose own content cannot be calculated is not valid evidence
        var forged = snap with { Resolved = snap.Resolved with { DiscountPercent = 500m } };
        Assert.False(PricingSnapshots.Verify(forged));
    }

    [Fact]
    public void The_same_inputs_always_give_the_same_price_regardless_of_input_order()
    {
        var a = List("A", 3); var b = List("B", 3); var c = List("C", 1, group: Group);
        var entries = new[] { Entry(a, 1m, 0), Entry(b, 2m, 0), Entry(c, 3m, 5, itemGroup: Fruit), Entry(c, 4m, 0, itemGroup: Fruit) };
        var ds = new[] { Disc("X", 1m, 5), Disc("Y", 2m, 4, item: Item), Disc("Z", 3m, 4, group: Group) };
        var baseline = PricingEngine.Calculate(Policy, Req(10, group: Group), Pick(Req(10, group: Group), [a, b, c], entries, ds));
        var rnd = new Random(7);
        for (var i = 0; i < 25; i++)
        {
            var r = PricingEngine.Calculate(Policy, Req(10, group: Group), Pick(Req(10, group: Group), [.. new[] { a, b, c }.OrderBy(_ => rnd.Next())], [.. entries.OrderBy(_ => rnd.Next())], [.. ds.OrderBy(_ => rnd.Next())]));
            Assert.Equal(baseline, r with { Trace = baseline.Trace } );
            Assert.Equal(baseline.Trace.Select(t => (t.Kind, t.Outcome)).OrderBy(x => x), r.Trace.Select(t => (t.Kind, t.Outcome)).OrderBy(x => x));
        }
    }
}
