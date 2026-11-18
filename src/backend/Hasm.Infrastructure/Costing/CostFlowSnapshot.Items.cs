using System.Collections.Immutable;
using System.Data;
using Hasm.Infrastructure.CompanyConfiguration;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;

namespace Hasm.Infrastructure.Costing;

internal sealed record CostFlowItemRoots(Guid ItemId, ImmutableArray<Guid> Warehouses);

internal sealed partial class CostFlowSnapshotService
{
    // Internal history evidence only. Source header/lines must eventually join this same read session;
    // this method is not a source-bound plan, approval, API or execution token.
    internal Task<ConfigResult<ImmutableArray<CostFlowSnapshot>>> LoadItemsAsync(Guid user, Guid company,
        ImmutableArray<CostFlowItemRoots> requested, CancellationToken ct = default)
        => ReadAuthorizedItemsAsync(user, company, requested, static (snapshots, _) => snapshots, ct);

    private static bool ValidItemRoots(ImmutableArray<CostFlowItemRoots> requested)
    {
        if (requested.IsDefaultOrEmpty || requested.Length > 500) return false;
        var items = new HashSet<Guid>(); var rawRoots = 0;
        foreach (var request in requested)
        {
            if (request is null || request.ItemId == Guid.Empty || !items.Add(request.ItemId) || request.Warehouses.IsDefaultOrEmpty)
                return false;
            if (request.Warehouses.Length > 10_000 - rawRoots || request.Warehouses.Contains(Guid.Empty)) return false;
            rawRoots += request.Warehouses.Length;
        }
        return true;
    }

    private async Task<ConfigResult<ImmutableArray<CostFlowSnapshot>>> ReadItemsAsync(Guid company,
        ImmutableArray<CostFlowItemRoots> requested, HashSet<Guid> allScopes, Func<Guid, Task<bool>> can,
        Budget budget, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(company, budget.Sql.Reserve, ct);
        budget.Sql.Reserve(2); // BEGIN and eventual ROLLBACK/disposal, before starting.
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using (var q = budget.Command("SET TRANSACTION READ ONLY", conn, tx)) await q.ExecuteNonQueryAsync(ct);
        // Establish the snapshot before the observer and prove every requested warehouse exists.
        await using (var q = budget.Command($"SELECT count(*) FROM {W.Table} WHERE {W.Id}=ANY(@roots)", conn, tx))
        {
            q.Parameters.AddWithValue("roots", allScopes.ToArray());
            if ((long)(await q.ExecuteScalarAsync(ct))! != allScopes.Count)
                return ConfigResult<ImmutableArray<CostFlowSnapshot>>.Fail(ConfigStatus.NotFound, "warehouse_not_found");
        }
        if (SnapshotEstablished is { } observe) await observe(conn, tx, ct);
        var components = new List<(Guid Item, HashSet<Guid> Scopes, Guid[] Operations)>();
        // ALL identifier closures precede ANY policy/valuation read or history diagnostic.
        foreach (var request in requested.OrderBy(r => r.ItemId.ToString("N"), StringComparer.Ordinal))
        {
            var scopes = request.Warehouses.ToHashSet();
            async Task<bool> RetainAndAuthorize(Guid warehouse)
            {
                allScopes.Add(warehouse);
                return await can(warehouse);
            }
            var operations = await Discover(conn, tx, budget, request.ItemId, scopes, RetainAndAuthorize, ct);
            components.Add((request.ItemId, scopes, operations));
        }
        foreach (var scope in allScopes) if (!await can(scope)) throw new Denied();
        var results = ImmutableArray.CreateBuilder<CostFlowSnapshot>(components.Count);
        foreach (var component in components)
        {
            var result = await ReadDiscoveredAsync(conn, tx, company, component.Item, component.Scopes, component.Operations, budget, ct);
            if (!result.IsOk) return ConfigResult<ImmutableArray<CostFlowSnapshot>>.Fail(result.Status, result.Code!);
            results.Add(result.Value ?? throw Inconsistent());
        }
        ct.ThrowIfCancellationRequested();
        return ConfigResult<ImmutableArray<CostFlowSnapshot>>.Ok(results.ToImmutable());
    }
}
