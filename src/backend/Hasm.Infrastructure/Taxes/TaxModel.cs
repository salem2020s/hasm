namespace Hasm.Infrastructure.Taxes;

/// <summary>How a tax applies to a line (persisted by later slices; never renumber). Zero-rated, exempt and not-applicable lines contribute no tax but their turnover is reported separately.</summary>
public enum TaxTreatment : short { Standard = 1, ZeroRated = 2, Exempt = 3, NotApplicable = 4 }

public enum TaxRoundingMode : short { HalfAwayFromZero = 1, HalfEven = 2 }

/// <summary>PerLine: every amount is rounded once. PerDocument: exact amounts are summed per tax, rounded once and allocated to the lines by largest remainder.</summary>
public enum TaxRoundingLevel : short { PerLine = 1, PerDocument = 2 }

/// <summary>A refused calculation: a stable code, never a partial result.</summary>
public sealed class TaxException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// One tax already resolved for a line (the resolver of a later slice turns document type, operation, item/party classification and date into these).
/// <paramref name="RatePercent"/> is a percentage (15 = 15 %), at most 6 decimals, zero for every treatment except Standard. <paramref name="Priority"/> orders the application
/// (unique per line). <paramref name="IncludesInBase"/> names EARLIER taxes of the same line whose amount is added to this tax's base (compound taxes).
/// <paramref name="RuleRef"/> is an opaque reference (rule/rate version) that only the snapshot keeps for traceability.
/// </summary>
public sealed record TaxComponent(Guid TaxId, string Code, TaxTreatment Treatment, decimal RatePercent, int Priority, IReadOnlyList<Guid>? IncludesInBase = null, string? RuleRef = null);

/// <summary>A document line. Quantity (at most 10 decimals), unit price (at most 6) and discount (at most the money decimals) are non-negative: credit notes are modelled by the document framework, not by negative arithmetic.</summary>
public sealed record TaxLineInput(int LineNo, decimal Quantity, decimal UnitPrice, decimal Discount, bool PriceIncludesTax, IReadOnlyList<TaxComponent> Taxes);

public sealed record TaxPolicy(int MoneyDecimals, TaxRoundingMode Mode, TaxRoundingLevel Level);

public sealed record TaxAmount(Guid TaxId, string Code, TaxTreatment Treatment, decimal RatePercent, decimal Base, decimal Amount);
public sealed record TaxLineResult(int LineNo, decimal Net, decimal TaxTotal, decimal Gross, IReadOnlyList<TaxAmount> Taxes);
public sealed record TaxTotal(Guid TaxId, string Code, TaxTreatment Treatment, decimal Base, decimal Amount);
public sealed record TaxDocumentResult(IReadOnlyList<TaxLineResult> Lines, IReadOnlyList<TaxTotal> TotalsByTax, decimal TotalNet, decimal TotalTax, decimal TotalGross);
