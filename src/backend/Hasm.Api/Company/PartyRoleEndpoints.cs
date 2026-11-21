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

    /// <summary>
    /// Without <c>search</c>, <c>offset</c>, <c>limit</c> or <c>ids</c> the list is exactly what it always was (all roles, no <c>total</c>). With any of them it is one bounded page
    /// (default 50, at most 500, ordered by party code) and the response adds <c>total</c>. <c>ids</c> (repeated, at most <see cref="ListPaging.MaxIds"/>) are PARTY ids: only those parties' roles come back
    /// (up to 500 rows unless a smaller <c>limit</c> is given), so a screen can resolve the names of the parties it shows without loading every customer. The permission and company scoping are the same in all forms.
    /// </summary>
    private static Task<IResult> ListRoles(short? kind, string? search, int? offset, int? limit, Guid[]? ids, HttpContext http, AccessService access, PartyRoleManagementService svc)
    {
        if (kind is not (null or 1 or 2)) return Task.FromResult(CompanyConfigurationEndpoints.Error(400, "invalid_request"));
        var role = kind is null ? (PartyRoleKind?)null : (PartyRoleKind)kind;
        if (ids is { Length: 0 }) ids = null;      // the framework binds an absent repeated parameter as an empty array: "no ids named" is the unfiltered list, never "no rows"
        if (search is null && offset is null && limit is null && ids is null)
            return Read(http, access, svc, true, (u, c) => svc.ListRolesAsync(u, c, role, http.RequestAborted), v => v.Select(RoleBody));
        if (offset is < 0 || limit is < 1 || ids is { Length: > ListPaging.MaxIds } || !ListPaging.TryCleanSearch(search, out _)) return Task.FromResult(CompanyConfigurationEndpoints.Error(400, "invalid_request"));
        return ReadPaged(http, access, svc, (u, c) => svc.SearchRolesAsync(u, c, role, search, offset, limit, ids, http.RequestAborted));
    }

    private static async Task<IResult> ReadPaged(HttpContext http, AccessService access, PartyRoleManagementService svc, Func<Guid, Guid, Task<ConfigResult<PartyRolePage>>> call)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await call(actor!.UserId, actor.CompanyId);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var caps = await svc.CapabilitiesAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { items = r.Value!.Items.Select(RoleBody), total = r.Value.Total, can = new { configure = caps.RolesConfigure } });
    }
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
