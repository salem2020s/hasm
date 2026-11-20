using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Npgsql;
using AT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Attachment;

namespace Hasm.Infrastructure.Attachments;

/// <summary>Business audit events of attachments (company stream, 1600-1609). Persisted codes: never renumber.</summary>
public static class AttachmentAuditEvents
{
    public const short Uploaded = 1600;
    public const short Removed = 1601;
    public const short SensitiveDownloaded = 1602;
    public const short Rewrapped = 1603;
    public const short IntegrityFailure = 1604;
    public const short RewrapFailed = 1605;
    public const short OrphansSwept = 1606;
    public const short Verified = 1607;
}

/// <summary>Where attachments live on the server and how large they may be (section <c>Hasm:Attachments</c>).</summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Hasm:Attachments";
    /// <summary>Absolute directory outside any web root (disk, SMB or NFS mount); a sub-directory per company is created owner-only. Required.</summary>
    public string? StorageDirectory { get; init; }
    public long MaxFileBytes { get; init; } = 25L * 1024 * 1024;

    public static AttachmentOptions FromConfiguration(IConfiguration configuration)
    {
        string? Get(string key) => string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{key}"]) ? null : configuration[$"{SectionName}:{key}"]!.Trim();
        var max = long.TryParse(Get("MaxFileBytes"), out var m) && m is > 0 and <= 512L * 1024 * 1024 ? m : new AttachmentOptions().MaxFileBytes;
        return new AttachmentOptions { StorageDirectory = Get("StorageDirectory"), MaxFileBytes = max };
    }
}

