using System.Numerics;

namespace Hasm.Infrastructure.Taxes;

/// <summary>
/// Pure, deterministic tax arithmetic (SRS 6): no database, clock, culture or floating point. Every figure is an exact rational (<see cref="BigInteger"/> fraction) and every
/// amount is rounded exactly ONCE under the policy (see docs/PHASE-3.10.md for the full contract: exclusive/inclusive pricing, compound bases, per-line and per-document
/// rounding with largest-remainder allocation, the inclusive residual rule). Inputs are validated completely before anything is calculated; a refusal never yields a partial result.
/// </summary>
public static class TaxEngine
{
    public const int MaxLines = 5000, MaxTaxesPerLine = 8, QuantityDecimals = 10, PriceDecimals = 6, RateDecimals = 6, MaxMoneyDecimals = 6;
    private const decimal MaxInput = 1_000_000_000_000_000m;       // inputs above 10^15 are refused before any scaling (no overflow paths)
    private static readonly BigInteger MaxLineUnits = BigInteger.Pow(10, 15);   // a line amount above 10^15 (before scaling) is refused

    private static TaxException Refuse(string code) => new(code);

    // ------------------------------------------------------------------------------------------------------------------------ exact rationals
    private readonly record struct Rat(BigInteger N, BigInteger D)
    {
        internal static Rat Int(BigInteger n) => new Rat(n, BigInteger.One);
        internal static Rat Of(decimal d)
        {
            var bits = decimal.GetBits(d);
            var scale = (bits[3] >> 16) & 0xFF;
            var magnitude = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
            return new Rat((bits[3] & int.MinValue) != 0 ? -magnitude : magnitude, BigInteger.Pow(10, scale)).Reduce();
        }
        internal Rat Reduce() { var g = BigInteger.GreatestCommonDivisor(N, D); return g.IsOne || g.IsZero ? this : new(N / g, D / g); }
        public static Rat operator +(Rat a, Rat b) => new(a.N * b.D + b.N * a.D, a.D * b.D);
        public static Rat operator -(Rat a, Rat b) => new(a.N * b.D - b.N * a.D, a.D * b.D);
        public static Rat operator *(Rat a, Rat b) => new(a.N * b.N, a.D * b.D);
        public static Rat operator /(Rat a, Rat b) => new(a.N * b.D, a.D * b.N);
        internal int CompareTo(Rat o) => (N * o.D).CompareTo(o.N * D);
        internal bool IsNegative => N.Sign < 0;
    }

    private static BigInteger Pow10(int n) => BigInteger.Pow(10, n);

    /// <summary>Rounds a NON-NEGATIVE rational (a count of 10^-decimals units) to an integer, once, under the mode.</summary>
    private static BigInteger RoundUnits(Rat value, TaxRoundingMode mode)
    {
        var quotient = BigInteger.DivRem(value.N, value.D, out var remainder);
        var twice = remainder * 2;
        var cmp = twice.CompareTo(value.D);
        if (cmp > 0) return quotient + 1;
        if (cmp < 0) return quotient;
        return mode == TaxRoundingMode.HalfAwayFromZero || !quotient.IsEven ? quotient + 1 : quotient;
    }

    private static decimal FromUnits(BigInteger units, int decimals) => (decimal)units / (decimal)Pow10(decimals);

    private static int ScaleOf(decimal value)
    {
        var stripped = value / 1.0000000000000000000000000000m;   // removes trailing zeros so 1.50 has scale 1
        return (decimal.GetBits(stripped)[3] >> 16) & 0xFF;
    }

    // ------------------------------------------------------------------------------------------------------------------------ validation
    private sealed record Prepared(TaxLineInput Line, TaxComponent[] Taxes, Rat Quantity, Rat Price, Rat Discount);

