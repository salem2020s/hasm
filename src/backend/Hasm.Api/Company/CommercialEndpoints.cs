using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Pricing;
using Hasm.Infrastructure.Taxes;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class CommercialSetup
{
    /// <summary>Production registers NO document type in slice S1a (owner decision Q1 / D-049): the registry is empty here and the test composition replaces it (the last registration wins).</summary>
    public static IServiceCollection AddHasmCommercial(this IServiceCollection services)
    {
        services.AddSingleton<ICommercialDocumentTypeRegistry>(new CommercialDocumentTypeRegistry([]));
        services.AddSingleton(sp => new CommercialDocumentService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ICommercialDocumentTypeRegistry>(), sp.GetRequiredService<PricingConfigService>(), sp.GetRequiredService<TaxConfigService>()));
        services.AddSingleton(sp => new CommercialDocumentManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<CommercialDocumentService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/commercial-documents (Phase 3.13 slice S1a). The company comes ONLY from the server-side session; every call is authorized inside <see cref="CommercialDocumentManagementService"/>
/// (branch-scoped <c>company.commercialdocs.*</c>). Changes use <c>If-Match</c> with the document revision (428 without, 412 with the current revision on a stale one). There is no DELETE route. Approval and rejection need the separate approve permission.
/// A request body carries no price, tax, cost or snapshot field: unknown fields are ignored.
/// </summary>
public static class CommercialEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 1024 * 1024; }
    public sealed record CancelBody(string? Reason);

    public static void MapCommercialEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/commercial-documents").RequireAuthorization();
        g.MapGet("/", List);
        g.MapPost("/", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/submit", Submit);
        g.MapPost("/{id:guid}/withdraw", Withdraw);
        g.MapPost("/{id:guid}/approve", Approve);
        g.MapPost("/{id:guid}/reject", Reject).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/cancel", Cancel).WithMetadata(new BodyLimit());
    }

    private static object Body(CommercialDocumentView d) => new
    {
        id = d.Id, docType = d.DocType, branchId = d.BranchId, partyId = d.PartyId, currency = d.Currency, documentDate = d.DocumentDate, mode = (short)d.Mode, status = (short)d.Status, note = d.Note,
        submittedBy = d.SubmittedBy, officialNumber = d.OfficialNumber, approvedBy = d.ApprovedBy, approvedAtUtc = d.ApprovedAtUtc, sealedAtUtc = d.SealedAtUtc, exchangeRate = d.ExchangeRate, totalNet = d.TotalNet, totalTax = d.TotalTax, totalGross = d.TotalGross, revision = d.Revision, createdBy = d.CreatedBy, createdAtUtc = d.CreatedAtUtc, updatedAtUtc = d.UpdatedAtUtc, replayed = d.Replayed,
        lines = d.Lines.Select(l => new { lineNo = l.LineNo, itemId = l.ItemId, unitId = l.UnitId, quantity = l.Quantity, warehouseId = l.WarehouseId, lineNet = l.LineNet, pricingHash = l.PricingHash }),
        events = d.Events.Select(e => new { kind = (short)e.Kind, fromStatus = e.FromStatus is { } f ? (short?)f : null, toStatus = (short)e.ToStatus, revision = e.Revision, actorUserId = e.ActorUserId, reason = e.Reason, atUtc = e.AtUtc }),
    };

    private static async Task<IResult> Run<T>(HttpContext http, AccessService access, bool needsRevision, Func<Guid, Guid, long?, Task<ConfigResult<T>>> call, Func<T, object> body, Func<T, long?>? revision)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        long? expected = null;
        if (needsRevision && !CompanyConfigurationEndpoints.TryReadIfMatch(http, out expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await call(actor!.UserId, actor.CompanyId, expected), body, revision);
    }

    private static Task<IResult> List(HttpContext http, AccessService access, CommercialDocumentManagementService svc, int? status, string? docType, Guid? branchId, int? limit, int? offset) =>
        Run(http, access, false, (u, c, _) => svc.ListAsync(u, c, new CommercialDocumentFilter(status is >= 1 and <= 5 ? (CommercialStatus)status : null, docType, branchId, null, limit ?? 50, offset ?? 0), http.RequestAborted),
            v => new { documents = v.Select(Body) }, null);
    private static Task<IResult> Get(Guid id, HttpContext http, AccessService access, CommercialDocumentManagementService svc) =>
        Run(http, access, false, (u, c, _) => svc.GetAsync(u, c, id, http.RequestAborted), Body, d => d.Revision);
    private static Task<IResult> Create(CommercialDocumentInput? body, HttpContext http, AccessService access, CommercialDocumentManagementService svc) =>
        Run(http, access, false, (u, c, _) => svc.CreateAsync(u, c, body, http.RequestAborted), Body, d => d.Revision);
    private static Task<IResult> Update(Guid id, CommercialDocumentInput? body, HttpContext http, AccessService access, CommercialDocumentManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.UpdateAsync(u, c, id, rev, body, http.RequestAborted), Body, d => d.Revision);
    private static Task<IResult> Submit(Guid id, HttpContext http, AccessService access, CommercialDocumentManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.SubmitAsync(u, c, id, rev, http.RequestAborted), Body, d => d.Revision);
    private static Task<IResult> Withdraw(Guid id, HttpContext http, AccessService access, CommercialDocumentManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.WithdrawAsync(u, c, id, rev, http.RequestAborted), Body, d => d.Revision);
    private static Task<IResult> Approve(Guid id, HttpContext http, AccessService access, CommercialDocumentManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.ApproveAsync(u, c, id, rev, http.RequestAborted), Body, d => d.Revision);
    private static Task<IResult> Reject(Guid id, CancelBody? body, HttpContext http, AccessService access, CommercialDocumentManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.RejectAsync(u, c, id, rev, body?.Reason, http.RequestAborted), Body, d => d.Revision);
    private static Task<IResult> Cancel(Guid id, CancelBody? body, HttpContext http, AccessService access, CommercialDocumentManagementService svc) =>
        Run(http, access, true, (u, c, rev) => svc.CancelAsync(u, c, id, rev, body?.Reason, http.RequestAborted), Body, d => d.Revision);
}
