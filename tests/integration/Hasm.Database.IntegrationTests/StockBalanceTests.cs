using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.8A: read-only stock balances, item ledger and the reconciliation with the General Ledger (system migration 0020) against real PostgreSQL. Disposable cluster only.</summary>
public abstract class StockBalanceTestBase : StockPostingTestBase
{
    protected StockBalanceService Balances = null!;
    protected StockBalanceManagementService BalMgmt = null!;

    protected async Task PrepareBalances()
    {
        await PreparePosting();
        Balances = new StockBalanceService(Resolver);
        BalMgmt = new StockBalanceManagementService(Rbac, Docs, Balances);
    }

    protected static StockBalanceFilter F(Guid[] visible, Guid? item = null, Guid? wh = null, DateOnly? asOf = null, bool zero = false, string? search = null, int offset = 0, int limit = 50) =>
        new(item, null, wh, search, asOf, zero, visible, offset, limit);
}

public class StockBalanceTests : StockBalanceTestBase
{
    [PostgresFact]
    public async Task Balances_equal_the_costing_positions_exactly_for_every_item_and_warehouse_and_reconcile_to_the_ledger_once_the_entries_are_posted()
    {
        await PrepareBalances();
        var r1 = Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10), L(Avg, Pcs, 4))), Amounts(100m, 40m)));
        var r2 = Ok(await Post(await Approved(Receipt(W1, "2026-03-12", L(Fifo, Pcs, 5))), Amounts(75m)));
        var i1 = Ok(await Post(await Approved(Issue(W1, "2026-03-14", L(Fifo, Pcs, 12)))));
        Ok(await Post(await Approved(Transfer(W1, W2, "2026-03-15", L(Avg, Pcs, 1)))));
        var all = new[] { W1.Id, W2.Id };
        var page = Ok(await BalMgmt.BalancesAsync(Admin.Id, Company.Id, F(all)));
        Assert.Equal(3, page.Total);
        foreach (var row in page.Items)
        {
            var pos = (await Cost.GetPositionAsync(Company.Id, row.ItemId, row.WarehouseId))!;
            Assert.Equal((pos.Quantity, pos.Value), (row.Quantity, row.Value));                                                  // the balance IS the costing position
        }
        Assert.Equal(page.Items.Sum(i => i.Value), page.TotalValue);
        // as of a past date it is the history to that date, never today's stock
        var oldDate = Ok(await BalMgmt.BalancesAsync(Admin.Id, Company.Id, F(all, item: Fifo.Id, asOf: new DateOnly(2026, 3, 11))));
        Assert.Equal((10m, 100m), (oldDate.Items.Single().Quantity, oldDate.Items.Single().Value));
        // the ledger agrees only after the entries are posted
        var pending = Ok(await BalMgmt.ReconcileAsync(Admin.Id, Company.Id, new DateOnly(2026, 3, 31)));
        Assert.NotEqual(0m, pending.Difference);
        var poster = (await Member([PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalPost], ScopeKind.Company, null)).User;
        foreach (var d in new[] { r1.JournalEntryId!.Value, r2.JournalEntryId!.Value, i1.JournalEntryId!.Value })
        {
            var entry = (await Journal.GetAsync(Company.Id, d))!;
            var sub = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, entry.Header.Id, entry.Header.Revision));
            Ok(await Journal.PostAsync(poster.Id, Company.Id, entry.Header.Id, sub.Header.Revision, false));
        }
        var agreed = Ok(await BalMgmt.ReconcileAsync(Admin.Id, Company.Id, new DateOnly(2026, 3, 31)));
        Assert.Equal((0m, page.TotalValue), (agreed.Difference, agreed.CostingValue));
        Assert.Equal(StockAcc.Id, agreed.Accounts.Single().AccountId);
        Assert.NotEqual(r1.Id, r2.Id);
    }

    [PostgresFact]
    public async Task The_ledger_shows_opening_movements_and_closing_with_running_balances_and_the_source_document()
    {
        await PreparePosting0();
        var first = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        Ok(await Post(first, Amounts(100m)));
        Ok(await Post(await Approved(Receipt(W1, "2026-03-20", L(Fifo, Pcs, 10))), Amounts(150m)));
        var issue = await Approved(Issue(W1, "2026-03-25", L(Fifo, Pcs, 12)));
        Ok(await Post(issue));
        var page = Ok(await BalMgmt.LedgerAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id, new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 31), 0, 100));
        Assert.Equal((10m, 100m), (page.OpeningQuantity, page.OpeningValue));
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal((8m, 120m), (page.ClosingQuantity, page.ClosingValue));
        Assert.Equal(page.OpeningQuantity + page.Rows.Sum(r => r.Direction * r.Quantity), page.ClosingQuantity);                  // opening + movements = closing
        Assert.Equal(page.OpeningValue + page.Rows.Sum(r => r.Direction * r.Amount), page.ClosingValue);
        Assert.Equal(issue.Number, page.Rows.Last().SourceNumber);                                                                // drill-down to the document
        Assert.Equal(("stock_document", issue.Id), (page.Rows.Last().SourceType, page.Rows.Last().SourceId));
        Assert.Equal((8m, 120m), (page.Rows.Last().QuantityAfter, page.Rows.Last().ValueAfter));
        var paged = Ok(await BalMgmt.LedgerAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id, null, null, 1, 1));
        Assert.Equal((3L, 1), (paged.Total, paged.Rows.Count));                                                                   // paging does not change the totals
    }

    private Task PreparePosting0() => PrepareBalances();

    [PostgresFact]
    public async Task Authorization_is_applied_inside_the_queries_before_paging_totals_and_the_reconciliation()
    {
        await PrepareBalances();
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10))), Amounts(100m)));
        Ok(await Post(await Approved(Receipt(W2, "2026-03-10", L(Fifo, Pcs, 4))), Amounts(40m)));
        var (onlyW1, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var (nobody, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null);
        var (everywhere, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Company, null);
        var page = Ok(await BalMgmt.BalancesAsync(onlyW1.Id, Company.Id, F([])));
        Assert.Equal((1L, 100m), (page.Total, page.TotalValue));                                                                  // W2 is neither listed, counted nor summed
        var asked = Ok(await BalMgmt.BalancesAsync(onlyW1.Id, Company.Id, F([], wh: W2.Id)));
        Assert.Equal((0L, 0m), (asked.Total, asked.TotalValue));                                                                  // asking for a warehouse that is not visible reveals nothing
        Fails(await BalMgmt.LedgerAsync(onlyW1.Id, Company.Id, Fifo.Id, W2.Id, null, null, 0, 50), ConfigStatus.Forbidden, "forbidden");
        Fails(await BalMgmt.ReconcileAsync(onlyW1.Id, Company.Id, new DateOnly(2026, 3, 31)), ConfigStatus.Forbidden, "forbidden");   // company-wide: needs every warehouse
        Fails(await BalMgmt.BalancesAsync(nobody.Id, Company.Id, F([])), ConfigStatus.Forbidden, "forbidden");
        Ok(await BalMgmt.ReconcileAsync(everywhere.Id, Company.Id, new DateOnly(2026, 3, 31)));
        Assert.Equal(2L, Ok(await BalMgmt.BalancesAsync(everywhere.Id, Company.Id, F([]))).Total);
        var admin = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!.Single(r => r.PresetKey == "company-administrator");
        Assert.Contains(PermissionCatalog.Codes.StockBalanceView, admin.Permissions);
    }

    [PostgresFact]
    public async Task Filters_zero_balances_search_and_paging_work_and_reading_changes_nothing()
    {
        await PrepareBalances();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10), L(Avg, Pcs, 4)));
        Ok(await Post(doc, Amounts(100m, 40m)));
        var empty = await Approved(Issue(W1, "2026-03-11", L(Avg, Pcs, 4)));
        Ok(await Post(empty));
        var all = new[] { W1.Id };
        Assert.Equal(1L, Ok(await BalMgmt.BalancesAsync(Admin.Id, Company.Id, F(all))).Total);                                    // the emptied position is hidden
        Assert.Equal(2L, Ok(await BalMgmt.BalancesAsync(Admin.Id, Company.Id, F(all, zero: true))).Total);                        // unless zero balances are requested
        Assert.Equal(1L, Ok(await BalMgmt.BalancesAsync(Admin.Id, Company.Id, F(all, zero: true, search: "FIFO"))).Total);
        Assert.Equal(0L, Ok(await BalMgmt.BalancesAsync(Admin.Id, Company.Id, F(all, zero: true, search: "%"))).Total);          // a wildcard is a literal
        var paged = Ok(await BalMgmt.BalancesAsync(Admin.Id, Company.Id, F(all, zero: true, offset: 1, limit: 1)));
        Assert.Equal((2L, 1), (paged.Total, paged.Items.Count));
        var items = Ok(await BalMgmt.ItemsAsync(Admin.Id, Company.Id, "fifo"));
        Assert.Contains(items, i => i.Id == Fifo.Id);
        var before = await Counts();
        await BalMgmt.BalancesAsync(Admin.Id, Company.Id, F(all));
        Assert.Equal(before, await Counts());
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }
}