/// <summary>
/// Files on the server filesystem (company-isolated owner-only directories, never inside a web root, never served directly). File names are generated identifiers (<c>&lt;32 hex&gt;.att</c>),
/// validated on every call, so a path can never escape its company directory. Writes go to a temporary name created exclusively and are moved into place atomically.
/// </summary>
public sealed partial class AttachmentFileStore(AttachmentOptions options, EncryptionOptions encryption)
{
    [GeneratedRegex("^[0-9a-f]{32}\\.att$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();

    private string Directory(Guid companyId)
    {
        if (options.StorageDirectory is not { } root || !Path.IsPathFullyQualified(root))
            throw new EncryptionUnavailableException("attachment_storage_not_configured", $"{AttachmentOptions.SectionName}:StorageDirectory (an absolute path) is required.");
        var dir = Path.Combine(root, companyId.ToString("N"));
        AuditFilePermissions.CreateOwnerOnlyDirectory(root);
        AuditFilePermissions.CreateOwnerOnlyDirectory(dir);
        if (!encryption.AllowInsecureFilePermissions) AuditFilePermissions.EnsureNotWritableByOthers(dir);
        return dir;
    }

    private string PathOf(Guid companyId, string name)
    {
        if (!NamePattern().IsMatch(name)) throw new ArgumentException("Invalid stored file name.", nameof(name));
        return Path.Combine(Directory(companyId), name);
    }

    public static string NewName() => Guid.NewGuid().ToString("N") + ".att";

    public void Create(Guid companyId, string name, byte[] bytes) => Write(companyId, name, bytes, overwrite: false);
    public void Replace(Guid companyId, string name, byte[] bytes) => Write(companyId, name, bytes, overwrite: true);

    private void Write(Guid companyId, string name, byte[] bytes, bool overwrite)
    {
        var path = PathOf(companyId, name);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            AuditFilePermissions.CreateOwnerOnlyFile(temp, bytes);
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public byte[]? Read(Guid companyId, string name) { var p = PathOf(companyId, name); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
    public bool Exists(Guid companyId, string name) => File.Exists(PathOf(companyId, name));
    public bool Delete(Guid companyId, string name) { var p = PathOf(companyId, name); if (!File.Exists(p)) return false; File.Delete(p); return true; }

    /// <summary>The stored files of a company with their last write time (UTC), for the orphan sweep. Only well-formed names are listed.</summary>
    public IReadOnlyList<(string Name, DateTime LastWriteUtc)> List(Guid companyId) =>
        [.. new DirectoryInfo(Directory(companyId)).EnumerateFiles("*.att").Where(f => NamePattern().IsMatch(f.Name)).Select(f => (f.Name, f.LastWriteTimeUtc))];
}

/// <summary>The record kinds an attachment may belong to. A new kind is registered here when its module exists; the polymorphic reference is validated by the service (the only writer).</summary>
public static class AttachmentSubjects
{
    public static readonly IReadOnlyDictionary<string, (string Table, string IdColumn)> Kinds = new Dictionary<string, (string, string)>
    {
        ["local_party"] = (CompanySchema.LocalParty.Table, CompanySchema.LocalParty.Id),
        ["inventory_item"] = (CompanySchema.Item.Table, CompanySchema.Item.Id),
    };
}

public sealed record AttachmentView(Guid Id, string SubjectKind, Guid SubjectId, string FileName, string ContentType, long SizeBytes, string Sha256, bool IsSensitive, bool IsRemoved, string? RemovalReason, long Revision, DateTimeOffset CreatedAtUtc);
public sealed record AttachmentContent(AttachmentView Attachment, byte[] Bytes);
public sealed record AttachmentCapabilities(bool View, bool Upload, bool Delete, bool SensitiveView, bool SensitiveUpload);
/// <summary><see cref="RetainedUnderOtherKeys"/>: sensitive attachments that were REMOVED (their bytes are retained, and a removed row never changes again) whose envelope is still under a master key other than the active one. Such a key must NOT be retired: the retained files could never be decrypted again.</summary>
public sealed record RewrapOutcome(int Rewrapped, int Skipped, int Failed, int RetainedUnderOtherKeys = 0)
{
    /// <summary>True only when nothing is left under a non-active key: every active file was rewrapped (or already current), none failed and no retained removed file needs another key.</summary>
    public bool OldKeysMayBeRetired => Failed == 0 && RetainedUnderOtherKeys == 0;
}

/// <summary>
/// The result of verifying a company's attachment set against its stored files (the proof after a restore or before a backup is relied on). Lists carry attachment ids only (never names or paths).
/// <see cref="KeyIdsInUse"/> are the master keys that the sensitive files need: they must be in the key backup. A file is OK only when it exists, is readable with an available key and its plaintext hash matches.
/// </summary>
public sealed record AttachmentVerification(int Checked, int Ok, int TotalSensitive, long TotalBytes, IReadOnlyList<Guid> Missing, IReadOnlyList<Guid> HashMismatch, IReadOnlyList<Guid> KeyUnavailable,
    IReadOnlyList<Guid> Unreadable, int OrphanFiles, IReadOnlyList<string> KeyIdsInUse)
{
    public bool IsClean => Missing.Count == 0 && HashMismatch.Count == 0 && KeyUnavailable.Count == 0 && Unreadable.Count == 0;
}

/// <summary>
/// The audited company-side store of attachments (Phase 3.12, SRS 14.3/14.4/17.1/17.2). <b>No authorization here by design:</b> <see cref="AttachmentManagementService"/> wraps it. Metadata in the company
/// database, bytes on the server filesystem; a normal file is stored as is, a SENSITIVE file is envelope-encrypted (AES-256-GCM, a fresh data key wrapped under the versioned master key, the context binds it
/// to its company and attachment so it cannot be swapped). The SHA-256 of the plaintext is stored and verified on EVERY read (a mismatch is an integrity failure, audited, and nothing is returned).
/// A removal keeps the metadata row and the stored bytes (retention policy is an owner decision): the file is simply no longer readable through the application.
/// </summary>
public sealed partial class AttachmentService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, AttachmentFileStore store, EnvelopeEncryptor encryptor, AttachmentOptions options, TimeProvider clock)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }
    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, code, [new FieldError(field, code)]);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Context(Guid companyId, Guid id) => $"hasm-attachment:{companyId:N}:{id:N}";

