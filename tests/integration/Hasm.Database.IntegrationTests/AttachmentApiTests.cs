using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using AT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Attachment;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.12: the attachment API over real HTTP: authentication, company selection, multipart upload, hardened download, RBAC with the separate sensitive permission, revision-fenced removal, no DELETE.</summary>
public class AttachmentApiTests : HttpApiTestBase
{
    private const string Base = "/api/v1/company/attachments";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 api attachment");
    private string dir = null!;
    private Guid party;

    private async Task Start(bool keyUnavailable = false, string? maxBytes = null)
    {
        dir = Path.Combine(Path.GetTempPath(), "hasm-att-api-" + Guid.NewGuid().ToString("N"));
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Attachments:StorageDirectory", dir);
            if (maxBytes is not null) b.UseSetting("Hasm:Attachments:MaxFileBytes", maxBytes);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<KekRing>(new InMemoryKekRing("k1") { Unavailable = keyUnavailable });
                s.AddSingleton(new EncryptionOptions { AllowInsecureFilePermissions = true });
            });
        });
        party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'AP', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (dir is not null && Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private async Task<ApiResponse> Upload(string? cookie, byte[] bytes, string fileName = "doc.pdf", string type = "application/pdf", bool sensitive = false, string kind = "local_party", Guid? subject = null, bool header = true)
    {
        using var form = new MultipartFormDataContent { { new StringContent(kind), "subjectKind" }, { new StringContent((subject ?? party).ToString()), "subjectId" }, { new StringContent(sensitive ? "true" : "false"), "sensitive" } };
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue(type); form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, Base) { Content = form };
        if (header) request.Headers.Add("X-Hasm-Request", "1");
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, byte[] Bytes, HttpResponseMessage Raw)> Download(string cookie, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/{id}/content");
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response);
    }

    [PostgresFact]
    public async Task Every_endpoint_needs_authentication_and_a_selected_company_and_nothing_can_be_deleted()
    {
        await Start();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[] { (HttpMethod.Get, $"{Base}?subjectKind=local_party&subjectId={party}"), (HttpMethod.Get, $"{Base}/capabilities"), (HttpMethod.Get, $"{Base}/{id}/content"), (HttpMethod.Post, $"{Base}/{id}/remove") };
        foreach (var (method, path) in routes)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, method == HttpMethod.Get ? null : new { })).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, method == HttpMethod.Get ? null : new { }, cookie: "forged")).Status);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(null, Pdf)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload("forged", Pdf)).Status);
        var noCompany = await SignIn(Admin, selectCompany: false);
        foreach (var (method, path) in routes)
        {
            var r = await Send(method, path, method == HttpMethod.Get ? null : new { }, noCompany, ifMatch: "\"1\"");
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("company_not_selected", r.Error);
        }
        var up = await Upload(noCompany, Pdf);
        Assert.Equal(HttpStatusCode.Conflict, up.Status);
        var cookie = await SignIn(Admin);
        foreach (var path in new[] { Base, $"{Base}/{id}", $"{Base}/{id}/content" })
            Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Delete, path, null, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}?subjectKind=nope&subjectId={party}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}?subjectKind=local_party", cookie: cookie)).Status);
    }

    [PostgresFact]
    public async Task Upload_list_download_and_removal_work_end_to_end_with_a_hardened_download()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var caps = await Send(HttpMethod.Get, $"{Base}/capabilities", cookie: cookie);
        Assert.True(caps.Json.GetProperty("upload").GetBoolean());
        Assert.Contains("application/pdf", caps.Body);

        var up = await Upload(cookie, Pdf, "../../secret/contract.pdf");
        Assert.Equal(HttpStatusCode.OK, up.Status);
        Assert.Equal("\"1\"", Etag(up));
        var id = up.Json.GetProperty("id").GetGuid();
        Assert.Equal("contract.pdf", up.Json.GetProperty("fileName").GetString());
        Assert.DoesNotContain("storage", up.Body, StringComparison.OrdinalIgnoreCase);                                       // no stored name or path is ever exposed

        var list = await Send(HttpMethod.Get, $"{Base}?subjectKind=local_party&subjectId={party}", cookie: cookie);
        Assert.Equal(1, list.Json.GetProperty("items").GetArrayLength());
        Assert.True(list.Json.GetProperty("can").GetProperty("upload").GetBoolean());

        var dl = await Download(cookie, id);
        Assert.Equal(HttpStatusCode.OK, dl.Status);
        Assert.Equal(Pdf, dl.Bytes);
        Assert.Equal("attachment", dl.Raw.Content.Headers.ContentDisposition!.DispositionType);                              // never inline
        Assert.Equal("contract.pdf", dl.Raw.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        Assert.Equal("nosniff", dl.Raw.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("sandbox", dl.Raw.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", dl.Raw.Headers.CacheControl!.ToString());

        // removal: revision fence and a reason
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Base}/{id}/remove", new { reason = "x" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Base}/{id}/remove", new { reason = "x" }, cookie, ifMatch: "\"7\"")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{id}/remove", new { reason = " " }, cookie, ifMatch: "\"1\"")).Status);
        var removed = await Send(HttpMethod.Post, $"{Base}/{id}/remove", new { reason = "wrong file" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Download(cookie, id)).Status);
        Assert.Equal(0, (await Send(HttpMethod.Get, $"{Base}?subjectKind=local_party&subjectId={party}", cookie: cookie)).Json.GetProperty("items").GetArrayLength());
    }

    [PostgresFact]
    public async Task Malformed_oversized_or_mismatched_uploads_are_refused_and_nothing_is_stored()
    {
        await Start(maxBytes: "5000");
        var cookie = await SignIn(Admin);
        var big = await Upload(cookie, [.. Pdf, .. new byte[6000]]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.Status);
        Assert.Contains("file_too_large", big.Body);
        var mismatch = await Upload(cookie, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1 });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);
        Assert.Contains("content_type_mismatch", mismatch.Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(cookie, Pdf, type: "application/x-msdownload")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(cookie, Pdf, kind: "journal_entry")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(cookie, Pdf, subject: Guid.NewGuid())).Status);
        using var json = new HttpRequestMessage(HttpMethod.Post, Base) { Content = JsonContent.Create(new { subjectKind = "local_party" }) };
        json.Headers.Add("X-Hasm-Request", "1"); json.Headers.Add("Cookie", $"{CookieName}={cookie}");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(json)).StatusCode);                                  // not multipart
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {AT.Table}"));
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir, "*.att", SearchOption.AllDirectories).Length > 0);
    }

    [PostgresFact]
    public async Task A_sensitive_file_is_hidden_from_a_user_without_the_sensitive_permission_and_a_missing_key_is_a_server_error()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var secret = await Upload(cookie, Pdf, "pay.pdf", sensitive: true);
        Assert.Equal(HttpStatusCode.OK, secret.Status);
        var secretId = secret.Json.GetProperty("id").GetGuid();
        var normal = await Upload(cookie, Pdf, "n.pdf");
        var normalId = normal.Json.GetProperty("id").GetGuid();
        Assert.Equal(Pdf, (await Download(cookie, secretId)).Bytes);

        var (editor, _) = await Member([PermissionCatalog.Codes.AttachmentsView, PermissionCatalog.Codes.AttachmentsUpload, PermissionCatalog.Codes.AttachmentsDelete], ScopeKind.Company, null);
        var editorCookie = await SignIn(editor);
        var list = await Send(HttpMethod.Get, $"{Base}?subjectKind=local_party&subjectId={party}", cookie: editorCookie);
        Assert.Equal(1, list.Json.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Download(editorCookie, secretId)).Status);                               // a sensitive file looks missing
        Assert.Equal(HttpStatusCode.OK, (await Download(editorCookie, normalId)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(editorCookie, Pdf, sensitive: true)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Base}/{secretId}/remove", new { reason = "x" }, editorCookie, ifMatch: "\"1\"")).Status);

        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        var outsiderCookie = await SignIn(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}?subjectKind=local_party&subjectId={party}", cookie: outsiderCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(outsiderCookie, Pdf)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Download(outsiderCookie, normalId)).Status);
    }

    [PostgresFact]
    public async Task Without_a_usable_encryption_key_a_sensitive_upload_is_a_503_and_stores_nothing()
    {
        await Start(keyUnavailable: true);
        var cookie = await SignIn(Admin);
        var r = await Upload(cookie, Pdf, sensitive: true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.Status);
        Assert.Equal("encryption_key_unavailable", r.Error);
        Assert.Equal(HttpStatusCode.OK, (await Upload(cookie, Pdf)).Status);                                                  // normal files do not need the key
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {AT.Table}"));
    }
}
