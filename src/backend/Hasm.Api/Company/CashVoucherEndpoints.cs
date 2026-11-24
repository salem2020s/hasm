using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class CashVoucherSetup
{
    public static IServiceCollection AddHasmCashVouchers(this IServiceCollection services)
    {
        services.AddSingleton(sp => new CashVoucherService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new CashVoucherManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<CashVoucherService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/cash-vouchers: receipt and payment vouchers WITHOUT any financial posting (Phase 3.14 slice S2a). The company comes ONLY from the server-side session; every call is authorized inside
/// <see cref="CashVoucherManagementService"/> (RBAC by BRANCH: a voucher of another branch answers like an unknown id); changes use <c>If-Match</c> with the revision (428 missing, 412 stale); creation is idempotent by key
/// (201 for a new voucher, 200 with <c>replayed</c> for a repeated key). There is NO delete, approve, reject, number, journal, post or reverse route: financial posting for cash and bank stays closed (D-214).
/// </summary>
public static class CashVoucherEndpoints
{
    private const string Root = "/api/v1/company/cash-vouchers";
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 512 * 1024; }
    public sealed record CancelBody(string? Reason);

    public static void MapCashVoucherEndpoints(this WebApplication app)
    {
        var g = app.MapGroup(Root).RequireAuthorization();
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/submit", Submit).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        g.MapPost("/{id:guid}/withdraw", Withdraw).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        g.MapPost("/{id:guid}/cancel", Cancel).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
    }

    private static string StatusName(CashVoucherStatus s) => s switch { CashVoucherStatus.Draft => "draft", CashVoucherStatus.Submitted => "submitted", _ => "cancelled" };

    private static object Body(CashVoucherView v) => new
    {
        id = v.Id, kind = CashVoucherRules.KindName(v.Kind), cashAccountId = v.CashAccountId, branchId = v.BranchId, currency = v.Currency, voucherDate = v.VoucherDate, partyId = v.PartyId, reference = v.Reference, note = v.Note,
        status = StatusName(v.Status), total = v.Total, submittedBy = v.SubmittedBy, updatedBy = v.UpdatedBy, revision = v.Revision, createdBy = v.CreatedBy, createdAtUtc = v.CreatedAtUtc, updatedAtUtc = v.UpdatedAtUtc, replayed = v.Replayed,
        lines = v.Lines.Select(l => new { lineNo = l.LineNo, accountId = l.AccountId, amount = l.Amount, description = l.Description }),
        events = v.Events.Select(e => new { kind = (short)e.Kind, fromStatus = e.FromStatus is { } f ? StatusName(f) : null, toStatus = StatusName(e.ToStatus), revision = e.Revision, actorUserId = e.ActorUserId, reason = e.Reason, atUtc = e.AtUtc }),
    };

    private static async Task<IResult> Run(HttpContext http, AccessService access, bool needsRevision, Func<Guid, Guid, long?, Task<ConfigResult<CashVoucherView>>> call)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        long? expected = null;
        if (needsRevision && !CompanyConfigurationEndpoints.TryReadIfMatch(http, out expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await call(actor!.UserId, actor.CompanyId, expected), Body, v => v.Revision);
    }

    private static async Task<IResult> List(HttpContext http, AccessService access, CashVoucherManagementService svc, string? status, string? kind, Guid? cashAccountId, Guid? branchId, int? limit, int? offset)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        CashVoucherStatus? st = status switch { "draft" => CashVoucherStatus.Draft, "submitted" => CashVoucherStatus.Submitted, "cancelled" => CashVoucherStatus.Cancelled, _ => null };
        if (status is not null && st is null) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        var k = CashVoucherRules.ParseKind(kind);
        if (kind is not null && k is null) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        var r = await svc.ListAsync(actor!.UserId, actor.CompanyId, new CashVoucherFilter(st, k, cashAccountId, branchId, null, limit ?? 50, offset ?? 0), http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new { vouchers = v.Select(Body) });
    }

    private static Task<IResult> Get(Guid id, HttpContext http, AccessService access, CashVoucherManagementService svc) =>
        Run(http, access, false, (u, c, _) => svc.GetAsync(u, c, id, http.RequestAborted));

    private static async Task<IResult> Create(CashVoucherInput? body, HttpContext http, AccessService access, CashVoucherManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk && !r.Value!.Replayed ? new CreatedEtag(r.Value.Id, r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body, v => v.Revision);
    }

    private static Task<IResult> Update(Guid id, CashVoucherInput? body, HttpContext http, AccessService access, CashVoucherManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.UpdateAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> Submit(Guid id, HttpContext http, AccessService access, CashVoucherManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.SubmitAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Withdraw(Guid id, HttpContext http, AccessService access, CashVoucherManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.WithdrawAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> Cancel(Guid id, CancelBody? body, HttpContext http, AccessService access, CashVoucherManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.CancelAsync(u, c, id, rev, body?.Reason, http.RequestAborted));

    private sealed class CreatedEtag(Guid id, long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = $"\"{revision}\"";
            httpContext.Response.Headers.Location = $"{Root}/{id}";
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            return httpContext.Response.WriteAsJsonAsync(body);
        }
    }
}
