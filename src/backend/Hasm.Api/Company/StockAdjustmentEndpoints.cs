using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class StockAdjustmentSetup
{
    public static IServiceCollection AddHasmStockAdjustments(this IServiceCollection services)
    {
        services.AddSingleton(sp => new StockAdjustmentService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<InventoryCostingService>(), sp.GetRequiredService<JournalService>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("Hasm.Numbering")));
        services.AddSingleton(sp => new StockAdjustmentManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<StocktakeService>(), sp.GetRequiredService<StockAdjustmentService>()));
        return services;
    }
}

/// <summary>
/// Stock adjustments of approved stocktakes (Phase 3.8B-2). <c>POST /api/v1/company/stocktakes/{id}/adjustment</c> generates (or returns) the one adjustment of a stocktake (<c>If-Match</c> = the stocktake revision);
/// <c>GET /api/v1/company/stock-adjustments/{id}</c>, <c>POST .../{id}/posting</c> (amounts of the surplus lines; idempotent) and <c>POST .../{id}/reversal</c> (effective date and reason; once).
/// The company comes ONLY from the server-side session, every call is authorized in <see cref="StockAdjustmentManagementService"/> (warehouse-scoped), responses are <c>no-store</c>, state changes pass the anti-CSRF
/// middleware. An adjustment is immutable, so posting and reversal are fenced by their own uniqueness and the company stock lock instead of a revision. Both clients use exactly these endpoints.
/// </summary>
public static class StockAdjustmentEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 512 * 1024; }

    public static void MapStockAdjustmentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/company/stocktakes/{id:guid}/adjustment", Generate).RequireAuthorization().WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
        var g = app.MapGroup("/api/v1/company/stock-adjustments").RequireAuthorization();
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/{id:guid}/posting", Post).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/reversal", Reverse).WithMetadata(Hasm.Api.Hosting.RequestLimits.Action);
    }

    internal static object Body(StockAdjustmentView a) => new
    {
        id = a.Id, stocktakeId = a.StocktakeId, stocktakeNumber = a.StocktakeNumber, branchId = a.BranchId, warehouseId = a.WarehouseId, adjustmentDate = a.AdjustmentDate, number = a.Number, fiscalYearId = a.FiscalYearId,
        createdBy = a.CreatedBy, createdAtUtc = a.CreatedAtUtc, replayed = a.Replayed,
        lines = a.Lines.Select(l => new
        {
            lineNo = l.LineNo, itemId = l.ItemId, itemCode = l.ItemCode, itemNameAr = l.ItemNameAr, itemNameEn = l.ItemNameEn, unitId = l.UnitId, unitCode = l.UnitCode, direction = l.Direction, quantity = l.Quantity,
            countedQuantity = l.CountedQuantity, bookQuantity = l.BookQuantity, reason = l.Reason, amount = l.Amount,
        }),
        posting = a.Posting is not { } p ? null : new
        {
            id = p.Id, effectiveDate = p.EffectiveDate, postedAtUtc = p.PostedAtUtc, postedBy = p.PostedBy, issueOperationId = p.IssueOperationId, receiptOperationId = p.ReceiptOperationId, accountingVersionId = p.AccountingVersionId,
            journalEntryId = p.JournalEntryId, journalStatus = p.JournalStatus, totalValue = p.TotalValue, accountingState = p.AccountingState,
            reversal = p.Reversal is not { } r ? null : new { id = r.Id, issueOperationId = r.IssueOperationId, receiptOperationId = r.ReceiptOperationId, journalAction = r.JournalAction, journalEntryId = r.JournalEntryId, effectiveDate = r.EffectiveDate, reversedAtUtc = r.ReversedAtUtc, reversedBy = r.ReversedBy, reason = r.Reason },
        },
    };

    private static async Task<IResult> Generate(Guid id, HttpContext http, AccessService access, StockAdjustmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.GenerateAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted), Body);
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, StockAdjustmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body);
    }

    private static async Task<IResult> Post(Guid id, StockPostingEndpoints.PostBody? body, HttpContext http, AccessService access, StockAdjustmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var input = new StockPostInput(body?.Lines?.Select(l => new StockPostLineInput(l.LineNo, l.Amount)).ToList());
        return CompanyConfigurationEndpoints.Map(await svc.PostAsync(actor!.UserId, actor.CompanyId, id, input, http.RequestAborted), Body);
    }

    private static async Task<IResult> Reverse(Guid id, StockPostingEndpoints.ReverseBody? body, HttpContext http, AccessService access, StockAdjustmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ReverseAsync(actor!.UserId, actor.CompanyId, id, new StockReverseInput(body?.EffectiveDate, body?.Reason), http.RequestAborted), Body);
    }
}
