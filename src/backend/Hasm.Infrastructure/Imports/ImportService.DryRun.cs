using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using IB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportBatch;
using IR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportRow;

namespace Hasm.Infrastructure.Imports;

/// <summary>One explicit decision about a conflicting row (<c>Action</c> null clears it).</summary>
public sealed record ResolutionInput(int? RowNo, string? Action);

/// <summary>The result of recomputing a plan against the CURRENT data: <c>Fresh</c> when it equals the plan that was validated (and approved).</summary>
public sealed record PlanCheck(bool Fresh, string CurrentHash, string? ApprovedHash, int Blocking);

/// <summary>Mapping, normalization, validation and the dry run (Phase 3.2D S2). Nothing here writes business data: the adapter analyses inside a read-only snapshot and only staging rows and the batch change.</summary>
public sealed partial class ImportService
{
    private sealed record StagedRow(int RowNo, string?[] Cells, string? Resolution);

    private sealed record RowResult(int RowNo, RowOutcome Outcome, Dictionary<string, string?> Values, List<RowIssue> Issues, IReadOnlyList<string>? Allowed, JsonObject? Operation, string? Explanation, int Sequence, int? DuplicateOf);

    private sealed record PlanComputation(IReadOnlyList<RowResult> Rows, JsonObject Plan, string PlanHash, int Blocking, int Operations);

    private IImportAdapter? AdapterOf(ImportBatchView batch) => adapters.Find(batch.AdapterCode);

    private static IReadOnlyList<string> HeadersOf(ImportBatchView b) => b.Options["headers"] is JsonArray h ? [.. h.Select(x => x?.GetValue<string>() ?? "")] : [];

    // ------------------------------------------------------------------------------------------------------------------------- mapping

