using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public sealed record SetCurrencyRequest(bool? Enabled, bool? IsBase);

public static class CompanyConfigurationSetup
{
    /// <summary>Registers the configuration services. Everything is lazy: the API still starts and anonymous endpoints work without a database.</summary>
    public static IServiceCollection AddHasmCompanyConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        // The Trial policy is the only entitlement policy: there is deliberately no allow-all registration.
        services.AddSingleton<IEntitlementPolicy>(TrialEntitlementPolicy.Instance);
        services.AddSingleton(_ => new FinancialHistoryGuard([new Hasm.Infrastructure.Ledger.JournalFinancialHistoryProbe()]));   // inventory and opening-balance modules register their own probes
        services.AddSingleton(sp =>
        {
            var options = HasmDatabaseOptions.FromConfiguration(configuration);
            options.EnsureValid(requireAdmin: false);
            // Runtime (least-privilege) system account: needs UPDATE on the three display-name projection columns only.
            return new CompanyDisplayNameProjection(new CompanyRegistry(options.BuildSystemConnectionString(admin: false)),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Hasm.Company"));
        });
        services.AddSingleton(sp => new CompanyConfigurationService(
            sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<AccessService>(),
            sp.GetRequiredService<IEntitlementPolicy>(), sp.GetRequiredService<FinancialHistoryGuard>(), sp.GetRequiredService<CompanyDisplayNameProjection>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/*: company profile, settings, currencies and configuration history. The company comes ONLY from the server-side session
/// (never from the request); every call is authorized by the service against that company; responses are <c>no-store</c> (response hardening
/// covers <c>/api/v1/company</c>) and never contain physical database identifiers or credentials. State-changing requests pass the existing
/// anti-CSRF middleware. Updates use <c>If-Match</c> with the revision (0 = does not exist yet): 428 when missing, 412 when stale.
/// </summary>
public static class CompanyConfigurationEndpoints
{
    private const long MaxBodyBytes = 16 * 1024;
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => MaxBodyBytes; }

    public static void MapCompanyConfigurationEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company").RequireAuthorization();
        g.MapGet("/profile", GetProfile);
        g.MapPut("/profile", PutProfile).WithMetadata(new BodyLimit());
        g.MapPost("/profile/display-name-sync", SyncDisplayName).WithMetadata(new BodyLimit());
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", PutSettings).WithMetadata(new BodyLimit());
        g.MapGet("/currencies", GetCurrencies);
        g.MapGet("/currencies/reference", GetCurrencyReference);
        g.MapPut("/currencies/{code}", PutCurrency).WithMetadata(new BodyLimit());
        g.MapGet("/history", GetHistory);
        g.MapGet("/entitlements", GetEntitlements);
    }

    internal static IResult Error(int status, string code) => AccessEndpoints.Error(status, code);

    internal static IResult Map<T>(ConfigResult<T> r, Func<T, object> body, Func<T, long?>? revision = null)
    {
        switch (r.Status)
        {
            case ConfigStatus.Ok:
                {
                    if (revision?.Invoke(r.Value!) is { } rev) return new EtagResult(rev, body(r.Value!));
                    return Results.Json(body(r.Value!));
                }
            case ConfigStatus.Forbidden: return Error(403, "forbidden");
            case ConfigStatus.Invalid: return Results.Json(new { error = r.Code, fields = r.Fields?.Select(f => new { field = f.Field, code = f.Code }) }, statusCode: 400);
            case ConfigStatus.PreconditionRequired: return Error(428, "precondition_required");
            case ConfigStatus.PreconditionFailed: return Results.Json(new { error = "revision_conflict", currentRevision = r.CurrentRevision }, statusCode: 412);
            case ConfigStatus.EntitlementDenied: return Error(403, "entitlement_denied");
            case ConfigStatus.NotFound: return Error(404, r.Code ?? "not_found");
            default:
                // a conflict may name the lines it concerns (Phase 3.8B-3); otherwise the body is the plain error as before
                if (r.Fields is { Count: > 0 } f) return Results.Json(new { error = r.Code ?? "conflict", fields = f.Select(x => new { field = x.Field, code = x.Code }) }, statusCode: 409);
                return Error(409, r.Code ?? "conflict");
        }
    }

    /// <summary>JSON response carrying the strong revision as <c>ETag</c>.</summary>
    internal sealed class EtagResult(long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = $"\"{revision}\"";
            return Results.Json(body).ExecuteAsync(httpContext);
        }
    }

    /// <summary>Parses <c>If-Match: "n"</c>. Absent = null (the service answers 428); a malformed value is a client error.</summary>
    internal static bool TryReadIfMatch(HttpContext http, out long? revision)
    {
        revision = null;
        var raw = http.Request.Headers.IfMatch.ToString().Trim();
        if (raw.Length == 0) return true;
        raw = raw.Trim('"');
        if (!long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0) return false;
        revision = n;
        return true;
    }

    private static object ProfileBody(ProfileView v) => new
    {
        profile = v.Data is null ? null : new
        {
            nameAr = v.Data.NameAr, nameEn = v.Data.NameEn, legalName = v.Data.LegalName,
            commercialRegistrationNo = v.Data.CommercialRegistrationNo, taxRegistrationNo = v.Data.TaxRegistrationNo, countryCode = v.Data.CountryCode,
            addressLine1 = v.Data.AddressLine1, addressLine2 = v.Data.AddressLine2, city = v.Data.City, region = v.Data.Region, postalCode = v.Data.PostalCode,
            phone = v.Data.Phone, email = v.Data.Email, website = v.Data.Website,
        },
        revision = v.Revision, updatedAtUtc = v.UpdatedAtUtc, projection = v.Projection,
    };

    private static object SettingsBody(SettingsView v) => new
    {
        settings = new { defaultLanguage = v.Data.DefaultLanguage, dateFormat = v.Data.DateFormat, numeralSystem = v.Data.NumeralSystem, timeZone = v.Data.TimeZone },
        revision = v.Revision, exists = v.Exists, setupComplete = v.SetupComplete, updatedAtUtc = v.UpdatedAtUtc,
        options = new { languages = ConfigurationValidation.Languages, dateFormats = ConfigurationValidation.DateFormats, numeralSystems = ConfigurationValidation.NumeralSystems },
    };

    private static object CurrenciesBody(CurrenciesView v) => new
    {
        currencies = v.Currencies.Select(c => new { code = c.Code, nameEn = c.NameEn, minorUnits = c.MinorUnits, isBase = c.IsBase, enabled = c.Enabled, revision = c.Revision }),
        baseCurrency = v.BaseCurrency, baseCurrencySelected = v.BaseCurrencySelected,
        baseCurrencyLock = new { locked = v.BaseCurrencyLock.Locked, checksImplemented = v.BaseCurrencyLock.ChecksImplemented, unimplementedDomains = v.BaseCurrencyLock.UnimplementedDomains },
    };

    private static async Task<IResult> GetProfile(HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.GetProfileAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), ProfileBody, v => v.Revision);
    }

    private static async Task<IResult> PutProfile(ProfileInput? body, HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!TryReadIfMatch(http, out var expected)) return Error(400, "invalid_if_match");
        return Map(await svc.PutProfileAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted), ProfileBody, v => v.Revision);
    }

    private static async Task<IResult> SyncDisplayName(HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.SyncDisplayNameAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), ProfileBody, v => v.Revision);
    }

    private static async Task<IResult> GetSettings(HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.GetSettingsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), SettingsBody, v => v.Revision);
    }

    private static async Task<IResult> PutSettings(SettingsInput? body, HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!TryReadIfMatch(http, out var expected)) return Error(400, "invalid_if_match");
        return Map(await svc.PutSettingsAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted), SettingsBody, v => v.Revision);
    }

    private static async Task<IResult> GetCurrencies(HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.GetCurrenciesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), CurrenciesBody);
    }

    private static async Task<IResult> GetCurrencyReference(HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.GetCurrencyReferenceAsync(actor!.UserId, actor.CompanyId, http.RequestAborted),
            list => new { currencies = list.Select(c => new { code = c.Code, numericCode = c.NumericCode, nameEn = c.NameEn, minorUnits = c.MinorUnits, active = c.IsActive, datasetVersion = c.DatasetVersion }) });
    }

    private static async Task<IResult> PutCurrency(string code, SetCurrencyRequest? body, HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (body is null) return Error(400, "invalid_request");
        if (!TryReadIfMatch(http, out var expected)) return Error(400, "invalid_if_match");
        return Map(await svc.SetCurrencyAsync(actor!.UserId, actor.CompanyId, code, expected, body.Enabled, body.IsBase, http.RequestAborted), CurrenciesBody);
    }

    private static async Task<IResult> GetHistory(string? entity, int? limit, long? before, HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.GetHistoryAsync(actor!.UserId, actor.CompanyId, entity, limit ?? 50, before, http.RequestAborted),
            list => new
            {
                entries = list.Select(e => new
                {
                    id = e.Id, entity = e.Entity, key = e.EntityKey, revision = e.EntityRevision, actorUserId = e.ActorUserId, changedAtUtc = e.ChangedAtUtc,
                    changes = System.Text.Json.JsonDocument.Parse(e.ChangesJson).RootElement.Clone(), auditSeq = e.AuditSeq,
                }),
            });
    }

    private static async Task<IResult> GetEntitlements(HttpContext http, AccessService access, CompanyConfigurationService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!await access.IsAllowedAsync(actor!.UserId, actor.CompanyId, PermissionCatalog.Codes.SettingsView, ScopeTarget.Company, http.RequestAborted)) return Error(403, "forbidden");
        return Results.Json(new
        {
            edition = svc.Entitlements.Edition,
            restrictions = svc.Entitlements.Restrictions.Select(r => new { key = r.Key, limit = r.Limit, enforced = r.Enforcement == Enforcement.Enforced, note = r.Note }),
        });
    }
}
