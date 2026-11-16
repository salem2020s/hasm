using System.Globalization;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using IA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingVersion;
using IG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingGroup;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using A = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;

namespace Hasm.Infrastructure.Inventory;

/// <summary>Quantity and value of one item in one warehouse, from the costing ledger. The average cost is derived (value / quantity) and is for display only.</summary>
public sealed record StockBalanceRow(Guid ItemId, string ItemCode, string? ItemNameAr, string? ItemNameEn, Guid GroupId, Guid WarehouseId, string WarehouseCode, string? WarehouseNameAr, string? WarehouseNameEn,
    string BaseUnitCode, int Method, decimal Quantity, decimal Value, decimal AverageUnitCost);

public sealed record StockBalanceFilter(Guid? ItemId, Guid? GroupId, Guid? WarehouseId, string? Search, DateOnly? AsOf, bool IncludeZero, IReadOnlyCollection<Guid> VisibleWarehouses, int Offset, int Limit);

/// <summary><see cref="TotalValue"/> sums the value of ALL rows that match the filter (not only the page); it never includes a warehouse the caller may not see.</summary>
public sealed record StockBalancePage(IReadOnlyList<StockBalanceRow> Items, long Total, decimal TotalValue, string? AsOf);

public sealed record StockLedgerRow(long Seq, string EffectiveDate, int Kind, int Direction, decimal Quantity, decimal Amount, decimal QuantityAfter, decimal ValueAfter, string? SourceType, Guid? SourceId, string? SourceNumber);

/// <summary>Opening + movements = closing for the chosen range; every line carries its source document for drill-down.</summary>
public sealed record StockLedgerPage(Guid ItemId, Guid WarehouseId, decimal OpeningQuantity, decimal OpeningValue, IReadOnlyList<StockLedgerRow> Rows, long Total, decimal ClosingQuantity, decimal ClosingValue);

public sealed record StockItemChoice(Guid Id, string Code, string? NameAr, string? NameEn);

public sealed record StockReconciliationAccount(Guid AccountId, string Code, string? NameAr, string? NameEn, decimal LedgerBalance);

/// <summary>Costing value versus the inventory accounts of the General Ledger up to the date. A difference is exactly a movement whose journal entry is not posted yet, a reversal entry not posted, or a ledger balance the costing ledger does not know.</summary>
public sealed record StockReconciliation(string AsOf, decimal CostingValue, decimal LedgerValue, decimal Difference, IReadOnlyList<StockReconciliationAccount> Accounts);

