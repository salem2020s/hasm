using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRequest;

namespace Hasm.Infrastructure.Parties;

/// <summary>
/// The company side of the party registry (Phase 3.9A): the company-local party records (<c>LocalPartyId</c>) and the request log (outbox) of the cross-database requests to the registry of the
/// system database (<see cref="PartyRegistryStore"/>). <b>No authorization here by design:</b> <see cref="PartyManagementService"/> wraps it.
/// <para><b>No distributed transaction</b> (SRS 17.4): every local change and its request are written in ONE company transaction with their audit events; the delivery to the registry happens after the
/// commit, keyed by the request id (idempotency key), and is recorded locally in a separate company transaction. A failure in between leaves the request pending; any later attempt (the next change,
/// <see cref="SyncAsync"/>, <see cref="ReconcileAsync"/>) finds the proposal the registry already holds and records the delivery: a retry never creates a duplicate.</para>
/// <para>The shared identity is read live from the registry through THIS company's link only; nothing of another company is ever read, and no financial figure exists in the registry.</para>
/// </summary>
public sealed class PartyService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, PartyRegistryStore registry, TimeProvider clock)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }

    /// <summary>Thrown inside a company transaction that turns out to have nothing to do: the transaction is rolled back and the caller treats it as a no-op.</summary>
    private sealed class NothingToDo : Exception;

    /// <summary>
    /// Failure injection for tests of the cross-database protocol (null in production): invoked with <c>"before_registry"</c> just before the registry is called and with <c>"after_registry"</c> right
    /// after the registry committed (before the company records the delivery). Throwing there simulates a crash or a lost connection at that exact point.
    /// </summary>
    internal Func<string, Task>? Fault { get; set; }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static object Db<T>(T? v) where T : class => (object?)v ?? DBNull.Value;
    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static DateTimeOffset? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    // ================================================================================================================ reads

    private const string LocalCols = $"{LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.Notes}, {LP.IsActive}, {LP.LinkState}, {LP.GlobalPartyId}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}";
    private const string RequestCols = $"{PR.Id}, {PR.Kind}, {PR.Status}, {PR.Outcome}, {PR.PartyKind}, {PR.LegalNameAr}, {PR.LegalNameEn}, {PR.TaxNumber}, {PR.RegistrationNumber}, {PR.ProposedStatus}, " +
                                       $"{PR.Note}, {PR.Attempts}, {PR.LastAttemptAtUtc}, {PR.LastErrorCode}, {PR.CreatedBy}, {PR.CreatedAtUtc}, {PR.DeliveredAtUtc}, {PR.CompletedAtUtc}, {PR.BasedOnRevision}, {PR.DecisionNote}";

    private static PartyRequestView ReadRequest(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetInt16(1), r.GetInt16(2), r.IsDBNull(3) ? null : r.GetInt16(3), r.GetInt16(4), r.GetString(5), Str(r, 6), Str(r, 7),
        Str(r, 8), r.GetInt16(9), Str(r, 10), r.GetInt32(11), T(r, 12), Str(r, 13), r.GetGuid(14), r.GetFieldValue<DateTimeOffset>(15), T(r, 16), T(r, 17), r.IsDBNull(18) ? null : r.GetInt64(18), Str(r, 19));

    private static async Task<LocalPartyView?> LoadLocal(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        LocalPartyView? v;
        await using (var q = new NpgsqlCommand($"SELECT {LocalCols} FROM {LP.Table} WHERE {LP.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            v = await r.ReadAsync(ct)
                ? new LocalPartyView(r.GetGuid(0), r.GetString(1), r.GetString(2), Str(r, 3), r.GetBoolean(4), r.GetInt16(5), G(r, 6), r.GetInt64(7), r.GetGuid(8), r.GetFieldValue<DateTimeOffset>(9),
                    r.GetFieldValue<DateTimeOffset>(10), null, [])
                : null;
        }
        if (v is null) return null;
        var requests = new List<PartyRequestView>();
        await using (var q = new NpgsqlCommand($"SELECT {RequestCols} FROM {PR.Table} WHERE {PR.LocalPartyId} = @id ORDER BY {PR.CreatedAtUtc} DESC, {PR.Id}", conn, tx))
        {
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) requests.Add(ReadRequest(r));
        }
        return v with { Requests = requests };
    }

    /// <summary>The shared identity of a linked local party, read through this company's link; shown only when the registry's link agrees with the local GlobalPartyId.</summary>
    private async Task<LocalPartyView> WithIdentity(Guid companyId, LocalPartyView v, CancellationToken ct)
    {
        if (v.GlobalPartyId is not { } g) return v;
        IReadOnlyDictionary<Guid, SharedIdentityView> linked;
        try { linked = await registry.GetLinkedIdentitiesAsync(companyId, [v.Id], ct); }
        catch (NpgsqlException) { return v with { RegistryUnavailable = true }; }        // review M2: the local record stays readable when the registry is not
        return linked.TryGetValue(v.Id, out var identity) && identity.GlobalPartyId == g ? v with { Identity = identity } : v;
    }

    public async Task<LocalPartyView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        LocalPartyView? v;
        await using (var conn = await resolver.OpenAsync(companyId, ct)) v = await LoadLocal(conn, null, id, false, ct);
        return v is null ? null : await WithIdentity(companyId, v, ct);
    }

    /// <summary>Filtered, paged list of this company's local parties (code or display name search, link state, active flag, parties without a role of a kind), with the legal names of the linked shared identities. Every filter is a bound parameter.</summary>
    public async Task<PartyPage> ListAsync(Guid companyId, PartyFilter f, CancellationToken ct = default)
    {
        var rows = new List<(Guid Id, string Code, string Name, bool Active, int State, Guid? Global, bool Open, long Rev, DateTimeOffset Updated)>();
        long total;
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        {
            var where = new List<string>();
            await using var cmd = new NpgsqlCommand { Connection = conn };
            if (f.LinkState is { } st) { where.Add($"p.{LP.LinkState} = @st"); cmd.Parameters.AddWithValue("st", (short)st); }
            if (f.Active is { } active) { where.Add($"p.{LP.IsActive} = @active"); cmd.Parameters.AddWithValue("active", active); }
            if (f.WithoutRole is { } role)
            {
                where.Add($"NOT EXISTS (SELECT 1 FROM {CompanyDatabase.CompanySchema.PartyRole.Table} pr WHERE pr.{CompanyDatabase.CompanySchema.PartyRole.PartyId} = p.{LP.Id} AND pr.{CompanyDatabase.CompanySchema.PartyRole.Role} = @withoutRole)");
                cmd.Parameters.AddWithValue("withoutRole", (short)role);
            }
            if (!string.IsNullOrWhiteSpace(f.Search))
            {
                where.Add($"(upper(p.{LP.Code}) LIKE @like OR upper(p.{LP.DisplayName}) LIKE @like)");
                cmd.Parameters.AddWithValue("like", "%" + f.Search.Trim().ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            }
            var wh = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
            cmd.CommandText = $"SELECT count(*) FROM {LP.Table} p{wh}";
            total = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            cmd.Parameters.AddWithValue("lim", Math.Clamp(f.Limit, 1, 200));
            cmd.Parameters.AddWithValue("off", Math.Max(0, f.Offset));
            cmd.CommandText = $"SELECT p.{LP.Id}, p.{LP.Code}, p.{LP.DisplayName}, p.{LP.IsActive}, p.{LP.LinkState}, p.{LP.GlobalPartyId}, " +
                              $"EXISTS (SELECT 1 FROM {PR.Table} r WHERE r.{PR.LocalPartyId} = p.{LP.Id} AND r.{PR.Status} IN (1, 2)), p.{LP.Revision}, p.{LP.UpdatedAtUtc} " +
                              $"FROM {LP.Table} p{wh} ORDER BY p.{LP.Code}, p.{LP.Id} LIMIT @lim OFFSET @off";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetInt16(4), G(r, 5), r.GetBoolean(6), r.GetInt64(7), r.GetFieldValue<DateTimeOffset>(8)));
        }
        IReadOnlyDictionary<Guid, SharedIdentityView> linked;
        var unavailable = false;
        try { linked = await registry.GetLinkedIdentitiesAsync(companyId, [.. rows.Where(x => x.Global is not null).Select(x => x.Id)], ct); }
        catch (NpgsqlException) { linked = new Dictionary<Guid, SharedIdentityView>(); unavailable = true; }   // review M2: the list without the shared names
        return new PartyPage([.. rows.Select(x =>
        {
            var identity = linked.TryGetValue(x.Id, out var i) && i.GlobalPartyId == x.Global ? i : null;
            return new LocalPartySummary(x.Id, x.Code, x.Name, x.Active, x.State, identity?.LegalNameAr, identity?.LegalNameEn, x.Open, x.Rev, x.Updated);
        })], total, unavailable);
    }

    // ================================================================================================================ local writes

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == LP.UniqueCode)
        {
            return ConfigResult<T>.Invalid([new FieldError("code", "code_taken")]);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == PR.UniqueOpen)
        {
            return ConfigResult<T>.Fail(ConfigStatus.Conflict, "request_open");
        }
    }

    private static async Task<Guid> QueueRequest(CompanyAuditedWork work, Guid actor, Guid localId, PartyRequestKind kind, Guid? target, long? basedOn, PartyValidation.Identity v, DateTime now, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            $"INSERT INTO {PR.Table} ({PR.Id}, {PR.LocalPartyId}, {PR.Kind}, {PR.TargetGlobalPartyId}, {PR.BasedOnRevision}, {PR.PartyKind}, {PR.LegalNameAr}, {PR.LegalNameEn}, {PR.TaxNumber}, {PR.RegistrationNumber}, {PR.ProposedStatus}, " +
            $"{PR.Note}, {PR.Status}, {PR.Attempts}, {PR.CreatedBy}, {PR.CreatedAtUtc}, {PR.Revision}) VALUES (@id, @l, @k, @t, @based, @pk, @ar, @en, @tax, @reg, @ps, @note, 1, 0, @a, @now, 1)", work.Connection, work.Transaction))
        {
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("l", localId); ins.Parameters.AddWithValue("k", (short)kind);
            ins.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Uuid) { Value = target is { } t ? t : DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("based", NpgsqlDbType.Bigint) { Value = basedOn is { } b ? b : DBNull.Value });
            ins.Parameters.AddWithValue("pk", (short)v.Kind); ins.Parameters.AddWithValue("ar", v.LegalNameAr); ins.Parameters.AddWithValue("en", Db(v.LegalNameEn)); ins.Parameters.AddWithValue("tax", Db(v.TaxNumber));
            ins.Parameters.AddWithValue("reg", Db(v.RegistrationNumber)); ins.Parameters.AddWithValue("ps", (short)v.Status); ins.Parameters.AddWithValue("note", Db(v.Note));
            ins.Parameters.AddWithValue("a", actor); ins.Parameters.AddWithValue("now", now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.RequestQueued, ActorUserId: actor, SubjectId: localId, Detail: $"party request queued request={id:N} kind={(short)kind}"), ct);
        return id;
    }

    /// <summary>
    /// Creates a local party (pending) and, in the SAME company transaction, queues the proposal of its shared identity; then tries to deliver it (best effort: a failed delivery leaves the request
    /// pending for a later retry and does not fail the creation).
    /// </summary>
    public async Task<ConfigResult<LocalPartyView>> CreateAsync(Guid actor, Guid companyId, LocalPartyInput? input, CancellationToken ct = default)
    {
        var ok = PartyValidation.TryLocal(input, true, out var local, out var errors);
        PartyValidation.Identity identity = default!;
        if (input is not null)
        {
            if (!PartyValidation.TryIdentity(input.Identity, false, "identity.", out identity, out var identityErrors)) errors.AddRange(identityErrors);
        }
        if (!ok || errors.Count > 0) return ConfigResult<LocalPartyView>.Invalid(errors);
        Guid id = default, requestId = default;
        var created = await Mutate(companyId, async work =>
        {
            (id, requestId) = await CreateInWorkAsync(work, actor, local, identity, ct);
            return true;
        }, ct);
        if (!created.IsOk) return ConfigResult<LocalPartyView>.Fail(created.Status, created.Code!) with { Fields = created.Fields };
        await ProcessAsync(companyId, requestId, actor, ct);
        return ConfigResult<LocalPartyView>.Ok((await GetAsync(companyId, id, ct))!);
    }

    /// <summary>
    /// Creates a pending local party and queues the proposal of its shared identity INSIDE the caller's company transaction (the same two writes and audit events as <see cref="CreateAsync"/>), without
    /// delivering the request: the caller delivers after its own commit (<see cref="SyncAsync"/> is safe to repeat). Used by the party import adapter so a whole approved batch stays all-or-nothing.
    /// Inputs must already be validated by <see cref="PartyValidation"/>. A code that is already taken surfaces as the unique-code <see cref="PostgresException"/>.
    /// </summary>
    internal async Task<(Guid Id, Guid RequestId)> CreateInWorkAsync(CompanyAuditedWork work, Guid actor, PartyValidation.Local local, PartyValidation.Identity identity, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = Now;
        await using (var ins = new NpgsqlCommand(
            $"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.Notes}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) " +
            "VALUES (@id, @code, @name, @notes, @active, 1, 1, @a, @now, @now)", work.Connection, work.Transaction))
        {
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("code", local.Code); ins.Parameters.AddWithValue("name", local.DisplayName); ins.Parameters.AddWithValue("notes", Db(local.Notes));
            ins.Parameters.AddWithValue("active", local.IsActive); ins.Parameters.AddWithValue("a", actor); ins.Parameters.AddWithValue("now", now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.LocalPartyCreated, ActorUserId: actor, SubjectId: id, Detail: $"local party created code={local.Code}"), ct);
        var requestId = await QueueRequest(work, actor, id, PartyRequestKind.NewIdentity, null, null, identity, now, ct);
        return (id, requestId);
    }

    /// <summary>Changes the company-local fields only (code, display name, notes, active). The shared identity is changed only by a proposal (<see cref="ProposeAsync"/>).</summary>
    public async Task<ConfigResult<LocalPartyView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, LocalPartyInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<LocalPartyView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!PartyValidation.TryLocal(input, false, out var local, out var errors)) return ConfigResult<LocalPartyView>.Invalid(errors);
        var r = await Mutate(companyId, async work =>
        {
            var cur = await LoadLocal(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "party_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {LP.Table} SET {LP.Code} = @code, {LP.DisplayName} = @name, {LP.Notes} = @notes, {LP.IsActive} = @active, {LP.Revision} = {LP.Revision} + 1, {LP.UpdatedAtUtc} = @now WHERE {LP.Id} = @id",
                work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("code", local.Code); upd.Parameters.AddWithValue("name", local.DisplayName); upd.Parameters.AddWithValue("notes", Db(local.Notes));
                upd.Parameters.AddWithValue("active", local.IsActive); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.LocalPartyUpdated, ActorUserId: actor, SubjectId: id, Detail: $"local party updated r{cur.Revision + 1} code={local.Code} active={local.IsActive}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<LocalPartyView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        return ConfigResult<LocalPartyView>.Ok((await GetAsync(companyId, id, ct))!);
    }

    /// <summary>
    /// Queues a proposal of shared data for a local party: a CHANGE of the shared identity of a linked party, or a NEW proposal after a rejection. Refused while a request of the party is still open
    /// (pending or delivered). Fenced by the local party's revision; then delivered like a creation.
    /// </summary>
    public async Task<ConfigResult<LocalPartyView>> ProposeAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, SharedIdentityInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<LocalPartyView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        // review M1: a change records the revision of the shared identity it is based on (read through this company's own link, before the company transaction)
        IReadOnlyDictionary<Guid, SharedIdentityView> current;
        try { current = await registry.GetLinkedIdentitiesAsync(companyId, [id], ct); }
        catch (NpgsqlException) { return ConfigResult<LocalPartyView>.Fail(ConfigStatus.Conflict, "registry_unavailable"); }
        Guid requestId = default;
        var r = await Mutate(companyId, async work =>
        {
            var cur = await LoadLocal(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "party_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (cur.OpenRequest is not null) throw new Abort(ConfigStatus.Conflict, "request_open");
            var change = cur.LinkState == (int)PartyLinkState.Linked;
            if (!change && cur.LinkState != (int)PartyLinkState.Rejected) throw new Abort(ConfigStatus.Conflict, "invalid_status");
            if (!PartyValidation.TryIdentity(input, change, "", out var identity, out var errors)) throw new Abort(ConfigStatus.Invalid, "validation_failed", errors);
            long? basedOn = null;
            if (change)
            {
                if (!current.TryGetValue(id, out var linkedIdentity) || linkedIdentity.GlobalPartyId != cur.GlobalPartyId) throw new Abort(ConfigStatus.Conflict, "link_missing");
                // the revision the USER saw is required and must still be current: otherwise the proposal would silently be based on newer shared data
                if (input?.ViewedSharedRevision is not { } viewed) throw new Abort(ConfigStatus.PreconditionRequired, "shared_revision_required");
                if (viewed != linkedIdentity.Revision) throw new Abort(ConfigStatus.PreconditionFailed, "shared_revision_conflict", null, linkedIdentity.Revision);
                basedOn = linkedIdentity.Revision;
            }
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {LP.Table} SET {LP.LinkState} = @st, {LP.Revision} = {LP.Revision} + 1, {LP.UpdatedAtUtc} = @now WHERE {LP.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("st", (short)(change ? PartyLinkState.Linked : PartyLinkState.Pending)); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            requestId = await QueueRequest(work, actor, id, change ? PartyRequestKind.Change : PartyRequestKind.NewIdentity, change ? cur.GlobalPartyId : null, basedOn, identity, Now, ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<LocalPartyView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        await ProcessAsync(companyId, requestId, actor, ct);
        return ConfigResult<LocalPartyView>.Ok((await GetAsync(companyId, id, ct))!);
    }

    // ================================================================================================================ the cross-database protocol

    private sealed record RequestRow(Guid Id, Guid LocalPartyId, PartyRequestKind Kind, Guid? Target, PartyValidation.Identity Values, PartyRequestStatus Status, Guid CreatedBy, DateTimeOffset CreatedAtUtc, long? BasedOnRevision);

    private static async Task<RequestRow?> LoadRequest(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            $"SELECT {PR.Id}, {PR.LocalPartyId}, {PR.Kind}, {PR.TargetGlobalPartyId}, {PR.PartyKind}, {PR.LegalNameAr}, {PR.LegalNameEn}, {PR.TaxNumber}, {PR.RegistrationNumber}, {PR.ProposedStatus}, {PR.Note}, " +
            $"{PR.Status}, {PR.CreatedBy}, {PR.CreatedAtUtc}, {PR.BasedOnRevision} FROM {PR.Table} WHERE {PR.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        q.Parameters.AddWithValue("id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var values = new PartyValidation.Identity((PartyKind)r.GetInt16(4), r.GetString(5), Str(r, 6), Str(r, 7), Str(r, 8), (GlobalPartyStatus)r.GetInt16(9), Str(r, 10));
        return new RequestRow(r.GetGuid(0), r.GetGuid(1), (PartyRequestKind)r.GetInt16(2), G(r, 3), values, (PartyRequestStatus)r.GetInt16(11), r.GetGuid(12), r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetInt64(14));
    }

    private enum Step { Nothing, Delivered, Applied, Failed, Waiting, Unavailable }

    /// <summary>Runs a company transaction that may find nothing to do (another caller got there first): then nothing is written and false is returned.</summary>
    private async Task<bool> TryMutate(Guid companyId, Func<CompanyAuditedWork, Task> work, CancellationToken ct)
    {
        try { await transactions.ExecuteAsync(companyId, work, ct); return true; }
        catch (NothingToDo) { return false; }
    }

    /// <summary>
    /// Advances ONE request as far as it can go now: a pending request is delivered (idempotently) and the delivery recorded; a delivered request whose proposal is decided gets its outcome applied.
    /// Never throws for a registry failure: the failure is recorded on the request (attempts, short code) and the request stays pending.
    /// </summary>
    private async Task<Step> ProcessAsync(Guid companyId, Guid requestId, Guid actor, CancellationToken ct)
    {
        RequestRow? req;
        await using (var conn = await resolver.OpenAsync(companyId, ct)) req = await LoadRequest(conn, null, requestId, false, ct);
        if (req is null || req.Status == PartyRequestStatus.Completed) return Step.Nothing;

        PartyProposalState? proposal;
        if (req.Status == PartyRequestStatus.Pending)
        {
            PartyDelivery delivery;
            try
            {
                if (Fault is { } f1) await f1("before_registry");
                delivery = await registry.ReceiveAsync(companyId, new PartyRequestEnvelope(req.Id, req.LocalPartyId, req.Kind, req.Target, req.Values, req.CreatedBy, req.CreatedAtUtc, req.BasedOnRevision), ct);
                if (Fault is { } f2) await f2("after_registry");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not AuditIntegrityException)
            {
                var code = ex is NpgsqlException or AuditUnavailableException ? "registry_unavailable" : "delivery_interrupted";
                await TryMutate(companyId, async work =>
                {
                    var cur = await LoadRequest(work.Connection, work.Transaction, requestId, true, ct);
                    if (cur?.Status != PartyRequestStatus.Pending) throw new NothingToDo();
                    await using (var upd = new NpgsqlCommand(
                        $"UPDATE {PR.Table} SET {PR.Attempts} = {PR.Attempts} + 1, {PR.LastAttemptAtUtc} = @now, {PR.LastErrorCode} = @code, {PR.Revision} = {PR.Revision} + 1 WHERE {PR.Id} = @id", work.Connection, work.Transaction))
                    {
                        upd.Parameters.AddWithValue("id", requestId); upd.Parameters.AddWithValue("now", Now); upd.Parameters.AddWithValue("code", code);
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                    await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.DeliveryFailed, ActorUserId: actor, SubjectId: req.LocalPartyId, Detail: $"party request delivery failed request={requestId:N} code={code}"), ct);
                }, ct);
                return code == "registry_unavailable" ? Step.Unavailable : Step.Failed;
            }
            if (delivery.Refusal is { } refusal)
            {
                await TryMutate(companyId, async work =>
                {
                    var cur = await LoadRequest(work.Connection, work.Transaction, requestId, true, ct);
                    if (cur?.Status != PartyRequestStatus.Pending) throw new NothingToDo();
                    await Complete(work, cur, PartyRequestOutcome.Refused, null, refusal, ct);
                    if (cur.Kind == PartyRequestKind.NewIdentity) await SetLinkState(work, cur.LocalPartyId, PartyLinkState.Rejected, null, ct);
                    await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.RequestRefused, ActorUserId: actor, SubjectId: cur.LocalPartyId, Detail: $"party request refused by the registry request={requestId:N} code={refusal}"), ct);
                }, ct);
                return Step.Applied;
            }
            proposal = delivery.Proposal!;
        }
        else
        {
            try { proposal = await registry.GetProposalStateAsync(companyId, requestId, ct); }
            catch (NpgsqlException) { return Step.Unavailable; }        // review M2: the request stays delivered; a later attempt pulls the outcome
            if (proposal is null) return Step.Waiting;              // the registry does not know a delivered request: reported by the reconciliation, never fabricated
        }

        var step = Step.Waiting;
        await TryMutate(companyId, async work =>
        {
            var cur = await LoadRequest(work.Connection, work.Transaction, requestId, true, ct);
            if (cur is null || cur.Status == PartyRequestStatus.Completed) throw new NothingToDo();
            var wrote = false;
            if (cur.Status == PartyRequestStatus.Pending)
            {
                await using (var upd = new NpgsqlCommand(
                    $"UPDATE {PR.Table} SET {PR.Status} = 2, {PR.Attempts} = {PR.Attempts} + 1, {PR.LastAttemptAtUtc} = @now, {PR.LastErrorCode} = NULL, {PR.DeliveredAtUtc} = @now, {PR.Revision} = {PR.Revision} + 1 WHERE {PR.Id} = @id",
                    work.Connection, work.Transaction))
                {
                    upd.Parameters.AddWithValue("id", requestId); upd.Parameters.AddWithValue("now", Now);
                    await upd.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.RequestDelivered, ActorUserId: actor, SubjectId: cur.LocalPartyId, Detail: $"party request delivered request={requestId:N} proposal={proposal.Id:N}"), ct);
                wrote = true;
                step = Step.Delivered;
            }
            if (proposal.Status != PartyProposalStatus.Pending)
            {
                await ApplyOutcome(work, cur, proposal, actor, ct);
                wrote = true;
                step = Step.Applied;
            }
            if (!wrote) throw new NothingToDo();
        }, ct);
        return step;
    }

    private async Task Complete(CompanyAuditedWork work, RequestRow req, PartyRequestOutcome outcome, Guid? outcomeGlobal, string? errorCode, CancellationToken ct, string? decisionNote = null)
    {
        await using var upd = new NpgsqlCommand(
            $"UPDATE {PR.Table} SET {PR.Status} = 3, {PR.Outcome} = @o, {PR.OutcomeGlobalPartyId} = @g, {PR.CompletedAtUtc} = @now, {PR.DeliveredAtUtc} = coalesce({PR.DeliveredAtUtc}, CASE WHEN @o = 3 THEN NULL ELSE @now END), " +
            $"{PR.LastErrorCode} = @code, {PR.DecisionNote} = @dn, {PR.Revision} = {PR.Revision} + 1 WHERE {PR.Id} = @id", work.Connection, work.Transaction);
        upd.Parameters.Add(new NpgsqlParameter("dn", NpgsqlDbType.Varchar) { Value = (object?)decisionNote ?? DBNull.Value });
        upd.Parameters.AddWithValue("id", req.Id); upd.Parameters.AddWithValue("o", (short)outcome);
        upd.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Uuid) { Value = outcomeGlobal is { } g ? g : DBNull.Value });
        upd.Parameters.AddWithValue("now", Now);
        upd.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = (object?)errorCode ?? DBNull.Value });
        await upd.ExecuteNonQueryAsync(ct);
    }

    private async Task SetLinkState(CompanyAuditedWork work, Guid localId, PartyLinkState state, Guid? global, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand(
            $"UPDATE {LP.Table} SET {LP.LinkState} = @st, {LP.GlobalPartyId} = coalesce(@g, {LP.GlobalPartyId}), {LP.Revision} = {LP.Revision} + 1, {LP.UpdatedAtUtc} = @now WHERE {LP.Id} = @id", work.Connection, work.Transaction);
        upd.Parameters.AddWithValue("id", localId); upd.Parameters.AddWithValue("st", (short)state);
        upd.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Uuid) { Value = global is { } g ? g : DBNull.Value });
        upd.Parameters.AddWithValue("now", Now);
        await upd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Applies a central decision to the request and (for a new identity) to the local party, in the caller's company transaction with one audit event.</summary>
    private async Task ApplyOutcome(CompanyAuditedWork work, RequestRow req, PartyProposalState p, Guid actor, CancellationToken ct)
    {
        var approved = p.Status == PartyProposalStatus.Approved;
        if (req.Kind == PartyRequestKind.NewIdentity)
        {
            if (approved)
            {
                await Complete(work, req, PartyRequestOutcome.Approved, p.OutcomeGlobalPartyId, null, ct);
                await SetLinkState(work, req.LocalPartyId, PartyLinkState.Linked, p.OutcomeGlobalPartyId, ct);
                await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.IdentityLinked, ActorUserId: actor, SubjectId: req.LocalPartyId,
                    Detail: $"local party linked global={p.OutcomeGlobalPartyId:N} request={req.Id:N} resolution={(short?)p.Resolution}"), ct);
            }
            else
            {
                await Complete(work, req, PartyRequestOutcome.Rejected, null, null, ct, p.DecisionNote);      // review M3: the proposing company sees why
                await SetLinkState(work, req.LocalPartyId, PartyLinkState.Rejected, null, ct);
                await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.ProposalRejected, ActorUserId: actor, SubjectId: req.LocalPartyId, Detail: $"party proposal rejected request={req.Id:N}"), ct);
            }
            return;
        }
        await Complete(work, req, approved ? PartyRequestOutcome.Approved : PartyRequestOutcome.Rejected, approved ? p.OutcomeGlobalPartyId : null, null, ct, approved ? null : p.DecisionNote);
        await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.ChangeCompleted, ActorUserId: actor, SubjectId: req.LocalPartyId,
            Detail: $"party change {(approved ? "approved" : "rejected")} request={req.Id:N}"), ct);
    }

    /// <summary>
    /// Synchronizes the request log of the company with the registry: every open request is delivered (pending) or gets its decided outcome applied (delivered). Safe to repeat at any time.
    /// </summary>
    public async Task<PartySyncResult> SyncAsync(Guid actor, Guid companyId, CancellationToken ct = default)
    {
        var open = new List<Guid>();
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        await using (var q = new NpgsqlCommand($"SELECT {PR.Id} FROM {PR.Table} WHERE {PR.Status} IN (1, 2) ORDER BY {PR.CreatedAtUtc}, {PR.Id}", conn))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) open.Add(r.GetGuid(0));
        int delivered = 0, applied = 0, failed = 0, waiting = 0;
        var unavailable = false;
        foreach (var id in open)
        {
            switch (await ProcessAsync(companyId, id, actor, ct))
            {
                case Step.Delivered: delivered++; break;
                case Step.Applied: applied++; break;
                case Step.Failed: failed++; break;
                case Step.Waiting: waiting++; break;
                case Step.Unavailable: failed++; unavailable = true; break;
            }
        }
        return new PartySyncResult(delivered, applied, failed, waiting, unavailable);
    }

    /// <summary>
    /// Compares this company's request log and links with the registry's proposals and links OF THIS COMPANY and reports every mismatch (docs/PHASE-3.9A.md D6). With <paramref name="repair"/>,
    /// the repairable findings (undelivered requests, unrecorded deliveries, unapplied outcomes) are fixed by the same idempotent steps as <see cref="SyncAsync"/>, and one audit event records the run.
    /// Without it nothing is written. Local state the registry does not confirm is reported, never fabricated.
    /// </summary>
    public async Task<PartyReconciliationReport> ReconcileAsync(Guid actor, Guid companyId, bool repair, CancellationToken ct = default)
    {
        var locals = new Dictionary<Guid, (int State, Guid? Global)>();
        var requests = new List<(Guid Id, Guid Local, int Kind, int Status, int? Outcome)>();
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        {
            await using (var q = new NpgsqlCommand($"SELECT {LP.Id}, {LP.LinkState}, {LP.GlobalPartyId} FROM {LP.Table}", conn))
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) locals[r.GetGuid(0)] = (r.GetInt16(1), G(r, 2));
            await using (var q = new NpgsqlCommand($"SELECT {PR.Id}, {PR.LocalPartyId}, {PR.Kind}, {PR.Status}, {PR.Outcome} FROM {PR.Table} ORDER BY {PR.CreatedAtUtc}, {PR.Id}", conn))
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) requests.Add((r.GetGuid(0), r.GetGuid(1), r.GetInt16(2), r.GetInt16(3), r.IsDBNull(4) ? null : r.GetInt16(4)));
        }
        PartyRegistrySnapshot snapshot;
        try { snapshot = await registry.SnapshotAsync(companyId, ct); }
        catch (NpgsqlException) { return new PartyReconciliationReport([new("registry_unavailable", null, null, null, false, false)], repair, true); }   // review M2
        var byKey = snapshot.Proposals.ToDictionary(p => p.RequestKey);
        var findings = new List<PartyReconciliationFinding>();
        var openLocals = new HashSet<Guid>();
        foreach (var q in requests)
        {
            byKey.TryGetValue(q.Id, out var p);
            if (q.Status == (int)PartyRequestStatus.Pending)
            {
                openLocals.Add(q.Local);
                findings.Add(new(p is null ? "request_not_delivered" : "request_delivered_not_recorded", q.Local, q.Id, p?.OutcomeGlobalPartyId, true, false));
            }
            else if (q.Status == (int)PartyRequestStatus.Delivered)
            {
                openLocals.Add(q.Local);
                if (p is null) findings.Add(new("request_missing_in_system", q.Local, q.Id, null, false, false));
                else if (p.Status != PartyProposalStatus.Pending) findings.Add(new("outcome_not_applied", q.Local, q.Id, p.OutcomeGlobalPartyId, true, false));
            }
            else if (q.Outcome is 1 or 2 && p is null) findings.Add(new("request_missing_in_system", q.Local, q.Id, null, false, false));
        }
        foreach (var (local, (state, global)) in locals)
        {
            var hasLink = snapshot.LinksByLocalParty.TryGetValue(local, out var linkedTo);
            if (state == (int)PartyLinkState.Linked)
            {
                if (!hasLink) findings.Add(new("link_missing_in_system", local, null, global, false, false));
                else if (linkedTo != global) findings.Add(new("link_mismatch", local, null, linkedTo, false, false));
            }
            else if (hasLink && !openLocals.Contains(local)) findings.Add(new("link_missing_locally", local, null, linkedTo, false, false));
        }
        foreach (var (local, global) in snapshot.LinksByLocalParty)
            if (!locals.ContainsKey(local)) findings.Add(new("orphan_system_link", local, null, global, false, false));
        var known = requests.Select(r => r.Id).ToHashSet();
        foreach (var p in snapshot.Proposals)
            if (!known.Contains(p.RequestKey)) findings.Add(new("proposal_unknown_locally", p.LocalPartyId, p.RequestKey, p.OutcomeGlobalPartyId, false, false, p.Id));

        if (!repair) return new PartyReconciliationReport(findings, false);
        var repaired = 0;
        for (var i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            if (!f.Repairable || f.RequestId is not { } rid) continue;
            var step = await ProcessAsync(companyId, rid, actor, ct);
            if (step is Step.Delivered or Step.Applied) { findings[i] = f with { Repaired = true }; repaired++; }
        }
        if (repaired > 0)
            await transactions.ExecuteAsync(companyId, async work =>
                await work.AppendAuditAsync(new AuditEventData(PartyAuditEvents.ReconciliationRepaired, ActorUserId: actor, Detail: $"party reconciliation repaired={repaired} findings={findings.Count}"), ct), ct);
        return new PartyReconciliationReport(findings, true);
    }
}

