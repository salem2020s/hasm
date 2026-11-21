using System.Numerics;

namespace Hasm.Infrastructure.Pricing;

/// <summary>
/// The pure pricing engine (SRS 8.1-8.4): deterministic, no database, clock, culture or floating point. <see cref="Resolve"/> selects the price and the discount by explicit, documented priority and
/// records every step; <see cref="Calculate"/> turns the selected VALUES into the result with ONE rounding per amount. The same inputs always give the same price and the same recorded rule.
/// <para>Price selection: eligible lists (active, in date, same currency, customer / group / general scope) are ordered by scope specificity (customer, group, general), then list priority (lower first),
/// then the later start, then code, then id; the FIRST list that holds a matching entry wins (item entry before item-group entry, exact unit, the highest quantity tier not above the quantity).
/// A list that cannot price the line is skipped and the skip is traced. No eligible price is a refusal: there is never an implicit zero.</para>
/// <para>Discount selection: applicable rules (active, in date, scope matches, quantity at or above the minimum) are ordered by customer specificity, item specificity, priority, higher minimum quantity,
/// later start, code, id; the first wins and discounts never stack. Two applicable rules identical in every ordering key before the code are ambiguous and refused.</para>
/// </summary>
public static class PricingEngine
{
    public const int MaxDecimals = 6;
    private const decimal MaxQuantity = 1_000_000_000m, MaxPrice = 1_000_000_000_000m;

    private static PricingException Refuse(string code) => new(code);
    private static bool Scale(decimal v, int places) => decimal.Round(v, places) == v;

    public static void ValidatePolicy(PricingPolicy policy)
    {
        if (policy is null) throw Refuse("invalid_policy");
        if (policy.PriceDecimals is < 0 or > MaxDecimals || policy.MoneyDecimals is < 0 or > MaxDecimals) throw Refuse("invalid_policy");
        if (policy.MinMarginPercent is { } m && (m < -1000m || m > 100m || !Scale(m, 4))) throw Refuse("invalid_policy");
        if (!Enum.IsDefined(policy.Action) || !Enum.IsDefined(policy.ListPrecedence) || !Enum.IsDefined(policy.DiscountPrecedence)) throw Refuse("invalid_policy");
    }

    private static void ValidateRequest(PricingRequest r)
    {
        if (r is null) throw Refuse("invalid_request");
        if (r.Quantity <= 0 || r.Quantity > MaxQuantity || !Scale(r.Quantity, 10)) throw Refuse("invalid_quantity");
        if (string.IsNullOrWhiteSpace(r.Currency) || r.Currency.Trim().Length != 3 || !r.Currency.Trim().All(char.IsAsciiLetter)) throw Refuse("invalid_currency");
        if (r.UnitCost is { } c && (c < 0 || c > MaxPrice || !Scale(c, MaxDecimals))) throw Refuse("invalid_cost");
    }

    private static string Ref(string kind, Guid id, string code) => $"{kind}:{id:N}@{code}";
    private static bool InRange(DateOnly date, DateOnly from, DateOnly? to) => from <= date && (to is null || date <= to);

