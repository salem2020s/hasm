using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Parties;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.13 slice S1a, isolation and authorization (SRS 19, scenario 21.3): another company's identity or session never reaches a document, and each permission authorizes exactly its own operation. Disposable cluster only.</summary>
public class CommercialIsolationTests : CommercialTestBase
{
    [PostgresFact]
    public async Task A_user_of_company_A_never_reads_or_writes_a_document_of_a_real_company_B_even_with_a_shared_party_identity()
    {
        await Prepare();
        // company A
        var branchA = await NewBranchId("BA"); var partyA = await NewCustomerId("C1"); var (itemA, unitA) = await NewItemAndUnit("IS1");
        var docA = await NewDraft(branchA, partyA, itemA, unitA, "iso-key-00001");
        // a REAL second company with its own database, branch, item, and a party that SHARES the global identity of company A's party
        var b = await ReadyCompany();
        var resolverB = SinglePrincipalResolver(b);
        var txB = new CompanyAuditedTransactions(resolverB, Audit, Clock);
        var docsB = new CommercialDocumentService(resolverB, txB, Clock, new CommercialDocumentTypeRegistry([CommercialTestType.Name]), new Hasm.Infrastructure.Pricing.PricingConfigService(resolverB, txB, Clock), new Hasm.Infrastructure.Taxes.TaxConfigService(resolverB, txB, Clock));
        var invB = new CompanyInventoryService(resolverB, txB, Clock);
        var rolesB = new PartyRoleService(resolverB, txB, Clock);
        var global = Guid.NewGuid();
        var sharedA = Guid.NewGuid();                                                                                            // company A's own record of the SAME shared identity (link state 2 = linked, exactly when a global id is set)
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.GlobalPartyId}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{sharedA}', 'C1S', 'p', NULL, true, 1, 1, '{Admin.Id}', now(), now())");
        await Sql($"UPDATE {LP.Table} SET {LP.GlobalPartyId} = '{global}', {LP.LinkState} = 2, {LP.Revision} = {LP.Revision} + 1 WHERE {LP.Id} = '{sharedA}'");   // a party is created pending and linked afterwards
        Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(sharedA, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null)));
        var branchB = Guid.NewGuid(); var partyB = Guid.NewGuid();
        await Sql($"INSERT INTO {Br.Table} ({Br.Id}, {Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('{branchB}', 'BB', 'BB', true, false, 1, 1, now(), now())", b);
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.GlobalPartyId}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{partyB}', 'C1', 'p', NULL, true, 1, 1, '{Admin.Id}', now(), now())", b);
        await Sql($"UPDATE {LP.Table} SET {LP.GlobalPartyId} = '{global}', {LP.LinkState} = 2, {LP.Revision} = {LP.Revision} + 1 WHERE {LP.Id} = '{partyB}'", b);
        Ok(await rolesB.CreateRoleAsync(Admin.Id, b.Id, new PartyRoleInput(partyB, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null)));
        var unitB = Ok(await invB.CreateUnitAsync(Admin.Id, b.Id, new UnitInput("PCS", "وحدة", "Piece", 0)));
        var groupB = Ok(await invB.CreateGroupAsync(Admin.Id, b.Id, new ItemGroupInput("G1", "مجموعة", "Group", null, 1)));
        var itemB = Ok(await invB.CreateItemAsync(Admin.Id, b.Id, new ItemInput("I1", "صنف", "Item", 1, groupB.Id, unitB.Id, null, null, false, false, false, null, [])));
        var docB = Ok(await docsB.CreateAsync(Admin.Id, b.Id, Doc(branchB, partyB, itemB.Id, unitB.Id, "iso-key-00002")));

        // reads through company A never reach company B's document (the ids do not exist in A's database)
        Fails(await DocMgmt.GetAsync(Admin.Id, Company.Id, docB.Id), ConfigStatus.NotFound, "commercial_document_not_found");
        Assert.Equal([docA.Id], Ok(await DocMgmt.ListAsync(Admin.Id, Company.Id, new CommercialDocumentFilter())).Select(d => d.Id));
        // an administrator of A holds nothing in company B
        Fails(await DocMgmt.GetAsync(Admin.Id, b.Id, docB.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.ListAsync(Admin.Id, b.Id, new CommercialDocumentFilter()), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.CreateAsync(Admin.Id, b.Id, Doc(branchB, partyB, itemB.Id, unitB.Id, "iso-key-00003")), ConfigStatus.Forbidden, "forbidden");
        // writes through company A never touch company B's document
        Fails(await DocMgmt.UpdateAsync(Admin.Id, Company.Id, docB.Id, 1, Doc(branchA, partyA, itemA, unitA, "ignored-key-xx", qty: 9m)), ConfigStatus.NotFound, "commercial_document_not_found");
        Fails(await DocMgmt.SubmitAsync(Admin.Id, Company.Id, docB.Id, 1), ConfigStatus.NotFound, "commercial_document_not_found");
        Fails(await DocMgmt.CancelAsync(Admin.Id, Company.Id, docB.Id, 1, "x"), ConfigStatus.NotFound, "commercial_document_not_found");
        // company B's branch, party (the SAME global identity) and item are unknown to company A
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branchB, partyA, itemA, unitA, "iso-key-00004")), "branchId", "branch_not_found");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branchA, partyB, itemA, unitA, "iso-key-00005")), "partyId", "party_role_required");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branchA, partyA, itemB.Id, unitB.Id, "iso-key-00006")), "lines", "item_not_found");
        Assert.Equal(1L, await DocCount());
        Assert.Equal(1L, await Count(b, CompanySchema.CommercialDocument.Table));
        var intactB = (await docsB.GetAsync(b.Id, docB.Id))!;
        Assert.Equal(CommercialStatus.Draft, intactB.Status); Assert.Equal(1L, intactB.Revision);                          // nothing of company B was touched
        Assert.Equal(CommercialStatus.Draft, (await Docs.GetAsync(Company.Id, docA.Id))!.Status);
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

        await OnlyHolderMay(PermissionCatalog.Codes.CommercialCreate, async u => (await DocMgmt.CreateAsync(u, Company.Id, Doc(branch, party, item, unit, "matrix-key-0001"))).IsOk);
        Assert.Equal(1L, await DocCount());                                                                                    // only the holder created
        var doc = (await Docs.ListAsync(Company.Id, new CommercialDocumentFilter())).Single();
        // a REPLAY by a caller who may create but not view returns only the identifiers: never the party, note, lines or history
        var replay = Ok(await DocMgmt.CreateAsync(holders[PermissionCatalog.Codes.CommercialCreate], Company.Id, Doc(branch, party, item, unit, "matrix-key-0001")));
        Assert.True(replay.Replayed);
        Assert.Equal(doc.Id, replay.Id);
        Assert.Equal(Guid.Empty, replay.PartyId); Assert.Null(replay.Note); Assert.Empty(replay.Lines); Assert.Empty(replay.Events);
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialView, async u => (await DocMgmt.GetAsync(u, Company.Id, doc.Id)).IsOk);
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialUpdate, async u => (await DocMgmt.UpdateAsync(u, Company.Id, doc.Id, 1, Doc(branch, party, item, unit, "ignored-key-xx", qty: 3m))).IsOk);
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialSubmit, async u => (await DocMgmt.SubmitAsync(u, Company.Id, doc.Id, 2)).IsOk);
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialSubmit, async u => (await DocMgmt.WithdrawAsync(u, Company.Id, doc.Id, 3)).IsOk);   // withdrawal needs the submit permission
        await OnlyHolderMay(PermissionCatalog.Codes.CommercialCancel, async u => (await DocMgmt.CancelAsync(u, Company.Id, doc.Id, 4, "matrix")).IsOk);
        var final = (await Docs.GetAsync(Company.Id, doc.Id))!;
        Assert.Equal(CommercialStatus.Cancelled, final.Status); Assert.Equal(5L, final.Revision);
        Assert.Equal(5, final.Events.Count);                                                                                    // refused operations left no event: created, updated, submitted, withdrawn, cancelled
    }
}
