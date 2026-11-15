using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class StockDocumentSetup
{
    public static IServiceCollection AddHasmStockDocuments(this IServiceCollection services)
    {
        services.AddSingleton(sp => new StockDocumentService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new StockDocumentManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<StockDocumentService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/stock-documents: stock receipts, issues and transfers up to approval (Phase 3.5). The company comes ONLY from the server-side session, every call is authorized inside
/// <see cref="StockDocumentManagementService"/> (warehouse-scoped RBAC), responses are <c>no-store</c>, state-changing requests pass the anti-CSRF middleware and use <c>If-Match</c> with the document revision
/// (428 missing, 412 stale, so a repeated or concurrent request never acts twice). Both clients use exactly these endpoints. This API changes no stock quantity and creates no accounting entry.
/// </summary>
public static class StockDocumentEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 512 * 1024; }
    public sealed record NoteBody(string? Note);
    public sealed record ReasonBody(string? Reason);

    public static void MapStockDocumentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/stock-documents").RequireAuthorization();
        g.MapGet("/capabilities", Capabilities);
        g.MapGet("/warehouses", Warehouses);
        g.MapGet("/items", Items);
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/submit", (Guid id, HttpContext h, AccessService a, StockDocumentManagementService s) => Act(id, h, a, (u, c, rev) => s.SubmitAsync(u, c, id, rev, h.RequestAborted)));
        g.MapPost("/{id:guid}/withdraw", (Guid id, HttpContext h, AccessService a, StockDocumentManagementService s) => Act(id, h, a, (u, c, rev) => s.WithdrawAsync(u, c, id, rev, h.RequestAborted)));
        g.MapPost("/{id:guid}/approve", (Guid id, HttpContext h, AccessService a, StockDocumentManagementService s) => Act(id, h, a, (u, c, rev) => s.ApproveAsync(u, c, id, rev, h.RequestAborted)));
        g.MapPost("/{id:guid}/reject", (Guid id, NoteBody? body, HttpContext h, AccessService a, StockDocumentManagementService s) => Act(id, h, a, (u, c, rev) => s.RejectAsync(u, c, id, rev, body?.Note, h.RequestAborted)));
        g.MapPost("/{id:guid}/cancel", (Guid id, ReasonBody? body, HttpContext h, AccessService a, StockDocumentManagementService s) => Act(id, h, a, (u, c, rev) => s.CancelAsync(u, c, id, rev, body?.Reason, h.RequestAborted)));
    }

    private static object Body(StockDocumentView d) => new
    {
        id = d.Id, docType = d.DocType, status = d.Status, branchId = d.BranchId, sourceWarehouseId = d.SourceWarehouseId, targetWarehouseId = d.TargetWarehouseId, documentDate = d.DocumentDate,
        reference = d.Reference, notes = d.Notes, number = d.Number, fiscalYearId = d.FiscalYearId, createdBy = d.CreatedBy, createdAtUtc = d.CreatedAtUtc, submittedBy = d.SubmittedBy, submittedAtUtc = d.SubmittedAtUtc,
        decidedBy = d.DecidedBy, decidedAtUtc = d.DecidedAtUtc, decisionNote = d.DecisionNote, cancelledBy = d.CancelledBy, cancelledAtUtc = d.CancelledAtUtc, cancelReason = d.CancelReason, revision = d.Revision, updatedAtUtc = d.UpdatedAtUtc,
        lines = d.Lines.Select(l => new { lineNo = l.LineNo, itemId = l.ItemId, itemCode = l.ItemCode, itemNameAr = l.ItemNameAr, itemNameEn = l.ItemNameEn, unitId = l.UnitId, unitCode = l.UnitCode, quantity = l.Quantity,
            factor = l.Factor, baseQuantity = l.BaseQuantity, baseUnitCode = l.BaseUnitCode, notes = l.Notes }),
        events = d.Events.Select(e => new { id = e.Id, kind = e.Kind, fromStatus = e.FromStatus, toStatus = e.ToStatus, actorUserId = e.ActorUserId, atUtc = e.AtUtc, note = e.Note }),
    };

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

    private static async Task<IResult> Capabilities(HttpContext http, AccessService access, StockDocumentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var c = await svc.CapabilitiesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { view = c.View, create = c.Create, update = c.Update, submit = c.Submit, approve = c.Approve, cancel = c.Cancel, post = c.Post });
    }

    private static async Task<IResult> Warehouses(HttpContext http, AccessService access, StockDocumentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.WarehousesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return r.IsOk ? Results.Json(new { warehouses = r.Value!.Select(w => new { id = w.Id, branchId = w.BranchId, code = w.Code, nameAr = w.NameAr, nameEn = w.NameEn, isActive = w.IsActive }) }) : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> Items(HttpContext http, AccessService access, StockDocumentManagementService svc, string? search)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.SearchItemsAsync(actor!.UserId, actor.CompanyId, search, http.RequestAborted);
        return r.IsOk
            ? Results.Json(new { items = r.Value!.Select(i => new { id = i.Id, code = i.Code, nameAr = i.NameAr, nameEn = i.NameEn, units = i.Units.Select(u => new { id = u.Id, code = u.Code, nameAr = u.NameAr, nameEn = u.NameEn, decimals = u.Decimals, factor = u.Factor, isBase = u.IsBase }) }) })
            : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> List(HttpContext http, AccessService access, StockDocumentManagementService svc, int? type, int? status, Guid? warehouseId, string? from, string? to, string? search, int? offset, int? limit)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListAsync(actor!.UserId, actor.CompanyId, new StockDocumentFilter(type, status, warehouseId, from, to, search, null, offset ?? 0, limit ?? 50), http.RequestAborted);
        return r.IsOk
            ? Results.Json(new
            {
                total = r.Value!.Total,
                documents = r.Value.Items.Select(d => new { id = d.Id, docType = d.DocType, status = d.Status, number = d.Number, documentDate = d.DocumentDate, branchId = d.BranchId, sourceWarehouseId = d.SourceWarehouseId,
                    targetWarehouseId = d.TargetWarehouseId, reference = d.Reference, lineCount = d.LineCount, revision = d.Revision, createdAtUtc = d.CreatedAtUtc, updatedAtUtc = d.UpdatedAtUtc }),
            })
            : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, StockDocumentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Create(StockDocumentInput? body, HttpContext http, AccessService access, StockDocumentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/stock-documents/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> Update(Guid id, StockDocumentInput? body, HttpContext http, AccessService access, StockDocumentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.UpdateAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Act(Guid id, HttpContext http, AccessService access, Func<Guid, Guid, long?, Task<ConfigResult<StockDocumentView>>> run)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await run(actor!.UserId, actor.CompanyId, expected), Body, v => v.Revision);
    }
}