    private static Prepared[] Validate(TaxPolicy policy, IReadOnlyList<TaxLineInput> lines)
    {
        if (policy is null || lines is null) throw Refuse("invalid_tax_input");
        if (policy.MoneyDecimals is < 0 or > MaxMoneyDecimals || !Enum.IsDefined(policy.Mode) || !Enum.IsDefined(policy.Level)) throw Refuse("invalid_tax_policy");
        if (lines.Count == 0 || lines.Count > MaxLines) throw Refuse("invalid_tax_lines");
        var numbers = new HashSet<int>();
        var prepared = new List<Prepared>(lines.Count);
        foreach (var line in lines)
        {
            if (line is null || line.Taxes is null || !numbers.Add(line.LineNo)) throw Refuse("invalid_tax_lines");
            if (line.Quantity < 0 || line.UnitPrice < 0 || line.Discount < 0) throw Refuse("negative_amount");
            if (line.Quantity > MaxInput || line.UnitPrice > MaxInput || line.Discount > MaxInput) throw Refuse("amount_too_large");
            if (ScaleOf(line.Quantity) > QuantityDecimals || ScaleOf(line.UnitPrice) > PriceDecimals || ScaleOf(line.Discount) > policy.MoneyDecimals) throw Refuse("precision_exceeded");
            if (line.PriceIncludesTax && policy.Level != TaxRoundingLevel.PerLine) throw Refuse("inclusive_requires_line_rounding");
            if (line.Taxes.Count > MaxTaxesPerLine) throw Refuse("too_many_taxes");
            var taxes = line.Taxes.OrderBy(t => t.Priority).ToArray();
            var seenIds = new HashSet<Guid>(); var priorities = new HashSet<int>();
            foreach (var tax in taxes)
            {
                if (tax is null || tax.TaxId == Guid.Empty || string.IsNullOrWhiteSpace(tax.Code) || !Enum.IsDefined(tax.Treatment)) throw Refuse("invalid_tax");
                if (!seenIds.Add(tax.TaxId)) throw Refuse("duplicate_tax");
                if (!priorities.Add(tax.Priority)) throw Refuse("duplicate_priority");
                if (tax.RatePercent < 0 || tax.RatePercent > 1000 || ScaleOf(tax.RatePercent) > RateDecimals) throw Refuse("invalid_rate");
                if (tax.Treatment != TaxTreatment.Standard && tax.RatePercent != 0) throw Refuse("treatment_rate_mismatch");
                foreach (var included in tax.IncludesInBase ?? [])
                    if (!taxes.TakeWhile(t => t != tax).Any(t => t.TaxId == included)) throw Refuse("invalid_base_reference");   // only EARLIER taxes of this line
            }
            var quantity = Rat.Of(line.Quantity); var price = Rat.Of(line.UnitPrice); var discount = Rat.Of(line.Discount);
            if (discount.CompareTo(quantity * price) > 0) throw Refuse("discount_exceeds_amount");
            prepared.Add(new(line, taxes, quantity, price, discount));
        }
        return [.. prepared];
    }

    private static bool Taxable(TaxComponent tax) => tax.Treatment == TaxTreatment.Standard && tax.RatePercent > 0;
    private static Rat Fraction(decimal ratePercent) => Rat.Of(ratePercent) / Rat.Of(100m);

    // ------------------------------------------------------------------------------------------------------------------------ calculation
    private sealed class LineWork(Prepared prepared)
    {
        internal Prepared P { get; } = prepared;
        internal BigInteger Net;
        internal readonly Dictionary<Guid, BigInteger> Amounts = [];
        internal readonly Dictionary<Guid, BigInteger> Bases = [];
        internal readonly Dictionary<Guid, Rat> Exact = [];     // per-document mode: exact amount in units
    }

