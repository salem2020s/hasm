using System.Net;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.9C slice 1: the template endpoints over real HTTP (authentication, company, RBAC, optimistic version, pre-fill only).</summary>
public class ImportTemplateApiTests : ImportTestBase
{
    private const string Base = "/api/v1/company/imports";
    private readonly ItemsTestAdapter Items = new();

    private async Task Start()
    {
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Imports:StorageDirectory", StorageDir);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<KekRing>(new InMemoryKekRing("k1"));
                s.AddSingleton(TestEncryption);
                s.AddSingleton<IImportAdapter>(Items);
            });
        });
        BuildImportFacade(adapters: [Items]);
    }

    private async Task<ImportBatchView> Staged(string csv)
    {
        var b = Ok(await Imports.UploadAsync(Admin.Id, Company.Id, "items", "items.csv", Bytes(csv), null));
        return Ok(await Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision, new StageInput(0, 1, "utf-8", null)));
    }

    [PostgresFact]
    public async Task Every_template_endpoint_needs_authentication_and_a_selected_company()
    {
        await Start();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, $"{Base}/templates"), (HttpMethod.Get, $"{Base}/templates/items/T/history"), (HttpMethod.Post, $"{Base}/templates/items/T/retire"),
            (HttpMethod.Post, $"{Base}/{id}/mapping/template"), (HttpMethod.Post, $"{Base}/{id}/mapping/apply-template"),
        };
        foreach (var (method, path) in routes)
        {
            object? body = method == HttpMethod.Get ? null : new { };
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body, cookie: "forged")).Status);
        }
        var noCompany = await SignIn(Admin, selectCompany: false);
        foreach (var (method, path) in routes)
        {
            var r = await Send(method, path, method == HttpMethod.Get ? null : new { }, noCompany);
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("company_not_selected", r.Error);
        }
    }

    [PostgresFact]
    public async Task Save_apply_list_history_and_retire_work_end_to_end_and_applying_only_prefills()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var mapped = await Staged("code,name,qty\nA1,Alpha,5\n");
        var map = new JsonObject { ["fields"] = new JsonObject { ["code"] = new JsonObject { ["column"] = 0 }, ["name"] = new JsonObject { ["column"] = 1 }, ["qty"] = new JsonObject { ["column"] = 2 } } };
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/{mapped.Id}/mapping", map, cookie, ifMatch: $"\"{mapped.Revision}\"")).Status);

        var saved = await Send(HttpMethod.Post, $"{Base}/{mapped.Id}/mapping/template", new { name = "Sheet", expectedVersion = 0 }, cookie);
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal(1, saved.Json.GetProperty("version").GetInt32());
        Assert.Equal("code", saved.Json.GetProperty("content").GetProperty("fields").GetProperty("code").GetProperty("header").GetString());
        var stale = await Send(HttpMethod.Post, $"{Base}/{mapped.Id}/mapping/template", new { name = "Sheet", expectedVersion = 0 }, cookie);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal(1, stale.Json.GetProperty("currentRevision").GetInt32());
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Base}/{mapped.Id}/mapping/template", new { name = "Sheet" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{mapped.Id}/mapping/template", new { name = " ", expectedVersion = 0 }, cookie)).Status);

        var other = await Staged("name,code,qty\nBeta,B2,7\n");
        var applied = await Send(HttpMethod.Post, $"{Base}/{other.Id}/mapping/apply-template", new { name = "Sheet" }, cookie);
        Assert.Equal(HttpStatusCode.OK, applied.Status);
        Assert.Equal(1, applied.Json.GetProperty("mapping").GetProperty("fields").GetProperty("code").GetProperty("column").GetInt32());
        Assert.Equal(0, applied.Json.GetProperty("unmatched").GetArrayLength());
        Assert.Null((await Imports.GetAsync(Company.Id, other.Id))!.Mapping);                                              // nothing was written to the batch
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Base}/{other.Id}/mapping/apply-template", new { name = "Missing" }, cookie)).Status);

        var list = await Send(HttpMethod.Get, $"{Base}/templates?adapter=items", cookie: cookie);
        Assert.Equal(1, list.Json.GetProperty("templates").GetArrayLength());
        Assert.Equal(1, (await Send(HttpMethod.Get, $"{Base}/templates/items/Sheet/history", cookie: cookie)).Json.GetProperty("versions").GetArrayLength());
        var retired = await Send(HttpMethod.Post, $"{Base}/templates/items/Sheet/retire", new { expectedVersion = 1 }, cookie);
        Assert.Equal(HttpStatusCode.OK, retired.Status);
        Assert.True(retired.Json.GetProperty("isRetired").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Base}/{other.Id}/mapping/apply-template", new { name = "Sheet" }, cookie)).Status);

        var (viewer, _) = await Member([PermissionCatalog.Codes.ImportsView, "company.accounts.view"], ScopeKind.Company, null);
        var viewerCookie = await SignIn(viewer);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/templates", cookie: viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{mapped.Id}/mapping/template", new { name = "X", expectedVersion = 0 }, viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/templates/items/Sheet/retire", new { expectedVersion = 2 }, viewerCookie)).Status);
    }
}
