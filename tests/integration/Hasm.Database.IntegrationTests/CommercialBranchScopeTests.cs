using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.13 slice S1a, branch-scoped permissions like the journal (SRS 3.5, 4.2): a grant for branch A never reaches branch B documents; an invisible document is never revealed; lists only walk visible branches. Disposable cluster only.</summary>
public class CommercialBranchScopeTests : CommercialTestBase
{
    private static readonly string[] All = [PermissionCatalog.Codes.CommercialView, PermissionCatalog.Codes.CommercialCreate, PermissionCatalog.Codes.CommercialUpdate, PermissionCatalog.Codes.CommercialSubmit, PermissionCatalog.Codes.CommercialCancel];

    [PostgresFact]
    public async Task A_grant_for_one_branch_never_reaches_the_documents_of_another_and_an_invisible_document_is_never_revealed()
    {
        await Prepare();
        var a = await NewBranchId("BA"); var b = await NewBranchId("BB"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("BS1");
        var docA = await NewDraft(a, party, item, unit, "scope-key-0001");
        var docB = await NewDraft(b, party, item, unit, "scope-key-0002");
        var (inA, _) = await Member(All, ScopeKind.Branch, a);
        var (viewerA, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Branch, a);
        var (company, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Company, null);
        var (nobody, _) = await Member([PermissionCatalog.Codes.ItemsView], ScopeKind.Company, null);

        Assert.Equal(docA.Id, Ok(await DocMgmt.GetAsync(viewerA.Id, Company.Id, docA.Id)).Id);
        Fails(await DocMgmt.GetAsync(viewerA.Id, Company.Id, docB.Id), ConfigStatus.Forbidden, "forbidden");                         // another branch
        Fails(await DocMgmt.GetAsync(viewerA.Id, Company.Id, Guid.NewGuid()), ConfigStatus.Forbidden, "forbidden");                  // the same answer for a document that does not exist: nothing is revealed
        Fails(await DocMgmt.GetAsync(company.Id, Company.Id, Guid.NewGuid()), ConfigStatus.NotFound, "commercial_document_not_found"); // only a company-wide viewer is told it does not exist
        Assert.Equal(docB.Id, Ok(await DocMgmt.GetAsync(company.Id, Company.Id, docB.Id)).Id);
        Fails(await DocMgmt.GetAsync(nobody.Id, Company.Id, docA.Id), ConfigStatus.Forbidden, "forbidden");

        var listA = Ok(await DocMgmt.ListAsync(viewerA.Id, Company.Id, new CommercialDocumentFilter()));
        Assert.Equal([docA.Id], listA.Select(d => d.Id));                                                                           // the restriction runs inside the query
        Assert.Equal(2, Ok(await DocMgmt.ListAsync(company.Id, Company.Id, new CommercialDocumentFilter())).Count);
        Assert.Equal([docB.Id], Ok(await DocMgmt.ListAsync(company.Id, Company.Id, new CommercialDocumentFilter(BranchId: b))).Select(d => d.Id));
        Fails(await DocMgmt.ListAsync(nobody.Id, Company.Id, new CommercialDocumentFilter()), ConfigStatus.Forbidden, "forbidden");

        // create / update / submit / withdraw / cancel are branch-scoped too
        var created = Ok(await DocMgmt.CreateAsync(inA.Id, Company.Id, Doc(a, party, item, unit, "scope-key-0003")));
        Fails(await DocMgmt.CreateAsync(inA.Id, Company.Id, Doc(b, party, item, unit, "scope-key-0004")), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.CreateAsync(viewerA.Id, Company.Id, Doc(a, party, item, unit, "scope-key-0005")), ConfigStatus.Forbidden, "forbidden");   // view alone does not create
        Fails(await DocMgmt.CreateAsync(inA.Id, Company.Id, Doc(a, party, item, unit, "scope-key-0006") with { BranchId = null }), ConfigStatus.Invalid, "validation_failed");
        Fails(await DocMgmt.UpdateAsync(inA.Id, Company.Id, docB.Id, 1, Doc(b, party, item, unit, "ignored-key-xx", qty: 7m)), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.SubmitAsync(inA.Id, Company.Id, docB.Id, 1), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.CancelAsync(inA.Id, Company.Id, docB.Id, 1, "not mine"), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(CommercialStatus.Draft, (await Docs.GetAsync(Company.Id, docB.Id))!.Status);                                  // nothing changed
        Assert.Equal(CommercialStatus.Submitted, Ok(await DocMgmt.SubmitAsync(inA.Id, Company.Id, created.Id, 1)).Status);
        Assert.Equal(CommercialStatus.Draft, Ok(await DocMgmt.WithdrawAsync(inA.Id, Company.Id, created.Id, 2)).Status);
        Assert.Equal(CommercialStatus.Cancelled, Ok(await DocMgmt.CancelAsync(inA.Id, Company.Id, created.Id, 3, "test")).Status);
        Assert.Equal(CommercialStatus.Draft, Ok(await DocMgmt.UpdateAsync(Admin.Id, Company.Id, docB.Id, 1, Doc(b, party, item, unit, "ignored-key-xx", qty: 4m))).Status);   // the administrator holds every branch
    }
}
