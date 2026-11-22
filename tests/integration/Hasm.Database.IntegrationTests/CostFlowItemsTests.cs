using System.Collections.Immutable;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;

namespace Hasm.Database.IntegrationTests;

public class CostFlowItemsTests : CostingTestBase
{
    private CostFlowSnapshotService Reader(CostFlowSnapshotLimits? limits = null) => new(Resolver, Rbac, limits);
    private CostFlowItemRoots Root(Guid item, params Guid[] warehouses) => new(item, warehouses.ToImmutableArray());
    private Task<ConfigResult<ImmutableArray<CostFlowSnapshot>>> Read(CostFlowSnapshotService reader,
        ImmutableArray<CostFlowItemRoots> roots, Guid? user = null)
        => reader.LoadItemsAsync(user ?? Admin.Id, Company.Id, roots);
    private static ImmutableArray<CostFlowSnapshot> Value(ConfigResult<ImmutableArray<CostFlowSnapshot>> result)
    {
        Assert.True(result.IsOk, result.Code); return result.Value;
    }
    private static void Reject(ConfigResult<ImmutableArray<CostFlowSnapshot>> result, ConfigStatus status, string code)
    {
        Assert.Equal((status, code), (result.Status, result.Code)); Assert.True(result.Value.IsDefault);
        Assert.Null(result.Fields); Assert.Null(result.CurrentRevision);
    }
    private async Task<long[]> Footprint() =>
        [await Count(CompanySchema.CostPosition.Table), await Count(CompanySchema.CostOperation.Table),
         await Count(CompanySchema.CostEntry.Table), await Count(CompanySchema.CostLayer.Table),
         await Count(CompanySchema.CostConsumption.Table), await Count(CompanySchema.BusinessAudit.Table)];

