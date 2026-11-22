using System.Collections.Immutable;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;

namespace Hasm.Database.IntegrationTests;

public class CostSourceEvidenceTests : StockPostingTestBase
{
    private CostFlowSnapshotService Reader(CostFlowSnapshotLimits? limits = null) => new(Resolver, Rbac, limits);
    private Task<ConfigResult<CostSourceEvidence>> Read(CostFlowSnapshotService reader, StockDocumentView doc,
        StockPostInput? input = null, Guid? user = null, long? revision = null)
        => reader.LoadSourceEvidenceAsync(user ?? Admin.Id, Company.Id, doc.Id, revision ?? doc.Revision, input);
    private static CostSourceEvidence Value(ConfigResult<CostSourceEvidence> result)
    { Assert.True(result.IsOk, result.Code); return result.Value!; }
    private static void Reject(ConfigResult<CostSourceEvidence> result, ConfigStatus status, string code)
    {
        Assert.Equal((status, code), (result.Status, result.Code)); Assert.Null(result.Value);
        Assert.Null(result.Fields);
    }
    private async Task<long[]> Footprint() =>
        [await Count(CompanySchema.CostPosition.Table), await Count(CompanySchema.CostOperation.Table),
         await Count(CompanySchema.CostEntry.Table), await Count(CompanySchema.CostLayer.Table),
         await Count(CompanySchema.CostConsumption.Table), await Count(CompanySchema.StockPosting.Table),
         await Count(CompanySchema.StockDocumentEvent.Table), await Count(CompanySchema.JournalEntry.Table),
         await Count(CompanySchema.JournalLine.Table), await Count(CompanySchema.BusinessAudit.Table)];

