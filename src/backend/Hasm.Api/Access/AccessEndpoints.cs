using Hasm.Api.Auth;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Access;

public sealed record SelectCompanyRequest(Guid? CompanyId);
public sealed record CreateRoleRequest(string? Name, string[]? Permissions);
public sealed record SetPermissionsRequest(string[]? Permissions);
public sealed record GrantMembershipRequest(string? Login);
public sealed record AssignRoleRequest(Guid? UserId, Guid? RoleId, string? Scope, Guid? ScopeId);

public static class AccessSetup
{
    public static IServiceCollection AddHasmAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(PermissionCatalog.Default);
        // Runtime (least-privilege) database account; never the provisioning administrator. Connection string is built lazily.
        services.AddSingleton(sp => new AccessService(
            () =>
            {
                var options = HasmDatabaseOptions.FromConfiguration(configuration);
                options.EnsureValid(requireAdmin: false);
                return options.BuildSystemConnectionString(admin: false);
            },
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PermissionCatalog>(),
            sp.GetRequiredService<AuditTrail>(),
            sp.GetRequiredService<CompanyBranchScopeValidator>()));   // branch scope is confirmed against the real branches (and their activation epochs)
        return services;
    }
}

/// <summary>
/// Company access endpoints. The company is NEVER taken from the request: it comes from the server-side session
/// (set only through <c>POST /session/company</c> after a membership check) and is re-validated by the service on every call.
/// Every operation is authorized inside <see cref="AccessService"/>; the endpoints only translate results.
/// </summary>
public static class AccessEndpoints
{
    private const long MaxBodyBytes = 16 * 1024;
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => MaxBodyBytes; }

    public static void MapAccessEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/companies", ListCompanies);
        api.MapPost("/session/company", SelectCompany).WithMetadata(new BodyLimit());
        api.MapDelete("/session/company", ClearCompany);

        var access = api.MapGroup("/access");
        access.MapGet("/me", Me);
        access.MapGet("/permissions", PermissionCatalogView);
        access.MapGet("/roles", ListRoles);
        access.MapPost("/roles", CreateRole).WithMetadata(new BodyLimit());
        access.MapPut("/roles/{id:guid}/permissions", SetRolePermissions).WithMetadata(new BodyLimit());
        access.MapPost("/roles/{id:guid}/deactivate", DeactivateRole);
        access.MapGet("/members", ListMembers);
        access.MapPost("/members", GrantMembership).WithMetadata(new BodyLimit());
        access.MapDelete("/members/{userId:guid}", RevokeMembership);
        access.MapGet("/assignments", ListAssignments);
        access.MapPost("/assignments", AssignRole).WithMetadata(new BodyLimit());
        access.MapDelete("/assignments/{id:guid}", RevokeAssignment);
        access.MapGet("/audit", ListAudit);
    }

    internal static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    /// <summary>Maps a service outcome to HTTP. Forbidden is 403, other companies' objects are 404; no internals are revealed.</summary>
    private static IResult Map<T>(AccessResult<T> result, Func<T, IResult> ok, int successStatus = 200) => result.Status switch
    {
        AccessStatus.Ok => ok(result.Value!),
        AccessStatus.Forbidden => Error(403, result.Code ?? "forbidden"),
        AccessStatus.NotFound => Error(404, result.Code ?? "not_found"),
        AccessStatus.Invalid => Error(400, result.Code ?? "invalid_request"),
        _ => Error(409, result.Code ?? "conflict"),
    };

    internal sealed record Actor(Guid UserId, Guid CompanyId);

    /// <summary>User and company of the live session, read from the database (not from claims or the request).</summary>
    internal static async Task<(Actor? Actor, IResult? Failure)> ResolveActor(HttpContext http, AccessService access)
    {
        if (http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession session) return (null, Error(401, "unauthenticated"));
        var ctx = await access.GetSessionContextAsync(session.SessionId, http.RequestAborted);
        if (ctx is null) return (null, Error(401, "unauthenticated"));
        if (ctx.CompanyId is not { } companyId) return (null, Error(409, "company_not_selected"));
        return (new Actor(ctx.UserId, companyId), null);
    }

    private static string ScopeName(ScopeKind kind) => kind.ToString().ToLowerInvariant();

    private static bool TryParseScope(string? text, out ScopeKind kind)
    {
        kind = text?.ToLowerInvariant() switch { "company" => ScopeKind.Company, "branch" => ScopeKind.Branch, "warehouse" => ScopeKind.Warehouse, _ => (ScopeKind)(-1) };
        return Enum.IsDefined(kind);
    }

    // ---- company selection

    private static async Task<IResult> ListCompanies(HttpContext http, AccessService access)
    {
        if (http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession s) return Error(401, "unauthenticated");
        var companies = await access.ListAccessibleCompanySummariesAsync(s.UserId, http.RequestAborted);
        return Results.Json(new { companies = companies.Select(c => new { id = c.Id, nameAr = c.NameAr, nameEn = c.NameEn }) });
    }

    private static async Task<IResult> SelectCompany(SelectCompanyRequest? body, HttpContext http, AccessService access)
    {
        if (body?.CompanyId is not { } companyId || companyId == Guid.Empty) return Error(400, "invalid_request");
        if (http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession s) return Error(401, "unauthenticated");
        // Unknown company, no membership, revoked membership and unavailable company are indistinguishable.
        return await access.SelectCompanyAsync(s.SessionId, s.UserId, companyId, http.RequestAborted)
            ? Results.Json(new { companyId })
            : Error(403, "forbidden");
    }

    private static async Task<IResult> ClearCompany(HttpContext http, AccessService access)
    {
        if (http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession s) return Error(401, "unauthenticated");
        await access.SelectCompanyAsync(s.SessionId, s.UserId, null, http.RequestAborted);
        return Results.NoContent();
    }

    // ---- own permissions / catalog

    private static async Task<IResult> Me(HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        var effective = await access.GetEffectivePermissionsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new
        {
            companyId = actor.CompanyId,
            grants = effective.Grants.Select(g => new { roleId = g.RoleId, scope = ScopeName(g.Scope), scopeId = g.ScopeId, permissions = g.Permissions.Order(StringComparer.Ordinal) }),
        });
    }

    private static async Task<IResult> PermissionCatalogView(HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!await access.IsAllowedAsync(actor!.UserId, actor.CompanyId, PermissionCatalog.Codes.RolesView, ScopeTarget.Company, http.RequestAborted)) return Error(403, "forbidden");
        return Results.Json(new
        {
            resources = access.Catalog.Resources.Select(r => new { key = r.Key, scope = ScopeName(r.Scope), actions = r.Actions.Select(a => a.ToString().ToLowerInvariant()) }),
        });
    }

    // ---- roles

    private static object RoleJson(RoleView r) => new { id = r.Id, name = r.Name, preset = r.IsPreset, presetKey = r.PresetKey, active = r.IsActive, permissions = r.Permissions };

    private static async Task<IResult> ListRoles(HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await access.ListRolesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), roles => Results.Json(new { roles = roles.Select(RoleJson) }));
    }

    private static async Task<IResult> CreateRole(CreateRoleRequest? body, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        if (body is null) return Error(400, "invalid_request");
        return Map(await access.CreateRoleAsync(actor!.UserId, actor.CompanyId, body.Name, body.Permissions, http.RequestAborted), r => Results.Json(RoleJson(r), statusCode: 201));
    }

    private static async Task<IResult> SetRolePermissions(Guid id, SetPermissionsRequest? body, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        if (body is null) return Error(400, "invalid_request");
        return Map(await access.SetRolePermissionsAsync(actor!.UserId, actor.CompanyId, id, body.Permissions, http.RequestAborted), r => Results.Json(RoleJson(r)));
    }

    private static async Task<IResult> DeactivateRole(Guid id, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await access.DeactivateRoleAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), _ => Results.NoContent());
    }

    // ---- members

    private static async Task<IResult> ListMembers(HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await access.ListMembersAsync(actor!.UserId, actor.CompanyId, http.RequestAborted),
            list => Results.Json(new { members = list.Select(m => new { userId = m.UserId, login = m.Login, displayName = m.DisplayName, state = m.State.ToString(), grantedAtUtc = m.GrantedAtUtc }) }));
    }

    private static async Task<IResult> GrantMembership(GrantMembershipRequest? body, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        if (body is null) return Error(400, "invalid_request");
        return Map(await access.GrantMembershipAsync(actor!.UserId, actor.CompanyId, body.Login, http.RequestAborted),
            m => Results.Json(new { userId = m.UserId, login = m.Login, displayName = m.DisplayName }, statusCode: 201));
    }

    private static async Task<IResult> RevokeMembership(Guid userId, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await access.RevokeMembershipAsync(actor!.UserId, actor.CompanyId, userId, http.RequestAborted), _ => Results.NoContent());
    }

    // ---- assignments

    private static async Task<IResult> ListAssignments(Guid? userId, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await access.ListAssignmentsAsync(actor!.UserId, actor.CompanyId, userId, http.RequestAborted),
            list => Results.Json(new { assignments = list.Select(a => new { id = a.Id, userId = a.UserId, roleId = a.RoleId, scope = ScopeName(a.Scope), scopeId = a.ScopeId, grantedAtUtc = a.GrantedAtUtc }) }));
    }

    private static async Task<IResult> AssignRole(AssignRoleRequest? body, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        if (body?.UserId is not { } userId || body.RoleId is not { } roleId || !TryParseScope(body.Scope, out var scope)) return Error(400, "invalid_request");
        return Map(await access.AssignRoleAsync(actor!.UserId, actor.CompanyId, userId, roleId, scope, body.ScopeId, http.RequestAborted),
            a => Results.Json(new { id = a.Id, userId = a.UserId, roleId = a.RoleId, scope = ScopeName(a.Scope), scopeId = a.ScopeId }, statusCode: 201));
    }

    private static async Task<IResult> RevokeAssignment(Guid id, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await access.RevokeAssignmentAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), _ => Results.NoContent());
    }

    // ---- audit

    private static async Task<IResult> ListAudit(int? limit, long? before, HttpContext http, AccessService access)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await access.ListAuditAsync(actor!.UserId, actor.CompanyId, limit ?? 100, before, http.RequestAborted),
            list => Results.Json(new
            {
                entries = list.Select(e => new { id = e.Id, occurredAtUtc = e.OccurredAtUtc, actorUserId = e.ActorUserId, targetUserId = e.TargetUserId, roleId = e.RoleId, @event = e.Event.ToString(), detail = e.Detail }),
            }));
    }
}