    /// <summary>Declared type -> allowed, with the signature the content must start with (null = text, validated as UTF-8 without NUL bytes).</summary>
    private static readonly IReadOnlyDictionary<string, byte[]?[]> Types = new Dictionary<string, byte[]?[]>
    {
        ["application/pdf"] = [Encoding.ASCII.GetBytes("%PDF-")],
        ["image/png"] = [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]],
        ["image/jpeg"] = [[0xFF, 0xD8, 0xFF]],
        ["text/plain"] = [null],
        ["text/csv"] = [null],
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = [[0x50, 0x4B, 0x03, 0x04]],
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = [[0x50, 0x4B, 0x03, 0x04]],
    };
    public static IReadOnlyCollection<string> AllowedContentTypes => [.. Types.Keys];

    [GeneratedRegex("[\"<>|:*?\\\\/]")] private static partial Regex Unsafe();

    /// <summary>
    /// The display name, only ever a label (never a path): any directory part removed; control characters replaced; format characters (bidirectional overrides such as RLO, zero-width and joiners),
    /// line and paragraph separators, private-use, unassigned and lone surrogate characters removed; path-significant characters replaced; trimmed; at most 200 UTF-16 units without splitting a
    /// surrogate pair. Null when nothing usable remains.
    /// </summary>
    public static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Replace('\\', '/'); n = n[(n.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder(n.Length);
        foreach (var rune in n.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(rune);
            if (rune == Rune.ReplacementChar || cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.Surrogate) continue;
            if (cat == System.Globalization.UnicodeCategory.Control) sb.Append('_'); else sb.Append(rune.ToString());
        }
        n = Unsafe().Replace(sb.ToString(), "_").Trim().Trim('.');
        if (n.Length == 0) return null;
        if (n.Length > 200) n = char.IsHighSurrogate(n[199]) ? n[..199] : n[..200];
        return n.Trim();
    }

    /// <summary>The declared type without parameters (a charset is accepted and dropped); Windows labels a .csv as application/vnd.ms-excel, which is read as text/csv for that extension only.</summary>
    private static string? NormalizeType(string? declared, string? fileName)
    {
        var t = declared?.Split(';')[0].Trim().ToLowerInvariant();
        if (t == "application/vnd.ms-excel" && fileName is not null && fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return "text/csv";
        return t;
    }

    private static bool LooksLike(string contentType, byte[] bytes)
    {
        foreach (var sig in Types[contentType])
        {
            if (sig is null)
            {
                if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
                try { new UTF8Encoding(false, true).GetString(bytes); return true; } catch (DecoderFallbackException) { return false; }
            }
            if (bytes.Length >= sig.Length && bytes.AsSpan(0, sig.Length).SequenceEqual(sig)) return true;
        }
        return false;
    }

    private static AttachmentView Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetString(6), r.GetInt16(7) == 2, r.GetInt16(8) == 2,
        r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10), r.GetFieldValue<DateTimeOffset>(11));
    private const string Cols = $"{AT.Id}, {AT.SubjectKind}, {AT.SubjectId}, {AT.FileName}, {AT.ContentType}, {AT.SizeBytes}, {AT.Sha256}, {AT.Sensitivity}, {AT.State}, {AT.RemovalReason}, {AT.Revision}, {AT.CreatedAtUtc}";

    private static async Task<List<(AttachmentView View, string StorageName, string? KeyId)>> Load(NpgsqlConnection conn, NpgsqlTransaction? tx, string where, Action<NpgsqlCommand> bind, bool forUpdate, CancellationToken ct)
    {
        var rows = new List<(AttachmentView, string, string?)>();
        await using var q = new NpgsqlCommand($"SELECT {Cols}, {AT.StorageName}, {AT.KeyId} FROM {AT.Table} WHERE {where} ORDER BY {AT.CreatedAtUtc}, {AT.Id}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        bind(q);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add((Map(r), r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13)));
        return rows;
    }

    public async Task<IReadOnlyList<AttachmentView>> ListAsync(Guid companyId, string subjectKind, Guid subjectId, bool includeSensitive, bool includeRemoved, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var rows = await Load(conn, null, $"{AT.SubjectKind} = @k AND {AT.SubjectId} = @s", c => { c.Parameters.AddWithValue("k", subjectKind); c.Parameters.AddWithValue("s", subjectId); }, false, ct);
        return [.. rows.Select(x => x.View).Where(v => (includeSensitive || !v.IsSensitive) && (includeRemoved || !v.IsRemoved))];
    }

    public async Task<AttachmentView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await Load(conn, null, $"{AT.Id} = @id", c => c.Parameters.AddWithValue("id", id), false, ct)).Select(x => x.View).SingleOrDefault();
    }

    public async Task<ConfigResult<AttachmentView>> UploadAsync(Guid actor, Guid companyId, string? subjectKind, Guid? subjectId, string? fileName, string? contentType, byte[]? content, bool sensitive, CancellationToken ct = default)
    {
        var errors = new List<FieldError>();
        if (subjectKind is null || !AttachmentSubjects.Kinds.ContainsKey(subjectKind)) errors.Add(new FieldError("subjectKind", "subject_kind_invalid"));
        if (subjectId is null) errors.Add(new FieldError("subjectId", "required"));
        var name = SanitizeFileName(fileName);
        if (name is null) errors.Add(new FieldError("fileName", "file_name_invalid"));
        var type = NormalizeType(contentType, name);
        if (type is null || !Types.ContainsKey(type)) errors.Add(new FieldError("contentType", "content_type_not_allowed"));
        if (content is null || content.Length == 0) errors.Add(new FieldError("file", "file_empty"));
        else if (content.LongLength > options.MaxFileBytes) errors.Add(new FieldError("file", "file_too_large"));
        else if (type is not null && Types.ContainsKey(type) && !LooksLike(type, content)) errors.Add(new FieldError("file", "content_type_mismatch"));
        if (errors.Count > 0) return ConfigResult<AttachmentView>.Invalid(errors);

        var id = Guid.NewGuid();
        var storageName = AttachmentFileStore.NewName();
        var hash = Convert.ToHexStringLower(SHA256.HashData(content!));
        var (table, idCol) = AttachmentSubjects.Kinds[subjectKind!];
        // cheap, non-locking check first: nothing is encrypted or written for a record that does not exist
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        await using (var pre = new NpgsqlCommand($"SELECT 1 FROM {table} WHERE {idCol} = @s", conn))
        {
            pre.Parameters.AddWithValue("s", subjectId!.Value);
            if (await pre.ExecuteScalarAsync(ct) is null) return ConfigResult<AttachmentView>.Invalid([new FieldError("subjectId", "subject_not_found")]);
        }
        // the file is encrypted and written BEFORE the transaction, so no row lock is held during file I/O; a failed or ambiguous commit removes it only when no row exists (an orphan left by a crash is removed by the sweep)
        string? keyId = null; byte[] stored = content!;
        try { if (sensitive) { stored = encryptor.Encrypt(content!, Context(companyId, id)); keyId = EnvelopeEncryptor.KeyIdOf(stored); } store.Create(companyId, storageName, stored); }
        catch (EncryptionUnavailableException ex) { return ConfigResult<AttachmentView>.Fail(ConfigStatus.Conflict, ex.Code); }
        try
        {
            var r = await Mutate<bool>(companyId, async work =>
            {
                await using (var sq = new NpgsqlCommand($"SELECT 1 FROM {table} WHERE {idCol} = @s FOR SHARE", work.Connection, work.Transaction))
                {
                    sq.Parameters.AddWithValue("s", subjectId!.Value);
                    if (await sq.ExecuteScalarAsync(ct) is null) throw Invalid("subjectId", "subject_not_found");
                }
                var now = Now;
                await using (var ins = new NpgsqlCommand($"INSERT INTO {AT.Table} ({AT.Id}, {AT.SubjectKind}, {AT.SubjectId}, {AT.FileName}, {AT.ContentType}, {AT.SizeBytes}, {AT.Sha256}, {AT.Sensitivity}, {AT.StorageName}, {AT.KeyId}, {AT.State}, {AT.Revision}, {AT.CreatedBy}, {AT.CreatedAtUtc}, {AT.UpdatedAtUtc}) " +
                    "VALUES (@id, @k, @s, @n, @t, @z, @h, @v, @sn, @key, 1, 1, @by, @now, @now)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("k", subjectKind!); ins.Parameters.AddWithValue("s", subjectId!.Value); ins.Parameters.AddWithValue("n", name!); ins.Parameters.AddWithValue("t", type!);
                    ins.Parameters.AddWithValue("z", (long)content!.Length); ins.Parameters.AddWithValue("h", hash); ins.Parameters.AddWithValue("v", (short)(sensitive ? 2 : 1)); ins.Parameters.AddWithValue("sn", storageName);
                    ins.Parameters.AddWithValue("key", (object?)keyId ?? DBNull.Value); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                // the plaintext digest of a SENSITIVE file is not copied into the audit trail (for a low-entropy document it would be a confirmation oracle)
                await work.AppendAuditAsync(new AuditEventData(AttachmentAuditEvents.Uploaded, ActorUserId: actor, SubjectId: id, Detail: $"attachment uploaded kind={subjectKind} record={subjectId:N} size={content.Length} sha256={(sensitive ? "withheld" : hash[..16])} sensitive={sensitive}"), ct);
                return true;
            }, ct);
            if (!r.IsOk) { await CleanupIfNoRow(companyId, id, storageName); return ConfigResult<AttachmentView>.Fail(r.Status, r.Code!) with { Fields = r.Fields }; }
        }
        catch { await CleanupIfNoRow(companyId, id, storageName); throw; }
        return ConfigResult<AttachmentView>.Ok((await GetAsync(companyId, id, ct))!);
    }

    private async Task CleanupIfNoRow(Guid companyId, Guid id, string storageName)
    {
        try
        {
            await using var conn = await resolver.OpenAsync(companyId, CancellationToken.None);
            await using var q = new NpgsqlCommand($"SELECT count(*) FROM {AT.Table} WHERE {AT.Id} = @id", conn);
            q.Parameters.AddWithValue("id", id);
            if ((long)(await q.ExecuteScalarAsync())! == 0) store.Delete(companyId, storageName);           // an ambiguous commit that DID commit keeps its file
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException) { /* the orphan sweep removes it later */ }
    }

    /// <summary>
    /// Removes stored files that no row names and that are older than <paramref name="minAge"/> (a crash between writing a file and committing its row leaves one; the age keeps a file that is being uploaded right now).
    /// Returns the number removed; audited. Rows and their files are never touched.
    /// </summary>
    public async Task<int> SweepOrphansAsync(Guid actor, Guid companyId, TimeSpan minAge, CancellationToken ct = default)
    {
        HashSet<string> known;
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        await using (var q = new NpgsqlCommand($"SELECT {AT.StorageName} FROM {AT.Table}", conn))
        {
            known = [];
            await using var rd = await q.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) known.Add(rd.GetString(0));
        }
        var cutoff = DateTime.UtcNow - minAge; var removed = 0;
        foreach (var (name, written) in store.List(companyId))
        {
            if (known.Contains(name) || written > cutoff) continue;
            // re-check right before deleting: a row may have been committed since the listing
            await using var conn = await resolver.OpenAsync(companyId, ct);
            await using var q = new NpgsqlCommand($"SELECT count(*) FROM {AT.Table} WHERE {AT.StorageName} = @n", conn);
            q.Parameters.AddWithValue("n", name);
            if ((long)(await q.ExecuteScalarAsync(ct))! == 0 && store.Delete(companyId, name)) removed++;
        }
        if (removed > 0) await transactions.ExecuteAsync(companyId, async work => { await work.AppendAuditAsync(new AuditEventData(AttachmentAuditEvents.OrphansSwept, ActorUserId: actor, Detail: $"orphan attachment files removed count={removed}"), ct); return true; }, ct);
        return removed;
    }

    /// <summary>Reads the file. The hash of the plaintext is verified; a mismatch or a missing file is an integrity failure (audited) and nothing is returned. A sensitive download is audited.</summary>
    public async Task<ConfigResult<AttachmentContent>> OpenAsync(Guid actor, Guid companyId, Guid id, bool allowSensitive, CancellationToken ct = default)
    {
        (AttachmentView View, string StorageName, string? KeyId) row;
        await using (var conn = await resolver.OpenAsync(companyId, ct))
            row = (await Load(conn, null, $"{AT.Id} = @id", c => c.Parameters.AddWithValue("id", id), false, ct)).SingleOrDefault();
        if (row.View is null || row.View.IsRemoved) return ConfigResult<AttachmentContent>.Fail(ConfigStatus.NotFound, "attachment_not_found");
        if (row.View.IsSensitive && !allowSensitive) return ConfigResult<AttachmentContent>.Fail(ConfigStatus.Forbidden, "forbidden");
        byte[] plain;
        try
        {
            byte[]? raw;
            try { raw = store.Read(companyId, row.StorageName); }
            catch (IOException) { return ConfigResult<AttachmentContent>.Fail(ConfigStatus.Conflict, "file_unavailable"); }       // transient (for example a concurrent re-wrap): not an integrity accusation
            if (raw is null) return await Failure(actor, companyId, row.View, "file_missing", ct);
            try { plain = row.View.IsSensitive ? encryptor.Decrypt(raw, Context(companyId, id)) : raw; }
            catch (EncryptionUnavailableException ex) when (ex.Code is not ("encryption_key_unavailable" or "encryption_key_invalid" or "encryption_not_configured")) { return await Failure(actor, companyId, row.View, "decryption_failed", ct); }   // tampered, swapped or truncated ciphertext
            catch (EncryptionUnavailableException ex) { return ConfigResult<AttachmentContent>.Fail(ConfigStatus.Conflict, ex.Code); }
            catch (CryptographicException) { return await Failure(actor, companyId, row.View, "decryption_failed", ct); }
        }
        catch (EncryptionUnavailableException ex) { return ConfigResult<AttachmentContent>.Fail(ConfigStatus.Conflict, ex.Code); }
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(plain)), row.View.Sha256, StringComparison.Ordinal)) return await Failure(actor, companyId, row.View, "hash_mismatch", ct);
        if (row.View.IsSensitive)
            await transactions.ExecuteAsync(companyId, async work => { await work.AppendAuditAsync(new AuditEventData(AttachmentAuditEvents.SensitiveDownloaded, ActorUserId: actor, SubjectId: id, Detail: $"sensitive attachment downloaded kind={row.View.SubjectKind} record={row.View.SubjectId:N}"), ct); return true; }, ct);
        return ConfigResult<AttachmentContent>.Ok(new AttachmentContent(row.View, plain));
    }

    private async Task<ConfigResult<AttachmentContent>> Failure(Guid actor, Guid companyId, AttachmentView v, string reason, CancellationToken ct)
    {
        await transactions.ExecuteAsync(companyId, async work => { await work.AppendAuditAsync(new AuditEventData(AttachmentAuditEvents.IntegrityFailure, ActorUserId: actor, SubjectId: v.Id, Detail: $"attachment integrity failure reason={reason} kind={v.SubjectKind} record={v.SubjectId:N}"), ct); return true; }, ct);
        return ConfigResult<AttachmentContent>.Fail(ConfigStatus.Conflict, "integrity_failure");
    }

    /// <summary>Removes an attachment from use (revision-fenced, a reason is required). The metadata row and the stored bytes are kept; the file is no longer readable through the application.</summary>
    public async Task<ConfigResult<AttachmentView>> RemoveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, bool allowSensitive, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<AttachmentView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) return ConfigResult<AttachmentView>.Invalid([new FieldError("reason", "required")]);
        if (why.Length > 500) return ConfigResult<AttachmentView>.Invalid([new FieldError("reason", "too_long")]);
        var r = await Mutate<bool>(companyId, async work =>
        {
            var cur = (await Load(work.Connection, work.Transaction, $"{AT.Id} = @id", c => c.Parameters.AddWithValue("id", id), true, ct)).SingleOrDefault();
            if (cur.View is null) throw new Abort(ConfigStatus.NotFound, "attachment_not_found");
            if (cur.View.IsSensitive && !allowSensitive) throw new Abort(ConfigStatus.Forbidden, "forbidden");
            if (cur.View.IsRemoved) throw new Abort(ConfigStatus.Conflict, "already_removed");
            if (cur.View.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.View.Revision);
            await using (var upd = new NpgsqlCommand($"UPDATE {AT.Table} SET {AT.State} = 2, {AT.RemovedBy} = @by, {AT.RemovedAtUtc} = @now, {AT.RemovalReason} = @why, {AT.Revision} = {AT.Revision} + 1, {AT.UpdatedAtUtc} = @now WHERE {AT.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("now", Now); upd.Parameters.AddWithValue("why", why);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(AttachmentAuditEvents.Removed, ActorUserId: actor, SubjectId: id, Detail: $"attachment removed kind={cur.View.SubjectKind} record={cur.View.SubjectId:N} sensitive={cur.View.IsSensitive}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<AttachmentView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        return ConfigResult<AttachmentView>.Ok((await GetAsync(companyId, id, ct))!);
    }

    /// <summary>
    /// Key rotation: re-wraps the data key of every ACTIVE sensitive attachment whose envelope is not under the active master key (the file bytes are replaced atomically, the key id recorded and audited).
    /// The envelope itself names its key, so a crash between the file replacement and the database update leaves a readable file. Nothing is decrypted to disk and no plaintext is retained.
    /// </summary>
    public async Task<RewrapOutcome> RewrapAsync(Guid actor, Guid companyId, CancellationToken ct = default)
    {
        string active = encryptor.ActiveKeyId;
        List<(AttachmentView View, string StorageName, string? KeyId)> rows;
        await using (var conn = await resolver.OpenAsync(companyId, ct))
            rows = await Load(conn, null, $"{AT.Sensitivity} = 2 AND {AT.State} = 1", _ => { }, false, ct);
        int done = 0, skipped = 0, failed = 0;
        foreach (var row in rows)
        {
            try
            {
                var raw = store.Read(companyId, row.StorageName);
                if (raw is null) { failed++; await RewrapFailure(actor, companyId, row.View.Id, "file_missing"); continue; }
                if (EnvelopeEncryptor.KeyIdOf(raw) == active && row.KeyId == active) { skipped++; continue; }
                var rewrapped = encryptor.Rewrap(raw, Context(companyId, row.View.Id));
                store.Replace(companyId, row.StorageName, rewrapped);
                var r = await Mutate<bool>(companyId, async work =>
                {
                    await using var upd = new NpgsqlCommand($"UPDATE {AT.Table} SET {AT.KeyId} = @k, {AT.Revision} = {AT.Revision} + 1, {AT.UpdatedAtUtc} = @now WHERE {AT.Id} = @id AND {AT.State} = 1", work.Connection, work.Transaction);
                    upd.Parameters.AddWithValue("k", active); upd.Parameters.AddWithValue("now", Now); upd.Parameters.AddWithValue("id", row.View.Id);
                    if (await upd.ExecuteNonQueryAsync(ct) == 0) return false;
                    await work.AppendAuditAsync(new AuditEventData(AttachmentAuditEvents.Rewrapped, ActorUserId: actor, SubjectId: row.View.Id, Detail: $"attachment key rewrapped from={row.KeyId ?? "none"} to={active}"), ct);
                    return true;
                }, ct);
                if (r.IsOk && r.Value) done++; else { failed++; await RewrapFailure(actor, companyId, row.View.Id, r.IsOk ? "row_changed" : (r.Code ?? "failed")); }
            }
            catch (Exception ex) when (ex is CryptographicException or EncryptionUnavailableException or IOException) { failed++; await RewrapFailure(actor, companyId, row.View.Id, ex is EncryptionUnavailableException eu ? eu.Code : ex.GetType().Name); }
        }
        int retained;
        await using (var conn = await resolver.OpenAsync(companyId, ct))
            retained = (await Load(conn, null, $"{AT.Sensitivity} = 2 AND {AT.State} <> 1", _ => { }, false, ct)).Count(r => r.KeyId is null || r.KeyId != active);
        return new RewrapOutcome(done, skipped, failed, retained);
    }

    private async Task RewrapFailure(Guid actor, Guid companyId, Guid id, string reason)
    {
        try { await transactions.ExecuteAsync(companyId, async work => { await work.AppendAuditAsync(new AuditEventData(AttachmentAuditEvents.RewrapFailed, ActorUserId: actor, SubjectId: id, Detail: $"attachment key rewrap failed reason={reason}"), CancellationToken.None); return true; }, CancellationToken.None); }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { /* the failure count of the outcome still reports it */ }
    }

    /// <summary>
    /// Verifies EVERY attachment row (active and removed: removed bytes are retained) against the stored bytes: existence, decryption with an available key, plaintext hash. One file is held in memory at a time.
    /// Orphan files (named by no row) are counted, never touched. Read-only apart from one audit event with the counts.
    /// </summary>
    public async Task<AttachmentVerification> VerifyAsync(Guid actor, Guid companyId, CancellationToken ct = default)
    {
        List<(AttachmentView View, string StorageName, string? KeyId)> rows;
        await using (var conn = await resolver.OpenAsync(companyId, ct)) rows = await Load(conn, null, "true", _ => { }, false, ct);
        var missing = new List<Guid>(); var mismatch = new List<Guid>(); var keys = new List<Guid>(); var unreadable = new List<Guid>();
        var keyIds = new SortedSet<string>(StringComparer.Ordinal); int ok = 0, sensitive = 0; long bytes = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            bytes += row.View.SizeBytes;
            if (row.View.IsSensitive) { sensitive++; if (row.KeyId is not null) keyIds.Add(row.KeyId); }
            byte[]? raw;
            try { raw = store.Read(companyId, row.StorageName); }
            catch (IOException) { unreadable.Add(row.View.Id); continue; }
            if (raw is null) { missing.Add(row.View.Id); continue; }
            byte[] plain;
            if (row.View.IsSensitive)
            {
                try { keyIds.Add(EnvelopeEncryptor.KeyIdOf(raw)); plain = encryptor.Decrypt(raw, Context(companyId, row.View.Id)); }
                catch (EncryptionUnavailableException ex) when (ex.Code is "encryption_key_unavailable" or "encryption_key_invalid" or "encryption_not_configured") { keys.Add(row.View.Id); continue; }
                catch (Exception ex) when (ex is EncryptionUnavailableException or CryptographicException or FormatException or ArgumentException) { mismatch.Add(row.View.Id); continue; }
            }
            else plain = raw;
            if (string.Equals(Convert.ToHexStringLower(SHA256.HashData(plain)), row.View.Sha256, StringComparison.Ordinal)) ok++; else mismatch.Add(row.View.Id);
        }
        var known = rows.Select(r => r.StorageName).ToHashSet();
        var orphans = store.List(companyId).Count(f => !known.Contains(f.Name));
        var result = new AttachmentVerification(rows.Count, ok, sensitive, bytes, missing, mismatch, keys, unreadable, orphans, [.. keyIds]);
        await transactions.ExecuteAsync(companyId, async work => { await work.AppendAuditAsync(new AuditEventData(AttachmentAuditEvents.Verified, ActorUserId: actor,
            Detail: $"attachments verified checked={result.Checked} ok={result.Ok} missing={missing.Count} hash_mismatch={mismatch.Count} key_unavailable={keys.Count} unreadable={unreadable.Count} orphans={orphans}"), ct); return true; }, ct);
        return result;
    }

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }
    }
}

