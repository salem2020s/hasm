using System.Net;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Pricing;
using Hasm.Infrastructure.Taxes;
using BA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Constraint-violation matrix of the MUTATING company endpoints (planning item 4, part 2; the injection, oversize, GUID and Unicode matrix is a separate class): a duplicate of a natural unique
/// key, a repeated or concurrent state transition and a stale-revision race must each end in a MAPPED 4xx refusal with a stable error code, never in a 5xx. The global exception handler turns an
/// unhandled PostgreSQL error (for example a unique violation 23505 that a service does not map, as #129 fixed for numbering) into 503 <c>service_unavailable</c>, so ANY 5xx here is a defect.
/// Every probe sends the same request several times IN PARALLEL (so the database constraint, not only the service's pre-check, decides the losers) and once more afterwards (the plain duplicate):
/// exactly one request succeeds (any 2xx) and every other one is the probe's EXPECTED refusal (review L1 of #155): the duplicate's own code (409 or 400 <c>code_taken</c>), 412
/// <c>revision_conflict</c> for a stale revision, or the transition's conflict code. A transition that carries no revision must still change state exactly once (one audit event). Every finding of a run is collected and reported together.
/// A mutating company route that is neither probed here nor listed as pending fails <see cref="Every_mutating_company_endpoint_is_probed_or_listed_as_pending"/>.
/// Disposable cluster only.
/// </summary>
public class EndpointConstraintMatrixTests : JournalTestBase
{
    private const int Parallel = 6;

    /// <summary>The routes the probes below cover, each with the probe that covers it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Probed = new Dictionary<string, string>
    {
        ["POST /api/v1/company/accounts/"] = "duplicate code",
        ["PUT /api/v1/company/accounts/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/accounts/{id:guid}/deactivate"] = "transition race",
        ["POST /api/v1/company/accounts/{id:guid}/activate"] = "transition race",
        ["POST /api/v1/company/branches/"] = "duplicate code",
        ["PUT /api/v1/company/branches/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/branches/{id:guid}/deactivate"] = "transition race",
        ["POST /api/v1/company/branches/{id:guid}/activate"] = "transition race",
        ["POST /api/v1/company/warehouses/"] = "duplicate code",
        ["PUT /api/v1/company/warehouses/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/warehouses/{id:guid}/deactivate"] = "transition race",
        ["POST /api/v1/company/warehouses/{id:guid}/activate"] = "transition race",
        ["POST /api/v1/company/cost-centers/"] = "duplicate code",
        ["POST /api/v1/company/cost-centers/{id:guid}/deactivate"] = "transition race",
        ["POST /api/v1/company/cost-centers/{id:guid}/activate"] = "transition race",
        ["POST /api/v1/company/inventory/units"] = "duplicate code",
        ["PUT /api/v1/company/inventory/units/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/inventory/units/{id:guid}/deactivate"] = "transition race",
        ["POST /api/v1/company/inventory/units/{id:guid}/activate"] = "transition race",
        ["POST /api/v1/company/inventory/groups"] = "duplicate code",
        ["POST /api/v1/company/inventory/groups/{id:guid}/deactivate"] = "transition race",
        ["POST /api/v1/company/inventory/groups/{id:guid}/activate"] = "transition race",
        ["POST /api/v1/company/inventory/items"] = "duplicate code",
        ["POST /api/v1/company/inventory/items/{id:guid}/deactivate"] = "transition race",
        ["POST /api/v1/company/inventory/items/{id:guid}/activate"] = "transition race",
        ["POST /api/v1/company/inventory/items/{id:guid}/barcodes"] = "duplicate barcode",
        ["POST /api/v1/company/pricing/customer-groups"] = "duplicate code",
        ["POST /api/v1/company/pricing/price-lists"] = "duplicate code",
        ["POST /api/v1/company/pricing/price-lists/{id:guid}/entries"] = "duplicate entry",
        ["POST /api/v1/company/pricing/discounts"] = "duplicate code",
        ["POST /api/v1/company/sales-representatives/"] = "duplicate code",
        ["POST /api/v1/company/parties/"] = "duplicate code",
        ["POST /api/v1/company/party-roles/"] = "duplicate party and role",
        ["POST /api/v1/company/taxes/"] = "duplicate code",
        ["POST /api/v1/company/taxes/{id:guid}/rates"] = "duplicate effective date",
        ["POST /api/v1/company/taxes/classes"] = "duplicate code",
        ["POST /api/v1/company/exchange-rates/"] = "duplicate currency and date",
        ["POST /api/v1/company/fiscal-years/"] = "duplicate (overlapping) year",
        ["PUT /api/v1/company/settings"] = "stale-revision race",
        ["PUT /api/v1/company/cost-centers/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/cost-centers/{id:guid}/recode"] = "recode race (one new code)",
        ["POST /api/v1/company/cost-centers/{id:guid}/move"] = "move race",
        ["POST /api/v1/company/accounts/{id:guid}/recode"] = "recode race (one new code)",
        ["PUT /api/v1/company/inventory/groups/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/inventory/items/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/inventory/items/{id:guid}/barcodes/{barcodeId:guid}/deactivate"] = "transition race",
        ["POST /api/v1/company/inventory/items/{id:guid}/barcodes/{barcodeId:guid}/activate"] = "transition race",
        ["PUT /api/v1/company/pricing/customer-groups/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/pricing/price-lists/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/pricing/entries/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/pricing/discounts/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/sales-representatives/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/party-roles/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/taxes/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/taxes/classes/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/journal/{id:guid}/submit"] = "transition race",
        ["POST /api/v1/company/journal/{id:guid}/withdraw"] = "transition race",
        ["POST /api/v1/company/journal/{id:guid}/cancel"] = "transition race",
        ["POST /api/v1/company/fiscal-periods/{id:guid}/soft-close"] = "transition race",
        ["POST /api/v1/company/fiscal-periods/{id:guid}/reopen"] = "transition race",
    };

