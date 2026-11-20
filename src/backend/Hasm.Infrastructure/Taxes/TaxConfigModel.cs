namespace Hasm.Infrastructure.Taxes;

/// <summary>Audit event types of the tax configuration (company business stream, 1400-1409). Persisted codes: never renumber.</summary>
public static class TaxAuditEvents
{
    public const short TaxCreated = 1400;
    public const short TaxUpdated = 1401;
    public const short RateAdded = 1402;
    public const short ClassCreated = 1403;
    public const short ClassUpdated = 1404;
    public const short ItemClassAssigned = 1405;
    public const short PartyClassAssigned = 1406;
    public const short RuleCreated = 1407;
    public const short RuleUpdated = 1408;
    public const short PolicyAdded = 1409;
}

/// <summary>Kind of a tax class: items and parties are classified separately and a class never serves both.</summary>
public enum TaxClassKind : short { Item = 1, Party = 2 }

/// <summary>IsActive: null = true on create and "unchanged" on update (an omitted flag never silently reactivates).</summary>
public sealed record TaxInput(string? Code, string? NameAr, string? NameEn, bool? IsActive = null);
public sealed record TaxView(Guid Id, string Code, string NameAr, string? NameEn, bool IsActive, long Revision, IReadOnlyList<TaxRateRecord> Rates, DateTimeOffset UpdatedAtUtc);
public sealed record TaxRateInput(DateOnly EffectiveFrom, TaxTreatment Treatment, decimal RatePercent);

public sealed record TaxClassInput(TaxClassKind Kind, string? Code, string? NameAr, string? NameEn, bool? IsActive = null);
public sealed record TaxClassView(Guid Id, TaxClassKind Kind, string Code, string NameAr, string? NameEn, bool IsActive, long Revision);

public sealed record TaxRuleInput(string? DocumentType, string? Operation, Guid? ItemClassId, Guid? PartyClassId, Guid TaxId, int Priority, IReadOnlyList<Guid>? IncludesTaxIds,
    DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive = true, TaxTreatment? TreatmentOverride = null);
public sealed record TaxRuleView(TaxRuleRecord Rule, long Revision);

public sealed record TaxPolicyInput(int MoneyDecimals, TaxRoundingMode Mode, TaxRoundingLevel Level);
public sealed record TaxPolicyView(Guid Id, int Version, TaxPolicy Policy, DateTimeOffset CreatedAtUtc);

/// <summary>What <see cref="TaxConfigService.ResolveAsync"/> hands to the engine: the ordered components, the rounding policy in force and its version (the snapshot seals both).</summary>
public sealed record TaxResolution(IReadOnlyList<TaxComponent> Components, TaxPolicy Policy, int PolicyVersion);

public sealed record TaxCapabilities(bool View, bool Configure);

/// <summary>
/// Deployment policy of the tax configuration. <see cref="MaxRateLeadDays"/>: how far ahead of today a rate version may be dated. The SRS sets no such limit; it is a guard
/// against a typo that could never be undone (versions are append-only and each must be later than the last), so the default is one year and 0 disables the limit.
/// </summary>
public sealed record TaxConfigOptions(int MaxRateLeadDays = TaxConfigService.DefaultMaxRateLeadDays);
