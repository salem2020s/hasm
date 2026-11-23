using Hasm.Infrastructure.Security;
using Hasm.Api.Auth;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Access;

public sealed record SelectCompanyRequest(Guid? CompanyId);
public sealed record SetMfaPolicyRequest(string? Policy, Guid[]? RoleIds);
public sealed record CreateRoleRequest(string? Name, string[]? Permissions);
public sealed record SetPermissionsRequest(string[]? Permissions);
public sealed record GrantMembershipRequest(string? Login);
public sealed record AssignRoleRequest(Guid? UserId, Guid? RoleId, string? Scope, Guid? ScopeId);

public static class AccessSetup
{
    public static IServiceCollection AddHasmAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(PermissionCatalog.Default);
        // Phase 4.1 S3b-1b: the company MFA policy (same runtime connection; the service asks the access service for permissions and the identity store for the actor's MFA state)
        services.AddSingleton(sp => new MfaPolicyService(
            () =>
            {
                var options = HasmDatabaseOptions.FromConfiguration(configuration);
                options.EnsureValid(requireAdmin: false);
                return options.BuildSystemConnectionString(admin: false);
            },
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AuditTrail>(), sp.GetRequiredService<AccessService>(), sp.GetRequiredService<IdentityStore>()));
        services.AddSingleton<ICompanyMfaPolicyEvaluator>(sp => sp.GetRequiredService<MfaPolicyService>());
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
            sp.GetRequiredService<CompanyBranchScopeValidator>(), sp.GetRequiredService<IdentityKeys>()));   // branch scope is confirmed against the real branches (and their activation epochs)
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
        // company list and selection stay reachable whatever the selected company's MFA policy says (a user must always be able to leave or change company)
        var open = app.MapGroup("/api/v1").RequireAuthorization(AuthDefaults.AuthenticatedPolicy);
        open.MapGet("/companies", ListCompanies);
        open.MapPost("/session/company", SelectCompany).WithMetadata(new BodyLimit());
        open.MapDelete("/session/company", ClearCompany);

        var api = app.MapGroup("/api/v1").RequireAuthorization();

        var access = api.MapGroup("/access").RequireAuthorization(AuthDefaults.MfaVerifiedPolicy);
        access.MapGet("/me", Me);
        access.MapGet("/permissions", PermissionCatalogView);
        access.MapGet("/roles", ListRoles);
        access.MapPost("/roles", CreateRole).WithMetadata(new BodyLimit());
        access.MapPut("/roles/{id:guid}/permissions", SetRolePermissions).WithMetadata(new BodyLimit());
        access.MapPost("/roles/{id:guid}/deactivate", DeactivateRole).WithMetadata(new BodyLimit());
        access.MapGet("/members", ListMembers);
        access.MapPost("/members", GrantMembership).WithMetadata(new BodyLimit());
        access.MapDelete("/members/{userId:guid}", RevokeMembership);
        access.MapGet("/assignments", ListAssignments);
        access.MapPost("/assignments", AssignRole).RequireAuthorization(AuthDefaults.MfaFreshPolicy).WithMetadata(new BodyLimit());
        access.MapDelete("/assignments/{id:guid}", RevokeAssignment);
        access.MapGet("/audit", ListAudit);
        access.MapGet("/mfa-policy", GetMfaPolicy);
        access.MapPut("/mfa-policy", SetMfaPolicy).RequireAuthorization(AuthDefaults.MfaFreshPolicy).WithMetadata(new BodyLimit());
    }

    internal static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    /// <summary>Maps a service outcome to HTTP. Forbidden is 403, other companies' objects are 404; no internals are revealed.</summary>
    private static IResult Map<T>(AccessResult<T> result, Func<T, IResult> ok, int successStatus = 200) => result.Status switch
    {
        AccessStatus.Ok => ok(result.Value!),
        AccessStatus.Forbidden => Error(403, result.Code ?? "forbidden"),
        AccessStatus.NotFound => Error(404, result.Code ?? "not_found"),
        AccessStatus.Invalid => Error(400, result.Code ?? "invalid_request"),
        // optimistic concurrency, the same answers as the settings endpoints (CompanyConfigurationEndpoints.Map): 428 without If-Match, 412 with the current revision on a stale one
        AccessStatus.PreconditionRequired => Error(428, "precondition_required"),
        AccessStatus.PreconditionFailed => Results.Json(new { error = "revision_conflict", currentRevision = result.CurrentRevision }, statusCode: 412),
        _ => Error(409, result.Code ?? "conflict"),
    };

    internal sealed record Actor(Guid UserId, Guid CompanyId);

    /// <summary>User and company of the live session, read from the database (not from claims or the request).</summary>
    internal static async Task<(Actor? Actor, IResult? Failure)> ResolveActor(HttpContext http, AccessService access)
    {
        if (http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession session) return (null, Error(401, "unauthenticated"));
        var ctx = await access.GetSessionContextAsync(session.SessionId, http.RequestAborted);
        if (ctx is null) return (null, Error(401, "unauthenticated"));
        // Review H1 of PR 181: the company MFA policy was judged for the company the session had when it was authenticated. The selection is read again here, so a different company now (another request
        // of the same session selected it in between) was NOT judged: refuse, fail closed, with a stable code. The client repeats the request, which is then judged for the new company.
        if (http.Items[MfaEnforcement.EvaluatedCompanyItemKey] is MfaEnforcement.EvaluatedCompany evaluated && evaluated.CompanyId != ctx.CompanyId)
            return (null, Error(409, "company_selection_changed"));
        if (ctx.CompanyId is not { } companyId) return (null, Error(409, "company_not_selected"));
        return (new Actor(ctx.UserId, companyId), null);
    }

    private static string ScopeName(ScopeKind kind) => kind.ToString().ToLowerInvariant();

    private static bool TryParseScope(string? text, out ScopeKind kind)
    {
        kind = text?.ToLowerInvariant() switch { "company" => ScopeKind.Company, "branch" => ScopeKind.Branch, "warehouse" => ScopeKind.Warehouse, "costcenter" => ScopeKind.CostCenter, _ => (ScopeKind)(-1) };
        return Enum.IsDefined(kind);
    }

    // ---- company MFA policy (Phase 4.1 S3b-1b, D-167)

    private static object PolicyView(CompanyMfaPolicy p) => new
    {
        policy = p.Kind switch { CompanyMfaPolicyKind.RequiredForRoles => "roles", CompanyMfaPolicyKind.RequiredForAll => "all", _ => "off" },
        roleIds = p.RoleIds,
        revision = p.Revision,
        updatedAtUtc = p.UpdatedAtUtc,
    };

    /// <summary>The policy with its revision as the strong <c>ETag</c> (the convention of the settings endpoints).</summary>
    private static IResult PolicyResult(CompanyMfaPolicy p) => new Hasm.Api.Company.CompanyConfigurationEndpoints.EtagResult(p.Revision, PolicyView(p));

    private static async Task<IResult> GetMfaPolicy(HttpContext http, AccessService access, MfaPolicyService policies)
    {
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await policies.GetAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), PolicyResult);
    }

    private static async Task<IResult> SetMfaPolicy(SetMfaPolicyRequest? body, HttpContext http, AccessService access, MfaPolicyService policies)
    {
        var kind = body?.Policy?.ToLowerInvariant() switch { "off" => (CompanyMfaPolicyKind?)CompanyMfaPolicyKind.Off, "roles" => CompanyMfaPolicyKind.RequiredForRoles, "all" => CompanyMfaPolicyKind.RequiredForAll, _ => null };
        if (kind is null) return Error(400, "invalid_request");
        var (actor, failure) = await ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!Hasm.Api.Company.CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return Error(400, "invalid_if_match");
        return Map(await policies.SetAsync(actor!.UserId, actor.CompanyId, expected, kind.Value, body!.RoleIds, http.RequestAborted), PolicyResult);
    }

    // ---- company selection

    private static async Task<IResult> ListCompanies(HttpContext http, AccessService access)
    {
        if (http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession s) return Error(401, "unauthenticated");
        var companies = await access.ListAccessibleCompanySummariesAsync(s.UserId, http.RequestAborted);
        return Results.Json(new { companies = companies.Select(c => new { id = c.Id, nameAr = c.NameAr, nameEn = c.NameEn }) });
    }

    private static async Task<IResult> SelectCompany(SelectCompanyRequest? body, HttpContext http, MfaPolicyService policies)
    {
        if (body?.CompanyId is not { } companyId || companyId == Guid.Empty) return Error(400, "invalid_request");
        if (http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession s) return Error(401, "unauthenticated");
        // ONE decision (review H1 of PR 181): membership, then the company's MFA policy (S3b-1b, D-167), and the selection is written only when both allow it, in one transaction under the company's policy
        // lock. Nothing is written and then undone, so no concurrent request can be served for a company this session may not enter, and a refused selection keeps the previously selected company.
        // Unknown company, no membership, revoked membership and unavailable company are indistinguishable, and the policy is asked only after the membership check.
        return await policies.SelectCompanyAsync(s.SessionId, s.UserId, companyId, s.MfaState, s.MfaVerifiedAtUtc, http.RequestAborted) switch
        {
            CompanySelectionOutcome.Selected => Results.Json(new { companyId }),
            CompanySelectionOutcome.EnrolmentRequired => Error(403, "mfa_enrolment_required"),
            CompanySelectionOutcome.StepUpRequired => Error(401, "mfa_step_up_required"),
            _ => Error(403, "forbidden"),
        };
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
