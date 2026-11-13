using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/exchange-rates: manual exchange-rate maintenance. Rate direction is fixed: base-currency units for ONE foreign unit. Company from the session only;
/// authorized inside the management service; entering a rate needs If-Match with the currency's latest entry id (from the overview; 0 when none).
/// </summary>
public static class ExchangeRateEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 4 * 1024; }

    public static void MapExchangeRateEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/exchange-rates").RequireAuthorization();
        g.MapGet("", Overview);
        g.MapGet("/history", History);
        g.MapGet("/resolve", Resolve);
        g.MapPost("", Enter).WithMetadata(new BodyLimit());
    }

    private static string Fmt(decimal rate) => rate.ToString("0.0000000000", CultureInfo.InvariantCulture);

    private static object Body(ExchangeRateView r) => new
    {
        id = r.Id, currencyCode = r.CurrencyCode, rate = Fmt(r.Rate), validFrom = r.ValidFrom.ToString("yyyy-MM-dd"),
        enteredAtUtc = r.EnteredAtUtc, enteredBy = r.EnteredBy, note = r.Note,
    };

    private static async Task<IResult> Overview(HttpContext http, AccessService access, ExchangeRateManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.OverviewAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        var o = r.Value!;
        return Results.Json(new
        {
            direction = "base_per_foreign_unit",
            baseCurrency = o.BaseCurrency, baseMinorUnits = o.BaseMinorUnits,
            currencies = o.Currencies.Select(c => new { currencyCode = c.CurrencyCode, nameEn = c.NameEn, minorUnits = c.MinorUnits, latestId = c.LatestId, latest = c.Latest is null ? null : Body(c.Latest) }),
            can = new { create = eff.Has(PermissionCatalog.Codes.ExchangeRatesCreate, ScopeTarget.Company) },
        });
    }

    private static async Task<IResult> History(string? currency, DateOnly? from, DateOnly? to, int? limit, HttpContext http, AccessService access, ExchangeRateManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListAsync(actor!.UserId, actor.CompanyId, currency, from, to, limit ?? 100, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new { rates = v.Select(Body) });
    }

    private static async Task<IResult> Resolve(string? currency, DateOnly? date, HttpContext http, AccessService access, ExchangeRateManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (date is null) return CompanyConfigurationEndpoints.Error(400, "invalid_date");
        var r = await svc.ResolveAsync(actor!.UserId, actor.CompanyId, currency, date.Value, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new { rateId = v.RateId, currencyCode = v.CurrencyCode, rate = Fmt(v.Rate), validFrom = v.ValidFrom.ToString("yyyy-MM-dd") });
    }

    private static async Task<IResult> Enter(ExchangeRateInput? body, HttpContext http, AccessService access, ExchangeRateManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        var r = await svc.EnterAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted);
        return r.IsOk ? Results.Json(Body(r.Value!), statusCode: StatusCodes.Status201Created) : CompanyConfigurationEndpoints.Map(r, Body);
    }
}
