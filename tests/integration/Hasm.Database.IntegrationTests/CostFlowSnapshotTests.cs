using System.Collections.Immutable;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using LayerTable = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;

namespace Hasm.Database.IntegrationTests;

public class CostFlowSnapshotTests : CostingTestBase
{
    private CostFlowSnapshotService Reader(CostFlowSnapshotLimits? limits = null) => new(Resolver, Rbac, limits);
    private static CostFlowSnapshot Snapshot(ConfigResult<CostFlowSnapshot> result)
    {
        Assert.True(result.IsOk, $"{result.Status} {result.Code}"); return result.Value!;
    }
    private static void Reject(ConfigResult<CostFlowSnapshot> result, ConfigStatus status, string code)
    {
        Assert.Equal((status, code), (result.Status, result.Code)); Assert.Null(result.Value); Assert.Null(result.Fields);
    }
    private Task<ConfigResult<CostFlowSnapshot>> Read(CostFlowSnapshotService reader, Guid? user = null, Guid? item = null, Guid? warehouse = null) =>
        reader.LoadAsync(user ?? Admin.Id, Company.Id, item ?? Fifo.Id, warehouse ?? W1.Id);
    private async Task<(long Positions, long Operations, long Entries, long Layers, long Takes, long Audit)> Footprint() =>
        (await Count(CompanySchema.CostPosition.Table), await Count(CompanySchema.CostOperation.Table), await Count(CompanySchema.CostEntry.Table),
            await Count(LayerTable.Table), await Count(CompanySchema.CostConsumption.Table), await Count(CompanySchema.BusinessAudit.Table));

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Complete_chained_transfer_snapshot_reconciles_every_entry_layer_and_consumption(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg; var w3 = await NewWarehouse("W3");
        await Receive(item, 4, 4); await Receive(item, 4, 12);
        await Move(item, 6, date: D2); await IssueOf(item, 3, W2, D2);
        await Move(item, 2, W2, w3, D3); await IssueOf(item, 1, w3, D3);
        var before = await Footprint(); var reader = Reader();
        var snapshot = Snapshot(await Read(reader, item: item.Id));
        Assert.Equal(3, snapshot.Positions.Length); Assert.Equal(8, snapshot.Entries.Length);
        var a = Assert.Single(snapshot.Positions, p => p.WarehouseId == W1.Id);
        var b = Assert.Single(snapshot.Positions, p => p.WarehouseId == W2.Id);
        var c = Assert.Single(snapshot.Positions, p => p.WarehouseId == w3.Id);
        Assert.Equal((2m, method == CostMethod.Fifo ? 6m : 4m), (a.Quantity, a.Value));
        Assert.Equal((1m, method == CostMethod.Fifo ? 3m : 2m), (b.Quantity, b.Value));
        Assert.Equal((1m, method == CostMethod.Fifo ? 3m : 2m), (c.Quantity, c.Value));
        if (method == CostMethod.Fifo)
        {
            Assert.Equal(6, snapshot.Layers.Length); Assert.Equal(6, snapshot.Consumptions.Length);
            Assert.Equal(6, snapshot.LayerBindings.Select(x => x.ActualId).Distinct().Count());
            Assert.Equal(4, snapshot.Layers.Count(l => l.SourceLayerId is not null));
        }
        else { Assert.Empty(snapshot.Layers); Assert.Empty(snapshot.Consumptions); Assert.Empty(snapshot.LayerBindings); }
        Assert.Equal(snapshot.HistoryFingerprint, Snapshot(await Read(reader, item: item.Id)).HistoryFingerprint);
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresFact]
    public async Task Empty_snapshot_does_not_create_positions_or_any_business_rows()
    {
        await PrepareCosting(); var before = await Footprint();
        var snapshot = Snapshot(await Read(Reader(), item: Fifo2.Id));
        Assert.Null(Assert.Single(snapshot.Positions).PositionId); Assert.Empty(snapshot.Entries); Assert.Empty(snapshot.Layers);
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Malformed_foreign_item_source_lineage_authorizes_every_hop_before_returning_a_diagnostic()
    {
        await PrepareCosting(); var w3 = await NewWarehouse("W3");
        await Receive(Fifo, 1, 10); await Receive(Fifo2, 1, 20, W2); await Receive(Fifo2, 1, 30, w3);
        var a = await Sql<Guid>($"SELECT {LayerTable.Id} FROM {LayerTable.Table} WHERE {LayerTable.ItemId}='{Fifo.Id}'");
        var b = await Sql<Guid>($"SELECT {LayerTable.Id} FROM {LayerTable.Table} WHERE {LayerTable.ItemId}='{Fifo2.Id}' AND {LayerTable.WarehouseId}='{W2.Id}'");
        var c = await Sql<Guid>($"SELECT {LayerTable.Id} FROM {LayerTable.Table} WHERE {LayerTable.WarehouseId}='{w3.Id}'");
        // Deliberately corrupt only this disposable test database, restoring the guard atomically.
        await Sql($"BEGIN; ALTER TABLE {LayerTable.Table} DISABLE TRIGGER {LayerTable.GuardRow}; UPDATE {LayerTable.Table} SET {LayerTable.SourceLayerId}=CASE WHEN {LayerTable.Id}='{a}' THEN '{b}'::uuid ELSE '{c}'::uuid END WHERE {LayerTable.Id} IN ('{a}','{b}'); ALTER TABLE {LayerTable.Table} ENABLE TRIGGER {LayerTable.GuardRow}; COMMIT;");
        var (viewer, role) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, viewer.Id, role, ScopeKind.Warehouse, W2.Id)).IsOk);
        // Grant the intermediate warehouse but deliberately not the final warehouse.
        // A company-scoped grant would accidentally authorize the hidden third endpoint.
        var before = await Footprint();
        Reject(await Read(Reader(), viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Reject(await Read(Reader()), ConfigStatus.Conflict, "impact_history_inconsistent");
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Transfer_component_denies_a_viewer_with_only_one_endpoint(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 4, 12); await Move(item, 2);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        Reject(await Read(Reader(), viewer.Id, item.Id), ConfigStatus.Forbidden, "forbidden");
        Reject(await Read(Reader(), viewer.Id, item.Id, W2.Id), ConfigStatus.Forbidden, "forbidden");
        var (downstream, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W2.Id);
        Reject(await Read(Reader(), downstream.Id, item.Id, W2.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task Revoked_membership_never_returns_snapshot_data()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 4);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var reader = Reader();
        reader.SnapshotEstablished = async (_, _, _) => Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, viewer.Id)).IsOk);
        Reject(await Read(reader, viewer.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task Repeatable_read_snapshot_is_read_only_and_does_not_include_a_concurrent_receipt()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 4); var reader = Reader();
        reader.SnapshotEstablished = async (connection, transaction, ct) =>
        {
            await using var mode = new NpgsqlCommand("SHOW transaction_read_only", connection, transaction);
            Assert.Equal("on", await mode.ExecuteScalarAsync(ct));
            mode.CommandText = "SHOW transaction_isolation"; Assert.Equal("repeatable read", await mode.ExecuteScalarAsync(ct));
            await Receive(Fifo, 3, 9);
        };
        var old = Snapshot(await Read(reader)); Assert.Single(old.Entries);
        Assert.Equal((2m, 4m), (Assert.Single(old.Positions).Quantity, Assert.Single(old.Positions).Value));
        var current = Snapshot(await Read(Reader())); Assert.Equal(2, current.Entries.Length);
        Assert.NotEqual(old.HistoryFingerprint, current.HistoryFingerprint);
    }

    [PostgresTheory]
    [InlineData("warehouses")]
    [InlineData("groups")]
    [InlineData("entries")]
    [InlineData("components")]
    [InlineData("discovery")]
    [InlineData("commands")]
    public async Task Every_global_bound_refuses_instead_of_returning_truncated_evidence(string bound)
    {
        await PrepareCosting(); await Receive(Fifo, 2, 2); await Receive(Fifo, 2, 6); await Move(Fifo, 3);
        var limits = bound switch
        {
            "warehouses" => new CostFlowSnapshotLimits(Warehouses: 1),
            "groups" => new CostFlowSnapshotLimits(Groups: 1),
            "entries" => new CostFlowSnapshotLimits(Entries: 1),
            "components" => new CostFlowSnapshotLimits(Components: 1),
            "discovery" => new CostFlowSnapshotLimits(DiscoveryRows: 1),
            _ => new CostFlowSnapshotLimits(Commands: 1)
        };
        var before = await Footprint();
        Reject(await Read(Reader(limits)), ConfigStatus.Conflict, "impact_history_too_large");
        Assert.Equal(before, await Footprint());
        Assert.Equal(4, Snapshot(await Read(Reader())).Entries.Length);
    }

    [PostgresFact]
    public async Task Changed_remaining_layer_value_is_refused_without_repairing_original_rows()
    {
        await PrepareCosting(); await Receive(Fifo, 3, 9); await IssueOf(Fifo, 1);
        await Sql($"UPDATE {LayerTable.Table} SET {LayerTable.RemainingAmount}=5");
        var before = await Footprint();
        Reject(await Read(Reader()), ConfigStatus.Conflict, "impact_history_inconsistent");
        Assert.Equal(5m, await Sql<decimal>($"SELECT {LayerTable.RemainingAmount} FROM {LayerTable.Table}"));
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Deadline_cancels_work_without_returning_partial_evidence()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 4);
        var reader = Reader(new(Deadline: TimeSpan.FromSeconds(3)));
        reader.SnapshotEstablished = (_, _, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var before = await Footprint();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(reader));
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Returning_transfers_preserve_original_fifo_priority_and_average_carrying_value(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 4, 4); await Receive(item, 4, 12);
        await Move(item, 6); await Move(item, 2, W2, W1); await IssueOf(item, 3);
        var snapshot = Snapshot(await Read(Reader(), item: item.Id));
        var source = Assert.Single(snapshot.Positions, p => p.WarehouseId == W1.Id);
        Assert.Equal((1m, method == CostMethod.Fifo ? 1m : 2m), (source.Quantity, source.Value));
        Assert.Equal(method == CostMethod.Fifo ? 7m : 6m, snapshot.Entries[^1].Amount);
        await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Multiline_shared_operations_keep_every_child_rounding_and_free_layer(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, null,
            [new(item.Id, W1.Id, 1, 1), new(item.Id, W1.Id, 2, 100), new(item.Id, W2.Id, 1, 0)]));
        await Cost.IssueAsync(Company.Id, Admin.Id, new CostingIssueRequest(Key(), D1, null,
            [new(item.Id, W1.Id, 1), new(item.Id, W1.Id, 1), new(item.Id, W2.Id, 1)]));
        var before = await Footprint(); var snapshot = Snapshot(await Read(Reader(), item: item.Id));
        Assert.Equal(6, snapshot.Entries.Length); Assert.Equal(2, snapshot.Blocks.Length);
        Assert.All(snapshot.Blocks, block => Assert.Equal(3, block.Children.Length));
        Assert.Equal(method == CostMethod.Fifo ? new[] { 1m, 50m, 0m } : new[] { 33.67m, 33.67m, 0m },
            snapshot.Entries.Skip(3).Select(e => e.Amount));
        var source = Assert.Single(snapshot.Positions, p => p.WarehouseId == W1.Id);
        Assert.Equal((1m, method == CostMethod.Fifo ? 50m : 33.66m), (source.Quantity, source.Value));
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        Reject(await Read(Reader(), viewer.Id, item.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresFact]
    public async Task Company_isolation_and_unknown_scope_are_checked_before_history_lookup()
    {
        await PrepareCosting(); await Receive(Fifo, 1, 10); var other = await ReadyCompany();
        Reject(await Reader().LoadAsync(Admin.Id, other.Id, Fifo.Id, W1.Id), ConfigStatus.Forbidden, "forbidden");
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        Reject(await Read(Reader(), viewer.Id, Guid.NewGuid(), Guid.NewGuid()), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task Reversed_history_is_not_reinterpreted_as_ordinary_receipts_and_issues()
    {
        await PrepareCosting(); await Receive(Fifo, 3, 9); var issue = await IssueOf(Fifo, 1); await Undo(issue.Key);
        var before = await Footprint();
        Reject(await Read(Reader()), ConfigStatus.Conflict, "impact_history_unsupported");
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_authorization_rechecks_membership_on_success_and_structural_failure(bool corrupt)
    {
        await PrepareCosting(); await Receive(Fifo, 3, 9); await IssueOf(Fifo, 1);
        if (corrupt) await Sql($"UPDATE {LayerTable.Table} SET {LayerTable.RemainingAmount}=5");
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var reader = Reader(); var reachedFinalCheck = false;
        reader.BeforeFinalAuthorization = async _ =>
        {
            reachedFinalCheck = true;
            Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, viewer.Id)).IsOk);
        };
        var before = await Footprint();
        Reject(await Read(reader, viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.True(reachedFinalCheck); Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Caller_cancellation_before_final_authorization_cannot_return_a_snapshot()
    {
        await PrepareCosting(); await Receive(Fifo, 1, 2); using var cancelled = new CancellationTokenSource();
        var reader = Reader(); reader.BeforeFinalAuthorization = _ => { cancelled.Cancel(); return Task.CompletedTask; };
        var before = await Footprint();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.LoadAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id, cancelled.Token));
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Repeated_shared_group_metadata_consumes_the_cumulative_discovery_budget()
    {
        await PrepareCosting();
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, null,
            Enumerable.Range(0, 10).Select(_ => new CostingReceiptLine(Fifo.Id, W1.Id, 1, 1)).ToArray()));
        var full = Snapshot(await Read(Reader())); Assert.Equal(10, full.Entries.Length);
        Assert.Single(full.Entries.Select(e => e.OperationId).Distinct());
        var before = await Footprint();
        Reject(await Read(Reader(new(Groups: 1, DiscoveryRows: 10))), ConfigStatus.Conflict, "impact_history_too_large");
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData("entry-sequence")]
    [InlineData("entry-date")]
    [InlineData("entry-amount")]
    [InlineData("entry-direction")]
    [InlineData("layer-parent")]
    [InlineData("layer-sequence")]
    [InlineData("consumption-amount")]
    [InlineData("consumption-restores")]
    public async Task Malformed_history_is_refused_in_full_without_repair_or_partial_evidence(string corruption)
    {
        await PrepareCosting(); await Receive(Fifo, 3, 9); await IssueOf(Fifo, 1);
        var ce = CompanySchema.CostEntry.Table; var cc = CompanySchema.CostConsumption.Table;
        var (table, trigger, update) = corruption switch
        {
            "entry-sequence" => (ce, CompanySchema.CostEntry.GuardRow, $"{CompanySchema.CostEntry.Seq}={CompanySchema.CostEntry.Seq}+10"),
            "entry-date" => (ce, CompanySchema.CostEntry.GuardRow, $"{CompanySchema.CostEntry.EffectiveDate}='2026-03-11'"),
            "entry-amount" => (ce, CompanySchema.CostEntry.GuardRow, $"{CompanySchema.CostEntry.Amount}={CompanySchema.CostEntry.Amount}+1"),
            "entry-direction" => (ce, CompanySchema.CostEntry.GuardRow, $"{CompanySchema.CostEntry.Direction}=-{CompanySchema.CostEntry.Direction}"),
            "layer-parent" => (LayerTable.Table, LayerTable.GuardRow, $"{LayerTable.SourceLayerId}={LayerTable.Id}"),
            "layer-sequence" => (LayerTable.Table, LayerTable.GuardRow, $"{LayerTable.Seq}=99"),
            "consumption-amount" => (cc, CompanySchema.CostConsumption.GuardRow, $"{CompanySchema.CostConsumption.Amount}=2"),
            _ => (cc, CompanySchema.CostConsumption.GuardRow, $"{CompanySchema.CostConsumption.Restores}=true")
        };
        await Sql($"BEGIN; ALTER TABLE {table} DISABLE TRIGGER {trigger}; UPDATE {table} SET {update}; ALTER TABLE {table} ENABLE TRIGGER {trigger}; COMMIT;");
        var before = await Footprint();
        Reject(await Read(Reader()), ConfigStatus.Conflict, "impact_history_inconsistent");
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData("stock_adjustment")]
    [InlineData("future_origin")]
    public async Task Unsupported_origins_cannot_be_silently_omitted_from_an_ordinary_component(string origin)
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1);
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D2, new CostingSource(origin, Guid.NewGuid()), [new(Fifo.Id, W1.Id, 1, 2)]));
        var before = await Footprint();
        Reject(await Read(Reader()), ConfigStatus.Conflict, "impact_history_unsupported");
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Changed_live_method_does_not_reprice_original_positions()
    {
        await PrepareCosting(); await Receive(Avg, 5, 50);
        var group = (await Inv.ListGroupsAsync(Company.Id)).Single(g => g.Code == "AVGG");
        Ok(await Inv.UpdateGroupAsync(Admin.Id, Company.Id, group.Id, group.Revision, new ItemGroupInput("AVGG", "x", "Avg", null, 1)));
        var before = await Footprint();
        Reject(await Read(Reader(), item: Avg.Id), ConfigStatus.Conflict, "valuation_method_changed");
        var position = await Pos(Avg);
        Assert.Equal((CostMethod.MovingAverage, 5m, 50m), (position.Method, position.Quantity, position.Value));
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Tracked_item_is_not_treated_as_an_ordinary_empty_position()
    {
        await PrepareCosting(); var group = (await Inv.ListGroupsAsync(Company.Id)).Single(g => g.Code == "FIFOG");
        var tracked = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id,
            new ItemInput("TRACKED", null, "Tracked", 1, group.Id, Pcs.Id, null, null, true, false, false, null, [])));
        var before = await Footprint();
        Reject(await Read(Reader(), item: tracked.Id), ConfigStatus.Conflict, "impact_history_unsupported");
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoking_only_the_peer_warehouse_hides_success_and_failure_while_root_access_remains(bool corrupt)
    {
        await PrepareCosting(); await Receive(Fifo, 4, 12); await Move(Fifo, 2);
        if (corrupt) await Sql($"UPDATE {LayerTable.Table} SET {LayerTable.RemainingAmount}=5");
        var (viewer, role) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var assignment = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, viewer.Id, role, ScopeKind.Warehouse, W2.Id);
        Assert.True(assignment.IsOk, assignment.Code); var reader = Reader(); var reachedFinalCheck = false;
        reader.BeforeFinalAuthorization = async _ =>
        {
            reachedFinalCheck = true;
            Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Value!.Id)).IsOk);
        };
        var before = await Footprint();
        Reject(await Read(reader, viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.True(reachedFinalCheck);
        Assert.True(await Rbac.IsAllowedAsync(viewer.Id, Company.Id, PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(W1.Id)));
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task All_proposal_root_components_share_one_canonical_read_set(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg; var w3 = await NewWarehouse("W3");
        await Receive(item, 4, 4); await Receive(item, 3, 9, W2); await Move(item, 1, W2, w3);
        var before = await Footprint(); var reader = Reader();
        var a = Snapshot(await reader.LoadAsync(Admin.Id, Company.Id, item.Id, [W1.Id, W2.Id]));
        var b = Snapshot(await reader.LoadAsync(Admin.Id, Company.Id, item.Id, [W2.Id, W1.Id, W2.Id]));
        Assert.Equal(3, a.Positions.Length); Assert.Equal(4, a.Entries.Length);
        Assert.Equal(a.HistoryFingerprint, b.HistoryFingerprint);
        Assert.Equal((4m, 4m), (Assert.Single(a.Positions, p => p.WarehouseId == W1.Id).Quantity,
            Assert.Single(a.Positions, p => p.WarehouseId == W1.Id).Value));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresFact]
    public async Task Every_proposal_root_is_authorized_before_opening_the_history_snapshot()
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1); await Receive(Fifo, 1, 2, W2);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var reader = Reader(); var opened = false;
        reader.SnapshotEstablished = (_, _, _) => { opened = true; return Task.CompletedTask; };
        Reject(await reader.LoadAsync(viewer.Id, Company.Id, Fifo.Id, [W1.Id, W2.Id]), ConfigStatus.Forbidden, "forbidden");
        Assert.False(opened);
    }

    [PostgresFact]
    public async Task Empty_proposal_destination_is_evidence_without_creating_a_position()
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1); var before = await Footprint();
        var snapshot = Snapshot(await Reader().LoadAsync(Admin.Id, Company.Id, Fifo.Id, [W1.Id, W2.Id]));
        var empty = Assert.Single(snapshot.Positions, p => p.WarehouseId == W2.Id);
        Assert.Null(empty.PositionId); Assert.Equal((0m, 0m, 0L), (empty.Quantity, empty.Value, empty.LastSequence));
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Concurrent_writes_to_the_second_root_do_not_mix_snapshot_versions()
    {
        await PrepareCosting(); await Receive(Fifo, 1, 1); await Receive(Fifo, 2, 4, W2);
        var reader = Reader(); reader.SnapshotEstablished = async (_, _, _) => { await Receive(Fifo, 3, 9, W2); };
        var old = Snapshot(await reader.LoadAsync(Admin.Id, Company.Id, Fifo.Id, [W1.Id, W2.Id]));
        Assert.Equal(2, old.Entries.Length);
        var p = Assert.Single(old.Positions, p => p.WarehouseId == W2.Id); Assert.Equal((2m, 4m), (p.Quantity, p.Value));
        var current = Snapshot(await Reader().LoadAsync(Admin.Id, Company.Id, Fifo.Id, [W1.Id, W2.Id]));
        Assert.Equal(3, current.Entries.Length); Assert.NotEqual(old.HistoryFingerprint, current.HistoryFingerprint);
    }

    [PostgresFact]
    public async Task All_roots_share_the_same_warehouse_budget_and_invalid_roots_fail_closed()
    {
        await PrepareCosting(); var before = await Footprint();
        Reject(await Reader(new(Warehouses: 1)).LoadAsync(Admin.Id, Company.Id, Fifo.Id, [W1.Id, W2.Id]),
            ConfigStatus.Conflict, "impact_history_too_large");
        var reader = Reader();
        foreach (var roots in new[] { default(ImmutableArray<Guid>), ImmutableArray<Guid>.Empty,
                     ImmutableArray.Create(Guid.Empty), Enumerable.Repeat(W1.Id, 10_001).ToImmutableArray() })
            Reject(await reader.LoadAsync(Admin.Id, Company.Id, Fifo.Id, roots), ConfigStatus.Conflict, "invalid_impact_roots");
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData("multiline-child-position")]
    [InlineData("foreign-consumption-layer")]
    [InlineData("foreign-consumption-entry")]
    public async Task Malformed_child_and_consumption_references_authorize_hidden_foreign_positions(string corruption)
    {
        await PrepareCosting();
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, null,
            [new(Fifo.Id, W1.Id, 1, 1), new(Fifo.Id, W1.Id, 2, 8)]));
        await IssueOf(Fifo, 1); await Receive(Fifo2, 1, 3, W2); var foreign = await Pos(Fifo2, W2);
        var ce = CompanySchema.CostEntry.Table; var cc = CompanySchema.CostConsumption.Table;
        if (corruption == "multiline-child-position")
            await Sql($"BEGIN; ALTER TABLE {ce} DISABLE TRIGGER {CompanySchema.CostEntry.GuardRow}; UPDATE {ce} SET {CompanySchema.CostEntry.PositionId}='{foreign.Id}',{CompanySchema.CostEntry.Seq}=99 WHERE {CompanySchema.CostEntry.Id}=(SELECT min({CompanySchema.CostEntry.Id}) FROM {ce}); ALTER TABLE {ce} ENABLE TRIGGER {CompanySchema.CostEntry.GuardRow}; COMMIT;");
        else
        {
            var update = corruption == "foreign-consumption-layer"
                ? $"{CompanySchema.CostConsumption.LayerId}=(SELECT {LayerTable.Id} FROM {LayerTable.Table} WHERE {LayerTable.ItemId}='{Fifo2.Id}')"
                : $"{CompanySchema.CostConsumption.EntryId}=(SELECT {CompanySchema.CostEntry.Id} FROM {ce} WHERE {CompanySchema.CostEntry.ItemId}='{Fifo2.Id}')";
            await Sql($"BEGIN; ALTER TABLE {cc} DISABLE TRIGGER {CompanySchema.CostConsumption.GuardRow}; UPDATE {cc} SET {update}; ALTER TABLE {cc} ENABLE TRIGGER {CompanySchema.CostConsumption.GuardRow}; COMMIT;");
        }
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var before = await Footprint();
        Reject(await Read(Reader(), viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Reject(await Read(Reader()), ConfigStatus.Conflict, "impact_history_inconsistent");
        Assert.Equal(before, await Footprint());
    }
}