    public static TaxDocumentResult Calculate(TaxPolicy policy, IReadOnlyList<TaxLineInput> lines, CancellationToken ct = default)
    {
        var prepared = Validate(policy, lines);
        var work = prepared.Select(p => new LineWork(p)).ToArray();
        var scale = Rat.Int(Pow10(policy.MoneyDecimals));
        foreach (var w in work)
        {
            ct.ThrowIfCancellationRequested();
            var amount = (w.P.Quantity * w.P.Price - w.P.Discount) * scale;                // the line amount in units, exact
            var line = RoundUnits(amount, policy.Mode);
            if (line > MaxLineUnits * Pow10(policy.MoneyDecimals)) throw Refuse("amount_too_large");
            if (w.P.Line.PriceIncludesTax) Inclusive(w, line, policy);
            else if (policy.Level == TaxRoundingLevel.PerLine) ExclusivePerLine(w, line, policy);
            else ExclusiveExact(w, line);
        }
        if (policy.Level == TaxRoundingLevel.PerDocument) AllocatePerDocument(work, policy);
        return Result(work, policy);
    }

    private static void ExclusivePerLine(LineWork w, BigInteger net, TaxPolicy policy)
    {
        w.Net = net;
        foreach (var tax in w.P.Taxes)
        {
            var baseUnits = net + (tax.IncludesInBase ?? []).Aggregate(BigInteger.Zero, (sum, id) => sum + w.Amounts[id]);   // earlier amounts as ROUNDED (as printed)
            w.Bases[tax.TaxId] = baseUnits;
            w.Amounts[tax.TaxId] = Taxable(tax) ? RoundUnits(Rat.Int(baseUnits) * Fraction(tax.RatePercent), policy.Mode) : BigInteger.Zero;
        }
    }

    private static void ExclusiveExact(LineWork w, BigInteger net)
    {
        w.Net = net;
        var exact = new Dictionary<Guid, Rat>();
        foreach (var tax in w.P.Taxes)
        {
            var baseRat = Rat.Int(net);
            foreach (var id in tax.IncludesInBase ?? []) baseRat += exact[id];                  // earlier amounts EXACT in this mode
            exact[tax.TaxId] = Taxable(tax) ? baseRat * Fraction(tax.RatePercent) : Rat.Int(BigInteger.Zero);
        }
        foreach (var (id, value) in exact) w.Exact[id] = value;
    }

    private static void Inclusive(LineWork w, BigInteger gross, TaxPolicy policy)
    {
        // multipliers m_i = 1 + sum(r_j m_j) over the earlier taxes included in tax i's base; net0 = G / (1 + sum r_i m_i)
        var multiplier = new Dictionary<Guid, Rat>(); var exactTax = new Dictionary<Guid, Rat>();
        var denominator = Rat.Int(BigInteger.One);
        foreach (var tax in w.P.Taxes)
        {
            var m = Rat.Int(BigInteger.One);
            foreach (var id in tax.IncludesInBase ?? []) m += Fraction(w.P.Taxes.Single(t => t.TaxId == id).RatePercent) * multiplier[id];
            multiplier[tax.TaxId] = m;
            if (Taxable(tax)) denominator += Fraction(tax.RatePercent) * m;
        }
        var net0 = Rat.Int(gross) / denominator;
        var net = RoundUnits(net0, policy.Mode);
        foreach (var tax in w.P.Taxes) exactTax[tax.TaxId] = Taxable(tax) ? net0 * Fraction(tax.RatePercent) * multiplier[tax.TaxId] : Rat.Int(BigInteger.Zero);
        foreach (var tax in w.P.Taxes) w.Amounts[tax.TaxId] = RoundUnits(exactTax[tax.TaxId], policy.Mode);
        // the GROSS is authoritative: the last taxable tax absorbs the residual; if that would make it negative the net absorbs it
        var residual = gross - net - w.Amounts.Values.Aggregate(BigInteger.Zero, (a, b) => a + b);
        var last = w.P.Taxes.LastOrDefault(Taxable);
        if (last is null) net = gross;
        else if (w.Amounts[last.TaxId] + residual >= 0) w.Amounts[last.TaxId] += residual;
        else net += residual;
        w.Net = net;
        foreach (var tax in w.P.Taxes)
            w.Bases[tax.TaxId] = net + (tax.IncludesInBase ?? []).Aggregate(BigInteger.Zero, (sum, id) => sum + w.Amounts[id]);
    }

