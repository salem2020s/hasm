using System.Net;
using System.Net.Http.Headers;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;
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
using Ni = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Constraint-violation matrix, part 2 (planning item 4): the mutating company routes part 1 (<c>EndpointConstraintMatrixTests</c>, #155) left pending. The rule and the engine are
/// part 1's: the same request is sent <see cref="Parallel"/> times in parallel and once more, and every answer is either the probe's winner or its EXPECTED refusal, never a 5xx
/// (the global handler maps an unhandled PostgreSQL error to 503 <c>service_unavailable</c>; the only 5xx a probe may pin is a documented one, the attachment upload gate's 503 <c>upload_busy</c>). Probe modes: <see cref="Mode.One"/> (exactly one 2xx; a create, update or transition from one revision),
/// <see cref="Mode.Same"/> (a read-only or idempotent operation: every answer has the same status and code), <see cref="Mode.Append"/> (an operation that appends a new
/// version without a precondition: at least one 2xx, every refusal the expected version conflict). Every finding of a run is reported together.
/// Disposable cluster only.
/// </summary>
public class EndpointConstraintMatrixPart2Tests : JournalTestBase
{
    private const int Parallel = 6;
    public enum Mode { One, Same, Append }

    /// <summary>
    /// The routes part 2 probes, by the test that probes them (its SHARE), each with the kind of probe. Every probing test ends with <see cref="ConstraintRaces.AssertRan"/> on its share:
    /// the routes it actually raced must equal its share exactly (review L1 of #161: a route is PROBED, not only classified). Literals and <c>nameof</c> only, so this field never
    /// reads another class's static state while initializing (review H1 of #161).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Shares = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        [nameof(Configuration_singletons_policies_and_assignments_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
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
            ["POST /api/v1/company/approval-policies/"] = "duplicate (409 approval_policy_duplicate)",
            ["PUT /api/v1/company/approval-policies/{id:guid}"] = "stale-revision race",
            ["POST /api/v1/company/approval-policies/{id:guid}/deactivate"] = "stale-revision race",
            ["POST /api/v1/company/approval-policies/{id:guid}/activate"] = "stale-revision race",
        },
        [nameof(Journal_entries_from_creation_to_reversal_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["POST /api/v1/company/journal/"] = "append (no uniqueness rule)",
            ["PUT /api/v1/company/journal/{id:guid}"] = "stale-revision race",
            ["POST /api/v1/company/journal/{id:guid}/decision"] = "decision race (409 entry_not_pending_approval)",
            ["POST /api/v1/company/journal/{id:guid}/post"] = "idempotent retry (same answer, one number)",
            ["POST /api/v1/company/journal/{id:guid}/reverse"] = "transition race (409 already_reversed)",
        },
        [nameof(The_fiscal_calendar_closing_and_reopening_requests_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["POST /api/v1/company/fiscal-periods/{id:guid}/final-close"] = "stale-revision race",
            ["POST /api/v1/company/fiscal-periods/{id:guid}/reopen-requests"] = "open-request race (409 reopen_request_pending)",
            ["POST /api/v1/company/fiscal-reopen-requests/{id:guid}/approve"] = "decision race (409 request_not_pending)",
            ["POST /api/v1/company/fiscal-reopen-requests/{id:guid}/cancel"] = "decision race (409 request_not_pending)",
            ["POST /api/v1/company/fiscal-reopen-requests/{id:guid}/deny"] = "decision race (409 request_not_pending)",
            ["POST /api/v1/company/fiscal-years/{id:guid}/close"] = "stale-revision race",
            ["POST /api/v1/company/fiscal-years/{id:guid}/reopen-requests"] = "open-request race (409 reopen_request_pending)",
        },
        [nameof(Year_end_closing_and_carry_forward_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["PUT /api/v1/company/fiscal-closing/settings"] = "stale-revision race",
            ["POST /api/v1/company/fiscal-closing/years/{yearId:guid}/close"] = "transition race (409 closing_blocked)",
            ["POST /api/v1/company/fiscal-closing/years/{yearId:guid}/carry-forward"] = "transition race (409 closing_blocked)",
            ["POST /api/v1/company/fiscal-closing/years/{yearId:guid}/reverse-opening"] = "transition race (409 already_reversed)",
            ["POST /api/v1/company/fiscal-closing/years/{yearId:guid}/reverse-closing"] = "transition race (409 already_reversed)",
        },
        [nameof(Attachments_and_their_retention_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["POST /api/v1/company/attachments/"] = "append (503 upload_busy back-pressure pinned)",
            ["POST /api/v1/company/attachments/{id:guid}/remove"] = "transition race (409 already_removed)",
            ["POST /api/v1/company/attachments/maintenance/rewrap"] = "idempotent (same answer)",
            ["POST /api/v1/company/attachments/maintenance/sweep"] = "idempotent (same answer)",
            ["POST /api/v1/company/attachments/maintenance/verify"] = "read-only (same answer)",
            ["POST /api/v1/company/attachment-retention/policies"] = "append (no uniqueness rule)",
            ["POST /api/v1/company/attachment-retention/policies/{id:guid}/approve"] = "decision race (409 not_a_draft)",
            ["POST /api/v1/company/attachment-retention/policies/{id:guid}/revoke"] = "decision race (409 not_approved)",
            ["POST /api/v1/company/attachment-retention/holds"] = "append (no uniqueness rule)",
            ["POST /api/v1/company/attachment-retention/holds/{id:guid}/release"] = "decision race (409 already_released)",
        },
        [nameof(EndpointConstraintMatrixPart2StockTests.Stock_documents_their_posting_reversal_and_negative_exception_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["POST /api/v1/company/stock-documents/"] = "append (no uniqueness rule)",
            ["PUT /api/v1/company/stock-documents/{id:guid}"] = "stale-revision race",
            ["POST /api/v1/company/stock-documents/{id:guid}/submit"] = "stale-revision race",
            ["POST /api/v1/company/stock-documents/{id:guid}/withdraw"] = "stale-revision race",
            ["POST /api/v1/company/stock-documents/{id:guid}/cancel"] = "stale-revision race",
            ["POST /api/v1/company/stock-documents/{id:guid}/approve"] = "stale-revision race",
            ["POST /api/v1/company/stock-documents/{id:guid}/reject"] = "stale-revision race",
            ["POST /api/v1/company/stock-documents/{id:guid}/posting/"] = "idempotent replay (same answer, one posting)",
            ["POST /api/v1/company/stock-documents/{id:guid}/reversal"] = "idempotent replay (same answer, one reversal)",
            ["POST /api/v1/company/stock-documents/{id:guid}/negative-exception/approvals"] = "approval race (409 already_approved)",
        },
        [nameof(EndpointConstraintMatrixPart2StockTests.Stocktakes_and_stock_adjustments_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["POST /api/v1/company/stocktakes/"] = "append (no uniqueness rule)",
            ["PUT /api/v1/company/stocktakes/{id:guid}"] = "stale-revision race",
            ["POST /api/v1/company/stocktakes/{id:guid}/generate-sheet"] = "stale-revision race",
            ["POST /api/v1/company/stocktakes/{id:guid}/submit"] = "stale-revision race",
            ["POST /api/v1/company/stocktakes/{id:guid}/withdraw"] = "stale-revision race",
            ["POST /api/v1/company/stocktakes/{id:guid}/cancel"] = "stale-revision race",
            ["POST /api/v1/company/stocktakes/{id:guid}/approve"] = "stale-revision race",
            ["POST /api/v1/company/stocktakes/{id:guid}/reject"] = "stale-revision race",
            ["POST /api/v1/company/stocktakes/{id:guid}/adjustment"] = "idempotent replay (same answer, one adjustment)",
            ["POST /api/v1/company/stock-adjustments/{id:guid}/posting"] = "idempotent replay (same answer, one posting)",
            ["POST /api/v1/company/stock-adjustments/{id:guid}/reversal"] = "idempotent replay (same answer, one reversal)",
        },
        [nameof(EndpointConstraintMatrixPart2CommercialTests.Commercial_documents_and_links_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["POST /api/v1/company/commercial-documents/"] = "idempotent replay (same answer, one document)",
            ["PUT /api/v1/company/commercial-documents/{id:guid}"] = "stale-revision race",
            ["POST /api/v1/company/commercial-documents/{id:guid}/submit"] = "stale-revision race",
            ["POST /api/v1/company/commercial-documents/{id:guid}/withdraw"] = "stale-revision race",
            ["POST /api/v1/company/commercial-documents/{id:guid}/cancel"] = "stale-revision race",
            ["POST /api/v1/company/commercial-documents/{id:guid}/approve"] = "stale-revision race",
            ["POST /api/v1/company/commercial-documents/{id:guid}/reject"] = "stale-revision race",
            ["POST /api/v1/company/commercial-documents/links"] = "idempotent replay (same answer, one link)",
            ["POST /api/v1/company/commercial-documents/{id:guid}/short-close"] = "idempotent replay (same answer, one short close)",
        },
        [nameof(EndpointConstraintMatrixPart2ImportTests.Imports_from_upload_to_application_with_templates_settings_and_maintenance_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["POST /api/v1/company/imports/{id:guid}/stage"] = "stale-revision race",
            ["PUT /api/v1/company/imports/{id:guid}/mapping"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/dry-run"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/submit"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/withdraw"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/reject"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/approve"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/apply"] = "idempotent retry (same answer, applied once)",
            ["POST /api/v1/company/imports/{id:guid}/renew"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/cancel"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/resolutions"] = "stale-revision race",
            ["POST /api/v1/company/imports/{id:guid}/mapping/template"] = "version race (412 revision_conflict)",
            ["POST /api/v1/company/imports/{id:guid}/mapping/apply-template"] = "read-only prefill (same answer)",
            ["POST /api/v1/company/imports/templates/{adapter}/{name}/retire"] = "version race (412 revision_conflict)",
            ["POST /api/v1/company/imports/maintenance/run"] = "idempotent (same answer)",
            ["POST /api/v1/company/imports/maintenance/rewrap"] = "idempotent (same answer)",
            ["POST /api/v1/company/imports/"] = "idempotent replay (same answer, one batch)",
            ["PUT /api/v1/company/imports/settings"] = "stale-revision race (first save included)",
        },
        [nameof(EndpointConstraintMatrixPart2CashTests.Cash_and_bank_accounts_end_in_a_mapped_4xx_never_a_5xx)] = new Dictionary<string, string>
        {
            ["POST /api/v1/company/cash-accounts/"] = "duplicate code",
            ["PUT /api/v1/company/cash-accounts/{id:guid}"] = "stale-revision race",
            ["POST /api/v1/company/cash-accounts/{id:guid}/deactivate"] = "transition race",
            ["POST /api/v1/company/cash-accounts/{id:guid}/activate"] = "transition race",
            ["POST /api/v1/company/cash-accounts/maintenance/rewrap"] = "idempotent (same answer)",
        },
    };

    /// <summary>The routes part 2 probes (the union of <see cref="Shares"/>), each with the kind of probe. A property: evaluated on use, never during type initialization.</summary>
    public static IReadOnlyDictionary<string, string> Probed => Shares.Values.SelectMany(s => s).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    /// <summary>The routes part 1 (<see cref="EndpointConstraintMatrixTests"/>, #155) probes: its own list, so the two classes can never disagree.</summary>
    /// <remarks>A property, not a field initializer: the two classes refer to each other's lists, and static field initializers would depend on which type initializes first.</remarks>
    public static IReadOnlyList<string> CoveredByPart1 => [.. EndpointConstraintMatrixTests.Probed.Keys];

    /// <summary>
    /// The explicit place for any mutating company route that is NOT probed (by part 2's <see cref="Shares"/> or by part 1), listed by family with the reason. Empty today: every
    /// one of the 166 routes is probed. A route the API adds must be put in a share (and probed there) or listed here, or the completeness test fails (review L2 of #168).
    /// </summary>
    public static readonly IReadOnlyList<string> Pending =
    [
    ];


    // ------------------------------------------------------------------------------------------------------------------------------- engine

    private readonly ConstraintRaces races = new(Parallel);
    private string cookie = "";

    private Task<ApiResponse> Call(HttpMethod method, string path, object? body, string? ifMatch = null) => Send(method, path, body, cookie, ifMatch: ifMatch);
    private static Guid IdOf(ApiResponse r) => ConstraintRaces.IdOf(r);
    private static string EtagOf(ApiResponse r) => ConstraintRaces.EtagOf(r);

    /// <summary>One probe (<see cref="ConstraintRaces.Run"/>) of the same request as <paramref name="caller"/> (the administrator by default).</summary>
    private Task<ApiResponse?> Race(string what, string[] expected, HttpMethod method, string path, object? body, string? ifMatch = null, Mode mode = Mode.One, string? caller = null) =>
        races.Run(what, expected, method, path, () => Send(method, path, body, caller ?? cookie, ifMatch: ifMatch), mode);

    /// <summary>One probe of any request (a multipart upload).</summary>
    private Task<ApiResponse?> RaceWith(string what, string[] expected, HttpMethod method, string path, Func<Task<ApiResponse>> send, Mode mode = Mode.One) => races.Run(what, expected, method, path, send, mode);

    private async Task<ApiResponse> Create(string path, object body, string? ifMatch = null)
    {
        var r = await Call(HttpMethod.Post, path, body, ifMatch);
        Assert.True((int)r.Status is >= 200 and < 300, $"{path}: {(int)r.Status} {r.Body}");
        return r;
    }

    private ApiResponse Won(ApiResponse? winner) => races.Won(winner);
    private void AssertNoFindings() => races.AssertNoFindings();

    private Guid main;
    private string? storage;

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (storage is not null && Directory.Exists(storage)) try { Directory.Delete(storage, true); } catch (IOException) { }
    }

    private async Task Start()
    {
        storage = Path.Combine(Path.GetTempPath(), "hasm-cvm2-" + Guid.NewGuid().ToString("N"));
        await StartApi(new WarehouseLimitPolicy(50, 50), b => b.UseSetting("Hasm:Attachments:StorageDirectory", storage).ConfigureServices(s =>
        {
            s.AddSingleton<KekRing>(new InMemoryKekRing("k1", TestIdentityKeys.Master));
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
        Main = main;
        cookie = await SignIn(Admin);
    }

    // ------------------------------------------------------------------------------------------------------------------------------- completeness

    private static string SamplePath(string template) =>
        string.Join("/", template.Split('/').Select(seg => seg.StartsWith('{') ? (seg.EndsWith(":guid}", StringComparison.Ordinal) ? Guid.NewGuid().ToString() : "sample1") : seg));

    [PostgresFact]
    public async Task Every_mutating_company_endpoint_is_probed_here_in_part_1_or_listed_as_pending()
    {
        // the permission matrix (#119) pins its rows to the API's route table; each of them is in exactly one of: probed here, probed by part 1, pending
        var all = EndpointPermissionMatrixTests.Matrix.Select(r => r.Key).Concat(EndpointPermissionMatrixTests.Excluded.Keys).ToHashSet(StringComparer.Ordinal);
        // the shares themselves (not their union) so that a route in two shares is reported as listed twice
        string[][] lists = [[.. Shares.Values.SelectMany(share => share.Keys)], [.. CoveredByPart1], [.. Pending]];
        var listed = lists.SelectMany(l => l).ToList();
        var twice = listed.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "routes listed more than once:\n" + string.Join("\n", twice));
        var unclassified = all.Except(listed).Order(StringComparer.Ordinal).ToList();
        Assert.True(unclassified.Count == 0, "mutating company routes neither probed nor listed as pending (classify them in EndpointConstraintMatrixPart2Tests):\n" + string.Join("\n", unclassified));
        var stale = listed.Except(all).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "listed routes the API no longer maps:\n" + string.Join("\n", stale));
        Assert.Equal((112, 60, 0), (Probed.Count, CoveredByPart1.Count, Pending.Count));   // 112 with the short close (D-201)
        Assert.Equal([32, 5, 7, 5, 10, 10, 11, 8, 18, 5], Shares.Values.Select(share => share.Count));
        // AssertRan resolves each probed request to its route through the route table: every route must be reachable by exactly one template (no ambiguity)
        var ambiguous = all.Where(t => ConstraintRaces.TemplateOf(new HttpMethod(t[..t.IndexOf(' ')]), SamplePath(t[(t.IndexOf(' ') + 1)..]), all) != t).Order(StringComparer.Ordinal).ToList();
        Assert.True(ambiguous.Count == 0, "routes the probe resolver cannot tell apart:\n" + string.Join("\n", ambiguous));
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
        // documented idempotent: docs/DEVELOPMENT.md (company configuration endpoints: "recovery of a failed projection") and CompanyConfigurationService.SyncDisplayNameAsync
        // ("re-projects the authoritative profile to the registry (idempotent, monotonic)") (review L2 of #161)
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
        // the per-item eligibility is a SET without a revision. No phase document states its idempotency; the code contract does: NegativeStockService.SetItemFlagAsync answers
        // the current flag unchanged when the requested value equals it (no new history row), so repeating it is idempotent. Pinned as observed (review L2 of #161).
        await Race("negative stock per item", ["2xx"], HttpMethod.Put, $"/api/v1/company/negative-stock/items/{IdOf(eligible)}", new { eligible = true, note = "matrix" }, mode: Mode.Same);

        // taxes: assignments and rules
        var tax = await Create("/api/v1/company/taxes", new TaxInput("CV2T", "ضريبة", "Tax", true));
        var itemClass = await Create("/api/v1/company/taxes/classes", new TaxClassInput(TaxClassKind.Item, "CV2K", "فئة", "Items", true));
        var partyClass = await Create("/api/v1/company/taxes/classes", new TaxClassInput(TaxClassKind.Party, "CV2Q", "فئة", "Parties", true));
        // a class assignment is a SET without a revision: docs/PHASE-3.10.md (API: "PUT item-classes/{itemId} and party-classes/{partyId} (body {classId}, null removes)") defines
        // the SET but states no idempotency; the code contract is an upsert (TaxConfigService.Assign: INSERT ... ON CONFLICT DO UPDATE), so repeating the same class leaves the
        // same state. Pinned as observed for both item and party classes (review L2 of #161).
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
        // a semantically identical ACTIVE policy is refused (company migration 0033, owner decision of 2026-11-21): of the identical parallel creates exactly one wins and every other gets the mapped 409
        var policy = await Race("approval policy", ["409:approval_policy_duplicate"], HttpMethod.Post, "/api/v1/company/approval-policies",
            new ApprovalPolicyInput(DocumentTypes.JournalEntry, "manual", null, 100m, 1, "سياسة", "Policy"));
        if (policy is not null)
        {
            var updated = await Race("approval policy update", R, HttpMethod.Put, $"/api/v1/company/approval-policies/{IdOf(policy)}",
                new ApprovalPolicyDetailsInput("manual", null, 200m, 1, "سياسة", "Renamed"), EtagOf(policy));
            var off = await Race("approval policy deactivate", R, HttpMethod.Post, $"/api/v1/company/approval-policies/{IdOf(policy)}/deactivate", null, EtagOf(updated ?? policy));
            if (off is not null) await Race("approval policy activate", R, HttpMethod.Post, $"/api/v1/company/approval-policies/{IdOf(policy)}/activate", null, EtagOf(off));
        }

        AssertNoFindings();
        races.AssertRan(Shares[nameof(Configuration_singletons_policies_and_assignments_end_in_a_mapped_4xx_never_a_5xx)]);
    }

    // ------------------------------------------------------------------------------------------------------------------------------- batch 2

    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A, 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A];

    /// <summary>A company member holding the preset approver role named by <paramref name="grant"/> (fiscal or retention), delegated the supported way (offline tool).</summary>
    private async Task<UserRecord> PresetApprover(Func<string, Guid, Task<AccessService.FiscalApproverStatus>> grant)
    {
        var user = await NewUser($"cv2approver{Guid.NewGuid():N}"[..24]);
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, user.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await grant(user.Login, Company.Id));
        return user;
    }

    /// <summary>Submits, approves with an independent approver and posts an entry generated by year-end processing (domain services: the probes are the HTTP calls).</summary>
    private async Task PostGenerated(Guid entryId)
    {
        var v = await Reload(entryId);
        var s = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, entryId, v.Header.Revision));
        if (s.Header.Status == JournalStatus.PendingApproval) s = Ok(await Approve(s, Checker1));
        Ok(await Journal.PostAsync(Checker3, Company.Id, entryId, s.Header.Revision, false));
    }

    [PostgresFact]
    public async Task Journal_entries_from_creation_to_reversal_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        var R = new[] { "412:revision_conflict" };
        Cash = await NewAccount("1101", "asset");
        Sales = await NewAccount("4101", "revenue");
        // one approval for every entry, decided by a separate approver; posting by a separate poster
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(DocumentTypes.JournalEntry, "any", null, 0m, 1, "سياسة", "Policy")));
        var approver = await SignIn(await PresetApprover((login, company) => Rbac.GrantJournalApproverOfflineAsync(login, company)));
        var poster = await SignIn((await Member([PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalPost], ScopeKind.Company, null)).User);

        // a create carries no precondition and no uniqueness rule: each parallel request appends one draft
        var draft = await Race("journal create", [], HttpMethod.Post, "/api/v1/company/journal", Simple("2026-03-10", 10m), mode: Mode.Append);
        Won(draft);
        var id = draft!.Json.GetProperty("entry").GetProperty("id").GetGuid();
        var updated = await Race("journal update", R, HttpMethod.Put, $"/api/v1/company/journal/{id}", Simple("2026-03-11", 20m), EtagOf(draft));
        Won(updated);
        var submitted = await Create($"/api/v1/company/journal/{id}/submit", null!, EtagOf(updated!));
        var decided = await Race("journal decision", ["412:revision_conflict", "409:already_decided", "409:entry_not_pending_approval"], HttpMethod.Post, $"/api/v1/company/journal/{id}/decision",
            new ApprovalDecisionInput(true, "ok"), EtagOf(submitted), caller: approver);
        Won(decided);
        // posting keeps the revision and a retry of a posted entry answers it unchanged (an idempotent retry): every answer is the posted entry, and it carries ONE number
        var posted = await Race("journal post", ["2xx"], HttpMethod.Post, $"/api/v1/company/journal/{id}/post", null, EtagOf(decided!), Mode.Same, poster);
        Won(posted);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ni.Table} WHERE {Ni.DocumentId} = '{id}'"));
        await Race("journal reverse", ["412:revision_conflict", "409:entry_not_posted", "409:already_reversed"], HttpMethod.Post, $"/api/v1/company/journal/{id}/reverse",
            new ReversalInput(new DateOnly(2026, 3, 20), "matrix"), EtagOf(posted!));

        AssertNoFindings();
        races.AssertRan(Shares[nameof(Journal_entries_from_creation_to_reversal_end_in_a_mapped_4xx_never_a_5xx)]);
    }

    [PostgresFact]
    public async Task The_fiscal_calendar_closing_and_reopening_requests_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        var R = new[] { "412:revision_conflict" };
        var fiscalApprover = await SignIn(await PresetApprover((login, company) => Rbac.GrantFiscalApproverOfflineAsync(login, company)));
        var p1 = Year.Periods[0];
        var soft = Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, p1.Id, p1.Revision));
        var final = await Race("period final-close", R, HttpMethod.Post, $"/api/v1/company/fiscal-periods/{p1.Id}/final-close", null, $"\"{soft.Revision}\"");
        Won(final);
        var periodTag = EtagOf(final!);

        // a reopening is a request: one open request per target; cancel, deny and approve each decide it once
        var requestBody = new ReasonInput("Correction of a posting error found in audit");
        string[] requestOpen = ["409:reopen_request_pending", "412:revision_conflict"];
        string[] notPending = ["409:request_not_pending", "412:revision_conflict"];
        var first = await Race("period reopen request", requestOpen, HttpMethod.Post, $"/api/v1/company/fiscal-periods/{p1.Id}/reopen-requests", requestBody, periodTag);
        Won(first);
        await Race("reopen request cancel", notPending, HttpMethod.Post, $"/api/v1/company/fiscal-reopen-requests/{IdOf(first!)}/cancel", null, EtagOf(first!));
        var second = await Create($"/api/v1/company/fiscal-periods/{p1.Id}/reopen-requests", requestBody, periodTag);
        await Race("reopen request deny", notPending, HttpMethod.Post, $"/api/v1/company/fiscal-reopen-requests/{IdOf(second)}/deny", new { note = "not needed" }, EtagOf(second), caller: fiscalApprover);
        var third = await Create($"/api/v1/company/fiscal-periods/{p1.Id}/reopen-requests", requestBody, periodTag);
        await Race("reopen request approve", notPending, HttpMethod.Post, $"/api/v1/company/fiscal-reopen-requests/{IdOf(third)}/approve", null, EtagOf(third), caller: fiscalApprover);

        // the year closes once every period is final closed; its reopening is a request too
        var year = (await Cal.GetAsync(Company.Id, Year.Id))!;
        foreach (var p in year.Periods)
        {
            var current = p;
            if (current.State == FiscalPeriodState.Open) current = Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, current.Id, current.Revision));
            if (current.State == FiscalPeriodState.SoftClosed) Ok(await Cal.FinalCloseAsync(Admin.Id, Company.Id, current.Id, current.Revision));
        }
        year = (await Cal.GetAsync(Company.Id, Year.Id))!;
        var closed = await Race("fiscal year close", ["412:revision_conflict", "409:fiscal_year_already_closed"], HttpMethod.Post, $"/api/v1/company/fiscal-years/{Year.Id}/close", null, $"\"{year.Revision}\"");
        Won(closed);
        await Race("year reopen request", requestOpen, HttpMethod.Post, $"/api/v1/company/fiscal-years/{Year.Id}/reopen-requests", requestBody, EtagOf(closed!));

        AssertNoFindings();
        races.AssertRan(Shares[nameof(The_fiscal_calendar_closing_and_reopening_requests_end_in_a_mapped_4xx_never_a_5xx)]);
    }

    [PostgresFact]
    public async Task Year_end_closing_and_carry_forward_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2027", new DateOnly(2027, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset");
        Sales = await NewAccount("4101", "revenue");
        var equity = await NewAccount("3101", "equity");
        await Posted(Simple("2026-03-10", 100m));
        const string Base = "/api/v1/company/fiscal-closing";
        await Race("closing settings", ["412:revision_conflict"], HttpMethod.Put, $"{Base}/settings", new ClosingSettingsInput(equity.Id, true), "\"0\"");

        // close, carry forward and the two reversals carry no precondition: one request wins, every other one meets the state it created
        var closing = await Race("year-end close", ["409:closing_blocked"], HttpMethod.Post, $"{Base}/years/{Year.Id}/close", null);
        Won(closing);
        await PostGenerated(closing!.Json.GetProperty("closingEntryId").GetGuid());
        var carry = await Race("carry forward", ["409:closing_blocked"], HttpMethod.Post, $"{Base}/years/{Year.Id}/carry-forward", null);
        Won(carry);
        await PostGenerated(carry!.Json.GetProperty("entryId").GetGuid());
        var opening = await Race("reverse opening", ["409:already_reversed"], HttpMethod.Post, $"{Base}/years/{Year.Id}/reverse-opening", new YearEndReversalInput(new DateOnly(2027, 1, 1)));
        Won(opening);
        await PostGenerated(opening!.Json.GetProperty("entryId").GetGuid());
        await Race("reverse closing", ["409:already_reversed"], HttpMethod.Post, $"{Base}/years/{Year.Id}/reverse-closing", new YearEndReversalInput(new DateOnly(2026, 12, 31)));

        AssertNoFindings();
        races.AssertRan(Shares[nameof(Year_end_closing_and_carry_forward_end_in_a_mapped_4xx_never_a_5xx)]);
    }

    private async Task<ApiResponse> Upload(Guid subject)
    {
        using var form = new MultipartFormDataContent { { new StringContent("local_party"), "subjectKind" }, { new StringContent(subject.ToString()), "subjectId" }, { new StringContent("false"), "sensitive" } };
        var file = new ByteArrayContent(Pdf); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf"); form.Add(file, "file", "doc.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/company/attachments") { Content = form };
        request.Headers.Add("X-Hasm-Request", "1");
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task Attachments_and_their_retention_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        var R = new[] { "412:revision_conflict" };
        var party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'CV2P', 'p', true, 1, 1, '{Admin.Id}', now(), now())");

        // uploads append one attachment each; the upload gate turns a parallel one away with its documented back-pressure answer (503 upload_busy, PHASE-3.12), pinned exactly
        var uploaded = await RaceWith("attachment upload", ["503:upload_busy"], HttpMethod.Post, "/api/v1/company/attachments", () => Upload(party), Mode.Append);
        Won(uploaded);
        await Race("attachment remove", ["412:revision_conflict", "409:already_removed"], HttpMethod.Post, $"/api/v1/company/attachments/{IdOf(uploaded!)}/remove", new { reason = "matrix" }, EtagOf(uploaded!));
        foreach (var task in new[] { "rewrap", "sweep", "verify" })
            await Race($"attachment {task}", ["2xx"], HttpMethod.Post, $"/api/v1/company/attachments/maintenance/{task}", null, mode: Mode.Same);

        // retention: drafts and holds append; approval, revocation and release each decide one revision
        const string Base = "/api/v1/company/attachment-retention";
        var approver = await SignIn(await PresetApprover((login, company) => Rbac.GrantRetentionApproverOfflineAsync(login, company)));
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var policy = await Race("retention draft", [], HttpMethod.Post, $"{Base}/policies",
            new { effectiveFrom = today, note = "matrix", rules = new[] { new { subjectKind = "*", sensitivity = 0, months = (int?)60, indefinite = false } } }, mode: Mode.Append);
        Won(policy);
        var approved = await Race("retention approve", ["412:revision_conflict", "409:not_a_draft"], HttpMethod.Post, $"{Base}/policies/{IdOf(policy!)}/approve", new { }, EtagOf(policy!), caller: approver);
        Won(approved);
        await Race("retention revoke", ["412:revision_conflict", "409:not_approved"], HttpMethod.Post, $"{Base}/policies/{IdOf(policy!)}/revoke", new { reason = "matrix" }, EtagOf(approved!), caller: approver);
        var hold = await Race("legal hold", [], HttpMethod.Post, $"{Base}/holds", new { scope = 1, reason = "audit", reference = "CASE-1" }, mode: Mode.Append);
        Won(hold);
        await Race("legal hold release", ["412:revision_conflict", "409:already_released"], HttpMethod.Post, $"{Base}/holds/{IdOf(hold!)}/release", new { reason = "closed" }, EtagOf(hold!), caller: approver);

        AssertNoFindings();
        races.AssertRan(Shares[nameof(Attachments_and_their_retention_end_in_a_mapped_4xx_never_a_5xx)]);
    }
}

