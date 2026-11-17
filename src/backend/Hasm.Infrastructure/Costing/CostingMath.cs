using System.Numerics;

namespace Hasm.Infrastructure.Costing;

/// <summary>Valuation methods (D-017). Persisted; never renumber.</summary>
public enum CostMethod : short { Fifo = 1, MovingAverage = 2 }

/// <summary>Kind of a costing operation (persisted).</summary>
public enum CostOperationKind : short { Receipt = 1, Issue = 2, Transfer = 3, Reversal = 4 }

/// <summary>Kind of a ledger entry (persisted). A reversal keeps the kind of the entry it reverses and flips the direction.</summary>
public enum CostEntryKind : short { Receipt = 1, Issue = 2, TransferOut = 3, TransferIn = 4 }

/// <summary>
/// Pure, deterministic costing arithmetic (no database, no clock, no floating point). All figures are handled as exact integers in fixed units (quantity: 10^-10, amount: 10^-6), so the results never depend on the
/// platform and a proportion is always computed exactly before ONE rounding.
/// <para><b>Rounding rule (the only place cost is rounded):</b> when part of a layer or of an average position is issued, the cost is <c>amount x part / whole</c> rounded half away from zero to the minor units of the
/// company base currency. When the part is the WHOLE (the layer or the position is emptied) the cost is the exact remaining amount, so the remainder of every rounding lands on the last unit and a layer's amount and
/// everything issued from it always add up exactly; nothing is lost or invented. Receipts are never rounded: the caller states the total amount and the engine refuses one with more decimals than the currency allows.</para>
/// </summary>
public static class CostingMath
{
    public const int QuantityScale = 10;
    public const int AmountScale = 6;
    public const int MaxMoneyDecimals = 6;

    private static readonly BigInteger[] Pow = Enumerable.Range(0, 30).Select(i => BigInteger.Pow(10, i)).ToArray();

    /// <summary>The exact integer count of 10^-scale units of a decimal; throws when the value has more decimals than the scale (nothing is ever silently rounded).</summary>
    public static BigInteger ToUnits(decimal value, int scale)
    {
        var shifted = value;
        for (var i = 0; i < scale; i++) shifted *= 10m;
        if (decimal.Truncate(shifted) != shifted) throw new CostingException("precision_exceeded");
        return new BigInteger(shifted);
    }

    public static decimal FromUnits(BigInteger units, int scale)
    {
        var v = (decimal)units;
        for (var i = 0; i < scale; i++) v /= 10m;
        return v;
    }

    /// <summary>True when the value has at most <paramref name="decimals"/> decimals.</summary>
    public static bool FitsDecimals(decimal value, int decimals)
    {
        var shifted = value;
        for (var i = 0; i < decimals; i++) shifted *= 10m;
        return decimal.Truncate(shifted) == shifted;
    }

    /// <summary><c>amount x part / whole</c>, rounded half away from zero to <paramref name="moneyDecimals"/> decimals (all arguments are non-negative integer units; <paramref name="amountUnits"/> has scale 6).</summary>
    public static BigInteger Proportion(BigInteger amountUnits, BigInteger partQuantityUnits, BigInteger wholeQuantityUnits, int moneyDecimals)
    {
        if (wholeQuantityUnits.Sign <= 0 || partQuantityUnits.Sign < 0 || partQuantityUnits > wholeQuantityUnits || amountUnits.Sign < 0) throw new CostingException("invalid_proportion");
        if (partQuantityUnits == wholeQuantityUnits) return amountUnits;                          // the whole: exact remainder, no rounding
        var step = Pow[AmountScale - moneyDecimals];
        var num = amountUnits * partQuantityUnits;
        var den = wholeQuantityUnits * step;
        var q = (2 * num + den) / (2 * den);                                                      // floor((num / den) + 1/2) for non-negative numbers = half away from zero
        return q * step;
    }

    /// <summary>
    /// Phase 3.8B-3: the provisional value of an uncovered quantity priced from a (value, quantity) pair (the last average or the latest receipt): <c>amount x part / whole</c> with ONE rounding half away from zero to
    /// <paramref name="moneyDecimals"/>; unlike <see cref="Proportion"/> the part may exceed the whole (more is issued than the pair held). The whole is the exact amount.
    /// </summary>
    public static BigInteger Scale(BigInteger amountUnits, BigInteger partQuantityUnits, BigInteger wholeQuantityUnits, int moneyDecimals)
    {
        if (wholeQuantityUnits.Sign <= 0 || partQuantityUnits.Sign <= 0 || amountUnits.Sign < 0) throw new CostingException("invalid_proportion");
        if (partQuantityUnits == wholeQuantityUnits) return amountUnits;
        var step = Pow[AmountScale - moneyDecimals];
        var num = amountUnits * partQuantityUnits;
        var den = wholeQuantityUnits * step;
        return (2 * num + den) / (2 * den) * step;
    }

    /// <summary>One open FIFO layer as the planner sees it (integer units).</summary>
    public sealed record LayerState(Guid Id, long Seq, BigInteger RemainingQuantity, BigInteger RemainingAmount);

    public sealed record Take(Guid LayerId, BigInteger Quantity, BigInteger Amount);

    /// <summary>
    /// Plans a FIFO issue: layers in ascending sequence (arrival order), the oldest first, each consumed fully before the next; the last one possibly partially. Returns the consumptions or throws
    /// <c>insufficient_stock</c> (the planner never produces a negative layer). Deterministic: the same layers and quantity give the same plan.
    /// </summary>
    public static IReadOnlyList<Take> PlanFifo(IEnumerable<LayerState> layers, BigInteger quantity, int moneyDecimals)
    {
        if (quantity.Sign <= 0) throw new CostingException("invalid_quantity");
        var left = quantity;
        var plan = new List<Take>();
        foreach (var l in layers.Where(l => l.RemainingQuantity.Sign > 0).OrderBy(l => l.Seq))
        {
            var take = BigInteger.Min(left, l.RemainingQuantity);
            var amount = take == l.RemainingQuantity ? l.RemainingAmount : Proportion(l.RemainingAmount, take, l.RemainingQuantity, moneyDecimals);
            plan.Add(new Take(l.Id, take, amount));
            left -= take;
            if (left.IsZero) return plan;
        }
        throw new CostingException("insufficient_stock");
    }

    /// <summary>Moving weighted average issue: the cost of <paramref name="quantity"/> out of a position holding <paramref name="onHand"/> worth <paramref name="value"/>. Issuing everything returns the exact value.</summary>
    public static BigInteger PlanAverage(BigInteger onHand, BigInteger value, BigInteger quantity, int moneyDecimals)
    {
        if (quantity.Sign <= 0) throw new CostingException("invalid_quantity");
        if (quantity > onHand) throw new CostingException("insufficient_stock");
        return Proportion(value, quantity, onHand, moneyDecimals);
    }

    /// <summary>The (derived, display-only) unit cost: amount / quantity to 10 decimals, 0 for an empty quantity. Never stored and never used to compute a cost.</summary>
    public static decimal UnitCost(decimal amount, decimal quantity) => quantity == 0m ? 0m : decimal.Round(amount / quantity, 10, MidpointRounding.AwayFromZero);
}

/// <summary>A refused costing operation. <see cref="Code"/> is a stable machine-readable reason (see the accounting integration contract for the list).</summary>
public sealed class CostingException(string code, string? detail = null) : Exception(detail is null ? code : code + ": " + detail)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}
