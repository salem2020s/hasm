using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Pricing;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.Taxes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using BA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using SysAudit = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using SysAsg = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Per-endpoint authorization matrix of every MUTATING company endpoint (<c>POST/PUT/PATCH/DELETE /api/v1/company/**</c>), discovered from the running API's route table:
/// <list type="bullet">
/// <item>a company member without any permission is refused with 403 on every one of them;</item>
/// <item>a member holding every read (view) permission is refused with 403 on every one of them that changes something;</item>
/// <item>a member holding the branch-scoped permissions for branch A only is refused with 403 on the branch-scoped endpoints acting on branch B, while the same grant works on branch A (the branch
/// structure operations need the company-wide structure permission and are refused on A as well);</item>
/// <item>a member holding the journal permissions for cost center A only is refused with 403 on the journal endpoints acting on entries allocated to cost center B, while the same grant works on A;
/// the warehouse scope is covered the same way by <see cref="EndpointPermissionMatrixWarehouseTests"/>;</item>
/// <item>every refusal is the authorization error itself (<c>forbidden</c>), never another 403;</item>
/// <item>no refused request writes anything (business audit, access audit and role assignments unchanged).</item>
/// </list>
/// A mutating company endpoint that is not listed here fails <see cref="Every_mutating_company_endpoint_is_in_the_matrix"/>, so a new endpoint cannot ship without its 403 coverage.
/// Disposable cluster only.
/// </summary>
public class EndpointPermissionMatrixTests : JournalTestBase
{
    private const string Prefix = "/api/v1/company/";

    /// <summary>How a stub request carries its body: JSON (<c>{}</c> unless the row gives a stub), a raw file body (uploads), or none.</summary>
    public enum Payload { Json, File, None }

    /// <summary>The branch-scoped resource a route acts on, for the branch-scope check.</summary>
    public enum BranchTarget { None, BranchItself, JournalEntry, JournalCreate, CommercialDocument, CommercialCreate }

    /// <summary>
    /// One mutating route. <paramref name="Stub"/> is a minimal JSON body for an endpoint that checks the SHAPE of its body before authorization (otherwise <c>{}</c>).
    /// <paramref name="ReadAllowed"/> marks a POST that only calculates (never changes anything) and is therefore allowed to a read-only member; it is still refused to a member without
    /// any permission. <paramref name="Branch"/> names the branch-scoped resource the route acts on.
    /// </summary>
    public sealed record Route(string Method, string Pattern, Payload Payload = Payload.Json, string? Stub = null, bool ReadAllowed = false, BranchTarget Branch = BranchTarget.None)
    {
        public string Key => $"{Method} {Pattern}";
    }

    private static Route P(string pattern, Payload payload = Payload.Json, string? stub = null, bool readAllowed = false, BranchTarget branch = BranchTarget.None) => new("POST", pattern, payload, stub, readAllowed, branch);
    private static Route U(string pattern, string? stub = null, BranchTarget branch = BranchTarget.None) => new("PUT", pattern, Payload.Json, stub, false, branch);

    private const string QuoteStub = """{"date":"2026-03-10","currency":"JOD","itemId":"5d2a3c43-1b8f-4a52-9d1c-6f2b0c1d7e11","unitId":"8b7e5f0a-2c4d-4e6f-8a9b-0c1d2e3f4a5b","quantity":1}""";
    private const string ClassStub = """{"classId":null}""";

    /// <summary>The matrix: every mutating company route of the API, its pattern exactly as the route table spells it (a group root ends with "/").</summary>
    public static readonly IReadOnlyList<Route> Matrix =
    [
        // chart of accounts
        P("/api/v1/company/accounts/"), P("/api/v1/company/accounts/apply-template"), U("/api/v1/company/accounts/settings"), U("/api/v1/company/accounts/{id:guid}"),
        P("/api/v1/company/accounts/{id:guid}/recode"), P("/api/v1/company/accounts/{id:guid}/move"), P("/api/v1/company/accounts/{id:guid}/deactivate"), P("/api/v1/company/accounts/{id:guid}/activate"),
        // approval policies
        P("/api/v1/company/approval-policies/"), U("/api/v1/company/approval-policies/{id:guid}"), P("/api/v1/company/approval-policies/{id:guid}/deactivate"), P("/api/v1/company/approval-policies/{id:guid}/activate"),
        // attachments
        P("/api/v1/company/attachments/", Payload.File), P("/api/v1/company/attachments/{id:guid}/remove"), P("/api/v1/company/attachments/maintenance/rewrap"),
        P("/api/v1/company/attachments/maintenance/sweep"), P("/api/v1/company/attachments/maintenance/verify"),
        // attachment retention (Phase R1): policies and attachment legal holds
        P("/api/v1/company/attachment-retention/policies"), P("/api/v1/company/attachment-retention/policies/{id:guid}/approve"), P("/api/v1/company/attachment-retention/policies/{id:guid}/revoke"),
        P("/api/v1/company/attachment-retention/holds"), P("/api/v1/company/attachment-retention/holds/{id:guid}/release"),
        // branches: the edit is branch-scoped; the structure operations are company-wide
        P("/api/v1/company/branches/"), U("/api/v1/company/branches/{id:guid}", branch: BranchTarget.BranchItself), P("/api/v1/company/branches/{id:guid}/deactivate", branch: BranchTarget.BranchItself),
        P("/api/v1/company/branches/{id:guid}/activate", branch: BranchTarget.BranchItself), P("/api/v1/company/branches/{id:guid}/make-main", branch: BranchTarget.BranchItself),
        // commercial documents (branch-scoped)
        P("/api/v1/company/commercial-documents/", branch: BranchTarget.CommercialCreate), U("/api/v1/company/commercial-documents/{id:guid}", branch: BranchTarget.CommercialDocument),
        P("/api/v1/company/commercial-documents/{id:guid}/submit", branch: BranchTarget.CommercialDocument), P("/api/v1/company/commercial-documents/{id:guid}/withdraw", branch: BranchTarget.CommercialDocument),
        P("/api/v1/company/commercial-documents/{id:guid}/approve", branch: BranchTarget.CommercialDocument), P("/api/v1/company/commercial-documents/{id:guid}/reject", branch: BranchTarget.CommercialDocument),
        P("/api/v1/company/commercial-documents/{id:guid}/cancel", branch: BranchTarget.CommercialDocument), P("/api/v1/company/commercial-documents/links"),
        // company configuration
        U("/api/v1/company/profile"), P("/api/v1/company/profile/display-name-sync"), U("/api/v1/company/settings"), U("/api/v1/company/currencies/{code}"),
        // cost centers and their policies
        P("/api/v1/company/cost-centers/"), U("/api/v1/company/cost-centers/{id:guid}"), P("/api/v1/company/cost-centers/{id:guid}/recode"), P("/api/v1/company/cost-centers/{id:guid}/move"),
        P("/api/v1/company/cost-centers/{id:guid}/deactivate"), P("/api/v1/company/cost-centers/{id:guid}/activate"),
        U("/api/v1/company/cost-center-policies/accounts/{accountId:guid}"), U("/api/v1/company/cost-center-policies/branches/{costCenterId:guid}"),
        // exchange rates, statement mapping
        P("/api/v1/company/exchange-rates/"), U("/api/v1/company/statements/mapping"),
        // fiscal closing, years, periods, reopen requests
        U("/api/v1/company/fiscal-closing/settings"), P("/api/v1/company/fiscal-closing/years/{yearId:guid}/close"), P("/api/v1/company/fiscal-closing/years/{yearId:guid}/carry-forward"),
        P("/api/v1/company/fiscal-closing/years/{yearId:guid}/reverse-opening"), P("/api/v1/company/fiscal-closing/years/{yearId:guid}/reverse-closing"),
        P("/api/v1/company/fiscal-years/"), P("/api/v1/company/fiscal-years/{id:guid}/close"), P("/api/v1/company/fiscal-years/{id:guid}/reopen-requests"),
        P("/api/v1/company/fiscal-periods/{id:guid}/soft-close"), P("/api/v1/company/fiscal-periods/{id:guid}/reopen"), P("/api/v1/company/fiscal-periods/{id:guid}/final-close"),
        P("/api/v1/company/fiscal-periods/{id:guid}/reopen-requests"),
        P("/api/v1/company/fiscal-reopen-requests/{id:guid}/approve"), P("/api/v1/company/fiscal-reopen-requests/{id:guid}/deny"), P("/api/v1/company/fiscal-reopen-requests/{id:guid}/cancel"),
        // imports (the id routes act on a real batch, see Fill)
        P("/api/v1/company/imports/templates/{adapter}/{name}/retire"), P("/api/v1/company/imports/{id:guid}/mapping/template"), P("/api/v1/company/imports/{id:guid}/mapping/apply-template"),
        U("/api/v1/company/imports/settings"), P("/api/v1/company/imports/maintenance/run"), P("/api/v1/company/imports/maintenance/rewrap"), P("/api/v1/company/imports/", Payload.File),
        P("/api/v1/company/imports/{id:guid}/stage"), P("/api/v1/company/imports/{id:guid}/cancel"), P("/api/v1/company/imports/{id:guid}/renew"), U("/api/v1/company/imports/{id:guid}/mapping"),
        P("/api/v1/company/imports/{id:guid}/dry-run"), P("/api/v1/company/imports/{id:guid}/resolutions"), P("/api/v1/company/imports/{id:guid}/submit"),
        P("/api/v1/company/imports/{id:guid}/withdraw"), P("/api/v1/company/imports/{id:guid}/approve"), P("/api/v1/company/imports/{id:guid}/reject"), P("/api/v1/company/imports/{id:guid}/apply"),
        // inventory: accounting, settings, units, groups, items, barcodes
        U("/api/v1/company/inventory/accounting/"), U("/api/v1/company/inventory/settings"),
        P("/api/v1/company/inventory/units"), U("/api/v1/company/inventory/units/{id:guid}"), P("/api/v1/company/inventory/units/{id:guid}/deactivate"), P("/api/v1/company/inventory/units/{id:guid}/activate"),
        P("/api/v1/company/inventory/groups"), U("/api/v1/company/inventory/groups/{id:guid}"), P("/api/v1/company/inventory/groups/{id:guid}/deactivate"), P("/api/v1/company/inventory/groups/{id:guid}/activate"),
        P("/api/v1/company/inventory/items"), U("/api/v1/company/inventory/items/{id:guid}"), P("/api/v1/company/inventory/items/{id:guid}/deactivate"), P("/api/v1/company/inventory/items/{id:guid}/activate"),
        P("/api/v1/company/inventory/items/{id:guid}/barcodes"), P("/api/v1/company/inventory/items/{id:guid}/barcodes/{barcodeId:guid}/deactivate"),
        P("/api/v1/company/inventory/items/{id:guid}/barcodes/{barcodeId:guid}/activate"),
        // journal (branch-scoped) and ledger settings
        P("/api/v1/company/journal/", branch: BranchTarget.JournalCreate), U("/api/v1/company/journal/{id:guid}", branch: BranchTarget.JournalEntry),
        P("/api/v1/company/journal/{id:guid}/submit", branch: BranchTarget.JournalEntry), P("/api/v1/company/journal/{id:guid}/withdraw", branch: BranchTarget.JournalEntry),
        P("/api/v1/company/journal/{id:guid}/cancel", branch: BranchTarget.JournalEntry), P("/api/v1/company/journal/{id:guid}/decision", branch: BranchTarget.JournalEntry),
        P("/api/v1/company/journal/{id:guid}/post", branch: BranchTarget.JournalEntry), P("/api/v1/company/journal/{id:guid}/reverse", branch: BranchTarget.JournalEntry),
        U("/api/v1/company/ledger-settings/"),
        // negative stock
        U("/api/v1/company/negative-stock/settings"), U("/api/v1/company/negative-stock/items/{itemId:guid}"), P("/api/v1/company/stock-documents/{id:guid}/negative-exception/approvals"),
        // numbering
        U("/api/v1/company/numbering/{docType}"),
        // parties, party roles, sales representatives
        P("/api/v1/company/parties/"), P("/api/v1/company/parties/sync"), P("/api/v1/company/parties/reconcile"), U("/api/v1/company/parties/{id:guid}"), P("/api/v1/company/parties/{id:guid}/propose"),
        P("/api/v1/company/party-roles/"), U("/api/v1/company/party-roles/{id:guid}"), P("/api/v1/company/sales-representatives/"), U("/api/v1/company/sales-representatives/{id:guid}"),
        // pricing (the quote only calculates: allowed to a read-only member)
        P("/api/v1/company/pricing/policy"), P("/api/v1/company/pricing/price-lists"), U("/api/v1/company/pricing/price-lists/{id:guid}"), P("/api/v1/company/pricing/price-lists/{id:guid}/entries"),
        U("/api/v1/company/pricing/entries/{id:guid}"), P("/api/v1/company/pricing/discounts"), U("/api/v1/company/pricing/discounts/{id:guid}"), P("/api/v1/company/pricing/customer-groups"),
        U("/api/v1/company/pricing/customer-groups/{id:guid}"), U("/api/v1/company/pricing/customer-assignments/{partyId:guid}"), P("/api/v1/company/pricing/quote", stub: QuoteStub, readAllowed: true),
        // stocktakes and their adjustments
        P("/api/v1/company/stocktakes/{id:guid}/adjustment"), P("/api/v1/company/stock-adjustments/{id:guid}/posting"), P("/api/v1/company/stock-adjustments/{id:guid}/reversal"),
        P("/api/v1/company/stocktakes/"), U("/api/v1/company/stocktakes/{id:guid}"), P("/api/v1/company/stocktakes/{id:guid}/generate-sheet"), P("/api/v1/company/stocktakes/{id:guid}/submit"),
        P("/api/v1/company/stocktakes/{id:guid}/withdraw"), P("/api/v1/company/stocktakes/{id:guid}/approve"), P("/api/v1/company/stocktakes/{id:guid}/reject"), P("/api/v1/company/stocktakes/{id:guid}/cancel"),
        // stock documents (their posting and reversal belong to PR #107 and are excluded below)
        P("/api/v1/company/stock-documents/"), U("/api/v1/company/stock-documents/{id:guid}"), P("/api/v1/company/stock-documents/{id:guid}/submit"), P("/api/v1/company/stock-documents/{id:guid}/withdraw"),
        P("/api/v1/company/stock-documents/{id:guid}/approve"), P("/api/v1/company/stock-documents/{id:guid}/reject"), P("/api/v1/company/stock-documents/{id:guid}/cancel"),
        // taxes
        P("/api/v1/company/taxes/"), U("/api/v1/company/taxes/{id:guid}"), P("/api/v1/company/taxes/{id:guid}/rates"), P("/api/v1/company/taxes/classes"), U("/api/v1/company/taxes/classes/{id:guid}"),
        U("/api/v1/company/taxes/item-classes/{itemId:guid}", stub: ClassStub), U("/api/v1/company/taxes/party-classes/{partyId:guid}", stub: ClassStub), P("/api/v1/company/taxes/rules"),
        U("/api/v1/company/taxes/rules/{id:guid}"), P("/api/v1/company/taxes/policy"),
        // warehouses
        P("/api/v1/company/warehouses/"), U("/api/v1/company/warehouses/{id:guid}"), P("/api/v1/company/warehouses/{id:guid}/deactivate"), P("/api/v1/company/warehouses/{id:guid}/activate"),
    ];

    /// <summary>Mutating company routes deliberately NOT in the matrix, each with its reason: an in-flight pull request owns the route and its 403 tests.</summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>
    {
        ["POST /api/v1/company/stock-documents/{id:guid}/posting/"] = "stock posting: PR #107 in flight",
        ["POST /api/v1/company/stock-documents/{id:guid}/reversal"] = "stock posting reversal: PR #107 in flight",
    };

    // ------------------------------------------------------------------------------------------------------------------------------- fixtures

    private Guid importId, branchA, branchB, entryA, entryB, docA, docB, party, item, unit;
    private string importBody = "";

    private void RegisterServices(IServiceCollection s)
    {
        s.AddSingleton<KekRing>(new InMemoryKekRing("k1"));
        s.AddSingleton(new EncryptionOptions { AllowInsecureFilePermissions = true });
        s.AddSingleton<ICommercialDocumentTypeRegistry>(new CommercialDocumentTypeRegistry([CommercialTestType.Name]));   // the internal test document type (D-049), as in CommercialApiTests
    }

    /// <summary>
    /// The API plus the company fixtures the branch and import checks need: base currency, fiscal year 2026, two accounts, branches A and B, one draft journal entry in each, the commercial
    /// composition (party, item, price, tax policy) with one draft document in each branch, and one real import batch.
    /// </summary>
    private readonly string storage = Path.Combine(Path.GetTempPath(), "hasm-matrix-" + Guid.NewGuid().ToString("N"));

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(storage)) try { Directory.Delete(storage, true); } catch (IOException) { }
    }

    private async Task Start()
    {
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Attachments:StorageDirectory", Path.Combine(storage, "attachments"));
            b.UseSetting("Hasm:Imports:StorageDirectory", Path.Combine(storage, "imports"));
            b.ConfigureServices(RegisterServices);
        });
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        foreach (var (code, isBase) in new[] { ("JOD", true), ("USD", false) })
            await Sql($"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('{code}', {(isBase ? "true" : "false")}, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset");
        Sales = await NewAccount("4101", "revenue");
        branchA = await NewBranch("MXA"); branchB = await NewBranch("MXB");
        entryA = Ok(await Journal.CreateDraftAsync(Admin.Id, Company.Id, new JournalInput(new DateOnly(2026, 3, 10), "matrix A", branchA, [Dr(Cash.Id, 10m), Cr(Sales.Id, 10m)], null), false)).Header.Id;
        entryB = Ok(await Journal.CreateDraftAsync(Admin.Id, Company.Id, new JournalInput(new DateOnly(2026, 3, 10), "matrix B", branchB, [Dr(Cash.Id, 10m), Cr(Sales.Id, 10m)], null), false)).Header.Id;

        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        var inventory = new CompanyInventoryService(Resolver, tx, Clock);
        var pricing = new PricingConfigService(Resolver, tx, Clock);
        var tax = new TaxConfigService(Resolver, tx, Clock);
        var docs = new CommercialDocumentService(Resolver, tx, Clock, new CommercialDocumentTypeRegistry([CommercialTestType.Name]), pricing, tax);
        Ok(await tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerLine)));
        party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'MXC', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
        Ok(await new PartyRoleService(Resolver, tx, Clock).CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(party, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null)));
        var u = Ok(await inventory.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("PCS-MX", "وحدة", "Piece", 3)));
        var g = Ok(await inventory.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("G-MX", "مجموعة", "Group", null, 1)));
        var it = Ok(await inventory.CreateItemAsync(Admin.Id, Company.Id, new ItemInput("MX1", "صنف", "Item", 1, g.Id, u.Id, null, null, false, false, false, null, [])));
        var list = Ok(await pricing.CreatePriceListAsync(Admin.Id, Company.Id, new PriceListInput("PL-MX", "قائمة", "List", 10, "JOD", new DateOnly(2026, 1, 1), null, true, null)));
        Ok(await pricing.CreateEntryAsync(Admin.Id, Company.Id, list.Id, new PriceEntryInput(it.Id, null, u.Id, 0m, 10m, null)));
        item = it.Id; unit = u.Id;
        docA = Ok(await docs.CreateAsync(Admin.Id, Company.Id, CommercialInput(branchA, "matrix-doc-a"))).Id;
        docB = Ok(await docs.CreateAsync(Admin.Id, Company.Id, CommercialInput(branchB, "matrix-doc-b"))).Id;

        var admin = await SignIn(Admin);
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/company/imports/?adapter=inventory-units&fileName=units.csv") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("code,name\nMXU,Unit\n")) };
        upload.Headers.Add("X-Hasm-Request", "1");
        upload.Headers.Add("Cookie", $"{CookieName}={admin}");
        using var uploaded = await client.SendAsync(upload);
        importBody = await uploaded.Content.ReadAsStringAsync();
        Assert.True(uploaded.StatusCode == HttpStatusCode.Created, $"import upload: {(int)uploaded.StatusCode} {importBody}");
        importId = System.Text.Json.JsonDocument.Parse(importBody).RootElement.GetProperty("id").GetGuid();
    }

    private CommercialDocumentInput CommercialInput(Guid branch, string key) =>
        new(CommercialTestType.Name, branch, party, "jod", new DateOnly(2026, 6, 15), 1, null, key, [new CommercialLineInput(item, unit, 2m, null)]);

    private async Task<Guid> NewBranch(string code)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {Br.Table} ({Br.Id}, {Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('{id}', '{code}', '{code}', true, false, 1, 1, now(), now())");
        return id;
    }

    // ------------------------------------------------------------------------------------------------------------------------------- requests

    /// <summary>Replaces the route parameters: the import id routes act on the real batch, everything else on fresh ids (or fixed codes).</summary>
    private string Fill(string pattern) =>
        Regex.Replace(pattern, @"\{([a-zA-Z]+)(:[a-z]+)?\}", m => m.Groups[1].Value switch
        {
            "code" => "USD",
            "docType" => "journal",
            "adapter" => "inventory-units",
            "name" => "template-x",
            "id" when pattern.StartsWith("/api/v1/company/imports/", StringComparison.Ordinal) => importId.ToString(),
            _ => Guid.NewGuid().ToString(),
        });

    private async Task<ApiResponse> Call(Route r, string path, string cookie, string? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(r.Method), path);
        request.Content = r.Payload switch
        {
            Payload.Json => new StringContent(body ?? r.Stub ?? "{}", Encoding.UTF8, "application/json"),
            Payload.File => new ByteArrayContent(Encoding.UTF8.GetBytes("code,name\nX,Y\n")),
            _ => null,
        };
        request.Headers.Add("X-Hasm-Request", "1");
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A route pattern as the matrix spells it: always with its leading slash (a pattern mapped without one is still a company route).</summary>
    internal static string Normalise(string raw) => raw.StartsWith('/') ? raw : "/" + raw;

    /// <summary>
    /// Every company route of the running API: the mutating ones as "METHOD /pattern", and the patterns of company routes WITHOUT HTTP-method metadata (they would answer every method, so a mutating
    /// call could hide behind them; there must be none).
    /// </summary>
    private (IReadOnlyList<string> Mutating, IReadOnlyList<string> MethodLess) DiscoveredRoutes()
    {
        var company = factory!.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } t && Normalise(t).StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        IReadOnlyList<string> methodLess = [.. company.Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is not { Count: > 0 }).Select(e => Normalise(e.RoutePattern.RawText!)).Distinct().Order(StringComparer.Ordinal)];
        IReadOnlyList<string> mutating = [.. company
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Where(m => m is "POST" or "PUT" or "PATCH" or "DELETE").Select(m => $"{m} {Normalise(e.RoutePattern.RawText!)}"))
            .Distinct().Order(StringComparer.Ordinal)];
        return (mutating, methodLess);
    }

    /// <summary>What a refused request must never change: the business audit stream, the access audit and the role assignments.</summary>
    private async Task<(long Business, long Access, long Assignments)> Footprint() =>
        (await Sql<long>($"SELECT count(*) FROM {BA.Table}"), await Scalar<long>($"SELECT count(*) FROM {SysAudit.Table}"), await Scalar<long>($"SELECT count(*) FROM {SysAsg.Table}"));

    private async Task<List<string>> NotForbidden(string cookie, IEnumerable<(Route Route, string Path, string? Body)> calls)
    {
        var failures = new List<string>();
        foreach (var (r, path, body) in calls)
        {
            var res = await Call(r, path, cookie, body);
            if (res.Status != HttpStatusCode.Forbidden || ErrorOf(res) != "forbidden")                                       // review L1: the authorization refusal itself, not another 403 (entitlement, CSRF)
                failures.Add($"{r.Key} ({path}) -> {(int)res.Status} {(res.Body.Length > 160 ? res.Body[..160] : res.Body)}");
        }
        return failures;
    }

    /// <summary>The <c>error</c> code of a JSON error body, or null.</summary>
    internal static string? ErrorOf(ApiResponse r)
    {
        try { return System.Text.Json.JsonDocument.Parse(r.Body).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // ------------------------------------------------------------------------------------------------------------------------------- tests

    [PostgresFact]
    public async Task Every_mutating_company_endpoint_is_in_the_matrix()
    {
        await StartApi(configure: b => b.ConfigureServices(RegisterServices));
        var (discovered, methodLess) = DiscoveredRoutes();
        Assert.True(methodLess.Count == 0, "company endpoints without HTTP-method metadata (they answer every method):\n" + string.Join("\n", methodLess));
        Assert.True(discovered.Count > 150, $"the route table looks incomplete: {discovered.Count} mutating company routes");
        var listed = Matrix.Select(r => r.Key).ToList();
        Assert.Equal(listed.Count, listed.Distinct().Count());                                                       // no duplicate rows
        Assert.All(Matrix, r => Assert.Equal(Normalise(r.Pattern), r.Pattern));                                       // every row is spelled with its leading slash
        Assert.Empty(listed.Intersect(Excluded.Keys));                                                                // a route is covered or excluded, never both
        var missing = discovered.Except(listed).Except(Excluded.Keys).ToList();
        Assert.True(missing.Count == 0, "mutating company endpoints without a row in the 403 matrix (add them to EndpointPermissionMatrixTests.Matrix):\n" + string.Join("\n", missing));
        var stale = listed.Concat(Excluded.Keys).Except(discovered).ToList();
        Assert.True(stale.Count == 0, "matrix rows for routes the API no longer maps:\n" + string.Join("\n", stale));
    }

    [PostgresFact]
    public async Task A_member_without_any_permission_is_refused_403_on_every_mutating_company_endpoint_and_nothing_is_written()
    {
        await Start();
        var user = await NewUser("matrix-none");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, user.Login)).IsOk);
        Assert.Empty((await Rbac.GetEffectivePermissionsAsync(user.Id, Company.Id)).Grants);
        var cookie = await SignIn(user);
        var before = await Footprint();
        var failures = await NotForbidden(cookie, Matrix.Select(r => (r, Fill(r.Pattern), (string?)null)));
        Assert.True(failures.Count == 0, $"{failures.Count} endpoint(s) did not answer 403 to a member without permissions:\n" + string.Join("\n", failures));
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task A_member_with_every_read_permission_is_refused_403_on_every_mutating_company_endpoint_and_nothing_is_written()
    {
        await Start();
        var held = (await Rbac.GetEffectivePermissionsAsync(Admin.Id, Company.Id)).HeldPermissionCodes.ToHashSet();
        var views = PermissionCatalog.Default.All.Select(p => p.Code).Where(c => c.EndsWith(".view", StringComparison.Ordinal) && held.Contains(c)).Order().ToArray();
        Assert.True(views.Length >= 30, $"only {views.Length} view permissions");
        var (reader, _) = await Member(views, ScopeKind.Company, null);
        var cookie = await SignIn(reader);
        var before = await Footprint();
        var failures = await NotForbidden(cookie, Matrix.Where(r => !r.ReadAllowed).Select(r => (r, Fill(r.Pattern), (string?)null)));
        Assert.True(failures.Count == 0, $"{failures.Count} endpoint(s) did not answer 403 to a read-only member:\n" + string.Join("\n", failures));
        Assert.Equal(before, await Footprint());
    }

    [PostgresFact]
    public async Task A_member_scoped_to_another_branch_is_refused_403_on_the_branch_scoped_endpoints_while_the_same_grant_works_on_its_own_branch()
    {
        await Start();
        var held = (await Rbac.GetEffectivePermissionsAsync(Admin.Id, Company.Id)).HeldPermissionCodes.ToHashSet();
        var branchScoped = PermissionCatalog.Default.All.Where(p => p.Resource.Scope == ScopeKind.Branch && held.Contains(p.Code)).Select(p => p.Code).Order().ToArray();
        Assert.Contains(PermissionCatalog.Codes.CommercialView, branchScoped);
        var (user, _) = await Member(branchScoped, ScopeKind.Branch, branchA);
        var cookie = await SignIn(user);

        var lines = $$"""[{"accountId":"{{Cash.Id}}","debit":10},{"accountId":"{{Sales.Id}}","credit":10}]""";
        string JournalBody(Guid branch) => $$"""{"entryDate":"2026-03-10","description":"matrix","branchId":"{{branch}}","lines":{{lines}}}""";
        string CommercialBody(Guid branch, string key) =>
            $$"""{"docType":"{{CommercialTestType.Name}}","branchId":"{{branch}}","partyId":"{{party}}","currency":"jod","documentDate":"2026-06-15","mode":1,"idempotencyKey":"{{key}}","lines":[{"itemId":"{{item}}","unitId":"{{unit}}","quantity":2}]}""";
        (string Path, string? Body) Target(Route r, Guid branch, Guid entry, Guid doc) => r.Branch switch
        {
            BranchTarget.BranchItself => (r.Pattern.Replace("{id:guid}", branch.ToString()), r.Method == "PUT" ? """{"code":"MXB","nameEn":"Changed"}""" : null),
            BranchTarget.JournalEntry => (r.Pattern.Replace("{id:guid}", entry.ToString()), r.Method == "PUT" ? JournalBody(branch) : null),
            BranchTarget.JournalCreate => (r.Pattern, JournalBody(branch)),
            BranchTarget.CommercialDocument => (r.Pattern.Replace("{id:guid}", doc.ToString()), r.Method == "PUT" ? CommercialBody(branch, "matrix-upd") : null),
            BranchTarget.CommercialCreate => (r.Pattern, CommercialBody(branch, "matrix-new-" + Guid.NewGuid().ToString("N")[..8])),
            _ => throw new InvalidOperationException(r.Key),
        };
        var scoped = Matrix.Where(r => r.Branch != BranchTarget.None).ToList();
        Assert.Equal(19, scoped.Count);                                                                             // branches 4, commercial documents 7, journal 8

        // branch B: every branch-scoped endpoint refuses, and nothing is written
        var before = await Footprint();
        var failures = await NotForbidden(cookie, scoped.Select(r => { var (path, body) = Target(r, branchB, entryB, docB); return (r, path, body); }));
        Assert.True(failures.Count == 0, $"{failures.Count} branch-scoped endpoint(s) did not answer 403 to a member scoped to another branch:\n" + string.Join("\n", failures));
        Assert.Equal(before, await Footprint());

        // control: the same grant works on its own branch A (so the refusals above come from the scope, not from a broken role)
        var created = await Send(HttpMethod.Post, "/api/v1/company/journal", System.Text.Json.JsonDocument.Parse(JournalBody(branchA)).RootElement, cookie);
        Assert.True(created.Status == HttpStatusCode.Created, $"journal create on branch A: {(int)created.Status} {created.Body}");
        var submitted = await Send(HttpMethod.Post, $"/api/v1/company/journal/{entryA}/submit", null, cookie, ifMatch: "\"1\"");
        Assert.True(submitted.Status == HttpStatusCode.OK, $"journal submit on branch A: {(int)submitted.Status} {submitted.Body}");
        var doc = await Send(HttpMethod.Post, $"/api/v1/company/commercial-documents/{docA}/submit", null, cookie, ifMatch: "\"1\"");
        Assert.True(doc.Status == HttpStatusCode.OK, $"commercial submit on branch A: {(int)doc.Status} {doc.Body}");
        // review L2: the branch EDIT is the branch-scoped one: it works on branch A; deactivate, activate and make-main are STRUCTURE operations (company-wide permission), refused on A as well
        var edited = await Send(HttpMethod.Put, $"/api/v1/company/branches/{branchA}", new { code = "MXA", nameAr = "فرع", nameEn = "Edited" }, cookie, ifMatch: "\"1\"");
        Assert.True(edited.Status == HttpStatusCode.OK, $"branch edit on branch A: {(int)edited.Status} {edited.Body}");
        var structure = scoped.Where(r => r.Branch == BranchTarget.BranchItself && r.Method == "POST").ToList();
        Assert.Equal(3, structure.Count);
        var onOwn = await NotForbidden(cookie, structure.Select(r => (r, r.Pattern.Replace("{id:guid}", branchA.ToString()), (string?)null)));
        Assert.True(onOwn.Count == 0, "branch structure operations must need the company-wide structure permission:\n" + string.Join("\n", onOwn));
    }

    /// <summary>
    /// Review M1: the cost-center scope. A member holding the journal permissions for cost center A only (no branch or company grant) is refused with 403 on every journal route acting on an entry
    /// allocated to cost center B, and on creating one; nothing is written. Control: the same grant creates and submits entries allocated to A.
    /// </summary>
    [PostgresFact]
    public async Task A_member_scoped_to_another_cost_center_is_refused_403_on_the_journal_endpoints_while_the_same_grant_works_on_its_own_cost_center()
    {
        await Start();
        var ccA = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("MXCA", null, "مركز", "CC A", null, false))).Id;
        var ccB = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("MXCB", null, "مركز", "CC B", null, false))).Id;
        JournalInput Allocated(Guid cc) => new(new DateOnly(2026, 3, 10), "matrix cc", branchA, [Dr(Cash.Id, 10m, null, branchA, (cc, 10m)), Cr(Sales.Id, 10m, null, branchA, (cc, 10m))], null);
        var inA = Ok(await Journal.CreateDraftAsync(Admin.Id, Company.Id, Allocated(ccA), false)).Header.Id;
        var inB = Ok(await Journal.CreateDraftAsync(Admin.Id, Company.Id, Allocated(ccB), false)).Header.Id;
        var held = (await Rbac.GetEffectivePermissionsAsync(Admin.Id, Company.Id)).HeldPermissionCodes.ToHashSet();
        var journalCodes = PermissionCatalog.Default.All.Where(p => p.Resource.Key == "company.journal" && held.Contains(p.Code)).Select(p => p.Code).Order().ToArray();
        Assert.Contains(PermissionCatalog.Codes.JournalCreate, journalCodes);
        var (user, _) = await Member(journalCodes, ScopeKind.CostCenter, ccA);
        var cookie = await SignIn(user);

        string Body(Guid cc) => $$"""{"entryDate":"2026-03-10","description":"matrix cc","branchId":"{{branchA}}","lines":[{"accountId":"{{Cash.Id}}","debit":10,"branchId":"{{branchA}}","allocations":[{"costCenterId":"{{cc}}","amount":10}]},{"accountId":"{{Sales.Id}}","credit":10,"branchId":"{{branchA}}","allocations":[{"costCenterId":"{{cc}}","amount":10}]}]}""";
        var journalRoutes = Matrix.Where(r => r.Branch is BranchTarget.JournalEntry or BranchTarget.JournalCreate).ToList();
        Assert.Equal(8, journalRoutes.Count);

        var before = await Footprint();
        var failures = await NotForbidden(cookie, journalRoutes.Select(r => r.Branch == BranchTarget.JournalCreate
            ? (r, r.Pattern, (string?)Body(ccB))
            : (r, r.Pattern.Replace("{id:guid}", inB.ToString()), r.Method == "PUT" ? Body(ccB) : null)));
        Assert.True(failures.Count == 0, $"{failures.Count} journal endpoint(s) did not answer 403 to a member scoped to another cost center:\n" + string.Join("\n", failures));
        Assert.Equal(before, await Footprint());

        // control: the same grant works inside cost center A
        var created = await Send(HttpMethod.Post, "/api/v1/company/journal", System.Text.Json.JsonDocument.Parse(Body(ccA)).RootElement, cookie);
        Assert.True(created.Status == HttpStatusCode.Created, $"journal create in cost center A: {(int)created.Status} {created.Body}");
        var submitted = await Send(HttpMethod.Post, $"/api/v1/company/journal/{inA}/submit", null, cookie, ifMatch: "\"1\"");
        Assert.True(submitted.Status == HttpStatusCode.OK, $"journal submit in cost center A: {(int)submitted.Status} {submitted.Body}");
    }
}
