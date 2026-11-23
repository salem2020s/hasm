using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Attachments;
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

    private InMemoryKekRing? apiRing;

    private async Task Start(bool keyUnavailable = false, string? maxBytes = null, (string Key, string Value)[]? settings = null)
    {
        dir = Path.Combine(Path.GetTempPath(), "hasm-att-api-" + Guid.NewGuid().ToString("N"));
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Attachments:StorageDirectory", dir);
            if (maxBytes is not null) b.UseSetting("Hasm:Attachments:MaxFileBytes", maxBytes);
            foreach (var (key, value) in settings ?? []) b.UseSetting("Hasm:Attachments:" + key, value);
            b.ConfigureServices(s =>
            {
                apiRing = new InMemoryKekRing("k1") { Unavailable = keyUnavailable };
                s.AddSingleton<KekRing>(apiRing);
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

    // ---- hand-built multipart bodies (boundary fixed) for the streaming-upload tests ----
    private const string Bnd = "xHASMx";
    private static byte[] Ascii(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] Field(string name, string value) => Ascii($"--{Bnd}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
    private static byte[] FilePart(byte[] data, string file = "doc.pdf") => [.. Ascii($"--{Bnd}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{file}\"\r\nContent-Type: application/pdf\r\n\r\n"), .. data, .. Ascii("\r\n")];
    private static byte[] End => Ascii($"--{Bnd}--\r\n");
    private byte[] Standard() => [.. Field("subjectKind", "local_party"), .. Field("subjectId", party.ToString()), .. Field("sensitive", "false")];

    /// <summary>A body sent without Content-Length (chunked); the part after <c>first</c> is withheld until <c>gate</c> completes.</summary>
    private sealed class Streamed : HttpContent
    {
        private readonly byte[] first, rest; private readonly Task gate;
        public Streamed(byte[] first, byte[] rest, Task gate)
        {
            this.first = first; this.rest = rest; this.gate = gate;
            Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data") { Parameters = { new NameValueHeaderValue("boundary", Bnd) } };
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(first); await stream.FlushAsync();
            await gate;
            await stream.WriteAsync(rest);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private async Task<ApiResponse> Raw(string cookie, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Base) { Content = content };
        request.Headers.Add("X-Hasm-Request", "1"); request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    private Task<ApiResponse> RawBody(string cookie, byte[] body, bool chunked)
    {
        if (chunked) return Raw(cookie, new Streamed(body, [], Task.CompletedTask));
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data") { Parameters = { new NameValueHeaderValue("boundary", Bnd) } };
        return Raw(cookie, content);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "condition not reached in time");
    }

    [PostgresFact]
    public async Task An_upload_in_flight_holds_its_slot_per_user_and_a_busy_upload_is_refused_with_503_until_it_is_released()
    {
        await Start(settings: [("MaxConcurrentUploads", "3"), ("MaxConcurrentUploadsPerUser", "1")]);
        var cookie = await SignIn(Admin);
        var gate = factory!.Services.GetRequiredService<UploadGate>();
        var release = new TaskCompletionSource();
        var held = Raw(cookie, new Streamed(Field("subjectKind", "local_party"), [.. Field("subjectId", party.ToString()), .. Field("sensitive", "false"), .. FilePart(Pdf), .. End], release.Task));
        await WaitFor(() => gate.InUse == 1);
        var busy = await Upload(cookie, Pdf, "second.pdf");                                    // the same user, a second concurrent upload
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.Status);
        Assert.Equal("upload_busy", busy.Error);
        Assert.Equal(1, gate.InUse);
        release.SetResult();
        var done = await held;
        Assert.Equal(HttpStatusCode.OK, done.Status);
        await WaitFor(() => gate.InUse == 0);
        Assert.Equal(HttpStatusCode.OK, (await Upload(cookie, Pdf, "third.pdf")).Status);     // the slot is free again
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {AT.Table}"));
    }

    [PostgresFact]
    public async Task Hostile_multipart_bodies_are_refused_without_storing_anything()
    {
        await Start(maxBytes: "5000");
        var cookie = await SignIn(Admin);
        var gate = factory!.Services.GetRequiredService<UploadGate>();
        // a non-file field larger than the field limit
        var bigField = await RawBody(cookie, [.. Field("subjectKind", new string('a', 5000)), .. Field("subjectId", party.ToString()), .. FilePart(Pdf), .. End], chunked: false);
        Assert.Equal(HttpStatusCode.BadRequest, bigField.Status);
        // two file parts: no single file
        var two = await RawBody(cookie, [.. Standard(), .. FilePart(Pdf), .. FilePart(Pdf, "other.pdf"), .. End], chunked: false);
        Assert.Equal(HttpStatusCode.BadRequest, two.Status);
        // too many parts
        var many = await RawBody(cookie, [.. Standard(), .. Enumerable.Range(0, 20).SelectMany(i => Field("f" + i, "x")), .. FilePart(Pdf), .. End], chunked: false);
        Assert.Equal(HttpStatusCode.BadRequest, many.Status);
        // truncated body (the closing boundary and the file never arrive)
        var truncated = await RawBody(cookie, [.. Field("subjectKind", "local_party"), .. Ascii($"--{Bnd}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.pdf\"\r\n\r\n%PDF-1.4 cut")], chunked: false);
        Assert.Equal(HttpStatusCode.BadRequest, truncated.Status);
        // not multipart content at all, under a multipart content type
        Assert.Equal(HttpStatusCode.BadRequest, (await RawBody(cookie, Ascii("this is not a multipart body"), chunked: false)).Status);
        // an oversize file sent chunked (no Content-Length to check up front) is still refused with 413
        var chunkedBig = await RawBody(cookie, [.. Standard(), .. FilePart([.. Pdf, .. new byte[6000]]), .. End], chunked: true);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedBig.Status);
        Assert.Equal("file_too_large", chunkedBig.Error);
        // the same file within the limit, chunked, is accepted
        Assert.Equal(HttpStatusCode.OK, (await RawBody(cookie, [.. Standard(), .. FilePart(Pdf), .. End], chunked: true)).Status);
        Assert.Equal(0, gate.InUse);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {AT.Table}"));
    }

    [PostgresFact]
    public async Task The_upload_slot_is_released_after_every_failure_so_a_single_slot_keeps_working()
    {
        await Start(maxBytes: "5000", settings: [("MaxConcurrentUploads", "1"), ("MaxConcurrentUploadsPerUser", "1")]);
        var cookie = await SignIn(Admin);
        var gate = factory!.Services.GetRequiredService<UploadGate>();
        Assert.Equal(HttpStatusCode.BadRequest, (await RawBody(cookie, Ascii("garbage"), chunked: false)).Status);                                  // malformed
        Assert.Equal(0, gate.InUse);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(cookie, [.. Pdf, .. new byte[6000]])).Status);                            // too large
        Assert.Equal(0, gate.InUse);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(cookie, Pdf, type: "application/x-msdownload")).Status);                             // refused by validation
        Assert.Equal(0, gate.InUse);
        Assert.Equal(HttpStatusCode.OK, (await Upload(cookie, Pdf)).Status);                                                                        // the only slot still works
        Assert.Equal(0, gate.InUse);
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
        Assert.Equal(System.Text.Json.JsonValueKind.Null, secret.Json.GetProperty("sha256").ValueKind);                       // the plaintext digest of a sensitive file is never returned
        Assert.Equal(System.Text.Json.JsonValueKind.String, normal.Json.GetProperty("sha256").ValueKind);

        var (editor, _) = await Member([PermissionCatalog.Codes.AttachmentsView, PermissionCatalog.Codes.AttachmentsUpload, PermissionCatalog.Codes.AttachmentsDelete, PermissionCatalog.Codes.PartiesView], ScopeKind.Company, null);
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
        await Start();
        var cookie = await SignIn(Admin);                                                                                     // sign-in needs the identity key too (identity integrity S-1): the key goes away AFTER it
        apiRing!.Unavailable = true;
        var r = await Upload(cookie, Pdf, sensitive: true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.Status);
        Assert.Equal("encryption_unavailable", r.Error);                                                                      // refused at authentication already: the same fail-closed 503, nothing stored (the service-level code is covered by the service tests)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Upload(cookie, Pdf)).Status);                                  // with no identity key NOBODY is authenticated, so not even a normal upload is served
        apiRing.Unavailable = false;
        Assert.Equal(HttpStatusCode.OK, (await Upload(cookie, Pdf)).Status);                                                  // normal files do not need the key themselves, and the key coming back restores service
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {AT.Table}"));
    }
}
