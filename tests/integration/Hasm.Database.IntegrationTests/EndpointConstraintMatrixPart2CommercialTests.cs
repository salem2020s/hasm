using Hasm.Infrastructure.Commercial;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using DL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentLink;
using Mode = Hasm.Database.IntegrationTests.EndpointConstraintMatrixPart2Tests.Mode;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Constraint-violation matrix, part 2, batch 4: commercial documents and their links, on the commercial fixture (the internal test document type, branches, a customer, an item with a
/// price). The rule, the engine (<see cref="ConstraintRaces"/>) and the completeness list are <see cref="EndpointConstraintMatrixPart2Tests"/>'s. Disposable cluster only.
/// </summary>
public class EndpointConstraintMatrixPart2CommercialTests : CommercialTestBase
{
    private const string Base = "/api/v1/company/commercial-documents";
    private readonly ConstraintRaces races = new(6);
    private string cookie = "";

    private Task<ApiResponse?> Race(string what, string[] expected, HttpMethod method, string path, object? body, string? ifMatch = null, Mode mode = Mode.One, string? caller = null) =>
        races.Run(what, expected, method, path, () => Send(method, path, body, caller ?? cookie, ifMatch: ifMatch), mode);

    private static Guid IdOf(ApiResponse r) => ConstraintRaces.IdOf(r);
    private static string EtagOf(ApiResponse r) => ConstraintRaces.EtagOf(r);
    private ApiResponse Won(ApiResponse? winner) => races.Won(winner);

    private async Task<ApiResponse> Ok2xx(HttpMethod method, string path, object? body, string? ifMatch = null)
    {
        var r = await Send(method, path, body, cookie, ifMatch: ifMatch);
        Assert.True((int)r.Status is >= 200 and < 300, $"{path}: {(int)r.Status} {r.Body}");
        return r;
    }

    [PostgresFact]
    public async Task Commercial_documents_and_links_end_in_a_mapped_4xx_never_a_5xx()
    {
        await StartApi(configure: b => b.ConfigureServices(RegisterTestType));
        await ComposeAsync();
        var branch = await NewBranchId("CVB"); var party = await NewCustomerId("CVC"); var (item, unit) = await NewItemAndUnit("CVI");
        cookie = await SignIn(Admin);
        var approver = await SignIn(await NewApproverUser());
        var R = new[] { "412:revision_conflict" };
        object Body(string key, decimal qty = 2m) => new
        {
            docType = CommercialTestType.Name, branchId = branch, partyId = party, currency = "JOD", documentDate = "2026-06-15", mode = 1, note = "matrix", idempotencyKey = key,
            lines = new[] { new { itemId = item, unitId = unit, quantity = qty } },
        };

        // a create carries an idempotency key: the identical request returns the original (a replay), so every answer is that document, and exactly ONE exists
        var draft = Won(await Race("commercial create", ["2xx"], HttpMethod.Post, Base, Body("cvm2-doc-00001"), mode: Mode.Same));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {CD.Table} WHERE {CD.IdempotencyKey} = 'cvm2-doc-00001'"));
        var id = IdOf(draft);
        var updated = Won(await Race("commercial update", R, HttpMethod.Put, $"{Base}/{id}", Body("cvm2-doc-00001", 3m), EtagOf(draft)));
        var submitted = Won(await Race("commercial submit", R, HttpMethod.Post, $"{Base}/{id}/submit", null, EtagOf(updated)));
        var withdrawn = Won(await Race("commercial withdraw", R, HttpMethod.Post, $"{Base}/{id}/withdraw", null, EtagOf(submitted)));
        await Race("commercial cancel", R, HttpMethod.Post, $"{Base}/{id}/cancel", new { reason = "matrix" }, EtagOf(withdrawn));

        // approval and rejection by the delegated approver, each from one revision
        var toApprove = await Ok2xx(HttpMethod.Post, Base, Body("cvm2-doc-00002"));
        var approvable = await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/submit", null, EtagOf(toApprove));
        await Race("commercial approve", R, HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/approve", null, EtagOf(approvable), caller: approver);
        var toReject = await Ok2xx(HttpMethod.Post, Base, Body("cvm2-doc-00003"));
        var rejectable = await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(toReject)}/submit", null, EtagOf(toReject));
        await Race("commercial reject", R, HttpMethod.Post, $"{Base}/{IdOf(toReject)}/reject", new { reason = "matrix" }, EtagOf(rejectable), caller: approver);

        // a link carries an idempotency key too (the key is re-checked after the line locks, #164): every answer is the one link, and exactly ONE exists
        var approverId = await NewApprover();
        var order = await NewApproved(branch, party, "cvm2-order-0001", approverId, CommercialTestType.Name, (item, unit, 10m));
        var delivery = await NewApproved(branch, party, "cvm2-deliv-0001", approverId, CommercialTestType.Name, (item, unit, 6m));
        await Race("commercial link", ["2xx"], HttpMethod.Post, $"{Base}/links",
            new { fromDocumentId = order.Id, fromLineNo = 1, toDocumentId = delivery.Id, toLineNo = 1, kind = 1, quantity = 4m, idempotencyKey = "cvm2-link-0001" }, mode: Mode.Same);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {DL.Table} WHERE {DL.IdempotencyKey} = 'cvm2-link-0001'"));

        races.AssertNoFindings();
        races.AssertRan(EndpointConstraintMatrixPart2Tests.Shares[nameof(Commercial_documents_and_links_end_in_a_mapped_4xx_never_a_5xx)]);
    }
}
