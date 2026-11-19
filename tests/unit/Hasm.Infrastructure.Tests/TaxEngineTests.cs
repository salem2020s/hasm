using System.Globalization;
using Hasm.Infrastructure.Taxes;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.10 slice 1: the pure tax engine and snapshot (SRS 6). Every expected figure below was worked out by hand from the contract in docs/PHASE-3.10.md.</summary>
public class TaxEngineTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a"), B = Guid.Parse("00000000-0000-0000-0000-00000000000b"), C = Guid.Parse("00000000-0000-0000-0000-00000000000c"),
        Z = Guid.Parse("00000000-0000-0000-0000-00000000000e");
    private static TaxComponent Std(Guid id, string code, decimal rate, int priority, params Guid[] includes) => new(id, code, TaxTreatment.Standard, rate, priority, includes);
    private static TaxLineInput Line(int no, decimal qty, decimal price, decimal discount, bool inclusive, params TaxComponent[] taxes) => new(no, qty, price, discount, inclusive, taxes);
    private static TaxLineInput Excl(decimal net, params TaxComponent[] taxes) => Line(1, 1m, net, 0m, false, taxes);
    private static TaxLineInput Incl(decimal gross, params TaxComponent[] taxes) => Line(1, 1m, gross, 0m, true, taxes);
    private static TaxPolicy Pol(int decimals = 2, TaxRoundingMode mode = TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel level = TaxRoundingLevel.PerLine) => new(decimals, mode, level);
    private static TaxDocumentResult Calc(TaxPolicy policy, params TaxLineInput[] lines) => TaxEngine.Calculate(policy, lines);
    private static string Refusal(TaxPolicy policy, params TaxLineInput[] lines) => Assert.Throws<TaxException>(() => TaxEngine.Calculate(policy, lines)).Code;
    private static decimal[] Amounts(TaxLineResult line) => line.Taxes.Select(t => t.Amount).ToArray();

    [Fact]
    public void A_single_exclusive_tax()
    {
        var r = Calc(Pol(), Excl(100m, Std(A, "VAT", 15m, 1)));
        Assert.Equal((100m, 15m, 115m), (r.TotalNet, r.TotalTax, r.TotalGross));
        Assert.Equal((100m, 15m), (r.Lines[0].Taxes[0].Base, r.Lines[0].Taxes[0].Amount));
    }

    [Fact]
    public void Quantity_price_and_discount_are_exact_before_the_one_rounding()
    {
        // 3 x 19.99 - 5.00 = 54.97; 15 % = 8.2455 -> 8.25 once
        var r = Calc(Pol(), Line(1, 3m, 19.99m, 5.00m, false, Std(A, "VAT", 15m, 1)));
        Assert.Equal((54.97m, 8.25m, 63.22m), (r.Lines[0].Net, r.Lines[0].TaxTotal, r.Lines[0].Gross));
    }

    [Fact]
    public void Fractional_quantities_up_to_ten_decimals_are_exact()
    {
        var r = Calc(Pol(), Line(1, 0.3333333333m, 3m, 0m, false, Std(A, "VAT", 10m, 1)));   // 0.9999999999 -> 1.00
        Assert.Equal(1.00m, r.Lines[0].Net);
    }

    [Theory]
    [InlineData(0.10, 0.01, 0.00)]   // 0.005: half-away goes up, half-even stays on the even 0.00
    [InlineData(0.50, 0.03, 0.02)]   // 0.025
    [InlineData(0.30, 0.02, 0.02)]   // 0.015: both go to the even/odd neighbour 0.02
    public void Both_rounding_modes_decide_ties_as_stated(double net, double halfAway, double halfEven)
    {
        var tax = Std(A, "VAT", 5m, 1);
        Assert.Equal((decimal)halfAway, Calc(Pol(2, TaxRoundingMode.HalfAwayFromZero), Excl((decimal)net, tax)).TotalTax);
        Assert.Equal((decimal)halfEven, Calc(Pol(2, TaxRoundingMode.HalfEven), Excl((decimal)net, tax)).TotalTax);
    }

    [Theory]
    [InlineData(0, 50, 3, 2)]        // 2.5 at zero decimals
    [InlineData(0, 30, 2, 2)]        // 1.5
    [InlineData(3, 1, 0.050, 0.050)] // 5 % of 1.000
    public void The_money_decimals_of_the_policy_drive_the_unit(int decimals, double net, double halfAway, double halfEven)
    {
        var tax = Std(A, "VAT", 5m, 1);
        Assert.Equal((decimal)halfAway, Calc(Pol(decimals, TaxRoundingMode.HalfAwayFromZero), Excl((decimal)net, tax)).TotalTax);
        Assert.Equal((decimal)halfEven, Calc(Pol(decimals, TaxRoundingMode.HalfEven), Excl((decimal)net, tax)).TotalTax);
    }

    [Fact]
    public void Several_taxes_apply_in_priority_order_and_totals_are_reported_per_tax()
    {
        var r = Calc(Pol(), Excl(100m, Std(B, "B", 10m, 2), Std(A, "A", 5m, 1)));     // given out of order: the priority decides
        Assert.Equal(["A", "B"], r.Lines[0].Taxes.Select(t => t.Code));
        Assert.Equal(new[] { 5m, 10m }, Amounts(r.Lines[0]));
        Assert.Equal(new[] { 5m, 10m }, r.TotalsByTax.Select(t => t.Amount));
        Assert.Equal(115m, r.TotalGross);
    }

    [Fact]
    public void A_compound_tax_takes_the_earlier_tax_into_its_base()
    {
        var r = Calc(Pol(), Excl(100m, Std(A, "A", 10m, 1), Std(B, "B", 5m, 2, A)));
        Assert.Equal(new[] { 10m, 5.50m }, Amounts(r.Lines[0]));
        Assert.Equal(110m, r.Lines[0].Taxes[1].Base);
        Assert.Equal(115.50m, r.TotalGross);
    }

    [Fact]
    public void Per_line_compounding_uses_the_rounded_earlier_amount_as_printed()
    {
        // A: 10 % of 0.15 = 0.015 -> 0.02; B (100 %, includes A): base 0.15 + 0.02 = 0.17
        var r = Calc(Pol(), Excl(0.15m, Std(A, "A", 10m, 1), Std(B, "B", 100m, 2, A)));
        Assert.Equal(new[] { 0.02m, 0.17m }, Amounts(r.Lines[0]));
    }

    [Fact]
    public void Zero_rated_exempt_and_not_applicable_lines_add_no_tax_but_their_turnover_is_reported()
    {
        var zero = new TaxComponent(Z, "ZERO", TaxTreatment.ZeroRated, 0m, 2);
        var exempt = new TaxComponent(C, "EXEMPT", TaxTreatment.Exempt, 0m, 3);
        var r = Calc(Pol(), Excl(100m, Std(A, "VAT", 15m, 1), zero, exempt));
        Assert.Equal(15m, r.TotalTax);
        var z = Assert.Single(r.TotalsByTax, t => t.Code == "ZERO"); var e = Assert.Single(r.TotalsByTax, t => t.Code == "EXEMPT");
        Assert.Equal((TaxTreatment.ZeroRated, 100m, 0m), (z.Treatment, z.Base, z.Amount));
        Assert.Equal((TaxTreatment.Exempt, 100m, 0m), (e.Treatment, e.Base, e.Amount));
    }

    [Fact]
    public void An_inclusive_price_is_split_exactly()
    {
        var r = Calc(Pol(), Incl(115m, Std(A, "VAT", 15m, 1)));
        Assert.Equal((100m, 15m, 115m), (r.Lines[0].Net, r.Lines[0].TaxTotal, r.Lines[0].Gross));
        var odd = Calc(Pol(), Incl(100m, Std(A, "VAT", 15m, 1)));                    // 86.9565... -> 86.96, tax = 100 - 86.96
        Assert.Equal((86.96m, 13.04m, 100m), (odd.Lines[0].Net, odd.Lines[0].TaxTotal, odd.Lines[0].Gross));
    }

    [Fact]
    public void The_gross_is_authoritative_and_the_last_taxable_tax_absorbs_the_inclusive_rounding_residual()
    {
        // 1.00 with three 10 % taxes: net 0.769230 -> 0.77, each tax 0.0769 -> 0.08; 0.77 + 0.24 = 1.01, so the LAST tax is 0.07
        var r = Calc(Pol(), Incl(1.00m, Std(A, "A", 10m, 1), Std(B, "B", 10m, 2), Std(C, "C", 10m, 3)));
        Assert.Equal(0.77m, r.Lines[0].Net);
        Assert.Equal(new[] { 0.08m, 0.08m, 0.07m }, Amounts(r.Lines[0]));
        Assert.Equal(1.00m, r.Lines[0].Gross);
    }

    [Fact]
    public void An_inclusive_price_with_compound_taxes_is_solved_through_the_multipliers()
    {
        var r = Calc(Pol(), Incl(115.50m, Std(A, "A", 10m, 1), Std(B, "B", 5m, 2, A)));   // 1 + 0.10 + 0.05 x 1.1 = 1.155
        Assert.Equal(100m, r.Lines[0].Net);
        Assert.Equal(new[] { 10m, 5.50m }, Amounts(r.Lines[0]));
    }

    [Fact]
    public void An_inclusive_price_without_a_taxable_tax_is_all_net()
    {
        var r = Calc(Pol(), Incl(50m, new TaxComponent(Z, "ZERO", TaxTreatment.ZeroRated, 0m, 1)));
        Assert.Equal((50m, 0m, 50m), (r.Lines[0].Net, r.Lines[0].TaxTotal, r.Lines[0].Gross));
    }

    [Fact]
    public void Per_document_rounding_sums_exactly_then_allocates_by_largest_remainder_with_ties_to_the_lower_line()
    {
        var tax = Std(A, "VAT", 10m, 1);
        var lines = new[] { Line(1, 1m, 0.33m, 0m, false, tax), Line(2, 1m, 0.33m, 0m, false, tax), Line(3, 1m, 0.33m, 0m, false, tax) };
        var doc = Calc(Pol(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerDocument), lines);
        Assert.Equal(new[] { 0.04m, 0.03m, 0.03m }, doc.Lines.Select(l => l.TaxTotal));        // exact 0.099 -> 0.10 in total, the extra cent to line 1
        Assert.Equal(0.10m, doc.TotalTax);
        var perLine = Calc(Pol(), lines);
        Assert.Equal(0.09m, perLine.TotalTax);                                                    // the policy genuinely changes the document total
    }

    [Fact]
    public void Per_document_ties_in_the_remainder_go_to_the_lower_line_number()
    {
        var tax = Std(A, "VAT", 10m, 1);
        var doc = Calc(Pol(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerDocument), Line(2, 1m, 0.25m, 0m, false, tax), Line(1, 1m, 0.25m, 0m, false, tax));
        Assert.Equal((0.03m, 0.02m, 0.05m), (doc.Lines.Single(l => l.LineNo == 1).TaxTotal, doc.Lines.Single(l => l.LineNo == 2).TaxTotal, doc.TotalTax));
    }

    [Fact]
    public void Document_level_totals_always_equal_the_sum_of_the_lines()
    {
        var seed = 12345; int Next(int max) { seed = unchecked(seed * 1103515245 + 12345) & 0x7fffffff; return seed % max; }
        foreach (var level in new[] { TaxRoundingLevel.PerLine, TaxRoundingLevel.PerDocument })
            for (var round = 0; round < 150; round++)
            {
                var taxes = new[] { Std(A, "A", Next(20) + 1, 1), Std(B, "B", Next(10) + 1, 2, Next(2) == 0 ? [] : [A]) };
                var lines = Enumerable.Range(1, Next(6) + 1).Select(i => Line(i, (Next(500) + 1) / 100m, (Next(100000) + 1) / 100m, 0m, false, taxes)).ToArray();
                var d = Calc(Pol(2, Next(2) == 0 ? TaxRoundingMode.HalfAwayFromZero : TaxRoundingMode.HalfEven, level), lines);
                Assert.Equal(d.TotalNet, d.Lines.Sum(l => l.Net)); Assert.Equal(d.TotalTax, d.Lines.Sum(l => l.TaxTotal)); Assert.Equal(d.TotalGross, d.Lines.Sum(l => l.Gross));
                Assert.Equal(d.TotalTax, d.TotalsByTax.Sum(t => t.Amount));
                foreach (var l in d.Lines) Assert.Equal(l.Net + l.TaxTotal, l.Gross);
            }
    }

    [Fact]
    public void Inclusive_lines_always_sum_to_their_stated_gross()
    {
        var seed = 777; int Next(int max) { seed = unchecked(seed * 1103515245 + 12345) & 0x7fffffff; return seed % max; }
        for (var round = 0; round < 300; round++)
        {
            var taxes = new[] { Std(A, "A", Next(25) + 1, 1), Std(B, "B", Next(15) + 1, 2, [A]), Std(C, "C", Next(8) + 1, 3) };
            var gross = (Next(2000000) + 1) / 100m;
            var d = Calc(Pol(), Incl(gross, taxes));
            Assert.Equal(gross, d.Lines[0].Gross);
            Assert.True(d.Lines[0].Net >= 0 && d.Lines[0].Taxes.All(t => t.Amount >= 0));
        }
    }

    [Fact]
    public void The_calculation_is_deterministic_and_culture_independent()
    {
        var lines = new[] { Line(1, 1m, 123.45m, 0m, false, Std(A, "A", 15m, 1)), Line(2, 1m, 99.99m, 0m, true, Std(A, "A", 15m, 1)) };
        var first = Calc(Pol(), lines); var original = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("ar-SA"); Assert.Equal(first.TotalGross, Calc(Pol(), lines).TotalGross); Assert.Equal(first.TotalTax, Calc(Pol(), lines).TotalTax); }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Every_invalid_input_is_refused_with_a_stable_code_and_no_partial_result()
    {
        var ok = Std(A, "A", 10m, 1);
        Assert.Equal("negative_amount", Refusal(Pol(), Line(1, -1m, 1m, 0m, false, ok)));
        Assert.Equal("negative_amount", Refusal(Pol(), Line(1, 1m, -1m, 0m, false, ok)));
        Assert.Equal("precision_exceeded", Refusal(Pol(), Line(1, 1.00000000001m, 1m, 0m, false, ok)));      // 11 decimals of quantity
        Assert.Equal("precision_exceeded", Refusal(Pol(), Line(1, 1m, 1.0000001m, 0m, false, ok)));          // 7 decimals of price
        Assert.Equal("precision_exceeded", Refusal(Pol(), Line(1, 1m, 10m, 0.001m, false, ok)));             // 3 decimals of discount with 2 money decimals
        Assert.Equal("discount_exceeds_amount", Refusal(Pol(), Line(1, 1m, 10m, 10.01m, false, ok)));
        Assert.Equal("amount_too_large", Refusal(Pol(), Line(1, 2_000_000_000_000_000m, 1m, 0m, false, ok)));
        Assert.Equal("invalid_rate", Refusal(Pol(), Excl(1m, Std(A, "A", -1m, 1))));
        Assert.Equal("invalid_rate", Refusal(Pol(), Excl(1m, Std(A, "A", 1000.01m, 1))));
        Assert.Equal("invalid_rate", Refusal(Pol(), Excl(1m, Std(A, "A", 1.0000001m, 1))));
        Assert.Equal("treatment_rate_mismatch", Refusal(Pol(), Excl(1m, new TaxComponent(A, "Z", TaxTreatment.ZeroRated, 5m, 1))));
        Assert.Equal("duplicate_tax", Refusal(Pol(), Excl(1m, Std(A, "A", 5m, 1), Std(A, "A", 5m, 2))));
        Assert.Equal("duplicate_priority", Refusal(Pol(), Excl(1m, Std(A, "A", 5m, 1), Std(B, "B", 5m, 1))));
        Assert.Equal("invalid_base_reference", Refusal(Pol(), Excl(1m, Std(A, "A", 5m, 1, B), Std(B, "B", 5m, 2))));        // only EARLIER taxes
        Assert.Equal("invalid_base_reference", Refusal(Pol(), Excl(1m, Std(A, "A", 5m, 1, Guid.NewGuid()))));
        Assert.Equal("too_many_taxes", Refusal(Pol(), Excl(1m, Enumerable.Range(1, 9).Select(i => Std(Guid.NewGuid(), "T" + i, 1m, i)).ToArray())));
        Assert.Equal("inclusive_requires_line_rounding", Refusal(Pol(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerDocument), Incl(10m, ok)));
        Assert.Equal("invalid_tax_lines", Refusal(Pol()));
        Assert.Equal("invalid_tax_lines", Refusal(Pol(), Excl(1m, ok), Excl(1m, ok)));                                       // duplicate line number
        Assert.Equal("invalid_tax_policy", Refusal(Pol(7), Excl(1m, ok)));
        Assert.Equal("invalid_tax", Refusal(Pol(), Excl(1m, new TaxComponent(Guid.Empty, "A", TaxTreatment.Standard, 5m, 1))));
    }

    [Fact]
    public void A_snapshot_seals_values_verifies_and_detects_every_tampering()
    {
        var policy = Pol(); var lines = new[] { Excl(100m, Std(A, "VAT", 15m, 1) with { RuleRef = "rate-v7" }) };
        var snapshot = TaxSnapshots.Create(policy, lines);
        Assert.True(TaxSnapshots.Verify(snapshot));
        Assert.Equal("rate-v7", snapshot.Lines[0].Taxes[0].RuleRef);                                           // the reference is kept as a value, nothing live is read
        Assert.False(TaxSnapshots.Verify(snapshot with { ContentHash = new string('0', 64) }));
        Assert.False(TaxSnapshots.Verify(snapshot with { Result = snapshot.Result with { TotalTax = 14.99m } }));
        Assert.False(TaxSnapshots.Verify(snapshot with { Policy = Pol(3) }));
        Assert.False(TaxSnapshots.Verify(snapshot with { Lines = [Excl(100m, Std(A, "VAT", 16m, 1))] }));
        Assert.False(TaxSnapshots.Verify(snapshot with { Version = "tax-snapshot-v0" }));
    }

    [Fact]
    public void A_later_rule_change_creates_a_new_snapshot_and_never_alters_the_old_one()
    {
        var old = TaxSnapshots.Create(Pol(), [Excl(100m, Std(A, "VAT", 15m, 1))]);
        var changed = TaxSnapshots.Create(Pol(), [Excl(100m, Std(A, "VAT", 16m, 1))]);           // the rate was changed afterwards
        Assert.NotEqual(old.ContentHash, changed.ContentHash);
        Assert.True(TaxSnapshots.Verify(old)); Assert.Equal(15m, old.Result.TotalTax);              // the old document is unchanged and still verifies
    }

    [Fact]
    public void The_snapshot_hash_ignores_the_scale_of_decimals_and_the_culture()
    {
        var one = TaxSnapshots.Create(Pol(), [Line(1, 1m, 100m, 0m, false, Std(A, "A", 15m, 1))]);
        var two = TaxSnapshots.Create(Pol(), [Line(1, 1.0m, 100.00m, 0.0m, false, Std(A, "A", 15.0m, 1))]);
        Assert.Equal(one.ContentHash, two.ContentHash);
        var original = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); Assert.Equal(one.ContentHash, TaxSnapshots.Create(Pol(), [Excl(100m, Std(A, "A", 15m, 1))]).ContentHash); }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
