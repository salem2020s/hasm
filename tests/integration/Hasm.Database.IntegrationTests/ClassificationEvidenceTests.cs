using System.Collections.Immutable;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using AC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.8D classification evidence (contract revision 4) against real PostgreSQL: the stored journal lines are the original classification;
/// authorization of every member precedes any diagnostic; OPEN and unsupported cases fail closed; nothing is written. Disposable cluster only.</summary>
public class ClassificationEvidenceTests : NegativeStockTestBase
{
    private ClassificationEvidenceService Svc(ClassificationLimits? limits = null) => new(Resolver, Rbac, limits);
    private Task<ConfigResult<ClassificationEvidence>> Read(ClassificationEvidenceService svc, Guid? user = null, params StockPostingView[] postings) =>
        svc.LoadAsync(user ?? Admin.Id, Company.Id, [.. postings.Select(p => p.Id)]);
    private static ClassificationEvidence Value(ConfigResult<ClassificationEvidence> r) { Assert.True(r.IsOk, r.Code); return r.Value!; }
    private static void Reject(ConfigResult<ClassificationEvidence> r, ConfigStatus status, string code)
    { Assert.Equal((status, code), (r.Status, r.Code)); Assert.Null(r.Value); Assert.Null(r.Fields); }
    private async Task<long[]> Footprint() =>
        [await Count(CompanySchema.CostPosition.Table), await Count(CompanySchema.CostOperation.Table), await Count(CompanySchema.CostEntry.Table),
         await Count(CompanySchema.StockPosting.Table), await Count(CompanySchema.JournalEntry.Table), await Count(CompanySchema.JournalLine.Table),
         await Count(CompanySchema.JournalAllocation.Table), await Count(CompanySchema.NegativeObligation.Table), await Count(CompanySchema.NegativeSettlement.Table),
         await Count(CompanySchema.BusinessAudit.Table)];

