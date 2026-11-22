using System.Collections.Immutable;
using System.Data;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;

namespace Hasm.Infrastructure.Costing;

internal sealed record CostFlowSnapshotLimits(int Warehouses = 10_000, int Groups = 100_000, int Entries = 200_000,
    int Components = 400_000, int DiscoveryRows = 400_000, int Commands = 20_000, TimeSpan? Deadline = null);

/// <summary>
/// Internal common-method ordinary operation-component snapshot. NO API, DI registration or production caller.
/// Whole-component live authorization precedes valuation reads; all company reads share one read-only repeatable
/// snapshot. Returned fingerprint is history evidence, never approval or execution authority.
/// </summary>
internal sealed partial class CostFlowSnapshotService
{
    private readonly CompanyDatabaseResolver resolver;
    private readonly AccessService access;
    private readonly CostFlowSnapshotLimits limits;
    private sealed class Denied : Exception;
    internal Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? SnapshotEstablished { get; set; }
    internal Func<CancellationToken, Task>? BeforeFinalAuthorization { get; set; }

    internal CostFlowSnapshotService(CompanyDatabaseResolver resolver, AccessService access, CostFlowSnapshotLimits? limits = null)
    {
        this.resolver = resolver; this.access = access; this.limits = limits ?? new();
        var l = this.limits;
        if (l.Warehouses is < 1 or > 10_000 || l.Groups is < 1 or > 100_000 || l.Entries is < 1 or > 200_000 ||
            l.Components is < 1 or > 400_000 || l.DiscoveryRows is < 1 or > 400_000 || l.Commands is < 1 or > 20_000 ||
            l.Deadline is { } d && (d <= TimeSpan.Zero || d > TimeSpan.FromSeconds(30))) throw new ArgumentOutOfRangeException(nameof(limits));
    }
    private Task<bool> Can(Guid user, Guid company, Guid warehouse, ReadCommandBudget quota, CancellationToken ct) =>
        access.IsAllowedAsync(user, company, PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(warehouse), quota.Reserve, ct);
    private static ConfigResult<CostFlowSnapshot> Fail(ConfigStatus status, string code) => ConfigResult<CostFlowSnapshot>.Fail(status, code);

    internal Task<ConfigResult<CostFlowSnapshot>> LoadAsync(Guid user, Guid company, Guid item, Guid warehouse, CancellationToken ct = default)
        => LoadAsync(user, company, item, ImmutableArray.Create(warehouse), ct);

    // A proposed operation can join previously disconnected components. All roots must be read
    // in ONE snapshot and under ONE set of budgets, never by merging separately loaded evidence.
    internal Task<ConfigResult<CostFlowSnapshot>> LoadAsync(Guid user, Guid company, Guid item,
        ImmutableArray<Guid> requested, CancellationToken ct = default)
        => ReadAuthorizedAsync(user, company, item, requested, static (snapshot, _) => snapshot, ct);

    private async Task<ConfigResult<T>> ReadAuthorizedAsync<T>(Guid user, Guid company, Guid item,
        ImmutableArray<Guid> requested, Func<CostFlowSnapshot, CancellationToken, T> project, CancellationToken ct)
        => await ReadAuthorizedItemsAsync(user, company, [new(item, requested)],
            (snapshots, token) => project(snapshots[0], token), ct);

    private async Task<ConfigResult<T>> ReadAuthorizedItemsAsync<T>(Guid user, Guid company,
        ImmutableArray<CostFlowItemRoots> requested, Func<ImmutableArray<CostFlowSnapshot>, CancellationToken, T> project, CancellationToken ct)
        => await RunBoundedReadAsync(user, company, async (budget, scopes, token) =>
        {
            if (!ValidItemRoots(requested)) return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invalid_impact_roots");
            foreach (var request in requested)
                foreach (var root in request.Warehouses)
                {
                    if (scopes.Add(root) && !await Can(user, company, root, budget.Sql, token)) throw new Denied();
                    budget.Position(request.ItemId, root);
                }
            var loaded = await ReadItemsAsync(company, requested, scopes, w => Can(user, company, w, budget.Sql, token), budget, token);
            if (!loaded.IsOk) return ConfigResult<T>.Fail(loaded.Status, loaded.Code!);
            token.ThrowIfCancellationRequested();
            return ConfigResult<T>.Ok(project(loaded.Value, token));
        }, null, ct);

