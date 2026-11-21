using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Npgsql;
using NpgsqlTypes;
using IB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportBatch;
using IR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportRow;
using IMS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportSettings;
using IV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportEvent;
using PH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PolicyHistory;

namespace Hasm.Infrastructure.Imports;

/// <summary>
/// The generic import framework (Phase 3.2D). <b>No authorization here by design:</b> <see cref="ImportManagementService"/> wraps it. It knows nothing about any business data: files are
/// validated and parsed by <see cref="ImportFileReader"/>, the ORIGINAL is stored only as authenticated envelope ciphertext (AES-256-GCM, per-file data key wrapped by a versioned master key), parsed
/// rows go to the staging tables and nothing else is ever written. Every change appends an immutable history row next to its authenticated audit event in the same transaction; PostgreSQL enforces
/// the life cycle (allowed transitions, immutable evidence, frozen rows, terminal states) for every writer. Retention is explicit and audited: pending batches expire, finished batches lose their
/// encrypted original and staged rows after the company's retention period, and a batch that is applying or failed (recoverable) is never removed automatically.
/// </summary>
public sealed partial class ImportService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, EnvelopeEncryptor encryptor, EncryptedFileStore store, ImportOptions options,
    ImportAdapterRegistry adapters, TimeProvider clock)
{
    public const int DefaultRetentionDays = 30, DefaultPendingExpiryDays = 30;

    internal sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
    }

    private static string Context(Guid companyId, Guid batchId) => $"hasm-import:{companyId:N}:{batchId:N}";

    /// <summary>
    /// Test hook only (never set in production; internal, visible to the test assemblies only): runs inside the settings write's transaction AFTER the revision was read and checked and
    /// BEFORE the write, so a race test can hold every writer at that point and make the race deterministic (#165 G L1, #168 H L1).
    /// </summary>
    internal Func<CancellationToken, Task>? AfterSettingsReadForTests { get; set; }

    // ------------------------------------------------------------------------------------------------------------------------- reads

    private const string Columns =
        "b.{0}, b.{1}, b.{2}, b.{3}, b.{4}, b.{5}, b.{6}, b.{7}, b.{8}, b.{9}, b.{10}, b.{11}, b.{12}, b.{13}, b.{14}, b.{15}, b.{16}, b.{17}, b.{18}, b.{19}, b.{20}, b.{21}, b.{22}, b.{23}, b.{24}, b.{25}, b.{26}";

    private static string SelectColumns => string.Format(Columns, IB.Id, IB.AdapterCode, IB.AdapterVersion, IB.Status, IB.FileName, IB.FileKind, IB.FileSize, IB.FileSha256, IB.KeyId, IB.Options, IB.Sheets,
        IB.RowCount, IB.RowsHash, IB.PlanHash, IB.Revision, IB.CreatedBy, IB.CreatedAtUtc, IB.UpdatedAtUtc, IB.SubmittedBy, IB.SubmittedAtUtc, IB.ExpiresAtUtc, IB.TerminalAtUtc, IB.OriginalPurgedAtUtc, IB.RowsPurgedAtUtc, IB.Mapping, IB.Plan, IB.Result);

    private static DateTimeOffset At(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc), TimeSpan.Zero);

    private static ImportBatchView ReadView(NpgsqlDataReader r, int retentionDays)
    {
        var status = (ImportStatus)r.GetInt16(3);
        DateTimeOffset? terminal = r.IsDBNull(21) ? null : At(r.GetDateTime(21));
        var sheets = JsonNode.Parse(r.GetString(10))!.AsArray().Select(s => new SheetInfo(s!["index"]!.GetValue<int>(), s["name"]!.GetValue<string>(), s["rows"]!.GetValue<long>(), s["columns"]!.GetValue<int>(), s["hidden"]!.GetValue<bool>())).ToList();
        var originalPurged = !r.IsDBNull(22);
        var rowsPurged = !r.IsDBNull(23);
        return new ImportBatchView(r.GetGuid(0), r.GetString(1), r.GetInt32(2), status, r.GetString(4), (ImportFileKind)r.GetInt16(5), r.GetInt64(6), r.GetString(7).Trim(), r.GetString(8),
            JsonNode.Parse(r.GetString(9))!, sheets, r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12).Trim(), r.IsDBNull(13) ? null : r.GetString(13).Trim(), r.GetInt64(14), r.GetGuid(15),
            At(r.GetDateTime(16)), At(r.GetDateTime(17)), r.IsDBNull(18) ? null : r.GetGuid(18), r.IsDBNull(19) ? null : At(r.GetDateTime(19)), At(r.GetDateTime(20)), terminal,
            !originalPurged, !rowsPurged && status >= ImportStatus.Staged, terminal?.AddDays(retentionDays),
            r.IsDBNull(24) ? null : JsonNode.Parse(r.GetString(24)), r.IsDBNull(25) ? null : JsonNode.Parse(r.GetString(25)), r.IsDBNull(26) ? null : JsonNode.Parse(r.GetString(26)));
    }

    private static async Task<(int Retention, int Pending, long Revision, bool Configured)> ReadSettingsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {IMS.RetentionDays}, {IMS.PendingExpiryDays}, {IMS.Revision} FROM {IMS.Table}", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), true) : (DefaultRetentionDays, DefaultPendingExpiryDays, 0, false);
    }

    private static async Task<ImportBatchView?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        var retention = (await ReadSettingsAsync(conn, tx, ct)).Retention;
        await using var cmd = new NpgsqlCommand($"SELECT {SelectColumns} FROM {IB.Table} b WHERE b.{IB.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadView(r, retention) : null;
    }

    public async Task<ImportBatchView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, id, false, ct);
    }

    /// <summary>Lists batches, newest first. <paramref name="allowedAdapters"/> (when not null) restricts the query itself, before paging, to the adapters the caller may see.</summary>
    public async Task<IReadOnlyList<ImportBatchView>> ListAsync(Guid companyId, ImportListFilter filter, IReadOnlyCollection<string>? allowedAdapters, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var retention = (await ReadSettingsAsync(conn, null, ct)).Retention;
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {IB.Table} b WHERE (@s::smallint IS NULL OR b.{IB.Status} = @s) AND (@a::text IS NULL OR b.{IB.AdapterCode} = @a) " +
            $"AND (@allowed::text[] IS NULL OR b.{IB.AdapterCode} = ANY(@allowed)) ORDER BY b.{IB.CreatedAtUtc} DESC, b.{IB.Id} LIMIT @l OFFSET @o", conn);
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Smallint) { Value = filter.Status is { } st ? (short)st : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Text) { Value = (object?)filter.AdapterCode ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("allowed", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = allowedAdapters is null ? DBNull.Value : allowedAdapters.ToArray() });
        cmd.Parameters.AddWithValue("l", Math.Clamp(filter.Limit, 1, 200)); cmd.Parameters.AddWithValue("o", Math.Max(0, filter.Offset));
        var list = new List<ImportBatchView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadView(r, retention));
        return list;
    }

    /// <summary>One page of the staged rows (at most 500 per page), optionally of one row status.</summary>
    public async Task<ImportRowsPage?> RowsAsync(Guid companyId, Guid id, int offset, int limit, short? rowStatus, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var batch = await LoadAsync(conn, null, id, false, ct);
        if (batch is null) return null;
        var headers = batch.Options["headers"] is JsonArray h ? h.Select(x => x?.GetValue<string>() ?? "").ToList() : [];
        await using var cmd = new NpgsqlCommand(
            $"SELECT {IR.RowNo}, {IR.Raw}, {IR.Status}, count(*) OVER (), {IR.Normalized}, {IR.Errors}, {IR.Resolution} ->> 'action', {IR.Analysis} FROM {IR.Table} WHERE {IR.BatchId} = @b AND (@s::smallint IS NULL OR {IR.Status} = @s) ORDER BY {IR.RowNo} LIMIT @l OFFSET @o", conn);
        cmd.Parameters.AddWithValue("b", id);
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Smallint) { Value = rowStatus is { } s ? s : DBNull.Value });
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, 500)); cmd.Parameters.AddWithValue("o", Math.Max(0, offset));
        var rows = new List<ImportRowView>(); var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var analysis = r.IsDBNull(7) ? null : JsonNode.Parse(r.GetString(7));
            rows.Add(new ImportRowView(r.GetInt32(0), JsonNode.Parse(r.GetString(1))!.AsArray().Select(c => c?.GetValue<string>()).ToList(), r.GetInt16(2),
                r.IsDBNull(4) ? null : JsonNode.Parse(r.GetString(4)), r.IsDBNull(5) ? null : JsonNode.Parse(r.GetString(5)), r.IsDBNull(6) ? null : r.GetString(6), analysis?["operation"]?.DeepClone(), analysis?["explanation"]?.GetValue<string>(),
                analysis?["allowed"] is JsonArray al ? al.Select(x => x!.GetValue<string>()).ToList() : null));
            total = (int)r.GetInt64(3);
        }
        return new ImportRowsPage(headers, rowStatus is null ? batch.RowCount : total, Math.Max(0, offset), rows);
    }

    public async Task<IReadOnlyList<ImportEventView>> EventsAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"SELECT {IV.Id}, {IV.Kind}, {IV.Revision}, {IV.ActorUserId}, {IV.AtUtc}, {IV.Detail} FROM {IV.Table} WHERE {IV.BatchId} = @b ORDER BY {IV.Id}", conn);
        cmd.Parameters.AddWithValue("b", id);
        var list = new List<ImportEventView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ImportEventView(r.GetInt64(0), (ImportEventKind)r.GetInt16(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetGuid(3), At(r.GetDateTime(4)), JsonNode.Parse(r.GetString(5))!));
        return list;
    }

    // ------------------------------------------------------------------------------------------------------------------------- writes

    private async Task<ConfigResult<T>> MutateAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) { return abort.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is { } c && c.Contains(IB.UniqueIdempotency.AsSpan(2), StringComparison.Ordinal))
        { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "idempotency_key_reused"); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, MapDatabaseRule(ex.MessageText)); }
    }

    /// <summary>Stable codes for the rules PostgreSQL enforces (the database message is never shown to clients).</summary>
    internal static string MapDatabaseRule(string message) => message switch
    {
        _ when message.Contains("state change is not allowed") || message.Contains("cannot change state") => "invalid_state_change",
        _ when message.Contains("frozen") => "rows_frozen",
        _ when message.Contains("revision") => "revision_conflict",
        _ when message.Contains("finished") => "batch_finished",
        _ when message.Contains("independent approver") => "separation_of_duties",
        _ => "import_invalid",
    };

    private async Task EventAsync(CompanyAuditedWork work, Guid batch, ImportEventKind kind, long revision, Guid? actor, JsonObject detail, CancellationToken ct)
    {
        var json = detail.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(ImportAuditEvents.Of(kind), ActorUserId: actor, SubjectId: batch,
            Detail: $"import {kind} r{revision} h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {IV.Table} ({IV.BatchId}, {IV.Kind}, {IV.Revision}, {IV.ActorUserId}, {IV.AtUtc}, {IV.Detail}, {IV.AuditStream}, {IV.AuditSeq}) VALUES (@b, @k, @r, @a, @t, @d, @s, @q)", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("b", batch); cmd.Parameters.AddWithValue("k", (short)kind); cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)actor ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey); cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? CleanKey(string? key)
    {
        var t = key?.Trim();
        return string.IsNullOrEmpty(t) || t.Length > 100 || t.Any(char.IsControl) ? null : t;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Stores an uploaded file: validates it (format, structure, limits, security scan), encrypts it BEFORE it touches the disk, records the batch with the file's SHA-256 and returns it. A repeated
    /// upload with the same idempotency key by the same user for the same content returns the first batch.
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> UploadAsync(Guid actor, Guid companyId, string? adapterCode, string? fileName, byte[] data, string? idempotencyKey, CancellationToken ct = default)
    {
        var adapter = adapters.Find(adapterCode);
        if (adapter is null) return ConfigResult<ImportBatchView>.Invalid([new("adapter", "unknown_adapter")]);
        if (idempotencyKey is not null && CleanKey(idempotencyKey) is null) return ConfigResult<ImportBatchView>.Invalid([new("idempotencyKey", "invalid")]);
        var key = CleanKey(idempotencyKey);
        var safeName = ImportFileReader.SafeFileName(fileName);
        FileInspection inspection;
        try { inspection = ImportFileReader.Inspect(safeName, data, options); }
        catch (ImportRejectedException ex) { return ConfigResult<ImportBatchView>.Invalid([new("file", ex.Code)]); }
        var sha = Sha(data);

        if (key is not null && await ReplayAsync(actor, companyId, key, sha, adapter.Code, ct) is { } replay) return replay;

        var id = Guid.NewGuid();
        var storageName = id.ToString("N") + ".enc";
        var envelope = encryptor.Encrypt(data, Context(companyId, id));     // throws EncryptionUnavailableException (fail closed) when no valid key is configured
        var keyId = EnvelopeEncryptor.KeyIdOf(envelope);
        store.Create(companyId, storageName, envelope);                       // ciphertext only; throws when storage is not configured
        ConfigResult<ImportBatchView> result;
        try
        {
            result = await MutateAsync(companyId, async work =>
            {
                var settings = await ReadSettingsAsync(work.Connection, work.Transaction, ct);
                var now = clock.GetUtcNow().UtcDateTime;
                var sheets = new JsonArray([.. inspection.Sheets.Select(s => (JsonNode)new JsonObject { ["index"] = s.Index, ["name"] = s.Name, ["rows"] = s.Rows, ["columns"] = s.Columns, ["hidden"] = s.Hidden })]);
                await using (var ins = new NpgsqlCommand(
                    $"INSERT INTO {IB.Table} ({IB.Id}, {IB.AdapterCode}, {IB.AdapterVersion}, {IB.Status}, {IB.FileName}, {IB.FileKind}, {IB.FileSize}, {IB.FileSha256}, {IB.StorageName}, {IB.KeyId}, {IB.Sheets}, {IB.Revision}, {IB.IdempotencyKey}, " +
                    $"{IB.CreatedBy}, {IB.CreatedAtUtc}, {IB.UpdatedAtUtc}, {IB.ExpiresAtUtc}) VALUES (@id, @ad, @av, 1, @fn, @fk, @fs, @sha, @sn, @kid, @sh, 1, @ik, @by, @t, @t, @exp)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("ad", adapter.Code); ins.Parameters.AddWithValue("av", adapter.Version); ins.Parameters.AddWithValue("fn", safeName);
                    ins.Parameters.AddWithValue("fk", (short)inspection.Kind); ins.Parameters.AddWithValue("fs", (long)data.Length); ins.Parameters.AddWithValue("sha", sha); ins.Parameters.AddWithValue("sn", storageName);
                    ins.Parameters.AddWithValue("kid", keyId); ins.Parameters.Add(new NpgsqlParameter("sh", NpgsqlDbType.Jsonb) { Value = sheets.ToJsonString() });
                    ins.Parameters.Add(new NpgsqlParameter("ik", NpgsqlDbType.Varchar) { Value = (object?)key ?? DBNull.Value });
                    ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("t", now); ins.Parameters.AddWithValue("exp", now.AddDays(settings.Pending));
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await EventAsync(work, id, ImportEventKind.Uploaded, 1, actor, new JsonObject { ["adapter"] = adapter.Code, ["adapterVersion"] = adapter.Version, ["kind"] = (int)inspection.Kind, ["size"] = data.Length, ["sha256"] = sha, ["keyId"] = keyId }, ct);
                return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            }, ct);
        }
        catch { store.Delete(companyId, storageName); throw; }
        if (!result.IsOk) store.Delete(companyId, storageName);      // nothing was recorded: the orphaned ciphertext must not stay
        // a concurrent upload with the same key won the race (its unique key was committed first): the documented contract is a replay of that batch when the request is the same (same user,
        // same file SHA-256, same adapter), and 409 idempotency_key_reused otherwise (C2 matrix part 2)
        if (!result.IsOk && result.Code == "idempotency_key_reused" && key is not null && await ReplayAsync(actor, companyId, key, sha, adapter.Code, ct) is { } winner) return winner;
        return result;
    }

    /// <summary>
    /// The batch already recorded under <paramref name="key"/>: a replay (200, the first batch) when it was uploaded by the same user with the same file SHA-256 and adapter, otherwise
    /// 409 <c>idempotency_key_reused</c>; null when no batch carries the key.
    /// </summary>
    private async Task<ConfigResult<ImportBatchView>?> ReplayAsync(Guid actor, Guid companyId, string key, string sha, string adapterCode, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT {IB.Id}, {IB.CreatedBy}, {IB.FileSha256}, {IB.AdapterCode} FROM {IB.Table} WHERE {IB.IdempotencyKey} = @k", conn);
        q.Parameters.AddWithValue("k", key);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var existing = r.GetGuid(0); var same = r.GetGuid(1) == actor && r.GetString(2).Trim() == sha && r.GetString(3) == adapterCode;
        await r.CloseAsync();
        if (!same) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "idempotency_key_reused");
        return ConfigResult<ImportBatchView>.Ok((await LoadAsync(conn, null, existing, false, ct))!);
    }

    private static string HashRow(int rowNo, string rawJson) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{rowNo}|{rawJson}"))).ToLowerInvariant();

    /// <summary>
    /// Parses the encrypted original with the chosen options into the staging rows (replacing any earlier staging of this batch, and voiding any mapping, plan and approval built on it).
    /// The batch moves to Staged; nothing business-side is touched.
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> StageAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, StageInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var batch = await GetAsync(companyId, id, ct);
        if (batch is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.NotFound, "import_not_found");
        if (batch.Revision != expectedRevision.Value) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionFailed, "revision_conflict", batch.Revision);
        if (batch.Status is not (ImportStatus.Uploaded or ImportStatus.Staged or ImportStatus.Mapped or ImportStatus.Validated or ImportStatus.Failed)) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "batch_not_stageable");
        if (!batch.OriginalAvailable) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "original_unavailable");

        var errors = new List<FieldError>();
        var sheet = input?.Sheet ?? 0; var header = input?.HeaderRow ?? 1; var encoding = (input?.Encoding ?? "utf-8").Trim().ToLowerInvariant();
        char? delimiter = null;
        if (!string.IsNullOrEmpty(input?.Delimiter)) { if (input.Delimiter.Length == 1) delimiter = input.Delimiter[0]; else if (input.Delimiter == "\\t") delimiter = '\t'; else errors.Add(new("delimiter", "invalid")); }
        if (sheet < 0 || sheet >= batch.Sheets.Count) errors.Add(new("sheet", "invalid_worksheet"));
        if (header is < 1 or > 1000) errors.Add(new("headerRow", "invalid_header_row"));
        if (errors.Count > 0) return ConfigResult<ImportBatchView>.Invalid(errors);

        var envelope = store.Read(companyId, batch.Id.ToString("N") + ".enc");
        if (envelope is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "original_unavailable");
        byte[] plain;
        try { plain = encryptor.Decrypt(envelope, Context(companyId, id)); }
        catch (EncryptionUnavailableException ex) when (ex.Code is "encryption_authentication_failed" or "encryption_format_invalid") { return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "original_corrupted"); }
        if (Sha(plain) != batch.FileSha256) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "original_corrupted");
        ParsedSheet parsed;
        try { parsed = ImportFileReader.Read(batch.FileName, plain, options, new ReadOptions(sheet, header, encoding, delimiter)); }
        catch (ImportRejectedException ex) { return ConfigResult<ImportBatchView>.Invalid([new("file", ex.Code)]); }
        finally { CryptographicOperations.ZeroMemory(plain); }

        var rawJson = parsed.Rows.Select(r => JsonSerializer.Serialize(r.Cells)).ToList();
        var hashes = parsed.Rows.Select((r, i) => HashRow(r.RowNo, rawJson[i])).ToList();
        var headersJson = JsonSerializer.Serialize(parsed.Headers);
        var rowsHash = Sha(Encoding.UTF8.GetBytes("hasm-import-rows/v1\n" + headersJson + "\n" + string.Join('\n', parsed.Rows.Select((r, i) => $"{r.RowNo}|{hashes[i]}"))));

        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Status is not (ImportStatus.Uploaded or ImportStatus.Staged or ImportStatus.Mapped or ImportStatus.Validated or ImportStatus.Failed)) throw new Abort(ConfigStatus.Conflict, "batch_not_stageable");
            var now = clock.GetUtcNow().UtcDateTime;
            // 1) the batch becomes Staged first (this voids mapping, plan and approval: they were built on the old rows); the row guard only accepts rows in that state
            await using (var up = new NpgsqlCommand(
                $"UPDATE {IB.Table} SET {IB.Status} = 2, {IB.Mapping} = NULL, {IB.PlanHash} = NULL, {IB.Plan} = NULL, {IB.SubmittedBy} = NULL, {IB.SubmittedAtUtc} = NULL, {IB.ApprovalRevision} = NULL, {IB.Result} = NULL, {IB.RowCount} = 0, {IB.RowsHash} = @h, " +
                $"{IB.Options} = @o, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            {
                up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("h", rowsHash); up.Parameters.AddWithValue("t", now);
                var opts = new JsonObject { ["sheet"] = sheet, ["sheetName"] = batch.Sheets[sheet].Name, ["headerRow"] = header, ["encoding"] = encoding, ["delimiter"] = parsed.Delimiter is { } d ? d.ToString() : null, ["headers"] = JsonNode.Parse(headersJson) };
                up.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Jsonb) { Value = opts.ToJsonString() });
                await up.ExecuteNonQueryAsync(ct);
            }
            // 2) replace the rows
            await using (var del = new NpgsqlCommand($"DELETE FROM {IR.Table} WHERE {IR.BatchId} = @id", work.Connection, work.Transaction)) { del.Parameters.AddWithValue("id", id); await del.ExecuteNonQueryAsync(ct); }
            for (var start = 0; start < parsed.Rows.Count; start += 1000)
            {
                var n = Math.Min(1000, parsed.Rows.Count - start);
                await using var ins = new NpgsqlCommand(
                    $"INSERT INTO {IR.Table} ({IR.BatchId}, {IR.RowNo}, {IR.Raw}, {IR.RowHash}) SELECT @b, t.n, t.r::jsonb, t.h FROM unnest(@nos, @raws, @hashes) AS t(n, r, h)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("b", id);
                ins.Parameters.Add(new NpgsqlParameter("nos", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = parsed.Rows.Skip(start).Take(n).Select(r => r.RowNo).ToArray() });
                ins.Parameters.Add(new NpgsqlParameter("raws", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rawJson.Skip(start).Take(n).ToArray() });
                ins.Parameters.Add(new NpgsqlParameter("hashes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = hashes.Skip(start).Take(n).ToArray() });
                await ins.ExecuteNonQueryAsync(ct);
            }
            // 3) count (the batch row guard keeps the revision strictly +1 per UPDATE, so the count rides along with a second, final update)
            await using (var cnt = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.RowCount} = @n, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            {
                cnt.Parameters.AddWithValue("id", id); cnt.Parameters.AddWithValue("n", parsed.Rows.Count); cnt.Parameters.AddWithValue("t", now);
                await cnt.ExecuteNonQueryAsync(ct);
            }
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.Staged, view.Revision, actor, new JsonObject { ["rows"] = parsed.Rows.Count, ["sheet"] = sheet, ["headerRow"] = header, ["encoding"] = encoding, ["rowsHash"] = rowsHash }, ct);
            return view;
        }, ct);
    }

    /// <summary>Cancels a batch that is not applying and not finished. Nothing business-side exists to undo; the staged rows and the encrypted original follow the retention rules.</summary>
    public async Task<ConfigResult<ImportBatchView>> CancelAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Status is ImportStatus.Applying) throw new Abort(ConfigStatus.Conflict, "batch_applying");
            if (ImportStatusRules.IsTerminal(cur.Status)) throw new Abort(ConfigStatus.Conflict, "batch_finished");
            var now = clock.GetUtcNow().UtcDateTime;
            await TerminalAsync(work, id, ImportStatus.Cancelled, now, ct);
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.Cancelled, view.Revision, actor, new JsonObject { ["from"] = ImportStatusRules.Name(cur.Status) }, ct);
            return view;
        }, ct);
    }

    private static async Task TerminalAsync(CompanyAuditedWork work, Guid id, ImportStatus status, DateTime now, CancellationToken ct)
    {
        await using var up = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.Status} = @s, {IB.TerminalAtUtc} = @t, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction);
        up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("s", (short)status); up.Parameters.AddWithValue("t", now);
        await up.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Extends a pending batch's expiry. Renewal never revives an approval: a batch that was already validated, submitted or approved returns to Mapped and must be validated (dry run) and approved
    /// again against the data as it is today.
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> RenewAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Status is not (ImportStatus.Uploaded or ImportStatus.Staged or ImportStatus.Mapped or ImportStatus.Validated or ImportStatus.Submitted or ImportStatus.Approved)) throw new Abort(ConfigStatus.Conflict, "batch_not_renewable");
            var settings = await ReadSettingsAsync(work.Connection, work.Transaction, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var revalidate = cur.Status >= ImportStatus.Validated;
            await using (var up = new NpgsqlCommand(
                $"UPDATE {IB.Table} SET {IB.ExpiresAtUtc} = @e, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t" +
                (revalidate ? $", {IB.Status} = 3, {IB.PlanHash} = NULL, {IB.Plan} = NULL, {IB.SubmittedBy} = NULL, {IB.SubmittedAtUtc} = NULL, {IB.ApprovalRevision} = NULL" : "") + $" WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            {
                up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("e", now.AddDays(settings.Pending)); up.Parameters.AddWithValue("t", now);
                await up.ExecuteNonQueryAsync(ct);
            }
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.Renewed, view.Revision, actor, new JsonObject { ["from"] = ImportStatusRules.Name(cur.Status), ["revalidationRequired"] = revalidate, ["expiresAtUtc"] = view.ExpiresAtUtc.UtcDateTime.ToString("O") }, ct);
            return view;
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------------------- settings

    public async Task<ImportSettingsView> GetSettingsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var s = await ReadSettingsAsync(conn, null, ct);
        return new ImportSettingsView(s.Retention, s.Pending, s.Revision, s.Configured);
    }

    public async Task<ConfigResult<ImportSettingsView>> PutSettingsAsync(Guid actor, Guid companyId, long? expectedRevision, ImportSettingsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportSettingsView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var errors = new List<FieldError>();
        if (input?.RetentionDays is null || input.RetentionDays < 1 || input.RetentionDays > options.MaxRetentionDays) errors.Add(new("retentionDays", "out_of_range"));
        if (input?.PendingExpiryDays is null || input.PendingExpiryDays < 1 || input.PendingExpiryDays > options.MaxRetentionDays) errors.Add(new("pendingExpiryDays", "out_of_range"));
        if (errors.Count > 0) return ConfigResult<ImportSettingsView>.Invalid(errors);
        var now0 = await GetSettingsAsync(companyId, ct);
        if (now0.Revision != expectedRevision.Value) return ConfigResult<ImportSettingsView>.Fail(ConfigStatus.PreconditionFailed, "revision_conflict", now0.Revision);
        if (now0.Configured && now0.RetentionDays == input!.RetentionDays && now0.PendingExpiryDays == input.PendingExpiryDays) return ConfigResult<ImportSettingsView>.Ok(now0);   // unchanged: no revision, no event
        return await MutateAsync(companyId, async work =>
        {
            var cur = await ReadSettingsAsync(work.Connection, work.Transaction, ct);
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (AfterSettingsReadForTests is { } hook) await hook(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            // race-safe against concurrent writers with the same If-Match (C2 matrix part 2): the first write inserts the singleton with ON CONFLICT DO NOTHING and an update applies only at the
            // expected revision; the loser writes nothing and gets 412 revision_conflict with the current revision. Any other database error still propagates.
            await using (var w = new NpgsqlCommand(cur.Configured
                ? $"UPDATE {IMS.Table} SET {IMS.RetentionDays} = @r, {IMS.PendingExpiryDays} = @p, {IMS.Revision} = {IMS.Revision} + 1, {IMS.UpdatedAtUtc} = @t WHERE {IMS.Revision} = @e"
                : $"INSERT INTO {IMS.Table} ({IMS.RetentionDays}, {IMS.PendingExpiryDays}, {IMS.Revision}, {IMS.UpdatedAtUtc}) VALUES (@r, @p, 1, @t) ON CONFLICT DO NOTHING", work.Connection, work.Transaction))
            {
                w.Parameters.AddWithValue("r", input!.RetentionDays!.Value); w.Parameters.AddWithValue("p", input.PendingExpiryDays!.Value); w.Parameters.AddWithValue("t", now);
                w.Parameters.AddWithValue("e", cur.Revision);
                if (await w.ExecuteNonQueryAsync(ct) != 1)
                    throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", (await ReadSettingsAsync(work.Connection, work.Transaction, ct)).Revision);
            }
            var revision = cur.Revision + 1;
            var changes = new JsonObject { ["retentionDays"] = new JsonObject { ["old"] = cur.Configured ? cur.Retention : null, ["new"] = input.RetentionDays }, ["pendingExpiryDays"] = new JsonObject { ["old"] = cur.Configured ? cur.Pending : null, ["new"] = input.PendingExpiryDays } };
            var json = changes.ToJsonString();
            var receipt = await work.AppendAuditAsync(new AuditEventData(ImportAuditEvents.SettingsChanged, ActorUserId: actor,
                Detail: $"import settings r{revision} h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
            await using (var h = new NpgsqlCommand(
                $"INSERT INTO {PH.Table} ({PH.Entity}, {PH.EntityKey}, {PH.Revision}, {PH.ActorUserId}, {PH.ChangedAtUtc}, {PH.Changes}, {PH.AuditStream}, {PH.AuditSeq}) VALUES (6, 'import_settings', @r, @a, @t, @c, @s, @q)", work.Connection, work.Transaction))
            {
                h.Parameters.AddWithValue("r", revision); h.Parameters.AddWithValue("a", actor); h.Parameters.AddWithValue("t", now);
                h.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
                h.Parameters.AddWithValue("s", receipt.StreamKey); h.Parameters.AddWithValue("q", receipt.Seq);
                await h.ExecuteNonQueryAsync(ct);
            }
            return new ImportSettingsView(input.RetentionDays!.Value, input.PendingExpiryDays!.Value, revision, true);
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------------------- retention and key rotation

    /// <summary>
    /// Retention pass for one company (also run on a schedule): expires pending batches whose expiry has passed (a batch that is applying or failed is never touched), then purges the encrypted original and
    /// the staged rows of finished batches whose retention period is over. Every step is its own transaction with its own audit event; the file is deleted first (idempotently), so a crash is repaired by
    /// the next pass. Hashes, history and approvals stay.
    /// </summary>
    public async Task<ImportMaintenanceResult> MaintainAsync(Guid? actor, Guid companyId, CancellationToken ct = default)
    {
        int expired = 0, purged = 0;
        List<Guid> due;
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        {
            await using var q = new NpgsqlCommand($"SELECT {IB.Id} FROM {IB.Table} WHERE {IB.Status} IN (1, 2, 3, 4, 5, 6) AND {IB.ExpiresAtUtc} <= @now ORDER BY {IB.ExpiresAtUtc}", conn);
            q.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
            due = []; await using var r = await q.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) due.Add(r.GetGuid(0));
        }
        foreach (var id in due)
        {
            var res = await MutateAsync(companyId, async work =>
            {
                var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct);
                if (cur is null || cur.Status is not (ImportStatus.Uploaded or ImportStatus.Staged or ImportStatus.Mapped or ImportStatus.Validated or ImportStatus.Submitted or ImportStatus.Approved) || cur.ExpiresAtUtc > clock.GetUtcNow()) return false;
                await TerminalAsync(work, id, ImportStatus.Expired, clock.GetUtcNow().UtcDateTime, ct);
                var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
                await EventAsync(work, id, ImportEventKind.Expired, view.Revision, actor, new JsonObject { ["from"] = ImportStatusRules.Name(cur.Status) }, ct);
                return true;
            }, ct);
            if (res is { IsOk: true, Value: true }) expired++;
        }

        List<(Guid Id, string Storage)> toPurge;
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        {
            var retention = (await ReadSettingsAsync(conn, null, ct)).Retention;
            await using var q = new NpgsqlCommand(
                $"SELECT {IB.Id}, {IB.StorageName} FROM {IB.Table} WHERE {IB.Status} IN (8, 10, 11, 12) AND {IB.TerminalAtUtc} + make_interval(days => @d) <= @now AND ({IB.OriginalPurgedAtUtc} IS NULL OR {IB.RowsPurgedAtUtc} IS NULL) ORDER BY {IB.TerminalAtUtc}", conn);
            q.Parameters.AddWithValue("d", retention); q.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
            toPurge = []; await using var r = await q.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) toPurge.Add((r.GetGuid(0), r.GetString(1)));
        }
        foreach (var (id, storage) in toPurge)
        {
            var fileExisted = store.Delete(companyId, storage);       // idempotent; the database is updated only after the file is gone
            var res = await MutateAsync(companyId, async work =>
            {
                var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct);
                if (cur is null || !ImportStatusRules.IsTerminal(cur.Status)) return false;
                long rowsDeleted;
                await using (var del = new NpgsqlCommand($"DELETE FROM {IR.Table} WHERE {IR.BatchId} = @id", work.Connection, work.Transaction)) { del.Parameters.AddWithValue("id", id); rowsDeleted = await del.ExecuteNonQueryAsync(ct); }
                var now = clock.GetUtcNow().UtcDateTime;
                await using (var up = new NpgsqlCommand(
                    $"UPDATE {IB.Table} SET {IB.OriginalPurgedAtUtc} = COALESCE({IB.OriginalPurgedAtUtc}, @t), {IB.RowsPurgedAtUtc} = COALESCE({IB.RowsPurgedAtUtc}, @t), {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
                { up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("t", now); await up.ExecuteNonQueryAsync(ct); }
                var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
                await EventAsync(work, id, ImportEventKind.Purged, view.Revision, actor, new JsonObject { ["rowsDeleted"] = rowsDeleted, ["originalDeleted"] = fileExisted, ["sha256"] = view.FileSha256, ["planHash"] = view.PlanHash }, ct);
                return true;
            }, ct);
            if (res is { IsOk: true, Value: true }) purged++;
        }
        return new ImportMaintenanceResult(expired, purged, 0);
    }

    /// <summary>
    /// Re-wraps the per-file data keys of every stored original under the CURRENT active master key (key rotation). Only the wrapped data key changes (the ciphertext is untouched), the old key must still be
    /// available, each file is replaced atomically and each step is audited. Safe to repeat: files already under the active key are skipped.
    /// </summary>
    public async Task<ImportMaintenanceResult> RewrapAsync(Guid actor, Guid companyId, CancellationToken ct = default)
    {
        var active = encryptor.ActiveKeyId;
        List<(Guid Id, string Storage)> todo;
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        {
            await using var q = new NpgsqlCommand($"SELECT {IB.Id}, {IB.StorageName} FROM {IB.Table} WHERE {IB.OriginalPurgedAtUtc} IS NULL AND {IB.KeyId} <> @k ORDER BY {IB.CreatedAtUtc}", conn);
            q.Parameters.AddWithValue("k", active);
            todo = []; await using var r = await q.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) todo.Add((r.GetGuid(0), r.GetString(1)));
        }
        var done = 0;
        foreach (var (id, storage) in todo)
        {
            var res = await MutateAsync(companyId, async work =>
            {
                var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct);
                if (cur is null || !cur.OriginalAvailable || cur.KeyId == active) return false;
                var envelope = store.Read(companyId, storage);
                if (envelope is null) return false;
                var oldKey = EnvelopeEncryptor.KeyIdOf(envelope);
                var rewrapped = oldKey == active ? envelope : encryptor.Rewrap(envelope, Context(companyId, id));
                if (!ReferenceEquals(rewrapped, envelope)) store.Replace(companyId, storage, rewrapped);
                await using (var up = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.KeyId} = @k, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
                { up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("k", active); up.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime); await up.ExecuteNonQueryAsync(ct); }
                var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
                await EventAsync(work, id, ImportEventKind.Rewrapped, view.Revision, actor, new JsonObject { ["fromKey"] = oldKey, ["toKey"] = active }, ct);
                return true;
            }, ct);
            if (res is { IsOk: true, Value: true }) done++;
        }
        return new ImportMaintenanceResult(0, 0, done);
    }
}
