using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenter;
using Ib = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportBatch;
using Mode = Hasm.Database.IntegrationTests.EndpointConstraintMatrixPart2Tests.Mode;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Constraint-violation matrix, part 2, batch 4: imports (upload, the wizard steps, the approval workflow, application, mapping templates, settings and maintenance), on the import fixture with
/// the two test adapters (cost centers, which write real rows, and items, which produce conflicts to resolve). The rule, the engine (<see cref="ConstraintRaces"/>) and the completeness
/// list are <see cref="EndpointConstraintMatrixPart2Tests"/>'s. Disposable cluster only.
/// </summary>
public class EndpointConstraintMatrixPart2ImportTests : ImportTestBase
{
    private const string Base = "/api/v1/company/imports";
    private const string ItemsHeader = "Code,Name,Qty,Price,Start,Active,Kind\n";
    private static readonly JsonObject CenterMap = JsonNode.Parse("""{"fields":{"code":{"column":0},"name":{"column":1}}}""")!.AsObject();
    private readonly ConstraintRaces races = new(6);
    private readonly ItemsTestAdapter items = new();
    private string cookie = "";

    private Task<ApiResponse?> Race(string what, string[] expected, HttpMethod method, string path, object? body, string? ifMatch = null, Mode mode = Mode.One, string? caller = null) =>
        races.Run(what, expected, method, path, () => Send(method, path, body, caller ?? cookie, ifMatch: ifMatch), mode);

    private static Guid IdOf(ApiResponse r) => ConstraintRaces.IdOf(r);
    private static string EtagOf(ApiResponse r) => ConstraintRaces.EtagOf(r);
    private ApiResponse Won(ApiResponse? winner) => races.Won(winner);

    private async Task<ApiResponse> Ok2xx(HttpMethod method, string path, object? body, string? ifMatch = null, string? caller = null)
    {
        var r = await Send(method, path, body, caller ?? cookie, ifMatch: ifMatch);
        Assert.True((int)r.Status is >= 200 and < 300, $"{path}: {(int)r.Status} {r.Body}");
        return r;
    }

    private async Task<ApiResponse> Upload(string adapter, string csv, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}?adapter={adapter}&fileName={adapter}.csv") { Content = new ByteArrayContent(Bytes(csv)) };
        request.Headers.Add("X-Hasm-Request", "1");
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    private async Task<ApiResponse> Uploaded(string adapter, string csv)
    {
        var r = await Upload(adapter, csv);
        Assert.True((int)r.Status is >= 200 and < 300, $"upload: {(int)r.Status} {r.Body}");
        return r;
    }

