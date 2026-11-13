using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.2B S2: exact decimal currency arithmetic, rate validation and the shape of the exchange-rate schema (no database needed).</summary>
public class ExchangeRateUnitTests
{
    [Theory]
    [InlineData("100.00", "0.71", 3, "71.000")]
    [InlineData("100.005", "0.71", 3, "71.004")]       // 71.00355 -> half away from zero at 3 places: 71.004
    [InlineData("0.01", "0.5", 2, "0.01")]             // 0.005 -> rounds away from zero to 0.01 (never banker's rounding)
    [InlineData("-0.01", "0.5", 2, "-0.01")]           // symmetric for negatives
    [InlineData("0.03", "0.5", 2, "0.02")]             // 0.015 -> 0.02
    [InlineData("1234567.89", "3.7500000000", 2, "4629629.59")]
    [InlineData("10", "1.0000000001", 2, "10.00")]
    [InlineData("1", "0.0000000001", 3, "0.000")]
    public void Conversion_is_exact_decimal_rounded_half_away_from_zero_to_the_base_minor_units(string amount, string rate, int minor, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            CurrencyMath.ConvertToBase(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), minor));

    [Fact]
    public void Conversion_overflow_throws_instead_of_wrapping() =>
        Assert.Throws<OverflowException>(() => CurrencyMath.ConvertToBase(decimal.MaxValue / 2, 9_999m, 2));

    [Fact]
    public void Zero_minor_units_round_to_whole_numbers() => Assert.Equal(3m, CurrencyMath.RoundToMinor(2.5m, 0));

    private static ExchangeRateInput In(string? c = "USD", decimal? rate = 0.71m, int y = 2026, string? note = null) => new(c, rate, new DateOnly(y, 1, 1), note);

    [Fact]
    public void A_valid_rate_is_normalised()
    {
        Assert.True(ExchangeRateValidation.TryNormalize(In(" usd ", 0.7100000000m, note: " manual "), out var c, out var r, out var d, out var n, out var errors));
        Assert.Equal(("USD", 0.71m, new DateOnly(2026, 1, 1), "manual"), (c, r, d, n));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("0", "must_be_positive")]
    [InlineData("-1", "must_be_positive")]
    [InlineData("0.12345678901", "too_many_decimals")]       // more precision than stored: refused, never rounded silently
    [InlineData("10000000000", "out_of_range")]
    public void Rates_must_be_positive_exact_and_within_the_stored_precision(string? rate, string code)
    {
        Assert.False(ExchangeRateValidation.TryNormalize(In(rate: rate is null ? null : decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)), out _, out _, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Field == "rate" && e.Code == code);
    }

    [Fact]
    public void Trailing_zeros_beyond_ten_decimals_are_the_same_value_and_accepted() =>
        Assert.True(ExchangeRateValidation.TryNormalize(In(rate: 0.71000000000000m), out _, out var r, out _, out _, out _) && r == 0.71m);

    [Theory]
    [InlineData(null, "required")]
    [InlineData("US", "invalid_format")]
    [InlineData("U5D", "invalid_format")]
    public void The_currency_code_must_be_three_letters(string? code, string error)
    {
        Assert.False(ExchangeRateValidation.TryNormalize(In(code), out _, out _, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Field == "currencyCode" && e.Code == error);
    }

    [Fact]
    public void Dates_and_notes_are_bounded()
    {
        Assert.False(ExchangeRateValidation.TryNormalize(In(y: 1999), out _, out _, out _, out _, out var e1));
        Assert.Contains(e1, e => e.Field == "validFrom");
        Assert.False(ExchangeRateValidation.TryNormalize(In(note: new string('x', 201)), out _, out _, out _, out _, out var e2));
        Assert.Contains(e2, e => e.Field == "note");
        Assert.False(ExchangeRateValidation.TryNormalize(new ExchangeRateInput("USD", 1m, null, null), out _, out _, out _, out _, out var e3));
        Assert.Contains(e3, e => e.Field == "validFrom");
        Assert.False(ExchangeRateValidation.TryNormalize(null, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void The_permissions_are_company_wide_view_and_create_only_and_in_the_administrator_preset()
    {
        var codes = PermissionCatalog.Default.All.Select(p => p.Code).Where(c => c.StartsWith("company.exchangerates")).Order();
        Assert.Equal(["company.exchangerates.create", "company.exchangerates.view"], codes);
        Assert.Contains("company.exchangerates.create", PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions);
        Assert.DoesNotContain(PresetRoles.Definitions.Where(d => d.Key != PresetRoles.CompanyAdministratorId).SelectMany(d => d.Value.Permissions), p => p.StartsWith("company.exchangerates"));
    }

    [Fact]
    public void The_migration_makes_rates_append_only_never_stores_the_base_and_grants_no_update_or_delete()
    {
        var sql = System.Text.RegularExpressions.Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 8).Sql, "--[^\n]*", "");
        Assert.Contains($"CREATE TABLE {CompanySchema.ExchangeRate.Table}", sql);
        Assert.Contains($"{CompanySchema.ExchangeRate.Rate} numeric(20,10)", sql);
        Assert.DoesNotContain("double precision", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" real", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["INSERT", "SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.ExchangeRate.Table).Privileges.Order());
    }
}
