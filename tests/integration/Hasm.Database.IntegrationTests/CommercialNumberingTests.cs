using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CDA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentApproval;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.13 slice S1b, numbering (SRS 7.6, owner decision D-050): the official number is issued ONCE at the first approval inside the approval transaction, kept through re-approval, voided (and never reissued) on
/// cancel; concurrent approvals receive distinct gapless numbers. The numbering RULE of a commercial type cannot be configured yet (the built-in type registry knows only the existing types), so the default rule applies.
/// Disposable cluster only.
/// </summary>
public class CommercialNumberingTests : CommercialTestBase
{
    private Task<Guid> Approver() => NewApprover();

    private async Task<CommercialDocumentView> Submitted(Guid branch, Guid party, Guid item, Guid unit, string key, decimal qty = 2m)
    {
        var d = await NewDraft(branch, party, item, unit, key, qty);
        return Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
    }

    private Task<long> VoidedNumbers() => Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.VoidedAtUtc} IS NOT NULL");

    [PostgresFact]
    public async Task The_number_is_issued_once_at_first_approval_kept_through_reapproval_and_voided_never_reused_on_cancel()
    {
        await Prepare();
        var approver = await Approver();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("NU1");
        var s1 = await Submitted(branch, party, item, unit, "num-key-00001");
        Assert.Null(s1.OfficialNumber);                                                                                         // no number before the approval
        var a1 = Ok(await Docs.ApproveAsync(approver, Company.Id, s1.Id, s1.Revision));
        Assert.Matches(new Regex(@"^2026-\d{6}$"), a1.OfficialNumber!);                                                         // the default rule: fiscal-year label and a zero-padded sequence
        Assert.Equal("2026-000001", a1.OfficialNumber);
        Assert.NotNull(a1.SealedAtUtc);
        var s2 = await Submitted(branch, party, item, unit, "num-key-00002");
        var a2 = Ok(await Docs.ApproveAsync(approver, Company.Id, s2.Id, s2.Revision));
        Assert.Equal("2026-000002", a2.OfficialNumber);                                                                         // the next document gets the next number

        // a refused approval consumes nothing: the changed price is detected BEFORE a number is issued
        var s3 = await Submitted(branch, party, item, unit, "num-key-00003");
        Ok(await Pricing.UpdateEntryAsync(Admin.Id, Company.Id, Prices[item].Entry, 1, new Hasm.Infrastructure.Pricing.PriceEntryInput(null, null, null, null, 11m, null)));
        Fails(await Docs.ApproveAsync(approver, Company.Id, s3.Id, s3.Revision), ConfigStatus.Conflict, "pricing_changed");
        Assert.Null((await Docs.GetAsync(Company.Id, s3.Id))!.OfficialNumber);
        var back = Ok(await Docs.WithdrawAsync(Admin.Id, Company.Id, s3.Id, s3.Revision));
        var resubmitted = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, s3.Id, back.Revision));
        var a3 = Ok(await Docs.ApproveAsync(approver, Company.Id, s3.Id, resubmitted.Revision));
        Assert.Equal("2026-000003", a3.OfficialNumber);                                                                         // no gap after the refused approval

        // re-approval: a material edit sends the document back, its number is KEPT and no new number is issued
        var edited = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, a1.Id, a1.Revision, Doc(branch, party, item, unit, "ignored-key-xx", qty: 5m)));
        Assert.Equal(CommercialStatus.Draft, edited.Status);
        Assert.Equal("2026-000001", edited.OfficialNumber);
        var again = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, a1.Id, edited.Revision));
        var re = Ok(await Docs.ApproveAsync(approver, Company.Id, a1.Id, again.Revision));
        Assert.Equal("2026-000001", re.OfficialNumber);

        // cancel: the number is voided and never issued again
        Assert.Equal(0L, await VoidedNumbers());
        var cancelled = Ok(await Docs.CancelAsync(Admin.Id, Company.Id, a2.Id, a2.Revision, "customer withdrew"));
        Assert.Equal(CommercialStatus.Cancelled, cancelled.Status);
        Assert.Equal("2026-000002", cancelled.OfficialNumber);                                                                  // the cancelled document keeps its (void) number
        Assert.Equal(1L, await VoidedNumbers());
        var s4 = await Submitted(branch, party, item, unit, "num-key-00004");
        var a4 = Ok(await Docs.ApproveAsync(approver, Company.Id, s4.Id, s4.Revision));
        Assert.Equal("2026-000004", a4.OfficialNumber);                                                                         // 000002 is not reused
        // a document returned to draft by re-approval still holds its number: cancelling it voids that number too
        var reopened = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, a3.Id, a3.Revision, Doc(branch, party, item, unit, "ignored-key-xx", qty: 9m)));
        Ok(await Docs.CancelAsync(Admin.Id, Company.Id, a3.Id, reopened.Revision, "cancelled while awaiting re-approval"));
        Assert.Equal(2L, await VoidedNumbers());
        // the number never changes or disappears, and is issued only by an approval (PostgreSQL)
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CDA.OfficialNumber} = 'X-1', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{a4.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CDA.OfficialNumber} = NULL, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{a4.Id}'"));
    }

    [PostgresFact]
    public async Task Concurrent_approvals_receive_distinct_gapless_numbers_and_a_repeated_approval_changes_nothing()
    {
        await Prepare();
        var approver = await Approver();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("NU2");
        var docs = new List<CommercialDocumentView>();
        for (var i = 0; i < 20; i++) docs.Add(await Submitted(branch, party, item, unit, $"num-race-{i:000}"));
        var results = await Task.WhenAll(docs.Select(d => Docs.ApproveAsync(approver, Company.Id, d.Id, d.Revision)));
        Assert.All(results, r => Assert.True(r.IsOk, $"{r.Status} {r.Code}"));
        var numbers = results.Select(r => r.Value!.OfficialNumber!).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(n => $"2026-{n:000000}"), numbers);                                        // distinct and gapless
        Assert.Equal(20L, await Sql<long>($"SELECT count(*) FROM {NI.Table}"));
        // the same document approved by two requests at once: exactly one approval, one number, the other is refused
        var one = await Submitted(branch, party, item, unit, "num-race-single");
        var twice = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Docs.ApproveAsync(approver, Company.Id, one.Id, one.Revision)));
        Assert.Equal(1, twice.Count(r => r.IsOk));
        Assert.Equal(3, twice.Count(r => !r.IsOk));
        Assert.Equal(21L, await Sql<long>($"SELECT count(*) FROM {NI.Table}"));
    }
}
