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
        services.AddSingleton(sp => new AttachmentFileStore(options, sp.GetRequiredService<EncryptionOptions>()));
        services.AddSingleton(sp => new AttachmentService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<AttachmentFileStore>(),
            sp.GetRequiredService<EnvelopeEncryptor>(), options, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AttachmentManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<AttachmentService>()));
        return services;
    }
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
    }

    private static object Body(AttachmentView v) => new
    {
        id = v.Id, subjectKind = v.SubjectKind, subjectId = v.SubjectId, fileName = v.FileName, contentType = v.ContentType, sizeBytes = v.SizeBytes, sha256 = v.Sha256, isSensitive = v.IsSensitive, revision = v.Revision, createdAtUtc = v.CreatedAtUtc,
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

    private static async Task<IResult> Upload(HttpContext http, AccessService access, AttachmentManagementService svc, AttachmentOptions options)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var limit = options.MaxFileBytes + FormOverhead;
        if (http.Request.ContentLength is { } len && len > limit) return CompanyConfigurationEndpoints.Error(413, "file_too_large");
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = limit;
        if (!http.Request.HasFormContentType) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        IFormCollection form;
        try { form = await http.Request.ReadFormAsync(http.RequestAborted); }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException or IOException) { return CompanyConfigurationEndpoints.Error(413, "file_too_large"); }
        var file = form.Files.Count == 1 ? form.Files[0] : null;
        byte[]? bytes = null;
        if (file is not null)
        {
            if (file.Length > options.MaxFileBytes) return CompanyConfigurationEndpoints.Error(413, "file_too_large");
            await using var s = file.OpenReadStream(); using var ms = new MemoryStream();
            await s.CopyToAsync(ms, http.RequestAborted); bytes = ms.ToArray();
        }
        Guid? subjectId = Guid.TryParse(form["subjectId"], out var sid) ? sid : null;
        var sensitive = string.Equals(form["sensitive"], "true", StringComparison.OrdinalIgnoreCase);
        return Map(await svc.UploadAsync(actor!.UserId, actor.CompanyId, form["subjectKind"], subjectId, file?.FileName, file?.ContentType, bytes, sensitive, http.RequestAborted), Body, v => v.Revision);
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
        return Map(await svc.RewrapAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new { rewrapped = v.Rewrapped, skipped = v.Skipped, failed = v.Failed });
    }

    private static async Task<IResult> Remove(Guid id, RemoveRequest? body, HttpContext http, AccessService access, AttachmentManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Map(await svc.RemoveAsync(actor!.UserId, actor.CompanyId, id, expected, body?.Reason, http.RequestAborted), Body, v => v.Revision);
    }
}
