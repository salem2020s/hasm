using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class CashAccountSetup
{
    public static IServiceCollection AddHasmCashAccounts(this IServiceCollection services)
    {
        // the real probe since slice S2a: an account is referenced once a voucher names it, and has open documents while one of its vouchers is not cancelled (S3 adds transfers)
        services.AddSingleton<ICashAccountUsageProbe, CashDocumentUsageProbe>();
        services.AddSingleton(sp => new CashAccountService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(),
            sp.GetRequiredService<EnvelopeEncryptor>(), sp.GetRequiredService<ICashAccountUsageProbe>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("Hasm.Cash")));
        services.AddSingleton(sp => new CashAccountManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<CashAccountService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/cash-accounts: the cash and bank account master (Phase 3.14 slice S1). The company comes ONLY from the server-side session; every call is authorized inside <see cref="CashAccountManagementService"/> (RBAC by BRANCH,
/// an account of another branch answers like an unknown id); responses are <c>no-store</c>; changes use <c>If-Match</c> with the revision (428 missing, 412 stale). The full bank account number is NEVER part of a list or detail body:
/// only <c>GET /{id}/account-number</c> returns it, for holders of the sensitive permission, and every call is audited. Nothing here posts to the ledger.
/// </summary>
public static class CashAccountEndpoints
{
    private const long MaxBodyBytes = 16 * 1024;
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => MaxBodyBytes; }

    public static void MapCashAccountEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/cash-accounts").RequireAuthorization();
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/deactivate", Deactivate).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/activate", Activate).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}/account-number", RevealNumber);
        g.MapGet("/gl-candidates", Candidates);
        g.MapPost("/maintenance/rewrap", Rewrap).WithMetadata(new BodyLimit());
    }

    private static object Body(CashAccountView a) => new
    {
        id = a.Id, kind = a.Data.Kind == CashAccountKind.Bank ? "bank" : "cashbox", code = a.Data.Code, nameAr = a.Data.NameAr, nameEn = a.Data.NameEn,
        branchId = a.Data.BranchId, accountId = a.Data.AccountId, currency = a.Currency,
        bankName = a.Data.BankName, bankBranchName = a.Data.BankBranchName, account = new { id = a.Account.Id, code = a.Account.Code, nameAr = a.Account.NameAr, nameEn = a.Account.NameEn }, accountNumberMasked = a.MaskedNumber, hasAccountNumber = a.MaskedNumber is not null,
        isActive = a.IsActive, revision = a.Revision, createdAtUtc = a.CreatedAtUtc, updatedAtUtc = a.UpdatedAtUtc,
    };

    private static async Task<IResult> List(HttpContext http, AccessService access, CashAccountManagementService svc, CompanyBranchService branchService, string? kind, Guid? branchId, bool? active)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        var rows = r.Value!.Where(a => (kind is null || (kind == "bank" && a.Data.Kind == CashAccountKind.Bank) || (kind == "cashbox" && a.Data.Kind == CashAccountKind.Cashbox))
            && (branchId is null || a.Data.BranchId == branchId) && (active is null || a.IsActive == active)).ToList();
        // the branches the caller may act on, per action (UI hints only; the server authorizes every call itself), and the branch names the screen needs: those the caller may create or update on, and those that host a visible account
        var branches = (await branchService.ListAsync(actor.CompanyId, http.RequestAborted)).ToList();
        HashSet<Guid> On(string code) => [.. branches.Where(b => eff.Has(code, ScopeTarget.Branch(b.Id))).Select(b => b.Id)];
        var create = On(PermissionCatalog.Codes.CashAccountsCreate); var update = On(PermissionCatalog.Codes.CashAccountsUpdate);
        var hosted = r.Value!.Select(a => a.Data.BranchId).ToHashSet();
        return Results.Json(new
        {
            accounts = rows.Select(Body),
            branches = branches.Where(b => create.Contains(b.Id) || update.Contains(b.Id) || hosted.Contains(b.Id)).Select(b => new { id = b.Id, code = b.Data.Code, nameAr = b.Data.NameAr, nameEn = b.Data.NameEn, isActive = b.IsActive }),
            can = new { create, update, activate = On(PermissionCatalog.Codes.CashAccountsActivate), revealNumber = On(PermissionCatalog.Codes.CashAccountsSensitiveView) },
        });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, CashAccountManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Create(CashAccountInput? body, HttpContext http, AccessService access, CashAccountManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag(r.Value!.Id, r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> Update(Guid id, CashAccountInput? body, HttpContext http, AccessService access, CashAccountManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.UpdateAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Deactivate(Guid id, HttpContext http, AccessService access, CashAccountManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.DeactivateAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Activate(Guid id, HttpContext http, AccessService access, CashAccountManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.ReactivateAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted), Body, v => v.Revision);
    }

    /// <summary>Re-wraps the stored account numbers under the current master key (the family of the attachment and import re-wraps); privileged, company-wide; idempotent; fail-closed.</summary>
    private static async Task<IResult> Rewrap(HttpContext http, AccessService access, CashAccountManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.RewrapNumbersAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new { rewrapped = v.Rewrapped, skipped = v.Skipped, failed = v.Failed });
    }

    /// <summary>The ledger accounts that could be bound (bounded, searchable), for those who may create or update an account: they need not hold the chart permission.</summary>
    private static async Task<IResult> Candidates(HttpContext http, AccessService access, CashAccountManagementService svc, string? search, int? limit)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (search is { Length: > 100 }) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        var r = await svc.CandidatesAsync(actor!.UserId, actor.CompanyId, search, limit ?? 50, http.RequestAborted);
        return r.IsOk ? Results.Json(new { accounts = r.Value.Items.Select(c => new { id = c.Id, code = c.Code, nameAr = c.NameAr, nameEn = c.NameEn }), total = r.Value.Total }) : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    /// <summary>The FULL number, for holders of <c>company.cashaccountssensitive.view</c> on the account's branch. Never cached; every call is audited; the number appears in no other response, log or audit detail.</summary>
    private static async Task<IResult> RevealNumber(Guid id, HttpContext http, AccessService access, CashAccountManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.RevealNumberAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted);
        http.Response.Headers.CacheControl = "no-store";
        return CompanyConfigurationEndpoints.Map(r, n => new { accountNumber = n });
    }

    private sealed class CreatedEtag(Guid id, long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = $"\"{revision}\"";
            httpContext.Response.Headers.Location = $"/api/v1/company/cash-accounts/{id}";
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            return httpContext.Response.WriteAsJsonAsync(body);
        }
    }
}