    [PostgresFact]
    public async Task Mixed_methods_are_canonical_and_empty_roots_create_nothing()
    {
        await PrepareCosting(); await Receive(Fifo, 3, 9); await Receive(Avg, 2, 10);
        var before = await Footprint();
        var roots = ImmutableArray.Create(Root(Fifo.Id, W1.Id, W2.Id, W1.Id), Root(Avg.Id, W1.Id));
        var result = Value(await Read(Reader(), roots));
        var reversed = Value(await Read(Reader(), roots.Reverse().ToImmutableArray()));
        Assert.Equal(result.Select(x => x.ItemId).OrderBy(x => x.ToString("N"), StringComparer.Ordinal), result.Select(x => x.ItemId));
        Assert.Equal(result.Select(x => x.HistoryFingerprint), reversed.Select(x => x.HistoryFingerprint));
        Assert.Equal(CostMethod.Fifo, Assert.Single(result, x => x.ItemId == Fifo.Id).Method);
        Assert.Equal(CostMethod.MovingAverage, Assert.Single(result, x => x.ItemId == Avg.Id).Method);
        var empty = Assert.Single(Assert.Single(result, x => x.ItemId == Fifo.Id).Positions, p => p.WarehouseId == W2.Id);
        Assert.Null(empty.PositionId); Assert.Equal((0m, 0m), (empty.Quantity, empty.Value));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresFact]
    public async Task One_repeatable_read_covers_all_items_despite_a_concurrent_commit()
    {
        await PrepareCosting(); await Receive(Fifo, 1, 2); await Receive(Avg, 1, 3);
        var roots = ImmutableArray.Create(Root(Fifo.Id, W1.Id), Root(Avg.Id, W1.Id));
        var reader = Reader(); var observed = 0;
        reader.SnapshotEstablished = async (_, _, _) =>
        {
            observed++;
            await Cost.ReceiveAsync(Company.Id, Admin.Id, new(Key(), D2, null,
                [new(Fifo.Id, W1.Id, 2, 4), new(Avg.Id, W1.Id, 2, 6)]));
        };
        var original = Value(await Read(reader, roots)); Assert.Equal(1, observed);
        Assert.All(original, item => { Assert.Single(item.Entries); Assert.Equal(1m, Assert.Single(item.Positions).Quantity); });
        var current = Value(await Read(Reader(), roots));
        Assert.All(current, item => { Assert.Equal(2, item.Entries.Length); Assert.Equal(3m, Assert.Single(item.Positions).Quantity); });
        Assert.All(original, item => Assert.NotEqual(item.HistoryFingerprint, Assert.Single(current, x => x.ItemId == item.ItemId).HistoryFingerprint));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Hidden_later_item_closure_masks_an_earlier_items_unsupported_history()
    {
        await PrepareCosting(); var ordered = new[] { Fifo, Fifo2 }.OrderBy(i => i.Id.ToString("N"), StringComparer.Ordinal).ToArray();
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new(Key(), D1, new("stock_adjustment", Guid.NewGuid()),
            [new(ordered[0].Id, W1.Id, 1, 1)]));
        await Receive(ordered[1], 2, 2); await Move(ordered[1], 1, date: D2);
        var roots = ImmutableArray.Create(Root(ordered[0].Id, W1.Id), Root(ordered[1].Id, W1.Id));
        Reject(await Read(Reader(), roots), ConfigStatus.Conflict, "impact_history_unsupported");
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        Reject(await Read(Reader(), roots, viewer.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_membership_revocation_masks_success_and_failure(bool unsupported)
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1);
        if (unsupported) await Cost.ReceiveAsync(Company.Id, Admin.Id, new(Key(), D1, new("stock_adjustment", Guid.NewGuid()), [new(Avg.Id, W1.Id, 1, 1)]));
        else await Receive(Avg, 1, 1);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var reader = Reader(); var final = false;
        reader.BeforeFinalAuthorization = async _ =>
        { final = true; Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, viewer.Id)).IsOk); };
        Reject(await Read(reader, [Root(Fifo.Id, W1.Id), Root(Avg.Id, W1.Id)], viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.True(final);
    }

    [PostgresTheory]
    [InlineData("positions")]
    [InlineData("groups")]
    [InlineData("entries")]
    [InlineData("components")]
    public async Task Capacity_is_global_even_when_each_item_fits_alone(string kind)
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1); await Receive(Fifo2, 1, 1);
        var limits = kind switch { "positions" => new CostFlowSnapshotLimits(Warehouses: 1),
            "groups" => new(Groups: 1), "entries" => new(Entries: 1), _ => new(Components: 1) };
        var reader = Reader(limits); var before = await Footprint();
        Assert.Single(Value(await Read(reader, [Root(Fifo.Id, W1.Id)])));
        Assert.Single(Value(await Read(reader, [Root(Fifo2.Id, W1.Id)])));
        Reject(await Read(reader, [Root(Fifo.Id, W1.Id), Root(Fifo2.Id, W1.Id)]), ConfigStatus.Conflict, "impact_history_too_large");
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task A_shared_operation_counts_once_but_retains_every_items_entries()
    {
        await PrepareCosting();
        var posting = await Cost.ReceiveAsync(Company.Id, Admin.Id, new(Key(), D1, null,
            [new(Fifo.Id, W1.Id, 1, 2), new(Avg.Id, W1.Id, 3, 9)]));
        var result = Value(await Read(Reader(new(Groups: 1)), [Root(Fifo.Id, W1.Id), Root(Avg.Id, W1.Id)]));
        Assert.Equal(2, result.Length);
        Assert.All(result, item => Assert.Equal(posting.OperationId, Assert.Single(item.Entries).OperationId));
        Assert.Equal(11m, result.Sum(item => Assert.Single(item.Positions).Value));
    }

    [PostgresFact]
    public async Task Another_items_authorized_warehouse_does_not_expand_the_wrong_component()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 2); await Move(Fifo, 1, date: D2);
        await Receive(Avg, 1, 3, W2); await Receive(Avg, 4, 20, W1);
        var result = Value(await Read(Reader(), [Root(Fifo.Id, W1.Id), Root(Avg.Id, W2.Id)]));
        Assert.Equal(2, Assert.Single(result, i => i.ItemId == Fifo.Id).Positions.Length);
        var avg = Assert.Single(result, i => i.ItemId == Avg.Id);
        Assert.Equal(W2.Id, Assert.Single(avg.Positions).WarehouseId); Assert.Single(avg.Entries);
        Assert.Equal(3m, Assert.Single(avg.Positions).Value);
    }

    [PostgresTheory]
    [InlineData("empty")]
    [InlineData("item")]
    [InlineData("duplicate")]
    [InlineData("roots")]
    [InlineData("warehouse")]
    [InlineData("items-limit")]
    [InlineData("roots-limit")]
    public async Task Invalid_requests_fail_before_opening_company_data(string kind)
    {
        await PrepareCosting(); var reader = Reader(); var opened = false;
        reader.SnapshotEstablished = (_, _, _) => { opened = true; return Task.CompletedTask; };
        ImmutableArray<CostFlowItemRoots> roots = kind switch
        {
            "empty" => [], "item" => [Root(Guid.Empty, W1.Id)],
            "duplicate" => [Root(Fifo.Id, W1.Id), Root(Fifo.Id, W2.Id)],
            "roots" => [Root(Fifo.Id)], "warehouse" => [Root(Fifo.Id, Guid.Empty)],
            "items-limit" => Enumerable.Range(0, 501).Select(_ => Root(Guid.NewGuid(), W1.Id)).ToImmutableArray(),
            _ => [Root(Fifo.Id, Enumerable.Repeat(W1.Id, 10_001).ToArray())]
        };
        Reject(await Read(reader, roots), ConfigStatus.Conflict, "invalid_impact_roots"); Assert.False(opened);
    }

    [PostgresFact]
    public async Task Hidden_roots_and_another_company_are_refused_before_snapshot_reads()
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1); await Receive(Avg, 1, 1, W2);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var reader = Reader(); var opened = false;
        reader.SnapshotEstablished = (_, _, _) => { opened = true; return Task.CompletedTask; };
        var roots = ImmutableArray.Create(Root(Fifo.Id, W1.Id), Root(Avg.Id, W2.Id));
        Reject(await Read(reader, roots, viewer.Id), ConfigStatus.Forbidden, "forbidden"); Assert.False(opened);
        var other = await ReadyCompany();
        Reject(await reader.LoadItemsAsync(Admin.Id, other.Id, roots), ConfigStatus.Forbidden, "forbidden"); Assert.False(opened);
    }

    [PostgresFact]
    public async Task Cancellation_of_the_shared_snapshot_never_returns_a_partial_item_set()
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1); await Receive(Avg, 1, 1);
        using var cancellation = new CancellationTokenSource(); var reader = Reader();
        reader.SnapshotEstablished = (_, _, _) => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.LoadItemsAsync(Admin.Id, Company.Id,
            [Root(Fifo.Id, W1.Id), Root(Avg.Id, W1.Id)], cancellation.Token));
    }

    [PostgresTheory]
    [InlineData("commands")]
    [InlineData("discovery")]
    public async Task Shared_discovery_and_sql_budgets_cannot_be_reset_between_items(string kind)
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1); await Receive(Fifo2, 1, 1);
        CostFlowSnapshotService Limited(int n) => Reader(kind == "commands" ? new(Commands: n) : new(DiscoveryRows: n));
        var lower = 1; var upper = 1000;
        Assert.True((await Read(Limited(upper), [Root(Fifo.Id, W1.Id)])).IsOk);
        while (lower < upper)
        {
            var middle = (lower + upper) / 2;
            if ((await Read(Limited(middle), [Root(Fifo.Id, W1.Id)])).IsOk) upper = middle;
            else lower = middle + 1;
        }
        var reader = Limited(lower);
        Assert.True((await Read(reader, [Root(Fifo2.Id, W1.Id)])).IsOk);
        Reject(await Read(reader, [Root(Fifo.Id, W1.Id), Root(Fifo2.Id, W1.Id)]), ConfigStatus.Conflict, "impact_history_too_large");
    }
}
