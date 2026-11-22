using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public sealed record ApplyTemplateRequest(string? Template);

public static class AccountSetup
{
    public static IServiceCollection AddHasmChartOfAccounts(this IServiceCollection services)
    {
        services.AddSingleton(new AccountUsageGuard([new Hasm.Infrastructure.Ledger.JournalAccountUsageProbe()]));   // the General Ledger probe replaces the placeholder
        services.AddSingleton(sp => new ChartOfAccountsService(
            sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(),
            sp.GetRequiredService<AccountUsageGuard>(), sp.GetRequiredService<FinancialHistoryGuard>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ChartOfAccountsManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<ChartOfAccountsService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/accounts: the Chart of Accounts. Same rules as the other company endpoints: the company comes ONLY from the server-side session, every call
/// is authorized inside <see cref="ChartOfAccountsManagementService"/> (company-wide RBAC with separate permissions for re-coding, restructuring and
/// configuring), responses are no-store, state-changing requests pass the anti-CSRF middleware, and changes use If-Match with the account (or settings)
/// revision (428 missing, 412 stale). Both clients (Web and Windows Desktop) use exactly these endpoints.
/// </summary>
public static class AccountEndpoints
{
    private const long MaxBodyBytes = 16 * 1024;
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => MaxBodyBytes; }

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/accounts").RequireAuthorization();
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapPost("/apply-template", ApplyTemplate).WithMetadata(new BodyLimit());
        g.MapPut("/settings", PutSettings).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/recode", Recode).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/move", Move).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/deactivate", Deactivate).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/activate", Activate).WithMetadata(new BodyLimit());
    }

    private static object Body(AccountView a) => new
    {
        id = a.Id, code = a.Code, nameAr = a.NameAr, nameEn = a.NameEn, parentId = a.ParentId,
        type = a.Type.ToString().ToLowerInvariant(), nature = a.Nature.ToString().ToLowerInvariant(),
        statement = a.Statement == AccountStatement.BalanceSheet ? "balance_sheet" : "profit_and_loss",
        isGroup = a.IsGroup, isActive = a.IsActive, postable = a.IsPostable, hasChildren = a.HasChildren,
        currencyPolicy = a.CurrencyPolicy.ToString().ToLowerInvariant(), currencyCode = a.CurrencyCode, template = a.TemplateKey,
        revision = a.Revision, createdAtUtc = a.CreatedAtUtc, updatedAtUtc = a.UpdatedAtUtc,
    };

    private static object SettingsBody(ChartSettingsView s) => new { segmentWidth = s.SegmentWidth, autoCodes = s.AutoCodes, revision = s.Revision, configured = s.Exists };

    private static async Task<IResult> List(HttpContext http, AccessService access, ChartOfAccountsManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var list = await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!list.IsOk) return CompanyConfigurationEndpoints.Map(list, _ => new { });
        var settings = await svc.GetSettingsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new
        {
            accounts = list.Value!.Select(Body),
            settings = settings.IsOk ? SettingsBody(settings.Value!) : null,
            // Business-type templates and Excel import are explicit Release 1 requirements that are NOT delivered yet; only the generic template exists.
            templates = ChartTemplates.All.Select(t => new { key = t.Key, nameAr = t.NameAr, nameEn = t.NameEn, accounts = t.Accounts.Count }),
            // Whether an account's posting history can be checked: false until the General Ledger exists. Never present "no history" as verified while false.
            usageChecksImplemented = svc.UnimplementedUsageChecks.Count == 0,
            can = new
            {
                create = eff.Has(PermissionCatalog.Codes.AccountsCreate, ScopeTarget.Company),
                update = eff.Has(PermissionCatalog.Codes.AccountsUpdate, ScopeTarget.Company),
                activate = eff.Has(PermissionCatalog.Codes.AccountsActivate, ScopeTarget.Company),
                recode = eff.Has(PermissionCatalog.Codes.AccountsRecode, ScopeTarget.Company),
                restructure = eff.Has(PermissionCatalog.Codes.AccountsRestructure, ScopeTarget.Company),
                configure = eff.Has(PermissionCatalog.Codes.AccountsConfigure, ScopeTarget.Company),
            },
        });
    }


    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, ChartOfAccountsManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Create(AccountInput? body, HttpContext http, AccessService access, ChartOfAccountsManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/accounts/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> ApplyTemplate(ApplyTemplateRequest? body, HttpContext http, AccessService access, ChartOfAccountsManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ApplyTemplateAsync(actor!.UserId, actor.CompanyId, body?.Template, http.RequestAborted);
        return r.IsOk ? Results.Json(new { accounts = r.Value!.Select(Body) }, statusCode: StatusCodes.Status201Created) : CompanyConfigurationEndpoints.Map(r, v => new { accounts = v.Select(Body) });
    }

    private static async Task<IResult> PutSettings(ChartSettingsInput? body, HttpContext http, AccessService access, ChartOfAccountsManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.PutSettingsAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted), SettingsBody, v => v.Revision);
    }

    private static async Task<IResult> Run(HttpContext http, AccessService access, Func<Guid, Guid, long?, Task<ConfigResult<AccountView>>> action)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await action(actor!.UserId, actor.CompanyId, expected), Body, v => v.Revision);
    }

    private static Task<IResult> Update(Guid id, AccountDetailsInput? body, HttpContext http, AccessService access, ChartOfAccountsManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.UpdateAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Recode(Guid id, AccountCodeInput? body, HttpContext http, AccessService access, ChartOfAccountsManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.RecodeAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Move(Guid id, AccountMoveInput? body, HttpContext http, AccessService access, ChartOfAccountsManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.MoveAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Deactivate(Guid id, HttpContext http, AccessService access, ChartOfAccountsManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.DeactivateAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Activate(Guid id, HttpContext http, AccessService access, ChartOfAccountsManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.ReactivateAsync(u, c, id, rev, http.RequestAborted));

    /// <summary>201 with the strong revision as ETag and the new resource location.</summary>
    private sealed class CreatedEtag(string location, long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = $"\"{revision}\"";
            httpContext.Response.Headers.Location = location;
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            return httpContext.Response.WriteAsJsonAsync(body);
        }
    }
}