    /// <summary>A cost-center batch taken to Validated over HTTP (upload, stage, mapping, dry run).</summary>
    private async Task<ApiResponse> Validated(string rows)
    {
        var up = await Uploaded("ccenters", "code,name\n" + rows);
        var staged = await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(up)}/stage", new { sheet = 0, headerRow = 1 }, EtagOf(up));
        var mapped = await Ok2xx(HttpMethod.Put, $"{Base}/{IdOf(up)}/mapping", CenterMap, EtagOf(staged));
        return await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(up)}/dry-run", new { }, EtagOf(mapped));
    }

    private async Task<ApiResponse> Submitted(string rows)
    {
        var v = await Validated(rows);
        return await Ok2xx(HttpMethod.Post, $"{Base}/{IdOf(v)}/submit", new { }, EtagOf(v));
    }

    [PostgresFact]
    public async Task Imports_from_upload_to_application_with_templates_settings_and_maintenance_end_in_a_mapped_4xx_never_a_5xx()
    {
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Imports:StorageDirectory", StorageDir);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<KekRing>(new InMemoryKekRing("k1", TestIdentityKeys.Master));
                s.AddSingleton(TestEncryption);
                s.AddSingleton<IImportAdapter>(new CostCenterTestAdapter());
                s.AddSingleton<IImportAdapter>(items);
            });
        });
        cookie = await SignIn(Admin);
        var R = new[] { "412:revision_conflict" };
        var (approverUser, _) = await Member([PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantImportApproverOfflineAsync(approverUser.Login, Company.Id));
        var approver = await SignIn(approverUser);

        // an upload with an idempotency key (the key race is resolved to the original batch, #165): every answer is that batch, and exactly ONE exists
        const string key = "cvm2-upload-0001";
        var up = Won(await races.Run("import upload", ["2xx"], HttpMethod.Post, Base, () => Upload("ccenters", "code,name\nM1,Matrix one\nM2,Matrix two\n", key), Mode.Same));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ib.Table} WHERE {Ib.IdempotencyKey} = '{key}'"));
        var id = IdOf(up);

        // the wizard steps, each from one revision
        var staged = Won(await Race("import stage", R, HttpMethod.Post, $"{Base}/{id}/stage", new { sheet = 0, headerRow = 1 }, EtagOf(up)));
        var mapped = Won(await Race("import mapping", R, HttpMethod.Put, $"{Base}/{id}/mapping", CenterMap, EtagOf(staged)));
        var validated = Won(await Race("import dry run", R, HttpMethod.Post, $"{Base}/{id}/dry-run", new { }, EtagOf(mapped)));
        var submitted = Won(await Race("import submit", R, HttpMethod.Post, $"{Base}/{id}/submit", new { }, EtagOf(validated)));
        await Race("import withdraw", R, HttpMethod.Post, $"{Base}/{id}/withdraw", new { }, EtagOf(submitted));

        // the decisions of the delegated approver, each from one revision; application is an idempotent retry that applies once
        var toReject = await Submitted("R1,Rejected one\n");
        await Race("import reject", R, HttpMethod.Post, $"{Base}/{IdOf(toReject)}/reject", new { comment = "not now" }, EtagOf(toReject), caller: approver);
        var toApprove = await Submitted("A1,Applied one\nA2,Applied two\n");
        var approved = Won(await Race("import approve", R, HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/approve", new { comment = "ok" }, EtagOf(toApprove), caller: approver));
        var centers = await Sql<long>($"SELECT count(*) FROM {Cc.Table}");
        await Race("import apply", ["2xx"], HttpMethod.Post, $"{Base}/{IdOf(toApprove)}/apply", new { }, EtagOf(approved), Mode.Same);
        Assert.Equal(centers + 2, await Sql<long>($"SELECT count(*) FROM {Cc.Table}"));

        // renewal and cancellation, each from one revision
        var toRenew = await Validated("N1,Renewed one\n");
        await Race("import renew", R, HttpMethod.Post, $"{Base}/{IdOf(toRenew)}/renew", null, EtagOf(toRenew));
        var toCancel = await Uploaded("ccenters", "code,name\nC1,Cancelled one\n");
        await Race("import cancel", R, HttpMethod.Post, $"{Base}/{IdOf(toCancel)}/cancel", null, EtagOf(toCancel));

        // conflicts to resolve (the items adapter knows E2), and the mapping templates of that batch
        items.Existing["E2"] = ("Original", "goods");
        var itemsUp = await Uploaded("items", ItemsHeader + "N1,New,1,2.5,2026-03-01,yes,goods\nE2,Changed,1,1,2026-03-01,yes,goods\n");
        var itemsId = IdOf(itemsUp);
        var itemsStaged = await Ok2xx(HttpMethod.Post, $"{Base}/{itemsId}/stage", new { sheet = 0, headerRow = 1 }, EtagOf(itemsUp));
        var suggestion = await Ok2xx(HttpMethod.Get, $"{Base}/{itemsId}/mapping/suggestion", null);
        var itemsMapped = await Ok2xx(HttpMethod.Put, $"{Base}/{itemsId}/mapping", suggestion.Json.GetProperty("mapping"), EtagOf(itemsStaged));
        var itemsValidated = await Ok2xx(HttpMethod.Post, $"{Base}/{itemsId}/dry-run", new { }, EtagOf(itemsMapped));
        await Race("import resolutions", R, HttpMethod.Post, $"{Base}/{itemsId}/resolutions", new { resolutions = new[] { new { rowNo = 3, action = "skip" } } }, EtagOf(itemsValidated));
        await Race("import save template", ["412:revision_conflict"], HttpMethod.Post, $"{Base}/{itemsId}/mapping/template", new { name = "Matrix", expectedVersion = 0 });
        await Race("import apply template", ["2xx"], HttpMethod.Post, $"{Base}/{itemsId}/mapping/apply-template", new { name = "Matrix" }, mode: Mode.Same);
        await Race("import retire template", ["412:revision_conflict"], HttpMethod.Post, $"{Base}/templates/items/Matrix/retire", new { expectedVersion = 1 });

        // settings from one revision, the first save included (#165); maintenance is repeatable. Deterministic (#168 H L1): the six parallel writers are held between their revision
        // read and their write until all six have read (the seventh, sequential request passes the spent barrier)
        var settingsService = factory!.Services.GetRequiredService<ImportService>();
        var settingsGate = new StartBarrier(6, TimeSpan.FromSeconds(60));
        settingsService.AfterSettingsReadForTests = settingsGate.WaitAsync;
        await Race("import settings", R, HttpMethod.Put, $"{Base}/settings", new ImportSettingsInput(30, 14), "\"0\"");
        settingsService.AfterSettingsReadForTests = null;
        Assert.Equal(6, settingsGate.Arrived);
        await Race("import maintenance", ["2xx"], HttpMethod.Post, $"{Base}/maintenance/run", null, mode: Mode.Same);
        await Race("import rewrap", ["2xx"], HttpMethod.Post, $"{Base}/maintenance/rewrap", null, mode: Mode.Same);

        races.AssertNoFindings();
        races.AssertRan(EndpointConstraintMatrixPart2Tests.Shares[nameof(Imports_from_upload_to_application_with_templates_settings_and_maintenance_end_in_a_mapped_4xx_never_a_5xx)]);
    }
}
