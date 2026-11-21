using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.13 slice S1a, isolation and authorization (SRS 19, scenario 21.3): another company's identity or session never reaches a document, and each permission authorizes exactly its own operation. Disposable cluster only.</summary>
public class CommercialIsolationTests : CommercialTestBase
{
    [PostgresFact]
    public async Task A_user_of_company_A_never_reads_or_writes_through_another_company()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("IS1");
        var doc = await NewDraft(branch, party, item, unit, "iso-key-00001");
        var other = Guid.NewGuid();                                                                                         // another company: the administrator of company A holds nothing there
        Fails(await DocMgmt.GetAsync(Admin.Id, other, doc.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.ListAsync(Admin.Id, other, new CommercialDocumentFilter()), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.CreateAsync(Admin.Id, other, Doc(branch, party, item, unit, "iso-key-00002")), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.UpdateAsync(Admin.Id, other, doc.Id, 1, Doc(branch, party, item, unit, "ignored-key-xx")), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.SubmitAsync(Admin.Id, other, doc.Id, 1), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.CancelAsync(Admin.Id, other, doc.Id, 1, "x"), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(1L, await DocCount());
        var intact = (await Docs.GetAsync(Company.Id, doc.Id))!;
        Assert.Equal((CommercialStatus.Draft, 1L), (intact.Status, intact.Revision));                                          // nothing was touched
        Assert.Empty(intact.Events.Where(e => e.Kind != CommercialEventKind.Created));
    }

    [PostgresFact]
    public async Task Each_permission_authorizes_exactly_its_own_operation_and_nothing_is_written_by_a_refused_one()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("IS2");
        var holders = new Dictionary<string, Guid>();
        foreach (var code in new[] { PermissionCatalog.Codes.CommercialView, PermissionCatalog.Codes.CommercialCreate, PermissionCatalog.Codes.CommercialUpdate, PermissionCatalog.Codes.CommercialSubmit, PermissionCatalog.Codes.CommercialCancel })
            holders[code] = (await Member([code], ScopeKind.Company, null)).User.Id;

        async Task OnlyHolderMay(string code, Func<Guid, Task<bool>> attempt)
        {
            foreach (var (c, user) in holders.Where(h => h.Key != code).Select(h => (h.Key, h.Value)))
                Assert.False(await attempt(user), $"{c} must not authorize the operation of {code}");
            Assert.True(await attempt(holders[code]), $"{code} must authorize its own operation");
        }

        long created = 0;
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialCreate, async u => { var r = await DocMgmt.CreateAsync(u, Company.Id, Doc(branch, party, item, unit, "matrix-key-" + created)); if (r.IsOk) created++; return r.IsOk; });
        Assert.Equal(1L, await DocCount());                                                                                    // only the holder created (the refused attempts used distinct keys but wrote nothing)
        var doc = (await Docs.ListAsync(Company.Id, new CommercialDocumentFilter())).Single();
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialView, async u => (await DocMgmt.GetAsync(u, Company.Id, doc.Id)).IsOk);
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialUpdate, async u => (await DocMgmt.UpdateAsync(u, Company.Id, doc.Id, 1, Doc(branch, party, item, unit, "ignored-key-xx", qty: 3m))).IsOk);
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialSubmit, async u => (await DocMgmt.SubmitAsync(u, Company.Id, doc.Id, 2)).IsOk);
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialSubmit, async u => (await DocMgmt.WithdrawAsync(u, Company.Id, doc.Id, 3)).IsOk);   // withdrawal needs the submit permission
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialCancel, async u => (await DocMgmt.CancelAsync(u, Company.Id, doc.Id, 4, "matrix")).IsOk);
        var final = (await Docs.GetAsync(Company.Id, doc.Id))!;
        Assert.Equal((CommercialStatus.Cancelled, 5L), (final.Status, final.Revision));
        Assert.Equal(5, final.Events.Count);                                                                                    // refused operations left no event: created, updated, submitted, withdrawn, cancelled
    }
}
