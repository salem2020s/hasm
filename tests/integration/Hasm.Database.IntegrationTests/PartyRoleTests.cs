using Hasm.Infrastructure.Access;
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
    public async Task A_representative_user_link_must_be_a_member_of_the_company_but_an_unchanged_stale_link_stays_editable_and_deactivatable()
    {
        await Prepare();
        // Through the permission-checked service: a user who is not a member of the company is refused on create and on a changed link.
        var stranger = Guid.NewGuid();
        Invalid(await RolesMgmt.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("R1", "مندوب", null, stranger)), "userId", "user_not_member");
        Assert.Equal(0L, await Count(Company, SR.Table));
        var member = Ok(await RolesMgmt.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("R1", "مندوب", null, Admin.Id)));
        Assert.Equal(Admin.Id, member.UserId);
        Invalid(await RolesMgmt.UpdateRepresentativeAsync(Admin.Id, Company.Id, member.Id, member.Revision, new SalesRepresentativeInput(null, "مندوب", null, stranger)), "userId", "user_not_member");
        // No link needs no membership.
        var unlinked = Ok(await RolesMgmt.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("R2", "مندوب 2", null, null)));
        Assert.Null(unlinked.UserId);
        // A link stored earlier (the user has since left the company) is not re-checked when unchanged: the representative stays editable and deactivatable.
        var stale = Ok(await Roles.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("R3", "قديم", null, stranger)));
        var renamed = Ok(await RolesMgmt.UpdateRepresentativeAsync(Admin.Id, Company.Id, stale.Id, stale.Revision, new SalesRepresentativeInput(null, "قديم معدل", null, stranger)));
        Assert.Equal("قديم معدل", renamed.NameAr);
        var off = Ok(await RolesMgmt.UpdateRepresentativeAsync(Admin.Id, Company.Id, stale.Id, renamed.Revision, new SalesRepresentativeInput(null, "قديم معدل", null, stranger, false)));
        Assert.False(off.IsActive);
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
    public async Task The_role_search_matches_party_code_and_name_literally_pages_with_a_total_in_a_stable_order_and_keeps_the_permission()
    {
        await Prepare();
        async Task<Guid> Named(string code, string name)
        {
            var id = Guid.NewGuid();
            await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{id}', '{code}', '{name}', true, 1, 1, '{Admin.Id}', now(), now())");
            return id;
        }
        foreach (var (code, name) in new[] { ("S01", "Alpha Trading"), ("S02", "alpha Holdings"), ("S03", "Beta 100%"), ("S04", "Gamma_Co"), ("S05", "GammaXCo") })
            Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(await Named(code, name))));
        Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(await Named("S06", "Delta"), PartyRoleKind.Supplier, PartyPaymentMode.Credit, 10, null, null, null)));
        static string[] Codes(PartyRolePage p) => [.. p.Items.Select(i => i.PartyCode)];
        PartyRolePage Search(string? q, PartyRoleKind? kind = null, int? offset = null, int? limit = null) => Ok(RolesMgmt.SearchRolesAsync(Admin.Id, Company.Id, kind, q, offset, limit).Result);

        // code and name, case-insensitive, ordered by code
        Assert.Equal(["S01", "S02"], Codes(Search("alpha")));
        Assert.Equal(2L, Search("alpha").Total);
        Assert.Equal(["S01", "S02"], Codes(Search("aLPHA")));
        Assert.Equal(["S02"], Codes(Search("s02")));
        Assert.Equal(["S01", "S02"], Codes(Search("  alpha  ")));                       // trimmed
        // the LIKE wildcards are literal
        Assert.Equal(["S03"], Codes(Search("%")));
        Assert.Equal(["S04"], Codes(Search("_")));
        Assert.Equal(["S04"], Codes(Search("Gamma_")));
        Assert.Equal(["S04", "S05"], Codes(Search("gamma")));
        Assert.Empty(Search("a'; DROP TABLE x; --").Items);                             // text is a parameter, never SQL
        // kind and search combine; the total is the matching rows before paging
        Assert.Equal(["S06"], Codes(Search(null, PartyRoleKind.Supplier)));
        Assert.Equal(5L, Search(null, PartyRoleKind.Customer).Total);
        Assert.Empty(Search("delta", PartyRoleKind.Customer).Items);
        // paging: bounds, total, stable order
        Assert.Equal(["S01", "S02"], Codes(Search(null, null, 0, 2)));
        Assert.Equal(6L, Search(null, null, 0, 2).Total);
        Assert.Equal(["S05", "S06"], Codes(Search(null, null, 4, 2)));
        Assert.Empty(Search(null, null, 99, 5).Items);
        Assert.Equal(6L, Search(null, null, 99, 5).Total);
        Assert.Equal(6, Search(null).Items.Count);                                      // default page size 50
        Assert.Single(Search(null, null, 0, 0).Items);                                  // a limit below 1 is clamped to 1
        Assert.Equal(6, Search(null, null, 0, 100000).Items.Count);                     // a limit above the maximum is clamped
        Assert.Equal(["S01", "S02", "S03", "S04", "S05", "S06"], Codes(Search(null, null, 0, 6)));
        var singles = Enumerable.Range(0, 6).Select(i => Codes(Search(null, null, i, 1)).Single()).ToArray();
        Assert.Equal((await Roles.ListRolesAsync(Company.Id, null)).Select(r => r.PartyCode), singles);   // pages of one reproduce the unpaged order
        // the permission is the same as the unpaged list
        var outsider = await NewUser("searchoutsider");
        Fails(await RolesMgmt.SearchRolesAsync(outsider.Id, Company.Id, null, "alpha", null, null), ConfigStatus.Forbidden, "forbidden");
        var (viewer, _) = await Member([PermissionCatalog.Codes.PartyRolesView], ScopeKind.Company, null);
        Assert.Equal(["S01", "S02"], Codes(Ok(await RolesMgmt.SearchRolesAsync(viewer.Id, Company.Id, null, "alpha", null, null))));
    }

    [PostgresFact]
    public async Task The_role_lookup_by_party_ids_returns_only_those_parties_in_the_same_permission_and_order()
    {
        await Prepare();
        var p1 = await NewParty("L01"); var p2 = await NewParty("L02"); var p3 = await NewParty("L03"); var p4 = await NewParty("L04");
        foreach (var p in new[] { p1, p2, p3 }) Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(p)));
        Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(p2, PartyRoleKind.Supplier, PartyPaymentMode.Credit, 10, null, null, null)));   // L02 is customer AND supplier
        static string[] Codes(PartyRolePage p) => [.. p.Items.Select(i => i.PartyCode + ":" + (short)i.Role)];
        PartyRolePage Lookup(IReadOnlyCollection<Guid>? ids, PartyRoleKind? kind = null, int? limit = null) => Ok(RolesMgmt.SearchRolesAsync(Admin.Id, Company.Id, kind, null, null, limit, ids).Result);

        Assert.Equal(["L01:1", "L03:1"], Codes(Lookup([p3, p1])));                  // only those parties, ordered by code, not by the order asked
        Assert.Equal(2L, Lookup([p3, p1]).Total);
        Assert.Equal(["L02:1", "L02:2"], Codes(Lookup([p2])));                      // a party's every role unless a kind is given
        Assert.Equal(["L02:1"], Codes(Lookup([p2], PartyRoleKind.Customer)));
        Assert.Empty(Lookup([p4]).Items);                                           // a party without a role, an unknown id: nothing, no error
        Assert.Empty(Lookup([Guid.NewGuid()]).Items);
        Assert.Empty(Lookup([]).Items);                                             // an empty id list names no rows (never "all")
        Assert.Equal(["L01:1", "L03:1"], Codes(Lookup([p1, p1, p3, p3])));          // duplicates are harmless
        Assert.Equal(["L01:1"], Codes(Lookup([p1, p2, p3], limit: 1)));             // an explicit limit still applies
        Assert.Equal(4L, Lookup([p1, p2, p3], limit: 1).Total);
        // the same permission as the unpaged list
        var outsider = await NewUser("lookupoutsider");
        Fails(await RolesMgmt.SearchRolesAsync(outsider.Id, Company.Id, null, null, null, null, [p1]), ConfigStatus.Forbidden, "forbidden");
        var (viewer, _) = await Member([PermissionCatalog.Codes.PartyRolesView], ScopeKind.Company, null);
        Assert.Equal(["L01:1"], Codes(Ok(await RolesMgmt.SearchRolesAsync(viewer.Id, Company.Id, null, null, null, null, [p1]))));
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

    [PostgresFact]
    public async Task A_role_whose_account_or_representative_was_deactivated_later_can_still_be_edited_and_deactivated()
    {
        await Prepare();
        var party = await NewParty("LATE");
        var account = await Acc("1401", "asset");
        var rep = Ok(await Roles.CreateRepresentativeAsync(Admin.Id, Company.Id, new SalesRepresentativeInput("LR", "م", null, null)));
        var role = Ok(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(party, account.Id, rep.Id)));
        Ok(await ChartDomain.DeactivateAsync(Admin.Id, Company.Id, account.Id, account.Revision));
        Ok(await Roles.UpdateRepresentativeAsync(Admin.Id, Company.Id, rep.Id, 1, new SalesRepresentativeInput(null, "م", null, null, false)));
        // unchanged references are not re-validated: the role stays editable and can be switched off
        var edited = Ok(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, role.Id, 1, Customer(party, account.Id, rep.Id, days: 90)));
        Assert.Equal(90, edited.TermDays);
        var off = Ok(await Roles.UpdateRoleAsync(Admin.Id, Company.Id, role.Id, 2, Customer(party, account.Id, rep.Id, days: 90) with { IsActive = false }));
        Assert.False(off.IsActive);
        // but a NEW reference to an inactive account or representative is still refused
        var other = await NewParty("LATE2");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, account.Id)), "controlAccountId", "account_inactive");
        Invalid(await Roles.CreateRoleAsync(Admin.Id, Company.Id, Customer(other, rep: rep.Id)), "representativeId", "representative_inactive");
    }
}
