using System.Net;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Pricing;
using Hasm.Infrastructure.Reporting;
using Hasm.Infrastructure.Taxes;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Constraint-violation matrix, part 2 (planning item 4): the mutating company routes part 1 (<c>EndpointConstraintMatrixTests</c>, #155) left pending. The rule and the engine are
/// part 1's: the same request is sent <see cref="Parallel"/> times in parallel and once more, and every answer is either the probe's winner or its EXPECTED refusal, never a 5xx
/// (the global handler maps an unhandled PostgreSQL error to 503). Probe modes: <see cref="Mode.One"/> (exactly one 2xx; a create, update or transition from one revision),
/// <see cref="Mode.Same"/> (a read-only or idempotent operation: every answer has the same status and code), <see cref="Mode.Append"/> (an operation that appends a new
/// version without a precondition: at least one 2xx, every refusal the expected version conflict). Every finding of a run is reported together.
/// Disposable cluster only.
/// </summary>
public class EndpointConstraintMatrixPart2Tests : JournalTestBase
{
    private const int Parallel = 6;
    public enum Mode { One, Same, Append }

    /// <summary>The routes this class probes, each with the kind of probe.</summary>
    public static readonly IReadOnlyDictionary<string, string> Probed = new Dictionary<string, string>
    {
        ["PUT /api/v1/company/accounts/settings"] = "stale-revision race",
        ["POST /api/v1/company/accounts/apply-template"] = "duplicate (409 chart_not_empty)",
        ["POST /api/v1/company/accounts/{id:guid}/move"] = "stale-revision race",
        ["PUT /api/v1/company/profile"] = "stale-revision race",
        ["POST /api/v1/company/profile/display-name-sync"] = "idempotent (same answer)",
        ["PUT /api/v1/company/currencies/{code}"] = "stale-revision race",
        ["PUT /api/v1/company/inventory/settings"] = "stale-revision race",
        ["PUT /api/v1/company/ledger-settings/"] = "stale-revision race",
        ["PUT /api/v1/company/negative-stock/settings"] = "stale-revision race",
        ["PUT /api/v1/company/numbering/{docType}"] = "stale-revision race",
        ["POST /api/v1/company/pricing/policy"] = "version race",
        ["POST /api/v1/company/taxes/policy"] = "append race (409 version_conflict)",
        ["POST /api/v1/company/branches/{id:guid}/make-main"] = "stale-revision race",
        ["PUT /api/v1/company/cost-center-policies/accounts/{accountId:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/cost-center-policies/branches/{costCenterId:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/inventory/accounting/"] = "stale-revision race",
        ["PUT /api/v1/company/negative-stock/items/{itemId:guid}"] = "idempotent (same answer)",
        ["PUT /api/v1/company/taxes/item-classes/{itemId:guid}"] = "idempotent (same answer)",
        ["PUT /api/v1/company/taxes/party-classes/{partyId:guid}"] = "idempotent (same answer)",
        ["POST /api/v1/company/taxes/rules"] = "duplicate (409 rule_overlap)",
        ["PUT /api/v1/company/taxes/rules/{id:guid}"] = "stale-revision race",
        ["PUT /api/v1/company/pricing/customer-assignments/{partyId:guid}"] = "stale-revision race",
        ["POST /api/v1/company/pricing/quote"] = "read-only (same answer)",
        ["PUT /api/v1/company/parties/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/parties/{id:guid}/propose"] = "open-request race (409 request_open)",
        ["POST /api/v1/company/parties/sync"] = "idempotent (same answer)",
        ["POST /api/v1/company/parties/reconcile"] = "read-only (same answer)",
        ["PUT /api/v1/company/statements/mapping"] = "stale-revision race",
        ["POST /api/v1/company/approval-policies/"] = "append (no uniqueness rule)",
        ["PUT /api/v1/company/approval-policies/{id:guid}"] = "stale-revision race",
        ["POST /api/v1/company/approval-policies/{id:guid}/deactivate"] = "stale-revision race",
        ["POST /api/v1/company/approval-policies/{id:guid}/activate"] = "stale-revision race",
    };

    /// <summary>The routes part 1 (<c>EndpointConstraintMatrixTests</c>, #155) probes; listed here so the completeness test is exact before the two classes are folded together.</summary>
    public static readonly IReadOnlyList<string> CoveredByPart1 =
    [
        "POST /api/v1/company/accounts/",
        "POST /api/v1/company/accounts/{id:guid}/activate",
        "POST /api/v1/company/accounts/{id:guid}/deactivate",
        "POST /api/v1/company/accounts/{id:guid}/recode",
        "POST /api/v1/company/branches/",
        "POST /api/v1/company/branches/{id:guid}/activate",
        "POST /api/v1/company/branches/{id:guid}/deactivate",
        "POST /api/v1/company/cost-centers/",
        "POST /api/v1/company/cost-centers/{id:guid}/activate",
        "POST /api/v1/company/cost-centers/{id:guid}/deactivate",
        "POST /api/v1/company/cost-centers/{id:guid}/move",
        "POST /api/v1/company/cost-centers/{id:guid}/recode",
        "POST /api/v1/company/exchange-rates/",
        "POST /api/v1/company/fiscal-periods/{id:guid}/reopen",
        "POST /api/v1/company/fiscal-periods/{id:guid}/soft-close",
        "POST /api/v1/company/fiscal-years/",
        "POST /api/v1/company/inventory/groups",
        "POST /api/v1/company/inventory/groups/{id:guid}/activate",
        "POST /api/v1/company/inventory/groups/{id:guid}/deactivate",
        "POST /api/v1/company/inventory/items",
        "POST /api/v1/company/inventory/items/{id:guid}/activate",
        "POST /api/v1/company/inventory/items/{id:guid}/barcodes",
        "POST /api/v1/company/inventory/items/{id:guid}/barcodes/{barcodeId:guid}/activate",
        "POST /api/v1/company/inventory/items/{id:guid}/barcodes/{barcodeId:guid}/deactivate",
        "POST /api/v1/company/inventory/items/{id:guid}/deactivate",
        "POST /api/v1/company/inventory/units",
        "POST /api/v1/company/inventory/units/{id:guid}/activate",
        "POST /api/v1/company/inventory/units/{id:guid}/deactivate",
        "POST /api/v1/company/journal/{id:guid}/cancel",
        "POST /api/v1/company/journal/{id:guid}/submit",
        "POST /api/v1/company/journal/{id:guid}/withdraw",
        "POST /api/v1/company/parties/",
        "POST /api/v1/company/party-roles/",
        "POST /api/v1/company/pricing/customer-groups",
        "POST /api/v1/company/pricing/discounts",
        "POST /api/v1/company/pricing/price-lists",
        "POST /api/v1/company/pricing/price-lists/{id:guid}/entries",
        "POST /api/v1/company/sales-representatives/",
        "POST /api/v1/company/taxes/",
        "POST /api/v1/company/taxes/classes",
        "POST /api/v1/company/taxes/{id:guid}/rates",
        "POST /api/v1/company/warehouses/",
        "POST /api/v1/company/warehouses/{id:guid}/activate",
        "POST /api/v1/company/warehouses/{id:guid}/deactivate",
        "PUT /api/v1/company/accounts/{id:guid}",
        "PUT /api/v1/company/branches/{id:guid}",
        "PUT /api/v1/company/cost-centers/{id:guid}",
        "PUT /api/v1/company/inventory/groups/{id:guid}",
        "PUT /api/v1/company/inventory/items/{id:guid}",
        "PUT /api/v1/company/inventory/units/{id:guid}",
        "PUT /api/v1/company/party-roles/{id:guid}",
        "PUT /api/v1/company/pricing/customer-groups/{id:guid}",
        "PUT /api/v1/company/pricing/discounts/{id:guid}",
        "PUT /api/v1/company/pricing/entries/{id:guid}",
        "PUT /api/v1/company/pricing/price-lists/{id:guid}",
        "PUT /api/v1/company/sales-representatives/{id:guid}",
        "PUT /api/v1/company/settings",
        "PUT /api/v1/company/taxes/classes/{id:guid}",
        "PUT /api/v1/company/taxes/{id:guid}",
        "PUT /api/v1/company/warehouses/{id:guid}",
    ];

    /// <summary>Mutating company routes not probed yet, listed EXPLICITLY by family: a new route must be classified here, in <see cref="Probed"/> or in part 1.</summary>
    public static readonly IReadOnlyList<string> Pending =
    [
        // attachment-retention
        "POST /api/v1/company/attachment-retention/holds",
        "POST /api/v1/company/attachment-retention/holds/{id:guid}/release",
        "POST /api/v1/company/attachment-retention/policies",
        "POST /api/v1/company/attachment-retention/policies/{id:guid}/approve",
        "POST /api/v1/company/attachment-retention/policies/{id:guid}/revoke",
        // attachments
        "POST /api/v1/company/attachments/",
        "POST /api/v1/company/attachments/maintenance/rewrap",
        "POST /api/v1/company/attachments/maintenance/sweep",
        "POST /api/v1/company/attachments/maintenance/verify",
        "POST /api/v1/company/attachments/{id:guid}/remove",
        // commercial-documents
        "POST /api/v1/company/commercial-documents/",
        "POST /api/v1/company/commercial-documents/links",
        "POST /api/v1/company/commercial-documents/{id:guid}/approve",
        "POST /api/v1/company/commercial-documents/{id:guid}/cancel",
        "POST /api/v1/company/commercial-documents/{id:guid}/reject",
        "POST /api/v1/company/commercial-documents/{id:guid}/submit",
        "POST /api/v1/company/commercial-documents/{id:guid}/withdraw",
        "PUT /api/v1/company/commercial-documents/{id:guid}",
        // fiscal-closing
        "POST /api/v1/company/fiscal-closing/years/{yearId:guid}/carry-forward",
        "POST /api/v1/company/fiscal-closing/years/{yearId:guid}/close",
        "POST /api/v1/company/fiscal-closing/years/{yearId:guid}/reverse-closing",
        "POST /api/v1/company/fiscal-closing/years/{yearId:guid}/reverse-opening",
        "PUT /api/v1/company/fiscal-closing/settings",
        // fiscal-periods
        "POST /api/v1/company/fiscal-periods/{id:guid}/final-close",
        "POST /api/v1/company/fiscal-periods/{id:guid}/reopen-requests",
        // fiscal-reopen-requests
        "POST /api/v1/company/fiscal-reopen-requests/{id:guid}/approve",
        "POST /api/v1/company/fiscal-reopen-requests/{id:guid}/cancel",
        "POST /api/v1/company/fiscal-reopen-requests/{id:guid}/deny",
        // fiscal-years
        "POST /api/v1/company/fiscal-years/{id:guid}/close",
        "POST /api/v1/company/fiscal-years/{id:guid}/reopen-requests",
        // imports
        "POST /api/v1/company/imports/",
        "POST /api/v1/company/imports/maintenance/rewrap",
        "POST /api/v1/company/imports/maintenance/run",
        "POST /api/v1/company/imports/templates/{adapter}/{name}/retire",
        "POST /api/v1/company/imports/{id:guid}/apply",
        "POST /api/v1/company/imports/{id:guid}/approve",
        "POST /api/v1/company/imports/{id:guid}/cancel",
        "POST /api/v1/company/imports/{id:guid}/dry-run",
        "POST /api/v1/company/imports/{id:guid}/mapping/apply-template",
        "POST /api/v1/company/imports/{id:guid}/mapping/template",
        "POST /api/v1/company/imports/{id:guid}/reject",
        "POST /api/v1/company/imports/{id:guid}/renew",
        "POST /api/v1/company/imports/{id:guid}/resolutions",
        "POST /api/v1/company/imports/{id:guid}/stage",
        "POST /api/v1/company/imports/{id:guid}/submit",
        "POST /api/v1/company/imports/{id:guid}/withdraw",
        "PUT /api/v1/company/imports/settings",
        "PUT /api/v1/company/imports/{id:guid}/mapping",
        // journal
        "POST /api/v1/company/journal/",
        "POST /api/v1/company/journal/{id:guid}/decision",
        "POST /api/v1/company/journal/{id:guid}/post",
        "POST /api/v1/company/journal/{id:guid}/reverse",
        "PUT /api/v1/company/journal/{id:guid}",
        // stock-adjustments
        "POST /api/v1/company/stock-adjustments/{id:guid}/posting",
        "POST /api/v1/company/stock-adjustments/{id:guid}/reversal",
        // stock-documents
        "POST /api/v1/company/stock-documents/",
        "POST /api/v1/company/stock-documents/{id:guid}/approve",
        "POST /api/v1/company/stock-documents/{id:guid}/cancel",
        "POST /api/v1/company/stock-documents/{id:guid}/negative-exception/approvals",
        "POST /api/v1/company/stock-documents/{id:guid}/posting/",
        "POST /api/v1/company/stock-documents/{id:guid}/reject",
        "POST /api/v1/company/stock-documents/{id:guid}/reversal",
        "POST /api/v1/company/stock-documents/{id:guid}/submit",
        "POST /api/v1/company/stock-documents/{id:guid}/withdraw",
        "PUT /api/v1/company/stock-documents/{id:guid}",
        // stocktakes
        "POST /api/v1/company/stocktakes/",
        "POST /api/v1/company/stocktakes/{id:guid}/adjustment",
        "POST /api/v1/company/stocktakes/{id:guid}/approve",
        "POST /api/v1/company/stocktakes/{id:guid}/cancel",
        "POST /api/v1/company/stocktakes/{id:guid}/generate-sheet",
        "POST /api/v1/company/stocktakes/{id:guid}/reject",
        "POST /api/v1/company/stocktakes/{id:guid}/submit",
        "POST /api/v1/company/stocktakes/{id:guid}/withdraw",
        "PUT /api/v1/company/stocktakes/{id:guid}",
    ];


    // ------------------------------------------------------------------------------------------------------------------------------- engine

    private readonly List<string> findings = [];
    private readonly List<string> diagnostics = [];
    private string cookie = "";

    private Task<ApiResponse> Call(HttpMethod method, string path, object? body, string? ifMatch = null) => Send(method, path, body, cookie, ifMatch: ifMatch);

    /// <summary>"status:code" of an answer; a 400 <c>validation_failed</c> is identified by its first field code (for example <c>400:code_taken</c>); a 2xx is "2xx".</summary>
    private static string Signature(ApiResponse r)
    {
        if ((int)r.Status is >= 200 and < 300) return "2xx";
        var code = EndpointPermissionMatrixTests.ErrorOf(r);
        if (code == "validation_failed")
            try { code = r.Json.GetProperty("fields")[0].GetProperty("code").GetString(); } catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { }
        return $"{(int)r.Status}:{code}";
    }

    private static string Short(string body) => body.Length > 160 ? body[..160] : body;
    private static Guid IdOf(ApiResponse r) => r.Json.GetProperty("id").GetGuid();
    private static string EtagOf(ApiResponse r) => r.Headers.ETag!.Tag;

    /// <summary>
    /// Sends the same request <see cref="Parallel"/> times in parallel, then once more. <see cref="Mode.One"/>: exactly one 2xx, every other answer one of <paramref name="expected"/>.
    /// <see cref="Mode.Same"/>: every answer has the signature <paramref name="expected"/>[0] ("2xx" for an idempotent success). A 5xx is always a finding. Returns a winner or null.
    /// </summary>
    private async Task<ApiResponse?> Race(string what, string[] expected, HttpMethod method, string path, object? body, string? ifMatch = null, Mode mode = Mode.One)
    {
        var raced = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => Task.Run(() => Call(method, path, body, ifMatch))));
        var again = await Call(method, path, body, ifMatch);
        var all = raced.Append(again).ToList();
        var winners = all.Where(r => Signature(r) == "2xx").ToList();
        diagnostics.Add($"{what} || {winners.Count} won || {string.Join(" ; ", all.Select(Signature).Distinct())} || {Short(all.FirstOrDefault(r => Signature(r) != "2xx")?.Body ?? all[0].Body)}");
        foreach (var r in all.Where(r => (int)r.Status >= 500))
            findings.Add($"{what}: {(int)r.Status} {Short(r.Body)}");
        if (mode == Mode.Same)
        {
            foreach (var s in all.Select(Signature).Distinct().Where(s => s != expected[0]))
                findings.Add($"{what}: {s} where every answer must be {expected[0]}");
            return winners.FirstOrDefault();
        }
        foreach (var r in all.Where(r => Signature(r) != "2xx" && (int)r.Status < 500 && !expected.Contains(Signature(r))))
            findings.Add($"{what}: {Signature(r)} is not the expected refusal ({string.Join(" or ", expected)}): {Short(r.Body)}");
        if (mode == Mode.Append ? winners.Count < 1 : winners.Count != 1)
            findings.Add($"{what}: {winners.Count} requests succeeded (expected {(mode == Mode.Append ? "at least one" : "exactly one")}): {string.Join(", ", all.Select(Signature))}");
        return winners.FirstOrDefault();
    }

    private async Task<ApiResponse> Create(string path, object body, string? ifMatch = null)
    {
        var r = await Call(HttpMethod.Post, path, body, ifMatch);
        Assert.True((int)r.Status is >= 200 and < 300, $"{path}: {(int)r.Status} {r.Body}");
        return r;
    }

    private void AssertNoFindings()
    {
        Assert.True(findings.Count == 0, $"{findings.Count} constraint-violation finding(s):\n" + string.Join("\n", findings) + "\nobserved per probe (winners, signatures):\n" + string.Join("\n", diagnostics));
    }

    private Guid main;

    private async Task Start()
    {
        await StartApi(new WarehouseLimitPolicy(50, 50), b => b.ConfigureServices(s =>
        {
            s.AddSingleton<KekRing>(new InMemoryKekRing("k1"));
            s.AddSingleton(new EncryptionOptions { AllowInsecureFilePermissions = true });
            s.AddSingleton<ICommercialDocumentTypeRegistry>(new CommercialDocumentTypeRegistry([CommercialTestType.Name]));   // the internal test document type (D-049)
        }));
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        foreach (var (code, isBase) in new[] { ("JOD", true), ("USD", false) })
            await Sql($"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('{code}', {(isBase ? "true" : "false")}, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        main = await Sql<Guid>($"SELECT {Br.Id} FROM {Br.Table} WHERE {Br.IsMain}");
        cookie = await SignIn(Admin);
    }

    // ------------------------------------------------------------------------------------------------------------------------------- completeness

    [PostgresFact]
    public async Task Every_mutating_company_endpoint_is_probed_here_in_part_1_or_listed_as_pending()
    {
        // the permission matrix (#119) pins its rows to the API's route table; each of them is in exactly one of: probed here, probed by part 1, pending
        var all = EndpointPermissionMatrixTests.Matrix.Select(r => r.Key).Concat(EndpointPermissionMatrixTests.Excluded.Keys).ToHashSet(StringComparer.Ordinal);
        string[][] lists = [[.. Probed.Keys], [.. CoveredByPart1], [.. Pending]];
        var listed = lists.SelectMany(l => l).ToList();
        var twice = listed.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "routes listed more than once:\n" + string.Join("\n", twice));
        var unclassified = all.Except(listed).Order(StringComparer.Ordinal).ToList();
        Assert.True(unclassified.Count == 0, "mutating company routes neither probed nor listed as pending (classify them in EndpointConstraintMatrixPart2Tests):\n" + string.Join("\n", unclassified));
        var stale = listed.Except(all).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "listed routes the API no longer maps:\n" + string.Join("\n", stale));
        Assert.Equal((32, 60, 74), (Probed.Count, CoveredByPart1.Count, Pending.Count));
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------------------------------------- batch 1

    [PostgresFact]
    public async Task Configuration_singletons_policies_and_assignments_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        var R = new[] { "412:revision_conflict" };

        // chart: settings, template, move under a group
        await Race("chart settings", R, HttpMethod.Put, "/api/v1/company/accounts/settings", new ChartSettingsInput(4, false), "\"0\"");
        await Race("chart template", ["409:chart_not_empty"], HttpMethod.Post, "/api/v1/company/accounts/apply-template", new { template = "service" });
        var groupAccount = await Create("/api/v1/company/accounts", new AccountInput("9400", null, "مجموعة", "Group", "asset", null, true, null, null));
        var leaf = await Create("/api/v1/company/accounts", new AccountInput("9401", null, "حساب", "Leaf", "asset", null, false, null, null));
        await Race("account move", R, HttpMethod.Post, $"/api/v1/company/accounts/{IdOf(leaf)}/move", new AccountMoveInput(IdOf(groupAccount)), EtagOf(leaf));

        // company configuration singletons
        await Race("profile", R, HttpMethod.Put, "/api/v1/company/profile",
            new ProfileInput("شركة", "Company", null, null, null, "SA", null, null, null, null, null, null, null, null), "\"0\"");
        await Race("display-name sync", ["2xx"], HttpMethod.Post, "/api/v1/company/profile/display-name-sync", null, mode: Mode.Same);
        await Race("currency disable", R, HttpMethod.Put, "/api/v1/company/currencies/USD", new { enabled = false, isBase = false }, "\"1\"");
        await Race("inventory settings", R, HttpMethod.Put, "/api/v1/company/inventory/settings", new InventorySettingsInput(1), "\"0\"");
        var rounding = await Create("/api/v1/company/accounts", new AccountInput("9402", null, "تقريب", "Rounding", "expense", null, false, null, null));
        await Race("ledger settings", R, HttpMethod.Put, "/api/v1/company/ledger-settings", new LedgerSettingsInput(IdOf(rounding)), "\"0\"");
        await Race("negative-stock settings", R, HttpMethod.Put, "/api/v1/company/negative-stock/settings", new { enabled = true, requireSecondApprover = false, note = "matrix" }, "\"0\"");
        await Race("numbering rule", R, HttpMethod.Put, "/api/v1/company/numbering/journal_entry", new NumberingRuleInput("J", "-", 5, false, false), "\"0\"");
        // a racing writer that passes the revision check meets the version guard (409 version_conflict) or a database guard (409 invariant_violation, the mapped RaiseException)
        await Race("pricing policy", ["412:revision_conflict", "409:version_conflict", "409:invariant_violation"], HttpMethod.Post, "/api/v1/company/pricing/policy", new PricingPolicyInput(2, 2, 5m, (int)MarginAction.Warn, (int)PriceListPrecedence.PriorityThenScope, (int)DiscountPrecedence.PriorityThenSpecificity), "\"0\"");
        // the tax rounding policy APPENDS a version without a precondition: parallel requests may each append, and a clash on the version number is the mapped 409
        await Race("tax policy", ["409:version_conflict"], HttpMethod.Post, "/api/v1/company/taxes/policy", new TaxPolicyInput(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerLine), mode: Mode.Append);

        // branches: make-main from one revision
        var branch = await Create("/api/v1/company/branches", new BranchInput("CV2B", "فرع", "Branch", null, null, null, null, null, null, null, null));
        await Race("branch make-main", R, HttpMethod.Post, $"/api/v1/company/branches/{IdOf(branch)}/make-main", null, EtagOf(branch));

        // cost-center policies
        var center = await Create("/api/v1/company/cost-centers", new CostCenterInput("CV2C", null, "مركز", "Center", null, false));
        await Race("account cost-center rule", R, HttpMethod.Put, $"/api/v1/company/cost-center-policies/accounts/{IdOf(leaf)}", new AccountCostCenterRuleInput(true), "\"0\"");
        await Race("cost-center branch rule", R, HttpMethod.Put, $"/api/v1/company/cost-center-policies/branches/{IdOf(center)}", new CostCenterBranchRuleInput(true, [main]), "\"0\"");

        // inventory accounting and negative stock per item
        var unit = await Create("/api/v1/company/inventory/units", new UnitInput("CV2U", "وحدة", "Unit", 2));
        var group = await Create("/api/v1/company/inventory/groups", new ItemGroupInput("CV2G", "مجموعة", "Group", null, 1));
        var item = await Create("/api/v1/company/inventory/items", new ItemInput("CV2I", "صنف", "Item", 1, IdOf(group), IdOf(unit), null, null, false, false, false, null, []));
        var stock = await Create("/api/v1/company/accounts", new AccountInput("9403", null, "مخزون", "Stock", "asset", null, false, null, null));
        await Race("inventory accounting", R, HttpMethod.Put, "/api/v1/company/inventory/accounting",
            new InventoryAccountingInput(IdOf(stock), IdOf(leaf), IdOf(leaf), [], "matrix"), "\"0\"");
        var averaged = await Create("/api/v1/company/inventory/groups", new ItemGroupInput("CV2A", "مجموعة", "Average", null, (int)ValuationMethod.MovingAverage));
        var eligible = await Create("/api/v1/company/inventory/items", new ItemInput("CV2E", "صنف", "Eligible", 1, IdOf(averaged), IdOf(unit), null, null, false, false, false, null, []));
        // the per-item eligibility is a SET without a revision: repeating it is idempotent
        await Race("negative stock per item", ["2xx"], HttpMethod.Put, $"/api/v1/company/negative-stock/items/{IdOf(eligible)}", new { eligible = true, note = "matrix" }, mode: Mode.Same);

        // taxes: assignments and rules
        var tax = await Create("/api/v1/company/taxes", new TaxInput("CV2T", "ضريبة", "Tax", true));
        var itemClass = await Create("/api/v1/company/taxes/classes", new TaxClassInput(TaxClassKind.Item, "CV2K", "فئة", "Items", true));
        var partyClass = await Create("/api/v1/company/taxes/classes", new TaxClassInput(TaxClassKind.Party, "CV2Q", "فئة", "Parties", true));
        // a class assignment is a SET without a revision: repeating it is idempotent
        await Race("item tax class", ["2xx"], HttpMethod.Put, $"/api/v1/company/taxes/item-classes/{IdOf(item)}", new { classId = IdOf(itemClass) }, mode: Mode.Same);
        var party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'CV2P', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
        await Create("/api/v1/company/party-roles", new PartyRoleInput(party, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null));
        await Race("party tax class", ["2xx"], HttpMethod.Put, $"/api/v1/company/taxes/party-classes/{party}", new { classId = IdOf(partyClass) }, mode: Mode.Same);
        var rule = await Race("tax rule", ["409:rule_overlap"], HttpMethod.Post, "/api/v1/company/taxes/rules",
            new TaxRuleInput(CommercialTestType.Name, CommercialDefaults.TaxOperation, IdOf(itemClass), null, IdOf(tax), 1, [], new DateOnly(2026, 1, 1), null));
        if (rule is not null)
            await Race("tax rule update", R, HttpMethod.Put, $"/api/v1/company/taxes/rules/{IdOf(rule)}",
                new TaxRuleInput(CommercialTestType.Name, CommercialDefaults.TaxOperation, IdOf(itemClass), null, IdOf(tax), 2, [], new DateOnly(2026, 1, 1), null), EtagOf(rule));

        // pricing: customer-group assignment and the read-only quote
        var customerGroup = await Create("/api/v1/company/pricing/customer-groups", new CustomerGroupInput("CV2G", "مجموعة", "Group", true));
        await Race("customer-group assignment", R, HttpMethod.Put, $"/api/v1/company/pricing/customer-assignments/{party}", new CustomerGroupAssignmentInput(IdOf(customerGroup)), "\"0\"");
        var list = await Create("/api/v1/company/pricing/price-lists", new PriceListInput("CV2L", "قائمة", "List", 10, "JOD", new DateOnly(2026, 1, 1), null, true, null));
        await Create($"/api/v1/company/pricing/price-lists/{IdOf(list)}/entries", new PriceEntryInput(IdOf(item), null, IdOf(unit), 0m, 10m, null));
        await Race("price quote", ["2xx"], HttpMethod.Post, "/api/v1/company/pricing/quote",
            new { date = "2026-03-10", currency = "JOD", itemId = IdOf(item), unitId = IdOf(unit), quantity = 1, customerId = party }, mode: Mode.Same);

        // parties: update, propose, sync, reconcile
        var created = await Create("/api/v1/company/parties", new { code = "CV2R", displayName = "Party", isActive = true, identity = new { kind = 1, legalNameAr = "طرف" } });
        var renamed = await Race("party update", R, HttpMethod.Put, $"/api/v1/company/parties/{IdOf(created)}", new LocalPartyInput("CV2R", "Renamed", null, true), EtagOf(created));
        // a party created with an identity has its proposal open: every parallel proposal meets it (the registry refuses a second open request)
        await Race("party propose", ["409:request_open"], HttpMethod.Post, $"/api/v1/company/parties/{IdOf(created)}/propose", new SharedIdentityInput(1, "تغيير", null, null, null, 1), EtagOf(renamed ?? created), Mode.Same);
        Assert.NotNull(renamed);
        await Race("party sync", ["2xx"], HttpMethod.Post, "/api/v1/company/parties/sync", null, mode: Mode.Same);
        await Race("party reconcile", ["2xx"], HttpMethod.Post, "/api/v1/company/parties/reconcile?repair=false", null, mode: Mode.Same);

        // statement mapping and approval policies
        await Race("statement mapping", R, HttpMethod.Put, "/api/v1/company/statements/mapping", new StatementMappingInput([new StatementMappingEntryInput(IdOf(leaf), "current_assets")], "matrix", null), "\"0\"");
        // approval policies carry no uniqueness rule (several policies per document type are allowed), so each create appends one: no refusal is expected
        var policy = await Race("approval policy", [], HttpMethod.Post, "/api/v1/company/approval-policies",
            new ApprovalPolicyInput(DocumentTypes.JournalEntry, "manual", null, 100m, 1, "سياسة", "Policy"), mode: Mode.Append);
        if (policy is not null)
        {
            var updated = await Race("approval policy update", R, HttpMethod.Put, $"/api/v1/company/approval-policies/{IdOf(policy)}",
                new ApprovalPolicyDetailsInput("manual", null, 200m, 1, "سياسة", "Renamed"), EtagOf(policy));
            var off = await Race("approval policy deactivate", R, HttpMethod.Post, $"/api/v1/company/approval-policies/{IdOf(policy)}/deactivate", null, EtagOf(updated ?? policy));
            if (off is not null) await Race("approval policy activate", R, HttpMethod.Post, $"/api/v1/company/approval-policies/{IdOf(policy)}/activate", null, EtagOf(off));
        }

        AssertNoFindings();
    }
}
