using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class WarehouseSetup
{
    public static IServiceCollection AddHasmWarehouses(this IServiceCollection services)
    {
        services.AddSingleton(sp => new CompanyWarehouseService(
            sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(),
            sp.GetRequiredService<IEntitlementPolicy>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new WarehouseManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<CompanyWarehouseService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/warehouses: warehouse management. Same rules as the branch endpoints: the company comes ONLY from the server-side session,
/// every call is authorized inside <see cref="WarehouseManagementService"/> (RBAC, warehouse scope, activation epochs, no inheritance from
/// branches), responses are <c>no-store</c>, state-changing requests pass the anti-CSRF middleware, and changes use <c>If-Match</c> with the
/// warehouse revision (428 missing, 412 stale). Invariants, the Trial limit, auditing and concurrency are enforced server-side and by PostgreSQL;
/// both clients (Web and Windows Desktop) use exactly these endpoints.
/// </summary>
public static class WarehouseEndpoints
{
    private const long MaxBodyBytes = 16 * 1024;
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => MaxBodyBytes; }

    public static void MapWarehouseEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/warehouses").RequireAuthorization();
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/deactivate", Deactivate);
        g.MapPost("/{id:guid}/activate", Activate);
    }

    private static object Body(WarehouseView w) => new
    {
        id = w.Id, branchId = w.BranchId, code = w.Data.Code, nameAr = w.Data.NameAr, nameEn = w.Data.NameEn,
        isActive = w.IsActive, activationEpoch = w.ActivationEpoch,
        addressLine1 = w.Data.AddressLine1, addressLine2 = w.Data.AddressLine2, city = w.Data.City, region = w.Data.Region, postalCode = w.Data.PostalCode,
        phone = w.Data.Phone, email = w.Data.Email, website = w.Data.Website,
        revision = w.Revision, createdAtUtc = w.CreatedAtUtc, updatedAtUtc = w.UpdatedAtUtc,
    };

    private static async Task<IResult> List(HttpContext http, AccessService access, WarehouseManagementService svc, CompanyBranchService branchService)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var list = await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!list.IsOk) return CompanyConfigurationEndpoints.Map(list, _ => new { });
        var limits = await svc.LimitsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        var l = limits.Value;
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        var canCreate = eff.Has(PermissionCatalog.Codes.WarehouseCreate, ScopeTarget.Company);
        // Branch names are shown only as far as needed: every branch to a caller who may create warehouses (to pick one), otherwise only the
        // branches that host a warehouse the caller can see. Minimal fields; never address or contact data.
        var hosted = list.Value!.Select(w => w.BranchId).ToHashSet();
        var branches = (await branchService.ListAsync(actor.CompanyId, http.RequestAborted))
            .Where(b => canCreate || hosted.Contains(b.Id))
            .Select(b => new { id = b.Id, code = b.Data.Code, nameAr = b.Data.NameAr, nameEn = b.Data.NameEn, isActive = b.IsActive });
        return Results.Json(new
        {
            warehouses = list.Value!.Select(Body),
            branches,
            limit = l?.Limit, everCreated = l?.EverCreated,
            // What this caller may do (the server still authorizes every action itself; this only lets the UI show/hide controls).
            can = new
            {
                create = canCreate,
                activate = eff.Has(PermissionCatalog.Codes.WarehouseActivate, ScopeTarget.Company),
                update = list.Value!.Where(w => eff.Has(PermissionCatalog.Codes.WarehousesUpdate, ScopeTarget.Warehouse(w.Id))).Select(w => w.Id),
            },
        });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, WarehouseManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Create(WarehouseInput? body, HttpContext http, AccessService access, WarehouseManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag(r.Value!.Id, r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> Update(Guid id, WarehouseInput? body, HttpContext http, AccessService access, WarehouseManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.UpdateAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Deactivate(Guid id, HttpContext http, AccessService access, WarehouseManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.DeactivateAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Activate(Guid id, HttpContext http, AccessService access, WarehouseManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.ReactivateAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted), Body, v => v.Revision);
    }

    /// <summary>201 with the strong revision as <c>ETag</c> and the new resource location.</summary>
    private sealed class CreatedEtag(Guid id, long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = $"\"{revision}\"";
            httpContext.Response.Headers.Location = $"/api/v1/company/warehouses/{id}";
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            return httpContext.Response.WriteAsJsonAsync(body);
        }
    }
}
