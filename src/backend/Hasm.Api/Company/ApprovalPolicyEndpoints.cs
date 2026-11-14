using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/approval-policies: configurable approval policies (document type, source, branch, base-currency threshold, number of approvers). Company from the
/// session only; authorized inside <see cref="ApprovalPolicyManagementService"/>; If-Match with the policy revision. Approving a document is part of the journal (S5).
/// </summary>
public static class ApprovalPolicyEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 8 * 1024; }

    public static void MapApprovalPolicyEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/approval-policies").RequireAuthorization();
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/deactivate", Deactivate);
        g.MapPost("/{id:guid}/activate", Activate);
    }

    private static object Body(ApprovalPolicyView p) => new
    {
        id = p.Id, docType = p.DocType, source = p.Source.ToString().ToLowerInvariant(), branchId = p.BranchId,
        minAmount = p.MinAmount.ToString("0.0000", CultureInfo.InvariantCulture), requiredApprovals = p.RequiredApprovals,
        nameAr = p.NameAr, nameEn = p.NameEn, isActive = p.IsActive, revision = p.Revision, createdAtUtc = p.CreatedAtUtc, updatedAtUtc = p.UpdatedAtUtc,
    };

    private static async Task<IResult> List(HttpContext http, AccessService access, ApprovalPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var list = await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!list.IsOk) return CompanyConfigurationEndpoints.Map(list, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new
        {
            policies = list.Value!.Select(Body),
            documentTypes = DocumentTypes.All,
            can = new
            {
                create = eff.Has(PermissionCatalog.Codes.ApprovalPoliciesCreate, ScopeTarget.Company),
                update = eff.Has(PermissionCatalog.Codes.ApprovalPoliciesUpdate, ScopeTarget.Company),
                activate = eff.Has(PermissionCatalog.Codes.ApprovalPoliciesActivate, ScopeTarget.Company),
            },
        });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, ApprovalPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Create(ApprovalPolicyInput? body, HttpContext http, AccessService access, ApprovalPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, Body);
        http.Response.Headers.ETag = $"\"{r.Value!.Revision}\"";
        http.Response.Headers.Location = $"/api/v1/company/approval-policies/{r.Value.Id}";
        return Results.Json(Body(r.Value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Run(HttpContext http, AccessService access, Func<Guid, Guid, long?, Task<ConfigResult<ApprovalPolicyView>>> action)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await action(actor!.UserId, actor.CompanyId, expected), Body, v => v.Revision);
    }

    private static Task<IResult> Update(Guid id, ApprovalPolicyDetailsInput? body, HttpContext http, AccessService access, ApprovalPolicyManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.UpdateAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Deactivate(Guid id, HttpContext http, AccessService access, ApprovalPolicyManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.DeactivateAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Activate(Guid id, HttpContext http, AccessService access, ApprovalPolicyManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.ReactivateAsync(u, c, id, rev, http.RequestAborted));
}
