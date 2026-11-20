using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Parties;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRole;
using SR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.SalesRepresentative;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.9B (company migration 0023, system migration 0025) against real PostgreSQL: customer and supplier roles of local parties, terms, credit-limit data, control-account mapping validated
/// against the chart, sales representatives, audit, revision fences, no deletion, authorization. Disposable cluster only.
/// </summary>
public class PartyRoleTests : InventoryTestBase
{
    private PartyRoleService Roles = null!;
    private PartyRoleManagementService RolesMgmt = null!;

    private async Task Prepare()
    {
        await PrepareInventory();
        BuildChartFacade();
        Roles = new PartyRoleService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        RolesMgmt = new PartyRoleManagementService(Rbac, Roles);
    }

    private Task<long> RoleEvents() => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1500 AND 1509");
    private async Task<Guid> NewParty(string code, bool active = true)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{id}', '{code}', 'p-{code}', {(active ? "true" : "false")}, 1, 1, '{Admin.Id}', now(), now())");
        return id;
    }
    private async Task<AccountView> Acc(string code, string type, bool group = false, bool deactivate = false)
    {
        var a = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput(code, null, "حساب " + code, "Account " + code, type, null, group, null, null)));
        return deactivate ? Ok(await ChartDomain.DeactivateAsync(Admin.Id, Company.Id, a.Id, a.Revision)) : a;
    }
    private static PartyRoleInput Customer(Guid party, Guid? account = null, Guid? rep = null, PartyPaymentMode mode = PartyPaymentMode.Credit, int? days = 30, decimal? limit = null) =>
        new(party, PartyRoleKind.Customer, mode, mode == PartyPaymentMode.Cash ? null : days, limit, account, rep);

    [PostgresFact]
    public async Task A_new_company_has_empty_tables_and_only_the_company_administrator_holds_the_permissions()
    {
        await Prepare();
        foreach (var t in new[] { PR.Table, SR.Table }) Assert.Equal(0L, await Count(Company, t));
        Assert.Empty(Ok(await RolesMgmt.ListRolesAsync(Admin.Id, Company.Id, null)));
        Assert.Empty(Ok(await RolesMgmt.ListRepresentativesAsync(Admin.Id, Company.Id)));
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var p in new[] { "company.partyroles.view", "company.partyroles.configure", "company.salesreps.view", "company.salesreps.configure" }) Assert.Contains(p, admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.partyroles.") || p.StartsWith("company.salesreps."));
        Assert.Equal(new PartyRoleCapabilities(true, true, true, true), await RolesMgmt.CapabilitiesAsync(Admin.Id, Company.Id));
    }

    [PostgresFact]
    public async Task Customer_and_supplier_roles_validate_terms_and_the_control_account_against_the_chart_and_are_audited()
    {
        await Prepare();
        var party = await NewParty("P1");
        var receivable = await Acc("1201", "asset");
        var payable = await Acc("2101", "liability");
        var group = await Acc("1", "asset", group: true);
        var dead = await Acc("1202", "asset", deactivate: true);
        var revenue = await Acc("4101", "revenue");
        var before = await RoleEvents();

        var customer = Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(party, receivable.Id, limit: 5000.5m)));
        Assert.Equal((PartyRoleKind.Customer, PartyPaymentMode.Credit, 30, 5000.5m, receivable.Id, 1L, true), (customer.Role, customer.PaymentMode, customer.TermDays, customer.CreditLimit, customer.ControlAccountId, customer.Revision, customer.IsActive));
        Assert.Equal("1201", customer.ControlAccountCode);
        var supplier = Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(party, PartyRoleKind.Supplier, PartyPaymentMode.Cash, null, null, payable.Id, null)));   // one party can be both
        Assert.Equal(2, (await Roles.ListRolesAsync(Company.Id, null)).Count);
        Assert.Single(await Roles.ListRolesAsync(Company.Id, PartyRoleKind.Supplier));
        Assert.Equal(2L, await RoleEvents() - before);

        Fails(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(party)), ConfigStatus.Conflict, "role_exists");
        // the control account must suit the kind, be postable and be active
        var other = await NewParty("P2");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, payable.Id)), "controlAccountId", "customer_account_must_be_asset");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(other, PartyRoleKind.Supplier, PartyPaymentMode.Credit, 10, null, receivable.Id, null)), "controlAccountId", "supplier_account_must_be_liability");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, group.Id)), "controlAccountId", "account_not_postable");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, dead.Id)), "controlAccountId", "account_inactive");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, revenue.Id)), "controlAccountId", "customer_account_must_be_asset");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, Guid.NewGuid())), "controlAccountId", "account_not_found");
        // terms
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, days: null)), "termDays", "required");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, days: 4000)), "termDays", "term_days_invalid");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(other, PartyRoleKind.Customer, PartyPaymentMode.Cash, 15, null, null, null)), "termDays", "terms_not_allowed_for_cash");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, limit: -1m)), "creditLimit", "credit_limit_invalid");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, limit: 1.23456m)), "creditLimit", "credit_limit_invalid");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(other, PartyRoleKind.Customer, null, 10, null, null, null)), "paymentMode", "payment_mode_invalid");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(null, PartyRoleKind.Customer, PartyPaymentMode.Credit, 10, null, null, null)), "partyId", "required");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(other, null, PartyPaymentMode.Credit, 10, null, null, null)), "role", "role_invalid");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(Guid.NewGuid())), "partyId", "party_not_found");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(await NewParty("OFF", active: false))), "partyId", "party_inactive");
        Assert.Equal(2L, await Count(Company, PR.Table));                                                                       // every refusal wrote nothing
        Assert.Equal(2L, await RoleEvents() - before);

        // update: revision fence, immutable party and kind, previous terms audited, an omitted flag never reactivates
        Fails(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, customer.Id, null, Customer(party, receivable.Id)), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, customer.Id, 9, Customer(party, receivable.Id)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, customer.Id, 1, Customer(other, receivable.Id)), "partyId", "party_immutable");
        Invalid(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, customer.Id, 1, new PartyRoleInput(null, PartyRoleKind.Supplier, PartyPaymentMode.Credit, 30, null, null, null)), "role", "role_immutable");
        var updated = Ok(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, customer.Id, 1, Customer(party, receivable.Id, days: 60, limit: 7000m) with { IsActive = false }));
        Assert.Equal((2L, 60, 7000m, false), (updated.Revision, updated.TermDays, updated.CreditLimit, updated.IsActive));
        var same = Ok(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, customer.Id, 2, Customer(party, receivable.Id, days: 60, limit: 7000m)));
        Assert.False(same.IsActive);                                                                                            // omitted flag
        var cleared = Ok(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, customer.Id, 3, Customer(party, null, limit: null)));
        Assert.Equal((null, null), (cleared.CreditLimit, cleared.ControlAccountId));                                             // a null clears the mapping
        var detail = await Sql<string>($"SELECT {Audit_.Detail} FROM {Audit_.Table} WHERE {Audit_.EventType} = {PartyRoleAuditEvents.RoleUpdated} ORDER BY {Audit_.Seq} DESC LIMIT 1");
        Assert.Contains("was:", detail);
        Assert.Contains("limit=7000", detail);
        Assert.Equal(supplier.Id, (await Roles.ListRolesAsync(Company.Id, PartyRoleKind.Supplier)).Single().Id);
    }

    [PostgresFact]
    public async Task Sales_representatives_are_unique_revision_fenced_never_removed_and_only_active_ones_serve_customers()
    {
        await Prepare();
        var userA = Guid.NewGuid();
        var rep = Ok(await Roles.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("rep1", "مندوب", "Rep One", userA)));
        Assert.Equal(("REP1", true, 1L, userA), (rep.Code, rep.IsActive, rep.Revision, rep.UserId));
        Invalid(await Roles.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("REP1", "مكرر", null, null)), "code", "code_taken");
        Invalid(await Roles.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("REP2", "آخر", null, userA)), "userId", "user_taken");
        Invalid(await Roles.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("bad code", "x", null, null)), "code", "code_invalid");
        Invalid(await Roles.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("REP3", " ", null, null)), "nameAr", "name_invalid");
        Fails(await Roles.UpdateRepresentativeAsync(Admin.Id, Company.Id, rep.Id, null, new SalesRepresentativeInput(null, "مندوب", null, userA)), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Roles.UpdateRepresentativeAsync(Admin.Id, Company.Id, rep.Id, 5, new SalesRepresentativeInput(null, "مندوب", null, userA)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await Roles.UpdateRepresentativeAsync(Admin.Id, Company.Id, rep.Id, 1, new SalesRepresentativeInput("OTHER", "مندوب", null, userA)), "code", "code_immutable");

        var party = await NewParty("C1");
        var customer = Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(party, rep: rep.Id)));
        Assert.Equal(rep.Id, customer.RepresentativeId);
        // a supplier never has a representative
        var supplierParty = await NewParty("S1");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(supplierParty, PartyRoleKind.Supplier, PartyPaymentMode.Credit, 30, null, null, rep.Id)), "representativeId", "representative_not_allowed");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(await NewParty("C2"), rep: Guid.NewGuid())), "representativeId", "representative_not_found");

        var off = Ok(await Roles.UpdateRepresentativeAsync(Admin.Id, Company.Id, rep.Id, 1, new SalesRepresentativeInput(null, "مندوب", null, userA, false)));
        Assert.False(off.IsActive);
        var still = Ok(await Roles.UpdateRepresentativeAsync(Admin.Id, Company.Id, rep.Id, 2, new SalesRepresentativeInput(null, "مندوب ١", "One", userA)));
        Assert.False(still.IsActive);                                                                                           // omitted flag keeps it off
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(await NewParty("C3"), rep: rep.Id)), "representativeId", "representative_inactive");
        Assert.Equal(rep.Id, (await Roles.ListRolesAsync(Company.Id, PartyRoleKind.Customer)).Single().RepresentativeId);          // an existing assignment is untouched
    }

    [PostgresFact]
    public async Task PostgreSQL_guards_the_roles_even_for_a_direct_write_nothing_is_deleted_and_revisions_advance_by_one()
    {
        await Prepare();
        var party = await NewParty("G1");
        var asset = await Acc("1301", "asset");
        var liability = await Acc("2301", "liability");
        var role = Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(party, asset.Id)));
        var rep = Ok(await Roles.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("G", "ج", null, null)));
        Assert.Equal("P0001", await Fails($"DELETE FROM {PR.Table}"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {SR.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {PR.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {PR.Table} SET {PR.TermDays} = 5"));                                                                    // revision did not advance
        Assert.Equal("P0001", await Fails($"UPDATE {PR.Table} SET {PR.Role} = 2, {PR.Revision} = {PR.Revision} + 1"));                                       // kind is immutable
        Assert.Equal("P0001", await Fails($"UPDATE {PR.Table} SET {PR.ControlAccountId} = '{liability.Id}', {PR.Revision} = {PR.Revision} + 1"));          // wrong account type for a customer
        Assert.Equal("P0001", await Fails($"UPDATE {SR.Table} SET {SR.Code} = 'X', {SR.Revision} = {SR.Revision} + 1"));
        Assert.Equal("23514", await Fails($"UPDATE {PR.Table} SET {PR.TermDays} = 99999, {PR.Revision} = {PR.Revision} + 1"));
        Assert.Equal("23514", await Fails($"UPDATE {PR.Table} SET {PR.CreditLimit} = -1, {PR.Revision} = {PR.Revision} + 1"));
        Assert.Equal("23514", await Fails($"UPDATE {PR.Table} SET {PR.Role} = 1, {PR.PaymentMode} = 9, {PR.Revision} = {PR.Revision} + 1"));
        Assert.Equal(1L, await Count(Company, PR.Table));
        Assert.Equal(role.Id, (await Roles.ListRolesAsync(Company.Id, null)).Single().Id);
        Assert.Equal(rep.Id, (await Roles.ListRepresentativesAsync(Company.Id)).Single().Id);
    }

    [PostgresFact]
    public async Task Without_the_permissions_nothing_is_read_or_written_and_nothing_is_audited()
    {
        await Prepare();
        var outsider = await NewUser("roleoutsider");
        var party = await NewParty("X1");
        var before = await RoleEvents();
        Fails(await RolesMgmt.ListRolesAsync(outsider.Id, Company.Id, null), ConfigStatus.Forbidden, "forbidden");
        Fails(await RolesMgmt.ListRepresentativesAsync(outsider.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await RolesMgmt.CreateRoleAsync(outsider.Id, Company.Id, Customer(party)), ConfigStatus.Forbidden, "forbidden");
        Fails(await RolesMgmt.CreateRepresentativeAsync(outsider.Id, Company.Id, new SalesRepresentativeInput("R", "م", null, null)), ConfigStatus.Forbidden, "forbidden");
        Fails(await RolesMgmt.UpdateRoleAsync(outsider.Id, Company.Id, Guid.NewGuid(), 1, Customer(party)), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(new PartyRoleCapabilities(false, false, false, false), await RolesMgmt.CapabilitiesAsync(outsider.Id, Company.Id));
        Assert.Equal((0L, 0L, before), (await Count(Company, PR.Table), await Count(Company, SR.Table), await RoleEvents()));
    }

    [PostgresFact]
    public async Task Concurrent_creation_of_the_same_role_yields_exactly_one()
    {
        await Prepare();
        var party = await NewParty("RACE");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(party)))));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal("role_exists", r.Code));
        Assert.Equal(1L, await Count(Company, PR.Table));
    }
}
