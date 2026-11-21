using Microsoft.Extensions.DependencyInjection;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Pricing;
using Hasm.Infrastructure.Taxes;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>Shared fixtures of the Phase 3.13 S1a/S1b tests (no tests here). The internal test document type is registered ONLY in this test composition.</summary>
public abstract class CommercialTestBase : InventoryTestBase
{
    protected CommercialDocumentService Docs = null!;
    protected CommercialDocumentManagementService DocMgmt = null!;
    protected PartyRoleService Roles = null!;
    protected CompanyInventoryService CInv = null!;
    protected PricingConfigService Pricing = null!;
    protected TaxConfigService Tax = null!;
    protected ExchangeRateService Rates = null!;
    protected static readonly DateOnly Day = new(2026, 6, 15);
    /// <summary>Per item: the general price list and its entry created by <see cref="NewItemAndUnit"/> (price 10, JOD, valid from 2026-01-01).</summary>
    protected readonly Dictionary<Guid, (Guid List, Guid Entry)> Prices = [];

    protected static void RegisterTestType(IServiceCollection s) => s.AddSingleton<ICommercialDocumentTypeRegistry>(new CommercialDocumentTypeRegistry([CommercialTestType.Name]));

    /// <summary>The standard composition: pricing and tax services, a fiscal year 2026, the base currency JOD with a USD rate, and a saved tax policy (a document is refused without one).</summary>
    protected async Task Prepare()
    {
        await PrepareInventory();
        await ComposeAsync();
    }

    /// <summary>Everything after the company exists (the API test calls <c>StartApi</c> first, which prepares the company itself, and then only this).</summary>
    protected async Task ComposeAsync()
    {
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        CInv = new CompanyInventoryService(Resolver, tx, Clock);
        Pricing = new PricingConfigService(Resolver, tx, Clock);
        Tax = new TaxConfigService(Resolver, tx, Clock);
        Rates = new ExchangeRateService(Resolver, tx, Clock);
        Docs = new CommercialDocumentService(Resolver, tx, Clock, new CommercialDocumentTypeRegistry([CommercialTestType.Name]), Pricing, Tax);
        DocMgmt = new CommercialDocumentManagementService(Rbac, Docs);
        Roles = new PartyRoleService(Resolver, tx, Clock);
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        var calendar = new FiscalCalendarService(Resolver, tx, new PeriodCloseGuard(), new FinancialHistoryGuard(), Clock);
        Ok(await calendar.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        foreach (var (code, isBase) in new[] { ("JOD", true), ("USD", false) })
            await Sql($"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('{code}', {(isBase ? "true" : "false")}, true, 1, now())");
        Ok(await Rates.EnterAsync(Admin.Id, Company.Id, 0, new ExchangeRateInput("USD", 0.71m, new DateOnly(2026, 1, 1), null)));
        Ok(await Tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerLine)));
    }

    /// <summary>A company member holding the delegated Commercial Approver preset (granted offline, like the journal approver: the administrator cannot grant a permission the preset lacks).</summary>
    protected async Task<Guid> NewApprover()
    {
        var (user, _) = await Member([Hasm.Infrastructure.Access.PermissionCatalog.Codes.CommercialView], Hasm.Infrastructure.Access.ScopeKind.Company, null);
        Assert.Equal(Hasm.Infrastructure.Access.AccessService.FiscalApproverStatus.Granted, await Rbac.GrantCommercialApproverOfflineAsync(user.Login, Company.Id));
        return user.Id;
    }

    /// <summary>The same delegated approver as a user record (to sign in over HTTP).</summary>
    protected async Task<Hasm.Infrastructure.Identity.UserRecord> NewApproverUser()
    {
        var (user, _) = await Member([Hasm.Infrastructure.Access.PermissionCatalog.Codes.CommercialView], Hasm.Infrastructure.Access.ScopeKind.Company, null);
        Assert.Equal(Hasm.Infrastructure.Access.AccessService.FiscalApproverStatus.Granted, await Rbac.GrantCommercialApproverOfflineAsync(user.Login, Company.Id));
        return user;
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

    /// <summary>Creates a unit, an item group and an item, and a general JOD price list pricing the item at 10 (so the server can quote it).</summary>
    protected async Task<(Guid Item, Guid Unit)> NewItemAndUnit(string code)
    {
        var u = Ok(await CInv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("PCS-" + code, "وحدة", "Piece", 3)));
        var g = Ok(await CInv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("G-" + code, "مجموعة", "Group", null, 1)));
        var item = Ok(await CInv.CreateItemAsync(Admin.Id, Company.Id, new ItemInput(code, "صنف", "Item", 1, g.Id, u.Id, null, null, false, false, false, null, [])));
        var list = Ok(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, new PriceListInput("PL-" + code.ToUpperInvariant(), "قائمة", "List", 10, "JOD", new DateOnly(2026, 1, 1), null, true, null)));
        var entry = Ok(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, list.Id, new PriceEntryInput(item.Id, null, u.Id, 0m, 10m, null)));
        Prices[item.Id] = (list.Id, entry.Id);
        return (item.Id, u.Id);
    }

    protected static CommercialDocumentInput Doc(Guid branch, Guid party, Guid item, Guid unit, string key, decimal qty = 2m, string? note = null, int mode = 1) =>
        new(CommercialTestType.Name, branch, party, "jod", Day, mode, note, key, [new CommercialLineInput(item, unit, qty, null)]);

    protected async Task<CommercialDocumentView> NewDraft(Guid branch, Guid party, Guid item, Guid unit, string key, decimal qty = 2m) =>
        Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, key, qty)));

    protected Task<long> DocCount() => Sql<long>($"SELECT count(*) FROM {CD.Table}");
}
