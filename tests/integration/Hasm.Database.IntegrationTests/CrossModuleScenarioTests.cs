using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.Companies;
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
using Microsoft.Extensions.DependencyInjection;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

// Release 1 acceptance, cross-module accounting scenarios (gap 3 of the acceptance-coverage analysis). Every scenario uses only behaviour that is already approved and merged: it chains modules that the
// per-module suites test one at a time, and asserts business outcomes (refusals, balances, values), never audit-trail internals. Disposable cluster only.

/// <summary>
/// Scenario (a), company isolation over real HTTP across every read family (SRS 4.3, scenario 21.3): two REAL companies, built with the same fixtures (same codes, different ids), with the same global party id value on both local parties (no registry identity; the registry path is covered by the party-registry suites).
/// A user of company A who presents company B's ids gets a refusal for every by-id read and never sees B's rows, values or totals in a list, report, balance or reconciliation; the same ids work for B's own user.
/// </summary>
public class CrossModuleIsolationScenarioTests : StockReversalTestBase
{
    private const string C = "/api/v1/company";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 cross-module isolation");
    private string dir = null!;

    private sealed record Seeded(CompanyRecord Company, UserRecord Admin, string Cookie, Guid Year, Guid Account, Guid Journal, Guid Warehouse, Guid Item, Guid PriceList, Guid Party, Guid Commercial, Guid Attachment)
    {
        /// <summary>Every id that exists only in this company (the global party id value written on both companies' customers is deliberately not here).</summary>
        public Guid[] Own => [Company.Id, Year, Account, Journal, Warehouse, Item, PriceList, Party, Commercial, Attachment];
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (dir is not null && Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private async Task<ApiResponse> Upload(string cookie, Guid party)
    {
        using var form = new MultipartFormDataContent { { new StringContent("local_party"), "subjectKind" }, { new StringContent(party.ToString()), "subjectId" }, { new StringContent("false"), "sensitive" } };
        var file = new ByteArrayContent(Pdf); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf"); form.Add(file, "file", "contract.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{C}/attachments") { Content = form };
        request.Headers.Add("X-Hasm-Request", "1");
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Builds one company with the standard posting fixtures and one object of every read family: a posted stock receipt with its posted journal (stock value <paramref name="amount"/>), a price list,
    /// a customer whose local record is linked to <paramref name="globalParty"/>, a draft commercial document and an attachment on the customer. Signs its administrator in with that company selected.
    /// </summary>
    private async Task<Seeded> Seed(string tag, decimal amount, Guid globalParty)
    {
        await PreparePosting();
        var posting = Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10))), Amounts(amount)));
        await PostJournal(posting);
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        var pricing = new PricingConfigService(Resolver, tx, Clock);
        var tax = new TaxConfigService(Resolver, tx, Clock);
        var roles = new PartyRoleService(Resolver, tx, Clock);
        var commercial = new CommercialDocumentService(Resolver, tx, Clock, new CommercialDocumentTypeRegistry([CommercialTestType.Name]), pricing, tax);
        var list = Ok(await pricing.CreatePriceListAsync(Admin.Id, Company.Id, new PriceListInput("PL-" + tag, "قائمة", "List " + tag, 10, "USD", new DateOnly(2026, 1, 1), null, true, null)));
        Ok(await pricing.CreateEntryAsync(Admin.Id, Company.Id, list.Id, new PriceEntryInput(Fifo.Id, null, Pcs.Id, 0m, 10m, null)));
        Ok(await tax.AddPolicyAsync(Admin.Id, Company.Id, new TaxPolicyInput(2, TaxRoundingMode.HalfAwayFromZero, TaxRoundingLevel.PerLine)));
        var party = Guid.NewGuid();   // created pending, then given the same global party id VALUE in both companies by SQL (link state 2 exactly when a global id is set); no registry identity is created here
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.GlobalPartyId}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'CUST', 'Customer {tag}', NULL, true, 1, 1, '{Admin.Id}', now(), now())");
        await Sql($"UPDATE {LP.Table} SET {LP.GlobalPartyId} = '{globalParty}', {LP.LinkState} = 2, {LP.Revision} = {LP.Revision} + 1 WHERE {LP.Id} = '{party}'");
        Ok(await roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(party, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null)));
        var doc = Ok(await commercial.CreateAsync(Admin.Id, Company.Id, new CommercialDocumentInput(CommercialTestType.Name, (await Main()).Id, party, "usd", new DateOnly(2026, 6, 15), 1, null, $"xm-{tag}-doc-0001", [new CommercialLineInput(Fifo.Id, Pcs.Id, 2m, null)])));
        var cookie = await SignIn(Admin);                                                                                       // selects THIS company
        var up = await Upload(cookie, party);
        Assert.True(up.Status is HttpStatusCode.OK or HttpStatusCode.Created, $"{up.Status} {up.Body}");
        var year = (await Calendar.ListAsync(Company.Id)).Single().Id;
        return new Seeded(Company, Admin, cookie, year, StockAcc.Id, posting.JournalEntryId!.Value, W1.Id, Fifo.Id, list.Id, party, doc.Id, up.Json.GetProperty("id").GetGuid());
    }

    private static void NoTrace(ApiResponse r, Seeded foreign, string path)
    {
        foreach (var id in foreign.Own) Assert.False(r.Body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase), $"{path} leaked the foreign id {id}");
    }

    [PostgresFact]
    public async Task A_user_of_company_A_reaches_nothing_of_company_B_through_any_read_family_even_with_the_same_global_party_id_value()
    {
        dir = Path.Combine(Path.GetTempPath(), "hasm-xmod-att-" + Guid.NewGuid().ToString("N"));
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Attachments:StorageDirectory", dir);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<KekRing>(new InMemoryKekRing("k1"));
                s.AddSingleton(new EncryptionOptions { AllowInsecureFilePermissions = true });
                s.AddSingleton<ICommercialDocumentTypeRegistry>(new CommercialDocumentTypeRegistry([CommercialTestType.Name]));
            });
        });
        var global = Guid.NewGuid();
        var b = await Seed("B", 777m, global);                                                                                  // the foreign company
        var a = await Seed("A", 100m, global);                                                                                  // the acting company (same codes, other ids)
        Assert.NotEqual(a.Company.Id, b.Company.Id);

        // Every by-id read of company B: it works for B's own administrator (the ids are real) and is refused for A's administrator without revealing anything.
        var byId = new[]
        {
            $"{C}/accounts/{b.Account}", $"{C}/journal/{b.Journal}", $"{C}/fiscal-years/{b.Year}", $"{C}/warehouses/{b.Warehouse}", $"{C}/inventory/items/{b.Item}",
            $"{C}/parties/{b.Party}", $"{C}/commercial-documents/{b.Commercial}", $"{C}/attachments/{b.Attachment}/content",
            $"{C}/stock-balances/ledger?itemId={b.Item}&warehouseId={b.Warehouse}", $"{C}/reports/trial-balance?fiscalYearId={b.Year}",
            $"{C}/reports/general-ledger?accountId={b.Account}&fiscalYearId={b.Year}",
        };
        foreach (var path in byId)
        {
            Assert.Equal((path, HttpStatusCode.OK), (path, (await Send(HttpMethod.Get, path, cookie: b.Cookie)).Status));
            var r = await Send(HttpMethod.Get, path, cookie: a.Cookie);
            Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, $"{path} answered {r.Status} to company A: {r.Body}");
            NoTrace(r, b, path);
        }

        // Every list, filtered read, balance and report of company A contains A's own objects and nothing of B (the same codes exist in both companies).
        var lists = new (string Path, Guid MustContain)[]
        {
            ($"{C}/accounts", a.Account), ($"{C}/journal", a.Journal), ($"{C}/fiscal-years", a.Year), ($"{C}/warehouses", a.Warehouse), ($"{C}/inventory/items", a.Item),
            ($"{C}/pricing/price-lists", a.PriceList), ($"{C}/parties", a.Party), ($"{C}/party-roles", a.Party), ($"{C}/commercial-documents/", a.Commercial),
            ($"{C}/attachments?subjectKind=local_party&subjectId={a.Party}", a.Attachment), ($"{C}/stock-balances", a.Item), ($"{C}/stock-balances/warehouses", a.Warehouse),
            ($"{C}/stock-balances/reconciliation?asOf=2026-03-31", a.Account), ($"{C}/reports/trial-balance?fiscalYearId={a.Year}", a.Account),
            ($"{C}/reports/general-ledger?accountId={a.Account}&fiscalYearId={a.Year}", a.Journal),
            ($"{C}/accounts?companyId={b.Company.Id}", a.Account),                                                              // a company id in the request is ignored
        };
        foreach (var (path, own) in lists)
        {
            var r = await Send(HttpMethod.Get, path, cookie: a.Cookie);
            Assert.Equal((path, HttpStatusCode.OK), (path, r.Status));
            Assert.True(r.Body.Contains(own.ToString(), StringComparison.OrdinalIgnoreCase), $"{path} does not show company A's own object {own}");
            NoTrace(r, b, path);
        }
        // B's ids used as FILTERS inside A (the entries of B's price list, the attachments of B's party, the balance of B's warehouse or item): refused or empty, never B's rows. The same filters show B's rows to B.
        foreach (var path in new[] { $"{C}/pricing/price-lists/{b.PriceList}/entries", $"{C}/attachments?subjectKind=local_party&subjectId={b.Party}", $"{C}/stock-balances?warehouseId={b.Warehouse}", $"{C}/stock-balances?itemId={b.Item}" })
        {
            var own = await Send(HttpMethod.Get, path, cookie: b.Cookie);
            Assert.Equal((path, HttpStatusCode.OK), (path, own.Status));
            Assert.True(own.Body.Contains(b.Item.ToString(), StringComparison.OrdinalIgnoreCase) || own.Body.Contains(b.Attachment.ToString(), StringComparison.OrdinalIgnoreCase), $"{path} shows B nothing of its own");
            var r = await Send(HttpMethod.Get, path, cookie: a.Cookie);
            Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, $"{path} answered {r.Status}");
            NoTrace(r, b, path);
            if (path.StartsWith($"{C}/stock-balances?", StringComparison.Ordinal) && r.Status == HttpStatusCode.OK)
                Assert.Equal((0m, 0), (r.Json.GetProperty("totalValue").GetDecimal(), r.Json.GetProperty("balances").GetArrayLength()));   // empty VALUES too, not only no foreign ids
        }

        // Totals: A's stock value and A's ledger value are A's alone (100), B's are B's alone (777); costing and ledger agree inside each company.
        var balA = await Send(HttpMethod.Get, $"{C}/stock-balances", cookie: a.Cookie);
        var balB = await Send(HttpMethod.Get, $"{C}/stock-balances", cookie: b.Cookie);
        Assert.Equal((100m, 777m), (balA.Json.GetProperty("totalValue").GetDecimal(), balB.Json.GetProperty("totalValue").GetDecimal()));
        foreach (var (s, value) in new[] { (a, 100m), (b, 777m) })
        {
            var rec = (await Send(HttpMethod.Get, $"{C}/stock-balances/reconciliation?asOf=2026-03-31", cookie: s.Cookie)).Json;
            Assert.Equal((value, value, 0m), (rec.GetProperty("costingValue").GetDecimal(), rec.GetProperty("ledgerValue").GetDecimal(), rec.GetProperty("difference").GetDecimal()));
        }

        // A's administrator cannot switch the session to company B, and the session keeps serving company A only.
        Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = b.Company.Id }, a.Cookie)).Status);
        var after = await Send(HttpMethod.Get, $"{C}/stock-balances", cookie: a.Cookie);
        Assert.Equal(100m, after.Json.GetProperty("totalValue").GetDecimal());
        NoTrace(after, b, "stock-balances after the refused switch");
        // A write that names B's document through company A does not reach it, and B's document is unchanged.
        Assert.True((await Send(HttpMethod.Post, $"{C}/commercial-documents/{b.Commercial}/cancel", new { reason = "cross-company attempt" }, a.Cookie, ifMatch: "\"1\"")).Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        var intact = await Send(HttpMethod.Get, $"{C}/commercial-documents/{b.Commercial}", cookie: b.Cookie);
        Assert.Equal((HttpStatusCode.OK, 1L), (intact.Status, intact.Json.GetProperty("revision").GetInt64()));
    }
}