public class StockDocumentCostFlowSnapshotTests : StockPostingTestBase
{
    [PostgresFact]
    public async Task Document_endpoint_is_authorized_even_when_the_hidden_transfer_leg_is_malformed()
    {
        await PreparePosting();
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10))), Amounts(100)));
        Ok(await Post(await Approved(Transfer(W1, W2, "2026-03-11", L(Avg, Pcs, 6)))));
        var position = await Pos(Avg);
        var e = CompanySchema.CostEntry.Table;
        await Sql($"BEGIN; ALTER TABLE {e} DISABLE TRIGGER {CompanySchema.CostEntry.GuardRow}; UPDATE {e} SET {CompanySchema.CostEntry.PositionId}='{position.Id}',{CompanySchema.CostEntry.WarehouseId}='{W1.Id}',{CompanySchema.CostEntry.Seq}=99 WHERE {CompanySchema.CostEntry.Kind}=4; ALTER TABLE {e} ENABLE TRIGGER {CompanySchema.CostEntry.GuardRow}; COMMIT;");
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var before = await Counts(); var reader = new CostFlowSnapshotService(Resolver, Rbac);
        var denied = await reader.LoadAsync(viewer.Id, Company.Id, Avg.Id, W1.Id);
        Assert.Equal((ConfigStatus.Forbidden, "forbidden"), (denied.Status, denied.Code)); Assert.Null(denied.Value); Assert.Null(denied.Fields);
        var malformed = await reader.LoadAsync(Admin.Id, Company.Id, Avg.Id, W1.Id);
        // Relocating the inbound leg invalidates its authentic document warehouse role;
        // provenance classification precedes sequence arithmetic for authorized readers.
        Assert.Equal((ConfigStatus.Conflict, "impact_history_unsupported"), (malformed.Status, malformed.Code)); Assert.Null(malformed.Value); Assert.Null(malformed.Fields);
        Assert.Equal(before, await Counts());
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Posted_receipt_transfer_and_issue_match_the_exact_immutable_posting(CostMethod method)
    {
        await PreparePosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        var receipt = await Approved(Receipt(W1, "2026-03-10", L(item, Pcs, 10)));
        Ok(await Post(receipt, Amounts(100)));
        var transfer = await Approved(Transfer(W1, W2, "2026-03-11", L(item, Pcs, 6)));
        Ok(await Post(transfer));
        var issue = await Approved(Issue(W2, "2026-03-12", L(item, Pcs, 4)));
        Ok(await Post(issue)); var before = await Counts();
        var result = await new CostFlowSnapshotService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, item.Id, W1.Id);
        Assert.True(result.IsOk, result.Code); var snapshot = result.Value!;
        Assert.Equal(4, snapshot.Entries.Length); Assert.All(snapshot.Entries, e => Assert.True(e.DocumentProof));
        Assert.Equal(new[] { receipt.Id, transfer.Id, transfer.Id, issue.Id }, snapshot.Entries.Select(e => e.SourceId!.Value));
        Assert.Equal((2m, 20m), (Assert.Single(snapshot.Positions, p => p.WarehouseId == W2.Id).Quantity,
            Assert.Single(snapshot.Positions, p => p.WarehouseId == W2.Id).Value));
        var comparison = CostFlowComparison.Calculate(snapshot, new(Guid.NewGuid(), D1.AddDays(-1), CostOperationKind.Receipt,
            [new(W1.Id, 10, 300)]));
        Assert.Equal(4, comparison.Changes.Length);
        Assert.Equal(method == CostMethod.Fifo ? 120m : 80m, comparison.Changes.Single(c => c.EntryId == snapshot.Entries[^1].Id).ProposedAmount);
        Assert.Equal(method == CostMethod.Fifo ? 60m : 40m, comparison.Positions.Single(p => p.WarehouseId == W2.Id).ProposedValue);
        Assert.Equal(before, await Counts());
    }

    [PostgresTheory]
    [InlineData("unposted")]
    [InlineData("wrong-operation")]
    [InlineData("wrong-type")]
    public async Task Unrelated_document_references_cannot_authenticate_a_cost_operation(string mismatch)
    {
        await PreparePosting();
        var source = await Approved(mismatch == "wrong-type" ? Issue(W1, "2026-03-10", L(Fifo, Pcs, 1)) : Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1)));
        if (mismatch == "wrong-operation") Ok(await Post(source, Amounts(10)));
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, new CostingSource("stock_document", source.Id), [new(Fifo.Id, W1.Id, 1, 10)]));
        var before = await Counts();
        var result = await new CostFlowSnapshotService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id);
        Assert.Equal((ConfigStatus.Conflict, "impact_history_unsupported"), (result.Status, result.Code));
        Assert.Null(result.Value); Assert.Null(result.Fields); Assert.Equal(before, await Counts());
    }
}

public class NegativeCostFlowSnapshotTests : NegativeStockTestBase
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Negative_obligation_history_is_not_silently_treated_as_complete_ordinary_history(bool settled)
    {
        await PrepareNegative(); await Emptied(Avg, 3, 15);
        Ok(await Post(await Exceptional(Avg, 7, "2026-03-12")));
        if (settled) { await ReceiveDoc(Avg, 7, 28, "2026-03-13"); Assert.Empty(await Obligations(openOnly: true)); }
        var before = await Counts();
        var result = await new CostFlowSnapshotService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, Avg.Id, W1.Id);
        Assert.Equal((ConfigStatus.Conflict, "impact_history_unsupported"), (result.Status, result.Code));
        Assert.Null(result.Value); Assert.Null(result.Fields); Assert.Equal(before, await Counts());
    }
}
