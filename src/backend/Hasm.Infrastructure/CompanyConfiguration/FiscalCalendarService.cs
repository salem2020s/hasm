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
using RQ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalReopenRequest;

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
    CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, PeriodCloseGuard closeGuard, FinancialHistoryGuard financialHistory, TimeProvider clock)
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
        ChangePeriodAsync(actor, companyId, periodId, expectedRevision, FiscalPeriodState.Open, FiscalPeriodState.SoftClosed, ConfigAuditEvents.FiscalPeriodSoftClosed, "fiscal period soft closed", ct);

    /// <summary>Soft Closed -> Open (routine reopening of a soft close).</summary>
    public Task<ConfigResult<FiscalPeriodView>> ReopenAsync(Guid actor, Guid companyId, Guid periodId, long? expectedRevision, CancellationToken ct = default) =>
        ChangePeriodAsync(actor, companyId, periodId, expectedRevision, FiscalPeriodState.SoftClosed, FiscalPeriodState.Open, ConfigAuditEvents.FiscalPeriodReopened, "fiscal period reopened", ct);

    /// <summary>Soft Closed -> Final Closed. The period must have been Soft Closed first (Open never jumps to Final Closed).</summary>
    public Task<ConfigResult<FiscalPeriodView>> FinalCloseAsync(Guid actor, Guid companyId, Guid periodId, long? expectedRevision, CancellationToken ct = default) =>
        ChangePeriodAsync(actor, companyId, periodId, expectedRevision, FiscalPeriodState.SoftClosed, FiscalPeriodState.FinalClosed, ConfigAuditEvents.FiscalPeriodFinalClosed, "fiscal period final closed", ct);

    // Exceptional reopening (Final Closed period, closed year) is NOT a method here: it is an approval workflow (request -> approve/deny/cancel) below.

    private async Task<ConfigResult<FiscalPeriodView>> ChangePeriodAsync(
        Guid actor, Guid companyId, Guid periodId, long? expectedRevision, FiscalPeriodState from, FiscalPeriodState to, short eventType, string label, CancellationToken ct)
    {
        if (expectedRevision is null) return ConfigResult<FiscalPeriodView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var (current, _) = await LoadPeriodAsync(work.Connection, work.Transaction, periodId, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "fiscal_period_not_found");
            if (current.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
            var year = (await LoadYearsAsync(work.Connection, work.Transaction, current.YearId, true, ct)).Single();
            if (year.IsClosed) throw new Abort(ConfigStatus.Conflict, "fiscal_year_closed");
            if (current.State != from) throw new Abort(ConfigStatus.Conflict, "invalid_period_state");
            var closing = to is FiscalPeriodState.SoftClosed or FiscalPeriodState.FinalClosed;
            if (closing) await EnsureCloseChecksCanBeTrustedAsync(work, companyId, ct);
            if (closing && await closeGuard.IsBlockedAsync(companyId, current, work.Connection, work.Transaction, ct))
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
            // The record never claims more than was checked: while the modules behind the close checks do not exist, every close says so in its own history.
            if (closing && closeGuard.UnimplementedDomains.Count > 0)
                changes["closeChecksNotImplemented"] = new JsonArray([.. closeGuard.UnimplementedDomains.Select(d => (JsonNode?)JsonValue.Create(d))]);
            await RecordAsync(work, eventType, actor, 2, periodId, current.Revision + 1, changes, (short)from, (short)to, null, label, ct);
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
            await EnsureCloseChecksCanBeTrustedAsync(work, companyId, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {Y.Table} SET {Y.Status} = 2, {Y.ClosedAtUtc} = @now, {Y.ClosedBy} = @actor, {Y.Revision} = {Y.Revision} + 1 WHERE {Y.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("now", now); upd.Parameters.AddWithValue("actor", actor); upd.Parameters.AddWithValue("id", yearId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var changes = new JsonObject { ["status"] = new JsonObject { ["old"] = "Open", ["new"] = "Closed" } };
            if (closeGuard.UnimplementedDomains.Count > 0)
                changes["closeChecksNotImplemented"] = new JsonArray([.. closeGuard.UnimplementedDomains.Select(d => (JsonNode?)JsonValue.Create(d))]);
            await RecordAsync(work, ConfigAuditEvents.FiscalYearClosed, actor, 1, yearId, year.Revision + 1, changes, 1, 2, null, "fiscal year closed", ct);
            return (await LoadYearsAsync(work.Connection, work.Transaction, yearId, false, ct)).Single();
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- exceptional reopening: request -> approval

    private const short KindPeriod = 1, KindYear = 2;
    private const short Pending = 1, Approved = 2, Denied = 3, Cancelled = 4;

    private static readonly string RequestColumns =
        $"{RQ.Id}, {RQ.TargetKind}, {RQ.TargetId}, {RQ.ExpectedRevision}, {RQ.Reason}, {RQ.Status}, {RQ.RequestedBy}, {RQ.RequestedAtUtc}, {RQ.DecidedBy}, {RQ.DecidedAtUtc}, {RQ.DecisionNote}, {RQ.Revision}";

    /// <summary>Recent reopen requests of the company (newest first), with a readable target.</summary>
    public async Task<IReadOnlyList<FiscalReopenRequestView>> ListRequestsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadRequestsAsync(conn, null, null, false, ct);
    }

    public async Task<FiscalReopenRequestView?> GetRequestAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadRequestsAsync(conn, null, id, false, ct)).SingleOrDefault();
    }

    /// <summary>
    /// Requests the exceptional reopening of a Final Closed PERIOD. Nothing changes in the calendar: only a pending request is recorded, bound to the
    /// period and the revision the requester saw. <paramref name="expectedRevision"/> must be the current one.
    /// </summary>
    public Task<ConfigResult<FiscalReopenRequestView>> RequestPeriodReopenAsync(Guid actor, Guid companyId, Guid periodId, long? expectedRevision, ReasonInput? reason, CancellationToken ct = default) =>
        RequestAsync(actor, companyId, KindPeriod, periodId, expectedRevision, reason, ct);

    /// <summary>Requests the exceptional reopening of a closed fiscal YEAR (same rules).</summary>
    public Task<ConfigResult<FiscalReopenRequestView>> RequestYearReopenAsync(Guid actor, Guid companyId, Guid yearId, long? expectedRevision, ReasonInput? reason, CancellationToken ct = default) =>
        RequestAsync(actor, companyId, KindYear, yearId, expectedRevision, reason, ct);

    private async Task<ConfigResult<FiscalReopenRequestView>> RequestAsync(Guid actor, Guid companyId, short kind, Guid targetId, long? expectedRevision, ReasonInput? reason, CancellationToken ct)
    {
        if (!ConfigurationValidation.TryNormalizeReason(reason, out var text, out var errors)) return ConfigResult<FiscalReopenRequestView>.Invalid(errors);
        if (expectedRevision is null) return ConfigResult<FiscalReopenRequestView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            if (kind == KindPeriod)
            {
                var (period, _) = await LoadPeriodAsync(work.Connection, work.Transaction, targetId, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "fiscal_period_not_found");
                if (period.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", period.Revision);
                if (period.State != FiscalPeriodState.FinalClosed) throw new Abort(ConfigStatus.Conflict, "invalid_period_state");
                var year = (await LoadYearsAsync(work.Connection, work.Transaction, period.YearId, true, ct)).Single();
                if (year.IsClosed) throw new Abort(ConfigStatus.Conflict, "fiscal_year_closed");
            }
            else
            {
                var year = (await LoadYearsAsync(work.Connection, work.Transaction, targetId, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "fiscal_year_not_found");
                if (year.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", year.Revision);
                if (!year.IsClosed) throw new Abort(ConfigStatus.Conflict, "fiscal_year_not_closed");
            }
            var id = Guid.NewGuid();
            try
            {
                await using var ins = new NpgsqlCommand(
                    $"INSERT INTO {RQ.Table} ({RQ.Id}, {RQ.TargetKind}, {RQ.TargetId}, {RQ.ExpectedRevision}, {RQ.Reason}, {RQ.Status}, {RQ.RequestedBy}, {RQ.RequestedAtUtc}, {RQ.Revision}) " +
                    "VALUES (@id, @k, @t, @r, @reason, 1, @actor, @now, 1)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("k", kind); ins.Parameters.AddWithValue("t", targetId);
                ins.Parameters.AddWithValue("r", expectedRevision.Value); ins.Parameters.AddWithValue("reason", text);
                ins.Parameters.AddWithValue("actor", actor); ins.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new Abort(ConfigStatus.Conflict, "reopen_request_pending");   // one pending request per target (also under concurrency)
            }
            var changes = new JsonObject
            {
                ["target"] = new JsonObject { ["kind"] = kind == KindPeriod ? "period" : "year", ["id"] = targetId.ToString(), ["expectedRevision"] = expectedRevision.Value },
                ["status"] = new JsonObject { ["old"] = null, ["new"] = "Pending" },
            };
            await RecordAsync(work, ConfigAuditEvents.FiscalReopenRequested, actor, 3, id, 1, changes, null, Pending, text, "fiscal reopen requested", ct);
            return (await LoadRequestsAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct);
    }

    /// <summary>
    /// Approves a pending request and EXECUTES the reopening in the same transaction, after re-validating everything now: the request is still pending
    /// and at the revision the approver saw; the approver is not the requester and (for the object's own closer) not the user who closed it;
    /// <paramref name="requesterStillAuthorized"/> (checked by the authorized facade against the live permissions) is true; and the target is still in
    /// the state and at the revision the request was made for (otherwise <c>request_stale</c>: nothing changes). A Final Closed period returns to
    /// Soft Closed (never straight to Open); a closed year reopens and its periods stay Final Closed.
    /// </summary>
    public async Task<ConfigResult<FiscalReopenRequestView>> ApproveRequestAsync(Guid approver, Guid companyId, Guid requestId, long? expectedRequestRevision, bool requesterStillAuthorized, CancellationToken ct = default)
    {
        if (expectedRequestRevision is null) return ConfigResult<FiscalReopenRequestView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var req = await RequirePendingAsync(work, requestId, expectedRequestRevision.Value, ct);
            if (req.RequestedBy == approver) throw new Abort(ConfigStatus.Forbidden, "self_approval_forbidden");
            if (!requesterStillAuthorized) throw new Abort(ConfigStatus.Conflict, "request_requester_not_authorized");
            var now = clock.GetUtcNow().UtcDateTime;
            long resultRevision;
            if (req.Target == FiscalReopenTarget.Period)
            {
                var (period, finalBy) = await LoadPeriodAsync(work.Connection, work.Transaction, req.TargetId, true, ct) ?? throw new Abort(ConfigStatus.Conflict, "request_stale");
                var year = (await LoadYearsAsync(work.Connection, work.Transaction, period.YearId, true, ct)).Single();
                if (year.IsClosed || period.State != FiscalPeriodState.FinalClosed || period.Revision != req.ExpectedRevision) throw new Abort(ConfigStatus.Conflict, "request_stale");
                if (finalBy == approver) throw new Abort(ConfigStatus.Forbidden, "segregation_of_duties");
                await using (var upd = new NpgsqlCommand(
                    $"UPDATE {P.Table} SET {P.Status} = 2, {P.StatusChangedAtUtc} = @now, {P.StatusChangedBy} = @a, {P.Revision} = {P.Revision} + 1 WHERE {P.Id} = @id", work.Connection, work.Transaction))
                {
                    upd.Parameters.AddWithValue("now", now); upd.Parameters.AddWithValue("a", approver); upd.Parameters.AddWithValue("id", period.Id);
                    await upd.ExecuteNonQueryAsync(ct);
                }
                resultRevision = period.Revision + 1;
                var changes = new JsonObject
                {
                    ["status"] = new JsonObject { ["old"] = "FinalClosed", ["new"] = "SoftClosed" }, ["periodNumber"] = period.Number,
                    ["approvedRequest"] = requestId.ToString(), ["requestedBy"] = req.RequestedBy.ToString(), ["approvedBy"] = approver.ToString(),
                };
                await RecordAsync(work, ConfigAuditEvents.FiscalPeriodFinalReopened, approver, 2, period.Id, resultRevision, changes, 3, 2, req.Reason, "fiscal period final closed reopened (approved)", ct);
            }
            else
            {
                var year = (await LoadYearsAsync(work.Connection, work.Transaction, req.TargetId, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.Conflict, "request_stale");
                if (!year.IsClosed || year.Revision != req.ExpectedRevision) throw new Abort(ConfigStatus.Conflict, "request_stale");
                Guid? closedBy;
                await using (var q = new NpgsqlCommand($"SELECT {Y.ClosedBy} FROM {Y.Table} WHERE {Y.Id} = @id", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("id", year.Id);
                    closedBy = await q.ExecuteScalarAsync(ct) as Guid?;
                }
                if (closedBy == approver) throw new Abort(ConfigStatus.Forbidden, "segregation_of_duties");
                await using (var upd = new NpgsqlCommand(
                    $"UPDATE {Y.Table} SET {Y.Status} = 1, {Y.ClosedAtUtc} = NULL, {Y.ClosedBy} = NULL, {Y.Revision} = {Y.Revision} + 1 WHERE {Y.Id} = @id", work.Connection, work.Transaction))
                {
                    upd.Parameters.AddWithValue("id", year.Id);
                    await upd.ExecuteNonQueryAsync(ct);
                }
                resultRevision = year.Revision + 1;
                var changes = new JsonObject
                {
                    ["status"] = new JsonObject { ["old"] = "Closed", ["new"] = "Open" },
                    ["approvedRequest"] = requestId.ToString(), ["requestedBy"] = req.RequestedBy.ToString(), ["approvedBy"] = approver.ToString(),
                };
                await RecordAsync(work, ConfigAuditEvents.FiscalYearReopened, approver, 1, year.Id, resultRevision, changes, 2, 1, req.Reason, "fiscal year reopened (approved)", ct);
            }
            await DecideAsync(work, requestId, Approved, approver, null, resultRevision, now, ct);
            await RecordAsync(work, ConfigAuditEvents.FiscalReopenApproved, approver, 3, requestId, req.Revision + 1,
                new JsonObject { ["status"] = new JsonObject { ["old"] = "Pending", ["new"] = "Approved" }, ["resultRevision"] = resultRevision }, Pending, Approved, null, "fiscal reopen approved", ct);
            return (await LoadRequestsAsync(work.Connection, work.Transaction, requestId, false, ct)).Single();
        }, ct);
    }

    /// <summary>Denies a pending request (by an approver who is not the requester). The calendar is not touched.</summary>
    public Task<ConfigResult<FiscalReopenRequestView>> DenyRequestAsync(Guid decider, Guid companyId, Guid requestId, long? expectedRequestRevision, string? note, CancellationToken ct = default) =>
        CloseRequestAsync(decider, companyId, requestId, expectedRequestRevision, Denied, note, ct);

    /// <summary>Withdraws a pending request (only by its requester). The calendar is not touched.</summary>
    public Task<ConfigResult<FiscalReopenRequestView>> CancelRequestAsync(Guid actor, Guid companyId, Guid requestId, long? expectedRequestRevision, CancellationToken ct = default) =>
        CloseRequestAsync(actor, companyId, requestId, expectedRequestRevision, Cancelled, null, ct);

    private async Task<ConfigResult<FiscalReopenRequestView>> CloseRequestAsync(Guid actor, Guid companyId, Guid requestId, long? expectedRequestRevision, short outcome, string? note, CancellationToken ct)
    {
        if (expectedRequestRevision is null) return ConfigResult<FiscalReopenRequestView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var cleaned = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleaned is { Length: > 500 }) return ConfigResult<FiscalReopenRequestView>.Invalid([new("note", "too_long")]);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var req = await RequirePendingAsync(work, requestId, expectedRequestRevision.Value, ct);
            if (outcome == Cancelled && req.RequestedBy != actor) throw new Abort(ConfigStatus.Forbidden, "not_the_requester");
            if (outcome == Denied && req.RequestedBy == actor) throw new Abort(ConfigStatus.Forbidden, "self_approval_forbidden");
            await DecideAsync(work, requestId, outcome, actor, cleaned, null, clock.GetUtcNow().UtcDateTime, ct);
            var name = outcome == Denied ? "Denied" : "Cancelled";
            await RecordAsync(work, outcome == Denied ? ConfigAuditEvents.FiscalReopenDenied : ConfigAuditEvents.FiscalReopenCancelled, actor, 3, requestId, req.Revision + 1,
                new JsonObject { ["status"] = new JsonObject { ["old"] = "Pending", ["new"] = name } }, Pending, outcome, cleaned, outcome == Denied ? "fiscal reopen denied" : "fiscal reopen cancelled", ct);
            return (await LoadRequestsAsync(work.Connection, work.Transaction, requestId, false, ct)).Single();
        }, ct);
    }

    private async Task<FiscalReopenRequestView> RequirePendingAsync(CompanyAuditedWork work, Guid id, long expectedRevision, CancellationToken ct)
    {
        var req = (await LoadRequestsAsync(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "reopen_request_not_found");
        if (req.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", req.Revision);
        if (req.Status != FiscalReopenStatus.Pending) throw new Abort(ConfigStatus.Conflict, "request_not_pending");
        return req;
    }

    private static async Task DecideAsync(CompanyAuditedWork work, Guid id, short status, Guid by, string? note, long? resultRevision, DateTime now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {RQ.Table} SET {RQ.Status} = @s, {RQ.DecidedBy} = @by, {RQ.DecidedAtUtc} = @now, {RQ.DecisionNote} = @note, {RQ.ResultRevision} = @rr, {RQ.Revision} = {RQ.Revision} + 1 WHERE {RQ.Id} = @id",
            work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("s", status); cmd.Parameters.AddWithValue("by", by); cmd.Parameters.AddWithValue("now", now); cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rr", NpgsqlDbType.Bigint) { Value = (object?)resultRevision ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<FiscalReopenRequestView>> LoadRequestsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        // Target description is read separately so the row lock (FOR UPDATE) applies to the request row only.
        var rows = new List<(Guid Id, short Kind, Guid Target, long Expected, string Reason, short Status, Guid By, DateTimeOffset At, Guid? DecBy, DateTimeOffset? DecAt, string? Note, long Rev)>();
        await using (var cmd = new NpgsqlCommand($"SELECT {RequestColumns} FROM {RQ.Table} WHERE (@id::uuid IS NULL OR {RQ.Id} = @id) ORDER BY {RQ.RequestedAtUtc} DESC, {RQ.Id} LIMIT 200{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)id ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetGuid(0), r.GetInt16(1), r.GetGuid(2), r.GetInt64(3), r.GetString(4), r.GetInt16(5), r.GetGuid(6), r.GetFieldValue<DateTimeOffset>(7),
                    r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt64(11)));
        }
        var list = new List<FiscalReopenRequestView>();
        foreach (var x in rows)
        {
            string yearName; int? number = null; DateOnly? start = null, end = null;
            if (x.Kind == KindPeriod)
            {
                await using var q = new NpgsqlCommand($"SELECT y.{Y.Name}, p.{P.Number}, p.{P.StartDate}, p.{P.EndDate} FROM {P.Table} p JOIN {Y.Table} y ON y.{Y.Id} = p.{P.YearId} WHERE p.{P.Id} = @t", conn, tx);
                q.Parameters.AddWithValue("t", x.Target);
                await using var r = await q.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                yearName = r.GetString(0); number = r.GetInt16(1); start = r.GetFieldValue<DateOnly>(2); end = r.GetFieldValue<DateOnly>(3);
            }
            else
            {
                await using var q = new NpgsqlCommand($"SELECT {Y.Name}, {Y.StartDate}, {Y.EndDate} FROM {Y.Table} WHERE {Y.Id} = @t", conn, tx);
                q.Parameters.AddWithValue("t", x.Target);
                await using var r = await q.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                yearName = r.GetString(0); start = r.GetFieldValue<DateOnly>(1); end = r.GetFieldValue<DateOnly>(2);
            }
            list.Add(new FiscalReopenRequestView(x.Id, x.Kind == KindPeriod ? FiscalReopenTarget.Period : FiscalReopenTarget.Year, x.Target, yearName, number, start, end,
                x.Expected, x.Reason, (FiscalReopenStatus)x.Status, x.By, x.At, x.DecBy, x.DecAt, x.Note, x.Rev));
        }
        return list;
    }


    /// <summary>
    /// No silent bypass of the mandatory close checks: once ANY accounting or inventory module reports financial history for the company (its
    /// <see cref="IFinancialHistoryProbe"/>), closing a period or a year is refused while any close-check domain is still an unimplemented placeholder.
    /// A module that holds data therefore cannot ship without also registering its <see cref="IPeriodCloseProbe"/>; until such data exists the
    /// placeholders are harmless and every close is recorded as "checks not implemented" in its history.
    /// </summary>
    private async Task EnsureCloseChecksCanBeTrustedAsync(CompanyAuditedWork work, Guid companyId, CancellationToken ct)
    {
        if (closeGuard.UnimplementedDomains.Count == 0) return;
        var info = await financialHistory.EvaluateAsync(companyId, work.Connection, work.Transaction, ct);
        if (info.Locked) throw new Abort(ConfigStatus.Conflict, "period_close_checks_not_implemented");
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
