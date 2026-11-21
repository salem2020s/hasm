using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Imports;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/imports: the generic import framework (Phase 3.2D). Company from the server-side session only; every call is authorized inside <see cref="ImportManagementService"/> (the generic
/// import permission AND the adapter's business permission); no-store responses; money-free generic payloads only. The upload body is the raw file (never multipart, never a path): the
/// server validates, scans and encrypts it before it is stored, and there is NO download route for originals.
/// </summary>
public static class ImportEndpoints
{
    private sealed class BodyLimit(long max) : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => max; }

    public static void MapImportEndpoints(this WebApplication app)
    {
        var limits = ImportOptions.FromConfiguration(app.Configuration);
        var g = app.MapGroup("/api/v1/company/imports").RequireAuthorization();
        g.MapGet("/adapters", Adapters);
        g.MapGet("/templates", ListTemplates);
        g.MapGet("/templates/{adapter}/{name}/history", TemplateHistory);
        g.MapPost("/templates/{adapter}/{name}/retire", RetireTemplate).WithMetadata(new BodyLimit(4 * 1024));
        g.MapPost("/{id:guid}/mapping/template", SaveTemplate).WithMetadata(new BodyLimit(4 * 1024));
        g.MapPost("/{id:guid}/mapping/apply-template", ApplyTemplate).WithMetadata(new BodyLimit(4 * 1024));
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", PutSettings).WithMetadata(new BodyLimit(4 * 1024));
        g.MapPost("/maintenance/run", Maintain);
        g.MapPost("/maintenance/rewrap", Rewrap);
        g.MapGet("/", List);
        g.MapPost("/", Upload).WithMetadata(new BodyLimit(limits.MaxFileBytes + 64 * 1024));
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/{id:guid}/stage", Stage).WithMetadata(new BodyLimit(4 * 1024));
        g.MapPost("/{id:guid}/cancel", Cancel);
        g.MapPost("/{id:guid}/renew", Renew);
        g.MapGet("/{id:guid}/rows", Rows);
        g.MapGet("/{id:guid}/events", Events);
        g.MapGet("/{id:guid}/fields", Fields);
        g.MapGet("/{id:guid}/mapping/suggestion", SuggestMapping);
        g.MapPut("/{id:guid}/mapping", SetMapping).WithMetadata(new BodyLimit(64 * 1024));
        g.MapPost("/{id:guid}/dry-run", DryRun);
        g.MapPost("/{id:guid}/resolutions", SetResolutions).WithMetadata(new BodyLimit(512 * 1024));
        g.MapGet("/{id:guid}/plan-check", PlanCheck);
        g.MapPost("/{id:guid}/submit", Submit);
        g.MapPost("/{id:guid}/withdraw", Withdraw);
        g.MapPost("/{id:guid}/approve", Approve).WithMetadata(new BodyLimit(4 * 1024));
        g.MapPost("/{id:guid}/reject", Reject).WithMetadata(new BodyLimit(4 * 1024));
        g.MapPost("/{id:guid}/apply", Apply);
        g.MapGet("/{id:guid}/decisions", Decisions);
    }

    private static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    internal static object BatchBody(ImportBatchView b) => new
    {
        id = b.Id, adapter = b.AdapterCode, adapterVersion = b.AdapterVersion, status = ImportStatusRules.Name(b.Status), fileName = b.FileName, fileKind = b.FileKind == ImportFileKind.Csv ? "csv" : "xlsx",
        fileSize = b.FileSize, fileSha256 = b.FileSha256, keyId = b.KeyId, options = b.Options, sheets = b.Sheets.Select(s => new { index = s.Index, name = s.Name, rows = s.Rows, columns = s.Columns, hidden = s.Hidden }),
        rowCount = b.RowCount, rowsHash = b.RowsHash, planHash = b.PlanHash, revision = b.Revision, createdBy = b.CreatedBy, createdAtUtc = Iso(b.CreatedAtUtc), updatedAtUtc = Iso(b.UpdatedAtUtc),
        submittedBy = b.SubmittedBy, submittedAtUtc = b.SubmittedAtUtc is { } s ? Iso(s) : null, expiresAtUtc = Iso(b.ExpiresAtUtc), terminalAtUtc = b.TerminalAtUtc is { } t ? Iso(t) : null,
        originalAvailable = b.OriginalAvailable, rowsAvailable = b.RowsAvailable, purgeAfterUtc = b.PurgeAfterUtc is { } p ? Iso(p) : null, mapping = b.Mapping, plan = b.Plan, result = b.Result,
    };

    private static IResult Batch(Hasm.Infrastructure.CompanyConfiguration.ConfigResult<ImportBatchView> r) => CompanyConfigurationEndpoints.Map(r, BatchBody, v => v.Revision);

    private static async Task<IResult> Adapters(HttpContext http, AccessService access, ImportManagementService svc, ImportOptions options)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.AdaptersAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new
        {
            adapters = v.Select(a => new { code = a.Adapter.Code, version = a.Adapter.Version, nameAr = a.Adapter.NameAr, nameEn = a.Adapter.NameEn, canUpload = a.CanUpload }),
            limits = new { maxFileBytes = options.MaxFileBytes, maxRows = options.MaxRows, maxColumns = options.MaxColumns, formats = new[] { "xlsx", "csv" }, encodings = new[] { "utf-8", "utf-16", "windows-1256" } },
        });
    }

    private static async Task<IResult> List(string? status, string? adapter, int? limit, int? offset, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        ImportStatus? st = null;
        if (!string.IsNullOrEmpty(status)) { st = Enum.GetValues<ImportStatus>().Cast<ImportStatus?>().FirstOrDefault(x => ImportStatusRules.Name(x!.Value) == status); if (st is null) return CompanyConfigurationEndpoints.Error(400, "invalid_status"); }
        return CompanyConfigurationEndpoints.Map(await svc.ListAsync(actor!.UserId, actor.CompanyId, new ImportListFilter(st, adapter, limit ?? 50, offset ?? 0), http.RequestAborted), v => new { imports = v.Select(BatchBody) });
    }

    private static async Task<IResult> Upload(string? adapter, string? fileName, HttpContext http, AccessService access, ImportManagementService svc, ImportOptions options)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (http.Request.ContentLength is { } len && len > options.MaxFileBytes) return CompanyConfigurationEndpoints.Error(413, "file_too_large");
        using var ms = new MemoryStream();
        var buffer = new byte[81920]; int n;
        while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > options.MaxFileBytes) return CompanyConfigurationEndpoints.Error(413, "file_too_large");
        }
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        var r = await svc.UploadAsync(actor!.UserId, actor.CompanyId, adapter, fileName, ms.ToArray(), string.IsNullOrEmpty(key) ? null : key, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, BatchBody);
        http.Response.Headers.ETag = $"\"{r.Value!.Revision}\"";
        return Results.Json(BatchBody(r.Value!), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Batch(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted));
    }

    private static async Task<IResult> Stage(Guid id, StageInput? body, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.StageAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted));
    }

    private static async Task<IResult> Cancel(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.CancelAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted));
    }

    private static async Task<IResult> Renew(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.RenewAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted));
    }

    private static async Task<IResult> Rows(Guid id, int? offset, int? limit, short? status, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.RowsAsync(actor!.UserId, actor.CompanyId, id, offset ?? 0, limit ?? 100, status, http.RequestAborted), p => new
        {
            headers = p.Headers, total = p.Total, offset = p.Offset, rows = p.Rows.Select(r => new { rowNo = r.RowNo, cells = r.Cells, status = r.Status, normalized = r.Normalized, issues = r.Issues, resolution = r.Resolution, operation = r.Operation, explanation = r.Explanation, allowedResolutions = r.AllowedResolutions }),
        });
    }

    private static async Task<IResult> Events(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.EventsAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), v => new
        {
            events = v.Select(e => new { id = e.Id, kind = e.Kind.ToString().ToLowerInvariant(), revision = e.Revision, actorUserId = e.ActorUserId, atUtc = Iso(e.AtUtc), detail = e.Detail }),
        });
    }

    private static async Task<IResult> Fields(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.FieldsAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), v => new
        {
            resolutions = v.Resolutions,
            fields = v.Fields.Select(f => new
            {
                key = f.Key, nameAr = f.NameAr, nameEn = f.NameEn, type = f.Type.ToString().ToLowerInvariant(), required = f.Required, maxLength = f.MaxLength, isKey = f.IsKey,
                values = f.EnumValues?.Select(e => new { value = e.Key, aliases = e.Value }),
            }),
            options = new { decimalSeparators = MappingOptions.Decimals, thousandsSeparators = MappingOptions.Thousands, dateFormats = MappingOptions.DateFormats },
        });
    }

    public sealed record TemplateSave(string? Name, long? ExpectedVersion);
    public sealed record TemplateName(string? Name);
    public sealed record TemplateRetire(long? ExpectedVersion);

    private static object TemplateBody(ImportTemplateView t) => new { id = t.Id, adapter = t.Adapter, name = t.Name, version = t.Version, isRetired = t.IsRetired, content = t.Content, createdAtUtc = t.CreatedAtUtc };

    private static async Task<IResult> ListTemplates(string? adapter, HttpContext http, AccessService access, ImportTemplateService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ListAsync(actor!.UserId, actor.CompanyId, adapter, http.RequestAborted), v => new { templates = v.Select(TemplateBody) });
    }

    private static async Task<IResult> TemplateHistory(string adapter, string name, HttpContext http, AccessService access, ImportTemplateService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.HistoryAsync(actor!.UserId, actor.CompanyId, adapter, name, http.RequestAborted), v => new { versions = v.Select(TemplateBody) });
    }

    private static async Task<IResult> SaveTemplate(Guid id, TemplateSave? body, HttpContext http, AccessService access, ImportTemplateService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.SaveFromBatchAsync(actor!.UserId, actor.CompanyId, id, body?.Name, body?.ExpectedVersion, http.RequestAborted), TemplateBody);
    }

    private static async Task<IResult> ApplyTemplate(Guid id, TemplateName? body, HttpContext http, AccessService access, ImportTemplateService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ApplyAsync(actor!.UserId, actor.CompanyId, id, body?.Name, http.RequestAborted), v => new { name = v.Name, version = v.Version, mapping = v.Mapping, unmatched = v.Unmatched });
    }

    private static async Task<IResult> RetireTemplate(string adapter, string name, TemplateRetire? body, HttpContext http, AccessService access, ImportTemplateService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.RetireAsync(actor!.UserId, actor.CompanyId, adapter, name, body?.ExpectedVersion, http.RequestAborted), TemplateBody);
    }

    private static async Task<IResult> SuggestMapping(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.SuggestMappingAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), v => new { mapping = v });
    }

    private static async Task<IResult> SetMapping(Guid id, System.Text.Json.Nodes.JsonNode? body, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.SetMappingAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted));
    }

    private static async Task<IResult> DryRun(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.DryRunAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted));
    }

    public sealed record ResolutionsBody(IReadOnlyList<ResolutionInput>? Resolutions);

    private static async Task<IResult> SetResolutions(Guid id, ResolutionsBody? body, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.SetResolutionsAsync(actor!.UserId, actor.CompanyId, id, expected, body?.Resolutions, http.RequestAborted));
    }

    private static async Task<IResult> PlanCheck(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.CheckPlanAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), c => new { fresh = c.Fresh, currentPlanHash = c.CurrentHash, approvedPlanHash = c.ApprovedHash, blocking = c.Blocking });
    }

    private static async Task<IResult> Submit(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.SubmitAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted));
    }

    private static async Task<IResult> Withdraw(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.WithdrawAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted));
    }

    public sealed record DecisionBody(string? Comment);

    private static async Task<IResult> Approve(Guid id, DecisionBody? body, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.DecideAsync(actor!.UserId, actor.CompanyId, id, expected, true, body?.Comment, http.RequestAborted));
    }

    private static async Task<IResult> Reject(Guid id, DecisionBody? body, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.DecideAsync(actor!.UserId, actor.CompanyId, id, expected, false, body?.Comment, http.RequestAborted));
    }

    private static async Task<IResult> Apply(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Batch(await svc.ApplyAsync(actor!.UserId, actor.CompanyId, id, expected, http.RequestAborted));
    }

    private static async Task<IResult> Decisions(Guid id, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.DecisionsAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), v => new
        {
            decisions = v.Select(d => new { approver = d.Approver, approved = d.Approved, revision = d.DocumentRevision, atUtc = Iso(d.DecidedAtUtc), comment = d.Comment }),
        });
    }

    private static object SettingsBody(ImportSettingsView s) => new { retentionDays = s.RetentionDays, pendingExpiryDays = s.PendingExpiryDays, revision = s.Revision, configured = s.Configured };

    private static async Task<IResult> GetSettings(HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.GetSettingsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return new CompanyConfigurationEndpoints.EtagResult(r.Value!.Revision, new { settings = SettingsBody(r.Value), can = new { configure = eff.Has(PermissionCatalog.Codes.ImportsConfigure, ScopeTarget.Company) } });
    }

    private static async Task<IResult> PutSettings(ImportSettingsInput? body, HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.PutSettingsAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted), SettingsBody, v => v.Revision);
    }

    private static object MaintenanceBody(ImportMaintenanceResult m) => new { expired = m.Expired, purged = m.Purged, rewrapped = m.Rewrapped };

    private static async Task<IResult> Maintain(HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.MaintainAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), MaintenanceBody);
    }

    private static async Task<IResult> Rewrap(HttpContext http, AccessService access, ImportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.RewrapAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), RewrapBody);
    }

    // additive (D-180): the earlier fields stay; the retirability signal mirrors the attachments' rewrap response
    private static object RewrapBody(ImportRewrapOutcome o) => new
    {
        expired = 0, purged = 0, rewrapped = o.Rewrapped, skipped = o.Skipped, failed = o.Failed, remainingUnderOtherKeys = o.Usage.UnderOtherKeys, unknownKeys = o.Usage.Unknown,
        keyIdsInUse = o.Usage.KeyIdsInUse, oldKeysMayBeRetired = o.OldKeysMayBeRetired,
    };
}
