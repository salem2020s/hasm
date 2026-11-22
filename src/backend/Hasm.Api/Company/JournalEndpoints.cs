using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/journal: journal entries and the posting workflow, and /api/v1/company/ledger-settings. The company comes ONLY from the server-side session, every call is
/// authorized inside <see cref="JournalManagementService"/> (branch-scoped RBAC re-read on each request), responses are no-store, and changes use If-Match with the entry
/// revision (428 missing, 412 stale). Amounts are returned as exact decimal strings. Both clients (Web and Windows Desktop) use exactly these endpoints.
/// </summary>
public static class JournalEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 512 * 1024; }

    public static void MapJournalEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/journal").RequireAuthorization();
        g.MapGet("", List);
        g.MapGet("/cost-centers", AllocatableCostCenters);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/submit", Submit).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        g.MapPost("/{id:guid}/withdraw", Withdraw).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        g.MapPost("/{id:guid}/cancel", Cancel).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        g.MapPost("/{id:guid}/decision", Decide).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        g.MapPost("/{id:guid}/post", Post).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        g.MapPost("/{id:guid}/reverse", Reverse).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        var s = app.MapGroup("/api/v1/company/ledger-settings").RequireAuthorization();
        s.MapGet("", GetSettings);
        s.MapPut("", PutSettings).WithMetadata(new BodyLimit());
    }

    private static string M(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string StatusName(JournalStatus s) => s switch
    {
        JournalStatus.Draft => "draft", JournalStatus.PendingApproval => "pending_approval", JournalStatus.Approved => "approved",
        JournalStatus.Posted => "posted", JournalStatus.Reversed => "reversed", _ => "cancelled",
    };

    private static object Header(JournalEntrySummary h) => new
    {
        id = h.Id, number = h.Number, status = StatusName(h.Status), type = h.Type.ToString().ToLowerInvariant(), source = h.Source.ToString().ToLowerInvariant(), sourceKey = h.SourceKey,
        entryDate = D(h.EntryDate), description = h.Description, branchId = h.BranchId, baseCurrency = h.BaseCurrency, baseTotal = M(h.BaseTotal), revision = h.Revision,
        createdBy = h.CreatedBy, createdAtUtc = h.CreatedAtUtc, updatedAtUtc = h.UpdatedAtUtc, submittedBy = h.SubmittedBy, submittedAtUtc = h.SubmittedAtUtc,
        postedBy = h.PostedBy, postedAtUtc = h.PostedAtUtc, periodId = h.PeriodId, postedInSoftClosedPeriod = h.PostedInSoftClosedPeriod, reversesId = h.ReversesId, adjustsId = h.AdjustsId,
    };

    private static object Body(JournalEntryView v) => new
    {
        entry = Header(v.Header), balanced = v.Balanced, baseDebitTotal = M(v.BaseDebitTotal), baseCreditTotal = M(v.BaseCreditTotal), requiredApprovals = v.RequiredApprovals,
        lines = v.Lines.Select(l => new
        {
            id = l.Id, lineNo = l.LineNo, accountId = l.AccountId, description = l.Description, currency = l.Currency, debit = M(l.Debit), credit = M(l.Credit),
            rate = l.Rate.ToString("0.0000000000", CultureInfo.InvariantCulture), rateId = l.RateId, rateOverrideReason = l.RateOverrideReason,
            baseDebit = M(l.BaseDebit), baseCredit = M(l.BaseCredit), branchId = l.BranchId, isRounding = l.IsRounding, reversesLineId = l.ReversesLineId,
            allocations = l.Allocations.Select(a => new { costCenterId = a.CostCenterId, amount = M(a.Amount), baseAmount = M(a.BaseAmount) }),
        }),
        approvals = v.Approvals.Select(a => new { id = a.Id, documentRevision = a.DocumentRevision, approver = a.Approver, approved = a.Approved, decidedAtUtc = a.DecidedAtUtc, comment = a.Comment, stage = a.Stage }),
        plan = (v.Plan ?? []).Select(p => new { stage = p.Stage, required = p.Required, approved = p.Approved, roleId = p.RoleId }),
        events = v.Events.Select(e => new { id = e.Id, kind = e.Kind.ToString(), actor = e.Actor, atUtc = e.AtUtc, revision = e.Revision, detail = e.Detail }),
    };

    private static JournalStatus? ParseStatus(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "draft" => JournalStatus.Draft, "pending_approval" => JournalStatus.PendingApproval, "approved" => JournalStatus.Approved,
        "posted" => JournalStatus.Posted, "reversed" => JournalStatus.Reversed, "cancelled" => JournalStatus.Cancelled, _ => null,
    };

    /// <summary>The cost centers the caller may allocate to (active leaves inside their grants): what the journal editor offers, independent of the administration permission.</summary>
    private static async Task<IResult> AllocatableCostCenters(HttpContext http, AccessService access, JournalManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.AllocatableCostCentersAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new { costCenters = v.Select(c => new { id = c.Id, code = c.Code, nameAr = c.NameAr, nameEn = c.NameEn, parentId = c.ParentId, branchId = c.BranchId }) });
    }

    private static async Task<IResult> List(string? status, DateOnly? from, DateOnly? to, Guid? branchId, string? q, int? limit, int? offset, HttpContext http, AccessService access, JournalManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!string.IsNullOrWhiteSpace(status) && ParseStatus(status) is null) return CompanyConfigurationEndpoints.Error(400, "invalid_status");
        var r = await svc.ListAsync(actor!.UserId, actor.CompanyId, new JournalFilter(ParseStatus(status), from, to, branchId, q, limit ?? 100, offset ?? 0), http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new
        {
            entries = r.Value!.Select(Header),
            can = new
            {
                create = eff.Grants.Any(x => x.Permissions.Contains(PermissionCatalog.Codes.JournalCreate)),
                approve = eff.Grants.Any(x => x.Permissions.Contains(PermissionCatalog.Codes.JournalApprove)),
                post = eff.Grants.Any(x => x.Permissions.Contains(PermissionCatalog.Codes.JournalPost)),
                reverse = eff.Grants.Any(x => x.Permissions.Contains(PermissionCatalog.Codes.JournalReverse)),
                rateOverride = eff.Has(PermissionCatalog.Codes.JournalRateOverride, ScopeTarget.Company),
                postInSoftClosed = eff.Has(PermissionCatalog.Codes.JournalSoftClosedPost, ScopeTarget.Company),
            },
        });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, JournalManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Header.Revision);
    }

    private static async Task<IResult> Create(JournalInput? body, HttpContext http, AccessService access, JournalManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, Body);
        http.Response.Headers.ETag = $"\"{r.Value!.Header.Revision}\"";
        http.Response.Headers.Location = $"/api/v1/company/journal/{r.Value.Header.Id}";
        return Results.Json(Body(r.Value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Run(HttpContext http, AccessService access, Func<Guid, Guid, long?, Task<ConfigResult<JournalEntryView>>> action)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await action(actor!.UserId, actor.CompanyId, expected), Body, v => v.Header.Revision);
    }

    private static Task<IResult> Update(Guid id, JournalInput? body, HttpContext http, AccessService access, JournalManagementService svc) => Run(http, access, (u, c, rev) => svc.UpdateAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Submit(Guid id, HttpContext http, AccessService access, JournalManagementService svc) => Run(http, access, (u, c, rev) => svc.SubmitAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Withdraw(Guid id, HttpContext http, AccessService access, JournalManagementService svc) => Run(http, access, (u, c, rev) => svc.WithdrawAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Cancel(Guid id, HttpContext http, AccessService access, JournalManagementService svc) => Run(http, access, (u, c, rev) => svc.CancelAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Decide(Guid id, ApprovalDecisionInput? body, HttpContext http, AccessService access, JournalManagementService svc) => Run(http, access, (u, c, rev) => svc.DecideAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Post(Guid id, HttpContext http, AccessService access, JournalManagementService svc) => Run(http, access, (u, c, rev) => svc.PostAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Reverse(Guid id, ReversalInput? body, HttpContext http, AccessService access, JournalManagementService svc) => Run(http, access, (u, c, rev) => svc.ReverseAsync(u, c, id, rev, body, http.RequestAborted));

    private static object SettingsBody(LedgerSettingsView s) => new { roundingAccountId = s.RoundingAccountId, revision = s.Revision, configured = s.Exists };

    private static async Task<IResult> GetSettings(HttpContext http, AccessService access, LedgerSettingsManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), SettingsBody, v => v.Revision);
    }

    private static async Task<IResult> PutSettings(LedgerSettingsInput? body, HttpContext http, AccessService access, LedgerSettingsManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.PutAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted), SettingsBody, v => v.Revision);
    }
}