public class StockBalanceApiTests : StockBalanceTestBase
{
    [PostgresFact]
    public async Task The_balance_endpoints_need_authentication_validate_their_input_and_return_only_visible_warehouses()
    {
        await StartApi();
        await PrepareBalances();
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10))), Amounts(100m)));
        Ok(await Post(await Approved(Receipt(W2, "2026-03-10", L(Fifo, Pcs, 4))), Amounts(40m)));
        const string Base = "/api/v1/company/stock-balances";
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, Base)).Status);
        var cookie = await SignIn(Admin);
        var all = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal((2, 140m), (all.Json.GetProperty("balances").GetArrayLength(), all.Json.GetProperty("totalValue").GetDecimal()));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, Base + "?asOf=garbage", cookie: cookie)).Status);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, Base + "/ledger", cookie: cookie)).Status);
        var ledger = await Send(HttpMethod.Get, $"{Base}/ledger?itemId={Fifo.Id}&warehouseId={W1.Id}", cookie: cookie);
        Assert.Equal(1, ledger.Json.GetProperty("rows").GetArrayLength());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, Base + "/reconciliation", cookie: cookie)).Status);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await Send(HttpMethod.Get, Base + "/reconciliation?asOf=2026-03-31", cookie: cookie)).Status);
        var (onlyW1, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        var mine = await Send(HttpMethod.Get, Base, cookie: await SignIn(onlyW1));
        Assert.Equal((1, 100m), (mine.Json.GetProperty("balances").GetArrayLength(), mine.Json.GetProperty("totalValue").GetDecimal()));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/ledger?itemId={Fifo.Id}&warehouseId={W2.Id}", cookie: await SignIn(onlyW1))).Status);
        Assert.Equal(1, (await Send(HttpMethod.Get, Base + "/warehouses", cookie: await SignIn(onlyW1))).Json.GetProperty("warehouses").GetArrayLength());
    }
}
