using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class InventoryAccountingSetup
{
    public static IServiceCollection AddHasmInventoryAccounting(this IServiceCollection services)
    {
        services.AddSingleton(sp => new InventoryAccountingService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new InventoryAccountingManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<InventoryAccountingService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/inventory/accounting: the versioned configuration of the inventory accounts (Phase 3.7A). The company comes only from the server-side session, every call is authorized in
/// <see cref="InventoryAccountingManagementService"/>, responses are <c>no-store</c>, changes use <c>If-Match</c> with the current VERSION NUMBER (0 while nothing is configured; 412 stale, 428 missing).
/// Saving creates a new immutable version; nothing is posted by this API.
/// </summary>
public static class InventoryAccountingEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 256 * 1024; }

    public static void MapInventoryAccountingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/inventory/accounting").RequireAuthorization();
        g.MapGet("", Get);
        g.MapGet("/versions", Versions);
        g.MapPut("", Put).WithMetadata(new BodyLimit());
    }

    private static object Body(InventoryAccountingView v) => new
    {
        version = v.Version is null ? null : new { id = v.Version.Id, versionNo = v.Version.VersionNo, createdAtUtc = v.Version.CreatedAtUtc, createdBy = v.Version.CreatedBy, note = v.Version.Note, groupCount = v.Version.GroupCount, contentHash = v.Version.ContentHash },
        versionNo = v.Version?.VersionNo ?? 0,
        defaultInventoryAccountId = v.DefaultInventoryAccountId, receiptCounterAccountId = v.ReceiptCounterAccountId, issueCounterAccountId = v.IssueCounterAccountId, adjustmentCounterAccountId = v.AdjustmentCounterAccountId,
        obligationAccountId = v.ObligationAccountId, varianceAccountId = v.VarianceAccountId,
        groups = v.GroupAccounts.Select(g => new { groupId = g.Key, accountId = g.Value }),
    };

    private static async Task<IResult> Get(HttpContext http, AccessService access, InventoryAccountingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.GetAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, Body);
        var canConfigure = await svc.CanConfigureAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        var cfg = r.Value!;
        var used = cfg.GroupAccounts.Values.Concat(new[] { cfg.DefaultInventoryAccountId, cfg.ReceiptCounterAccountId, cfg.IssueCounterAccountId, cfg.AdjustmentCounterAccountId, cfg.ObligationAccountId, cfg.VarianceAccountId }.Where(x => x is not null).Select(x => x!.Value)).Distinct().ToList();
        var lookups = (await svc.LookupsAsync(actor.UserId, actor.CompanyId, used, http.RequestAborted)).Value;
        http.Response.Headers.ETag = $"\"{r.Value!.Version?.VersionNo ?? 0}\"";
        return Results.Json(new
        {
            configuration = Body(r.Value), canConfigure,
            accounts = lookups?.Accounts.Select(a => new { id = a.Id, code = a.Code, nameAr = a.NameAr, nameEn = a.NameEn, type = a.Type }),
            groups = lookups?.Groups.Select(g => new { id = g.Id, code = g.Code, nameAr = g.NameAr, nameEn = g.NameEn, parentId = g.ParentId }),
        });
    }

    private static async Task<IResult> Versions(HttpContext http, AccessService access, InventoryAccountingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.VersionsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return r.IsOk ? Results.Json(new { versions = r.Value!.Select(v => new { id = v.Id, versionNo = v.VersionNo, createdAtUtc = v.CreatedAtUtc, createdBy = v.CreatedBy, note = v.Note, groupCount = v.GroupCount, contentHash = v.ContentHash }) }) : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> Put(InventoryAccountingInput? body, HttpContext http, AccessService access, InventoryAccountingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.SaveAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted), Body, v => v.Version?.VersionNo ?? 0);
    }
}