/// <summary>Permission-checked entry: <c>company.attachments.view|upload|delete</c>; sensitive files additionally need <c>company.attachmentssensitive.view|upload</c> (company-wide). A user without the sensitive permission neither sees nor opens nor removes a sensitive attachment.</summary>
public sealed class AttachmentManagementService(AccessService access, AttachmentService attachments)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Can(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);

    /// <summary>The view permission of the module that owns the record kind: an attachment is never reachable by someone who may not see the record it belongs to.</summary>
    private static string? OwnerView(string? kind) => kind switch { "local_party" => PermissionCatalog.Codes.PartiesView, "inventory_item" => PermissionCatalog.Codes.ItemsView, _ => null };
    private async Task<bool> CanSeeOwner(Guid user, Guid company, string? kind, CancellationToken ct) => OwnerView(kind) is { } code ? await Can(user, company, code, ct) : true;   // an unknown kind is refused by validation

    public async Task<AttachmentCapabilities> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var e = await access.GetEffectivePermissionsAsync(user, company, ct);
        var c = PermissionCatalog.Codes.AttachmentsView;
        return new AttachmentCapabilities(e.HasCompanyWide(c), e.HasCompanyWide(PermissionCatalog.Codes.AttachmentsUpload), e.HasCompanyWide(PermissionCatalog.Codes.AttachmentsDelete),
            e.HasCompanyWide(PermissionCatalog.Codes.AttachmentsSensitiveView), e.HasCompanyWide(PermissionCatalog.Codes.AttachmentsSensitiveUpload));
    }

    public async Task<ConfigResult<IReadOnlyList<AttachmentView>>> ListAsync(Guid user, Guid company, string subjectKind, Guid subjectId, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.AttachmentsView, ct) || !await CanSeeOwner(user, company, subjectKind, ct)) return Denied<IReadOnlyList<AttachmentView>>();
        var sensitive = await Can(user, company, PermissionCatalog.Codes.AttachmentsSensitiveView, ct);
        return ConfigResult<IReadOnlyList<AttachmentView>>.Ok(await attachments.ListAsync(company, subjectKind, subjectId, sensitive, includeRemoved: false, ct));
    }

    public async Task<ConfigResult<AttachmentView>> UploadAsync(Guid user, Guid company, string? subjectKind, Guid? subjectId, string? fileName, string? contentType, byte[]? content, bool sensitive, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.AttachmentsUpload, ct) || !await CanSeeOwner(user, company, subjectKind, ct)) return Denied<AttachmentView>();
        if (sensitive && !await Can(user, company, PermissionCatalog.Codes.AttachmentsSensitiveUpload, ct)) return Denied<AttachmentView>();
        return await attachments.UploadAsync(user, company, subjectKind, subjectId, fileName, contentType, content, sensitive, ct);
    }

    public async Task<ConfigResult<AttachmentContent>> OpenAsync(Guid user, Guid company, Guid id, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.AttachmentsView, ct)) return Denied<AttachmentContent>();
        if (await attachments.GetAsync(company, id, ct) is { } meta && !await CanSeeOwner(user, company, meta.SubjectKind, ct)) return Denied<AttachmentContent>();
        var sensitive = await Can(user, company, PermissionCatalog.Codes.AttachmentsSensitiveView, ct);
        var r = await attachments.OpenAsync(user, company, id, sensitive, ct);
        // a sensitive file the caller may not read looks exactly like a missing one (no existence probing)
        return r.Status == ConfigStatus.Forbidden ? ConfigResult<AttachmentContent>.Fail(ConfigStatus.NotFound, "attachment_not_found") : r;
    }

    /// <summary>Key rotation (re-wraps sensitive files under the active master key). Needs the sensitive-upload permission: it handles the keys of sensitive files.</summary>
    public async Task<ConfigResult<RewrapOutcome>> RewrapAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.AttachmentsSensitiveUpload, ct)) return Denied<RewrapOutcome>();
        return ConfigResult<RewrapOutcome>.Ok(await attachments.RewrapAsync(user, company, ct));
    }

    /// <summary>Verifies the whole attachment set against the stored files (restore proof; also lists the master keys the sensitive files need). Same permission as the key rotation.</summary>
    public async Task<ConfigResult<AttachmentVerification>> VerifyAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.AttachmentsSensitiveUpload, ct)) return Denied<AttachmentVerification>();
        return ConfigResult<AttachmentVerification>.Ok(await attachments.VerifyAsync(user, company, ct));
    }

    /// <summary>Removes stored files no row names (left by a crash between the file write and the commit). Same permission as the key rotation.</summary>
    public async Task<ConfigResult<int>> SweepOrphansAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.AttachmentsSensitiveUpload, ct)) return Denied<int>();
        return ConfigResult<int>.Ok(await attachments.SweepOrphansAsync(user, company, TimeSpan.FromHours(1), ct));
    }

    public async Task<ConfigResult<AttachmentView>> RemoveAsync(Guid user, Guid company, Guid id, long? revision, string? reason, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.AttachmentsDelete, ct)) return Denied<AttachmentView>();
        if (await attachments.GetAsync(company, id, ct) is { } meta && !await CanSeeOwner(user, company, meta.SubjectKind, ct)) return Denied<AttachmentView>();
        var sensitive = await Can(user, company, PermissionCatalog.Codes.AttachmentsSensitiveView, ct);
        var r = await attachments.RemoveAsync(user, company, id, revision, reason, sensitive, ct);
        return r.Status == ConfigStatus.Forbidden ? ConfigResult<AttachmentView>.Fail(ConfigStatus.NotFound, "attachment_not_found") : r;
    }
}