/// <summary>
/// Scenario (b), the fiscal life cycle across journal, period close, exceptional reopening and year end (SRS 6, Phase 3.2C): a pending entry blocks the period close; once it is posted the period closes;
/// a Final Closed period refuses posting; reopening it needs a request approved by a different user and lands in Soft Closed (posting still refused without the soft-closed right) before the routine reopen to Open;
/// then the year is closed and carried forward, and the next year opens with exactly the balance-sheet balances. Business outcomes only.
/// </summary>
public class CrossModuleFiscalLifecycleScenarioTests : FiscalClosingTestBase
{
    private const string Why = "Correction of a misdated sales entry approved by finance";

    [PostgresFact]
    public async Task Close_is_blocked_by_pending_entries_closed_periods_refuse_posting_reopening_needs_an_independent_approval_and_the_year_end_carries_the_right_balances()
    {
        await PrepareClosing();                                                                                                 // 2026: sales 1000 (Main), expense 300 (Main), sales 200 (B2); FY2027 exists
        var cal = new FiscalCalendarService(Resolver, Tx, RealGuard(), new FinancialHistoryGuard(), Clock);                   // the real close probes, as the application registers them
        async Task<FiscalPeriodView> March() => (await cal.GetAsync(Company.Id, Year.Id))!.Periods.Single(p => p.StartDate == new DateOnly(2026, 3, 1));

        // 1. A pending (submitted, not posted) entry dated in March blocks closing March; nothing changes.
        var pending = await Submitted(Simple("2026-03-20", 50m));
        var march = await March();
        Assert.Equal((ConfigStatus.Conflict, "period_close_blocked"), Code(await cal.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision)));
        Assert.Equal(FiscalPeriodState.Open, (await March()).State);