    /// <summary>
    /// Mutating company routes this class does NOT probe: part 2 (<see cref="EndpointConstraintMatrixPart2Tests"/>) probes them or lists them as pending there, explicitly by family, so a
    /// route the API adds must be classified in <see cref="Probed"/> or in part 2, or <see cref="Every_mutating_company_endpoint_is_probed_or_listed_as_pending"/> fails.
    /// </summary>
    /// <remarks>A property, not a field initializer: the two classes refer to each other's lists, and static field initializers would depend on which type initializes first.</remarks>
    public static IReadOnlyList<string> Pending => [.. EndpointConstraintMatrixPart2Tests.Probed.Keys, .. EndpointConstraintMatrixPart2Tests.Pending];

    // ------------------------------------------------------------------------------------------------------------------------------- engine

    private readonly List<string> findings = [];
    private string cookie = "";

    private Task<ApiResponse> Call(HttpMethod method, string path, object? body, string? ifMatch = null) => Send(method, path, body, cookie, ifMatch: ifMatch);

    /// <summary>
    /// Sends the same request <see cref="Parallel"/> times in parallel, then once more: exactly one request may succeed (any 2xx); every other answer must be a 4xx with an error
    /// code. Returns the winning response (or null). Findings are collected, not thrown, so one run reports every endpoint.
    /// </summary>
    private async Task<ApiResponse?> Race(string what, string[] expected, HttpMethod method, string path, object? body, string? ifMatch = null)
    {
        var raced = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => Task.Run(() => Call(method, path, body, ifMatch))));
        var again = await Call(method, path, body, ifMatch);
        var all = raced.Append(again).ToList();
        static bool Ok2xx(ApiResponse r) => (int)r.Status is >= 200 and < 300;
        var winners = all.Where(Ok2xx).ToList();
        foreach (var r in all.Where(r => (int)r.Status >= 500))
            findings.Add($"{what}: {(int)r.Status} {Short(r.Body)}");
        foreach (var r in all.Where(r => !Ok2xx(r) && (int)r.Status < 500 && !expected.Contains(Signature(r))))
            findings.Add($"{what}: {Signature(r)} is not the expected refusal ({string.Join(" or ", expected)}): {Short(r.Body)}");
        if (winners.Count != 1)
            findings.Add($"{what}: {winners.Count} requests succeeded (expected exactly one): {string.Join(", ", all.Select(r => $"{(int)r.Status}:{EndpointPermissionMatrixTests.ErrorOf(r)}"))}");
        return winners.FirstOrDefault();
    }

    private static string Short(string body) => body.Length > 160 ? body[..160] : body;

    /// <summary>"status:code" of a refusal; a 400 <c>validation_failed</c> is identified by its first field code (for example <c>400:code_taken</c>).</summary>
    private static string Signature(ApiResponse r)
    {
        var code = EndpointPermissionMatrixTests.ErrorOf(r);
        if (code == "validation_failed")
            try { code = r.Json.GetProperty("fields")[0].GetProperty("code").GetString(); } catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { }
        return $"{(int)r.Status}:{code}";
    }
    private static Guid IdOf(ApiResponse r) => r.Json.GetProperty("id").GetGuid();
    private static string EtagOf(ApiResponse r) => r.Headers.ETag!.Tag;

    /// <summary>Deactivate then activate an entity, each as a race of identical requests carrying the same If-Match.</summary>
    private async Task TransitionRace(string what, string basePath, Guid id, string etag)
    {
        var off = await Race($"{what} deactivate", ["412:revision_conflict"], HttpMethod.Post, $"{basePath}/{id}/deactivate", new { }, etag);
        if (off is null) return;
        await Race($"{what} activate", ["412:revision_conflict"], HttpMethod.Post, $"{basePath}/{id}/activate", new { }, EtagOf(off));
    }

    private async Task Start()
    {
        await StartApi(new WarehouseLimitPolicy(50, 50));
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        foreach (var (code, isBase) in new[] { ("JOD", true), ("USD", false) })
            await Sql($"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('{code}', {(isBase ? "true" : "false")}, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        cookie = await SignIn(Admin);
    }

    // ------------------------------------------------------------------------------------------------------------------------------- tests

    [PostgresFact]
    public async Task Every_mutating_company_endpoint_is_probed_or_listed_as_pending()
    {
        // the permission matrix (#119) pins its rows to the API's route table; every one of them is either probed here or explicitly pending, never both, and nothing else is listed
        var all = EndpointPermissionMatrixTests.Matrix.Select(r => r.Key).Concat(EndpointPermissionMatrixTests.Excluded.Keys).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Pending.Count, Pending.Distinct(StringComparer.Ordinal).Count());
        var both = Probed.Keys.Intersect(Pending, StringComparer.Ordinal).ToList();
        Assert.True(both.Count == 0, "routes both probed and pending:\n" + string.Join("\n", both));
        var listed = Probed.Keys.Concat(Pending).ToHashSet(StringComparer.Ordinal);
        var unclassified = all.Except(listed).Order(StringComparer.Ordinal).ToList();
        Assert.True(unclassified.Count == 0, "mutating company routes neither probed nor listed as pending (classify them in EndpointConstraintMatrixTests or EndpointConstraintMatrixPart2Tests):\n" + string.Join("\n", unclassified));
        var stale = listed.Except(all).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "listed routes the API no longer maps:\n" + string.Join("\n", stale));
        Assert.Equal(60, Probed.Count);
        Assert.Equal(111, Pending.Count);
        await Task.CompletedTask;
    }

    [PostgresFact]
    public async Task Duplicates_and_parallel_duplicates_of_master_data_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        var main = await Sql<Guid>($"SELECT {Br.Id} FROM {Br.Table} WHERE {Br.IsMain}");

        await Race("account", ["409:account_code_taken"], HttpMethod.Post, "/api/v1/company/accounts", new { code = "9101", nameAr = "حساب", nameEn = "Account", type = "asset" });
        await Race("branch", ["409:branch_code_taken"], HttpMethod.Post, "/api/v1/company/branches", new BranchInput("CVB", "فرع", "Branch", null, null, null, null, null, null, null, null));
        await Race("warehouse", ["409:warehouse_code_taken"], HttpMethod.Post, "/api/v1/company/warehouses", new WarehouseInput("CVW", main, "مستودع", "Warehouse", null, null, null, null, null, null, null, null));
        await Race("cost center", ["409:code_in_use"], HttpMethod.Post, "/api/v1/company/cost-centers", new CostCenterInput("CVC", null, "مركز", "Center", null, false));
        var unit = await Race("unit", ["409:unit_code_taken"], HttpMethod.Post, "/api/v1/company/inventory/units", new UnitInput("CVU", "وحدة", "Unit", 2));
        var group = await Race("item group", ["409:group_code_taken"], HttpMethod.Post, "/api/v1/company/inventory/groups", new ItemGroupInput("CVG", "مجموعة", "Group", null, 1));
        Assert.True(unit is not null && group is not null, string.Join("\n", findings));
        var item = await Race("item", ["409:item_code_taken"], HttpMethod.Post, "/api/v1/company/inventory/items",
            new ItemInput("CVI", "صنف", "Item", 1, IdOf(group!), IdOf(unit!), null, null, false, false, false, null, []));
        Assert.True(item is not null, string.Join("\n", findings));
        await Race("barcode", ["409:barcode_taken"], HttpMethod.Post, $"/api/v1/company/inventory/items/{IdOf(item!)}/barcodes", new BarcodeInput("4006381333931", null, null));

        await Race("customer group", ["400:code_taken"], HttpMethod.Post, "/api/v1/company/pricing/customer-groups", new CustomerGroupInput("CVCG", "مجموعة", "Group", true));
        var list = await Race("price list", ["400:code_taken"], HttpMethod.Post, "/api/v1/company/pricing/price-lists",
            new PriceListInput("CVPL", "قائمة", "List", 10, "JOD", new DateOnly(2026, 1, 1), null, true, null));
        if (list is not null)
            await Race("price entry", ["400:tier_exists"], HttpMethod.Post, $"/api/v1/company/pricing/price-lists/{IdOf(list)}/entries", new PriceEntryInput(IdOf(item!), null, IdOf(unit!), 0m, 10m, null));
        await Race("discount", ["400:code_taken"], HttpMethod.Post, "/api/v1/company/pricing/discounts",
            new DiscountRuleInput("CVD", "خصم", "Discount", 1, new DateOnly(2026, 1, 1), null, true, 5m, null, null, IdOf(item!), null));
        await Race("sales representative", ["400:code_taken"], HttpMethod.Post, "/api/v1/company/sales-representatives", new SalesRepresentativeInput("CVR", "مندوب", "Rep", null, true));
        await Race("party", ["400:code_taken"], HttpMethod.Post, "/api/v1/company/parties", new { code = "CVP", displayName = "Party", isActive = true, identity = new { kind = 1, legalNameAr = "طرف" } });
        var party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'CVQ', 'q', true, 1, 1, '{Admin.Id}', now(), now())");
        await Race("party role", ["409:role_exists"], HttpMethod.Post, "/api/v1/company/party-roles", new PartyRoleInput(party, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null));

        var tax = await Race("tax", ["400:code_taken"], HttpMethod.Post, "/api/v1/company/taxes", new TaxInput("CVT", "ضريبة", "Tax", true));
        if (tax is not null)
            await Race("tax rate", ["400:rate_not_later"], HttpMethod.Post, $"/api/v1/company/taxes/{IdOf(tax)}/rates", new TaxRateInput(new DateOnly(2026, 1, 1), TaxTreatment.Standard, 15m));
        await Race("tax class", ["400:code_taken"], HttpMethod.Post, "/api/v1/company/taxes/classes", new TaxClassInput(TaxClassKind.Item, "CVTC", "فئة", "Class", true));
        await Race("exchange rate", ["412:revision_conflict"], HttpMethod.Post, "/api/v1/company/exchange-rates", new ExchangeRateInput("USD", 0.71m, new DateOnly(2026, 2, 1), null), "\"0\"");     // If-Match: the latest rate id of the series (none yet)
        // a parallel duplicate of the same year meets the overlap or, equally legitimately, the name already taken (review follow-up of #155)
        await Race("fiscal year", ["409:fiscal_year_overlap", "409:fiscal_year_name_taken"], HttpMethod.Post, "/api/v1/company/fiscal-years", new FiscalYearInput("FY2027", new DateOnly(2027, 1, 1), "monthly"));

        Assert.True(findings.Count == 0, $"{findings.Count} constraint-violation finding(s):\n" + string.Join("\n", findings));
    }

    [PostgresFact]
    public async Task Parallel_transitions_and_stale_revision_updates_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        var main = await Sql<Guid>($"SELECT {Br.Id} FROM {Br.Table} WHERE {Br.IsMain}");
        async Task<ApiResponse> Create(string path, object body)
        {
            var r = await Call(HttpMethod.Post, path, body);
            Assert.True(r.Status == HttpStatusCode.Created, $"{path}: {(int)r.Status} {r.Body}");
            return r;
        }

        var account = await Create("/api/v1/company/accounts", new { code = "9201", nameAr = "حساب", nameEn = "Account", type = "asset" });
        var renamed = await Race("account update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/accounts/{IdOf(account)}", new AccountDetailsInput("حساب", "Renamed", null, null, null, null), EtagOf(account));
        await TransitionRace("account", "/api/v1/company/accounts", IdOf(account), EtagOf(renamed ?? account));

        var branch = await Create("/api/v1/company/branches", new BranchInput("CVT1", "فرع", "Branch", null, null, null, null, null, null, null, null));
        renamed = await Race("branch update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/branches/{IdOf(branch)}", new BranchInput(null, "فرع", "Renamed", null, null, null, null, null, null, null, null), EtagOf(branch));
        await TransitionRace("branch", "/api/v1/company/branches", IdOf(branch), EtagOf(renamed ?? branch));

        var warehouse = await Create("/api/v1/company/warehouses", new WarehouseInput("CVT2", main, "مستودع", "Warehouse", null, null, null, null, null, null, null, null));
        renamed = await Race("warehouse update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/warehouses/{IdOf(warehouse)}",
            new WarehouseInput(null, null, "مستودع", "Renamed", null, null, null, null, null, null, null, null), EtagOf(warehouse));
        await TransitionRace("warehouse", "/api/v1/company/warehouses", IdOf(warehouse), EtagOf(renamed ?? warehouse));

        var center = await Create("/api/v1/company/cost-centers", new CostCenterInput("CVT3", null, "مركز", "Center", null, false));
        await TransitionRace("cost center", "/api/v1/company/cost-centers", IdOf(center), EtagOf(center));

        var unit = await Create("/api/v1/company/inventory/units", new UnitInput("CVT4", "وحدة", "Unit", 2));
        renamed = await Race("unit update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/inventory/units/{IdOf(unit)}", new UnitInput("CVT4", "وحدة", "Renamed", 2), EtagOf(unit));
        var group = await Create("/api/v1/company/inventory/groups", new ItemGroupInput("CVT5", "مجموعة", "Group", null, 1));
        var item = await Create("/api/v1/company/inventory/items", new ItemInput("CVT6", "صنف", "Item", 1, IdOf(group), IdOf(unit), null, null, false, false, false, null, []));
        await TransitionRace("item", "/api/v1/company/inventory/items", IdOf(item), EtagOf(item));
        var emptyGroup = await Create("/api/v1/company/inventory/groups", new ItemGroupInput("CVT8", "مجموعة", "Empty", null, 1));     // a group with active items cannot be deactivated
        await TransitionRace("item group", "/api/v1/company/inventory/groups", IdOf(emptyGroup), EtagOf(emptyGroup));
        var spare = await Create("/api/v1/company/inventory/units", new UnitInput("CVT7", "وحدة", "Spare", 2));
        await TransitionRace("unit", "/api/v1/company/inventory/units", IdOf(spare), EtagOf(spare));
        Assert.NotNull(renamed);

        var settings = await Call(HttpMethod.Get, "/api/v1/company/settings", null);
        await Race("settings update", ["412:revision_conflict"], HttpMethod.Put, "/api/v1/company/settings", new SettingsInput("ar", "dd/MM/yyyy", "latin", "UTC"), EtagOf(settings));

        Assert.True(findings.Count == 0, $"{findings.Count} constraint-violation finding(s):\n" + string.Join("\n", findings));
    }
    [PostgresFact]
    public async Task Parallel_updates_recodes_and_workflow_transitions_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        async Task<ApiResponse> Create(string path, object body, string? ifMatch = null)
        {
            var r = await Call(HttpMethod.Post, path, body, ifMatch);
            Assert.True((int)r.Status is >= 200 and < 300, $"{path}: {(int)r.Status} {r.Body}");
            return r;
        }
        ApiResponse Or(ApiResponse? winner, ApiResponse fallback) => winner ?? fallback;

        // cost centers: update, recode to ONE new code, move under a group
        var parent = await Create("/api/v1/company/cost-centers", new CostCenterInput("CVW1", null, "مجموعة", "Parent", null, true));
        var center = await Create("/api/v1/company/cost-centers", new CostCenterInput("CVW2", null, "مركز", "Center", null, false));
        var step = Or(await Race("cost center update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/cost-centers/{IdOf(center)}", new CostCenterDetailsInput("مركز", "Renamed", null, false), EtagOf(center)), center);
        step = Or(await Race("cost center recode", ["412:revision_conflict"], HttpMethod.Post, $"/api/v1/company/cost-centers/{IdOf(center)}/recode", new CostCenterCodeInput("CVW3"), EtagOf(step)), step);
        await Race("cost center move", ["412:revision_conflict"], HttpMethod.Post, $"/api/v1/company/cost-centers/{IdOf(center)}/move", new CostCenterMoveInput(IdOf(parent)), EtagOf(step));

        // accounts: recode to ONE new code
        var account = await Create("/api/v1/company/accounts", new { code = "9301", nameAr = "حساب", nameEn = "Account", type = "asset" });
        await Race("account recode", ["412:revision_conflict"], HttpMethod.Post, $"/api/v1/company/accounts/{IdOf(account)}/recode", new AccountCodeInput("9302"), EtagOf(account));

        // inventory: group and item updates, barcode transitions
        var unit = await Create("/api/v1/company/inventory/units", new UnitInput("CVW4", "وحدة", "Unit", 2));
        var group = await Create("/api/v1/company/inventory/groups", new ItemGroupInput("CVW5", "مجموعة", "Group", null, 1));
        await Race("item group update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/inventory/groups/{IdOf(group)}", new ItemGroupInput("CVW5", "مجموعة", "Renamed", null, 1), EtagOf(group));
        var item = await Create("/api/v1/company/inventory/items", new ItemInput("CVW6", "صنف", "Item", 1, IdOf(group), IdOf(unit), null, null, false, false, false, null, []));
        var itemStep = Or(await Race("item update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/inventory/items/{IdOf(item)}",
            new ItemInput("CVW6", "صنف", "Renamed", 1, IdOf(group), IdOf(unit), null, null, false, false, false, null, []), EtagOf(item)), item);
        var barcode = await Create($"/api/v1/company/inventory/items/{IdOf(item)}/barcodes", new BarcodeInput("4006381333931", null, null));
        // barcode activation carries no revision (no If-Match): the state must still change exactly once under concurrency (review L2 of #155): one winner and ONE business audit event
        foreach (var (what, action, refusal) in new[] { ("barcode deactivate", "deactivate", "409:barcode_already_inactive"), ("barcode activate", "activate", "409:barcode_already_active") })
        {
            // counted for THIS barcode and event type only (review follow-up of #155), so an unrelated concurrent business event cannot mask a double application
            var ofThisBarcode = $"SELECT count(*) FROM {BA.Table} WHERE {BA.SubjectId} = '{IdOf(barcode)}' AND {BA.EventType} = {InventoryAuditEvents.BarcodeActiveChanged}";
            var events = await Sql<long>(ofThisBarcode);
            await Race(what, [refusal], HttpMethod.Post, $"/api/v1/company/inventory/items/{IdOf(item)}/barcodes/{IdOf(barcode)}/{action}", new { });
            var written = await Sql<long>(ofThisBarcode) - events;
            if (written != 1) findings.Add($"{what}: {written} business audit events for one state change");
        }
        Assert.NotNull(itemStep);

        // pricing, sales representatives, party roles, taxes: updates from one revision
        var customerGroup = await Create("/api/v1/company/pricing/customer-groups", new CustomerGroupInput("CVW7", "مجموعة", "Group", true));
        await Race("customer group update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/pricing/customer-groups/{IdOf(customerGroup)}", new CustomerGroupInput("CVW7", "مجموعة", "Renamed", true), EtagOf(customerGroup));
        var list = await Create("/api/v1/company/pricing/price-lists", new PriceListInput("CVW8", "قائمة", "List", 10, "JOD", new DateOnly(2026, 1, 1), null, true, null));
        await Race("price list update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/pricing/price-lists/{IdOf(list)}", new PriceListInput("CVW8", "قائمة", "Renamed", 10, "JOD", new DateOnly(2026, 1, 1), null, true, null), EtagOf(list));
        var entry = await Create($"/api/v1/company/pricing/price-lists/{IdOf(list)}/entries", new PriceEntryInput(IdOf(item), null, IdOf(unit), 0m, 10m, null));
        await Race("price entry update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/pricing/entries/{IdOf(entry)}", new PriceEntryInput(IdOf(item), null, IdOf(unit), 0m, 12m, null), EtagOf(entry));
        var discount = await Create("/api/v1/company/pricing/discounts", new DiscountRuleInput("CVW9", "خصم", "Discount", 1, new DateOnly(2026, 1, 1), null, true, 5m, null, null, IdOf(item), null));
        await Race("discount update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/pricing/discounts/{IdOf(discount)}",
            new DiscountRuleInput("CVW9", "خصم", "Renamed", 1, new DateOnly(2026, 1, 1), null, true, 6m, null, null, IdOf(item), null), EtagOf(discount));
        var rep = await Create("/api/v1/company/sales-representatives", new SalesRepresentativeInput("CVWA", "مندوب", "Rep", null, true));
        await Race("sales representative update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/sales-representatives/{IdOf(rep)}", new SalesRepresentativeInput("CVWA", "مندوب", "Renamed", null, true), EtagOf(rep));
        var party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'CVWB', 'q', true, 1, 1, '{Admin.Id}', now(), now())");
        var role = await Create("/api/v1/company/party-roles", new PartyRoleInput(party, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null));
        await Race("party role update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/party-roles/{IdOf(role)}", new PartyRoleInput(party, PartyRoleKind.Customer, PartyPaymentMode.Credit, 30, 100m, null, null), EtagOf(role));
        var tax = await Create("/api/v1/company/taxes", new TaxInput("CVWC", "ضريبة", "Tax", true));
        await Race("tax update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/taxes/{IdOf(tax)}", new TaxInput("CVWC", "ضريبة", "Renamed", true), EtagOf(tax));
        var taxClass = await Create("/api/v1/company/taxes/classes", new TaxClassInput(TaxClassKind.Item, "CVWD", "فئة", "Class", true));
        await Race("tax class update", ["412:revision_conflict"], HttpMethod.Put, $"/api/v1/company/taxes/classes/{IdOf(taxClass)}", new TaxClassInput(TaxClassKind.Item, "CVWD", "فئة", "Renamed", true), EtagOf(taxClass));

        // journal: submit, withdraw and cancel of one draft, each from one revision
        Cash = await NewAccount("1101", "asset");
        Sales = await NewAccount("4101", "revenue");
        var main = await Sql<Guid>($"SELECT {Br.Id} FROM {Br.Table} WHERE {Br.IsMain}");
        var draft = Ok(await Journal.CreateDraftAsync(Admin.Id, Company.Id, new JournalInput(new DateOnly(2026, 3, 10), "matrix", main, [Dr(Cash.Id, 10m), Cr(Sales.Id, 10m)], null), false)).Header;
        var submitted = await Race("journal submit", ["409:entry_not_draft", "412:revision_conflict"], HttpMethod.Post, $"/api/v1/company/journal/{draft.Id}/submit", null, "\"1\"");
        var withdrawn = submitted is null ? null : await Race("journal withdraw", ["412:revision_conflict"], HttpMethod.Post, $"/api/v1/company/journal/{draft.Id}/withdraw", null, EtagOf(submitted));
        if (withdrawn is not null) await Race("journal cancel", ["409:entry_not_cancellable", "412:revision_conflict"], HttpMethod.Post, $"/api/v1/company/journal/{draft.Id}/cancel", null, EtagOf(withdrawn));

        // fiscal period: soft close and reopen, each from one revision
        var period = Year.Periods[0];
        var closed = await Race("period soft-close", ["412:revision_conflict"], HttpMethod.Post, $"/api/v1/company/fiscal-periods/{period.Id}/soft-close", null, $"\"{period.Revision}\"");
        if (closed is not null) await Race("period reopen", ["412:revision_conflict"], HttpMethod.Post, $"/api/v1/company/fiscal-periods/{period.Id}/reopen", null, EtagOf(closed));

        Assert.True(findings.Count == 0, $"{findings.Count} constraint-violation finding(s):\n" + string.Join("\n", findings));
    }
}
