using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using A = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using MV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StatementMappingVersion;
using ME = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StatementMappingEntry;

namespace Hasm.Infrastructure.Reporting;

public sealed record StatementMappingVersionView(Guid Id, int VersionNo, DateTimeOffset CreatedAtUtc, Guid CreatedBy, short Source, string? Note, int EntryCount, string ContentHash);

/// <summary>A mapping version with its entries. <c>Version</c> is null while no mapping exists yet (every account is then unclassified and every statement says so).</summary>
public sealed record StatementMappingView(StatementMappingVersionView? Version, IReadOnlyDictionary<Guid, StatementSection> Entries);

public sealed record StatementMappingEntryInput(Guid? AccountId, string? Section);
public sealed record StatementMappingInput(IReadOnlyList<StatementMappingEntryInput>? Entries, string? Note, string? Source);

/// <summary>
/// The versioned company-level classification of accounts into financial-statement sections. <b>No authorization here by design:</b> <see cref="FinancialStatementManagementService"/> wraps it. Every save creates a NEW
/// immutable version holding the complete mapping (audited, optimistic concurrency by version number, serialized by an advisory lock); an unchanged mapping creates nothing; earlier versions are never touched, so a
/// statement can always be reproduced with the version it was produced with.
/// </summary>
public sealed class StatementMappingService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48534D50;   // "HSMP"
    internal const int LockKey2 = 1;
    public const string SourceManual = "manual", SourceTemplate = "template";

    private sealed class Abort(ConfigStatus status, string code, long? current = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, current);
    }

    /// <summary>The submitted mapping equals the current one: nothing is written and nothing is audited (an audited mutation needs a change).</summary>
    private sealed class Unchanged(StatementMappingView view) : Exception("unchanged") { public StatementMappingView View { get; } = view; }

    private static string Cols => $"{MV.Id}, {MV.VersionNo}, {MV.CreatedAtUtc}, {MV.CreatedBy}, {MV.Source}, {MV.Note}, {MV.EntryCount}, {MV.ContentHash}";

    private static StatementMappingVersionView ReadVersion(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetInt32(1), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), r.GetGuid(3), r.GetInt16(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.GetString(7).Trim());

    // ------------------------------------------------------------------------------------------------------------- reads

    public async Task<IReadOnlyList<StatementMappingVersionView>> ListVersionsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM {MV.Table} ORDER BY {MV.VersionNo} DESC", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<StatementMappingVersionView>();
        while (await r.ReadAsync(ct)) list.Add(ReadVersion(r));
        return list;
    }

    /// <summary>The mapping of a version (null = the current one) on the caller's connection, so a statement reads it from the same snapshot as its figures. Null when that version does not exist.</summary>
    public static async Task<StatementMappingView?> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int? versionNo, CancellationToken ct)
    {
        StatementMappingVersionView? version = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {Cols} FROM {MV.Table} " + (versionNo is null ? $"ORDER BY {MV.VersionNo} DESC LIMIT 1" : $"WHERE {MV.VersionNo} = @v"), conn, tx))
        {
            if (versionNo is { } v) cmd.Parameters.AddWithValue("v", v);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) version = ReadVersion(r);
        }
        if (version is null) return versionNo is null ? new StatementMappingView(null, new Dictionary<Guid, StatementSection>()) : null;
        var entries = new Dictionary<Guid, StatementSection>();
        await using (var cmd = new NpgsqlCommand($"SELECT {ME.AccountId}, {ME.Section} FROM {ME.Table} WHERE {ME.VersionId} = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", version.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) entries[r.GetGuid(0)] = (StatementSection)r.GetInt16(1);
        }
        return new StatementMappingView(version, entries);
    }

    public async Task<StatementMappingView?> GetAsync(Guid companyId, int? versionNo, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var v = await ReadAsync(conn, tx, versionNo, ct);
        await tx.CommitAsync(ct);
        return v;
    }

    public static async Task<List<StatementAccount>> ReadAccountsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var list = new List<StatementAccount>();
        await using var cmd = new NpgsqlCommand($"SELECT {A.Id}, {A.Code}, {A.NameAr}, {A.NameEn}, {A.ParentId}, {A.Type}, {A.Nature}, {A.IsGroup}, {A.TemplateKey} FROM {A.Table} ORDER BY upper({A.Code})", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new StatementAccount(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                (AccountType)r.GetInt16(5), (AccountNature)r.GetInt16(6), r.GetBoolean(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    public async Task<IReadOnlyList<StatementAccount>> AccountsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await ReadAccountsAsync(conn, null, ct);
    }

    /// <summary>The default mapping derived from the starter templates for the company's template accounts (a suggestion: nothing is saved).</summary>
    public async Task<IReadOnlyDictionary<Guid, StatementSection>> ProposeDefaultAsync(Guid companyId, CancellationToken ct = default) =>
        StatementDefaults.Propose(await AccountsAsync(companyId, ct));

    // ------------------------------------------------------------------------------------------------------------- save

    /// <summary>The canonical hash of an entry set (independent of order).</summary>
    public static string HashOf(IEnumerable<KeyValuePair<Guid, StatementSection>> entries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries.Select(e => $"{e.Key:N}:{(short)e.Value}").Order(StringComparer.Ordinal))))).ToLowerInvariant();

    /// <summary>
    /// Saves a complete mapping as a new version. <paramref name="expectedVersion"/> is the version number the caller last saw (0 when none exists): another save in between is refused so nobody overwrites a newer mapping.
    /// </summary>
    public async Task<ConfigResult<StatementMappingView>> SaveAsync(Guid actor, Guid companyId, long? expectedVersion, StatementMappingInput? input, CancellationToken ct = default)
    {
        if (expectedVersion is null) return ConfigResult<StatementMappingView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input?.Entries is null) return ConfigResult<StatementMappingView>.Invalid([new("entries", "required")]);
        var errors = new List<FieldError>();
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note is { Length: > 500 }) errors.Add(new("note", "too_long"));
        var source = (input.Source ?? SourceManual).Trim().ToLowerInvariant();
        if (source is not (SourceManual or SourceTemplate)) errors.Add(new("source", "invalid_value"));
        var parsed = new Dictionary<Guid, StatementSection>();
        for (var i = 0; i < input.Entries.Count; i++)
        {
            var e = input.Entries[i];
            if (e.AccountId is not { } id || id == Guid.Empty) { errors.Add(new($"entries[{i}].accountId", "required")); continue; }
            if (!StatementSections.TryParse(e.Section, out var section)) { errors.Add(new($"entries[{i}].section", "invalid_value")); continue; }
            if (!parsed.TryAdd(id, section)) errors.Add(new($"entries[{i}].accountId", "duplicate_account"));
        }
        if (errors.Count > 0) return ConfigResult<StatementMappingView>.Invalid(errors);
        try
        {
            var saved = await transactions.ExecuteAsync(companyId, async work =>
            {
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
                {
                    lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2);
                    await lk.ExecuteNonQueryAsync(ct);
                }
                var current = await ReadAsync(work.Connection, work.Transaction, null, ct);
                var currentNo = current?.Version?.VersionNo ?? 0;
                if (currentNo != expectedVersion) throw new Abort(ConfigStatus.PreconditionFailed, "mapping_version_conflict", currentNo);
                var accounts = (await ReadAccountsAsync(work.Connection, work.Transaction, ct)).ToDictionary(a => a.Id);
                var fields = new List<FieldError>();
                var index = 0;
                foreach (var (id, section) in parsed)
                {
                    if (!accounts.TryGetValue(id, out var a)) fields.Add(new($"entries[{index}].accountId", "account_not_found"));
                    else if (!StatementSections.Allows(a.Type, section)) fields.Add(new($"entries[{index}].section", "section_not_allowed"));
                    index++;
                }
                if (fields.Count > 0) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: fields);
                var hash = HashOf(parsed);
                if (current?.Version is { } cv && cv.ContentHash == hash) throw new Unchanged(current);          // unchanged: no new version, nothing to audit
                var versionId = Guid.NewGuid(); var no = currentNo + 1; var now = clock.GetUtcNow().UtcDateTime;
                await using (var ins = new NpgsqlCommand($"INSERT INTO {MV.Table} ({MV.Id}, {MV.VersionNo}, {MV.CreatedAtUtc}, {MV.CreatedBy}, {MV.Source}, {MV.Note}, {MV.EntryCount}, {MV.ContentHash}) VALUES (@id, @no, @now, @by, @src, @note, @cnt, @hash)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", versionId); ins.Parameters.AddWithValue("no", no); ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("by", actor);
                    ins.Parameters.AddWithValue("src", (short)(source == SourceTemplate ? 2 : 1)); ins.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
                    ins.Parameters.AddWithValue("cnt", parsed.Count); ins.Parameters.AddWithValue("hash", hash);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                if (parsed.Count > 0)
                {
                    await using var many = new NpgsqlCommand($"INSERT INTO {ME.Table} ({ME.VersionId}, {ME.AccountId}, {ME.Section}) SELECT @v, x.a, x.s FROM unnest(@a, @s) AS x(a, s)", work.Connection, work.Transaction);
                    many.Parameters.AddWithValue("v", versionId);
                    many.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = parsed.Keys.ToArray() });
                    many.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = parsed.Values.Select(v => (short)v).ToArray() });
                    await many.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(ReportAuditEvents.StatementMappingSaved, ActorUserId: actor, Detail: $"statement mapping v{no} entries={parsed.Count} source={source} hash={hash[..12]}"), ct);
                return (await ReadAsync(work.Connection, work.Transaction, no, ct))!;
            }, ct);
            return ConfigResult<StatementMappingView>.Ok(saved);
        }
        catch (Unchanged same) { return ConfigResult<StatementMappingView>.Ok(same.View); }
        catch (Abort abort) { return abort.To<StatementMappingView>(); }
    }
}
