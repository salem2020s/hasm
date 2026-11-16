using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;

namespace Hasm.Api.Company;

public static class StockBalanceSetup
{
    public static IServiceCollection AddHasmStockBalances(this IServiceCollection services)
    {
        services.AddSingleton(sp => new StockBalanceService(sp.GetRequiredService<CompanyDatabaseResolver>()));
        services.AddSingleton(sp => new StockBalanceManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<StockDocumentService>(), sp.GetRequiredService<StockBalanceService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/stock-balances: read-only quantity and value on hand per item and warehouse, the item ledger and the reconciliation with the General Ledger (Phase 3.8A). The company comes ONLY from the server-side session;
/// <c>company.stockbalance.view</c> is warehouse-scoped and applied inside every query before paging and totals; responses are <c>no-store</c>. Both clients use exactly these endpoints.
/// </summary>
public static class StockBalanceEndpoints
{
    public static void MapStockBalanceEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/stock-balances").RequireAuthorization();
        g.MapGet("", Balances);
        g.MapGet("/warehouses", Warehouses);
        g.MapGet("/items", Items);
        g.MapGet("/ledger", Ledger);
        g.MapGet("/reconciliation", Reconciliation);
    }

    private static DateOnly? Date(string? v) => DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    private static Guid? Id(string? v) => Guid.TryParse(v, out var g) && g != Guid.Empty ? g : null;
    private static IResult Bad(string field, string code) => Results.Json(new { error = "validation_failed", fields = new[] { new { field, code } } }, statusCode: 400);

    private static async Task<IResult> Warehouses(HttpContext http, AccessService access, StockBalanceManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.WarehousesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), w => new { warehouses = w.Select(x => new { id = x.Id, branchId = x.BranchId, code = x.Code, nameAr = x.NameAr, nameEn = x.NameEn, isActive = x.IsActive }) });
    }

    private static async Task<IResult> Items(string? search, HttpContext http, AccessService access, StockBalanceManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ItemsAsync(actor!.UserId, actor.CompanyId, search, http.RequestAborted), i => new { items = i.Select(x => new { id = x.Id, code = x.Code, nameAr = x.NameAr, nameEn = x.NameEn }) });
    }

    private static async Task<IResult> Balances(HttpContext http, AccessService access, StockBalanceManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var q = http.Request.Query;
        if (!string.IsNullOrEmpty(q["asOf"]) && Date(q["asOf"]) is null) return Bad("asOf", "invalid_date");
        _ = int.TryParse(q["offset"], out var offset); var limit = int.TryParse(q["limit"], out var l) ? l : 50;
        var filter = new StockBalanceFilter(Id(q["itemId"]), Id(q["groupId"]), Id(q["warehouseId"]), q["search"], Date(q["asOf"]), q["includeZero"] == "true", [], offset, limit);
        return CompanyConfigurationEndpoints.Map(await svc.BalancesAsync(actor!.UserId, actor.CompanyId, filter, http.RequestAborted),
            p => new { balances = p.Items.Select(r => new { itemId = r.ItemId, itemCode = r.ItemCode, itemNameAr = r.ItemNameAr, itemNameEn = r.ItemNameEn, groupId = r.GroupId, warehouseId = r.WarehouseId, warehouseCode = r.WarehouseCode, warehouseNameAr = r.WarehouseNameAr, warehouseNameEn = r.WarehouseNameEn, baseUnitCode = r.BaseUnitCode, method = r.Method, quantity = r.Quantity, value = r.Value, averageUnitCost = r.AverageUnitCost }), total = p.Total, totalValue = p.TotalValue, asOf = p.AsOf });
    }

    private static async Task<IResult> Ledger(HttpContext http, AccessService access, StockBalanceManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var q = http.Request.Query;
        if (Id(q["itemId"]) is not { } item) return Bad("itemId", "required");
        if (Id(q["warehouseId"]) is not { } wh) return Bad("warehouseId", "required");
        if (!string.IsNullOrEmpty(q["from"]) && Date(q["from"]) is null) return Bad("from", "invalid_date");
        if (!string.IsNullOrEmpty(q["to"]) && Date(q["to"]) is null) return Bad("to", "invalid_date");
        _ = int.TryParse(q["offset"], out var offset); var limit = int.TryParse(q["limit"], out var l) ? l : 100;
        return CompanyConfigurationEndpoints.Map(await svc.LedgerAsync(actor!.UserId, actor.CompanyId, item, wh, Date(q["from"]), Date(q["to"]), offset, limit, http.RequestAborted),
            p => new { itemId = p.ItemId, warehouseId = p.WarehouseId, openingQuantity = p.OpeningQuantity, openingValue = p.OpeningValue, closingQuantity = p.ClosingQuantity, closingValue = p.ClosingValue, total = p.Total,
                rows = p.Rows.Select(r => new { seq = r.Seq, effectiveDate = r.EffectiveDate, kind = r.Kind, direction = r.Direction, quantity = r.Quantity, amount = r.Amount, quantityAfter = r.QuantityAfter, valueAfter = r.ValueAfter, sourceType = r.SourceType, sourceId = r.SourceId, sourceNumber = r.SourceNumber }) });
    }

    private static async Task<IResult> Reconciliation(HttpContext http, AccessService access, StockBalanceManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var asOf = Date(http.Request.Query["asOf"]);
        if (asOf is null) return Bad("asOf", string.IsNullOrEmpty(http.Request.Query["asOf"]) ? "required" : "invalid_date");
        return CompanyConfigurationEndpoints.Map(await svc.ReconcileAsync(actor!.UserId, actor.CompanyId, asOf.Value, http.RequestAborted),
            r => new { asOf = r.AsOf, costingValue = r.CostingValue, ledgerValue = r.LedgerValue, difference = r.Difference, accounts = r.Accounts.Select(a => new { accountId = a.AccountId, code = a.Code, nameAr = a.NameAr, nameEn = a.NameEn, ledgerBalance = a.LedgerBalance }) });
    }
}
