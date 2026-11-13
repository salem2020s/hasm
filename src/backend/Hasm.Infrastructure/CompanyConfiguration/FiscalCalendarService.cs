using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using Y = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using H = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalHistory;
using S = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Settings;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Fiscal calendar domain operations (Phase 3.1D). <b>No authorization here by design:</b> <see cref="FiscalCalendarManagementService"/> wraps it.
/// Scope: the calendar structure and the period state machine ONLY. It creates no journal entry, opening balance or carry-forward: those need the
/// General Ledger and the posting engine, which call <see cref="FiscalPeriodGate"/> to learn whether a date accepts postings.
/// Every mutation runs in <see cref="CompanyAuditedTransactions"/>: the change, its append-only history row and its authenticated audit event commit
/// or roll back together, after ONE transaction-level advisory lock that serialises all fiscal-calendar changes of the company (taken before the
/// audit-stream lock, as the lock order requires). PostgreSQL independently enforces the invariants (see migration 0006).
/// <list type="bullet">
/// <item>Years are exactly 12 months from the first of a month; they never overlap and form one contiguous calendar. Several may be Open at once.</item>
/// <item>Periods are monthly or quarterly, generated with the year, never edited. States: Open, Soft Closed, Final Closed; Open and Final Closed are never adjacent.</item>
/// <item>Closing consults <see cref="PeriodCloseGuard"/>; reopening a Final Closed period or a closed year needs a recorded reason, and a Final Closed
///   period can only be reopened by someone other than the user who final-closed it (segregation of duties).</item>
/// </list>
/// </summary>
public sealed class FiscalCalendarService(
    CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, PeriodCloseGuard closeGuard, TimeProvider clock)
{
    internal const int LockKey1 = 0x48464341;   // "HFCA": advisory-lock namespace of the fiscal calendar
    internal const int LockKey2 = 1;

    private static readonly string YearColumns =
        $"{Y.Id}, {Y.Name}, {Y.StartDate}, {Y.EndDate}, {Y.PeriodType}, {Y.TimeZone}, {Y.Status}, {Y.ClosedAtUtc}, {Y.Revision}, {Y.CreatedAtUtc}";
    private static readonly string PeriodColumns =
        $"{P.Id}, {P.YearId}, {P.Number}, {P.StartDate}, {P.EndDate}, {P.Status}, {P.StatusChangedAtUtc}, {P.Revision}, {P.FinalClosedBy}";

    public IReadOnlyList<string> UnimplementedCloseChecks => closeGuard.UnimplementedDomains;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
    }

    // ------------------------------------------------------------------------------------------------------------- reads

    public async Task<IReadOnlyList<FiscalYearView>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadYearsAsync(conn, null, null, false, ct);
    }

    public async Task<FiscalYearView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadYearsAsync(conn, null, id, false, ct)).SingleOrDefault();
    }

    /// <summary>The period containing a calendar date of the company, or null (no period covers it).</summary>
    public async Task<FiscalPeriodView?> FindPeriodAsync(Guid companyId, DateOnly date, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"SELECT {PeriodColumns} FROM {P.Table} WHERE @d BETWEEN {P.StartDate} AND {P.EndDate}", conn);
        cmd.Parameters.AddWithValue("d", date);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadPeriod(r).View : null;
    }

    /// <summary>"Today" in the company's business time zone (null until the time zone is configured: nothing is ever defaulted).</summary>
    public async Task<DateOnly?> TodayAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var tz = await ReadTimeZoneAsync(conn, null, ct);
        return tz is null ? null : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(tz)).DateTime);
    }

    // ------------------------------------------------------------------------------------------------------------- mutations

    public async Task<ConfigResult<FiscalYearView>> CreateYearAsync(Guid actor, Guid companyId, FiscalYearInput? input, CancellationToken ct = default)
    {
        if (!ConfigurationValidation.TryNormalizeFiscalYear(input, out var name, out var start, out var type, out var errors)) return ConfigResult<FiscalYearView>.Invalid(errors);
        var end = start.AddYears(1).AddDays(-1);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            // The business time zone must have been chosen (it is never defaulted); it is recorded on the year as the calendar the dates belong to.
            var tz = await ReadTimeZoneAsync(work.Connection, work.Transaction, ct) ?? throw new Abort(ConfigStatus.Conflict, CompanyTimeZoneNotConfiguredException.Code);
            var existing = await LoadYearsAsync(work.Connection, work.Transaction, null, true, ct);
            if (existing.Any(y => start <= y.EndDate && end >= y.StartDate)) throw new Abort(ConfigStatus.Conflict, "fiscal_year_overlap");
            if (existing.Count > 0 && !(end.AddDays(1) == existing.Min(y => y.StartDate) || start == existing.Max(y => y.EndDate).AddDays(1)))
                throw new Abort(ConfigStatus.Conflict, "fiscal_year_not_contiguous");
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            try
            {
                await using (var ins = new NpgsqlCommand(
                    $"INSERT INTO {Y.Table} ({Y.Id}, {Y.Name}, {Y.StartDate}, {Y.EndDate}, {Y.PeriodType}, {Y.TimeZone}, {Y.Status}, {Y.Revision}, {Y.CreatedAtUtc}, {Y.CreatedBy}) " +
                    "VALUES (@id, @name, @s, @e, @t, @tz, 1, 1, @now, @actor)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("name", name);
                    ins.Parameters.AddWithValue("s", start); ins.Parameters.AddWithValue("e", end);
                    ins.Parameters.AddWithValue("t", (short)type); ins.Parameters.AddWithValue("tz", tz);
                    ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("actor", actor);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                var step = type == FiscalPeriodType.Monthly ? 1 : 3;
                var count = 12 / step;
                for (var n = 1; n <= count; n++)
                {
                    var ps = start.AddMonths((n - 1) * step);
                    var pe = ps.AddMonths(step).AddDays(-1);
                    await using var pins = new NpgsqlCommand(
                        $"INSERT INTO {P.Table} ({P.Id}, {P.YearId}, {P.Number}, {P.StartDate}, {P.EndDate}, {P.Status}, {P.Revision}, {P.CreatedAtUtc}) VALUES (gen_random_uuid(), @y, @n, @s, @e, 1, 1, @now)",
                        work.Connection, work.Transaction);
                    pins.Parameters.AddWithValue("y", id); pins.Parameters.AddWithValue("n", (short)n);
                    pins.Parameters.AddWithValue("s", ps); pins.Parameters.AddWithValue("e", pe); pins.Parameters.AddWithValue("now", now);
                    await pins.ExecuteNonQueryAsync(ct);
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new Abort(ConfigStatus.Conflict, "fiscal_year_name_taken");
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
            {
                throw new Abort(ConfigStatus.Conflict, "fiscal_year_overlap");
            }
            var changes = new JsonObject
            {
                ["name"] = new JsonObject { ["old"] = null, ["new"] = name },
                ["startDate"] = new JsonObject { ["old"] = null, ["new"] = start.ToString("yyyy-MM-dd") },
                ["endDate"] = new JsonObject { ["old"] = null, ["new"] = end.ToString("yyyy-MM-dd") },
                ["periodType"] = new JsonObject { ["old"] = null, ["new"] = type.ToString().ToLowerInvariant() },
                ["timeZone"] = new JsonObject { ["old"] = null, ["new"] = tz },
            };
            await RecordAsync(work, ConfigAuditEvents.FiscalYearCreated, actor, 1, id, 1, changes, null, null, null, "fiscal year created", ct);
            return (await LoadYearsAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct);
    }

    /// <summary>Open -> Soft Closed.</summary>
    public Task<ConfigResult<FiscalPeriodView>> SoftCloseAsync(Guid actor, Guid companyId, Guid periodId, long? expectedRevision, CancellationToken ct = default) =>
        ChangePeriodAsync(actor, companyId, periodId, expectedRevision, FiscalPeriodState.Open, FiscalPeriodState.SoftClosed, ConfigAuditEvents.FiscalPeriodSoftClosed, "fiscal period soft closed", null, ct);

    /// <summary>Soft Closed -> Open (routine reopening of a soft close).</summary>
    public Task<ConfigResult<FiscalPeriodView>> ReopenAsync(Guid actor, Guid companyId, Guid periodId, long? expectedRevision, CancellationToken ct = default) =>
        ChangePeriodAsync(actor, companyId, periodId, expectedRevision, FiscalPeriodState.SoftClosed, FiscalPeriodState.Open, ConfigAuditEvents.FiscalPeriodReopened, "fiscal period reopened", null, ct);

    /// <summary>Soft Closed -> Final Closed. The period must have been Soft Closed first (Open never jumps to Final Closed).</summary>
    public Task<ConfigResult<FiscalPeriodView>> FinalCloseAsync(Guid actor, Guid companyId, Guid periodId, long? expectedRevision, CancellationToken ct = default) =>
        ChangePeriodAsync(actor, companyId, periodId, expectedRevision, FiscalPeriodState.SoftClosed, FiscalPeriodState.FinalClosed, ConfigAuditEvents.FiscalPeriodFinalClosed, "fiscal period final closed", null, ct);

    /// <summary>
    /// Exceptional: Final Closed -> Soft Closed (never straight to Open), with a recorded reason, by someone other than the user who final-closed the
    /// period. The caller (the authorized facade) has already checked the dedicated permission.
    /// </summary>
    public Task<ConfigResult<FiscalPeriodView>> ReopenFinalAsync(Guid actor, Guid companyId, Guid periodId, long? expectedRevision, ReasonInput? reason, CancellationToken ct = default)
    {
        if (!ConfigurationValidation.TryNormalizeReason(reason, out var text, out var errors)) return Task.FromResult(ConfigResult<FiscalPeriodView>.Invalid(errors));
        return ChangePeriodAsync(actor, companyId, periodId, expectedRevision, FiscalPeriodState.FinalClosed, FiscalPeriodState.SoftClosed, ConfigAuditEvents.FiscalPeriodFinalReopened, "fiscal period final closed reopened", text, ct);
    }

    private async Task<ConfigResult<FiscalPeriodView>> ChangePeriodAsync(
        Guid actor, Guid companyId, Guid periodId, long? expectedRevision, FiscalPeriodState from, FiscalPeriodState to, short eventType, string label, string? reason, CancellationToken ct)
    {
        if (expectedRevision is null) return ConfigResult<FiscalPeriodView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var (current, finalBy) = await LoadPeriodAsync(work.Connection, work.Transaction, periodId, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "fiscal_period_not_found");
            if (current.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
            var year = (await LoadYearsAsync(work.Connection, work.Transaction, current.YearId, true, ct)).Single();
            if (year.IsClosed) throw new Abort(ConfigStatus.Conflict, "fiscal_year_closed");
            if (current.State != from) throw new Abort(ConfigStatus.Conflict, "invalid_period_state");
            // Segregation of duties: the user who final-closed a period cannot be the one who reopens it.
            if (from == FiscalPeriodState.FinalClosed && finalBy == actor) throw new Abort(ConfigStatus.Forbidden, "segregation_of_duties");
            if (to is FiscalPeriodState.SoftClosed or FiscalPeriodState.FinalClosed && from is FiscalPeriodState.Open or FiscalPeriodState.SoftClosed
                && await closeGuard.IsBlockedAsync(companyId, current, work.Connection, work.Transaction, ct))
                throw new Abort(ConfigStatus.Conflict, "period_close_blocked");
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {P.Table} SET {P.Status} = @to, {P.StatusChangedAtUtc} = @now, {P.StatusChangedBy} = @actor, " +
                $"{P.FinalClosedBy} = CASE WHEN @to = 3 THEN @actor ELSE {P.FinalClosedBy} END, {P.Revision} = {P.Revision} + 1 WHERE {P.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("to", (short)to); upd.Parameters.AddWithValue("now", now);
                upd.Parameters.AddWithValue("actor", actor); upd.Parameters.AddWithValue("id", periodId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var changes = new JsonObject
            {
                ["status"] = new JsonObject { ["old"] = from.ToString(), ["new"] = to.ToString() },
                ["periodNumber"] = current.Number,
            };
            if (reason is not null) changes["reasonRecorded"] = true;   // the reason text lives in the history row, never in the audit detail
            await RecordAsync(work, eventType, actor, 2, periodId, current.Revision + 1, changes, (short)from, (short)to, reason, label, ct);
            return (await LoadPeriodAsync(work.Connection, work.Transaction, periodId, false, ct))!.Value.View;
        }, ct);
    }

    /// <summary>Closes the fiscal year: allowed only when every period is Final Closed. State only: no closing entries or balance carry-forward exist yet.</summary>
    public async Task<ConfigResult<FiscalYearView>> CloseYearAsync(Guid actor, Guid companyId, Guid yearId, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<FiscalYearView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var year = (await LoadYearsAsync(work.Connection, work.Transaction, yearId, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "fiscal_year_not_found");
            if (year.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", year.Revision);
            if (year.IsClosed) throw new Abort(ConfigStatus.Conflict, "fiscal_year_already_closed");
            if (year.Periods.Any(p => p.State != FiscalPeriodState.FinalClosed)) throw new Abort(ConfigStatus.Conflict, "fiscal_year_has_unfinalized_periods");
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {Y.Table} SET {Y.Status} = 2, {Y.ClosedAtUtc} = @now, {Y.ClosedBy} = @actor, {Y.Revision} = {Y.Revision} + 1 WHERE {Y.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("now", now); upd.Parameters.AddWithValue("actor", actor); upd.Parameters.AddWithValue("id", yearId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var changes = new JsonObject { ["status"] = new JsonObject { ["old"] = "Open", ["new"] = "Closed" } };
            await RecordAsync(work, ConfigAuditEvents.FiscalYearClosed, actor, 1, yearId, year.Revision + 1, changes, 1, 2, null, "fiscal year closed", ct);
            return (await LoadYearsAsync(work.Connection, work.Transaction, yearId, false, ct)).Single();
        }, ct);
    }

    /// <summary>Exceptional: reopens a closed fiscal year, with a recorded reason. Its periods stay Final Closed (each needs its own exceptional reopening).</summary>
    public async Task<ConfigResult<FiscalYearView>> ReopenYearAsync(Guid actor, Guid companyId, Guid yearId, long? expectedRevision, ReasonInput? reason, CancellationToken ct = default)
    {
        if (!ConfigurationValidation.TryNormalizeReason(reason, out var text, out var errors)) return ConfigResult<FiscalYearView>.Invalid(errors);
        if (expectedRevision is null) return ConfigResult<FiscalYearView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var year = (await LoadYearsAsync(work.Connection, work.Transaction, yearId, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "fiscal_year_not_found");
            if (year.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", year.Revision);
            if (!year.IsClosed) throw new Abort(ConfigStatus.Conflict, "fiscal_year_not_closed");
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {Y.Table} SET {Y.Status} = 1, {Y.ClosedAtUtc} = NULL, {Y.ClosedBy} = NULL, {Y.Revision} = {Y.Revision} + 1 WHERE {Y.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", yearId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var changes = new JsonObject { ["status"] = new JsonObject { ["old"] = "Closed", ["new"] = "Open" }, ["reasonRecorded"] = true };
            await RecordAsync(work, ConfigAuditEvents.FiscalYearReopened, actor, 1, yearId, year.Revision + 1, changes, 2, 1, text, "fiscal year reopened", ct);
            return (await LoadYearsAsync(work.Connection, work.Transaction, yearId, false, ct)).Single();
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private async Task<ConfigResult<T>> MutateAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) { return abort.To<T>(); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", LockKey1);
        lk.Parameters.AddWithValue("b", LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> ReadTimeZoneAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {S.TimeZone} FROM {S.Table}", conn, tx);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<List<FiscalYearView>> LoadYearsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var years = new List<(Guid Id, string Name, DateOnly S, DateOnly E, FiscalPeriodType T, string Tz, bool Closed, DateTimeOffset? ClosedAt, long Rev, DateTimeOffset Created)>();
        await using (var cmd = new NpgsqlCommand($"SELECT {YearColumns} FROM {Y.Table} WHERE (@id::uuid IS NULL OR {Y.Id} = @id) ORDER BY {Y.StartDate}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)id ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                years.Add((r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetFieldValue<DateOnly>(3), (FiscalPeriodType)r.GetInt16(4), r.GetString(5),
                    r.GetInt16(6) == 2, r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.GetInt64(8), r.GetFieldValue<DateTimeOffset>(9)));
        }
        var periods = new Dictionary<Guid, List<FiscalPeriodView>>();
        await using (var cmd = new NpgsqlCommand($"SELECT {PeriodColumns} FROM {P.Table} WHERE (@id::uuid IS NULL OR {P.YearId} = @id) ORDER BY {P.StartDate}", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)id ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var v = ReadPeriod(r).View;
                if (!periods.TryGetValue(v.YearId, out var list)) periods[v.YearId] = list = [];
                list.Add(v);
            }
        }
        return [.. years.Select(y => new FiscalYearView(y.Id, y.Name, y.S, y.E, y.T, y.Tz, y.Closed, y.ClosedAt, y.Rev, y.Created, periods.GetValueOrDefault(y.Id) ?? []))];
    }

    private static async Task<(FiscalPeriodView View, Guid? FinalClosedBy)?> LoadPeriodAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {PeriodColumns} FROM {P.Table} WHERE {P.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadPeriod(r) : null;
    }

    // Columns: 0 id, 1 year, 2 number, 3 start, 4 end, 5 status, 6 changed at, 7 revision, 8 final closed by.
    private static (FiscalPeriodView View, Guid? FinalClosedBy) ReadPeriod(NpgsqlDataReader r) => (new FiscalPeriodView(
        r.GetGuid(0), r.GetGuid(1), r.GetInt16(2), r.GetFieldValue<DateOnly>(3), r.GetFieldValue<DateOnly>(4), (FiscalPeriodState)r.GetInt16(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.GetInt64(7)), r.IsDBNull(8) ? null : r.GetGuid(8));

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();

    /// <summary>Appends the authenticated audit event and the linked append-only history row, inside the caller's transaction.</summary>
    private async Task RecordAsync(CompanyAuditedWork work, short eventType, Guid actor, short entity, Guid entityId, long revision, JsonObject changes, short? from, short? to, string? reason, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor,
            Detail: $"{label} r{revision} f={string.Join(',', changes.Select(c => c.Key))} h={Hash(json)}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {H.Table} ({H.Entity}, {H.EntityId}, {H.Revision}, {H.ActorUserId}, {H.ChangedAtUtc}, {H.Changes}, {H.FromStatus}, {H.ToStatus}, {H.Reason}, {H.AuditStream}, {H.AuditSeq}) " +
            "VALUES (@e, @k, @r, @a, @t, @c, @f, @to, @reason, @s, @q)", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("e", entity);
        cmd.Parameters.AddWithValue("k", entityId);
        cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Smallint) { Value = (object?)from ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Smallint) { Value = (object?)to ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Varchar) { Value = (object?)reason ?? DBNull.Value });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey);
        cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