/// <summary>
/// The probe engine of the constraint-violation matrix, part 2, shared by its test classes: sends the same request <c>parallel</c> times in parallel, then once more, and records every
/// finding of a run so they are reported together. <see cref="EndpointConstraintMatrixPart2Tests.Mode.One"/>: exactly one 2xx, every other answer one of <c>expected</c>.
/// <see cref="EndpointConstraintMatrixPart2Tests.Mode.Same"/>: every answer has the signature <c>expected[0]</c> ("2xx" for an idempotent success).
/// <see cref="EndpointConstraintMatrixPart2Tests.Mode.Append"/>: at least one 2xx, every refusal one of <c>expected</c>. A 5xx is always a finding unless the probe pins it EXACTLY as a
/// documented answer (the attachment upload gate's 503 <c>upload_busy</c>); the unhandled-error 503 <c>service_unavailable</c> is never pinned.
/// </summary>
internal sealed class ConstraintRaces(int parallel)
{
    private readonly List<string> findings = [];
    private readonly List<string> diagnostics = [];
    private readonly List<(string What, HttpMethod Method, string Path)> executed = [];

    /// <summary>"status:code" of an answer; a 400 <c>validation_failed</c> is identified by its first field code (for example <c>400:code_taken</c>); a 2xx is "2xx".</summary>
    public static string Signature(ApiResponse r)
    {
        if ((int)r.Status is >= 200 and < 300) return "2xx";
        var code = EndpointPermissionMatrixTests.ErrorOf(r);
        if (code == "validation_failed")
            try { code = r.Json.GetProperty("fields")[0].GetProperty("code").GetString(); } catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { }
        return $"{(int)r.Status}:{code}";
    }