/// <summary>
/// Read-only stock balances and item ledger (Phase 3.8A) over the costing ledger. <b>No authorization here by design:</b> <see cref="StockBalanceManagementService"/> wraps it and passes the warehouses the caller may see;
/// the restriction is applied INSIDE every query (before paging, totals and the reconciliation), so a caller never sees, counts or sums a warehouse outside their authorization. Nothing here writes.
/// Quantities and values are exact decimals read from the append-only costing entries; as-of dates use the effective date of each entry, never today's stock.
/// </summary>
public sealed class StockBalanceService(CompanyDatabaseResolver resolver)
{
    private static NpgsqlParameter Ids(string name, IEnumerable<Guid> ids) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.Distinct().ToArray() };
    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<StockBalancePage> BalancesAsync(Guid companyId, StockBalanceFilter f, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var where = new List<string> { $"e.{CE.WarehouseId} = ANY(@vis)" };
        await using var cmd = new NpgsqlCommand { Connection = conn };
        cmd.Parameters.Add(Ids("vis", f.VisibleWarehouses));
        if (f.AsOf is { } asOf) { where.Add($"e.{CE.EffectiveDate} <= @asof"); cmd.Parameters.Add(new NpgsqlParameter("asof", NpgsqlDbType.Date) { Value = asOf.ToDateTime(TimeOnly.MinValue) }); }
        if (f.ItemId is { } item) { where.Add($"e.{CE.ItemId} = @item"); cmd.Parameters.AddWithValue("item", item); }
        if (f.WarehouseId is { } wh) { where.Add($"e.{CE.WarehouseId} = @wh"); cmd.Parameters.AddWithValue("wh", wh); }
        if (f.GroupId is { } g) { where.Add($"i.{I.GroupId} = @grp"); cmd.Parameters.AddWithValue("grp", g); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            where.Add($"(i.{I.Code} ILIKE @q OR i.{I.NameAr} ILIKE @q OR i.{I.NameEn} ILIKE @q)");
            cmd.Parameters.AddWithValue("q", "%" + f.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        var having = f.IncludeZero ? "" : " HAVING SUM(e." + CE.Direction + " * e." + CE.Quantity + ") <> 0 OR SUM(e." + CE.Direction + " * e." + CE.Amount + ") <> 0";
        var baseSql =
            $"SELECT e.{CE.ItemId} AS item, e.{CE.WarehouseId} AS wh, SUM(e.{CE.Direction} * e.{CE.Quantity}) AS qty, SUM(e.{CE.Direction} * e.{CE.Amount}) AS val " +
            $"FROM {CE.Table} e JOIN {I.Table} i ON i.{I.Id} = e.{CE.ItemId} WHERE {string.Join(" AND ", where)} GROUP BY e.{CE.ItemId}, e.{CE.WarehouseId}{having}";
        cmd.CommandText = $"SELECT count(*), COALESCE(SUM(val), 0) FROM ({baseSql}) b";
        long total; decimal totalValue;
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { await r.ReadAsync(ct); total = r.GetInt64(0); totalValue = r.GetDecimal(1); }
        cmd.Parameters.AddWithValue("lim", Math.Clamp(f.Limit, 1, 200));
        cmd.Parameters.AddWithValue("off", Math.Max(0, f.Offset));
        cmd.CommandText =
            $"SELECT b.item, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, i.{I.GroupId}, b.wh, w.{W.Code}, w.{W.NameAr}, w.{W.NameEn}, bu.{U.Code}, COALESCE(p.{CP.Method}, 0), b.qty, b.val " +
            $"FROM ({baseSql}) b JOIN {I.Table} i ON i.{I.Id} = b.item JOIN {W.Table} w ON w.{W.Id} = b.wh JOIN {U.Table} bu ON bu.{U.Id} = i.{I.BaseUnitId} " +
            $"LEFT JOIN {CP.Table} p ON p.{CP.ItemId} = b.item AND p.{CP.WarehouseId} = b.wh ORDER BY i.{I.Code}, w.{W.Code}, b.item, b.wh LIMIT @lim OFFSET @off";
        var rows = new List<StockBalanceRow>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var qty = r.GetDecimal(11); var val = r.GetDecimal(12);
                rows.Add(new StockBalanceRow(r.GetGuid(0), r.GetString(1), S(r, 2), S(r, 3), r.GetGuid(4), r.GetGuid(5), r.GetString(6), S(r, 7), S(r, 8), r.GetString(9), r.GetInt16(10), qty, val, qty == 0 ? 0 : decimal.Round(val / qty, 6, MidpointRounding.ToEven)));
            }
        return new StockBalancePage(rows, total, totalValue, f.AsOf is { } d ? Iso(d) : null);
    }

    /// <summary>The movements of one item in one warehouse in a date range with the opening and closing balance. The caller has already been authorized for that warehouse.</summary>
    public async Task<StockLedgerPage> LedgerAsync(Guid companyId, Guid itemId, Guid warehouseId, DateOnly? from, DateOnly? to, int offset, int limit, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        decimal openQ = 0, openV = 0, closeQ = 0, closeV = 0;
        await using (var q = new NpgsqlCommand(
            $"SELECT COALESCE(SUM(CASE WHEN @from IS NOT NULL AND {CE.EffectiveDate} < @from THEN {CE.Direction} * {CE.Quantity} END), 0), COALESCE(SUM(CASE WHEN @from IS NOT NULL AND {CE.EffectiveDate} < @from THEN {CE.Direction} * {CE.Amount} END), 0), " +
            $"COALESCE(SUM(CASE WHEN @to IS NULL OR {CE.EffectiveDate} <= @to THEN {CE.Direction} * {CE.Quantity} END), 0), COALESCE(SUM(CASE WHEN @to IS NULL OR {CE.EffectiveDate} <= @to THEN {CE.Direction} * {CE.Amount} END), 0) " +
            $"FROM {CE.Table} WHERE {CE.ItemId} = @i AND {CE.WarehouseId} = @w", conn))
        {
            q.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from is { } f ? f.ToDateTime(TimeOnly.MinValue) : DBNull.Value });
            q.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = to is { } t ? t.ToDateTime(TimeOnly.MinValue) : DBNull.Value });
            q.Parameters.AddWithValue("i", itemId); q.Parameters.AddWithValue("w", warehouseId);
            await using var r = await q.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
            openQ = r.GetDecimal(0); openV = r.GetDecimal(1); closeQ = r.GetDecimal(2); closeV = r.GetDecimal(3);
        }
        const string range = " AND (@from IS NULL OR e.{0} >= @from) AND (@to IS NULL OR e.{0} <= @to)";
        var filter = $"e.{CE.ItemId} = @i AND e.{CE.WarehouseId} = @w" + string.Format(range, CE.EffectiveDate);
        long total;
        await using (var c = new NpgsqlCommand($"SELECT count(*) FROM {CE.Table} e WHERE {filter}", conn))
        {
            c.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from is { } f ? f.ToDateTime(TimeOnly.MinValue) : DBNull.Value });
            c.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = to is { } t ? t.ToDateTime(TimeOnly.MinValue) : DBNull.Value });
            c.Parameters.AddWithValue("i", itemId); c.Parameters.AddWithValue("w", warehouseId);
            total = Convert.ToInt64(await c.ExecuteScalarAsync(ct));
        }
        var rows = new List<StockLedgerRow>();
        await using (var q = new NpgsqlCommand(
            $"SELECT e.{CE.Seq}, e.{CE.EffectiveDate}, e.{CE.Kind}, e.{CE.Direction}, e.{CE.Quantity}, e.{CE.Amount}, e.{CE.QuantityAfter}, e.{CE.ValueAfter}, o.{CO.SourceType}, o.{CO.SourceId}, d.{SD.Number} " +
            $"FROM {CE.Table} e JOIN {CO.Table} o ON o.{CO.Id} = e.{CE.OperationId} LEFT JOIN {SD.Table} d ON d.{SD.Id} = o.{CO.SourceId} AND o.{CO.SourceType} IN ('stock_document', 'stock_document_reversal') " +
            $"WHERE {filter} ORDER BY e.{CE.EffectiveDate}, e.{CE.Seq} LIMIT @lim OFFSET @off", conn))
        {
            q.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from is { } f ? f.ToDateTime(TimeOnly.MinValue) : DBNull.Value });
            q.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = to is { } t ? t.ToDateTime(TimeOnly.MinValue) : DBNull.Value });
            q.Parameters.AddWithValue("i", itemId); q.Parameters.AddWithValue("w", warehouseId);
            q.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 500)); q.Parameters.AddWithValue("off", Math.Max(0, offset));
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new StockLedgerRow(r.GetInt64(0), Iso(r.GetFieldValue<DateOnly>(1)), r.GetInt16(2), r.GetInt16(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), S(r, 8), r.IsDBNull(9) ? null : r.GetGuid(9), S(r, 10)));
        }
        return new StockLedgerPage(itemId, warehouseId, openQ, openV, rows, total, closeQ, closeV);
    }

    /// <summary>Stockable items by code or name, for the pickers of the balance screens (the authorization is by the screens' own permission).</summary>
    public async Task<IReadOnlyList<StockItemChoice>> SearchItemsAsync(Guid companyId, string? search, IReadOnlyCollection<Guid> visibleWarehouses, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        // only items that have costing entries in a warehouse the caller may see: the picker never lists the rest of the item catalog
        await using var q = new NpgsqlCommand($"SELECT {I.Id}, {I.Code}, {I.NameAr}, {I.NameEn} FROM {I.Table} WHERE {I.ItemType} = 1 AND EXISTS (SELECT 1 FROM {CE.Table} e WHERE e.{CE.ItemId} = {I.Table}.{I.Id} AND e.{CE.WarehouseId} = ANY(@vis)) AND (@q = '' OR {I.Code} ILIKE @q2 OR {I.NameAr} ILIKE @q2 OR {I.NameEn} ILIKE @q2) ORDER BY {I.Code} LIMIT 30", conn);
        q.Parameters.Add(Ids("vis", visibleWarehouses));
        var term = (search ?? "").Trim();
        q.Parameters.AddWithValue("q", term); q.Parameters.AddWithValue("q2", "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        var list = new List<StockItemChoice>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new StockItemChoice(r.GetGuid(0), r.GetString(1), S(r, 2), S(r, 3)));
        return list;
    }

    /// <summary>The company-wide comparison of the costing ledger with the inventory accounts of the General Ledger up to <paramref name="asOf"/> (the caller must be authorized for every warehouse).</summary>
    public async Task<StockReconciliation> ReconcileAsync(Guid companyId, DateOnly asOf, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var date = new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = asOf.ToDateTime(TimeOnly.MinValue) };
        decimal costing;
        await using (var q = new NpgsqlCommand($"SELECT COALESCE(SUM({CE.Direction} * {CE.Amount}), 0) FROM {CE.Table} WHERE {CE.EffectiveDate} <= @d", conn))
        { q.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = date.Value }); costing = (decimal)(await q.ExecuteScalarAsync(ct))!; }
        var accounts = new List<StockReconciliationAccount>();
        await using (var q = new NpgsqlCommand(
            $"SELECT a.{A.Id}, a.{A.Code}, a.{A.NameAr}, a.{A.NameEn}, COALESCE((SELECT SUM(l.{JL.BaseDebit} - l.{JL.BaseCredit}) FROM {JL.Table} l JOIN {JE.Table} j ON j.{JE.Id} = l.{JL.Entry} WHERE l.{JL.Account} = a.{A.Id} AND j.{JE.Status} IN (4, 5) AND j.{JE.Date} <= @d), 0) " +
            $"FROM {A.Table} a WHERE a.{A.Id} IN (SELECT {IA.DefaultAccountId} FROM {IA.Table} WHERE {IA.DefaultAccountId} IS NOT NULL UNION SELECT {IG.AccountId} FROM {IG.Table}) ORDER BY a.{A.Code}", conn))
        {
            q.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = date.Value });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) accounts.Add(new StockReconciliationAccount(r.GetGuid(0), r.GetString(1), S(r, 2), S(r, 3), r.GetDecimal(4)));
        }
        var ledger = accounts.Sum(a => a.LedgerBalance);
        return new StockReconciliation(Iso(asOf), costing, ledger, costing - ledger, accounts);
    }
}