    [PostgresFact]
    public async Task Real_mixed_item_source_preserves_every_line_and_reproducibly_binds_receipt_amounts_without_writes()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, q: 2), L(Avg, q: 1), L(Fifo, q: 3)));
        var before = await Footprint();
        var evidence = Value(await Read(Reader(), doc, Amounts(20, 30, 45)));
        Assert.Equal((doc.Id, doc.Revision, doc.DecidedBy), (evidence.Header.Id, evidence.Header.Revision, evidence.Header.DecidedBy));
        Assert.Equal(new[] { 1, 2, 3 }, evidence.Lines.Select(l => l.LineNo));
        Assert.Equal(new decimal?[] { 20, 30, 45 }, evidence.Lines.Select(l => l.ReceiptAmount));
        Assert.Equal(2, evidence.Comparisons.Length);
        var fifo = Assert.Single(evidence.Comparisons, c => c.Proposal.Lines.Length == 2);
        Assert.Equal(new[] { 2m, 3m }, fifo.Proposal.Lines.Select(l => l.Quantity));
        Assert.Equal(new[] { 20m, 45m }, fifo.ProposedSteps.SelectMany(s => s.Legs).Select(l => l.Step.Amount));
        Assert.Equal(evidence.EvidenceFingerprint, Value(await Read(Reader(), doc, Amounts(20, 30, 45))).EvidenceFingerprint);
        Assert.NotEqual(evidence.EvidenceFingerprint, Value(await Read(Reader(), doc, Amounts(20, 31, 45))).EvidenceFingerprint);
        Assert.Equal(before, await Footprint()); Assert.Null(await Cost.GetPositionAsync(Company.Id, Fifo.Id, W1.Id));
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Stored_issue_date_drives_exact_backdated_comparison(CostMethod method)
    {
        await PreparePosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 10, 100); await IssueOf(item, 4, date: D3);
        var doc = await Approved(Issue(W1, "2026-03-11", L(item, q: 2))); var before = await Footprint();
        var comparison = Assert.Single(Value(await Read(Reader(), doc)).Comparisons);
        Assert.Equal(D2, comparison.Proposal.EffectiveDate);
        Assert.Equal(20m, Assert.Single(Assert.Single(comparison.ProposedSteps).Legs).Step.Amount);
        Assert.Equal((4m, 40m), (Assert.Single(comparison.Positions).ProposedQuantity, Assert.Single(comparison.Positions).ProposedValue));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Duplicate_item_transfer_lines_use_normal_exact_aggregation(CostMethod method)
    {
        await PreparePosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 10, 100);
        var doc = await Approved(Transfer(W1, W2, "2026-03-11", L(item, q: 2), L(item, q: 3)));
        var before = await Footprint(); var evidence = Value(await Read(Reader(), doc));
        Assert.Equal(2, evidence.Lines.Length);
        var comparison = Assert.Single(evidence.Comparisons); Assert.Equal(5m, Assert.Single(comparison.Proposal.Lines).Quantity);
        Assert.All(Assert.Single(comparison.ProposedSteps).Legs, leg => Assert.Equal(50m, leg.Step.Amount));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData("draft")]
    [InlineData("submitted")]
    [InlineData("stale")]
    [InlineData("posted")]
    [InlineData("missing")]
    [InlineData("missing-revision")]
    public async Task Eligibility_is_bound_to_actual_source_state(string kind)
    {
        await PreparePosting(); StockDocumentView doc;
        if (kind is "draft" or "submitted")
        {
            doc = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(lines: [L(Fifo)])));
            if (kind == "submitted") doc = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, doc.Id, doc.Revision));
        }
        else doc = await Approved(Receipt(lines: [L(Fifo)]));
        if (kind == "posted") Ok(await Post(doc, Amounts(1)));
        var before = await Footprint();
        var result = await Reader().LoadSourceEvidenceAsync(Admin.Id, Company.Id, kind == "missing" ? Guid.NewGuid() : doc.Id,
            kind == "missing-revision" ? null : kind == "stale" ? 0 : doc.Revision, Amounts(1));
        var expected = kind switch
        {
            "draft" or "submitted" => (ConfigStatus.Conflict, "document_not_approved"),
            "stale" => (ConfigStatus.PreconditionFailed, "revision_conflict"), "posted" => (ConfigStatus.Conflict, "already_posted"),
            "missing-revision" => (ConfigStatus.PreconditionRequired, "precondition_required"), _ => (ConfigStatus.Forbidden, "forbidden")
        };
        Reject(result, expected.Item1, expected.Item2);
        if (kind == "stale") Assert.Equal(doc.Revision, result.CurrentRevision);
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData("missing")]
    [InlineData("null-amount")]
    [InlineData("negative")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("precision")]
    [InlineData("range")]
    [InlineData("issue-amount")]
    [InlineData("null-child")]
    public async Task Amount_refusals_match_the_normal_posting_path(string kind)
    {
        await PreparePosting(); await Receive(Fifo, 2, 2);
        var doc = await Approved(kind == "issue-amount" ? Issue(lines: [L(Fifo)]) : Receipt(lines: [L(Fifo)]));
        StockPostInput? input = kind switch
        {
            "missing" => null, "null-amount" => new([new(1, null)]), "negative" => Amounts(-1),
            "duplicate" => new([new(1, 1), new(1, 1)]), "unknown" => new([new(2, 1)]),
            "precision" => Amounts(1.001m), "range" => Amounts(1_000_000_000_000_000_000m), "null-child" => new([null!]), _ => Amounts(1)
        };
        var before = await Footprint(); var source = await Read(Reader(), doc, input); var normal = await Post(doc, input);
        Assert.Equal(ConfigStatus.Invalid, source.Status); Assert.Equal(normal.Status, source.Status);
        Assert.Equal(normal.Code, source.Code); Assert.Equal(normal.Fields, source.Fields); Assert.Null(source.Value);
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData("document-only")]
    [InlineData("cost-only")]
    [InlineData("other-warehouse")]
    public async Task A_bootstrap_grant_is_never_actual_source_or_cost_authorization(string kind)
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)]));
        var permissions = kind == "document-only" ? new[] { PermissionCatalog.Codes.StockDocsView }
            : kind == "cost-only" ? [PermissionCatalog.Codes.StockBalanceView]
            : new[] { PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockBalanceView };
        var (viewer, _) = await Member(permissions, ScopeKind.Warehouse, kind == "other-warehouse" ? W2.Id : W1.Id);
        var reader = Reader(); var observed = false; reader.SnapshotEstablished = (_, _, _) => { observed = true; return Task.CompletedTask; };
        Reject(await Read(reader, doc, Amounts(1), viewer.Id), ConfigStatus.Forbidden, "forbidden"); Assert.False(observed);
    }

    [PostgresFact]
    public async Task Hidden_downstream_history_masks_a_stale_source_revision()
    {
        await PreparePosting(); await Receive(Fifo, 2, 2); await Move(Fifo, 1, date: D2);
        var doc = await Approved(Receipt(lines: [L(Fifo)]));
        Reject(await Read(Reader(), doc, Amounts(1), revision: 0), ConfigStatus.PreconditionFailed, "revision_conflict");
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var result = await Read(Reader(), doc, Amounts(1), viewer.Id, 0);
        Reject(result, ConfigStatus.Forbidden, "forbidden"); Assert.Null(result.CurrentRevision);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_source_visibility_masks_both_success_and_validation_failure(bool invalid)
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)]));
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Company, null);
        var role = await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "source-view", [PermissionCatalog.Codes.StockDocsView]); Assert.True(role.IsOk);
        var assignment = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, viewer.Id, role.Value!.Id, ScopeKind.Warehouse, W1.Id); Assert.True(assignment.IsOk);
        var reader = Reader(); var reachedFinal = false;
        reader.BeforeFinalAuthorization = async _ =>
        { reachedFinal = true; Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Value!.Id)).IsOk); };
        Reject(await Read(reader, doc, Amounts(invalid ? -1 : 1), viewer.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.True(reachedFinal); Assert.True(await Rbac.IsAllowedAsync(viewer.Id, Company.Id, PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(W1.Id)));
    }

    [PostgresFact]
    public async Task Header_lines_and_all_item_histories_share_one_snapshot_during_a_writer_commit()
    {
        await PreparePosting(); await Receive(Fifo, 1, 1); await Receive(Avg, 1, 2);
        var doc = await Approved(Receipt(W1, "2026-03-11", L(Fifo), L(Avg)));
        var before = Value(await Read(Reader(), doc, Amounts(3, 4)));
        var reader = Reader(); var observers = 0;
        reader.SnapshotEstablished = async (_, _, _) =>
        {
            observers++;
            await Cost.ReceiveAsync(Company.Id, Admin.Id, new(Key(), D3, null, [new(Fifo.Id, W1.Id, 2, 4), new(Avg.Id, W1.Id, 2, 8)]));
        };
        var snapshot = Value(await Read(reader, doc, Amounts(3, 4))); Assert.Equal(1, observers);
        Assert.Equal(before.EvidenceFingerprint, snapshot.EvidenceFingerprint);
        var fresh = Value(await Read(Reader(), doc, Amounts(3, 4))); Assert.NotEqual(snapshot.EvidenceFingerprint, fresh.EvidenceFingerprint);
        Assert.All(snapshot.Comparisons, c => Assert.Equal(1m, Assert.Single(c.Positions).OriginalQuantity));
        Assert.All(fresh.Comparisons, c => Assert.Equal(3m, Assert.Single(c.Positions).OriginalQuantity));
    }

    [PostgresFact]
    public async Task Source_status_changed_after_header_read_is_not_mixed_into_the_old_snapshot()
    {
        await PreparePosting(); var doc = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(lines: [L(Fifo)])));
        var reader = Reader(); StockDocumentView? approved = null;
        reader.SnapshotEstablished = async (_, _, _) =>
        {
            var changed = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, doc.Id, doc.Revision, Receipt(lines: [L(Fifo, q: 2)])));
            var submitted = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, doc.Id, changed.Revision));
            var approver = await Approver(); approved = Ok(await Docs.ApproveAsync(approver.Id, Company.Id, doc.Id, submitted.Revision));
        };
        Reject(await Read(reader, doc, Amounts(4)), ConfigStatus.Conflict, "document_not_approved");
        var current = Value(await Read(Reader(), approved!, Amounts(4)));
        Assert.Equal(2m, Assert.Single(current.Lines).BaseQuantity); Assert.Equal(approved!.Revision, current.Header.Revision);
    }

    [PostgresFact]
    public async Task Changed_conversion_is_refused_instead_of_repricing_the_original_source()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Apple, Box, 1)]));
        await Sql($"UPDATE {CompanySchema.ItemUnit.Table} SET {CompanySchema.ItemUnit.Factor}=6 WHERE {CompanySchema.ItemUnit.ItemId}='{Apple.Id}' AND {CompanySchema.ItemUnit.UnitId}='{Box.Id}'");
        var before = await Footprint();
        Reject(await Read(Reader(), doc, Amounts(12)), ConfigStatus.Conflict, "line_conversion_changed");
        Assert.Equal(before, await Footprint()); Assert.Equal(12m, (await Docs.GetAsync(Company.Id, doc.Id))!.Lines[0].BaseQuantity);
    }

    [PostgresTheory]
    [InlineData("positions")]
    [InlineData("groups")]
    [InlineData("entries")]
    [InlineData("components")]
    [InlineData("commands")]
    [InlineData("discovery")]
    public async Task Source_metadata_and_all_proposals_share_the_whole_operation_budget(string kind)
    {
        await PreparePosting(); await Receive(Fifo, 1, 1); await Receive(Avg, 1, 1);
        var doc = await Approved(Receipt(lines: [L(Fifo), L(Avg)]));
        var limits = kind switch { "positions" => new CostFlowSnapshotLimits(Warehouses: 1), "groups" => new(Groups: 3),
            "entries" => new(Entries: 3), "components" => new(Components: 2), "commands" => new(Commands: 1), _ => new(DiscoveryRows: 1) };
        var before = await Footprint(); Reject(await Read(Reader(limits), doc, Amounts(1, 1)), ConfigStatus.Conflict, "impact_history_too_large");
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_and_deadline_never_return_partial_source_evidence(bool deadline)
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)])); using var cancellation = new CancellationTokenSource();
        var reader = Reader(deadline ? new(Deadline: TimeSpan.FromSeconds(3)) : null);
        reader.SnapshotEstablished = async (_, _, token) =>
        { if (deadline) await Task.Delay(Timeout.InfiniteTimeSpan, token); else cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.LoadSourceEvidenceAsync(Admin.Id, Company.Id,
            doc.Id, doc.Revision, Amounts(1), cancellation.Token));
    }

    [PostgresFact]
    public async Task Final_source_view_authorization_cannot_escape_the_shared_sql_quota()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)]));
        async Task<(ConfigResult<CostSourceEvidence> Result, bool Final)> Run(int n)
        {
            var final = false; var reader = Reader(new(Commands: n));
            reader.BeforeFinalAuthorization = _ => { final = true; return Task.CompletedTask; };
            return (await Read(reader, doc, Amounts(1)), final);
        }
        var low = 1; var high = 1000; var proof = await Run(high); Assert.True(proof.Result.IsOk, proof.Result.Code);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if ((await Run(middle)).Result.IsOk) high = middle;
            else low = middle + 1;
        }
        var refused = await Run(low - 1); Assert.True(refused.Final);
        Reject(refused.Result, ConfigStatus.Conflict, "impact_history_too_large");
        Assert.Equal(proof.Result.Value!.EvidenceFingerprint, Value((await Run(low)).Result).EvidenceFingerprint);
    }

    [PostgresFact]
    public async Task A_source_identifier_cannot_resolve_in_another_company()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)])); var other = await ReadyCompany();
        Reject(await Reader().LoadSourceEvidenceAsync(Admin.Id, other.Id, doc.Id, doc.Revision, Amounts(1)), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task Oversized_entered_amount_payload_is_refused_before_source_reads()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)])); var reader = Reader(); var observed = false;
        reader.SnapshotEstablished = (_, _, _) => { observed = true; return Task.CompletedTask; };
        var input = new StockPostInput(Enumerable.Range(1, 501).Select(n => new StockPostLineInput(n, 1)).ToArray());
        Reject(await Read(reader, doc, input), ConfigStatus.Conflict, "impact_history_too_large"); Assert.False(observed);
    }
}