    private static string Short(string body) => body.Length > 160 ? body[..160] : body;
    public static Guid IdOf(ApiResponse r) => r.Json.GetProperty("id").GetGuid();
    public static string EtagOf(ApiResponse r) => r.Headers.ETag!.Tag;

    /// <summary>Runs one probe; returns a winner or null.</summary>
    public async Task<ApiResponse?> Run(string what, string[] expected, HttpMethod method, string path, Func<Task<ApiResponse>> send, EndpointConstraintMatrixPart2Tests.Mode mode)
    {
        executed.Add((what, method, path));
        var raced = await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => Task.Run(send)));
        var again = await send();
        var all = raced.Append(again).ToList();
        var winners = all.Where(r => Signature(r) == "2xx").ToList();
        diagnostics.Add($"{what} || {winners.Count} won || {string.Join(" ; ", all.Select(Signature).Distinct())} || {Short(all.FirstOrDefault(r => Signature(r) != "2xx")?.Body ?? all[0].Body)}");
        foreach (var r in all.Where(r => (int)r.Status >= 500 && !expected.Contains(Signature(r))))
            findings.Add($"{what}: {(int)r.Status} {Short(r.Body)}");
        if (mode == EndpointConstraintMatrixPart2Tests.Mode.Same)
        {
            foreach (var s in all.Select(Signature).Distinct().Where(s => s != expected[0]))
                findings.Add($"{what}: {s} where every answer must be {expected[0]}");
            // review L3 of #161: where the answers carry an id, an idempotent repeat must answer the SAME resource, not a second one
            var ids = winners.Select(IdentityOf).Distinct().ToList();
            if (ids.Count > 1) findings.Add($"{what}: the idempotent answers name {ids.Count} different resources ({string.Join(", ", ids.Select(i => i ?? "no id"))})");
            return winners.FirstOrDefault();
        }
        foreach (var r in all.Where(r => Signature(r) != "2xx" && (int)r.Status < 500 && !expected.Contains(Signature(r))))
            findings.Add($"{what}: {Signature(r)} is not the expected refusal ({string.Join(" or ", expected)}): {Short(r.Body)}");
        var append = mode == EndpointConstraintMatrixPart2Tests.Mode.Append;
        if (append ? winners.Count < 1 : winners.Count != 1)
            findings.Add($"{what}: {winners.Count} requests succeeded (expected {(append ? "at least one" : "exactly one")}): {string.Join(", ", all.Select(Signature))}");
        return winners.FirstOrDefault();
    }

    /// <summary>The id an answer names: its top-level <c>id</c>, else <c>entry.id</c> (journal answers), else null (an answer that names no resource).</summary>
    private static string? IdentityOf(ApiResponse r)
    {
        try
        {
            var json = r.Json;
            if (json.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (json.TryGetProperty("id", out var id)) return id.ToString();
            if (json.TryGetProperty("entry", out var entry) && entry.ValueKind == System.Text.Json.JsonValueKind.Object && entry.TryGetProperty("id", out var entryId)) return entryId.ToString();
            return null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The route template (as the permission matrix names it, "METHOD /path/{param}") that a concrete request resolves to; null when none or more than one matches.</summary>
    public static string? TemplateOf(HttpMethod method, string path, IEnumerable<string> templates)
    {
        var bare = path.Split('?')[0].TrimEnd('/');
        var matches = templates.Where(t =>
        {
            var space = t.IndexOf(' ');
            if (!string.Equals(t[..space], method.Method, StringComparison.Ordinal)) return false;
            var pattern = "^" + string.Join("/", t[(space + 1)..].TrimEnd('/').Split('/').Select(seg =>
                seg.StartsWith('{') && seg.EndsWith('}') ? (seg.EndsWith(":guid}", StringComparison.Ordinal) ? "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" : "[^/]+")
                : System.Text.RegularExpressions.Regex.Escape(seg))) + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(bare, pattern);
        }).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Review L1 of #161: the routes this test actually raced (resolved to their templates through the permission matrix's route table) must equal its <paramref name="share"/> EXACTLY:
    /// a share route without a probe fails, and so does a probe of a route outside the share or of a request that resolves to no single route.
    /// </summary>
    public void AssertRan(IReadOnlyDictionary<string, string> share)
    {
        var templates = EndpointPermissionMatrixTests.Matrix.Select(r => r.Key).Concat(EndpointPermissionMatrixTests.Excluded.Keys).ToList();
        var ran = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (what, method, path) in executed)
        {
            if (TemplateOf(method, path, templates) is { } t) ran.Add(t);
            else problems.Add($"probe '{what}' ({method.Method} {path}) resolves to no single route of the route table");
        }
        problems.AddRange(share.Keys.Except(ran, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(r => $"in the share but never raced: {r}"));
        problems.AddRange(ran.Except(share.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(r => $"raced but not in the share: {r}"));
        Assert.True(problems.Count == 0, "the probes this test ran do not match its share:\n" + string.Join("\n", problems));
    }

    /// <summary>A probe's winner the next step builds on; without one, every finding so far is reported (the probe recorded why nobody won).</summary>
    public ApiResponse Won(ApiResponse? winner)
    {
        if (winner is null) AssertNoFindings();
        Assert.NotNull(winner);
        return winner!;
    }

    public void AssertNoFindings() =>
        Assert.True(findings.Count == 0, $"{findings.Count} constraint-violation finding(s):\n" + string.Join("\n", findings) + "\nobserved per probe (winners, signatures):\n" + string.Join("\n", diagnostics));
}
