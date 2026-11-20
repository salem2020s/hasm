using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using IT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportMappingTemplate;

namespace Hasm.Infrastructure.Imports;

/// <summary>Business audit events of import mapping templates (company stream). Persisted codes: never renumber.</summary>
public static class ImportTemplateAuditEvents
{
    public const short Saved = 1110;
    public const short Retired = 1111;
}

/// <summary>
/// The CONTENT of a reusable mapping template: field key -> source HEADER NAME (never a column position, so it applies to any file whose headers match) plus an optional constant default, and the
/// number/date options. <see cref="Apply"/> only PRE-FILLS the ordinary mapping of a batch (a header that is missing or ambiguous leaves the field unmapped and is reported): the result still goes
/// through the normal mapping validation, dry run, approval and plan hash, so a template can never skip any of them.
/// </summary>
public static class ImportTemplateContent
{
    public const int MaxFields = 100, MaxHeaderLength = 200;
    private static string Norm(string s) => Regex.Replace(ImportNormalizer.Digits(s.Normalize(NormalizationForm.FormC)).Trim().ToLowerInvariant(), @"[\s_\-./]+", " ");

    /// <summary>Builds the template content from a batch's column-based mapping and its headers. Refused when a mapped header is empty or two headers are the same after normalization (the template could not be re-applied deterministically).</summary>
    public static (JsonObject? Content, List<FieldError> Errors) FromMapping(ImportMapping mapping, IReadOnlyList<string> headers)
    {
        var errors = new List<FieldError>(); var fields = new JsonObject();
        var normalized = headers.Select(Norm).ToList();
        foreach (var (key, map) in mapping.Fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var node = new JsonObject();
            if (map.Column is { } c)
            {
                var header = c >= 0 && c < headers.Count ? headers[c].Trim() : "";
                if (header.Length == 0 || header.Length > MaxHeaderLength) { errors.Add(new($"mapping.fields.{key}.column", "header_unusable")); continue; }
                if (normalized.Count(n => n == normalized[c]) > 1) { errors.Add(new($"mapping.fields.{key}.column", "header_ambiguous")); continue; }
                node["header"] = header;
            }
            if (map.Default is { } d) node["default"] = d;
            if (node.Count == 0) continue;                                                      // a field with neither a source nor a default is simply not part of the template
            fields[key] = node;
        }
        if (fields.Count == 0 && errors.Count == 0) errors.Add(new("mapping.fields", "required"));
        if (errors.Count > 0) return (null, errors);
        return (new JsonObject
        {
            ["fields"] = fields,
            ["options"] = new JsonObject { ["decimalSeparator"] = mapping.Options.DecimalSeparator, ["thousandsSeparator"] = mapping.Options.ThousandsSeparator, ["dateFormat"] = mapping.Options.DateFormat },
        }, errors);
    }

    /// <summary>Validates stored content against the adapter's fields (unknown fields, bad headers, options). Used before a template is applied so a template saved for an older adapter version cannot inject anything.</summary>
    public static List<FieldError> Validate(JsonNode? json, IReadOnlyList<ImportField> fields)
    {
        var errors = new List<FieldError>();
        if (json is not JsonObject root || root["fields"] is not JsonObject f || f.Count == 0 || f.Count > MaxFields) return [new("template", "invalid")];
        var known = fields.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, node) in f)
        {
            if (!known.Contains(key)) { errors.Add(new($"template.fields.{key}", "unknown_field")); continue; }
            if (node is not JsonObject o) { errors.Add(new($"template.fields.{key}", "invalid")); continue; }
            if (o["header"] is { } h && (h is not JsonValue hv || !hv.TryGetValue<string>(out var hs) || string.IsNullOrWhiteSpace(hs) || hs.Length > MaxHeaderLength)) errors.Add(new($"template.fields.{key}.header", "invalid"));
            if (o["default"] is { } d && (d is not JsonValue dv || !dv.TryGetValue<string>(out var ds) || ds.Length > 400)) errors.Add(new($"template.fields.{key}.default", "invalid"));
        }
        var (parsedOptions, optionErrors) = ImportMapping.Parse(new JsonObject { ["fields"] = new JsonObject(), ["options"] = root["options"]?.DeepClone() }, [], 0);
        _ = parsedOptions;
        errors.AddRange(optionErrors.Where(e => e.Field.StartsWith("mapping.options", StringComparison.Ordinal)));
        return errors;
    }

    /// <summary>Resolves the template against the staged headers: a field whose header matches exactly ONE column (after normalization) and whose column is not already taken gets that column; every other field is returned in <c>Unmatched</c> (its default, if any, is kept).</summary>
    public static (JsonObject Mapping, List<string> Unmatched) Apply(JsonObject content, IReadOnlyList<string> headers)
    {
        var normalized = headers.Select(Norm).ToList();
        var taken = new HashSet<int>(); var unmatched = new List<string>(); var fields = new JsonObject();
        foreach (var (key, node) in ((JsonObject)content["fields"]!).OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var o = (JsonObject)node!; int? column = null;
            if (o["header"] is JsonValue hv && hv.TryGetValue<string>(out var header))
            {
                var wanted = Norm(header); var hits = Enumerable.Range(0, normalized.Count).Where(i => normalized[i] == wanted).ToList();
                if (hits.Count == 1 && !taken.Contains(hits[0])) { column = hits[0]; taken.Add(hits[0]); } else unmatched.Add(key);
            }
            var def = o["default"] is JsonValue dv && dv.TryGetValue<string>(out var ds) && !string.IsNullOrWhiteSpace(ds) ? ds.Trim() : null;
            if (column is null && def is null) continue;
            fields[key] = new JsonObject { ["column"] = column, ["default"] = def };
        }
        var opts = content["options"] is JsonObject oo ? (JsonObject)oo.DeepClone() : new JsonObject();
        return (new JsonObject { ["fields"] = fields, ["options"] = opts }, unmatched);
    }
}