    /// <summary>Selects the price and the discount. Lists, entries and rules are DATA of the company: invalid or ambiguous data is refused, never guessed.</summary>
    public static ResolvedPricing Resolve(PricingRequest request, IReadOnlyList<PriceListRecord> lists, IReadOnlyList<PriceEntryRecord> entries, IReadOnlyList<DiscountRuleRecord> discounts, PricingPolicy? policy = null)
    {
        ValidateRequest(request);
        if (policy is not null) ValidatePolicy(policy);
        var listPrecedence = policy?.ListPrecedence ?? PriceListPrecedence.ScopeThenPriority;
        var discountPrecedence = policy?.DiscountPrecedence ?? DiscountPrecedence.SpecificityThenPriority;
        if (lists is null || entries is null || discounts is null) throw Refuse("invalid_request");
        var currency = request.Currency.Trim().ToUpperInvariant();
        var trace = new List<TraceStep>();
        if (listPrecedence != PriceListPrecedence.ScopeThenPriority) trace.Add(new("policy", "list-precedence:priority-then-scope", "applied"));
        if (discountPrecedence != DiscountPrecedence.SpecificityThenPriority) trace.Add(new("policy", "discount-precedence:priority-then-specificity", "applied"));

        // ---- price
        var eligible = new List<(PriceListRecord List, int Scope)>();
        foreach (var l in lists)
        {
            if (!l.IsActive) continue;                                                       // an inactive list is never evaluated, so its data cannot block pricing
            if ((l.CustomerId is not null && l.CustomerGroupId is not null) || (l.ValidTo is { } vt && vt < l.ValidFrom)) throw Refuse("invalid_price_list");
            if ( !InRange(request.Date, l.ValidFrom, l.ValidTo) || !string.Equals(l.Currency?.Trim(), currency, StringComparison.OrdinalIgnoreCase)) continue;
            if (l.CustomerId is { } cu) { if (cu != request.CustomerId) continue; eligible.Add((l, 2)); }
            else if (l.CustomerGroupId is { } g) { if (g != request.CustomerGroupId) continue; eligible.Add((l, 1)); }
            else eligible.Add((l, 0));
        }
        var orderedLists = listPrecedence == PriceListPrecedence.ScopeThenPriority
            ? eligible.OrderByDescending(x => x.Scope).ThenBy(x => x.List.Priority)
            : eligible.OrderBy(x => x.List.Priority).ThenByDescending(x => x.Scope);
        foreach (var (list, _) in orderedLists.ThenByDescending(x => x.List.ValidFrom).ThenBy(x => x.List.Code, StringComparer.Ordinal).ThenBy(x => x.List.Id))
        {
            foreach (var bad in entries.Where(e => e.PriceListId == list.Id))
                if (bad.MinQuantity < 0 || !Scale(bad.MinQuantity, 10) || (bad.ItemId is not null && bad.ItemGroupId is not null) || (bad.ItemId is null && bad.ItemGroupId is null)) throw Refuse("invalid_price");
            var mine = entries.Where(e => e.PriceListId == list.Id && e.UnitId == request.UnitId && e.MinQuantity <= request.Quantity
                && ((e.ItemId is not null && e.ItemId == request.ItemId) || (e.ItemId is null && e.ItemGroupId is not null && e.ItemGroupId == request.ItemGroupId))).ToList();
            var listRef = Ref("list", list.Id, list.Code);
            if (mine.Count == 0) { trace.Add(new("price", listRef, "skipped:no_entry")); continue; }
            var byItem = mine.Where(e => e.ItemId is not null).ToList();
            var pool = byItem.Count > 0 ? byItem : mine;                                       // an item entry beats an item-group entry of the same list
            var tier = pool.Max(e => e.MinQuantity);
            var top = pool.Where(e => e.MinQuantity == tier).ToList();
            if (top.Count != 1) throw Refuse("ambiguous_price");
            var e0 = top[0];
            if (e0.UnitPrice < 0 || e0.UnitPrice > MaxPrice || !Scale(e0.UnitPrice, MaxDecimals)) throw Refuse("invalid_price");
            var priceRef = $"{listRef};tier:{tier.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            trace.Add(new("price", priceRef, "selected"));
            return ResolveDiscount(request, e0.UnitPrice, priceRef, discounts, trace, discountPrecedence);
        }
        throw Refuse("no_price");
    }

