using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/cost-centers: the cost-center tree. Company from the server-side session only; authorized inside <see cref="CostCenterManagementService"/>
/// (company-wide RBAC, separate permissions for re-coding and restructuring); no-store responses; changes use If-Match with the cost-center revision.
/// </summary>
public static class CostCenterEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 8 * 1024; }

    public static void MapCostCenterEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/cost-centers").RequireAuthorization();
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/recode", Recode).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/move", Move).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/deactivate", Deactivate);
        g.MapPost("/{id:guid}/activate", Activate);
    }

    private static object Body(CostCenterView c) => new
    {
        id = c.Id, code = c.Code, nameAr = c.NameAr, nameEn = c.NameEn, parentId = c.ParentId, branchId = c.BranchId, isGroup = c.IsGroup, isActive = c.IsActive,
        canReceiveAllocations = c.CanReceiveAllocations, hasChildren = c.HasChildren, revision = c.Revision, createdAtUtc = c.CreatedAtUtc, updatedAtUtc = c.UpdatedAtUtc,
    };

    private static async Task<IResult> List(HttpContext http, AccessService access, CostCenterManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var list = await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!list.IsOk) return CompanyConfigurationEndpoints.Map(list, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new
        {
            costCenters = list.Value!.Select(Body),
            can = new
            {
                create = eff.Has(PermissionCatalog.Codes.CostCentersCreate, ScopeTarget.Company),
                update = eff.Has(PermissionCatalog.Codes.CostCentersUpdate, ScopeTarget.Company),
                activate = eff.Has(PermissionCatalog.Codes.CostCentersActivate, ScopeTarget.Company),
                recode = eff.Has(PermissionCatalog.Codes.CostCentersRecode, ScopeTarget.Company),
                restructure = eff.Has(PermissionCatalog.Codes.CostCentersRestructure, ScopeTarget.Company),
            },
        });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, CostCenterManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Create(CostCenterInput? body, HttpContext http, AccessService access, CostCenterManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, Body);
        http.Response.Headers.ETag = $"\"{r.Value!.Revision}\"";
        http.Response.Headers.Location = $"/api/v1/company/cost-centers/{r.Value.Id}";
        return Results.Json(Body(r.Value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Run(HttpContext http, AccessService access, Func<Guid, Guid, long?, Task<ConfigResult<CostCenterView>>> action)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await action(actor!.UserId, actor.CompanyId, expected), Body, v => v.Revision);
    }

    private static Task<IResult> Update(Guid id, CostCenterDetailsInput? body, HttpContext http, AccessService access, CostCenterManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.UpdateAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Recode(Guid id, CostCenterCodeInput? body, HttpContext http, AccessService access, CostCenterManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.RecodeAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Move(Guid id, CostCenterMoveInput? body, HttpContext http, AccessService access, CostCenterManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.MoveAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Deactivate(Guid id, HttpContext http, AccessService access, CostCenterManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.DeactivateAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Activate(Guid id, HttpContext http, AccessService access, CostCenterManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.ReactivateAsync(u, c, id, rev, http.RequestAborted));
}
