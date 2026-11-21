using Microsoft.AspNetCore.Http.Metadata;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/attachment-retention (Phase R slice R1, owner decision D-082): the retention POLICY (versions), LEGAL HOLDS and a read-only eligibility PREVIEW for removed attachments. Nothing here deletes anything.
/// The company comes only from the server-side session; permissions are checked inside <see cref="AttachmentRetentionManagementService"/> (view, configure, hold, approve); changes use <c>If-Match</c> with the
/// revision; independence of author and approver, and of placer and releaser, is enforced by the service and PostgreSQL. While the retention basis (D-109) is owner-pending every preview item answers
/// <c>retention_basis_pending</c>. There is no DELETE route.
/// </summary>
public static class AttachmentRetentionEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 64 * 1024; }
    public sealed record ApproveRequest(string? ShorteningAcknowledgement);
    public sealed record ReasonRequest(string? Reason);

    public static void MapAttachmentRetentionEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/attachment-retention").RequireAuthorization();
        g.MapGet("/capabilities", Capabilities);
        g.MapGet("/policies", Policies);
        g.MapPost("/policies", Draft).WithMetadata(new BodyLimit());
        g.MapPost("/policies/{id:guid}/approve", Approve).WithMetadata(new BodyLimit());
        g.MapPost("/policies/{id:guid}/revoke", Revoke).WithMetadata(new BodyLimit());
        g.MapGet("/holds", Holds);
        g.MapPost("/holds", Place).WithMetadata(new BodyLimit());
        g.MapPost("/holds/{id:guid}/release", Release).WithMetadata(new BodyLimit());
        g.MapGet("/preview", Preview);
    }

    private sealed class CreatedEtag(string location, long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers.ETag = $"\"{revision}\"";
            http.Response.Headers.Location = location;
            http.Response.StatusCode = StatusCodes.Status201Created;
            return http.Response.WriteAsJsonAsync(body);
        }
    }

    private static object Body(RetentionPolicyVersionView v) => new
    {
        id = v.Id, version = v.Version, status = (short)v.Status, effectiveFrom = v.EffectiveFrom, shortensAnyPeriod = v.ShortensAnyPeriod, shorteningAcknowledgement = v.ShorteningAcknowledgement, note = v.Note,
        draftedBy = v.DraftedBy, draftedAtUtc = v.DraftedAtUtc, approvedBy = v.ApprovedBy, approvedAtUtc = v.ApprovedAtUtc, revokedBy = v.RevokedBy, revokedAtUtc = v.RevokedAtUtc, revokeReason = v.RevokeReason,
        endedAtUtc = v.EndedAtUtc, revision = v.Revision, rules = v.Rules.Select(r => new { subjectKind = r.SubjectKind, sensitivity = r.Sensitivity, months = r.Months, indefinite = r.Indefinite }),
    };

    private static object Body(LegalHoldView h) => new
    {
        id = h.Id, scope = (short)h.Scope, subjectKind = h.SubjectKind, subjectId = h.SubjectId, attachmentId = h.AttachmentId, reason = h.Reason, reference = h.Reference, placedBy = h.PlacedBy, placedAtUtc = h.PlacedAtUtc,
        releasedBy = h.ReleasedBy, releasedAtUtc = h.ReleasedAtUtc, releaseReason = h.ReleaseReason, isActive = h.IsActive, revision = h.Revision,
    };

    private static async Task<IResult> Capabilities(HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var c = await svc.CapabilitiesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { view = c.View, configure = c.Configure, hold = c.Hold, approve = c.Approve, execute = c.Execute, basisConfirmed = RetentionBasis.OwnerConfirmed });
    }

    private static async Task<IResult> Policies(HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ListPoliciesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new { policies = v.Select(Body) });
    }

    private static async Task<IResult> Draft(RetentionPolicyInput? body, HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.DraftPolicyAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/attachment-retention/policies/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> Approve(Guid id, ApproveRequest? body, HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.ApprovePolicyAsync(actor!.UserId, actor.CompanyId, id, expected, body?.ShorteningAcknowledgement, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Revoke(Guid id, ReasonRequest? body, HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.RevokePolicyAsync(actor!.UserId, actor.CompanyId, id, expected, body?.Reason, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Holds(bool? includeReleased, HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ListHoldsAsync(actor!.UserId, actor.CompanyId, includeReleased ?? false, http.RequestAborted), v => new { holds = v.Select(Body) });
    }

    private static async Task<IResult> Place(LegalHoldInput? body, HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.PlaceHoldAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/attachment-retention/holds/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> Release(Guid id, ReasonRequest? body, HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.ReleaseHoldAsync(actor!.UserId, actor.CompanyId, id, expected, body?.Reason, http.RequestAborted), Body, v => v.Revision);
    }

    /// <summary>
    /// The optional preview filter: <c>subjectKind</c> (a registered attachment record kind) and <c>subjectId</c> (a GUID, only together with a kind). Anything else answers the standard
    /// <c>invalid_request</c> body naming the field: <c>subject_kind_invalid</c> (unknown kind), <c>required</c> (an id without a kind), <c>invalid_value</c> (not a GUID).
    /// </summary>
    private static IResult? PreviewFilterError(string? subjectKind, string? subjectId, out string? kind, out Guid? id)
    {
        kind = null; id = null;
        static IResult Bad(string field, string code) => Results.Json(new { error = "invalid_request", fields = new[] { new { field, code } } }, statusCode: StatusCodes.Status400BadRequest);
        if (!string.IsNullOrWhiteSpace(subjectKind))
        {
            if (!AttachmentSubjects.Kinds.ContainsKey(subjectKind.Trim())) return Bad("subjectKind", "subject_kind_invalid");
            kind = subjectKind.Trim();
        }
        if (!string.IsNullOrWhiteSpace(subjectId))
        {
            if (kind is null) return Bad("subjectKind", "required");
            if (!Guid.TryParse(subjectId.Trim(), out var g)) return Bad("subjectId", "invalid_value");
            id = g;
        }
        return null;
    }

    private static async Task<IResult> Preview(string? offset, string? limit, string? subjectKind, string? subjectId, HttpContext http, AccessService access, AttachmentRetentionManagementService svc)
    {
        if (ListParameters.TryParse(null, offset, limit, out var q) is { } bad) return ListParameters.Invalid(bad);
        if (PreviewFilterError(subjectKind, subjectId, out var kind, out var id) is { } badFilter) return badFilter;
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.PreviewAsync(actor!.UserId, actor.CompanyId, q.Offset, q.Limit, kind, id, http.RequestAborted), p => new
        {
            total = p.Total, basisConfirmed = p.BasisConfirmed, reasonCounts = p.ReasonCounts,
            items = p.Items.Select(i => new { attachmentId = i.AttachmentId, subjectKind = i.SubjectKind, subjectId = i.SubjectId, isSensitive = i.IsSensitive, removedAtUtc = i.RemovedAtUtc, reason = i.Reason, eligibleAtUtc = i.EligibleAtUtc, holdId = i.HoldId, policyVersion = i.PolicyVersion }),
        });
    }
}