    private static ResolvedPricing ResolveDiscount(PricingRequest request, decimal price, string priceRef, IReadOnlyList<DiscountRuleRecord> discounts, List<TraceStep> trace, DiscountPrecedence precedence)
    {
        var applicable = new List<(DiscountRuleRecord Rule, int Customer, int Item)>();
        foreach (var d in discounts)
        {
            if (!d.IsActive) continue;                                                       // an inactive rule is never evaluated, so its data cannot block pricing
            if (d.Percent < 0 || d.Percent > 100 || !Scale(d.Percent, 4) || d.MinQuantity < 0 || !Scale(d.MinQuantity, 10) || (d.ValidTo is { } dv && dv < d.ValidFrom)) throw Refuse("invalid_discount");
            if ((d.CustomerId is not null && d.CustomerGroupId is not null) || (d.ItemId is not null && d.ItemGroupId is not null)) throw Refuse("invalid_discount");
            if (!InRange(request.Date, d.ValidFrom, d.ValidTo) || request.Quantity < d.MinQuantity) continue;
            int cs;
            if (d.CustomerId is { } cu) { if (cu != request.CustomerId) continue; cs = 2; }
            else if (d.CustomerGroupId is { } g) { if (g != request.CustomerGroupId) continue; cs = 1; }
            else cs = 0;
            int its;
            if (d.ItemId is { } it) { if (it != request.ItemId) continue; its = 2; }
            else if (d.ItemGroupId is { } ig) { if (ig != request.ItemGroupId) continue; its = 1; }
            else its = 0;
            applicable.Add((d, cs, its));
        }
        if (applicable.Count == 0) { trace.Add(new("discount", "none", "no_applicable_rule")); return new(price, priceRef, 0m, null, trace.ToArray()); }
        var ordered = (precedence == DiscountPrecedence.SpecificityThenPriority
            ? applicable.OrderByDescending(x => x.Customer).ThenByDescending(x => x.Item).ThenBy(x => x.Rule.Priority)
            : applicable.OrderBy(x => x.Rule.Priority).ThenByDescending(x => x.Customer).ThenByDescending(x => x.Item)).ThenByDescending(x => x.Rule.MinQuantity)
            .ThenByDescending(x => x.Rule.ValidFrom).ThenBy(x => x.Rule.Code, StringComparer.Ordinal).ThenBy(x => x.Rule.Id).ToList();
        var first = ordered[0];
        if (ordered.Count > 1)
        {
            var s = ordered[1];
            if (s.Customer == first.Customer && s.Item == first.Item && s.Rule.Priority == first.Rule.Priority && s.Rule.MinQuantity == first.Rule.MinQuantity && s.Rule.ValidFrom == first.Rule.ValidFrom) throw Refuse("ambiguous_discount");
        }
        foreach (var other in ordered.Skip(1)) trace.Add(new("discount", Ref("discount", other.Rule.Id, other.Rule.Code), "not_selected:lower_precedence"));
        var dref = Ref("discount", first.Rule.Id, first.Rule.Code);
        trace.Add(new("discount", dref, "selected"));
        return new(price, priceRef, first.Rule.Percent, dref, trace.ToArray());
    }

    private static decimal Round(decimal v, int places) => decimal.Round(v, places, MidpointRounding.AwayFromZero);

    /// <summary>net x quantity rounded once, half away from zero, computed EXACTLY on integers (a decimal product could overflow or round silently before the real rounding). Too large a result is a refusal, never an overflow.</summary>
    private static decimal ExactLine(decimal net, decimal quantity, int places)
    {
        try
        {
            static (BigInteger Value, int Scale) Scaled(decimal v) { var bits = decimal.GetBits(v); var scale = (bits[3] >> 16) & 0xFF; var mantissa = new BigInteger(new decimal(bits[0], bits[1], bits[2], false, 0)); return (v < 0 ? -mantissa : mantissa, scale); }
            var (a, sa) = Scaled(net); var (b, sb) = Scaled(quantity);
            var product = a * b; var scale = sa + sb;                                              // value = product / 10^scale
            var shift = scale - places;
            if (shift > 0)
            {
                var divisor = BigInteger.Pow(10, shift);
                var q = BigInteger.DivRem(BigInteger.Abs(product), divisor, out var rem);
                if (rem * 2 >= divisor) q += 1;                                                    // half away from zero
                product = product.Sign < 0 ? -q : q; scale = places;
            }
            var result = (decimal)product;
            for (var i = 0; i < scale; i++) result /= 10m;
            return result;
        }
        catch (OverflowException) { throw Refuse("amount_too_large"); }
    }

