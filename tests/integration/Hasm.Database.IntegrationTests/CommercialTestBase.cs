using Microsoft.Extensions.DependencyInjection;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Parties;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentEvent;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>Shared fixtures of the Phase 3.13 S1a tests (no tests here). The internal test document type is registered ONLY in this test composition.</summary>
public abstract class CommercialTestBase : InventoryTestBase
{
    protected CommercialDocumentService Docs = null!;
    protected CommercialDocumentManagementService DocMgmt = null!;
    protected PartyRoleService Roles = null!;
    protected static readonly DateOnly Day = new(2026, 6, 15);

    protected static void RegisterTestType(IServiceCollection s) => s.AddSingleton<ICommercialDocumentTypeRegistry>(new CommercialDocumentTypeRegistry([CommercialTestType.Name]));

    protected async Task Prepare()
    {
        await PrepareInventory();
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Docs = new CommercialDocumentService(Resolver, tx, Clock, new CommercialDocumentTypeRegistry([CommercialTestType.Name]));
        DocMgmt = new CommercialDocumentManagementService(Rbac, Docs);
        Roles = new PartyRoleService(Resolver, tx, Clock);
    }

    protected Task<long> Events(int type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");

    protected async Task<Guid> NewBranchId(string code)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {Br.Table} ({Br.Id}, {Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('{id}', '{code}', '{code}', true, false, 1, 1, now(), now())");
        return id;
    }

    protected async Task<Guid> NewCustomerId(string code)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{id}', '{code}', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
        Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(id, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null)));
        return id;
    }

    protected async Task<(Guid Item, Guid Unit)> NewItemAndUnit(string code)
    {
        var u = await Unit("PCS-" + code); var g = await Group("G-" + code, valuation: 1);
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, u.Id, code)));
        return (item.Id, u.Id);
    }

    protected static CommercialDocumentInput Doc(Guid branch, Guid party, Guid item, Guid unit, string key, decimal qty = 2m, string? note = null, int mode = 1) =>
        new(CommercialTestType.Name, branch, party, "jod", Day, mode, note, key, [new CommercialLineInput(item, unit, qty, null)]);

    protected async Task<CommercialDocumentView> NewDraft(Guid branch, Guid party, Guid item, Guid unit, string key, decimal qty = 2m) =>
        Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, key, qty)));

    protected Task<long> DocCount() => Sql<long>($"SELECT count(*) FROM {CD.Table}");
}
