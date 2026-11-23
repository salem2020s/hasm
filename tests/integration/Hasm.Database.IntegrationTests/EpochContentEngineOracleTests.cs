using System.Collections.Immutable;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Costing;
using Npgsql;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using LY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.8D executor slice E1b, the engine oracle: the layers, consumptions, remaining figures and position totals that <see cref="EpochContent"/> derives for an epoch equal, row by row, what the REAL
/// <c>InventoryCostingService</c> writes when the same epoch is performed with its own operations (reversal of every stornoed operation in plan order, the event, the rebooks), starting from a history built
/// with the real engine. The test performs the epoch only through the ordinary public engine (dates that never go backwards: dates do not influence layers, consumptions or costs); nothing here writes
/// outside the engine, no executor exists.
/// </summary>
public class EpochContentEngineOracleTests : CostingTestBase
{
    private async Task<List<object[]>> Rows(string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await r.ReadAsync()) { var row = new object[r.FieldCount]; r.GetValues(row); rows.Add(row); }
        return rows;
    }

    private sealed record DbLayer(Guid Id, EpochLayerKey Key, long Rank, decimal OriginalQuantity, decimal OriginalAmount, decimal Quantity, decimal Amount, Guid? Source);

    /// <summary>All layers of an item with their deterministic keys: (position, creating entry sequence, ordinal in creation order).</summary>
    private async Task<List<DbLayer>> Layers(Guid item)
    {
        var rows = await Rows($"SELECT l.{LY.Id}, l.{LY.WarehouseId}, e.{EN.Seq}, l.{LY.Seq}, l.{LY.OriginalQuantity}, l.{LY.OriginalAmount}, l.{LY.RemainingQuantity}, l.{LY.RemainingAmount}, l.{LY.SourceLayerId} " +
            $"FROM {LY.Table} l JOIN {EN.Table} e ON e.{EN.Id} = l.{LY.EntryId} WHERE l.{LY.ItemId} = '{item}' ORDER BY l.{LY.WarehouseId}, e.{EN.Seq}, l.{LY.Seq}");
        var list = new List<DbLayer>(); var ordinal = new Dictionary<(Guid, long), int>();
        foreach (var r in rows)
        {
            var k = ((Guid)r[1], (long)r[2]); ordinal[k] = ordinal.TryGetValue(k, out var o) ? o + 1 : 0;
            list.Add(new DbLayer((Guid)r[0], new EpochLayerKey(k.Item1, k.Item2, ordinal[k]), (long)r[3], (decimal)r[4], (decimal)r[5], (decimal)r[6], (decimal)r[7], r[8] as Guid?));
        }
        return list;
    }

    private async Task<CostFlowSnapshot> Snapshot(Guid item, params Guid[] roots)
    {
        var result = await new CostFlowSnapshotService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, item, roots.ToImmutableArray());
        Assert.True(result.IsOk, result.Code); return result.Value!;
    }

    private static ProposedFlowOperation Proposal(CostOperationKind kind, DateOnly date, params ProposedFlowLine[] lines) => new(Guid.NewGuid(), date, kind, lines.ToImmutableArray());

    /// <summary>The scenarios: a history built with the engine and the proposal that is inserted before part of it.</summary>
    private async Task<(ItemView Item, ProposedFlowOperation Proposal)> History(CostMethod method, int scenario)
    {
        var item = method == CostMethod.Fifo ? Fifo : Avg; var dayBefore = D1.AddDays(-1);
        switch (scenario)
        {
            case 0:     // the worked example: an issue behind a back-dated receipt
                await Receive(item, 10, 100); await IssueOf(item, 4, W1, D2);
                return (item, Proposal(CostOperationKind.Receipt, D1.AddDays(0), new ProposedFlowLine(W1.Id, 10, 200)));
            case 1:     // two layers, a transfer consuming both (two mirrors), an issue at the destination
                await Receive(item, 10, 100); await Receive(item, 10, 200, W1, D2); await Move(item, 15, W1, W2, D2); await IssueOf(item, 5, W2, D3);
                return (item, Proposal(CostOperationKind.Receipt, dayBefore, new ProposedFlowLine(W1.Id, 5, 25)));
            case 2:     // an issue event inside the history, receipts and issues after it
                await Receive(item, 10, 100); await Receive(item, 10, 200, W1, D3); await IssueOf(item, 12, W1, D3); await IssueOf(item, 2, W1, D3);
                return (item, Proposal(CostOperationKind.Issue, D2, new ProposedFlowLine(W1.Id, 3)));
            case 3:     // a chain of transfers in both directions on equal dates, then an issue; the event is back-dated before everything
                await Receive(item, 10, 100); await Receive(item, 10, 150, W2, D1); await Move(item, 3, W1, W2, D2); await Move(item, 4, W2, W1, D2); await IssueOf(item, 2, W1, D3);
                return (item, Proposal(CostOperationKind.Receipt, dayBefore, new ProposedFlowLine(W1.Id, 10, 200)));
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo, 0)] [InlineData(CostMethod.Fifo, 1)] [InlineData(CostMethod.Fifo, 2)] [InlineData(CostMethod.Fifo, 3)]
    [InlineData(CostMethod.MovingAverage, 0)] [InlineData(CostMethod.MovingAverage, 1)] [InlineData(CostMethod.MovingAverage, 2)] [InlineData(CostMethod.MovingAverage, 3)]
    public async Task The_derived_epoch_content_equals_every_row_the_real_engine_writes_for_the_same_epoch(CostMethod method, int scenario)
    {
        await PrepareCosting();
        var (item, proposal) = await History(method, scenario);
        var snapshot = await Snapshot(item.Id, W1.Id, W2.Id);
        var comparison = CostFlowComparison.Calculate(snapshot, proposal);
        var plan = EpochSchedule.Derive(snapshot, comparison);
        var content = EpochContent.Derive(snapshot, plan);
        Assert.NotEmpty(plan.Lineage);                                                        // a real epoch: something is stornoed and rebooked
        var before = (await Layers(item.Id)).ToDictionary(l => l.Key);
        var warehouses = new Dictionary<Guid, WarehouseView> { [W1.Id] = W1, [W2.Id] = W2 };
        var date = D3.AddDays(10);

        // ---- the epoch, performed with the engine's own operations, in the plan's order
        foreach (var operation in plan.Operations)
        {
            var first = operation.Entries[0];
            switch (operation.Role)
            {
                case EpochRole.Storno:
                    await Undo(await Sql<string>($"SELECT {OP.Key} FROM {OP.Table} WHERE {OP.Id} = '{operation.OriginalOperationId}'"), date);
                    break;
                default:
                    if (operation.Kind == CostOperationKind.Receipt) await Receive(item, first.Quantity, first.Amount, warehouses[first.WarehouseId], date);
                    else if (operation.Kind == CostOperationKind.Issue) await IssueOf(item, first.Quantity, warehouses[first.WarehouseId], date);
                    else await Move(item, first.Quantity, warehouses[first.WarehouseId], warehouses[operation.Entries[1].WarehouseId], date);
                    break;
            }
        }
        await AssertConsistent();

        // ---- entries: every entry the plan names is the row the engine wrote at that (position, sequence)
        var entries = (await Rows($"SELECT {EN.WarehouseId}, {EN.Seq}, {EN.Kind}, {EN.Direction}, {EN.Quantity}, {EN.Amount}, {EN.QuantityAfter}, {EN.ValueAfter} FROM {EN.Table} WHERE {EN.ItemId} = '{item.Id}'"))
            .ToDictionary(r => ((Guid)r[0], (long)r[1]));
        foreach (var e in plan.Operations.SelectMany(o => o.Entries))
        {
            var row = entries[(e.WarehouseId, e.Sequence)];
            Assert.Equal(((short)e.Kind, (short)e.Direction, e.Quantity, e.Amount, e.QuantityAfter, e.ValueAfter), ((short)row[2], (short)row[3], (decimal)row[4], (decimal)row[5], (decimal)row[6], (decimal)row[7]));
        }
        var written = plan.Operations.SelectMany(o => o.Entries).Count();
        Assert.Equal(written, entries.Keys.Count(k => k.Item2 > snapshot.Positions.Single(p => p.WarehouseId == k.Item1).LastSequence));      // the engine wrote no other entry

        // ---- layers: the created layers are exactly the L lines (key, arrival rank, figures, source)
        var after = await Layers(item.Id); var byId = after.ToDictionary(l => l.Id); var afterByKey = after.ToDictionary(l => l.Key);
        var createdInDb = after.Where(l => !before.ContainsKey(l.Key)).OrderBy(l => l.Key.WarehouseId).ThenBy(l => l.Key.EntrySequence).ThenBy(l => l.Key.Ordinal).ToList();
        var createdInContent = content.Entries.SelectMany(e => e.Layers).OrderBy(l => l.Key.WarehouseId).ThenBy(l => l.Key.EntrySequence).ThenBy(l => l.Key.Ordinal).ToList();
        Assert.Equal(createdInContent.Select(l => (l.Key, l.ArrivalRank, l.Quantity, l.Amount, l.Source)),
            createdInDb.Select(l => (l.Key, l.Rank, l.OriginalQuantity, l.OriginalAmount, l.Source is { } s ? byId[s].Key : null)));

        // ---- consumptions: per entry, in order (the C lines, restores included)
        var consumptionRows = await Rows($"SELECT e.{EN.WarehouseId}, e.{EN.Seq}, c.{CS.LayerId}, c.{CS.Quantity}, c.{CS.Amount}, c.{CS.Restores} FROM {CS.Table} c JOIN {EN.Table} e ON e.{EN.Id} = c.{CS.EntryId} WHERE e.{EN.ItemId} = '{item.Id}' ORDER BY c.{CS.Id}");
        var consumptions = consumptionRows.GroupBy(r => ((Guid)r[0], (long)r[1])).ToDictionary(g => g.Key, g => g.Select(r => new EpochConsumption(byId[(Guid)r[2]].Key, (decimal)r[3], (decimal)r[4], (bool)r[5])).ToList());
        foreach (var e in content.Entries)
            Assert.Equal(e.Consumptions.ToArray(), consumptions.GetValueOrDefault((e.WarehouseId, e.Sequence)) is { } list ? list.ToArray() : []);
        Assert.Equal(content.Entries.Count(e => e.Consumptions.Length > 0), consumptions.Keys.Count(k => k.Item2 > snapshot.Positions.Single(p => p.WarehouseId == k.Item1).LastSequence));   // no consumption of any other entry

        // ---- remaining figures: the R lines for the touched layers; every other layer is unchanged
        foreach (var r in content.Layers)
        {
            var layer = afterByKey[r.Key];
            Assert.Equal((r.AfterQuantity, r.AfterAmount, r.ArrivalRank), (layer.Quantity, layer.Amount, layer.Rank));
            if (!r.Created) Assert.Equal((before[r.Key].Quantity, before[r.Key].Amount), (r.BeforeQuantity, r.BeforeAmount));
        }
        var touched = content.Layers.Select(l => l.Key).ToHashSet();
        foreach (var layer in after.Where(l => !touched.Contains(l.Key))) Assert.Equal((before[layer.Key].Quantity, before[layer.Key].Amount), (layer.Quantity, layer.Amount));

        // ---- the L, C and R groups of the T2 epoch text, rebuilt from the rows the engine wrote, are byte-identical to the text derived from the plan (the E and O groups need the member rows of the later slices)
        long BaselineLast(Guid warehouse) => snapshot.Positions.Single(p => p.WarehouseId == warehouse).LastSequence;
        var positionRows = (await Rows($"SELECT {PO.WarehouseId}, {PO.Quantity}, {PO.Value}, {PO.LastSeq} FROM {PO.Table} WHERE {PO.ItemId} = '{item.Id}'")).ToDictionary(r => (Guid)r[0]);
        var fromRows = new EpochTextInput([], [],
            [.. createdInDb.Select(l => new EpochTextLayer(item.Id, l.Key.WarehouseId, l.Rank, l.Key.EntrySequence, l.OriginalQuantity, l.OriginalAmount,
                l.Source is null ? null : item.Id, l.Source is { } s ? byId[s].Key.WarehouseId : null, l.Source is { } s2 ? byId[s2].Rank : null))],
            [.. consumptionRows.Where(r => (long)r[1] > BaselineLast((Guid)r[0])).Select(r => new EpochTextConsumption(item.Id, (Guid)r[0], (long)r[1], item.Id, byId[(Guid)r[2]].Key.WarehouseId, byId[(Guid)r[2]].Rank, (decimal)r[3], (decimal)r[4], (bool)r[5]))],
            [.. content.Layers.Select(r => new EpochTextRemaining(item.Id, r.Key.WarehouseId, afterByKey[r.Key].Rank, afterByKey[r.Key].Quantity, afterByKey[r.Key].Amount))],
            [.. snapshot.Positions.Where(p => positionRows.TryGetValue(p.WarehouseId, out var row) && (long)row[3] > p.LastSequence).Select(p => new EpochTextPosition(item.Id, p.WarehouseId, p.LastSequence, (long)positionRows[p.WarehouseId][3],
                (decimal)positionRows[p.WarehouseId][1], (decimal)positionRows[p.WarehouseId][2], after.Count(l => l.Key.WarehouseId == p.WarehouseId && l.Quantity > 0m)))]);
        var derived = EpochText.FromEpoch(snapshot, plan, content, Guid.NewGuid(), Guid.NewGuid()) with { Operations = [], Entries = [] };
        Assert.Equal(EpochText.Build(derived).Text, EpochText.Build(fromRows).Text);
        Assert.Equal(EpochText.Build(derived).Digest, EpochText.Build(fromRows).Digest);

        // ---- positions: the P lines
        var positions = (await Rows($"SELECT {PO.WarehouseId}, {PO.Quantity}, {PO.Value}, {PO.LastSeq} FROM {PO.Table} WHERE {PO.ItemId} = '{item.Id}'")).ToDictionary(r => (Guid)r[0]);
        foreach (var p in content.Positions)
        {
            // a position of the closure that has never held anything has no row (the planner simulates it): the epoch must not have touched it
            var row = positions.GetValueOrDefault(p.WarehouseId);
            Assert.Equal((p.Quantity, p.Value, p.LastSequence), row is null ? (0m, 0m, 0L) : ((decimal)row[1], (decimal)row[2], (long)row[3]));
        }
    }
}
