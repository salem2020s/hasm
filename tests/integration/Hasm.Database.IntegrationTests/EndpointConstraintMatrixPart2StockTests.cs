using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Inventory;
using Mode = Hasm.Database.IntegrationTests.EndpointConstraintMatrixPart2Tests.Mode;
using SA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustment;
using SAP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentPosting;
using SAR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentReversal;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using SPR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingReversal;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Constraint-violation matrix, part 2, batch 3: stock documents (with posting, reversal and the negative-stock exception), stocktakes and stock adjustments, on the stock test fixture
/// (warehouses, items, units, inventory accounting, negative stock enabled). The rule, the engine (<see cref="ConstraintRaces"/>) and the completeness list are
/// <see cref="EndpointConstraintMatrixPart2Tests"/>'s. Disposable cluster only.
/// </summary>
public class EndpointConstraintMatrixPart2StockTests : NegativeStockTestBase
{
    private readonly ConstraintRaces races = new(6);
    private string cookie = "";

    private Task<ApiResponse?> Race(string what, string[] expected, HttpMethod method, string path, object? body, string? ifMatch = null, Mode mode = Mode.One, string? caller = null) =>
        races.Run(what, expected, () => Send(method, path, body, caller ?? cookie, ifMatch: ifMatch), mode);

    private static Guid IdOf(ApiResponse r) => ConstraintRaces.IdOf(r);
    private static string EtagOf(ApiResponse r) => ConstraintRaces.EtagOf(r);
    private ApiResponse Won(ApiResponse? winner) => races.Won(winner);

    private async Task<ApiResponse> Ok2xx(HttpMethod method, string path, object? body, string? ifMatch = null, string? caller = null)
    {
        var r = await Send(method, path, body, caller ?? cookie, ifMatch: ifMatch);
        Assert.True((int)r.Status is >= 200 and < 300, $"{path}: {(int)r.Status} {r.Body}");
        return r;
    }

    private async Task Start()
    {
        await StartApi();
        await PrepareNegative();
        cookie = await SignIn(Admin);
    }

    private StockDocumentInput ReceiptOf(ItemView item, decimal qty, string date = "2026-03-10") => new(1, null, W1.Id, date, "CV2", null, [new StockLineInput(item.Id, Pcs.Id, qty, null)]);

    [PostgresFact]
    public async Task Stock_documents_their_posting_reversal_and_negative_exception_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        const string Base = "/api/v1/company/stock-documents";
        var R = new[] { "412:revision_conflict" };
        var approver = await SignIn(await Approver());

        // a create carries no precondition and no uniqueness rule: each parallel request appends one draft
        var draft = Won(await Race("stock document create", [], HttpMethod.Post, Base, ReceiptOf(Apple, 2m), mode: Mode.Append));
        var id = IdOf(draft);
        var updated = Won(await Race("stock document update", R, HttpMethod.Put, $"{Base}/{id}", ReceiptOf(Apple, 3m), EtagOf(draft)));
        var submitted = Won(await Race("stock document submit", R, HttpMethod.Post, $"{Base}/{id}/submit", null, EtagOf(updated)));
        var withdrawn = Won(await Race("stock document withdraw", R, HttpMethod.Post, $"{Base}/{id}/withdraw", null, EtagOf(submitted)));
        await Race("stock document cancel", R, HttpMethod.Post, $"{Base}/{id}/cancel", new { reason = "entered by mistake" }, EtagOf(withdrawn));