/// <summary>Authorized entry point: <c>company.stockbalance.view</c> is warehouse-scoped; the warehouses where the caller holds it are applied inside every query. The reconciliation is company-wide and needs the permission on EVERY warehouse.</summary>
public sealed class StockBalanceManagementService(AccessService access, StockDocumentService documents, StockBalanceService balances)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    public async Task<(IReadOnlyList<StockWarehouseRef> Visible, IReadOnlyList<StockWarehouseRef> All)> VisibleAsync(Guid user, Guid company, CancellationToken ct)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        var all = await documents.ListWarehousesAsync(company, ct);
        return ([.. all.Where(w => eff.Has(PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(w.Id)))], all);
    }

    public async Task<ConfigResult<IReadOnlyList<StockWarehouseRef>>> WarehousesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var (visible, _) = await VisibleAsync(user, company, ct);
        return visible.Count == 0 ? Denied<IReadOnlyList<StockWarehouseRef>>() : ConfigResult<IReadOnlyList<StockWarehouseRef>>.Ok(visible);
    }

    public async Task<ConfigResult<StockBalancePage>> BalancesAsync(Guid user, Guid company, StockBalanceFilter filter, CancellationToken ct = default)
    {
        var (visible, _) = await VisibleAsync(user, company, ct);
        if (visible.Count == 0) return Denied<StockBalancePage>();
        // a requested warehouse the caller cannot see is not disclosed: the result is simply restricted to what they may see
        return ConfigResult<StockBalancePage>.Ok(await balances.BalancesAsync(company, filter with { VisibleWarehouses = [.. visible.Select(w => w.Id)] }, ct));
    }

    public async Task<ConfigResult<StockLedgerPage>> LedgerAsync(Guid user, Guid company, Guid itemId, Guid warehouseId, DateOnly? from, DateOnly? to, int offset, int limit, CancellationToken ct = default)
    {
        var (visible, _) = await VisibleAsync(user, company, ct);
        if (!visible.Any(w => w.Id == warehouseId)) return Denied<StockLedgerPage>();
        return ConfigResult<StockLedgerPage>.Ok(await balances.LedgerAsync(company, itemId, warehouseId, from, to, offset, limit, ct));
    }

    public async Task<ConfigResult<IReadOnlyList<StockItemChoice>>> ItemsAsync(Guid user, Guid company, string? search, CancellationToken ct = default)
    {
        var (visible, _) = await VisibleAsync(user, company, ct);
        return visible.Count == 0 ? Denied<IReadOnlyList<StockItemChoice>>() : ConfigResult<IReadOnlyList<StockItemChoice>>.Ok(await balances.SearchItemsAsync(company, search, visible.Select(w => w.Id).ToList(), ct));
    }

    public async Task<ConfigResult<StockReconciliation>> ReconcileAsync(Guid user, Guid company, DateOnly asOf, CancellationToken ct = default)
    {
        var (visible, all) = await VisibleAsync(user, company, ct);
        if (all.Count == 0 || visible.Count != all.Count) return Denied<StockReconciliation>();
        return ConfigResult<StockReconciliation>.Ok(await balances.ReconcileAsync(company, asOf, ct));
    }
}
