using System.Numerics;
using Hasm.Infrastructure.Costing;
using static Hasm.Infrastructure.Costing.CostingMath;

namespace Hasm.Infrastructure.Tests;

/// <summary>Exact, deterministic checks of the pure costing arithmetic: every expected figure is stated explicitly.</summary>
public class CostingMathTests
{
    private static BigInteger Qty(decimal q) => ToUnits(q, QuantityScale);
    private static BigInteger Amt(decimal a) => ToUnits(a, AmountScale);
    private static decimal A(BigInteger u) => FromUnits(u, AmountScale);
    private static decimal Q(BigInteger u) => FromUnits(u, QuantityScale);
    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
    private static LayerState Layer(int n, decimal qty, decimal amount) => new(Id(n), n, Qty(qty), Amt(amount));

    [Theory]
    [InlineData("100", "3", "7", 2, "42.86")]                 // 100 x 3/7 = 42.857142... -> 42.86
    [InlineData("100", "1", "3", 2, "33.33")]
    [InlineData("100", "2", "3", 2, "66.67")]
    [InlineData("10", "1", "4", 2, "2.50")]
    [InlineData("0.05", "1", "2", 2, "0.03")]                 // exactly half a cent: away from zero
    [InlineData("0.04", "1", "2", 2, "0.02")]
    [InlineData("100", "1", "3", 3, "33.333")]                // three-decimal currency
    [InlineData("100", "1", "3", 0, "33")]                    // zero-decimal currency
    [InlineData("1000000000000", "1", "3", 2, "333333333333.33")]
    public void A_proportion_is_computed_exactly_and_rounded_once_half_away_from_zero(string amount, string part, string whole, int decimals, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), A(Proportion(Amt(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)), Qty(decimal.Parse(part)), Qty(decimal.Parse(whole)), decimals)));

    [Fact]
    public void Taking_the_whole_returns_the_exact_remainder_with_no_rounding_at_all() =>
        Assert.Equal(123.457m, A(Proportion(Amt(123.457m), Qty(3m), Qty(3m), 2)));

    [Fact]
    public void Huge_quantities_and_amounts_do_not_lose_exactness_because_big_integers_are_used()
    {
        // 999,999,999,999,999.99 x 1 / 3 would overflow 28-digit decimal arithmetic; the integer arithmetic stays exact
        Assert.Equal(333333333333333.33m, A(Proportion(Amt(999999999999999.99m), Qty(1m), Qty(3m), 2)));
    }

    [Fact]
    public void Quantities_and_amounts_with_too_many_decimals_are_refused_never_rounded()
    {
        Assert.Throws<CostingException>(() => ToUnits(0.00000000001m, QuantityScale));
        Assert.Throws<CostingException>(() => ToUnits(0.0000001m, AmountScale));
        Assert.True(FitsDecimals(1.25m, 2));
        Assert.False(FitsDecimals(1.255m, 2));
        Assert.Equal(1234567m, A(ToUnits(1234567m, AmountScale)));
    }

    [Fact]
    public void A_fifo_issue_consumes_the_oldest_layers_first_fully_before_the_next()
    {
        // layers: 10 @ 1.00 (10.00), 10 @ 2.00 (20.00), 10 @ 3.00 (30.00); issue 25 = 10 + 10 + 5
        var plan = PlanFifo([Layer(2, 10, 20m), Layer(1, 10, 10m), Layer(3, 10, 30m)], Qty(25m), 2);   // given out of order: arrival order (seq) decides
        Assert.Equal([(Id(1), 10m, 10m), (Id(2), 10m, 20m), (Id(3), 5m, 15m)], plan.Select(t => (t.LayerId, Q(t.Quantity), A(t.Amount))));
        Assert.Equal(45m, A(plan.Aggregate(BigInteger.Zero, (s, t) => s + t.Amount)));
    }

    [Fact]
    public void A_partial_consumption_rounds_once_and_the_last_unit_takes_the_remainder_so_nothing_is_lost()
    {
        // one layer 3 @ total 100.00 issued one unit at a time: 33.33 + 33.34 (66.67-33.33) ... the sum is always exactly 100.00
        var remainingQty = Qty(3m); var remainingAmount = Amt(100m); var issued = BigInteger.Zero;
        var costs = new List<decimal>();
        for (var i = 0; i < 3; i++)
        {
            var layer = new LayerState(Guid.Empty, 1, remainingQty, remainingAmount);
            var t = PlanFifo([layer], Qty(1m), 2).Single();
            costs.Add(A(t.Amount));
            issued += t.Amount; remainingQty -= t.Quantity; remainingAmount -= t.Amount;
        }
        Assert.Equal([33.33m, 33.34m, 33.33m], costs);                      // 100.00 x 1/3 = 33.33; then 66.67 x 1/2 = 33.335 -> 33.34; then the whole remainder 33.33
        Assert.Equal(100m, A(issued));
        Assert.Equal(BigInteger.Zero, remainingAmount);
    }

    [Fact]
    public void Issuing_more_than_the_layers_hold_is_refused_and_a_non_positive_quantity_is_invalid()
    {
        Assert.Equal("insufficient_stock", Assert.Throws<CostingException>(() => PlanFifo([Layer(1, 5, 10m)], Qty(5.0000000001m), 2)).Code);
        Assert.Equal("insufficient_stock", Assert.Throws<CostingException>(() => PlanFifo([], Qty(1m), 2)).Code);
        Assert.Equal("invalid_quantity", Assert.Throws<CostingException>(() => PlanFifo([Layer(1, 5, 10m)], BigInteger.Zero, 2)).Code);
        Assert.Empty(PlanFifo([Layer(1, 5, 10m)], Qty(5m), 2).Where(t => t.Quantity.IsZero));       // never a zero-quantity (or negative) consumption
    }

    [Fact]
    public void Exhausted_layers_are_skipped()
    {
        var plan = PlanFifo([Layer(1, 0, 0m), Layer(2, 4, 8m)], Qty(1m), 2);
        Assert.Equal((1m, 2m), (Q(plan.Single().Quantity), A(plan.Single().Amount)));
    }

    [Theory]
    [InlineData("10", "100.00", "4", "40.00")]                // average 10.00: 4 x 10
    [InlineData("3", "100.00", "1", "33.33")]
    [InlineData("3", "100.00", "2", "66.67")]
    [InlineData("3", "100.00", "3", "100.00")]                // emptying the position returns the exact value: the balance ends at zero
    [InlineData("7", "0.00", "7", "0.00")]
    public void An_average_issue_takes_the_proportional_value_and_emptying_it_returns_everything(string onHand, string value, string issued, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), A(PlanAverage(Qty(decimal.Parse(onHand)), Amt(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)), Qty(decimal.Parse(issued)), 2)));

    [Fact]
    public void An_average_issue_above_the_balance_is_refused()
    {
        Assert.Equal("insufficient_stock", Assert.Throws<CostingException>(() => PlanAverage(Qty(3m), Amt(10m), Qty(3.0000000001m), 2)).Code);
        Assert.Equal("insufficient_stock", Assert.Throws<CostingException>(() => PlanAverage(BigInteger.Zero, BigInteger.Zero, Qty(1m), 2)).Code);
    }

    [Fact]
    public void The_moving_average_walks_exactly_through_receipts_and_issues_and_the_value_always_reconciles()
    {
        // receipt 10 @ 100.00; receipt 20 @ 260.00 (avg 12.00); issue 15; receipt 5 @ 70.00; issue all
        BigInteger qty = Qty(10m), value = Amt(100m);
        qty += Qty(20m); value += Amt(260m);
        Assert.Equal(12m, UnitCost(A(value), Q(qty)));
        var c1 = PlanAverage(qty, value, Qty(15m), 2);
        Assert.Equal(180m, A(c1));                                                    // 15 x 12.00
        qty -= Qty(15m); value -= c1;
        Assert.Equal((15m, 180m), (Q(qty), A(value)));
        qty += Qty(5m); value += Amt(70m);
        Assert.Equal((20m, 250m), (Q(qty), A(value)));
        Assert.Equal(12.5m, UnitCost(A(value), Q(qty)));
        var c2 = PlanAverage(qty, value, qty, 2);
        Assert.Equal(250m, A(c2));
        Assert.Equal(0m, A(value - c2));
    }

    [Fact]
    public void A_provisional_value_scales_a_pair_beyond_its_quantity_with_one_rounding_and_is_exact_for_the_whole()
    {
        // Phase 3.8B-3: the uncovered quantity may exceed the quantity of the (value, quantity) pair it is priced from
        Assert.Equal(35m, A(Scale(Amt(15m), Qty(7m), Qty(3m), 2)));                                         // 15.00 x 7 / 3
        Assert.Equal(6.67m, A(Scale(Amt(10m), Qty(2m), Qty(3m), 2)));                                       // half away from zero
        Assert.Equal(10m, A(Scale(Amt(10m), Qty(3m), Qty(3m), 2)));                                         // the whole: the exact value
        Assert.Equal(0m, A(Scale(Amt(0.01m), Qty(1m), Qty(1000m), 2)));                                     // rounds to zero: the caller refuses it, never accepts it
        Assert.Throws<CostingException>(() => Scale(Amt(1m), Qty(0m), Qty(1m), 2));
        Assert.Throws<CostingException>(() => Scale(Amt(1m), Qty(1m), Qty(0m), 2));
    }

    [Fact]
    public void The_derived_unit_cost_is_display_only_ten_decimals_and_zero_for_no_quantity()
    {
        Assert.Equal(0.3333333333m, UnitCost(1m, 3m));
        Assert.Equal(0m, UnitCost(5m, 0m));
        Assert.Equal(2.5m, UnitCost(10m, 4m));
    }
}
