using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using TD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxDefinition;
using TR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxRateVersion;
using TC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxClass;
using TI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxItemClass;
using TP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxPartyClass;
using TU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxRule;
using TB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxRuleBase;
using TV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxPolicyVersion;

namespace Hasm.Infrastructure.Taxes;

/// <summary>
/// The audited company-side store of the tax configuration (Phase 3.10 slice 2b): taxes, DATED rate versions (append-only, each strictly later than the one before), item and party
/// classes with their assignments, rules and the versioned rounding policy. <b>No authorization here by design:</b> <see cref="TaxManagementService"/> wraps it. Every write is one company
/// transaction with its audit event; taxes, classes and rules advance by revision-fenced updates and are never removed (PostgreSQL refuses it). Nothing here touches a document: a final
/// document stores an immutable snapshot, so no change of this configuration can alter it. <see cref="ResolveAsync"/> feeds the pure <see cref="TaxResolver"/> from these rows.
/// </summary>
public sealed partial class TaxConfigService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }

    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, code, [new FieldError(field, code)]);

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,29}$")] private static partial Regex CodePattern();
    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,39}$")] private static partial Regex KeyPattern();

    /// <summary>A rate version may be dated at most this many days ahead of today (planned changes), because versions can never be removed or reordered.</summary>
    public const int MaxRateLeadDays = 366;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static object Db<T>(T? v) where T : class => (object?)v ?? DBNull.Value;
    private static object Db<T>(T? v, bool _) where T : struct => v is null ? DBNull.Value : v.Value;

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is TD.UqCode or TC.UqCode)
        {
            return ConfigResult<T>.Invalid([new FieldError("code", "code_taken")]);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is TR.UqVersion or TV.UqVersion)
        {
            return ConfigResult<T>.Fail(ConfigStatus.Conflict, "version_conflict");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException)
        {
            return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation");   // a database guard refused a racing or inconsistent change: nothing was written
        }
    }

    private static (bool Ok, List<FieldError> Errors, string Code, string NameAr, string? NameEn) TryNames(string? code, string? nameAr, string? nameEn)
    {
        var errors = new List<FieldError>();
        var c = code?.Trim().ToUpperInvariant() ?? "";
        if (!CodePattern().IsMatch(c)) errors.Add(new FieldError("code", "code_invalid"));
        var ar = nameAr?.Trim() ?? "";
        if (ar.Length is < 1 or > 200) errors.Add(new FieldError("nameAr", "name_invalid"));
        var en = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
        if (en is { Length: > 200 }) errors.Add(new FieldError("nameEn", "name_invalid"));
        return (errors.Count == 0, errors, c, ar, en);
    }

    // ================================================================================================================ taxes and rate versions

    private static async Task<List<TaxRateRecord>> LoadRates(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? taxId, CancellationToken ct)
    {
        var rates = new List<TaxRateRecord>();
        await using var q = new NpgsqlCommand($"SELECT {TR.TaxId}, {TR.EffectiveFrom}, {TR.Treatment}, {TR.RatePercent} FROM {TR.Table}{(taxId is null ? "" : $" WHERE {TR.TaxId} = @t")} ORDER BY {TR.TaxId}, {TR.EffectiveFrom}", conn, tx);
        if (taxId is { } t) q.Parameters.AddWithValue("t", t);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rates.Add(new TaxRateRecord(r.GetGuid(0), r.GetFieldValue<DateOnly>(1), (TaxTreatment)r.GetInt16(2), r.GetDecimal(3)));
        return rates;
    }

    private static async Task<List<TaxView>> LoadTaxes(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var rows = new List<(Guid Id, string Code, string Ar, string? En, bool Active, long Rev, DateTimeOffset Upd)>();
        await using (var q = new NpgsqlCommand($"SELECT {TD.Id}, {TD.Code}, {TD.NameAr}, {TD.NameEn}, {TD.IsActive}, {TD.Revision}, {TD.UpdatedAtUtc} FROM {TD.Table}{(id is null ? "" : $" WHERE {TD.Id} = @id")} ORDER BY {TD.Code}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            if (id is { } g) q.Parameters.AddWithValue("id", g);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetInt64(5), r.GetFieldValue<DateTimeOffset>(6)));
        }
        var rates = (await LoadRates(conn, tx, id, ct)).ToLookup(x => x.TaxId);
        return [.. rows.Select(x => new TaxView(x.Id, x.Code, x.Ar, x.En, x.Active, x.Rev, [.. rates[x.Id]], x.Upd))];
    }

    public async Task<IReadOnlyList<TaxView>> ListTaxesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadTaxes(conn, null, null, false, ct);
    }

    public async Task<TaxView?> GetTaxAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadTaxes(conn, null, id, false, ct)).SingleOrDefault();
    }

    public async Task<ConfigResult<TaxView>> CreateTaxAsync(Guid actor, Guid companyId, TaxInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<TaxView>.Invalid([new FieldError("input", "required")]);
        var n = TryNames(input.Code, input.NameAr, input.NameEn);
        if (!n.Ok) return ConfigResult<TaxView>.Invalid(n.Errors);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {TD.Table} ({TD.Id}, {TD.Code}, {TD.NameAr}, {TD.NameEn}, {TD.IsActive}, {TD.Revision}, {TD.CreatedBy}, {TD.CreatedAtUtc}, {TD.UpdatedAtUtc}) VALUES (@id, @c, @ar, @en, @a, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("c", n.Code); ins.Parameters.AddWithValue("ar", n.NameAr); ins.Parameters.AddWithValue("en", Db(n.NameEn));
                ins.Parameters.AddWithValue("a", input.IsActive ?? true); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(TaxAuditEvents.TaxCreated, ActorUserId: actor, SubjectId: id, Detail: $"tax created code={n.Code}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<TaxView>.Ok((await GetTaxAsync(companyId, id, ct))!) : ConfigResult<TaxView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    /// <summary>Changes the names and the active flag only (the code and identity are immutable; the rate is changed by a new dated version).</summary>
    public async Task<ConfigResult<TaxView>> UpdateTaxAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, TaxInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<TaxView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<TaxView>.Invalid([new FieldError("input", "required")]);
        var n = TryNames(input.Code ?? "A", input.NameAr, input.NameEn);
        if (n.Errors.Any(e => e.Field != "code")) return ConfigResult<TaxView>.Invalid([.. n.Errors.Where(e => e.Field != "code")]);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadTaxes(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "tax_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (input.Code is { } c && !string.Equals(c.Trim(), cur.Code, StringComparison.OrdinalIgnoreCase)) throw Invalid("code", "code_immutable");
            await using (var upd = new NpgsqlCommand($"UPDATE {TD.Table} SET {TD.NameAr} = @ar, {TD.NameEn} = @en, {TD.IsActive} = @a, {TD.Revision} = {TD.Revision} + 1, {TD.UpdatedAtUtc} = @now WHERE {TD.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("ar", n.NameAr); upd.Parameters.AddWithValue("en", Db(n.NameEn)); upd.Parameters.AddWithValue("a", input.IsActive ?? cur.IsActive); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(TaxAuditEvents.TaxUpdated, ActorUserId: actor, SubjectId: id, Detail: $"tax updated r{cur.Revision + 1} code={cur.Code} active={input.IsActive ?? cur.IsActive}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<TaxView>.Ok((await GetTaxAsync(companyId, id, ct))!) : ConfigResult<TaxView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
    }

    /// <summary>
    /// Appends a dated rate version. It must be STRICTLY LATER than every earlier version of the tax (history is never rewritten; a document dated in the past keeps resolving to the rate
    /// that was in force), non-Standard treatments have rate 0 and the rate has at most 6 decimals. The tax row is locked so concurrent versions are ordered.
    /// </summary>
    public async Task<ConfigResult<TaxView>> AddRateAsync(Guid actor, Guid companyId, Guid taxId, TaxRateInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<TaxView>.Invalid([new FieldError("input", "required")]);
        var errors = new List<FieldError>();
        if (!Enum.IsDefined(input.Treatment)) errors.Add(new FieldError("treatment", "treatment_invalid"));
        if (input.RatePercent < 0 || input.RatePercent > 1000 || decimal.Round(input.RatePercent, 6) != input.RatePercent) errors.Add(new FieldError("ratePercent", "rate_invalid"));
        else if (input.Treatment != TaxTreatment.Standard && input.RatePercent != 0) errors.Add(new FieldError("ratePercent", "treatment_rate_mismatch"));
        if (errors.Count > 0) return ConfigResult<TaxView>.Invalid(errors);
        var r = await Mutate(companyId, async work =>
        {
            var tax = (await LoadTaxes(work.Connection, work.Transaction, taxId, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "tax_not_found");
            if (input.EffectiveFrom > DateOnly.FromDateTime(Now).AddDays(MaxRateLeadDays)) throw Invalid("effectiveFrom", "rate_too_far");   // versions are never removed: a typo years ahead would block every later version
            if (tax.Rates.Count > 0 && tax.Rates.Max(x => x.EffectiveFrom) >= input.EffectiveFrom) throw Invalid("effectiveFrom", "rate_not_later");
            await using (var ins = new NpgsqlCommand($"INSERT INTO {TR.Table} ({TR.Id}, {TR.TaxId}, {TR.EffectiveFrom}, {TR.Treatment}, {TR.RatePercent}, {TR.CreatedBy}, {TR.CreatedAtUtc}) VALUES (@id, @t, @f, @tr, @r, @by, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("t", taxId); ins.Parameters.AddWithValue("f", input.EffectiveFrom);
                ins.Parameters.AddWithValue("tr", (short)input.Treatment); ins.Parameters.AddWithValue("r", input.RatePercent); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(TaxAuditEvents.RateAdded, ActorUserId: actor, SubjectId: taxId,
                Detail: $"tax rate added code={tax.Code} from={input.EffectiveFrom:yyyy-MM-dd} treatment={(short)input.Treatment} rate={input.RatePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<TaxView>.Ok((await GetTaxAsync(companyId, taxId, ct))!) : ConfigResult<TaxView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    // ================================================================================================================ classes and assignments

    private static TaxClassView ReadClass(NpgsqlDataReader r) => new(r.GetGuid(0), (TaxClassKind)r.GetInt16(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.GetInt64(6));
    private const string ClassCols = $"{TC.Id}, {TC.Kind}, {TC.Code}, {TC.NameAr}, {TC.NameEn}, {TC.IsActive}, {TC.Revision}";

    private static async Task<List<TaxClassView>> LoadClasses(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var list = new List<TaxClassView>();
        await using var q = new NpgsqlCommand($"SELECT {ClassCols} FROM {TC.Table}{(id is null ? "" : $" WHERE {TC.Id} = @id")} ORDER BY {TC.Kind}, {TC.Code}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        if (id is { } g) q.Parameters.AddWithValue("id", g);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadClass(r));
        return list;
    }

    public async Task<IReadOnlyList<TaxClassView>> ListClassesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadClasses(conn, null, null, false, ct);
    }

    public async Task<ConfigResult<TaxClassView>> CreateClassAsync(Guid actor, Guid companyId, TaxClassInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<TaxClassView>.Invalid([new FieldError("input", "required")]);
        var n = TryNames(input.Code, input.NameAr, input.NameEn);
        var errors = n.Errors;
        if (!Enum.IsDefined(input.Kind)) errors.Add(new FieldError("kind", "kind_invalid"));
        if (errors.Count > 0) return ConfigResult<TaxClassView>.Invalid(errors);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {TC.Table} ({TC.Id}, {TC.Kind}, {TC.Code}, {TC.NameAr}, {TC.NameEn}, {TC.IsActive}, {TC.Revision}, {TC.CreatedBy}, {TC.CreatedAtUtc}, {TC.UpdatedAtUtc}) VALUES (@id, @k, @c, @ar, @en, @a, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("k", (short)input.Kind); ins.Parameters.AddWithValue("c", n.Code); ins.Parameters.AddWithValue("ar", n.NameAr);
                ins.Parameters.AddWithValue("en", Db(n.NameEn)); ins.Parameters.AddWithValue("a", input.IsActive ?? true); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(TaxAuditEvents.ClassCreated, ActorUserId: actor, SubjectId: id, Detail: $"tax class created kind={(short)input.Kind} code={n.Code}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<TaxClassView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<TaxClassView>.Ok((await LoadClasses(conn, null, id, false, ct)).Single());
    }

    /// <summary>Changes the names and the active flag only (identity, kind and code are immutable). An inactive class can no longer be assigned, rules that name it keep working.</summary>
    public async Task<ConfigResult<TaxClassView>> UpdateClassAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, TaxClassInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<TaxClassView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<TaxClassView>.Invalid([new FieldError("input", "required")]);
        var n = TryNames(input.Code ?? "A", input.NameAr, input.NameEn);
        if (n.Errors.Any(e => e.Field != "code")) return ConfigResult<TaxClassView>.Invalid([.. n.Errors.Where(e => e.Field != "code")]);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadClasses(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "class_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (input.Code is { } c && !string.Equals(c.Trim(), cur.Code, StringComparison.OrdinalIgnoreCase)) throw Invalid("code", "code_immutable");
            if (input.Kind != cur.Kind && Enum.IsDefined(input.Kind) && input.Kind != 0) throw Invalid("kind", "kind_immutable");
            await using (var upd = new NpgsqlCommand($"UPDATE {TC.Table} SET {TC.NameAr} = @ar, {TC.NameEn} = @en, {TC.IsActive} = @a, {TC.Revision} = {TC.Revision} + 1, {TC.UpdatedAtUtc} = @now WHERE {TC.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("ar", n.NameAr); upd.Parameters.AddWithValue("en", Db(n.NameEn)); upd.Parameters.AddWithValue("a", input.IsActive ?? cur.IsActive); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(TaxAuditEvents.ClassUpdated, ActorUserId: actor, SubjectId: id, Detail: $"tax class updated r{cur.Revision + 1} code={cur.Code} active={input.IsActive ?? cur.IsActive}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<TaxClassView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<TaxClassView>.Ok((await LoadClasses(conn, null, id, false, ct)).Single());
    }

    /// <summary>The class of an item (null removes the assignment). The class must exist, be active and be an ITEM class; an item has at most one.</summary>
    public Task<ConfigResult<bool>> AssignItemClassAsync(Guid actor, Guid companyId, Guid itemId, Guid? classId, CancellationToken ct = default) =>
        Assign(actor, companyId, itemId, classId, TaxClassKind.Item, TI.Table, TI.ItemId, TI.ClassId, TI.AssignedBy, TI.AssignedAtUtc, TaxAuditEvents.ItemClassAssigned, "item", ct);

    /// <summary>The class of a local party (null removes the assignment). The class must exist, be active and be a PARTY class; a party has at most one.</summary>
    public Task<ConfigResult<bool>> AssignPartyClassAsync(Guid actor, Guid companyId, Guid partyId, Guid? classId, CancellationToken ct = default) =>
        Assign(actor, companyId, partyId, classId, TaxClassKind.Party, TP.Table, TP.PartyId, TP.ClassId, TP.AssignedBy, TP.AssignedAtUtc, TaxAuditEvents.PartyClassAssigned, "party", ct);

    private async Task<ConfigResult<bool>> Assign(Guid actor, Guid companyId, Guid subject, Guid? classId, TaxClassKind kind, string table, string subjectCol, string classCol, string byCol, string atCol,
        short auditType, string label, CancellationToken ct)
    {
        try
        {
            return ConfigResult<bool>.Ok(await transactions.ExecuteAsync(companyId, async work =>
            {
                if (classId is { } cid)
                {
                    var cls = (await LoadClasses(work.Connection, work.Transaction, cid, false, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "class_not_found");
                    if (cls.Kind != kind) throw Invalid("classId", "class_kind_mismatch");
                    if (!cls.IsActive) throw Invalid("classId", "class_inactive");
                    await using var up = new NpgsqlCommand($"INSERT INTO {table} ({subjectCol}, {classCol}, {byCol}, {atCol}) VALUES (@s, @c, @by, @now) ON CONFLICT ({subjectCol}) DO UPDATE SET {classCol} = EXCLUDED.{classCol}, {byCol} = EXCLUDED.{byCol}, {atCol} = EXCLUDED.{atCol}", work.Connection, work.Transaction);
                    up.Parameters.AddWithValue("s", subject); up.Parameters.AddWithValue("c", cid); up.Parameters.AddWithValue("by", actor); up.Parameters.AddWithValue("now", Now);
                    try { await up.ExecuteNonQueryAsync(ct); }
                    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { throw new Abort(ConfigStatus.NotFound, $"{label}_not_found"); }
                }
                else
                {
                    await using var del = new NpgsqlCommand($"DELETE FROM {table} WHERE {subjectCol} = @s", work.Connection, work.Transaction);
                    del.Parameters.AddWithValue("s", subject);
                    if (await del.ExecuteNonQueryAsync(ct) == 0) throw new Abort(ConfigStatus.NotFound, "assignment_not_found");
                }
                await work.AppendAuditAsync(new AuditEventData(auditType, ActorUserId: actor, SubjectId: subject, Detail: $"{label} tax class {(classId is null ? "removed" : $"assigned class={classId:N}")}"), ct);
                return true;
            }, ct));
        }
        catch (Abort a) { return a.To<bool>(); }
    }

    // ================================================================================================================ rules

    private const string RuleCols = $"{TU.Id}, {TU.DocumentType}, {TU.Operation}, {TU.ItemClassId}, {TU.PartyClassId}, {TU.TaxId}, {TU.Priority}, {TU.ValidFrom}, {TU.ValidTo}, {TU.IsActive}, {TU.TreatmentOverride}, {TU.Revision}";

    private static async Task<List<TaxRuleView>> LoadRules(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, string? documentType, string? operation, CancellationToken ct)
    {
        var rows = new List<(TaxRuleRecord Rule, long Rev)>();
        var where = new List<string>();
        await using (var q = new NpgsqlCommand { Connection = conn, Transaction = tx })
        {
            if (id is { } g) { where.Add($"{TU.Id} = @id"); q.Parameters.AddWithValue("id", g); }
            if (documentType is not null) { where.Add($"{TU.DocumentType} = @d"); q.Parameters.AddWithValue("d", documentType); }
            if (operation is not null) { where.Add($"{TU.Operation} = @o"); q.Parameters.AddWithValue("o", operation); }
            q.CommandText = $"SELECT {RuleCols} FROM {TU.Table}{(where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where))} ORDER BY {TU.DocumentType}, {TU.Operation}, {TU.Priority}, {TU.Id}{(forUpdate ? " FOR UPDATE" : "")}";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((new TaxRuleRecord(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetGuid(5), r.GetInt32(6), [],
                    r.GetFieldValue<DateOnly>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateOnly>(8), r.GetBoolean(9), r.IsDBNull(10) ? null : (TaxTreatment)r.GetInt16(10)), r.GetInt64(11)));
        }
        var bases = new Dictionary<Guid, List<Guid>>();
        await using (var q = new NpgsqlCommand($"SELECT {TB.RuleId}, {TB.IncludedTaxId} FROM {TB.Table} ORDER BY {TB.RuleId}, {TB.IncludedTaxId}", conn, tx))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { if (!bases.TryGetValue(r.GetGuid(0), out var l)) bases[r.GetGuid(0)] = l = []; l.Add(r.GetGuid(1)); }
        }
        return [.. rows.Select(x => new TaxRuleView(x.Rule with { IncludesTaxIds = bases.TryGetValue(x.Rule.Id, out var l) ? l : [] }, x.Rev))];
    }

    public async Task<IReadOnlyList<TaxRuleView>> ListRulesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadRules(conn, null, null, false, null, null, ct);
    }

    private async Task ValidateRule(CompanyAuditedWork work, TaxRuleInput input, Guid? selfId, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        if (!KeyPattern().IsMatch(input.DocumentType?.Trim() ?? "")) errors.Add(new FieldError("documentType", "document_type_invalid"));
        if (!KeyPattern().IsMatch(input.Operation?.Trim() ?? "")) errors.Add(new FieldError("operation", "operation_invalid"));
        if (input.Priority is < 1 or > 1000) errors.Add(new FieldError("priority", "priority_invalid"));
        if (input.ValidTo is { } to && to < input.ValidFrom) errors.Add(new FieldError("validTo", "range_invalid"));
        if (input.TreatmentOverride is { } o && (o == TaxTreatment.Standard || !Enum.IsDefined(o))) errors.Add(new FieldError("treatmentOverride", "treatment_invalid"));
        var includes = input.IncludesTaxIds ?? [];
        if (includes.Count != includes.Distinct().Count() || includes.Contains(input.TaxId)) errors.Add(new FieldError("includesTaxIds", "base_invalid"));
        if (errors.Count > 0) throw new Abort(ConfigStatus.Invalid, "invalid", errors);

        var taxes = (await LoadTaxes(work.Connection, work.Transaction, null, false, ct)).ToDictionary(t => t.Id);
        if (!taxes.TryGetValue(input.TaxId, out var tax)) throw Invalid("taxId", "tax_not_found");
        if (!tax.IsActive && input.IsActive) throw Invalid("taxId", "tax_inactive");   // an inactive tax stays referenced by old rules, which may be switched off but not newly activated
        await LoadTaxes(work.Connection, work.Transaction, input.TaxId, true, ct);          // lock the tax row so concurrent rule writes for it are serialized (overlap and order checks below)
        foreach (var inc in includes) if (!taxes.ContainsKey(inc)) throw Invalid("includesTaxIds", "tax_not_found");
        var classes = (await LoadClasses(work.Connection, work.Transaction, null, false, ct)).ToDictionary(c => c.Id);
        if (input.ItemClassId is { } ic && (!classes.TryGetValue(ic, out var icv) || icv.Kind != TaxClassKind.Item)) throw Invalid("itemClassId", "class_kind_mismatch");
        if (input.PartyClassId is { } pc && (!classes.TryGetValue(pc, out var pcv) || pcv.Kind != TaxClassKind.Party)) throw Invalid("partyClassId", "class_kind_mismatch");

        // two active rules of the same tax, document type, operation and classification that overlap in time would always be ambiguous: refuse them when they are written
        var doc = input.DocumentType!.Trim(); var op = input.Operation!.Trim();
        if (input.IsActive)
        {
            foreach (var other in await LoadRules(work.Connection, work.Transaction, null, false, doc, op, ct))
            {
                var x = other.Rule;
                if (x.Id == selfId || !x.IsActive || x.TaxId != input.TaxId || x.ItemClassId != input.ItemClassId || x.PartyClassId != input.PartyClassId) continue;
                var overlap = x.ValidFrom <= (input.ValidTo ?? DateOnly.MaxValue) && input.ValidFrom <= (x.ValidTo ?? DateOnly.MaxValue);
                if (overlap) throw new Abort(ConfigStatus.Conflict, "rule_overlap");
            }
            // what the resolver would refuse for a line that matches both rules is refused here, when the rule is written
            var peers = (await LoadRules(work.Connection, work.Transaction, null, false, doc, op, ct)).Select(v => v.Rule)
                .Where(x => x.Id != selfId && x.IsActive && x.ValidFrom <= (input.ValidTo ?? DateOnly.MaxValue) && input.ValidFrom <= (x.ValidTo ?? DateOnly.MaxValue)
                            && (x.ItemClassId is null || input.ItemClassId is null || x.ItemClassId == input.ItemClassId) && (x.PartyClassId is null || input.PartyClassId is null || x.PartyClassId == input.PartyClassId)).ToArray();
            var specificity = (input.ItemClassId is null ? 0 : 1) + (input.PartyClassId is null ? 0 : 1);
            if (peers.Any(x => x.TaxId == input.TaxId && ((x.ItemClassId is null ? 0 : 1) + (x.PartyClassId is null ? 0 : 1)) == specificity)) throw new Abort(ConfigStatus.Conflict, "rule_ambiguous");
            if (peers.Any(x => x.TaxId != input.TaxId && x.Priority == input.Priority)) throw new Abort(ConfigStatus.Conflict, "duplicate_priority");
            if (peers.Any(x => includes.Contains(x.TaxId) && x.Priority >= input.Priority) || peers.Any(x => x.IncludesTaxIds!.Contains(input.TaxId) && input.Priority >= x.Priority))
                throw new Abort(ConfigStatus.Conflict, "invalid_base_reference");
        }
    }

    private static async Task ReplaceBase(CompanyAuditedWork work, Guid ruleId, IReadOnlyList<Guid> includes, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand($"DELETE FROM {TB.Table} WHERE {TB.RuleId} = @r", work.Connection, work.Transaction)) { del.Parameters.AddWithValue("r", ruleId); await del.ExecuteNonQueryAsync(ct); }
        foreach (var inc in includes.Distinct())
        {
            await using var ins = new NpgsqlCommand($"INSERT INTO {TB.Table} ({TB.RuleId}, {TB.IncludedTaxId}) VALUES (@r, @t)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("r", ruleId); ins.Parameters.AddWithValue("t", inc);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<ConfigResult<TaxRuleView>> CreateRuleAsync(Guid actor, Guid companyId, TaxRuleInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<TaxRuleView>.Invalid([new FieldError("input", "required")]);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            await ValidateRule(work, input, null, ct);
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {TU.Table} ({TU.Id}, {TU.DocumentType}, {TU.Operation}, {TU.ItemClassId}, {TU.PartyClassId}, {TU.TaxId}, {TU.Priority}, {TU.ValidFrom}, {TU.ValidTo}, {TU.IsActive}, {TU.TreatmentOverride}, {TU.Revision}, {TU.CreatedBy}, {TU.CreatedAtUtc}, {TU.UpdatedAtUtc}) " +
                "VALUES (@id, @d, @o, @ic, @pc, @t, @p, @vf, @vt, @a, @ov, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                Bind(ins, input); ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await ReplaceBase(work, id, input.IncludesTaxIds ?? [], ct);
            await work.AppendAuditAsync(new AuditEventData(TaxAuditEvents.RuleCreated, ActorUserId: actor, SubjectId: id, Detail: $"tax rule created doc={input.DocumentType!.Trim()} op={input.Operation!.Trim()} tax={input.TaxId:N} priority={input.Priority}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<TaxRuleView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<TaxRuleView>.Ok((await LoadRules(conn, null, id, false, null, null, ct)).Single());
    }

    /// <summary>Replaces the editable fields of a rule (revision-fenced). Documents already finalized are unaffected: they hold their own snapshot.</summary>
    public async Task<ConfigResult<TaxRuleView>> UpdateRuleAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, TaxRuleInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<TaxRuleView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<TaxRuleView>.Invalid([new FieldError("input", "required")]);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadRules(work.Connection, work.Transaction, id, true, null, null, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "rule_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            await ValidateRule(work, input, id, ct);
            await using (var upd = new NpgsqlCommand($"UPDATE {TU.Table} SET {TU.DocumentType} = @d, {TU.Operation} = @o, {TU.ItemClassId} = @ic, {TU.PartyClassId} = @pc, {TU.TaxId} = @t, {TU.Priority} = @p, {TU.ValidFrom} = @vf, {TU.ValidTo} = @vt, " +
                $"{TU.IsActive} = @a, {TU.TreatmentOverride} = @ov, {TU.Revision} = {TU.Revision} + 1, {TU.UpdatedAtUtc} = @now WHERE {TU.Id} = @id", work.Connection, work.Transaction))
            {
                Bind(upd, input); upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await ReplaceBase(work, id, input.IncludesTaxIds ?? [], ct);
            await work.AppendAuditAsync(new AuditEventData(TaxAuditEvents.RuleUpdated, ActorUserId: actor, SubjectId: id, Detail: $"tax rule updated r{cur.Revision + 1} doc={input.DocumentType!.Trim()} op={input.Operation!.Trim()} tax={input.TaxId:N} priority={input.Priority} active={input.IsActive}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<TaxRuleView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<TaxRuleView>.Ok((await LoadRules(conn, null, id, false, null, null, ct)).Single());
    }

    private static void Bind(NpgsqlCommand c, TaxRuleInput i)
    {
        c.Parameters.AddWithValue("d", i.DocumentType!.Trim()); c.Parameters.AddWithValue("o", i.Operation!.Trim()); c.Parameters.AddWithValue("ic", Db(i.ItemClassId, true)); c.Parameters.AddWithValue("pc", Db(i.PartyClassId, true));
        c.Parameters.AddWithValue("t", i.TaxId); c.Parameters.AddWithValue("p", i.Priority); c.Parameters.AddWithValue("vf", i.ValidFrom); c.Parameters.AddWithValue("vt", Db(i.ValidTo, true));
        c.Parameters.AddWithValue("a", i.IsActive); c.Parameters.AddWithValue("ov", i.TreatmentOverride is { } o ? (object)(short)o : DBNull.Value);
    }

    // ================================================================================================================ rounding policy

    private static async Task<TaxPolicyView?> LoadPolicy(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {TV.Id}, {TV.Version}, {TV.MoneyDecimals}, {TV.Mode}, {TV.Level}, {TV.CreatedAtUtc} FROM {TV.Table} ORDER BY {TV.Version} DESC LIMIT 1", conn, tx);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new TaxPolicyView(r.GetGuid(0), r.GetInt32(1), new TaxPolicy(r.GetInt16(2), (TaxRoundingMode)r.GetInt16(3), (TaxRoundingLevel)r.GetInt16(4)), r.GetFieldValue<DateTimeOffset>(5)) : null;
    }

    /// <summary>The rounding policy in force (the latest version), or null when the company has not configured one yet (nothing is seeded).</summary>
    public async Task<TaxPolicyView?> GetPolicyAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadPolicy(conn, null, ct);
    }

    /// <summary>Appends a NEW policy version (versions are never edited). The engine rejects what it cannot do, so the combination is validated here with the same rules.</summary>
    public async Task<ConfigResult<TaxPolicyView>> AddPolicyAsync(Guid actor, Guid companyId, TaxPolicyInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<TaxPolicyView>.Invalid([new FieldError("input", "required")]);
        var errors = new List<FieldError>();
        if (input.MoneyDecimals is < 0 or > TaxEngine.MaxMoneyDecimals) errors.Add(new FieldError("moneyDecimals", "decimals_invalid"));
        if (!Enum.IsDefined(input.Mode)) errors.Add(new FieldError("mode", "mode_invalid"));
        if (!Enum.IsDefined(input.Level)) errors.Add(new FieldError("level", "level_invalid"));
        if (errors.Count > 0) return ConfigResult<TaxPolicyView>.Invalid(errors);
        var r = await Mutate(companyId, async work =>
        {
            var cur = await LoadPolicy(work.Connection, work.Transaction, ct);
            var version = (cur?.Version ?? 0) + 1;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {TV.Table} ({TV.Id}, {TV.Version}, {TV.MoneyDecimals}, {TV.Mode}, {TV.Level}, {TV.CreatedBy}, {TV.CreatedAtUtc}) VALUES (@id, @v, @d, @m, @l, @by, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("v", version); ins.Parameters.AddWithValue("d", (short)input.MoneyDecimals);
                ins.Parameters.AddWithValue("m", (short)input.Mode); ins.Parameters.AddWithValue("l", (short)input.Level); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(TaxAuditEvents.PolicyAdded, ActorUserId: actor, Detail: $"tax rounding policy v{version} decimals={input.MoneyDecimals} mode={(short)input.Mode} level={(short)input.Level}"), ct);
            return version;
        }, ct);
        if (!r.IsOk) return ConfigResult<TaxPolicyView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
        return ConfigResult<TaxPolicyView>.Ok((await GetPolicyAsync(companyId, ct))!);
    }

    // ================================================================================================================ resolution

    /// <summary>
    /// Resolves the taxes of ONE line for a document type and operation on a tax date: reads the item's and the party's classes, the dated rate versions and the rules in one read-only
    /// transaction (a consistent view), runs the pure <see cref="TaxResolver"/> and returns the components together with the rounding policy and its version. A refusal of the resolver
    /// or a missing policy is returned as a Conflict with the stable code (<c>no_tax_rate</c>, <c>ambiguous_tax_rule</c>, <c>no_tax_policy</c> ...): nothing is guessed.
    /// </summary>
    public async Task<ConfigResult<TaxResolution>> ResolveAsync(Guid companyId, string documentType, string operation, Guid? itemId, Guid? partyId, DateOnly taxDate, CancellationToken ct = default)
    {
        if (!KeyPattern().IsMatch(documentType ?? "") || !KeyPattern().IsMatch(operation ?? "")) return ConfigResult<TaxResolution>.Fail(ConfigStatus.Invalid, "invalid_tax_context");
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await ro.ExecuteNonQueryAsync(ct);
        Guid? itemClass = null, partyClass = null;
        if (itemId is { } i) itemClass = await ClassOf(conn, tx, TI.Table, TI.ItemId, TI.ClassId, i, ct);
        if (partyId is { } p) partyClass = await ClassOf(conn, tx, TP.Table, TP.PartyId, TP.ClassId, p, ct);
        var taxes = await LoadTaxes(conn, tx, null, false, ct);
        var rules = (await LoadRules(conn, tx, null, false, documentType, operation, ct)).Select(x => x.Rule).ToArray();
        var policy = await LoadPolicy(conn, tx, ct);
        if (policy is null) return ConfigResult<TaxResolution>.Fail(ConfigStatus.Conflict, "no_tax_policy");
        try
        {
            var components = TaxResolver.Resolve(new TaxContext(documentType!, operation!, itemClass, partyClass, taxDate),
                [.. taxes.Select(t => new TaxDefinitionRecord(t.Id, t.Code, t.IsActive))], [.. taxes.SelectMany(t => t.Rates)], rules);
            return ConfigResult<TaxResolution>.Ok(new TaxResolution(components, policy.Policy, policy.Version));
        }
        catch (TaxException ex) { return ConfigResult<TaxResolution>.Fail(ConfigStatus.Conflict, ex.Code); }
    }

    private static async Task<Guid?> ClassOf(NpgsqlConnection conn, NpgsqlTransaction tx, string table, string subjectCol, string classCol, Guid subject, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {classCol} FROM {table} WHERE {subjectCol} = @s", conn, tx);
        q.Parameters.AddWithValue("s", subject);
        return await q.ExecuteScalarAsync(ct) as Guid?;
    }
}

/// <summary>Permission-checked entry to <see cref="TaxConfigService"/>: <c>company.tax.view</c> reads, <c>company.tax.configure</c> writes (company-wide).</summary>
public sealed class TaxManagementService(AccessService access, TaxConfigService taxes)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Can(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);
    private async Task<ConfigResult<T>> Read<T>(Guid user, Guid company, Func<Task<T>> read, CancellationToken ct) =>
        await Can(user, company, PermissionCatalog.Codes.TaxView, ct) ? ConfigResult<T>.Ok(await read()) : Denied<T>();
    private async Task<ConfigResult<T>> Write<T>(Guid user, Guid company, Func<Task<ConfigResult<T>>> write, CancellationToken ct) =>
        await Can(user, company, PermissionCatalog.Codes.TaxConfigure, ct) ? await write() : Denied<T>();

    public async Task<TaxCapabilities> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        return new TaxCapabilities(eff.HasCompanyWide(PermissionCatalog.Codes.TaxView), eff.HasCompanyWide(PermissionCatalog.Codes.TaxConfigure));
    }

    public Task<ConfigResult<IReadOnlyList<TaxView>>> ListTaxesAsync(Guid user, Guid company, CancellationToken ct = default) => Read(user, company, () => taxes.ListTaxesAsync(company, ct), ct);
    public Task<ConfigResult<IReadOnlyList<TaxClassView>>> ListClassesAsync(Guid user, Guid company, CancellationToken ct = default) => Read(user, company, () => taxes.ListClassesAsync(company, ct), ct);
    public Task<ConfigResult<IReadOnlyList<TaxRuleView>>> ListRulesAsync(Guid user, Guid company, CancellationToken ct = default) => Read(user, company, () => taxes.ListRulesAsync(company, ct), ct);
    public Task<ConfigResult<TaxPolicyView?>> GetPolicyAsync(Guid user, Guid company, CancellationToken ct = default) => Read(user, company, () => taxes.GetPolicyAsync(company, ct), ct);

    public Task<ConfigResult<TaxView>> CreateTaxAsync(Guid user, Guid company, TaxInput? input, CancellationToken ct = default) => Write(user, company, () => taxes.CreateTaxAsync(user, company, input, ct), ct);
    public Task<ConfigResult<TaxView>> UpdateTaxAsync(Guid user, Guid company, Guid id, long? revision, TaxInput? input, CancellationToken ct = default) => Write(user, company, () => taxes.UpdateTaxAsync(user, company, id, revision, input, ct), ct);
    public Task<ConfigResult<TaxView>> AddRateAsync(Guid user, Guid company, Guid taxId, TaxRateInput? input, CancellationToken ct = default) => Write(user, company, () => taxes.AddRateAsync(user, company, taxId, input, ct), ct);
    public Task<ConfigResult<TaxClassView>> CreateClassAsync(Guid user, Guid company, TaxClassInput? input, CancellationToken ct = default) => Write(user, company, () => taxes.CreateClassAsync(user, company, input, ct), ct);
    public Task<ConfigResult<TaxClassView>> UpdateClassAsync(Guid user, Guid company, Guid id, long? revision, TaxClassInput? input, CancellationToken ct = default) => Write(user, company, () => taxes.UpdateClassAsync(user, company, id, revision, input, ct), ct);
    public Task<ConfigResult<bool>> AssignItemClassAsync(Guid user, Guid company, Guid itemId, Guid? classId, CancellationToken ct = default) => Write(user, company, () => taxes.AssignItemClassAsync(user, company, itemId, classId, ct), ct);
    public Task<ConfigResult<bool>> AssignPartyClassAsync(Guid user, Guid company, Guid partyId, Guid? classId, CancellationToken ct = default) => Write(user, company, () => taxes.AssignPartyClassAsync(user, company, partyId, classId, ct), ct);
    public Task<ConfigResult<TaxRuleView>> CreateRuleAsync(Guid user, Guid company, TaxRuleInput? input, CancellationToken ct = default) => Write(user, company, () => taxes.CreateRuleAsync(user, company, input, ct), ct);
    public Task<ConfigResult<TaxRuleView>> UpdateRuleAsync(Guid user, Guid company, Guid id, long? revision, TaxRuleInput? input, CancellationToken ct = default) => Write(user, company, () => taxes.UpdateRuleAsync(user, company, id, revision, input, ct), ct);
    public Task<ConfigResult<TaxPolicyView>> AddPolicyAsync(Guid user, Guid company, TaxPolicyInput? input, CancellationToken ct = default) => Write(user, company, () => taxes.AddPolicyAsync(user, company, input, ct), ct);

    /// <summary>Resolving reads the configuration (view permission): the result is what a document would be taxed with, nothing is stored.</summary>
    public async Task<ConfigResult<TaxResolution>> ResolveAsync(Guid user, Guid company, string documentType, string operation, Guid? itemId, Guid? partyId, DateOnly taxDate, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.TaxView, ct) ? await taxes.ResolveAsync(company, documentType, operation, itemId, partyId, taxDate, ct) : Denied<TaxResolution>();
}
