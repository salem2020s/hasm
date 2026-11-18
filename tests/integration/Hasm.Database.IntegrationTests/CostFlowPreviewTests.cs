using System.Collections.Immutable;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;

namespace Hasm.Database.IntegrationTests;

public class CostFlowPreviewTests : CostingTestBase
{
    private CostFlowSnapshotService Reader(CostFlowSnapshotLimits? limits = null) => new(Resolver, Rbac, limits);
    private static ProposedFlowOperation Proposal(CostOperationKind kind, DateOnly date, params ProposedFlowLine[] lines)
        => new(Guid.NewGuid(), date, kind, lines.ToImmutableArray());
    private Task<ConfigResult<CostFlowComparisonResult>> Preview(CostFlowSnapshotService reader, ProposedFlowOperation proposal,
        Guid? user = null, Guid? item = null) => reader.PreviewAsync(user ?? Admin.Id, Company.Id, item ?? Fifo.Id, proposal);
    private static CostFlowComparisonResult Value(ConfigResult<CostFlowComparisonResult> result)
    {
        Assert.True(result.IsOk, result.Code); return result.Value!;
    }
    private static void Reject(ConfigResult<CostFlowComparisonResult> result, ConfigStatus status, string code)
    {
        Assert.Equal((status, code), (result.Status, result.Code)); Assert.Null(result.Value); Assert.Null(result.Fields); Assert.Null(result.CurrentRevision);
    }
    private async Task<long[]> Footprint() =>
        [await Count(CompanySchema.CostPosition.Table), await Count(CompanySchema.CostOperation.Table),
         await Count(CompanySchema.CostEntry.Table), await Count(CompanySchema.CostLayer.Table),
         await Count(CompanySchema.CostConsumption.Table), await Count(CompanySchema.BusinessAudit.Table),
         await Count(CompanySchema.JournalEntry.Table), await Count(CompanySchema.JournalLine.Table)];

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Integrated_preview_loads_its_own_evidence_and_matches_exact_downstream_costs(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 10, 100); await Move(item, 6, date: D2); var issue = await IssueOf(item, 4, W2, D3);
        var proposal = Proposal(CostOperationKind.Receipt, D1.AddDays(-1), new ProposedFlowLine(W1.Id, 10, 300));
        var baseline = await Reader().LoadAsync(Admin.Id, Company.Id, item.Id, W1.Id); Assert.True(baseline.IsOk, baseline.Code);
        var expected = CostFlowComparison.Calculate(baseline.Value!, proposal); var before = await Footprint();
        var result = Value(await Preview(Reader(), proposal, item: item.Id));
        Assert.Equal(expected.ComparisonFingerprint, result.ComparisonFingerprint); Assert.Equal(4, result.Changes.Length);
        Assert.Equal(method == CostMethod.Fifo ? 120m : 80m, Assert.Single(result.Changes, c => c.OperationId == issue.OperationId).ProposedAmount);
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Proposed_transfer_derives_both_root_components_before_calculating(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 2, 2); await Receive(item, 2, 6, W2); var issue = await IssueOf(item, 2, W2, D3);
        var before = await Footprint();
        var result = Value(await Preview(Reader(), Proposal(CostOperationKind.Transfer, D2,
            new ProposedFlowLine(W1.Id, 1, DestinationWarehouseId: W2.Id)), item: item.Id));
        Assert.Equal(2, result.Positions.Length); Assert.All(Assert.Single(result.ProposedSteps).Legs, l => Assert.Equal(1m, l.Step.Amount));
        Assert.Equal(method == CostMethod.Fifo ? 6m : 4.67m, Assert.Single(result.Changes, c => c.OperationId == issue.OperationId).ProposedAmount);
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresFact]
    public async Task Unauthorized_roots_and_hidden_history_return_only_generic_forbidden()
    {
        await PrepareCosting(); await Receive(Fifo, 4, 12); await Move(Fifo, 2, date: D2);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var reader = Reader(); var opened = false;
        reader.SnapshotEstablished = (_, _, _) => { opened = true; return Task.CompletedTask; };
        Reject(await Preview(reader, Proposal(CostOperationKind.Transfer, D1,
            new ProposedFlowLine(W1.Id, 1, DestinationWarehouseId: W2.Id)), viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.False(opened);
        Reject(await Preview(reader, Proposal(CostOperationKind.Issue, D1, new ProposedFlowLine(W1.Id, 3)), viewer.Id),
            ConfigStatus.Forbidden, "forbidden"); // Hidden history wins over the otherwise-invalid later transfer shortage.
        Assert.True(opened);
        var other = await ReadyCompany();
        Reject(await reader.PreviewAsync(Admin.Id, other.Id, Fifo.Id,
            Proposal(CostOperationKind.Receipt, D1, new ProposedFlowLine(W1.Id, 1, 1))), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresTheory]
    [InlineData("success")]
    [InlineData("shortage")]
    [InlineData("unsupported-source")]
    [InlineData("corrupt-layer")]
    public async Task Peer_revocation_masks_results_and_both_loading_and_calculation_errors(string outcome)
    {
        await PrepareCosting(); await Receive(Fifo, 4, 12); await Move(Fifo, 2, date: D2);
        if (outcome == "unsupported-source")
            await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D3, new("stock_adjustment", Guid.NewGuid()), [new(Fifo.Id, W2.Id, 1, 1)]));
        if (outcome == "corrupt-layer")
            await Sql($"UPDATE {CompanySchema.CostLayer.Table} SET {CompanySchema.CostLayer.RemainingAmount}=5 WHERE {CompanySchema.CostLayer.WarehouseId}='{W2.Id}'");
        var proposal = outcome == "shortage" ? Proposal(CostOperationKind.Issue, D1, new ProposedFlowLine(W1.Id, 3))
            : Proposal(CostOperationKind.Receipt, D1, new ProposedFlowLine(W1.Id, 1, 3));
        var proof = await Preview(Reader(), proposal);
        if (outcome == "success") Assert.True(proof.IsOk, proof.Code);
        else Reject(proof, ConfigStatus.Conflict, outcome switch
            { "shortage" => "insufficient_stock", "unsupported-source" => "impact_history_unsupported", _ => "impact_history_inconsistent" });
        var (viewer, role) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var peer = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, viewer.Id, role, ScopeKind.Warehouse, W2.Id);
        Assert.True(peer.IsOk, peer.Code); var reader = Reader(); var reachedFinal = false;
        reader.BeforeFinalAuthorization = async _ =>
        {
            reachedFinal = true;
            Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, peer.Value!.Id)).IsOk);
        };
        var before = await Footprint();
        Reject(await Preview(reader, proposal, viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.True(reachedFinal); Assert.True(await Rbac.IsAllowedAsync(viewer.Id, Company.Id,
            PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(W1.Id)));
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Membership_revocation_after_calculation_returns_no_costs()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 2);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var reader = Reader(); reader.BeforeFinalAuthorization = async _ =>
            Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, viewer.Id)).IsOk);
        var before = await Footprint();
        Reject(await Preview(reader, Proposal(CostOperationKind.Issue, D2, new ProposedFlowLine(W1.Id, 1)), viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task One_deadline_token_spans_loading_calculation_and_final_authorization()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 2); var reader = Reader(new(Deadline: TimeSpan.FromSeconds(3)));
        var sharedToken = default(CancellationToken); var reachedFinal = false;
        reader.SnapshotEstablished = (_, _, token) => { sharedToken = token; return Task.CompletedTask; };
        reader.BeforeFinalAuthorization = async token =>
        {
            reachedFinal = true; Assert.Equal(sharedToken, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var proposal = Proposal(CostOperationKind.Issue, D2, new ProposedFlowLine(W1.Id, 1)); var before = await Footprint();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Preview(reader, proposal)); Assert.True(reachedFinal);
        using var cancelled = new CancellationTokenSource(); var callerReader = Reader();
        callerReader.BeforeFinalAuthorization = _ => { cancelled.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerReader.PreviewAsync(Admin.Id, Company.Id, Fifo.Id, proposal, cancelled.Token));
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task Request_shape_and_global_root_budget_fail_closed_without_partial_output()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 2); var before = await Footprint();
        var ordinary = Proposal(CostOperationKind.Receipt, D2, new ProposedFlowLine(W1.Id, 1, 1));
        var reader = Reader();
        Reject(await Preview(reader, null!), ConfigStatus.Conflict, "invalid_replay_input");
        Reject(await Preview(reader, ordinary with { Lines = [] }), ConfigStatus.Conflict, "invalid_replay_input");
        Reject(await Preview(reader, ordinary with { Lines = Enumerable.Repeat(ordinary.Lines[0], 501).ToImmutableArray() }), ConfigStatus.Conflict, "impact_history_too_large");
        Reject(await Preview(reader, ordinary with { Kind = CostOperationKind.Reversal }), ConfigStatus.Conflict, "impact_history_unsupported");
        Reject(await Preview(reader, Proposal(CostOperationKind.Issue, D2, new ProposedFlowLine(W1.Id, 1, 1))), ConfigStatus.Conflict, "invalid_replay_input");
        Reject(await Preview(Reader(new(Warehouses: 1)), Proposal(CostOperationKind.Transfer, D2,
            new ProposedFlowLine(W1.Id, 1, DestinationWarehouseId: W2.Id))), ConfigStatus.Conflict, "impact_history_too_large");
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task A_concurrent_receipt_does_not_turn_a_preview_into_a_mixed_or_executable_snapshot()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 2); var reader = Reader();
        reader.SnapshotEstablished = async (_, _, _) => { await Receive(Fifo, 1, 100, date: D3); };
        var result = Value(await Preview(reader, Proposal(CostOperationKind.Issue, D2, new ProposedFlowLine(W1.Id, 1))));
        var position = Assert.Single(result.Positions);
        Assert.Equal((2m, 1m, 2m, 1m), (position.OriginalQuantity, position.ProposedQuantity, position.OriginalValue, position.ProposedValue));
        var actual = await Pos(Fifo); Assert.Equal((3m, 102m), (actual.Quantity, actual.Value));
        Assert.Equal(2, await Count(CompanySchema.CostOperation.Table)); Assert.Equal(2, await Count(CompanySchema.CostEntry.Table));
        await AssertConsistent();
    }
}