    /// <summary>Arithmetic only: net unit price = round(list price x (1 - discount / 100)), line net = round(net unit price x quantity). An override replaces the selected price and/or discount (a reason is mandatory).</summary>
    public static PricingResult Calculate(PricingPolicy policy, PricingRequest request, ResolvedPricing resolved, PriceOverride? over = null)
    {
        ValidatePolicy(policy); ValidateRequest(request);
        if (resolved is null || resolved.ListUnitPrice < 0 || resolved.ListUnitPrice > MaxPrice || !Scale(resolved.ListUnitPrice, MaxDecimals) || resolved.DiscountPercent < 0 || resolved.DiscountPercent > 100 || !Scale(resolved.DiscountPercent, 4))
            throw Refuse("invalid_resolution");
        if (string.IsNullOrWhiteSpace(resolved.PriceRuleRef) || resolved.Trace is null || (resolved.DiscountPercent > 0 && string.IsNullOrWhiteSpace(resolved.DiscountRuleRef))) throw Refuse("invalid_resolution");   // the chosen rule must always be on record
        var price = resolved.ListUnitPrice; var discount = resolved.DiscountPercent; string? reason = null; var overridden = false;
        var trace = resolved.Trace.ToList();
        var priceRef = resolved.PriceRuleRef; var discountRef = resolved.DiscountRuleRef;
        if (over is not null)
        {
            if (string.IsNullOrWhiteSpace(over.Reason)) throw Refuse("override_reason_required");
            if (over.Reason.Trim().Length > 500) throw Refuse("override_reason_invalid");
            if (over.UnitPrice is null && over.DiscountPercent is null) throw Refuse("override_empty");
            if (over.UnitPrice is { } op) { if (op < 0 || op > MaxPrice || !Scale(op, MaxDecimals)) throw Refuse("invalid_override"); price = op; }
            if (over.DiscountPercent is { } od) { if (od < 0 || od > 100 || !Scale(od, 4)) throw Refuse("invalid_override"); discount = od; }
            overridden = true; reason = over.Reason.Trim();
            // the rules that would have applied, and their values, stay on record; the result no longer claims them for the replaced part
            trace.Add(new("override", "manual", $"price={(over.UnitPrice is null ? "rule" : $"manual(replaced {resolved.PriceRuleRef}={resolved.ListUnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)})")};discount={(over.DiscountPercent is null ? "rule" : $"manual(replaced {resolved.DiscountRuleRef ?? "none"}={resolved.DiscountPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)})")}"));
            if (over.UnitPrice is not null) priceRef = "manual-override";
            if (over.DiscountPercent is not null) discountRef = "manual-override";
        }
        var net = Round(price * (100m - discount) / 100m, policy.PriceDecimals);
        var line = ExactLine(net, request.Quantity, policy.MoneyDecimals);

        decimal? margin = null; var status = MarginStatus.NotEvaluated; var approval = false;
        if (policy.MinMarginPercent is { } min)
        {
            if (request.UnitCost is not { } cost)
            {
                if (policy.Action != MarginAction.Warn) throw Refuse("cost_unavailable");
                trace.Add(new("margin", "policy", "not_evaluated:cost_unavailable"));
            }
            else
            {
                margin = net == 0m ? (cost == 0m ? 0m : -100m) : Round((net - cost) / net * 100m, 4);       // zero price with a cost is a total loss; the report caps it at -100
                if (margin < min)
                {
                    status = MarginStatus.BelowMinimum;
                    if (policy.Action == MarginAction.Block) throw Refuse("margin_below_minimum");
                    approval = policy.Action == MarginAction.RequireApproval;
                    trace.Add(new("margin", "policy", approval ? "below_minimum:approval_required" : "below_minimum:warning"));
                }
                else { status = MarginStatus.Ok; trace.Add(new("margin", "policy", "ok")); }
            }
        }
        return new(price, priceRef, discount, discountRef, net, line, overridden, reason, margin, status, approval, trace.ToArray());
    }
}
