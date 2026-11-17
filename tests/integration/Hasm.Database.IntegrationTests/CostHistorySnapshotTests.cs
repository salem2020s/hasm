using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

public class CostHistorySnapshotTests : CostingTestBase
{
    private CostHistorySnapshotService Reader(int limit = 100_000) => new(Resolver, Rbac, limit);
    private static CostHistorySnapshot Snapshot(ConfigResult<CostHistorySnapshot> result)
    {
        Assert.True(result.IsOk, $"{result.Status} {result.Code}");
        return result.Value!;
    }
    private static void Refused(ConfigResult<CostHistorySnapshot> result, ConfigStatus status, string code)
    {
        Assert.Equal((status, code), (result.Status, result.Code));
        Assert.Null(result.Value); Assert.Null(result.Fields);
    }
    private async Task<(long Positions, long Operations, long Entries, long Layers, long Audit)> Footprint() =>
        (await Count(CompanySchema.CostPosition.Table), await Count(CompanySchema.CostOperation.Table), await Count(CompanySchema.CostEntry.Table),
            await Count(CompanySchema.CostLayer.Table), await Count(CompanySchema.BusinessAudit.Table));

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Complete_ordinary_history_is_immutable_repeatable_and_matches_the_original_engine(CostMethod method)
    {
        await PrepareCosting();
        var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 10, 100, date: D1);
        await Receive(item, 5, 100, date: D2);
        await IssueOf(item, 12, date: D3);
        var before = await Footprint();
        var reader = Reader();
        var result = Snapshot(await reader.LoadAsync(Admin.Id, Company.Id, item.Id, W1.Id));
        Assert.Equal((Company.Id, item.Id, W1.Id, method, "USD", 2, 3L),
            (result.CompanyId, result.ItemId, result.WarehouseId, result.Method, result.CurrencyCode, result.MoneyDecimals, result.LastSequence));
        Assert.Equal((3m, method == CostMethod.Fifo ? 60m : 40m), (result.Quantity, result.Value));
        Assert.Equal([1L, 2L, 3L], result.Entries.Select(e => e.Sequence));
        Assert.Equal([D1, D2, D3], result.Entries.Select(e => e.EffectiveDate));
        Assert.Equal(method == CostMethod.Fifo ? 140m : 160m, result.Entries[^1].Amount);
        Assert.Equal(3, result.Entries.Select(e => e.ToReplayEvent().Id).Distinct().Count());
        var again = Snapshot(await reader.LoadAsync(Admin.Id, Company.Id, item.Id, W1.Id));
        Assert.Equal(result.HistoryFingerprint, again.HistoryFingerprint);
        Assert.Equal(result.Entries.ToArray(), again.Entries.ToArray());
        var comparison = CostImpactPreview.Calculate(result, new ProposedCostEvent(Guid.NewGuid(), D1, CostOperationKind.Receipt, 5, 50));
        var repricedIssue = Assert.Single(comparison.Changes.Where(e => e.EntryId == result.Entries[^1].EntryId));
        Assert.Equal(method == CostMethod.Fifo ? 120m : 150m, repricedIssue.ProposedAmount);
        Assert.Equal((8m, method == CostMethod.Fifo ? 130m : 100m), (comparison.ProposedQuantity, comparison.ProposedValue));
        Assert.Equal(250m, comparison.ProposedValue + repricedIssue.ProposedAmount);
        Assert.Equal(result.HistoryFingerprint, comparison.HistoryFingerprint);
        Assert.Equal(result.Entries[^1].Amount, repricedIssue.OriginalAmount);
        Assert.Equal(before, await Footprint());
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Reading_an_empty_position_creates_no_position_layer_operation_or_audit_event()
    {
        await PrepareCosting();
        var before = await Footprint();
        var result = Snapshot(await Reader().LoadAsync(Admin.Id, Company.Id, Fifo2.Id, W2.Id));
        Assert.Null(result.PositionId);
        Assert.Equal((CostMethod.Fifo, 0L, 0m, 0m), (result.Method, result.LastSequence, result.Quantity, result.Value));
        Assert.Empty(result.Entries);
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData("transfer")]
    [InlineData("reversal")]
    [InlineData("adjustment")]
    public async Task Unsupported_history_is_refused_in_full_instead_of_becoming_a_partial_ordinary_trajectory(string kind)
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100);
        if (kind == "transfer") await Move(Fifo, 2, date: D2);
        else if (kind == "reversal") { var issue = await IssueOf(Fifo, 2); await Undo(issue.Key, date: D2); }
        else await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D2, new CostingSource("stock_adjustment", Guid.NewGuid()), [new(Fifo.Id, W1.Id, 1, 10)]));
        var before = await Footprint();
        Refused(await Reader().LoadAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id), ConfigStatus.Conflict, "impact_history_unsupported");
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Warehouse_permissions_and_company_membership_are_checked_before_any_snapshot_data_is_returned()
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var (nobody, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null);
        var other = await ReadyCompany();
        var reader = Reader();
        Assert.Equal(100m, Snapshot(await reader.LoadAsync(viewer.Id, Company.Id, Fifo.Id, W1.Id)).Value);
        Refused(await reader.LoadAsync(viewer.Id, Company.Id, Fifo.Id, W2.Id), ConfigStatus.Forbidden, "forbidden");
        Refused(await reader.LoadAsync(viewer.Id, Company.Id, Guid.NewGuid(), Guid.NewGuid()), ConfigStatus.Forbidden, "forbidden");
        Refused(await reader.LoadAsync(nobody.Id, Company.Id, Fifo.Id, W1.Id), ConfigStatus.Forbidden, "forbidden");
        Refused(await reader.LoadAsync(Admin.Id, other.Id, Fifo.Id, W1.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task Snapshot_is_repeatable_read_and_read_only_without_blocking_a_concurrent_receipt()
    {
        await PrepareCosting();
        await Receive(Avg, 10, 100);
        var reader = Reader();
        reader.SnapshotEstablished = async (conn, tx) =>
        {
            await using var mode = new NpgsqlCommand("SHOW transaction_read_only", conn, tx);
            Assert.Equal("on", await mode.ExecuteScalarAsync());
            mode.CommandText = "SHOW transaction_isolation";
            Assert.Equal("repeatable read", await mode.ExecuteScalarAsync());
            // The writer commits while the reader retains its original, internally consistent snapshot.
            await Receive(Avg, 1, 25, date: D2).WaitAsync(TimeSpan.FromSeconds(30));
        };
        var old = Snapshot(await reader.LoadAsync(Admin.Id, Company.Id, Avg.Id, W1.Id));
        Assert.Equal((1L, 10m, 100m), (old.LastSequence, old.Quantity, old.Value));
        reader.SnapshotEstablished = null;
        var current = Snapshot(await reader.LoadAsync(Admin.Id, Company.Id, Avg.Id, W1.Id));
        Assert.Equal((2L, 11m, 125m), (current.LastSequence, current.Quantity, current.Value));
        Assert.NotEqual(old.HistoryFingerprint, current.HistoryFingerprint);
        Assert.Single(old.Entries);
    }

    [PostgresFact]
    public async Task A_revocation_during_loading_returns_no_data_even_though_the_company_snapshot_is_older()
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var reader = Reader();
        reader.SnapshotEstablished = async (_, _) => Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, viewer.Id)).IsOk);
        Refused(await reader.LoadAsync(viewer.Id, Company.Id, Fifo.Id, W1.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task A_resource_limit_refuses_the_whole_snapshot_and_does_not_silently_truncate_history()
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100); await IssueOf(Fifo, 1); await IssueOf(Fifo, 1);
        Refused(await Reader(limit: 2).LoadAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id), ConfigStatus.Conflict, "impact_history_too_large");
    }

    [PostgresFact]
    public async Task Changed_live_policy_never_silently_reinterprets_a_position_method()
    {
        await PrepareCosting();
        await Receive(Avg, 5, 50);
        var group = (await Inv.ListGroupsAsync(Company.Id)).Single(g => g.Code == "AVGG");
        Ok(await Inv.UpdateGroupAsync(Admin.Id, Company.Id, group.Id, group.Revision, new ItemGroupInput("AVGG", "x", "Avg", null, 1)));
        Refused(await Reader().LoadAsync(Admin.Id, Company.Id, Avg.Id, W1.Id), ConfigStatus.Conflict, "valuation_method_changed");
        Assert.Equal((CostMethod.MovingAverage, 5m, 50m), ((await Pos(Avg)).Method, (await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
    }
}

public class StockDocumentCostHistorySnapshotTests : StockPostingTestBase
{
    [PostgresFact]
    public async Task Real_posted_receipt_and_issue_origins_are_verified_against_the_actual_cost_operation()
    {
        await PreparePosting();
        var receipt = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var received = Ok(await Post(receipt, Amounts(100)));
        var issue = await Approved(Issue(W1, "2026-03-11", L(Fifo, Pcs, 4)));
        var issued = Ok(await Post(issue));
        var before = await Counts();
        var result = await new CostHistorySnapshotService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id);
        Assert.True(result.IsOk, result.Code);
        Assert.Equal((6m, 60m), (result.Value!.Quantity, result.Value.Value));
        Assert.Equal([receipt.Id, issue.Id], result.Value.Entries.Select(e => e.SourceId!.Value));
        Assert.Equal([received.CostOperationId, issued.CostOperationId], result.Value.Entries.Select(e => e.OperationId));
        Assert.Equal(before, await Counts());
    }

    [PostgresTheory]
    [InlineData("unposted")]
    [InlineData("wrong-operation")]
    [InlineData("wrong-type")]
    public async Task A_document_reference_cannot_authenticate_an_unrelated_ordinary_cost_operation(string mismatch)
    {
        await PreparePosting();
        var source = await Approved(mismatch == "wrong-type" ? Issue(W1, "2026-03-10", L(Fifo, Pcs, 1)) : Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1)));
        if (mismatch == "wrong-operation") Ok(await Post(source, Amounts(10)));
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, new CostingSource("stock_document", source.Id), [new(Fifo.Id, W1.Id, 1, 10)]));
        var result = await new CostHistorySnapshotService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id);
        Assert.Equal((ConfigStatus.Conflict, "impact_history_unsupported"), (result.Status, result.Code));
        Assert.Null(result.Value);
    }
}

public class NegativeCostHistorySnapshotTests : NegativeStockTestBase
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_and_settled_negative_history_cannot_be_mistaken_for_complete_normal_ledger_history(bool settled)
    {
        await PrepareNegative();
        await Emptied(Avg, 3, 15);
        Ok(await Post(await Exceptional(Avg, 7, "2026-03-12")));
        if (settled) { await ReceiveDoc(Avg, 7, 28, "2026-03-13"); Assert.Empty(await Obligations(openOnly: true)); }
        var before = await Counts();
        var result = await new CostHistorySnapshotService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, Avg.Id, W1.Id);
        Assert.Equal((ConfigStatus.Conflict, "impact_history_unsupported"), (result.Status, result.Code));
        Assert.Null(result.Value); Assert.Null(result.Fields);
        Assert.Equal(before, await Counts());
    }
}