        // approval and rejection by a separate approver, each from one revision
        var toReject = await Ok2xx(HttpMethod.Post, Base, ReceiptOf(Apple, 1m));
        var rejectable = await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(toReject)}/submit", null, EtagOf(toReject));
        await Race("stock document reject", R, HttpMethod.Post, $"{Base}/{IdOf(toReject)}/reject", new { note = "wrong warehouse" }, EtagOf(rejectable), caller: approver);
        var toApprove = await Ok2xx(HttpMethod.Post, Base, ReceiptOf(Fifo, 5m));
        var approvable = await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/submit", null, EtagOf(toApprove));
        var approved = Won(await Race("stock document approve", R, HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/approve", null, EtagOf(approvable), caller: approver));

        // posting is idempotent (a repeat returns the original posting): every answer is the posting, and there is exactly ONE
        var docId = IdOf(toApprove);
        await Race("stock document posting", ["2xx"], HttpMethod.Post, $"{Base}/{docId}/posting", new { lines = new[] { new { lineNo = 1, amount = 50m } } }, EtagOf(approved), Mode.Same);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SP.Table} WHERE {SP.DocumentId} = '{docId}'"));
        // a posting is reversed at most once and repeating the identical request returns the original reversal: every answer is that reversal, and there is exactly ONE
        await Race("stock document reversal", ["2xx"], HttpMethod.Post, $"{Base}/{docId}/reversal", new { effectiveDate = "2026-03-20", reason = "matrix" }, mode: Mode.Same);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SPR.Table} r JOIN {SP.Table} p ON p.{SP.Id} = r.{SPR.PostingId} WHERE p.{SP.DocumentId} = '{docId}'"));

        // the negative-stock exception of an approved issue that would take the position below zero: one approval per document revision
        await Emptied(Avg, 3m, 15m);
        var issue = await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 7m)));
        await Race("negative exception approval", ["409:already_approved", "412:revision_conflict"], HttpMethod.Post, $"{Base}/{issue.Id}/negative-exception/approvals",
            new { reason = "urgent dispatch before the delivery" }, $"\"{issue.Revision}\"", caller: await SignIn(Excepter));

        races.AssertNoFindings();
    }

    [PostgresFact]
    public async Task Stocktakes_and_stock_adjustments_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        const string Base = "/api/v1/company/stocktakes";
        var R = new[] { "412:revision_conflict" };
        var approver = await SignIn((await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StocktakeApprove], ScopeKind.Company, null)).User);
        await Receive(Fifo, 10m, 100m);
        StocktakeInput Count(decimal qty) => new(W1.Id, CountDate, "COUNT", null, [new StocktakeLineInput(Fifo.Id, Pcs.Id, qty, "counted")]);

        var draft = Won(await Race("stocktake create", [], HttpMethod.Post, Base, Count(8m), mode: Mode.Append));
        var id = IdOf(draft);
        var updated = Won(await Race("stocktake update", R, HttpMethod.Put, $"{Base}/{id}", Count(9m), EtagOf(draft)));
        var submitted = Won(await Race("stocktake submit", R, HttpMethod.Post, $"{Base}/{id}/submit", null, EtagOf(updated)));
        var withdrawn = Won(await Race("stocktake withdraw", R, HttpMethod.Post, $"{Base}/{id}/withdraw", null, EtagOf(submitted)));
        await Race("stocktake cancel", R, HttpMethod.Post, $"{Base}/{id}/cancel", new { reason = "recount" }, EtagOf(withdrawn));

        // the count sheet adds uncounted lines for the chosen items, from one revision (a separate stocktake: its new lines stay uncounted)
        var sheetTake = await Ok2xx(HttpMethod.Post, Base, Count(6m));
        await Race("stocktake generate sheet", R, HttpMethod.Post, $"{Base}/{IdOf(sheetTake)}/generate-sheet", new StocktakeSheetInput([Fifo.Id, Apple.Id]), EtagOf(sheetTake));

        var toReject = await Ok2xx(HttpMethod.Post, Base, Count(7m));
        var rejectable = await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(toReject)}/submit", null, EtagOf(toReject));
        await Race("stocktake reject", R, HttpMethod.Post, $"{Base}/{IdOf(toReject)}/reject", new { note = "count again" }, EtagOf(rejectable), caller: approver);
        var toApprove = await Ok2xx(HttpMethod.Post, Base, Count(8m));
        var approvable = await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/submit", null, EtagOf(toApprove));
        var approved = Won(await Race("stocktake approve", R, HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/approve", null, EtagOf(approvable), caller: approver));

        // the adjustment of an approved stocktake, its posting and its reversal each happen once, and the identical retry returns the original (documented replays): every answer is that
        // result, and the database holds exactly ONE adjustment, posting and reversal
        var adjustment = Won(await Race("stock adjustment generate", ["2xx"], HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/adjustment", null, EtagOf(approved), Mode.Same));
        var adjustmentId = IdOf(adjustment);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SA.Table} WHERE {SA.StocktakeId} = '{IdOf(toApprove)}'"));
        await Race("stock adjustment posting", ["2xx"], HttpMethod.Post, $"/api/v1/company/stock-adjustments/{adjustmentId}/posting", new { lines = Array.Empty<object>() }, mode: Mode.Same);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SAP.Table} WHERE {SAP.AdjustmentId} = '{adjustmentId}'"));
        await Race("stock adjustment reversal", ["2xx"], HttpMethod.Post, $"/api/v1/company/stock-adjustments/{adjustmentId}/reversal", new { effectiveDate = "2026-03-20", reason = "recount" }, mode: Mode.Same);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SAR.Table} r JOIN {SAP.Table} p ON p.{SAP.Id} = r.{SAR.PostingId} WHERE p.{SAP.AdjustmentId} = '{adjustmentId}'"));

        races.AssertNoFindings();
    }
}