        // 2. The pending state is resolved (the entry is posted); March can now be soft closed and final closed.
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Checker3, Company.Id, pending.Header.Id, pending.Header.Revision, false)).Header.Status);
        var soft = Ok(await cal.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        var final = Ok(await cal.FinalCloseAsync(Admin.Id, Company.Id, march.Id, soft.Revision));
        Assert.Equal(FiscalPeriodState.FinalClosed, final.State);

        // 3. Posting into the Final Closed period is refused, even with the soft-closed right.
        var late = await Submitted(Simple("2026-03-25", 10m));
        Assert.Equal((ConfigStatus.Conflict, "fiscal_period_final_closed"), Code(await Journal.PostAsync(Checker3, Company.Id, late.Header.Id, late.Header.Revision, true)));
        Assert.Equal(JournalStatus.Approved, (await Reload(late.Header.Id)).Header.Status);

        // 4. Reopening needs a reasoned request approved by a DIFFERENT user (not the requester, not the closer); it lands in Soft Closed, never straight in Open.
        var requester = await NewAdminOf(Company);
        var approver = await NewAdminOf(Company);
        var request = Ok(await cal.RequestPeriodReopenAsync(requester.Id, Company.Id, march.Id, final.Revision, new ReasonInput(Why)));
        Assert.Equal(FiscalPeriodState.FinalClosed, (await March()).State);                                                    // a request changes nothing
        Assert.Equal((ConfigStatus.Forbidden, "self_approval_forbidden"), Code(await cal.ApproveRequestAsync(requester.Id, Company.Id, request.Id, request.Revision, true)));
        Assert.Equal((ConfigStatus.Forbidden, "segregation_of_duties"), Code(await cal.ApproveRequestAsync(Admin.Id, Company.Id, request.Id, request.Revision, true)));
        Ok(await cal.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, true));
        var reopened = await March();
        Assert.Equal(FiscalPeriodState.SoftClosed, reopened.State);
        Assert.Equal((ConfigStatus.Conflict, "fiscal_period_soft_closed"), Code(await Journal.PostAsync(Checker3, Company.Id, late.Header.Id, late.Header.Revision, false)));
        Ok(await cal.ReopenAsync(Admin.Id, Company.Id, reopened.Id, reopened.Revision));                                       // the routine reopening of a soft close
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Checker3, Company.Id, late.Header.Id, late.Header.Revision, false)).Header.Status);

        // 5. Year end: the closing moves the year's result (sales 1000 + 50 + 10 + 200, expense 300: profit 960) to equity; the carry-forward opens 2027 with the balance sheet only.
        var closing = await Close();
        Assert.Equal(-960m, closing.NetResult);
        await PostYearEnd(closing.ClosingEntryId);
        var closed = await Tb("2026-01-01", "2026-12-31");
        Assert.Equal((0m, 0m, -960m), (Row(closed, Sales.Id).ClosingBalance, Row(closed, Expense.Id).ClosingBalance, Row(closed, Equity.Id).ClosingBalance));
        var activity = Ok(await RMg.TrialBalanceAsync(Admin.Id, Company.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, null, false, false, excludeClosing: true));
        Assert.Equal((-1260m, 300m), (Row(activity, Sales.Id).ClosingBalance, Row(activity, Expense.Id).ClosingBalance));  // the year's activity is still reportable
        var carry = await Carry();
        await PostYearEnd(carry.EntryId);
        var next = await Tb("2027-01-01", "2027-12-31");
        Assert.True(next.Balanced);
        Assert.Equal((960m, -960m), (Row(next, Cash.Id).ClosingBalance, Row(next, Equity.Id).ClosingBalance));
        Assert.Equal((760m, 200m), (Row(await Tb("2027-01-01", "2027-12-31", Main), Cash.Id).ClosingBalance, Row(await Tb("2027-01-01", "2027-12-31", B2), Cash.Id).ClosingBalance));
        Assert.DoesNotContain(next.Rows, r => (r.AccountId == Sales.Id || r.AccountId == Expense.Id) && r.ClosingBalance != 0m);
        Assert.Equal("carried_forward", (await Closing.StatusAsync(Company.Id, Year.Id))!.Closings.Single().State);
    }

    private static (ConfigStatus, string?) Code<T>(ConfigResult<T> r) => (r.Status, r.Code);
}