public sealed record ImportTemplateView(Guid Id, string Adapter, string Name, int Version, bool IsRetired, JsonNode Content, DateTimeOffset CreatedAtUtc);
public sealed record ImportTemplateApplication(string Name, int Version, JsonObject Mapping, IReadOnlyList<string> Unmatched);

/// <summary>
/// Reusable import mapping templates (Phase 3.9C, OD-9 default: per company and per adapter, versioned, audited; no per-user templates). The only authorized entry point: a template is read with
/// <c>company.imports.view</c> plus the adapter's own view permission and written or applied with <c>company.imports.upload</c> plus the adapter's own upload permission, exactly like the mapping it
/// pre-fills. Versions are append-only; a save names the version it was based on (a stale one is refused, 0 = new); retirement is a new version.
/// </summary>
public sealed class ImportTemplateService(AccessService access, CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, ImportService imports, ImportAdapterRegistry adapters, TimeProvider clock)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Allowed(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);
    private async Task<bool> MayAsync(Guid user, Guid company, string generic, string adapterPermission, CancellationToken ct) => await Allowed(user, company, generic, ct) && await Allowed(user, company, adapterPermission, ct);
    private static IReadOnlyList<string> HeadersOf(ImportBatchView b) => b.Options["headers"] is JsonArray h ? [.. h.Select(x => x?.GetValue<string>() ?? "")] : [];

    private sealed class Abort(ConfigStatus status, string code, long? current = null) : Exception(code) { public ConfigResult<T> To<T>() => ConfigResult<T>.Fail(status, code, current); }

    private static string? CleanName(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) || n.Length > 100 || n.Any(char.IsControl) ? null : n;
    }

    private static ImportTemplateView Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetBoolean(4), JsonNode.Parse(r.GetString(5))!, r.GetFieldValue<DateTimeOffset>(6));
    private const string Cols = $"{IT.Id}, {IT.Adapter}, {IT.Name}, {IT.Version}, {IT.IsRetired}, {IT.Content}, {IT.CreatedAtUtc}";

    /// <summary>The latest version of every template of the adapters the caller may see (optionally of one adapter), retired ones flagged.</summary>
    public async Task<ConfigResult<IReadOnlyList<ImportTemplateView>>> ListAsync(Guid user, Guid company, string? adapterCode, CancellationToken ct = default)
    {
        if (!await Allowed(user, company, PermissionCatalog.Codes.ImportsView, ct)) return Denied<IReadOnlyList<ImportTemplateView>>();
        var visible = new List<string>();
        foreach (var a in adapters.All) if ((adapterCode is null || a.Code == adapterCode) && await Allowed(user, company, a.ViewPermission, ct)) visible.Add(a.Code);
        await using var conn = await resolver.OpenAsync(company, ct);
        await using var q = new NpgsqlCommand($"SELECT {Cols} FROM {IT.Table} t WHERE {IT.Adapter} = ANY(@a) AND {IT.Version} = (SELECT max(x.{IT.Version}) FROM {IT.Table} x WHERE x.{IT.Adapter} = t.{IT.Adapter} AND x.{IT.Name} = t.{IT.Name}) ORDER BY {IT.Adapter}, {IT.Name}", conn);
        q.Parameters.AddWithValue("a", visible.ToArray());
        var rows = new List<ImportTemplateView>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(Map(r));
        return ConfigResult<IReadOnlyList<ImportTemplateView>>.Ok(rows);
    }

    /// <summary>Every version of one template, newest first.</summary>
    public async Task<ConfigResult<IReadOnlyList<ImportTemplateView>>> HistoryAsync(Guid user, Guid company, string adapterCode, string name, CancellationToken ct = default)
    {
        var adapter = adapters.Find(adapterCode);
        if (adapter is null || !await MayAsync(user, company, PermissionCatalog.Codes.ImportsView, adapter.ViewPermission, ct)) return Denied<IReadOnlyList<ImportTemplateView>>();
        await using var conn = await resolver.OpenAsync(company, ct);
        await using var q = new NpgsqlCommand($"SELECT {Cols} FROM {IT.Table} WHERE {IT.Adapter} = @a AND {IT.Name} = @n ORDER BY {IT.Version} DESC", conn);
        q.Parameters.AddWithValue("a", adapterCode); q.Parameters.AddWithValue("n", name);
        var rows = new List<ImportTemplateView>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(Map(r));
        return ConfigResult<IReadOnlyList<ImportTemplateView>>.Ok(rows);
    }

    private async Task<ImportTemplateView?> LatestAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string adapter, string name, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {Cols} FROM {IT.Table} WHERE {IT.Adapter} = @a AND {IT.Name} = @n ORDER BY {IT.Version} DESC LIMIT 1", conn, tx);
        q.Parameters.AddWithValue("a", adapter); q.Parameters.AddWithValue("n", name);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private async Task<ConfigResult<ImportTemplateView>> AppendAsync(Guid actor, Guid company, string adapter, string name, long? expectedVersion, JsonNode? content, bool retire, CancellationToken ct)
    {
        if (expectedVersion is null) return ConfigResult<ImportTemplateView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        try
        {
            var id = Guid.NewGuid();
            await transactions.ExecuteAsync(company, async work =>
            {
                // serialize every writer of ONE template: the version is assigned under this lock
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", work.Connection, work.Transaction)) { lk.Parameters.AddWithValue("k", "importtemplate:" + adapter + "|" + name); await lk.ExecuteNonQueryAsync(ct); }
                var cur = await LatestAsync(work.Connection, work.Transaction, adapter, name, ct);
                var curVersion = cur?.Version ?? 0;
                if (curVersion != expectedVersion) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", curVersion);
                if (retire) { if (cur is null) throw new Abort(ConfigStatus.NotFound, "template_not_found"); if (cur.IsRetired) throw new Abort(ConfigStatus.Conflict, "already_retired"); content = cur.Content; }
                await using var ins = new NpgsqlCommand($"INSERT INTO {IT.Table} ({IT.Id}, {IT.Adapter}, {IT.Name}, {IT.Version}, {IT.IsRetired}, {IT.Content}, {IT.CreatedBy}, {IT.CreatedAtUtc}) VALUES (@id, @a, @n, @v, @ret, @c, @by, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("a", adapter); ins.Parameters.AddWithValue("n", name); ins.Parameters.AddWithValue("v", curVersion + 1); ins.Parameters.AddWithValue("ret", retire);
                ins.Parameters.AddWithValue("c", content!.ToJsonString()); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                await ins.ExecuteNonQueryAsync(ct);
                await work.AppendAuditAsync(new AuditEventData(retire ? ImportTemplateAuditEvents.Retired : ImportTemplateAuditEvents.Saved, ActorUserId: actor, SubjectId: id, Detail: $"import mapping template {(retire ? "retired" : "saved")} adapter={adapter} name={name} v{curVersion + 1}"), ct);
                return true;
            }, ct);
            await using var conn = await resolver.OpenAsync(company, ct);
            return ConfigResult<ImportTemplateView>.Ok((await LatestAsync(conn, null, adapter, name, ct))!);
        }
        catch (Abort a) { return a.To<ImportTemplateView>(); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.RaiseException) { return ConfigResult<ImportTemplateView>.Fail(ConfigStatus.Conflict, "version_conflict"); }
    }

    /// <summary>Saves the CURRENT mapping of a batch as the next version of the named template (0 = a new template).</summary>
    public async Task<ConfigResult<ImportTemplateView>> SaveFromBatchAsync(Guid user, Guid company, Guid batchId, string? name, long? expectedVersion, CancellationToken ct = default)
    {
        var batch = await imports.GetAsync(company, batchId, ct);
        var adapter = batch is null ? null : adapters.Find(batch.AdapterCode);
        if (batch is null || adapter is null) return await Allowed(user, company, PermissionCatalog.Codes.ImportsView, ct) ? ConfigResult<ImportTemplateView>.Fail(ConfigStatus.NotFound, "import_not_found") : Denied<ImportTemplateView>();
        if (!await MayAsync(user, company, PermissionCatalog.Codes.ImportsUpload, adapter.UploadPermission, ct)) return Denied<ImportTemplateView>();
        var clean = CleanName(name);
        if (clean is null) return ConfigResult<ImportTemplateView>.Invalid([new FieldError("name", "name_invalid")]);
        if (batch.Mapping is null) return ConfigResult<ImportTemplateView>.Fail(ConfigStatus.Conflict, "batch_not_mapped");
        var headers = HeadersOf(batch);
        var (mapping, mapErrors) = ImportMapping.Parse(batch.Mapping, adapter.Fields, headers.Count);
        if (mapping is null) return ConfigResult<ImportTemplateView>.Invalid(mapErrors);
        var (content, errors) = ImportTemplateContent.FromMapping(mapping, headers);
        if (content is null) return ConfigResult<ImportTemplateView>.Invalid(errors);
        return await AppendAsync(user, company, adapter.Code, clean, expectedVersion, content, retire: false, ct);
    }

    public async Task<ConfigResult<ImportTemplateView>> RetireAsync(Guid user, Guid company, string adapterCode, string name, long? expectedVersion, CancellationToken ct = default)
    {
        var adapter = adapters.Find(adapterCode);
        if (adapter is null || !await MayAsync(user, company, PermissionCatalog.Codes.ImportsUpload, adapter.UploadPermission, ct)) return Denied<ImportTemplateView>();
        return await AppendAsync(user, company, adapter.Code, name, expectedVersion, null, retire: true, ct);
    }

    /// <summary>Resolves the latest non-retired version of a template against the headers of a staged batch and returns the mapping to PRE-FILL (nothing is written to the batch: the caller submits it through the ordinary, validated mapping step).</summary>
    public async Task<ConfigResult<ImportTemplateApplication>> ApplyAsync(Guid user, Guid company, Guid batchId, string? name, CancellationToken ct = default)
    {
        var batch = await imports.GetAsync(company, batchId, ct);
        var adapter = batch is null ? null : adapters.Find(batch.AdapterCode);
        if (batch is null || adapter is null) return await Allowed(user, company, PermissionCatalog.Codes.ImportsView, ct) ? ConfigResult<ImportTemplateApplication>.Fail(ConfigStatus.NotFound, "import_not_found") : Denied<ImportTemplateApplication>();
        if (!await MayAsync(user, company, PermissionCatalog.Codes.ImportsUpload, adapter.UploadPermission, ct)) return Denied<ImportTemplateApplication>();
        if (batch.Status < ImportStatus.Staged) return ConfigResult<ImportTemplateApplication>.Fail(ConfigStatus.Conflict, "batch_not_staged");
        var clean = CleanName(name);
        if (clean is null) return ConfigResult<ImportTemplateApplication>.Invalid([new FieldError("name", "name_invalid")]);
        await using var conn = await resolver.OpenAsync(company, ct);
        var t = await LatestAsync(conn, null, adapter.Code, clean, ct);
        if (t is null || t.IsRetired) return ConfigResult<ImportTemplateApplication>.Fail(ConfigStatus.NotFound, "template_not_found");
        var errors = ImportTemplateContent.Validate(t.Content, adapter.Fields);
        if (errors.Count > 0) return ConfigResult<ImportTemplateApplication>.Fail(ConfigStatus.Conflict, "template_incompatible");     // saved for different fields: never guessed
        var (mapping, unmatched) = ImportTemplateContent.Apply((JsonObject)t.Content, HeadersOf(batch));
        return ConfigResult<ImportTemplateApplication>.Ok(new ImportTemplateApplication(t.Name, t.Version, mapping, unmatched));
    }
}
