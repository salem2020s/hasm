namespace Hasm.Infrastructure.Pricing;

/// <summary>A refusal with a stable code (no partial result is ever returned). Codes are persisted in documents and tests: never rename.</summary>
public sealed class PricingException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>What a margin below the minimum does: Warn only reports it; RequireApproval flags the line for an exceptional approval; Block refuses the price.</summary>
public enum MarginAction { Warn = 1, RequireApproval = 2, Block = 3 }

public enum MarginStatus { NotEvaluated = 0, Ok = 1, BelowMinimum = 2 }

/// <summary>
/// How competing price lists are ordered (company-configurable; owner decision P). <see cref="ScopeThenPriority"/> (default): customer list, then group list, then general list; list priority only decides among lists
/// of the same scope. <see cref="PriorityThenScope"/>: list priority decides first (lower first); scope specificity only breaks ties between lists of equal priority. Either way the order is total and deterministic.
/// </summary>
public enum PriceListPrecedence { ScopeThenPriority = 1, PriorityThenScope = 2 }

/// <summary>
/// How competing discount rules are ordered (company-configurable). <see cref="SpecificityThenPriority"/> (default): customer specificity, then item specificity, then priority. <see cref="PriorityThenSpecificity"/>:
/// priority first (lower first), then customer and item specificity. Discounts never stack in either mode: exactly one applies (stacking is not an approved requirement and is not offered).
/// </summary>
public enum DiscountPrecedence { SpecificityThenPriority = 1, PriorityThenSpecificity = 2 }

/// <summary>
/// The company's pricing policy. <see cref="PriceDecimals"/> is the scale of a unit price (0-6), <see cref="MoneyDecimals"/> the scale of a line amount (0-6); both round half away from zero, ONCE.
/// A <see cref="MinMarginPercent"/> of null disables the margin control; with a minimum set, a missing cost is refused (<c>cost_unavailable</c>) when the action is Block or RequireApproval, because
/// a control that silently skips itself is not a control.
/// </summary>
public sealed record PricingPolicy(int PriceDecimals, int MoneyDecimals, decimal? MinMarginPercent = null, MarginAction Action = MarginAction.Warn,
    PriceListPrecedence ListPrecedence = PriceListPrecedence.ScopeThenPriority, DiscountPrecedence DiscountPrecedence = DiscountPrecedence.SpecificityThenPriority)
{
    /// <summary>True when both precedences are the defaults of slice 1 (a snapshot of such a policy keeps the v1 form and hash).</summary>
    public bool HasDefaultPrecedence => ListPrecedence == PriceListPrecedence.ScopeThenPriority && DiscountPrecedence == DiscountPrecedence.SpecificityThenPriority;
}

/// <summary>A price list. Scope: for ONE customer, for a customer group, or general (both null). Both set is invalid data. Lower <see cref="Priority"/> wins among lists of equal scope.</summary>
public sealed record PriceListRecord(Guid Id, string Code, int Priority, string Currency, DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive, Guid? CustomerId = null, Guid? CustomerGroupId = null);

/// <summary>One price of an item (or of an item group) in a unit, from a minimum quantity (a quantity tier). An item entry beats an item-group entry of the same list.</summary>
public sealed record PriceEntryRecord(Guid PriceListId, Guid? ItemId, Guid? ItemGroupId, Guid UnitId, decimal MinQuantity, decimal UnitPrice);

/// <summary>A percentage discount. Specificity (customer, then item) decides before priority; rules never stack: exactly one discount applies.</summary>
public sealed record DiscountRuleRecord(Guid Id, string Code, int Priority, DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive, decimal Percent, decimal MinQuantity = 0,
    Guid? CustomerId = null, Guid? CustomerGroupId = null, Guid? ItemId = null, Guid? ItemGroupId = null);

/// <summary>
/// The priced line. DECISIONS (recorded in docs/PHASE-3.11.md): the quantity is expressed in the unit being priced (a price entry exists per unit; conversion between units is the caller's job before pricing,
/// never implied here) and the currency is the document currency (a price list is in one currency; conversion is the document framework's job). UnitCost is optional input of the margin control and MUST be in the
/// same unit and currency as the price.
/// </summary>
public sealed record PricingRequest(DateOnly Date, string Currency, Guid ItemId, Guid? ItemGroupId, Guid UnitId, decimal Quantity, Guid? CustomerId = null, Guid? CustomerGroupId = null, decimal? UnitCost = null);

/// <summary>An authorized manual override (the permission is checked by the caller): a price and/or a discount percent, with a mandatory reason. The rule that WOULD have applied stays in the trace.</summary>
public sealed record PriceOverride(decimal? UnitPrice, decimal? DiscountPercent, string Reason);

public sealed record TraceStep(string Kind, string RuleRef, string Outcome);

/// <summary>What the rule selection chose: values, not references, so the arithmetic (and a snapshot of it) never needs the rule tables again.</summary>
public sealed record ResolvedPricing(decimal ListUnitPrice, string PriceRuleRef, decimal DiscountPercent, string? DiscountRuleRef, IReadOnlyList<TraceStep> Trace);

public sealed record PricingResult(decimal ListUnitPrice, string PriceRuleRef, decimal DiscountPercent, string? DiscountRuleRef, decimal NetUnitPrice, decimal LineNet,
    bool Overridden, string? OverrideReason, decimal? MarginPercent, MarginStatus Margin, bool RequiresApproval, IReadOnlyList<TraceStep> Trace);
