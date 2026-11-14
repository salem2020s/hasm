using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// One entered exchange rate. <b>Direction:</b> <see cref="Rate"/> is the number of BASE-currency units for ONE unit of <see cref="CurrencyCode"/>
/// (1 USD = 0.7100000000 JOD). Rows are never changed or deleted.
/// </summary>
public sealed record ExchangeRateView(long Id, string CurrencyCode, decimal Rate, DateOnly ValidFrom, DateTimeOffset EnteredAtUtc, Guid EnteredBy, string? Note);

/// <summary>The rate a document applied, preserved on the document itself (slice S5) so later rate maintenance never recalculates it.</summary>
public sealed record AppliedRate(long RateId, string CurrencyCode, decimal Rate, DateOnly ValidFrom);

public sealed record ExchangeRateInput(string? CurrencyCode, decimal? Rate, DateOnly? ValidFrom, string? Note);

/// <summary>A foreign currency of the company with its latest entry (any date) and the id that guards concurrent entry for it.</summary>
public sealed record ExchangeRateCurrencyView(string CurrencyCode, string? NameEn, int? MinorUnits, ExchangeRateView? Latest, long LatestId);

public sealed record ExchangeRateOverview(string BaseCurrency, int? BaseMinorUnits, IReadOnlyList<ExchangeRateCurrencyView> Currencies);

/// <summary>Exact decimal currency arithmetic. No floating point anywhere.</summary>
public static class CurrencyMath
{
    public const int RateScale = 10;
    public const decimal MaxRate = 9_999_999_999.9999999999m;

    /// <summary>Rounds half away from zero to the currency's minor units (the company-wide rounding rule for base amounts).</summary>
    public static decimal RoundToMinor(decimal amount, int minorUnits) => Math.Round(amount, minorUnits, MidpointRounding.AwayFromZero);

    /// <summary>Foreign amount x rate, rounded half away from zero to the base currency's minor units. Overflow throws (never wraps or truncates).</summary>
    public static decimal ConvertToBase(decimal foreignAmount, decimal rate, int baseMinorUnits) => RoundToMinor(checked(foreignAmount * rate), baseMinorUnits);

    public static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;
}

public static class ExchangeRateValidation
{
    public static bool TryNormalize(ExchangeRateInput? input, out string currency, out decimal rate, out DateOnly validFrom, out string? note, out List<FieldError> errors)
    {
        errors = [];
        currency = ""; rate = 0; validFrom = default; note = null;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var c = input.CurrencyCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(c)) errors.Add(new("currencyCode", "required"));
        else if (c.Length != 3 || !c.All(ch => ch is >= 'A' and <= 'Z')) errors.Add(new("currencyCode", "invalid_format"));
        else currency = c;
        if (input.Rate is null) errors.Add(new("rate", "required"));
        else if (input.Rate <= 0) errors.Add(new("rate", "must_be_positive"));
        else if (input.Rate > CurrencyMath.MaxRate) errors.Add(new("rate", "out_of_range"));
        else if (CurrencyMath.ScaleOf(input.Rate.Value) > CurrencyMath.RateScale && input.Rate.Value != decimal.Round(input.Rate.Value, CurrencyMath.RateScale)) errors.Add(new("rate", "too_many_decimals"));   // never rounded silently
        else rate = decimal.Round(input.Rate.Value, CurrencyMath.RateScale);
        if (input.ValidFrom is null) errors.Add(new("validFrom", "required"));
        else if (input.ValidFrom < new DateOnly(2000, 1, 1) || input.ValidFrom > new DateOnly(2100, 12, 31)) errors.Add(new("validFrom", "out_of_range"));
        else validFrom = input.ValidFrom.Value;
        var n = input.Note?.Trim();
        if (n is { Length: > 200 }) errors.Add(new("note", "too_long"));
        else if (!string.IsNullOrEmpty(n)) note = n;
        return errors.Count == 0;
    }
}
