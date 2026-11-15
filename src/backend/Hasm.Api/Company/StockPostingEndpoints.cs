using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class StockPostingSetup
{
    public static IServiceCollection AddHasmStockPosting(this IServiceCollection services)
    {
        services.AddSingleton(sp => new InventoryCostingService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<CompanyInventoryService>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new StockPostingService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<InventoryCostingService>(), sp.GetRequiredService<JournalService>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new StockPostingManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<StockDocumentService>(), sp.GetRequiredService<StockPostingService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/stock-documents/{id}/posting: the explicit, authorized posting of an APPROVED stock document (Phase 3.7B). The company comes ONLY from the server-side session; the call needs the warehouse-scoped
/// <c>company.stockdocs.post</c> permission on every involved warehouse; POST uses <c>If-Match</c> with the document revision (428 missing, 412 stale) and is idempotent (a repeat returns the original posting).
/// One transaction moves the stock, values it, creates the balanced draft journal entry and records the posting; any failure changes nothing. Both clients use exactly these endpoints.
/// </summary>
public static class StockPostingEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 128 * 1024; }

    public sealed record PostLineBody(int? LineNo, decimal? Amount);
    public sealed record PostBody(List<PostLineBody>? Lines);

    private static object Body(StockPostingView p) => new
    {
        id = p.Id, documentId = p.DocumentId, docType = p.DocType, effectiveDate = p.EffectiveDate, postedAtUtc = p.PostedAtUtc, postedBy = p.PostedBy, costOperationId = p.CostOperationId,
        accountingVersionId = p.AccountingVersionId, journalEntryId = p.JournalEntryId, totalValue = p.TotalValue, accountingState = p.AccountingState, replayed = p.Replayed,
        lines = p.Lines.Select(l => new { lineNo = l.LineNo, itemId = l.ItemId, warehouseId = l.WarehouseId, baseQuantity = l.BaseQuantity, amount = l.Amount }),
    };

    public static void MapStockPostingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/stock-documents/{id:guid}/posting").RequireAuthorization();
        g.MapGet("", Get);
        g.MapPost("", Post).WithMetadata(new BodyLimit());
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, StockPostingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted);
        if (r.Status == ConfigStatus.Ok && r.Value is null) return CompanyConfigurationEndpoints.Error(404, "not_posted");
        return CompanyConfigurationEndpoints.Map(r, p => Body(p!));
    }

    private static async Task<IResult> Post(Guid id, PostBody? body, HttpContext http, AccessService access, StockPostingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        var input = new StockPostInput(body?.Lines?.Select(l => new StockPostLineInput(l.LineNo, l.Amount)).ToList());
        return CompanyConfigurationEndpoints.Map(await svc.PostAsync(actor!.UserId, actor.CompanyId, id, expected, input, http.RequestAborted), Body);
    }
}