/// <summary>
/// Authorized entry point of the company side of the party registry (the only thing the API calls). Permissions are company-wide (<c>company.parties.*</c>) and re-read on every call; the company is
/// always the caller's session company, so a user only ever reaches the local parties of that company and the shared identities it is linked to.
/// </summary>
public sealed class PartyManagementService(AccessService access, PartyService parties)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    public async Task<PartyCapabilities> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        return new PartyCapabilities(eff.HasCompanyWide(PermissionCatalog.Codes.PartiesView), eff.HasCompanyWide(PermissionCatalog.Codes.PartiesCreate), eff.HasCompanyWide(PermissionCatalog.Codes.PartiesUpdate),
            eff.HasCompanyWide(PermissionCatalog.Codes.PartiesSubmit), eff.HasCompanyWide(PermissionCatalog.Codes.PartiesConfigure));
    }

    private Task<bool> Can(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<PartyPage>> ListAsync(Guid user, Guid company, PartyFilter filter, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.PartiesView, ct) ? ConfigResult<PartyPage>.Ok(await parties.ListAsync(company, filter, ct)) : Denied<PartyPage>();

    public async Task<ConfigResult<LocalPartyView>> GetAsync(Guid user, Guid company, Guid id, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.PartiesView, ct)) return Denied<LocalPartyView>();
        var v = await parties.GetAsync(company, id, ct);
        return v is null ? ConfigResult<LocalPartyView>.Fail(ConfigStatus.NotFound, "party_not_found") : ConfigResult<LocalPartyView>.Ok(v);
    }

    public async Task<ConfigResult<LocalPartyView>> CreateAsync(Guid user, Guid company, LocalPartyInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.PartiesCreate, ct) ? await parties.CreateAsync(user, company, input, ct) : Denied<LocalPartyView>();

    public async Task<ConfigResult<LocalPartyView>> UpdateAsync(Guid user, Guid company, Guid id, long? revision, LocalPartyInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.PartiesUpdate, ct) ? await parties.UpdateAsync(user, company, id, revision, input, ct) : Denied<LocalPartyView>();

    public async Task<ConfigResult<LocalPartyView>> ProposeAsync(Guid user, Guid company, Guid id, long? revision, SharedIdentityInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.PartiesSubmit, ct) ? await parties.ProposeAsync(user, company, id, revision, input, ct) : Denied<LocalPartyView>();

    /// <summary>Synchronizing only advances requests that were already queued (idempotent); it is allowed to whoever may create, propose or configure (docs/PHASE-3.9A.md D2).</summary>
    public async Task<ConfigResult<PartySyncResult>> SyncAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var caps = await CapabilitiesAsync(user, company, ct);
        return caps.Create || caps.Submit || caps.Configure ? ConfigResult<PartySyncResult>.Ok(await parties.SyncAsync(user, company, ct)) : Denied<PartySyncResult>();
    }

    public async Task<ConfigResult<PartyReconciliationReport>> ReconcileAsync(Guid user, Guid company, bool repair, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.PartiesConfigure, ct) ? ConfigResult<PartyReconciliationReport>.Ok(await parties.ReconcileAsync(user, company, repair, ct)) : Denied<PartyReconciliationReport>();
}