    private static void AllocatePerDocument(LineWork[] work, TaxPolicy policy)
    {
        var ids = work.SelectMany(w => w.Exact.Keys).Distinct().ToArray();
        foreach (var id in ids)
        {
            var lines = work.Where(w => w.Exact.ContainsKey(id)).ToArray();
            var floors = new BigInteger[lines.Length]; var fractions = new Rat[lines.Length];
            var sum = Rat.Int(BigInteger.Zero);
            for (var i = 0; i < lines.Length; i++)
            {
                var exact = lines[i].Exact[id];
                floors[i] = BigInteger.Divide(exact.N, exact.D);
                fractions[i] = exact - Rat.Int(floors[i]);
                sum += exact;
            }
            var total = RoundUnits(sum, policy.Mode);
            var extra = total - floors.Aggregate(BigInteger.Zero, (a, b) => a + b);
            if (extra < 0 || extra > lines.Length) throw Refuse("allocation_inconsistent");
            // largest remainder first, ties to the lower line number
            var order = Enumerable.Range(0, lines.Length).OrderByDescending(i => fractions[i], Comparer<Rat>.Create((a, b) => a.CompareTo(b))).ThenBy(i => lines[i].P.Line.LineNo).ToArray();
            for (var k = 0; k < (int)extra; k++) floors[order[k]] += 1;
            for (var i = 0; i < lines.Length; i++) lines[i].Amounts[id] = floors[i];
        }
        foreach (var w in work)                                                                  // informational line bases: net + the ALLOCATED earlier amounts
            foreach (var tax in w.P.Taxes)
                w.Bases[tax.TaxId] = w.Net + (tax.IncludesInBase ?? []).Aggregate(BigInteger.Zero, (sum, included) => sum + w.Amounts[included]);
    }

    private static TaxDocumentResult Result(LineWork[] work, TaxPolicy policy)
    {
        var d = policy.MoneyDecimals;
        var lineResults = new List<TaxLineResult>(work.Length);
        var totals = new Dictionary<Guid, (TaxComponent Tax, BigInteger Base, BigInteger Amount)>();
        BigInteger totalNet = 0, totalTax = 0;
        foreach (var w in work)
        {
            var amounts = new List<TaxAmount>(w.P.Taxes.Length); BigInteger lineTax = 0;
            foreach (var tax in w.P.Taxes)
            {
                var amount = w.Amounts[tax.TaxId]; var baseUnits = w.Bases.TryGetValue(tax.TaxId, out var b) ? b : w.Net;
                lineTax += amount;
                amounts.Add(new(tax.TaxId, tax.Code, tax.Treatment, tax.RatePercent, FromUnits(baseUnits, d), FromUnits(amount, d)));
                (TaxComponent Tax, BigInteger Base, BigInteger Amount) current = totals.TryGetValue(tax.TaxId, out var t) ? t : (tax, BigInteger.Zero, BigInteger.Zero);
                totals[tax.TaxId] = (current.Tax, current.Base + baseUnits, current.Amount + amount);
            }
            totalNet += w.Net; totalTax += lineTax;
            lineResults.Add(new(w.P.Line.LineNo, FromUnits(w.Net, d), FromUnits(lineTax, d), FromUnits(w.Net + lineTax, d), amounts));
        }
        var byTax = totals.OrderBy(t => t.Value.Tax.Priority).ThenBy(t => t.Key)
            .Select(t => new TaxTotal(t.Key, t.Value.Tax.Code, t.Value.Tax.Treatment, FromUnits(t.Value.Base, d), FromUnits(t.Value.Amount, d))).ToList();
        return new(lineResults, byTax, FromUnits(totalNet, d), FromUnits(totalTax, d), FromUnits(totalNet + totalTax, d));
    }
}