    /// <summary>A mapping suggestion: the mapping a previous batch of the same adapter used for the same headers, else a match of headers to field names and aliases.</summary>
    public async Task<ConfigResult<JsonObject>> SuggestMappingAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        var batch = await GetAsync(companyId, id, ct);
        if (batch is null) return ConfigResult<JsonObject>.Fail(ConfigStatus.NotFound, "import_not_found");
        var adapter = AdapterOf(batch);
        if (adapter is null || batch.Status < ImportStatus.Staged) return ConfigResult<JsonObject>.Fail(ConfigStatus.Conflict, "batch_not_staged");
        var headers = HeadersOf(batch);
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand(
            $"SELECT {IB.Mapping} FROM {IB.Table} WHERE {IB.AdapterCode} = @a AND {IB.Id} <> @id AND {IB.Mapping} IS NOT NULL AND ({IB.Options} -> 'headers') = (@h::jsonb) ORDER BY {IB.CreatedAtUtc} DESC LIMIT 1", conn);
        q.Parameters.AddWithValue("a", batch.AdapterCode); q.Parameters.AddWithValue("id", id);
        q.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Text) { Value = JsonSerializer.Serialize(headers) });
        if (await q.ExecuteScalarAsync(ct) is string prev && JsonNode.Parse(prev) is JsonObject previous && ImportMapping.Parse(previous, adapter.Fields, headers.Count).Mapping is not null)
            return ConfigResult<JsonObject>.Ok(previous);
        return ConfigResult<JsonObject>.Ok(ImportMapping.Suggest(headers, adapter.Fields));
    }

    /// <summary>
    /// Stores the validated mapping. The batch becomes Mapped: earlier normalization, validation, resolutions, plan, submission and approval were built on the old mapping and are voided (rows reset), and
    /// the dry run must be run again.
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> SetMappingAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, JsonNode? mapping, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var batch = await GetAsync(companyId, id, ct);
        if (batch is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.NotFound, "import_not_found");
        if (batch.Revision != expectedRevision.Value) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionFailed, "revision_conflict", batch.Revision);
        var adapter = AdapterOf(batch);
        if (adapter is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "unknown_adapter");
        if (batch.Status is not (ImportStatus.Staged or ImportStatus.Mapped or ImportStatus.Validated or ImportStatus.Failed) || !batch.RowsAvailable) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "batch_not_mappable");
        var (parsed, errors) = ImportMapping.Parse(mapping, adapter.Fields, HeadersOf(batch).Count);
        if (parsed is null) return ConfigResult<ImportBatchView>.Invalid(errors);
        var json = parsed.ToJson();
        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Status is not (ImportStatus.Staged or ImportStatus.Mapped or ImportStatus.Validated or ImportStatus.Failed)) throw new Abort(ConfigStatus.Conflict, "batch_not_mappable");
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var up = new NpgsqlCommand(
                $"UPDATE {IB.Table} SET {IB.Status} = 3, {IB.Mapping} = @m, {IB.PlanHash} = NULL, {IB.Plan} = NULL, {IB.SubmittedBy} = NULL, {IB.SubmittedAtUtc} = NULL, {IB.ApprovalRevision} = NULL, {IB.Result} = NULL, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            {
                up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("t", now);
                up.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Jsonb) { Value = json.ToJsonString() });
                await up.ExecuteNonQueryAsync(ct);
            }
            if (cur.Status != ImportStatus.Staged)
                await using (var reset = new NpgsqlCommand($"UPDATE {IR.Table} SET {IR.Status} = 1, {IR.Normalized} = NULL, {IR.Errors} = NULL, {IR.Resolution} = NULL, {IR.Analysis} = NULL, {IR.Sequence} = 0 WHERE {IR.BatchId} = @id", work.Connection, work.Transaction))
                { reset.Parameters.AddWithValue("id", id); await reset.ExecuteNonQueryAsync(ct); }
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.Mapped, view.Revision, actor, new JsonObject { ["mappingHash"] = Sha(Encoding.UTF8.GetBytes(ImportNormalizer.CanonicalJson(json))), ["fields"] = parsed.Fields.Count }, ct);
            return view;
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------------------- analysis

    private static string CanonicalMappingHash(JsonNode? mapping) => Sha(Encoding.UTF8.GetBytes(ImportNormalizer.CanonicalJson(mapping)));

    /// <summary>Normalizes, detects duplicates inside the file, asks the adapter (read-only snapshot) and assembles the plan with its canonical hash. Writes nothing.</summary>
    private async Task<PlanComputation> ComputeAsync(Guid companyId, ImportBatchView batch, IImportAdapter adapter, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await ro.ExecuteNonQueryAsync(ct);
        var plan = await ComputeInAsync(companyId, batch, adapter, conn, tx, ct);
        await tx.CommitAsync(ct);
        return plan;
    }

    /// <summary>The same computation inside a transaction the caller owns (the application transaction re-validates with it, under the adapter's own locks).</summary>
    private async Task<PlanComputation> ComputeInAsync(Guid companyId, ImportBatchView batch, IImportAdapter adapter, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var headers = HeadersOf(batch);
        var (mapping, mappingErrors) = ImportMapping.Parse(batch.Mapping, adapter.Fields, headers.Count);
        if (mapping is null) throw new Abort(ConfigStatus.Conflict, "mapping_invalid", fields: mappingErrors);
        var staged = new List<StagedRow>();
        await using (var q = new NpgsqlCommand($"SELECT {IR.RowNo}, {IR.Raw}, {IR.Resolution} ->> 'action' FROM {IR.Table} WHERE {IR.BatchId} = @b ORDER BY {IR.RowNo}", conn, tx))
        {
            q.Parameters.AddWithValue("b", batch.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) staged.Add(new StagedRow(r.GetInt32(0), [.. JsonNode.Parse(r.GetString(1))!.AsArray().Select(c => c?.GetValue<string>())], r.IsDBNull(2) ? null : r.GetString(2)));
        }

        // 1) normalization by field type
        var results = new Dictionary<int, RowResult>();
        var normalized = new List<(StagedRow Row, Dictionary<string, string?> Values)>();
        foreach (var s in staged)
        {
            var (values, issues) = ImportNormalizer.NormalizeRow(adapter.Fields, mapping, s.Cells);
            if (issues.Count > 0) results[s.RowNo] = new RowResult(s.RowNo, RowOutcome.Invalid, values, issues, null, null, null, 0, null);
            else normalized.Add((s, values));
        }

        // 2) duplicates inside the file (by the key fields): an identical repeat is a resolvable conflict (skip it), a differing one is a blocking error
        var keyFields = adapter.Fields.Where(f => f.IsKey).Select(f => f.Key).ToList();
        var duplicateOf = new Dictionary<int, int>(); var identical = new HashSet<int>();
        if (keyFields.Count > 0)
        {
            var firsts = new Dictionary<string, (int RowNo, Dictionary<string, string?> Values)>(StringComparer.Ordinal);
            foreach (var (row, values) in normalized)
            {
                if (keyFields.Any(k => values[k] is null)) continue;
                var key = string.Join('\u001F', keyFields.Select(k => values[k]));
                if (!firsts.TryGetValue(key, out var first)) { firsts[key] = (row.RowNo, values); continue; }
                duplicateOf[row.RowNo] = first.RowNo;
                if (adapter.Fields.All(f => values[f.Key] == first.Values[f.Key])) identical.Add(row.RowNo);
            }
        }
        var forAdapter = new List<NormalizedRow>();
        foreach (var (row, values) in normalized)
        {
            if (duplicateOf.TryGetValue(row.RowNo, out var first))
            {
                if (identical.Contains(row.RowNo))
                    results[row.RowNo] = row.Resolution == ImportResolutions.Skip
                        ? new RowResult(row.RowNo, RowOutcome.Resolved, values, [], [ImportResolutions.Skip], null, $"duplicate_in_file_skipped:{first}", 0, first)
                        : new RowResult(row.RowNo, RowOutcome.Conflict, values, [new RowIssue("", "duplicate_in_file", first.ToString(System.Globalization.CultureInfo.InvariantCulture))], [ImportResolutions.Skip], null, $"duplicate_in_file:{first}", 0, first);
                else results[row.RowNo] = new RowResult(row.RowNo, RowOutcome.Invalid, values, [new RowIssue("", "conflicting_duplicate_in_file", first.ToString(System.Globalization.CultureInfo.InvariantCulture))], null, null, null, 0, first);
            }
            else forAdapter.Add(new NormalizedRow(row.RowNo, values, row.Resolution));
        }

        // 3) the adapter's business rules against the current data (read-only snapshot)
        var analysis = forAdapter.Count == 0 ? new AnalysisResult([], "") : await adapter.AnalyzeAsync(new ImportAnalysisContext(companyId, conn, tx), forAdapter, ct);
        var answers = analysis.Rows.GroupBy(a => a.RowNo).ToDictionary(g => g.Key, g => g.First());
        foreach (var row in forAdapter)
        {
            var values = row.Values;
            if (!answers.TryGetValue(row.RowNo, out var a)) { results[row.RowNo] = new RowResult(row.RowNo, RowOutcome.Invalid, values, [new RowIssue("", "adapter_gave_no_answer")], null, null, null, 0, null); continue; }
            var allowed = (a.AllowedResolutions ?? []).Where(x => adapter.Resolutions.Contains(x)).Distinct().ToList();
            var issues = a.Issues?.ToList() ?? [];
            var outcome = a.Outcome; var op = a.Operation; var explanation = a.Explanation;
            if (outcome == RowOutcome.Valid && op is null) { outcome = RowOutcome.Invalid; issues.Add(new RowIssue("", "adapter_gave_no_operation")); }
            if (outcome == RowOutcome.Conflict && row.Resolution is { } chosen)
            {
                if (chosen == ImportResolutions.Skip && allowed.Contains(ImportResolutions.Skip)) { outcome = RowOutcome.Resolved; op = null; }
                else issues.Add(new RowIssue("", "resolution_not_applicable", chosen));
            }
            if (outcome == RowOutcome.Conflict && allowed.Count == 0) { outcome = RowOutcome.Invalid; issues.Add(new RowIssue("", "conflict_without_resolution")); }
            if (outcome is not (RowOutcome.Valid or RowOutcome.Resolved)) op = null;                // only rows that will be applied carry an operation
            results[row.RowNo] = new RowResult(row.RowNo, outcome, values, issues, allowed.Count > 0 ? allowed : null, op, explanation, a.Sequence, null);
        }
        foreach (var s in staged) if (!results.ContainsKey(s.RowNo)) results[s.RowNo] = new RowResult(s.RowNo, RowOutcome.Invalid, [], [new RowIssue("", "row_not_analysed")], null, null, null, 0, null);

        // 4) the plan: counts, operations in a fixed order, resolutions, baseline, reconciliation and one canonical hash
        var ordered = results.Values.OrderBy(r => r.RowNo).ToList();
        int Count(RowOutcome o) => ordered.Count(r => r.Outcome == o);
        var ops = ordered.Where(r => r.Operation is not null && r.Outcome is RowOutcome.Valid or RowOutcome.Resolved).OrderBy(r => r.Sequence).ThenBy(r => r.RowNo).ToList();
        var blocking = Count(RowOutcome.Invalid) + Count(RowOutcome.Conflict);
        var summary = new JsonObject
        {
            ["total"] = ordered.Count, ["valid"] = Count(RowOutcome.Valid), ["exact"] = Count(RowOutcome.Exact), ["invalid"] = Count(RowOutcome.Invalid), ["conflict"] = Count(RowOutcome.Conflict),
            ["resolved"] = Count(RowOutcome.Resolved), ["operations"] = ops.Count, ["blocking"] = blocking,
        };
        var recon = new JsonArray([.. (analysis.Reconciliation ?? []).OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => (JsonNode)new JsonObject { ["key"] = l.Key, ["value"] = l.Value })]);
        var opsText = new StringBuilder();
        foreach (var o in ops) opsText.Append(o.Sequence).Append('|').Append(o.RowNo).Append('|').Append(ImportNormalizer.CanonicalJson(o.Operation)).Append('\n');
        var resolutionsText = string.Join('\n', staged.Where(s => s.Resolution is not null).Select(s => $"{s.RowNo}={s.Resolution}"));
        var canonical = $"hasm-import-plan/v1\n{adapter.Code}|{adapter.Version}\n{batch.RowsHash}\n{CanonicalMappingHash(batch.Mapping)}\n{analysis.Baseline}\n{resolutionsText}\n{ImportNormalizer.CanonicalJson(summary)}\n{ImportNormalizer.CanonicalJson(recon)}\n{opsText}";
        var planHash = Sha(Encoding.UTF8.GetBytes(canonical));
        var plan = new JsonObject
        {
            ["v"] = 1, ["adapter"] = adapter.Code, ["adapterVersion"] = adapter.Version, ["rowsHash"] = batch.RowsHash, ["mappingHash"] = CanonicalMappingHash(batch.Mapping), ["baseline"] = analysis.Baseline,
            ["summary"] = summary, ["reconciliation"] = recon, ["canApply"] = blocking == 0 && ops.Count > 0, ["operationsHash"] = Sha(Encoding.UTF8.GetBytes(opsText.ToString())),
        };
        return new PlanComputation(ordered, plan, planHash, blocking, ops.Count);
    }

    // ------------------------------------------------------------------------------------------------------------------------- dry run

    /// <summary>
    /// The dry run: normalizes every row, detects duplicates and conflicts, lets the adapter validate against the current data, writes the row results and the plan (with its canonical hash and the
    /// reconciliation) and moves the batch to Validated. Nothing business-side is written. Any earlier submission or approval is void (the plan changed).
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> DryRunAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var batch = await GetAsync(companyId, id, ct);
        if (batch is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.NotFound, "import_not_found");
        if (batch.Revision != expectedRevision.Value) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionFailed, "revision_conflict", batch.Revision);
        if (batch.Status is not (ImportStatus.Mapped or ImportStatus.Validated or ImportStatus.Failed) || batch.Mapping is null || !batch.RowsAvailable) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "batch_not_validatable");
        var adapter = AdapterOf(batch);
        if (adapter is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "unknown_adapter");
        if (adapter.Version != batch.AdapterVersion) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "adapter_version_changed");
        PlanComputation plan;
        try { plan = await ComputeAsync(companyId, batch, adapter, ct); }
        catch (Abort abort) { return abort.To<ImportBatchView>(); }

        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            for (var start = 0; start < plan.Rows.Count; start += 1000)
            {
                var chunk = plan.Rows.Skip(start).Take(1000).ToList();
                await using var up = new NpgsqlCommand(
                    $"UPDATE {IR.Table} r SET {IR.Status} = t.s, {IR.Normalized} = t.n::jsonb, {IR.Errors} = t.e::jsonb, {IR.Analysis} = t.a::jsonb, {IR.Sequence} = t.q " +
                    $"FROM unnest(@nos, @st, @norm, @err, @ana, @seq) AS t(no, s, n, e, a, q) WHERE r.{IR.BatchId} = @b AND r.{IR.RowNo} = t.no", work.Connection, work.Transaction);
                up.Parameters.AddWithValue("b", id);
                up.Parameters.Add(new NpgsqlParameter("nos", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = chunk.Select(r => r.RowNo).ToArray() });
                up.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = chunk.Select(r => (short)r.Outcome).ToArray() });
                up.Parameters.Add(new NpgsqlParameter("norm", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = chunk.Select(r => (string?)new JsonObject(r.Values.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)(v.Value is null ? null : JsonValue.Create(v.Value))))).ToJsonString()).ToArray() });
                up.Parameters.Add(new NpgsqlParameter("err", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = chunk.Select(r => r.Issues.Count == 0 ? null : new JsonArray([.. r.Issues.Select(i => (JsonNode)new JsonObject { ["field"] = i.Field, ["code"] = i.Code, ["detail"] = i.Detail })]).ToJsonString()).ToArray() });
                up.Parameters.Add(new NpgsqlParameter("ana", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = chunk.Select(r => (string?)new JsonObject { ["operation"] = r.Operation?.DeepClone(), ["explanation"] = r.Explanation, ["allowed"] = r.Allowed is null ? null : new JsonArray([.. r.Allowed.Select(a => (JsonNode)JsonValue.Create(a)!)]), ["duplicateOf"] = r.DuplicateOf }.ToJsonString()).ToArray() });
                up.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = chunk.Select(r => r.Sequence).ToArray() });
                await up.ExecuteNonQueryAsync(ct);
            }
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var up = new NpgsqlCommand(
                $"UPDATE {IB.Table} SET {IB.Status} = 4, {IB.PlanHash} = @h, {IB.Plan} = @p, {IB.SubmittedBy} = NULL, {IB.SubmittedAtUtc} = NULL, {IB.ApprovalRevision} = NULL, {IB.Result} = NULL, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            {
                up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("h", plan.PlanHash); up.Parameters.AddWithValue("t", now);
                up.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = plan.Plan.ToJsonString() });
                await up.ExecuteNonQueryAsync(ct);
            }
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.Validated, view.Revision, actor, new JsonObject { ["planHash"] = plan.PlanHash, ["summary"] = plan.Plan["summary"]!.DeepClone() }, ct);
            return view;
        }, ct);
    }

    /// <summary>Recomputes the plan against the data as it is NOW (no writes) and compares it with the validated plan: a changed ledger, chart or any other dependency makes it stale.</summary>
    public async Task<ConfigResult<PlanCheck>> CheckPlanAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        var batch = await GetAsync(companyId, id, ct);
        if (batch is null) return ConfigResult<PlanCheck>.Fail(ConfigStatus.NotFound, "import_not_found");
        var adapter = AdapterOf(batch);
        if (adapter is null || batch.PlanHash is null || batch.Mapping is null) return ConfigResult<PlanCheck>.Fail(ConfigStatus.Conflict, "batch_not_validated");
        try
        {
            var plan = await ComputeAsync(companyId, batch, adapter, ct);
            return ConfigResult<PlanCheck>.Ok(new PlanCheck(plan.PlanHash == batch.PlanHash, plan.PlanHash, batch.PlanHash, plan.Blocking));
        }
        catch (Abort abort) { return abort.To<PlanCheck>(); }
    }

    // ------------------------------------------------------------------------------------------------------------------------- resolutions

    /// <summary>
    /// Records explicit per-row decisions for conflicting rows (an action the row's analysis allows, or null to clear). Every change returns the batch to Mapped: the dry run and the plan hash are recomputed
    /// and any submission or approval is void. Each decision is in the history.
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> SetResolutionsAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, IReadOnlyList<ResolutionInput>? resolutions, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (resolutions is null || resolutions.Count == 0 || resolutions.Count > 5000) return ConfigResult<ImportBatchView>.Invalid([new("resolutions", resolutions is null || resolutions.Count == 0 ? "required" : "too_many")]);
        if (resolutions.Any(r => r.RowNo is null or < 1)) return ConfigResult<ImportBatchView>.Invalid([new("resolutions", "invalid_row")]);
        if (resolutions.Select(r => r.RowNo).Distinct().Count() != resolutions.Count) return ConfigResult<ImportBatchView>.Invalid([new("resolutions", "duplicate_row")]);
        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Status != ImportStatus.Validated) throw new Abort(ConfigStatus.Conflict, "batch_not_validated");
            var errors = new List<FieldError>();
            var current = new Dictionary<int, (short Status, string[] Allowed)>();
            await using (var q = new NpgsqlCommand($"SELECT {IR.RowNo}, {IR.Status}, {IR.Analysis} -> 'allowed' FROM {IR.Table} WHERE {IR.BatchId} = @b AND {IR.RowNo} = ANY(@nos)", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("b", id);
                q.Parameters.Add(new NpgsqlParameter("nos", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = resolutions.Select(r => r.RowNo!.Value).ToArray() });
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) current[r.GetInt32(0)] = (r.GetInt16(1), !r.IsDBNull(2) && JsonNode.Parse(r.GetString(2)) is JsonArray allowedList ? allowedList.Select(x => x!.GetValue<string>()).ToArray() : []);
            }
            foreach (var res in resolutions)
            {
                if (!current.TryGetValue(res.RowNo!.Value, out var row)) { errors.Add(new($"resolutions[{res.RowNo}]", "row_not_found")); continue; }
                if (row.Status is not ((short)RowOutcome.Conflict or (short)RowOutcome.Resolved)) { errors.Add(new($"resolutions[{res.RowNo}]", "row_not_in_conflict")); continue; }
                if (res.Action is { } a && !row.Allowed.Contains(a)) errors.Add(new($"resolutions[{res.RowNo}]", "resolution_not_allowed"));
            }
            if (errors.Count > 0) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: errors);
            foreach (var res in resolutions)
            {
                await using var up = new NpgsqlCommand($"UPDATE {IR.Table} SET {IR.Resolution} = @r WHERE {IR.BatchId} = @b AND {IR.RowNo} = @n", work.Connection, work.Transaction);
                up.Parameters.AddWithValue("b", id); up.Parameters.AddWithValue("n", res.RowNo!.Value);
                up.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = res.Action is null ? DBNull.Value : new JsonObject { ["action"] = res.Action, ["by"] = actor.ToString() }.ToJsonString() });
                await up.ExecuteNonQueryAsync(ct);
            }
            await using (var b = new NpgsqlCommand(
                $"UPDATE {IB.Table} SET {IB.Status} = 3, {IB.PlanHash} = NULL, {IB.Plan} = NULL, {IB.SubmittedBy} = NULL, {IB.SubmittedAtUtc} = NULL, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            { b.Parameters.AddWithValue("id", id); b.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime); await b.ExecuteNonQueryAsync(ct); }
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.ResolutionChanged, view.Revision, actor, new JsonObject
            {
                ["count"] = resolutions.Count, ["decisions"] = new JsonArray([.. resolutions.Take(100).Select(r => (JsonNode)new JsonObject { ["row"] = r.RowNo, ["action"] = r.Action })]),
            }, ct);
            return view;
        }, ct);
    }
}