    private async Task<ConfigResult<T>> RunBoundedReadAsync<T>(Guid user, Guid company,
        Func<Budget, HashSet<Guid>, CancellationToken, Task<ConfigResult<T>>> read,
        Func<Budget, CancellationToken, Task<bool>>? finalExtra, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limits.Deadline ?? TimeSpan.FromSeconds(30)); var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var quota = new ReadCommandBudget(limits.Commands, token);
        var budget = new Budget(limits, token, quota);
        var scopes = new HashSet<Guid>();
        ConfigResult<T> result;
        try
        {
            result = await read(budget, scopes, token);
        }
        catch (ReadCommandBudgetException e) { return BudgetFailure<T>(e, token); }
        catch (Denied) { result = ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden"); }
        catch (CostingException e) { result = ConfigResult<T>.Fail(ConfigStatus.Conflict, e.Code); }
        catch (Hasm.Infrastructure.Inventory.StockDocumentService.Abort e) { result = e.To<T>(); }
        if (BeforeFinalAuthorization is { } observe) await observe(token);
        // Includes error paths. Expired/cancelled work yields no diagnostic or partially authorized data.
        try
        {
            foreach (var scope in scopes)
                if (!await Can(user, company, scope, quota, token)) return ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
            if (finalExtra is not null && !await finalExtra(budget, token)) return ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
        }
        catch (ReadCommandBudgetException e) { return BudgetFailure<T>(e, token); }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static ConfigResult<T> BudgetFailure<T>(ReadCommandBudgetException error, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ConfigResult<T>.Fail(ConfigStatus.Conflict, error.Failure == ReadBudgetFailure.Exhausted
            ? "impact_history_too_large" : "impact_query_budget_unavailable");
    }

    private sealed class Budget(CostFlowSnapshotLimits limits, CancellationToken ct, ReadCommandBudget quota)
    {
        private int discoveryRows, entries, components;
        private readonly HashSet<(Guid Item, Guid Warehouse)> positions = [];
        private readonly HashSet<Guid> groups = [];
        internal ReadCommandBudget Sql => quota;
        internal int DiscoveryRemaining => limits.DiscoveryRows - discoveryRows;
        internal int ComponentRemaining => limits.Components - components;
        private void Take(ref int count, int maximum)
        {
            ct.ThrowIfCancellationRequested(); if (count == maximum) throw TooLarge(); count++;
        }
        internal void Discovery() => Take(ref discoveryRows, limits.DiscoveryRows);
        internal void Entry() => Take(ref entries, limits.Entries);
        internal void Component() => Take(ref components, limits.Components);
        internal void Position(Guid item, Guid warehouse)
        {
            ct.ThrowIfCancellationRequested();
            if (positions.Contains((item, warehouse))) return;
            if (positions.Count == limits.Warehouses) throw TooLarge();
            positions.Add((item, warehouse));
        }
        internal void Group(Guid id)
        {
            ct.ThrowIfCancellationRequested();
            if (groups.Contains(id)) return;
            if (groups.Count == limits.Groups) throw TooLarge();
            groups.Add(id);
        }
        internal NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            quota.Reserve(); return new NpgsqlCommand(sql, connection, transaction);
        }
    }

