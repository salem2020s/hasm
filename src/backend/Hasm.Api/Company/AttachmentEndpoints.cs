using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class AttachmentSetup
{
    public static IServiceCollection AddHasmAttachments(this IServiceCollection services, IConfiguration configuration)
    {
        var options = AttachmentOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.AddSingleton(new UploadGate(options.MaxConcurrentUploads));
        services.AddSingleton(sp => new AttachmentFileStore(options, sp.GetRequiredService<EncryptionOptions>()));
        services.AddSingleton(sp => new AttachmentService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<AttachmentFileStore>(),
            sp.GetRequiredService<EnvelopeEncryptor>(), options, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AttachmentManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<AttachmentService>()));
        return services;
    }
}

/// <summary>Non-blocking limit on concurrent uploads (each holds a bounded in-memory copy of its file).</summary>
public sealed class UploadGate(int max)
{
    private readonly SemaphoreSlim semaphore = new(max, max);
    public bool TryEnter() => semaphore.Wait(0);
    public void Exit() => semaphore.Release();
}

/// <summary>
/// /api/v1/company/attachments (Phase 3.12): files kept on the server, reached ONLY through these authorized endpoints (never a static path). The company comes only from the server-side session;
/// permissions are checked inside <see cref="AttachmentManagementService"/> (a sensitive file needs the sensitive permission and otherwise looks missing); a download is always an attachment with
/// the stored, allow-listed type, <c>nosniff</c> and a sandboxing CSP, never inline; removal is revision-fenced with a reason; there is no DELETE route.
/// </summary>
public static class AttachmentEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 4 * 1024; }
    private const long FormOverhead = 1024 * 1024;
    public sealed record RemoveRequest(string? Reason);

    public static void MapAttachmentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/attachments").RequireAuthorization();
        g.MapGet("", List);
        g.MapGet("/capabilities", Capabilities);
        g.MapPost("", Upload).DisableAntiforgery();
        g.MapGet("/{id:guid}/content", Download);
        g.MapPost("/{id:guid}/remove", Remove).WithMetadata(new BodyLimit());
        g.MapPost("/maintenance/rewrap", Rewrap);
        g.MapPost("/maintenance/sweep", Sweep);
        g.MapPost("/maintenance/verify", Verify);
    }

    private static object Body(AttachmentView v) => new
    {
        id = v.Id, subjectKind = v.SubjectKind, subjectId = v.SubjectId, fileName = v.FileName, contentType = v.ContentType, sizeBytes = v.SizeBytes, sha256 = v.IsSensitive ? null : v.Sha256, isSensitive = v.IsSensitive, revision = v.Revision, createdAtUtc = v.CreatedAtUtc,
    };

    private static IResult Map<T>(ConfigResult<T> r, Func<T, object> body, Func<T, long?>? revision = null)
    {
        // storage or key problems are the server's, not the caller's: 503 with a stable code and no detail
        if (r.Status == ConfigStatus.Conflict && r.Code is "attachment_storage_not_configured" or "encryption_not_configured" or "encryption_key_unavailable" or "encryption_key_invalid")
            return CompanyConfigurationEndpoints.Error(503, r.Code!);
        return CompanyConfigurationEndpoints.Map(r, body, revision);
    }

    private static async Task<IResult> Capabilities(HttpContext http, AccessService access, AttachmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var c = await svc.CapabilitiesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { view = c.View, upload = c.Upload, delete = c.Delete, sensitiveView = c.SensitiveView, sensitiveUpload = c.SensitiveUpload, allowedContentTypes = AttachmentService.AllowedContentTypes });
    }

    private static async Task<IResult> List(string? subjectKind, Guid? subjectId, HttpContext http, AccessService access, AttachmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (subjectKind is null || subjectId is null || !AttachmentSubjects.Kinds.ContainsKey(subjectKind)) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        var r = await svc.ListAsync(actor!.UserId, actor.CompanyId, subjectKind, subjectId.Value, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var caps = await svc.CapabilitiesAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { items = r.Value!.Select(Body), can = new { upload = caps.Upload, delete = caps.Delete, sensitiveUpload = caps.SensitiveUpload } });
    }

    private const int MaxFieldBytes = 4096;

    /// <summary>Reads at most <paramref name="max"/> bytes; null when the stream is longer. The caller never holds more than the bounded copy.</summary>
    private static async Task<byte[]?> ReadBounded(Stream s, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int n;
            while ((n = await s.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                if (ms.Length + n > max) return null;
                ms.Write(buffer, 0, n);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        return ms.ToArray();
    }

    private static async Task<IResult> Upload(HttpContext http, AccessService access, AttachmentManagementService svc, AttachmentOptions options, UploadGate gate)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        // authorize BEFORE the body is read: a user without the upload permission cannot make the server read a large form
        if (!(await svc.CapabilitiesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted)).Upload) return CompanyConfigurationEndpoints.Error(403, "forbidden");
        var limit = options.MaxFileBytes + FormOverhead;
        if (http.Request.ContentLength is { } len && len > limit) return CompanyConfigurationEndpoints.Error(413, "file_too_large");
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = limit;
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        var boundary = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        // bound the number of uploads held in memory at once (a refused upload is retried by the client)
        if (!gate.TryEnter()) return CompanyConfigurationEndpoints.Error(503, "upload_busy");
        try
        {
            // The multipart body is streamed section by section: the file is held only as one bounded in-memory copy and NOTHING is spooled to the operating-system temporary directory (no plaintext on disk).
            var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, http.Request.Body) { HeadersLengthLimit = 16 * 1024, BodyLengthLimit = limit };
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            byte[]? bytes = null; string? fileName = null, fileType = null; var files = 0;
            try
            {
                while (await reader.ReadNextSectionAsync(http.RequestAborted) is { } section)
                {
                    if (!Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd)
                        || !cd.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(cd.Name).Value ?? "";
                    var isFile = !string.IsNullOrEmpty(cd.FileName.Value) || !string.IsNullOrEmpty(cd.FileNameStar.Value);
                    if (isFile)
                    {
                        if (++files > 1) { await section.Body.CopyToAsync(Stream.Null, http.RequestAborted); continue; }      // more than one file is refused by the service as "no file"
                        bytes = await ReadBounded(section.Body, options.MaxFileBytes, http.RequestAborted);
                        if (bytes is null) return CompanyConfigurationEndpoints.Error(413, "file_too_large");
                        fileName = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(string.IsNullOrEmpty(cd.FileNameStar.Value) ? cd.FileName : cd.FileNameStar).Value;
                        fileType = section.ContentType;
                    }
                    else
                    {
                        var raw = await ReadBounded(section.Body, MaxFieldBytes, http.RequestAborted);
                        if (raw is null) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
                        fields[name] = System.Text.Encoding.UTF8.GetString(raw);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException or IOException) { return CompanyConfigurationEndpoints.Error(413, "file_too_large"); }
            if (files != 1) { bytes = null; fileName = null; fileType = null; }
            fields.TryGetValue("subjectKind", out var subjectKind);
            Guid? subjectId = fields.TryGetValue("subjectId", out var sidText) && Guid.TryParse(sidText, out var sid) ? sid : null;
            var sensitive = fields.TryGetValue("sensitive", out var sensitiveText) && string.Equals(sensitiveText, "true", StringComparison.OrdinalIgnoreCase);
            return Map(await svc.UploadAsync(actor!.UserId, actor.CompanyId, subjectKind, subjectId, fileName, fileType, bytes, sensitive, http.RequestAborted), Body, v => v.Revision);
        }
        finally { gate.Exit(); }
    }

    private static async Task<IResult> Download(Guid id, HttpContext http, AccessService access, AttachmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.OpenAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted);
        if (!r.IsOk) return Map(r, _ => new { });
        var a = r.Value!.Attachment;
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(r.Value.Bytes, a.ContentType, a.FileName, enableRangeProcessing: false);      // Content-Disposition: attachment, never inline
    }

    private static async Task<IResult> Rewrap(HttpContext http, AccessService access, AttachmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.RewrapAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new { rewrapped = v.Rewrapped, skipped = v.Skipped, failed = v.Failed, retainedUnderOtherKeys = v.RetainedUnderOtherKeys, oldKeysMayBeRetired = v.OldKeysMayBeRetired });
    }

    private static async Task<IResult> Verify(HttpContext http, AccessService access, AttachmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.VerifyAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new
        {
            clean = v.IsClean, checkedCount = v.Checked, ok = v.Ok, totalSensitive = v.TotalSensitive, totalBytes = v.TotalBytes, missing = v.Missing, hashMismatch = v.HashMismatch,
            keyUnavailable = v.KeyUnavailable, unreadable = v.Unreadable, orphanFiles = v.OrphanFiles, keyIdsInUse = v.KeyIdsInUse,
        });
    }

    private static async Task<IResult> Sweep(HttpContext http, AccessService access, AttachmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Map(await svc.SweepOrphansAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new { removed = v });
    }

    private static async Task<IResult> Remove(Guid id, RemoveRequest? body, HttpContext http, AccessService access, AttachmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Map(await svc.RemoveAsync(actor!.UserId, actor.CompanyId, id, expected, body?.Reason, http.RequestAborted), Body, v => v.Revision);
    }
}