/// <summary>
/// Scenario (c), inventory valuation consistency between costing and the General Ledger across warehouses (SRS 7, Phase 3.7/3.8): receipts, issues and a transfer in two warehouses are posted with their journals;
/// the stock value (costing positions, balances report) equals the inventory account balance in the trial balance; after the reversal of an issue and the posting of its reversal entry they agree again, the
/// cost returns exactly, and the period can be closed by the real inventory-valuation close probe. Uses only the merged posting API (not the in-work posting of #107).
/// </summary>
public class CrossModuleInventoryValuationScenarioTests : StockReversalTestBase
{
    private async Task<decimal> LedgerBalance(Guid account, string to = "2026-03-31")
    {
        var tb = await new LedgerReportService(Resolver).TrialBalanceAsync(Company.Id, new ReportScope(new DateOnly(2026, 1, 1), DateOnly.Parse(to), null, null, null), false);
        return tb.Rows.SingleOrDefault(r => r.AccountId == account)?.ClosingBalance ?? 0m;
    }

    private async Task AssertAgree(decimal stock, decimal cogs, string asOf = "2026-03-31")
    {
        var balances = new StockBalanceManagementService(Rbac, Docs, new StockBalanceService(Resolver));
        Assert.Equal(stock, await CompanyValue());                                                                              // the costing positions
        Assert.Equal(stock, Ok(await balances.BalancesAsync(Admin.Id, Company.Id, new StockBalanceFilter(null, null, null, null, null, false, [W1.Id, W2.Id], 0, 50))).TotalValue);
        Assert.Equal(stock, await LedgerBalance(StockAcc.Id, asOf));                                                            // the inventory account in the trial balance
        Assert.Equal(cogs, await LedgerBalance(CogsAcc.Id, asOf));
        var rec = Ok(await balances.ReconcileAsync(Admin.Id, Company.Id, DateOnly.Parse(asOf)));
        Assert.Equal((stock, 0m), (rec.CostingValue, rec.Difference));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }

    private async Task<StockPostingView> PostWithJournal(StockDocumentInput input, StockPostInput? amounts = null)
    {
        var p = Ok(await Post(await Approved(input), amounts));
        if (p.JournalEntryId is { } j) await PostJournalEntry(j);
        return p;
    }

    [PostgresFact]
    public async Task Stock_value_equals_the_inventory_ledger_balance_across_warehouses_after_posting_and_after_a_reversal()
    {
        await PreparePosting();
        // W1: FIFO 10 pieces for 100 (10 each). W2: AVG 4 for 40 (10 each) and FIFO 5 for 60 (12 each).
        await PostWithJournal(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)), Amounts(100m));
        await PostWithJournal(Receipt(W2, "2026-03-10", L(Avg, Pcs, 4), L(Fifo, Pcs, 5)), Amounts(40m, 60m));
        await AssertAgree(200m, 0m);
        // Issues in both warehouses at their own cost (W1: 3 x 10 = 30; W2: 2 x 12 = 24) and a transfer W2 -> W1 that carries its cost (1 x 10) and changes no ledger balance.
        var issueW1 = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 3)));
        var i1 = Ok(await Post(issueW1));
        await PostJournalEntry(i1.JournalEntryId!.Value);
        await PostWithJournal(Issue(W2, "2026-03-13", L(Fifo, Pcs, 2)));
        await PostWithJournal(Transfer(W2, W1, "2026-03-14", L(Avg, Pcs, 1)));
        Assert.Equal((10m, 30m, 70m), ((await Pos(Avg, W1)).Value, (await Pos(Avg, W2)).Value, (await Pos(Fifo, W1)).Value));
        await AssertAgree(146m, 54m);

        // The W1 issue is reversed; until its reversal entry is posted the period cannot close, afterwards costing and ledger agree again and the 30 returns exactly.
        var r = Ok(await Reverse(issueW1, "2026-03-20", "issued to the wrong cost centre"));
        var march = await March();
        Fails(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision), ConfigStatus.Conflict, "period_close_blocked");
        await PostJournalEntry(r.Reversal!.JournalEntryId!.Value);
        Assert.Equal(100m, (await Pos(Fifo, W1)).Value);
        await AssertAgree(176m, 24m);
        Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));                                 // the real inventory-valuation probe finds no difference
    }
}
