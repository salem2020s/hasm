using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Parties;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class PartyRoleSetup
{
    public static IServiceCollection AddHasmPartyRoles(this IServiceCollection services)
    {
        services.AddSingleton(sp => new PartyRoleService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PartyRoleManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<PartyRoleService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/party-roles and /api/v1/company/sales-representatives (Phase 3.9B): customer and supplier roles of local parties (terms, credit-limit data, control account, representative)
/// and the company's sales representatives. The company comes ONLY from the server-side session; every call is authorized inside <see cref="PartyRoleManagementService"/>
/// (<c>company.partyroles.*</c>, <c>company.salesreps.*</c>); changes use If-Match with the revision; there is no DELETE route: roles and representatives are deactivated, never removed.
/// </summary>
public static class PartyRoleEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 64 * 1024; }

    public static void MapPartyRoleEndpoints(this WebApplication app)
    {
        var roles = app.MapGroup("/api/v1/company/party-roles").RequireAuthorization();
        roles.MapGet("", ListRoles);
        roles.MapPost("", CreateRole).WithMetadata(new BodyLimit());
        roles.MapPut("/{id:guid}", UpdateRole).WithMetadata(new BodyLimit());
        var reps = app.MapGroup("/api/v1/company/sales-representatives").RequireAuthorization();
        reps.MapGet("", ListReps);
        reps.MapPost("", CreateRep).WithMetadata(new BodyLimit());
        reps.MapPut("/{id:guid}", UpdateRep).WithMetadata(new BodyLimit());
    }

    private static object RoleBody(PartyRoleView v) => new
    {
        id = v.Id, partyId = v.PartyId, partyCode = v.PartyCode, partyName = v.PartyName, role = (short)v.Role, isActive = v.IsActive, paymentMode = (short)v.PaymentMode, termDays = v.TermDays,
        creditLimit = v.CreditLimit, controlAccountId = v.ControlAccountId, controlAccountCode = v.ControlAccountCode, representativeId = v.RepresentativeId, revision = v.Revision,
    };
    private static object RepBody(SalesRepresentativeView v) => new { id = v.Id, code = v.Code, nameAr = v.NameAr, nameEn = v.NameEn, userId = v.UserId, isActive = v.IsActive, revision = v.Revision };

    private static async Task<IResult> Read<T>(HttpContext http, AccessService access, PartyRoleManagementService svc, bool roles, Func<Guid, Guid, Task<ConfigResult<T>>> call, Func<T, object> body)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await call(actor!.UserId, actor.CompanyId);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var caps = await svc.CapabilitiesAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { items = body(r.Value!), can = new { configure = roles ? caps.RolesConfigure : caps.RepsConfigure } });
    }

    private static async Task<IResult> Write<T>(HttpContext http, AccessService access, bool needsRevision, Func<Guid, Guid, long?, Task<ConfigResult<T>>> call, Func<T, object> body, Func<T, long?> revision)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        long? expected = null;
        if (needsRevision && !CompanyConfigurationEndpoints.TryReadIfMatch(http, out expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await call(actor!.UserId, actor.CompanyId, expected), body, revision);
    }

    private static Task<IResult> ListRoles(short? kind, HttpContext http, AccessService access, PartyRoleManagementService svc) =>
        kind is not (null or 1 or 2) ? Task.FromResult(CompanyConfigurationEndpoints.Error(400, "invalid_request"))
            : Read(http, access, svc, true, (u, c) => svc.ListRolesAsync(u, c, kind is null ? null : (PartyRoleKind)kind, http.RequestAborted), v => v.Select(RoleBody));
    private static Task<IResult> ListReps(HttpContext http, AccessService access, PartyRoleManagementService svc) =>
        Read(http, access, svc, false, (u, c) => svc.ListRepresentativesAsync(u, c, http.RequestAborted), v => v.Select(RepBody));

    private static Task<IResult> CreateRole(PartyRoleInput? body, HttpContext http, AccessService access, PartyRoleManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreateRoleAsync(u, c, body, http.RequestAborted), RoleBody, v => v.Revision);
    private static Task<IResult> UpdateRole(Guid id, PartyRoleInput? body, HttpContext http, AccessService access, PartyRoleManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdateRoleAsync(u, c, id, rev, body, http.RequestAborted), RoleBody, v => v.Revision);
    private static Task<IResult> CreateRep(SalesRepresentativeInput? body, HttpContext http, AccessService access, PartyRoleManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreateRepresentativeAsync(u, c, body, http.RequestAborted), RepBody, v => v.Revision);
    private static Task<IResult> UpdateRep(Guid id, SalesRepresentativeInput? body, HttpContext http, AccessService access, PartyRoleManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdateRepresentativeAsync(u, c, id, rev, body, http.RequestAborted), RepBody, v => v.Revision);
}
