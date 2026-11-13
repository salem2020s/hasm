using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public sealed record DeactivateBranchRequest(Guid? NewMainId);

public static class BranchSetup
{
    public static IServiceCollection AddHasmBranches(this IServiceCollection services)
    {
        services.AddSingleton(sp => new CompanyBranchScopeValidator(() => sp.GetRequiredService<CompanyDatabaseResolver>()));
        services.AddSingleton(sp => new CompanyBranchService(
            sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(),
            sp.GetRequiredService<IEntitlementPolicy>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new BranchManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<CompanyBranchService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/branches: branch management. Same rules as the other company endpoints: the company comes ONLY from the server-side
/// session, every call is authorized inside <see cref="BranchManagementService"/> (RBAC, branch scope, activation epochs), responses are
/// <c>no-store</c>, state-changing requests pass the anti-CSRF middleware, and changes use <c>If-Match</c> with the branch revision
/// (428 missing, 412 stale). Invariants, the Trial limit, auditing and concurrency are enforced server-side and by PostgreSQL; both clients
/// (Web and Windows Desktop) use exactly these endpoints.
/// </summary>
public static class BranchEndpoints
{
    private const long MaxBodyBytes = 16 * 1024;
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => MaxBodyBytes; }

    public static void MapBranchEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/branches").RequireAuthorization();
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/deactivate", Deactivate).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/activate", Activate);
        g.MapPost("/{id:guid}/make-main", MakeMain);
    }

    private static object Body(BranchView b) => new
    {
        id = b.Id, code = b.Data.Code, nameAr = b.Data.NameAr, nameEn = b.Data.NameEn,
        isActive = b.IsActive, isMain = b.IsMain, activationEpoch = b.ActivationEpoch, seeded = b.SeedMigration is not null,
        addressLine1 = b.Data.AddressLine1, addressLine2 = b.Data.AddressLine2, city = b.Data.City, region = b.Data.Region, postalCode = b.Data.PostalCode,
        phone = b.Data.Phone, email = b.Data.Email, website = b.Data.Website,
        revision = b.Revision, createdAtUtc = b.CreatedAtUtc, updatedAtUtc = b.UpdatedAtUtc,
    };

    private static async Task<IResult> List(HttpContext http, AccessService access, BranchManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var list = await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!list.IsOk) return CompanyConfigurationEndpoints.Map(list, _ => new { });
        var limits = await svc.LimitsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        var l = limits.Value;
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new
        {
            branches = list.Value!.Select(Body),
            limit = l?.Limit, everCreated = l?.EverCreated,
            // What this caller may do (the server still authorizes every action itself; this only lets the UI show/hide controls).
            can = new
            {
                create = eff.Has(PermissionCatalog.Codes.BranchCreate, ScopeTarget.Company),
                activate = eff.Has(PermissionCatalog.Codes.BranchActivate, ScopeTarget.Company),
                designate = eff.Has(PermissionCatalog.Codes.BranchDesignate, ScopeTarget.Company),
                update = list.Value!.Where(b => eff.Has(PermissionCatalog.Codes.BranchesUpdate, ScopeTarget.Branch(b.Id))).Select(b => b.Id),
            },
        });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, BranchManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Create(BranchInput? body, HttpContext http, AccessService access, BranchManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag(r.Value!.Id, r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> Update(Guid id, BranchInput? body, HttpContext http, AccessService access, BranchManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.UpdateAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Deactivate(Guid id, DeactivateBranchRequest? body, HttpContext http, AccessService access, BranchManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.DeactivateAsync(actor!.UserId, actor.CompanyId, id, expected, body?.NewMainId, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Activate(Guid id, HttpContext http, AccessService access, BranchManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.ReactivateAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> MakeMain(Guid id, HttpContext http, AccessService access, BranchManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.MakeMainAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted), Body, v => v.Revision);
    }

    /// <summary>201 with the strong revision as <c>ETag</c> and the new resource location.</summary>
    private sealed class CreatedEtag(Guid id, long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = $"\"{revision}\"";
            httpContext.Response.Headers.Location = $"/api/v1/company/branches/{id}";
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            return httpContext.Response.WriteAsJsonAsync(body);
        }
    }
}