    private async Task<ConfigResult<CostFlowSnapshot>> ReadDiscoveredAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid company, Guid item, HashSet<Guid> scopes, Guid[] operationIds, Budget budget, CancellationToken ct)
    {
        CostMethod method;
        await using (var q = budget.Command($"SELECT {I.ItemType},{I.IsLot},{I.IsSerial},{I.HasExpiry},{CompanySchema.NegativeStockGuard.ResolvedMethodFunction}({I.Id}) FROM {I.Table} WHERE {I.Id}=@i", conn, tx))
        {
            q.Parameters.AddWithValue("i", item); await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail(ConfigStatus.NotFound, "item_not_found");
            if (r.GetInt16(0) != 1 || r.GetBoolean(1) || r.GetBoolean(2) || r.GetBoolean(3)) throw Unsupported();
            if (r.IsDBNull(4) || Convert.ToInt32(r.GetValue(4)) is not (1 or 2)) throw new CostingException("valuation_method_unresolved");
            method = (CostMethod)Convert.ToInt32(r.GetValue(4));
        }
        string currency; int decimals;
        await using (var q = budget.Command($"SELECT c.{CompanySchema.Currency.Code},c.{CompanySchema.Currency.MinorUnits} FROM {CompanySchema.CompanyCurrency.Table} cc JOIN {CompanySchema.Currency.Table} c ON c.{CompanySchema.Currency.Code}=cc.{CompanySchema.CompanyCurrency.Code} WHERE cc.{CompanySchema.CompanyCurrency.IsBase}", conn, tx))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new CostingException("base_currency_not_set");
            currency = r.GetString(0); decimals = Convert.ToInt32(r.GetValue(1));
            if (decimals is < 0 or > CostingMath.MaxMoneyDecimals) throw new CostingException("unsupported_currency_precision");
        }
        var warehouses = scopes.OrderBy(w => w.ToString("N"), StringComparer.Ordinal).ToArray();
        var data = await ReadData(conn, tx, budget, item, method, warehouses, operationIds, ct);
        var result = Reconcile(company, item, method, currency, decimals, data.Positions, data.Entries, data.Layers, data.Consumptions, ct);
        return ConfigResult<CostFlowSnapshot>.Ok(result);
    }

    private static string SelectedEntry(string e, string p) =>
        $"(({p}.{CP.ItemId}=@i AND {p}.{CP.WarehouseId}=ANY(@ws)) OR ({e}.{CE.ItemId}=@i AND {e}.{CE.WarehouseId}=ANY(@ws)))";
    private static string SelectedLayer(string l, string p) =>
        $"(({p}.{CP.ItemId}=@i AND {p}.{CP.WarehouseId}=ANY(@ws)) OR ({l}.{CL.ItemId}=@i AND {l}.{CL.WarehouseId}=ANY(@ws)) OR EXISTS(SELECT 1 FROM {CE.Table} re LEFT JOIN {CP.Table} rp ON rp.{CP.Id}=re.{CE.PositionId} WHERE re.{CE.Id}={l}.{CL.EntryId} AND {SelectedEntry("re", "rp")}))";

    // Identifier-only quarantine. Never filter edges through a VALID transfer/source/layer join.
    private async Task<Guid[]> Discover(NpgsqlConnection conn, NpgsqlTransaction tx, Budget budget, Guid item,
        HashSet<Guid> scopes, Func<Guid, Task<bool>> can, CancellationToken ct)
    {
        var warehouses = new Queue<Guid>(scopes);
        var pendingOperations = new Queue<Guid>(); var operations = new HashSet<Guid>();
        var pendingLayers = new Queue<Guid>(); var layers = new HashSet<Guid>();
        void Layer(Guid id)
        {
            if (layers.Contains(id)) return;
            if (layers.Count == limits.Components) throw TooLarge();
            layers.Add(id); pendingLayers.Enqueue(id);
        }
        async Task Reference(Guid id)
        {
            if (scopes.Contains(id)) return;
            budget.Position(item, id);
            if (!await can(id)) throw new Denied();
            if (scopes.Count == limits.Warehouses) throw TooLarge();
            scopes.Add(id); warehouses.Enqueue(id);
        }
        async Task Scan(string sql, Guid[] ws, Guid[] ops)
        {
            await using var q = budget.Command(sql + " LIMIT @limit", conn, tx);
            q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("ws", ws); q.Parameters.AddWithValue("ops", ops);
            q.Parameters.AddWithValue("limit", budget.DiscoveryRemaining + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                budget.Discovery();
                if (!r.IsDBNull(0))
                {
                    var op = r.GetGuid(0);
                    if (!operations.Contains(op))
                    {
                        if (operations.Count == limits.Groups) throw TooLarge();
                        budget.Group(op); operations.Add(op); pendingOperations.Enqueue(op);
                    }
                }
                for (var column = 1; column < r.FieldCount; column++)
                    if (!r.IsDBNull(column)) await Reference(r.GetGuid(column));
            }
        }
        Guid[] Batch(Queue<Guid> queue)
        {
            var batch = new List<Guid>(); while (batch.Count < 128 && queue.TryDequeue(out var id)) batch.Add(id); return batch.ToArray();
        }
        while (warehouses.Count > 0 || pendingOperations.Count > 0 || pendingLayers.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (warehouses.Count > 0)
            {
                var ws = Batch(warehouses);
                await Scan($"SELECT e.{CE.OperationId},e.{CE.WarehouseId},p.{CP.WarehouseId} FROM {CE.Table} e LEFT JOIN {CP.Table} p ON p.{CP.Id}=e.{CE.PositionId} WHERE {SelectedEntry("e", "p")}", ws, []);
                // Seed actual layer identifiers, including malformed foreign-item consumption targets.
                // A warehouse/item frontier alone cannot follow their transitive source lineage.
                await using (var q = budget.Command($"SELECT l.{CL.Id} FROM {CL.Table} l LEFT JOIN {CP.Table} p ON p.{CP.Id}=l.{CL.PositionId} WHERE {SelectedLayer("l", "p")} UNION ALL SELECT c.{CC.LayerId} FROM {CC.Table} c JOIN {CE.Table} e ON e.{CE.Id}=c.{CC.EntryId} LEFT JOIN {CP.Table} p ON p.{CP.Id}=e.{CE.PositionId} WHERE {SelectedEntry("e", "p")} LIMIT @limit", conn, tx))
                {
                    q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("ws", ws);
                    q.Parameters.AddWithValue("limit", budget.DiscoveryRemaining + 1);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) { budget.Discovery(); Layer(r.GetGuid(0)); }
                }
                var layerFrom = $" FROM {CL.Table} l LEFT JOIN {CP.Table} p ON p.{CP.Id}=l.{CL.PositionId} LEFT JOIN {CE.Table} e ON e.{CE.Id}=l.{CL.EntryId} LEFT JOIN {CP.Table} ep ON ep.{CP.Id}=e.{CE.PositionId} LEFT JOIN {CL.Table} s ON s.{CL.Id}=l.{CL.SourceLayerId} LEFT JOIN {CP.Table} sp ON sp.{CP.Id}=s.{CL.PositionId} LEFT JOIN {CE.Table} se ON se.{CE.Id}=s.{CL.EntryId} LEFT JOIN {CP.Table} sep ON sep.{CP.Id}=se.{CE.PositionId} WHERE {SelectedLayer("l", "p")}";
                await Scan($"SELECT e.{CE.OperationId},l.{CL.WarehouseId},p.{CP.WarehouseId},e.{CE.WarehouseId},ep.{CP.WarehouseId}" + layerFrom +
                    $" UNION ALL SELECT se.{CE.OperationId},s.{CL.WarehouseId},sp.{CP.WarehouseId},se.{CE.WarehouseId},sep.{CP.WarehouseId}" + layerFrom, ws, []);
                var consumptionFrom = $" FROM {CC.Table} c LEFT JOIN {CE.Table} e ON e.{CE.Id}=c.{CC.EntryId} LEFT JOIN {CP.Table} p ON p.{CP.Id}=e.{CE.PositionId} LEFT JOIN {CL.Table} l ON l.{CL.Id}=c.{CC.LayerId} LEFT JOIN {CP.Table} lp ON lp.{CP.Id}=l.{CL.PositionId} LEFT JOIN {CE.Table} le ON le.{CE.Id}=l.{CL.EntryId} LEFT JOIN {CP.Table} lep ON lep.{CP.Id}=le.{CE.PositionId} LEFT JOIN {CL.Table} s ON s.{CL.Id}=l.{CL.SourceLayerId} LEFT JOIN {CP.Table} sp ON sp.{CP.Id}=s.{CL.PositionId} LEFT JOIN {CE.Table} se ON se.{CE.Id}=s.{CL.EntryId} LEFT JOIN {CP.Table} sep ON sep.{CP.Id}=se.{CE.PositionId} WHERE {SelectedEntry("e", "p")} OR {SelectedLayer("l", "lp")}";
                await Scan($"SELECT e.{CE.OperationId},e.{CE.WarehouseId},p.{CP.WarehouseId},NULL::uuid,NULL::uuid" + consumptionFrom +
                    $" UNION ALL SELECT le.{CE.OperationId},l.{CL.WarehouseId},lp.{CP.WarehouseId},le.{CE.WarehouseId},lep.{CP.WarehouseId}" + consumptionFrom +
                    $" UNION ALL SELECT se.{CE.OperationId},s.{CL.WarehouseId},sp.{CP.WarehouseId},se.{CE.WarehouseId},sep.{CP.WarehouseId}" + consumptionFrom, ws, []);
            }
            if (pendingOperations.Count > 0)
            {
                var ops = Batch(pendingOperations);
                await Scan($"SELECT e.{CE.OperationId},e.{CE.WarehouseId},p.{CP.WarehouseId} FROM {CE.Table} e LEFT JOIN {CP.Table} p ON p.{CP.Id}=e.{CE.PositionId} WHERE e.{CE.OperationId}=ANY(@ops)" +
                    $" UNION ALL SELECT sp.{SP.CostOperationId},d.{SD.SourceWarehouseId},d.{SD.TargetWarehouseId} FROM {CO.Table} o JOIN {SD.Table} d ON d.{SD.Id}=o.{CO.SourceId} LEFT JOIN {SP.Table} sp ON sp.{SP.DocumentId}=d.{SD.Id} WHERE o.{CO.Id}=ANY(@ops)" +
                    $" UNION ALL SELECT sp.{SP.CostOperationId},d.{SD.SourceWarehouseId},d.{SD.TargetWarehouseId} FROM {SP.Table} sp JOIN {SD.Table} d ON d.{SD.Id}=sp.{SP.DocumentId} WHERE sp.{SP.CostOperationId}=ANY(@ops)", [], ops);
            }
            if (pendingLayers.Count > 0)
            {
                var ids = Batch(pendingLayers);
                await using var q = budget.Command($"SELECT l.{CL.SourceLayerId},e.{CE.OperationId},l.{CL.WarehouseId},p.{CP.WarehouseId},e.{CE.WarehouseId},ep.{CP.WarehouseId} FROM {CL.Table} l LEFT JOIN {CP.Table} p ON p.{CP.Id}=l.{CL.PositionId} LEFT JOIN {CE.Table} e ON e.{CE.Id}=l.{CL.EntryId} LEFT JOIN {CP.Table} ep ON ep.{CP.Id}=e.{CE.PositionId} WHERE l.{CL.Id}=ANY(@layers) LIMIT @limit", conn, tx);
                q.Parameters.AddWithValue("layers", ids); q.Parameters.AddWithValue("limit", budget.DiscoveryRemaining + 1);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    budget.Discovery();
                    for (var column = 2; column < r.FieldCount; column++)
                        if (!r.IsDBNull(column)) await Reference(r.GetGuid(column));
                    if (!r.IsDBNull(0)) Layer(r.GetGuid(0));
                    if (!r.IsDBNull(1) && !operations.Contains(r.GetGuid(1)))
                    {
                        if (operations.Count == limits.Groups) throw TooLarge();
                        budget.Group(r.GetGuid(1)); operations.Add(r.GetGuid(1));
                        pendingOperations.Enqueue(r.GetGuid(1));
                    }
                }
            }
        }
        return operations.OrderBy(o => o.ToString("N"), StringComparer.Ordinal).ToArray();
    }
}
