namespace Hasm.Infrastructure.Pricing;

/// <summary>Business-audit event types of the pricing configuration (persisted; never renumber; unique across every <c>*AuditEvents</c> class).</summary>
public static class PricingAuditEvents
{
    public const short PolicySaved = 1800;
    public const short PriceListCreated = 1801;
    public const short PriceListUpdated = 1802;
    public const short PriceEntryCreated = 1803;
    public const short PriceEntryUpdated = 1804;
    public const short DiscountCreated = 1805;
    public const short DiscountUpdated = 1806;
}

/// <summary>A saved policy version (append-only; the latest is the current one). <see cref="Version"/> 0 with <see cref="IsDefault"/> means "never saved": the defaults of slice 1 apply.</summary>
public sealed record PricingPolicyView(int Version, bool IsDefault, PricingPolicy Policy, DateTimeOffset? CreatedAtUtc);

/// <summary>Explicit values only (nothing is guessed): scales 0-6, optional minimum margin (-1000..100), margin action 1-3, precedences 1-2.</summary>
public sealed record PricingPolicyInput(int? PriceDecimals, int? MoneyDecimals, decimal? MinMarginPercent, int? MarginAction, int? ListPrecedence, int? DiscountPrecedence);

/// <summary>Code, currency and customer are fixed at creation (immutable in PostgreSQL); priority, dates, names and the active flag change by revision-fenced updates. Customer-group scope arrives with the customer-group registry (not built yet).</summary>
public sealed record PriceListInput(string? Code, string? NameAr, string? NameEn, int? Priority, string? Currency, DateOnly? ValidFrom, DateOnly? ValidTo, bool? IsActive, Guid? CustomerId);

public sealed record PriceListView(Guid Id, string Code, string NameAr, string? NameEn, int Priority, string Currency, DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive, Guid? CustomerId, long Revision, DateTimeOffset UpdatedAtUtc);

/// <summary>An item OR an item group, a unit and a quantity tier identify an entry (immutable); only the price and the active flag change.</summary>
public sealed record PriceEntryInput(Guid? ItemId, Guid? ItemGroupId, Guid? UnitId, decimal? MinQuantity, decimal? UnitPrice, bool? IsActive);

public sealed record PriceEntryView(Guid Id, Guid PriceListId, Guid? ItemId, Guid? ItemGroupId, Guid UnitId, decimal MinQuantity, decimal UnitPrice, bool IsActive, long Revision, DateTimeOffset UpdatedAtUtc);

/// <summary>Code and scope (customer, item or item group) are fixed at creation; priority, dates, names, percentage, minimum quantity and the active flag change.</summary>
public sealed record DiscountRuleInput(string? Code, string? NameAr, string? NameEn, int? Priority, DateOnly? ValidFrom, DateOnly? ValidTo, bool? IsActive, decimal? Percent, decimal? MinQuantity, Guid? CustomerId, Guid? ItemId, Guid? ItemGroupId);

public sealed record DiscountRuleView(Guid Id, string Code, string NameAr, string? NameEn, int Priority, DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive, decimal Percent, decimal MinQuantity,
    Guid? CustomerId, Guid? ItemId, Guid? ItemGroupId, long Revision, DateTimeOffset UpdatedAtUtc);

public sealed record PricingCapabilities(bool View, bool Configure, bool Override);
