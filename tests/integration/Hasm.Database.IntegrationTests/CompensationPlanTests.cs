using System.Collections.Immutable;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;

namespace Hasm.Database.IntegrationTests;

/// <summary>3.8D matrix cells M1/M2 end to end against real PostgreSQL: the real authorized snapshot, the real comparison, the real classification evidence of the
/// posted journals and the pure compensation calculation (examples E1, E2 and E6 of PHASE-3.8D-TRANSITION-MATRIX-DECISIONS.md). Nothing is executed or written.
/// The ledger check is the point: inventory account balance + the back-dated receipt's own journal + the compensation = the replayed inventory value.</summary>
public class CompensationPlanTests : StockReversalTestBase
{
    private async Task<StockPostingView> Posted(StockDocumentInput input, StockPostInput? amounts = null)
    {
        var p = Ok(await Post(await Approved(input), amounts)); await PostJournal(p); return p;
    }

    private async Task<(CostFlowComparisonResult Comparison, ClassificationEvidence Evidence, CompensationPlan Plan)> PlanFor(
        ItemView item, WarehouseView[] roots, ProposedFlowOperation proposal, params StockPostingView[] postings)
    {
        var snapshot = await new CostFlowSnapshotService(Resolver, Rbac, null).LoadAsync(Admin.Id, Company.Id, item.Id, [.. roots.Select(w => w.Id)]);
        Assert.True(snapshot.IsOk, snapshot.Code);
        var comparison = CostFlowComparison.Calculate(snapshot.Value!, proposal);
        var evidence = await new ClassificationEvidenceService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, [.. postings.Select(p => p.Id)]);
        Assert.True(evidence.IsOk, evidence.Code);
        return (comparison, evidence.Value!, CompensationCalculator.Calculate(Company.Id, comparison.ComparisonFingerprint, comparison.Changes, evidence.Value!));
    }

    private static ProposedFlowOperation Backdated(string date, WarehouseView warehouse, decimal quantity, decimal amount) =>
        new(Guid.NewGuid(), DateOnly.Parse(date), CostOperationKind.Receipt, [new ProposedFlowLine(warehouse.Id, quantity, amount)]);

    private decimal Ledger(ClassificationEvidence evidence, Guid account) =>
        evidence.Journals.SelectMany(j => j.Lines).Where(l => l.AccountId == account).Sum(l => l.BaseDebit - l.BaseCredit);

    [PostgresFact]
    public async Task E1_average_issue_recost_is_compensated_Dr_K_Cr_I_and_the_ledger_ends_at_the_replayed_inventory_value()
    {
        await PreparePosting();
        var a = await Posted(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10)), Amounts(100m));
        var issue = await Posted(Issue(W1, "2026-03-12", L(Avg, Pcs, 4)));
        var before = (await Count(CompanySchema.JournalEntry.Table), await Count(CompanySchema.CostEntry.Table), await Count(CompanySchema.BusinessAudit.Table));
        var (comparison, evidence, plan) = await PlanFor(Avg, [W1], Backdated("2026-03-11", W1, 5m, 80m), a, issue);
        Assert.Equal(2, plan.Lines.Length);
        var debit = Assert.Single(plan.Lines, l => l.Debit > 0); var credit = Assert.Single(plan.Lines, l => l.Credit > 0);
        Assert.Equal((CogsAcc.Id, 8m, StockAcc.Id, 8m), (debit.AccountId, debit.Debit, credit.AccountId, credit.Credit));
        Assert.Equal((DateOnly.Parse("2026-03-12"), issue.Id), (debit.Date, debit.PostingId));
        Assert.NotNull(debit.PeriodId);                                                         // posted: the original period
        // ledger: receipt A 100 - issue 40 + the back-dated receipt's own journal 80 - compensation 8 = the replayed position value
        Assert.Equal(Assert.Single(comparison.Positions).ProposedValue, Ledger(evidence, StockAcc.Id) + 80m - 8m);
        Assert.Equal(132m, Ledger(evidence, StockAcc.Id) + 80m - 8m);
        Assert.Equal(before, (await Count(CompanySchema.JournalEntry.Table), await Count(CompanySchema.CostEntry.Table), await Count(CompanySchema.BusinessAudit.Table)));   // nothing written
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task E2_fifo_issue_recost_by_an_older_back_dated_layer_is_compensated_and_the_ledger_matches_the_layers()
    {
        await PreparePosting();
        var a = await Posted(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)), Amounts(100m));
        var issue = await Posted(Issue(W1, "2026-03-12", L(Fifo, Pcs, 4)));
        var (comparison, evidence, plan) = await PlanFor(Fifo, [W1], Backdated("2026-03-09", W1, 5m, 100m), a, issue);
        Assert.Equal((40m, 40m), (plan.Lines.Sum(l => l.Debit), plan.Lines.Sum(l => l.Credit)));
        Assert.Equal(Assert.Single(comparison.Positions).ProposedValue, Ledger(evidence, StockAcc.Id) + 100m - 40m);
        Assert.Equal(120m, Ledger(evidence, StockAcc.Id) + 100m - 40m);
    }

    [PostgresFact]
    public async Task E6_a_transfer_needs_no_line_and_the_downstream_issue_carries_the_whole_compensation()
    {
        await PreparePosting();
        var a = await Posted(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10)), Amounts(100m));
        var transfer = Ok(await Post(await Approved(Transfer(W1, W2, "2026-03-11", L(Avg, Pcs, 4)))));
        var issue = await Posted(Issue(W2, "2026-03-12", L(Avg, Pcs, 2)));
        var (comparison, evidence, plan) = await PlanFor(Avg, [W1, W2], Backdated("2026-03-10", W1, 5m, 80m), a, transfer, issue);
        Assert.Equal(issue.Id, Assert.Single(plan.AffectedPostingIds));
        Assert.Equal((4m, 4m), (plan.Lines.Sum(l => l.Debit), plan.Lines.Sum(l => l.Credit)));
        Assert.Equal(comparison.Positions.Sum(p => p.ProposedValue), Ledger(evidence, StockAcc.Id) + 80m - 4m);
        Assert.Equal(156m, Ledger(evidence, StockAcc.Id) + 80m - 4m);
    }

    [PostgresFact]
    public async Task An_issue_whose_journal_is_still_a_draft_is_never_compensated_against()
    {
        await PreparePosting();
        var a = await Posted(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10)), Amounts(100m));
        var issue = Ok(await Post(await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 4)))));   // journal left as Draft
        var error = await Assert.ThrowsAsync<CostingException>(() => PlanFor(Avg, [W1], Backdated("2026-03-11", W1, 5m, 80m), a, issue));
        Assert.Equal("unposted_effect", error.Code);
    }
}