    [PostgresFact]
    public async Task Posted_receipt_and_issue_journals_are_the_original_classification_and_a_read_writes_nothing()
    {
        await PreparePosting();
        var receipt = await ReceiveDocPosted(Avg, 10m, 100m, "2026-03-10");
        var issue = await IssueDocPosted(Avg, 4m, "2026-03-11");
        var before = await Footprint();
        var evidence = Value(await Read(Svc(), null, receipt, issue));
        Assert.Equal(2, evidence.Postings.Length);
        Assert.Equal(2, evidence.Journals.Length);
        Assert.All(evidence.Journals, j => Assert.Equal((short)JournalStatus.Posted, j.Status));
        var receiptJournal = evidence.Journals.Single(j => j.Id == receipt.JournalEntryId);
        Assert.Contains(receiptJournal.Lines, l => l.AccountId == StockAcc.Id && l.BaseDebit == 100m);
        Assert.Contains(receiptJournal.Lines, l => l.AccountId == GoodsAcc.Id && l.BaseCredit == 100m);
        Assert.All(evidence.Journals.SelectMany(j => j.Lines), l => { Assert.False(l.IsRounding); Assert.Empty(l.Allocations); });
        Assert.Equal(evidence.EvidenceFingerprint, Value(await Read(Svc(), null, issue, receipt)).EvidenceFingerprint); // order independent
        Assert.Equal(before, await Footprint());
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task A_draft_journal_is_not_a_posted_effect_and_posting_it_changes_the_fingerprint()
    {
        await PreparePosting();
        var receipt = Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 5))), Amounts(50m)));
        var draft = Value(await Read(Svc(), null, receipt));
        Assert.Equal((short)JournalStatus.Draft, Assert.Single(draft.Journals).Status);
        await PostJournal(receipt);
        var posted = Value(await Read(Svc(), null, receipt));
        Assert.Equal((short)JournalStatus.Posted, Assert.Single(posted.Journals).Status);
        Assert.NotEqual(draft.EvidenceFingerprint, posted.EvidenceFingerprint);
    }

    [PostgresFact]
    public async Task A_later_accounting_configuration_change_does_not_alter_the_original_classification()
    {
        await PreparePosting();
        var receipt = await ReceiveDocPosted(Avg, 5m, 50m, "2026-03-10");
        var before = Value(await Read(Svc(), null, receipt));
        var other = Ok(await new ChartOfAccountsService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new AccountUsageGuard(), new FinancialHistoryGuard(), Clock)
            .CreateAsync(Admin.Id, Company.Id, new AccountInput("1399", null, "مخزون جديد", "New stock", "asset", null, false, null, null)));
        Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 1, new InventoryAccountingInput(other.Id, GoodsAcc.Id, CogsAcc.Id, [], null)));
        var after = Value(await Read(Svc(), null, receipt));
        Assert.Equal(StockAcc.Id, after.Journals.Single().Lines.First(l => l.BaseDebit > 0).AccountId);   // still the account booked at posting time
        Assert.Equal(before.Journals.Single().Lines.Select(l => (l.No, l.AccountId, l.BaseDebit, l.BaseCredit)),
            after.Journals.Single().Lines.Select(l => (l.No, l.AccountId, l.BaseDebit, l.BaseCredit)));
    }

    [PostgresFact]
    public async Task Transfers_have_no_journal_by_design_and_are_refused_not_treated_as_no_change_needed()
    {
        await PreparePosting();
        await ReceiveDocPosted(Avg, 10m, 100m, "2026-03-10");
        var transfer = Ok(await Post(await Approved(Transfer(W1, W2, "2026-03-11", L(Avg, Pcs, 3)))));
        Reject(await Read(Svc(), null, transfer), ConfigStatus.Conflict, "classification_unavailable");
    }

    [PostgresFact]
    public async Task A_reversed_posting_needs_both_journals_and_the_reversing_journal_must_be_posted()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 5)));
        var posted = Ok(await Post(doc, Amounts(50m)));
        await PostJournal(posted);
        var reversed = Ok(await Posting.ReverseAsync(Admin.Id, Company.Id, doc.Id, new StockReverseInput("2026-03-20", "wrong supplier")));
        Assert.Equal(2, reversed.Reversal!.JournalAction);
        // The reversing journal is still a draft: OPEN decision, the plan refuses.
        Reject(await Read(Svc(), null, posted), ConfigStatus.Conflict, "open_decision");
        await PostJournalEntry(reversed.Reversal.JournalEntryId!.Value);
        var evidence = Value(await Read(Svc(), null, posted));
        Assert.Equal(2, evidence.Journals.Length);
        Assert.Contains(evidence.Journals, j => j.Reverses == posted.JournalEntryId && j.Status == (short)JournalStatus.Posted);
        Assert.Contains(evidence.Journals, j => j.Id == posted.JournalEntryId && j.Status == (short)JournalStatus.Reversed);
    }

    [PostgresFact]
    public async Task A_cancelled_draft_of_a_reversed_posting_is_an_open_decision()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 5)));
        var posted = Ok(await Post(doc, Amounts(50m)));           // journal stays a draft
        var reversed = Ok(await Posting.ReverseAsync(Admin.Id, Company.Id, doc.Id, new StockReverseInput("2026-03-20", "mistake")));
        Assert.Equal(1, reversed.Reversal!.JournalAction);        // the draft was cancelled
        Reject(await Read(Svc(), null, posted), ConfigStatus.Conflict, "open_decision");
    }

    [PostgresFact]
    public async Task The_negative_chain_binds_obligation_issue_journal_and_settlement_journals_and_netting_excludes_reversed_settlements()
    {
        await PrepareNegative();
        await ReceiveDoc(Avg, 3m, 15m, "2026-03-10");
        var issueDoc = await Exceptional(Avg, 10m, "2026-03-12");
        var issue = Ok(await Post(issueDoc));
        await PostJournal(issue);
        var receiptDoc = await Approved(Receipt(W1, "2026-03-15", L(Avg, Pcs, 4m)));
        var receipt = Ok(await Post(receiptDoc, Amounts(24m)));
        var before = await Footprint();
        var evidence = Value(await Read(Svc(), null, receipt));         // requesting the receipt pulls the whole chain
        Assert.Contains(evidence.Postings, p => p.Id == issue.Id);
        var obligation = Assert.Single(evidence.Obligations);
        Assert.Equal(issue.JournalEntryId, obligation.JournalId);        // the obligation journal IS the issue posting journal
        Assert.Equal(OblAcc.Id, obligation.ObligationAccountId);
        var settlement = Assert.Single(evidence.Settlements);
        Assert.True(settlement.InNetChain);
        Assert.Equal(before, await Footprint());
        var fromIssue = Value(await Read(Svc(), null, issue));                      // the same closure from either end; the fingerprint also binds the request
        Assert.Equal(evidence.Postings.Select(p => p.Id), fromIssue.Postings.Select(p => p.Id));
        Assert.Equal(evidence.Journals.Select(j => j.Id), fromIssue.Journals.Select(j => j.Id));
        Assert.NotEqual(evidence.EvidenceFingerprint, fromIssue.EvidenceFingerprint);
        Assert.Equal(evidence.EvidenceFingerprint, Value(await Read(Svc(), null, receipt)).EvidenceFingerprint);
    }

    [PostgresFact]
    public async Task A_reversed_settlement_leaves_the_net_chain_while_both_journals_remain_evidence_and_unposted_reversals_are_open_decisions()
    {
        await PrepareNegative();
        await ReceiveDoc(Avg, 3m, 15m, "2026-03-10");
        var issue = Ok(await Post(await Exceptional(Avg, 10m, "2026-03-12")));
        await PostJournal(issue);
        var receiptDoc = await Approved(Receipt(W1, "2026-03-15", L(Avg, Pcs, 4m)));
        var receipt = Ok(await Post(receiptDoc, Amounts(24m)));
        foreach (var s in (await Obligations()).SelectMany(o => o.Settlements).Where(x => x.JournalEntryId is not null)) await PostJournalEntry(s.JournalEntryId!.Value);
        if (receipt.JournalEntryId is { } own) await PostJournalEntry(own);
        Assert.Single(Value(await Read(Svc(), null, receipt)).Settlements, x => x.InNetChain);
        var reversed = Ok(await Posting.ReverseAsync(Admin.Id, Company.Id, receiptDoc.Id, new StockReverseInput("2026-03-20", "wrong supplier")));
        // Posted originals were reversed by DRAFT reversing journals: OPEN decision, the plan refuses.
        Reject(await Read(Svc(), null, receipt), ConfigStatus.Conflict, "open_decision");
        var drafts = (await Obligations()).SelectMany(o => o.Settlements).Where(x => x.ReversesSettlementId is not null && x.JournalEntryId is not null)
            .Select(x => (Guid?)x.JournalEntryId).Append(reversed.Reversal!.JournalEntryId).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        foreach (var id in drafts) if ((await Journal.GetAsync(Company.Id, id))!.Header.Status == JournalStatus.Draft) await PostJournalEntry(id);
        var evidence = Value(await Read(Svc(), null, receipt));
        Assert.DoesNotContain(evidence.Settlements, x => x.InNetChain);                 // the reversed settlement AND its negating row are both out of the net chain
        Assert.Equal(2, evidence.Settlements.Length);
        Assert.Contains(evidence.Journals, j => j.Reverses is not null);               // the reversing journals stay evidence
    }

    [PostgresFact]
    public async Task Callers_without_ledger_account_or_stock_visibility_get_one_indistinguishable_forbidden_without_diagnostics()
    {
        await PreparePosting();
        await ReceiveDocPosted(Avg, 10m, 100m, "2026-03-10");
        var transfer = Ok(await Post(await Approved(Transfer(W1, W2, "2026-03-11", L(Avg, Pcs, 3)))));   // would be classification_unavailable for an authorized caller
        var receipt = Ok(await Post(await Approved(Receipt(W1, "2026-03-12", L(Avg, Pcs, 1))), Amounts(10m)));
        var stockOnly = (await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null)).User;
        var noAccounts = (await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.JournalView], ScopeKind.Company, null)).User;
        var noStock = (await Member([PermissionCatalog.Codes.AccountsView, PermissionCatalog.Codes.JournalView], ScopeKind.Company, null)).User;
        foreach (var user in new[] { stockOnly, noAccounts, noStock })
            Reject(await Read(Svc(), user.Id, transfer), ConfigStatus.Forbidden, "forbidden");
        var missing = await Svc().LoadAsync(Admin.Id, Company.Id, [Guid.NewGuid()]);
        Reject(missing, ConfigStatus.Forbidden, "forbidden");               // missing is indistinguishable from invisible
        Reject(await Svc().LoadAsync(Admin.Id, Company.Id, [Guid.Empty]), ConfigStatus.Conflict, "invalid_classification_request");
        Assert.True((await Read(Svc(), null, receipt)).IsOk);
    }

    [PostgresFact]
    public async Task A_branch_scoped_ledger_grant_for_another_branch_is_denied_for_every_member_of_the_closure()
    {
        await PreparePosting();
        var receipt = await ReceiveDocPosted(Avg, 5m, 50m, "2026-03-10");
        var other = Ok(await Domain.CreateAsync(Admin.Id, Company.Id, new BranchInput("B2", "فرع", "Second", null, null, null, null, null, null, null, null)));
        var user = (await Member([PermissionCatalog.Codes.JournalView], ScopeKind.Branch, other.Id)).User;
        Reject(await Read(Svc(), user.Id, receipt), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task An_account_that_is_no_longer_valid_requires_an_owner_decision_and_is_never_remapped()
    {
        await PreparePosting();
        var receipt = await ReceiveDocPosted(Avg, 5m, 50m, "2026-03-10");
        var message = await RawMessage($"UPDATE {AC.Table} SET {AC.IsActive} = false, {AC.Revision} = {AC.Revision} + 1 WHERE {AC.Id} = '{GoodsAcc.Id}'");
        Assert.Null(message);
        Reject(await Read(Svc(), null, receipt), ConfigStatus.Conflict, "open_decision");
    }

    [PostgresFact]
    public async Task An_unexpected_journal_shape_fails_closed()
    {
        await PreparePosting();
        var receipt = await ReceiveDocPosted(Avg, 5m, 50m, "2026-03-10");
        var evidence = Value(await Read(Svc(), null, receipt));
        Assert.Equal(2, evidence.Journals.Single().Lines.Length);   // aggregated per account: no rounding lines in stock journals
        var tight = Svc(new ClassificationLimits(Postings: 500, Journals: 1000, Lines: 1));
        Reject(await Read(tight, null, receipt), ConfigStatus.Conflict, "classification_history_too_large");
    }

    [PostgresFact]
    public async Task Resource_budgets_and_cancellation_fail_closed_without_partial_evidence()
    {
        await PreparePosting();
        var receipt = await ReceiveDocPosted(Avg, 5m, 50m, "2026-03-10");
        Reject(await Read(Svc(new ClassificationLimits(Commands: 3)), null, receipt), ConfigStatus.Conflict, "classification_history_too_large");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Svc().LoadAsync(Admin.Id, Company.Id, [receipt.Id], cts.Token));
    }

    [PostgresFact]
    public async Task A_revocation_during_the_read_wins_over_the_snapshot()
    {
        await PreparePosting();
        var receipt = await ReceiveDocPosted(Avg, 5m, 50m, "2026-03-10");
        var member = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        Assert.True((await Read(Svc(), member.User.Id, receipt)).IsOk);
        var svc = Svc();
        svc.BeforeFinalAuthorization = async _ => Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, member.User.Id)).IsOk);
        Reject(await Read(svc, member.User.Id, receipt), ConfigStatus.Forbidden, "forbidden");
    }

    private async Task<StockPostingView> ReceiveDocPosted(ItemView item, decimal qty, decimal amount, string date)
    {
        var p = Ok(await Post(await Approved(Receipt(W1, date, L(item, Pcs, qty))), Amounts(amount)));
        await PostJournal(p); return p;
    }
    private async Task<StockPostingView> IssueDocPosted(ItemView item, decimal qty, string date)
    {
        var p = Ok(await Post(await Approved(Issue(W1, date, L(item, Pcs, qty)))));
        await PostJournal(p); return p;
    }
}
