using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Npgsql;
using NpgsqlTypes;
using GP = Hasm.Infrastructure.SystemDatabase.SystemSchema.GlobalParty;
using PP = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyProposal;
using PL = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyLink;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;

namespace Hasm.Infrastructure.Parties;

/// <summary>A request of a company's request log as delivered to the registry. <see cref="RequestKey"/> is the outbox id: the idempotency key of the delivery.</summary>
public sealed record PartyRequestEnvelope(Guid RequestKey, Guid LocalPartyId, PartyRequestKind Kind, Guid? TargetGlobalPartyId, PartyValidation.Identity Values, Guid ProposedBy, DateTimeOffset ProposedAtUtc);

/// <summary>What the registry tells a company about one of ITS proposals.</summary>
public sealed record PartyProposalState(Guid Id, Guid RequestKey, Guid LocalPartyId, PartyRequestKind Kind, PartyProposalStatus Status, PartyProposalResolution? Resolution, Guid? OutcomeGlobalPartyId);

/// <summary>The registry state of one company only (for its reconciliation): its proposals and its links. Never anything of another company.</summary>
public sealed record PartyRegistrySnapshot(IReadOnlyList<PartyProposalState> Proposals, IReadOnlyDictionary<Guid, Guid> LinksByLocalParty);

/// <summary>Outcome of a delivery: the proposal (new or already existing for this key), or the code of a permanent refusal.</summary>
public sealed record PartyDelivery(PartyProposalState? Proposal, string? Refusal);

/// <summary>
/// The party registry in the SYSTEM database (Phase 3.9A): shared identities (<c>GlobalPartyId</c>), central proposals and links. Everything a company asks is scoped to that company INSIDE the query
/// (a company only ever reaches its own proposals and the identities it is linked to through its own links). The central decisions (approve, reject) are reserved to active identity administrators,
/// checked in the deciding transaction, and the decider is never the proposer (also a PostgreSQL CHECK). Every write and its system audit event commit in ONE system transaction.
/// This store never touches a company database: the company side (<see cref="PartyService"/>) owns the request log and calls this store across the database boundary.
/// </summary>
public sealed class PartyRegistryStore(Func<string> connectionString, AuditTrail audit, TimeProvider clock)
{
    public PartyRegistryStore(string connectionString, AuditTrail audit, TimeProvider clock) : this(() => connectionString, audit, clock) { }

    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static object Db<T>(T? v) where T : class => (object?)v ?? DBNull.Value;
    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString());
        await conn.OpenAsync(ct);
        return conn;
    }

    private Task<AuditReceipt> AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, short type, Guid? actor, Guid subject, string detail, CancellationToken ct) =>
        audit.AppendAsync(conn, tx, AuditStream.System, AuditCanonical.FromMicros(AuditCanonical.ToMicros(clock.GetUtcNow())), new AuditEventData(type, actor, null, subject, detail), ct);

    // ======================================================================================================== company side (scoped to one company)

    private const string StateCols = $"{PP.Id}, {PP.RequestKey}, {PP.LocalPartyId}, {PP.Kind}, {PP.Status}, {PP.Resolution}, {PP.OutcomeGlobalPartyId}";

    private static PartyProposalState ReadState(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), (PartyRequestKind)r.GetInt16(3), (PartyProposalStatus)r.GetInt16(4),
        r.IsDBNull(5) ? null : (PartyProposalResolution)r.GetInt16(5), G(r, 6));

    private static async Task<PartyProposalState?> StateByKey(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid companyId, Guid key, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {StateCols} FROM {PP.Table} WHERE {PP.CompanyId} = @c AND {PP.RequestKey} = @k", conn, tx);
        q.Parameters.AddWithValue("c", companyId);
        q.Parameters.AddWithValue("k", key);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadState(r) : null;
    }

    /// <summary>The proposal this company's request became, or null when the registry never received it.</summary>
    public async Task<PartyProposalState?> GetProposalStateAsync(Guid companyId, Guid requestKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await StateByKey(conn, null, companyId, requestKey, ct);
    }

    /// <summary>
    /// Idempotent delivery of one request: inserts the proposal keyed by (company, request id) only when it does not exist yet, with its system audit event, in one system transaction; a repeated
    /// delivery returns the existing proposal and writes nothing. A request the registry can never accept is refused permanently (a change of a party the company is not linked to, a new identity for a
    /// local party that is already linked, a second undecided proposal for the same local party).
    /// </summary>
    public async Task<PartyDelivery> ReceiveAsync(Guid companyId, PartyRequestEnvelope e, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // one delivery per (company, key) at a time: a concurrent duplicate waits and then finds the proposal
        await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 39))", conn, tx))
        {
            lk.Parameters.AddWithValue("k", $"hasm.party.request.{companyId:N}.{e.RequestKey:N}");
            await lk.ExecuteNonQueryAsync(ct);
        }
        if (await StateByKey(conn, tx, companyId, e.RequestKey, ct) is { } existing) return new PartyDelivery(existing, null);

        Guid? linked;
        await using (var q = new NpgsqlCommand($"SELECT {PL.GlobalPartyId} FROM {PL.Table} WHERE {PL.CompanyId} = @c AND {PL.LocalPartyId} = @l", conn, tx))
        {
            q.Parameters.AddWithValue("c", companyId);
            q.Parameters.AddWithValue("l", e.LocalPartyId);
            linked = await q.ExecuteScalarAsync(ct) is Guid g ? g : null;
        }
        if (e.Kind == PartyRequestKind.NewIdentity && linked is not null) return new PartyDelivery(null, "already_linked");
        if (e.Kind == PartyRequestKind.Change && (linked is null || linked != e.TargetGlobalPartyId)) return new PartyDelivery(null, "not_linked");

        var id = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            $"INSERT INTO {PP.Table} ({PP.Id}, {PP.CompanyId}, {PP.RequestKey}, {PP.LocalPartyId}, {PP.Kind}, {PP.TargetGlobalPartyId}, {PP.PartyKind}, {PP.LegalNameAr}, {PP.LegalNameEn}, {PP.TaxNumber}, " +
            $"{PP.RegistrationNumber}, {PP.ProposedStatus}, {PP.Note}, {PP.ProposedBy}, {PP.ProposedAtUtc}, {PP.Status}, {PP.Revision}) " +
            "VALUES (@id, @c, @k, @l, @kind, @t, @pk, @ar, @en, @tax, @reg, @ps, @note, @by, @at, 1, 1)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("c", companyId); ins.Parameters.AddWithValue("k", e.RequestKey); ins.Parameters.AddWithValue("l", e.LocalPartyId);
            ins.Parameters.AddWithValue("kind", (short)e.Kind); ins.Parameters.AddWithValue("t", e.TargetGlobalPartyId is { } t ? t : DBNull.Value); ins.Parameters.AddWithValue("pk", (short)e.Values.Kind);
            ins.Parameters.AddWithValue("ar", e.Values.LegalNameAr); ins.Parameters.AddWithValue("en", Db(e.Values.LegalNameEn)); ins.Parameters.AddWithValue("tax", Db(e.Values.TaxNumber));
            ins.Parameters.AddWithValue("reg", Db(e.Values.RegistrationNumber)); ins.Parameters.AddWithValue("ps", (short)e.Values.Status); ins.Parameters.AddWithValue("note", Db(e.Values.Note));
            ins.Parameters.AddWithValue("by", e.ProposedBy); ins.Parameters.AddWithValue("at", e.ProposedAtUtc.UtcDateTime);
            try { await ins.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == PP.UniqueOpenPerLocal)
            {
                return new PartyDelivery(null, "open_proposal_exists");
            }
        }
        var receipt = await AuditAsync(conn, tx, PartyAuditEvents.ProposalReceived, e.ProposedBy, id, $"party proposal received company={companyId:N} request={e.RequestKey:N} kind={(short)e.Kind}", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return new PartyDelivery(new PartyProposalState(id, e.RequestKey, e.LocalPartyId, e.Kind, PartyProposalStatus.Pending, null, null), null);
    }

    private const string IdentityCols = $"{GP.Id}, {GP.PartyKind}, {GP.LegalNameAr}, {GP.LegalNameEn}, {GP.TaxNumber}, {GP.RegistrationNumber}, {GP.Status}, {GP.Revision}";
    private static SharedIdentityView ReadIdentity(NpgsqlDataReader r, int o = 0) =>
        new(r.GetGuid(o), r.GetInt16(o + 1), r.GetString(o + 2), Str(r, o + 3), Str(r, o + 4), Str(r, o + 5), r.GetInt16(o + 6), r.GetInt64(o + 7));

    /// <summary>
    /// The shared identities of the given local parties of ONE company, through that company's links only (a local party without a link of this company yields nothing, whatever id it claims).
    /// Returns, per local party, the linked <c>GlobalPartyId</c> and its identifying data. Never another company's link, local party or anything financial.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, SharedIdentityView>> GetLinkedIdentitiesAsync(Guid companyId, IReadOnlyCollection<Guid> localPartyIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, SharedIdentityView>();
        if (localPartyIds.Count == 0) return result;
        await using var conn = await OpenAsync(ct);
        await using var q = new NpgsqlCommand(
            $"SELECT l.{PL.LocalPartyId}, {string.Join(", ", IdentityCols.Split(", ").Select(c => "g." + c))} FROM {PL.Table} l JOIN {GP.Table} g ON g.{GP.Id} = l.{PL.GlobalPartyId} " +
            $"WHERE l.{PL.CompanyId} = @c AND l.{PL.LocalPartyId} = ANY(@ids)", conn);
        q.Parameters.AddWithValue("c", companyId);
        q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = localPartyIds.ToArray() });
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetGuid(0)] = ReadIdentity(r, 1);
        return result;
    }

    /// <summary>The proposals and links of ONE company (its reconciliation input).</summary>
    public async Task<PartyRegistrySnapshot> SnapshotAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var proposals = new List<PartyProposalState>();
        await using (var q = new NpgsqlCommand($"SELECT {StateCols} FROM {PP.Table} WHERE {PP.CompanyId} = @c", conn))
        {
            q.Parameters.AddWithValue("c", companyId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) proposals.Add(ReadState(r));
        }
        var links = new Dictionary<Guid, Guid>();
        await using (var q = new NpgsqlCommand($"SELECT {PL.LocalPartyId}, {PL.GlobalPartyId} FROM {PL.Table} WHERE {PL.CompanyId} = @c", conn))
        {
            q.Parameters.AddWithValue("c", companyId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) links[r.GetGuid(0)] = r.GetGuid(1);
        }
        return new PartyRegistrySnapshot(proposals, links);
    }

    // ======================================================================================================== central side (identity administrators only)

    /// <summary>True when the user exists, is active and is an identity administrator (the only central role, docs/PHASE-3.9A.md D1).</summary>
    public async Task<bool> IsCentralApproverAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await IsCentralApprover(conn, null, userId, ct);
    }

    private static async Task<bool> IsCentralApprover(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid userId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {U.State} = 0 AND {U.IsIdentityAdministrator} FROM {U.Table} WHERE {U.Id} = @u{(tx is null ? "" : " FOR SHARE")}", conn, tx);
        q.Parameters.AddWithValue("u", userId);
        return await q.ExecuteScalarAsync(ct) is true;
    }

    public async Task<ConfigResult<PartyProposalPage>> ListProposalsAsync(Guid actor, int? status, int offset, int limit, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        if (!await IsCentralApprover(conn, null, actor, ct)) return ConfigResult<PartyProposalPage>.Fail(ConfigStatus.Forbidden, "forbidden");
        await using var cmd = new NpgsqlCommand { Connection = conn };
        var where = "";
        if (status is { } s) { where = $" WHERE p.{PP.Status} = @s"; cmd.Parameters.AddWithValue("s", (short)s); }
        cmd.CommandText = $"SELECT count(*) FROM {PP.Table} p{where}";
        var total = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 200));
        cmd.Parameters.AddWithValue("off", Math.Max(0, offset));
        cmd.CommandText = $"SELECT p.{PP.Id}, p.{PP.CompanyId}, c.{Co.DisplayNameAr}, c.{Co.DisplayNameEn}, p.{PP.Kind}, p.{PP.LegalNameAr}, p.{PP.LegalNameEn}, p.{PP.TaxNumber}, p.{PP.RegistrationNumber}, " +
                          $"p.{PP.ProposedAtUtc}, p.{PP.Status}, p.{PP.Revision} FROM {PP.Table} p JOIN {Co.Table} c ON c.{Co.Id} = p.{PP.CompanyId}{where} " +
                          $"ORDER BY p.{PP.Status}, p.{PP.ProposedAtUtc}, p.{PP.Id} LIMIT @lim OFFSET @off";
        var items = new List<PartyProposalSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new PartyProposalSummary(r.GetGuid(0), r.GetGuid(1), Str(r, 2), Str(r, 3), r.GetInt16(4), r.GetString(5), Str(r, 6), Str(r, 7), Str(r, 8), r.GetFieldValue<DateTimeOffset>(9), r.GetInt16(10), r.GetInt64(11)));
        return ConfigResult<PartyProposalPage>.Ok(new PartyProposalPage(items, total));
    }

    private static async Task<PartyProposalView?> LoadProposal(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        PartyProposalView? v;
        await using (var q = new NpgsqlCommand(
            $"SELECT p.{PP.Id}, p.{PP.CompanyId}, c.{Co.DisplayNameAr}, c.{Co.DisplayNameEn}, p.{PP.LocalPartyId}, p.{PP.Kind}, p.{PP.TargetGlobalPartyId}, p.{PP.PartyKind}, p.{PP.LegalNameAr}, p.{PP.LegalNameEn}, " +
            $"p.{PP.TaxNumber}, p.{PP.RegistrationNumber}, p.{PP.ProposedStatus}, p.{PP.Note}, p.{PP.ProposedBy}, u.{U.DisplayName}, p.{PP.ProposedAtUtc}, p.{PP.Status}, p.{PP.Resolution}, p.{PP.OutcomeGlobalPartyId}, " +
            $"p.{PP.DecidedBy}, p.{PP.DecidedAtUtc}, p.{PP.DecisionNote}, p.{PP.Revision} " +
            $"FROM {PP.Table} p JOIN {Co.Table} c ON c.{Co.Id} = p.{PP.CompanyId} JOIN {U.Table} u ON u.{U.Id} = p.{PP.ProposedBy} WHERE p.{PP.Id} = @id{(forUpdate ? " FOR UPDATE OF p" : "")}", conn, tx))
        {
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            v = await r.ReadAsync(ct)
                ? new PartyProposalView(r.GetGuid(0), r.GetGuid(1), Str(r, 2), Str(r, 3), r.GetGuid(4), r.GetInt16(5), G(r, 6), r.GetInt16(7), r.GetString(8), Str(r, 9), Str(r, 10), Str(r, 11), r.GetInt16(12),
                    Str(r, 13), r.GetGuid(14), Str(r, 15), r.GetFieldValue<DateTimeOffset>(16), r.GetInt16(17), r.IsDBNull(18) ? null : r.GetInt16(18), G(r, 19), G(r, 20),
                    r.IsDBNull(21) ? null : r.GetFieldValue<DateTimeOffset>(21), Str(r, 22), r.GetInt64(23), null, [])
                : null;
        }
        if (v is null) return null;
        SharedIdentityView? target = null;
        if (v.TargetGlobalPartyId is { } t)
        {
            await using var q = new NpgsqlCommand($"SELECT {IdentityCols} FROM {GP.Table} WHERE {GP.Id} = @t", conn, tx);
            q.Parameters.AddWithValue("t", t);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) target = ReadIdentity(r);
        }
        // possible duplicates: same identifier or same Arabic legal name (central approver only)
        var matches = new List<SharedIdentityView>();
        await using (var q = new NpgsqlCommand(
            $"SELECT {IdentityCols} FROM {GP.Table} WHERE ({GP.TaxNumber} = @tax OR {GP.RegistrationNumber} = @reg OR lower({GP.LegalNameAr}) = lower(@ar)) AND {GP.Id} IS DISTINCT FROM @t ORDER BY {GP.LegalNameAr} LIMIT 20", conn, tx))
        {
            q.Parameters.AddWithValue("tax", (object?)v.TaxNumber ?? DBNull.Value);
            q.Parameters.AddWithValue("reg", (object?)v.RegistrationNumber ?? DBNull.Value);
            q.Parameters.AddWithValue("ar", v.LegalNameAr);
            q.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Uuid) { Value = (object?)v.TargetGlobalPartyId ?? DBNull.Value });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) matches.Add(ReadIdentity(r));
        }
        return v with { Target = target, Matches = matches };
    }

    public async Task<ConfigResult<PartyProposalView>> GetProposalAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        if (!await IsCentralApprover(conn, null, actor, ct)) return ConfigResult<PartyProposalView>.Fail(ConfigStatus.Forbidden, "forbidden");
        var v = await LoadProposal(conn, null, id, false, ct);
        return v is null ? ConfigResult<PartyProposalView>.Fail(ConfigStatus.NotFound, "proposal_not_found") : ConfigResult<PartyProposalView>.Ok(v);
    }

    /// <summary>Registry search for the central approver (name contains, or identifier equals); at most 50 identities.</summary>
    public async Task<ConfigResult<IReadOnlyList<SharedIdentityView>>> SearchIdentitiesAsync(Guid actor, string? term, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        if (!await IsCentralApprover(conn, null, actor, ct)) return ConfigResult<IReadOnlyList<SharedIdentityView>>.Fail(ConfigStatus.Forbidden, "forbidden");
        var t = term?.Trim() ?? "";
        if (t.Length == 0) return ConfigResult<IReadOnlyList<SharedIdentityView>>.Ok([]);
        await using var q = new NpgsqlCommand(
            $"SELECT {IdentityCols} FROM {GP.Table} WHERE lower({GP.LegalNameAr}) LIKE @like OR lower(coalesce({GP.LegalNameEn}, '')) LIKE @like OR {GP.TaxNumber} = @id OR {GP.RegistrationNumber} = @id " +
            $"ORDER BY {GP.LegalNameAr}, {GP.Id} LIMIT 50", conn);
        q.Parameters.AddWithValue("like", "%" + t.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        q.Parameters.AddWithValue("id", System.Text.RegularExpressions.Regex.Replace(t, @"\s+", "").ToUpperInvariant());
        var list = new List<SharedIdentityView>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadIdentity(r));
        return ConfigResult<IReadOnlyList<SharedIdentityView>>.Ok(list);
    }

    private async Task<ConfigResult<PartyProposalView>> Decide(Guid actor, Guid id, long? expectedRevision, Func<NpgsqlConnection, NpgsqlTransaction, PartyProposalView, Task<AuditReceipt>> body, CancellationToken ct)
    {
        if (expectedRevision is null) return ConfigResult<PartyProposalView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            if (!await IsCentralApprover(conn, tx, actor, ct)) throw new Abort(ConfigStatus.Forbidden, "forbidden");
            var cur = await LoadProposal(conn, tx, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "proposal_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (cur.Status != (int)PartyProposalStatus.Pending) throw new Abort(ConfigStatus.Conflict, "invalid_status");
            if (cur.ProposedBy == actor) throw new Abort(ConfigStatus.Conflict, "self_approval_not_allowed");
            var receipt = await body(conn, tx, cur);
            var after = (await LoadProposal(conn, tx, id, false, ct))!;
            await audit.CommitAsync(tx, receipt, ct);
            return ConfigResult<PartyProposalView>.Ok(after);
        }
        catch (Abort a) { return a.To<PartyProposalView>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && (ex.ConstraintName == GP.UniqueTaxNumber || ex.ConstraintName == GP.UniqueRegistrationNumber))
        {
            return ConfigResult<PartyProposalView>.Fail(ConfigStatus.Conflict, "identifier_in_use");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == PL.UniqueCompanyGlobal)
        {
            return ConfigResult<PartyProposalView>.Fail(ConfigStatus.Conflict, "already_linked_in_company");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == PL.PrimaryKey)
        {
            return ConfigResult<PartyProposalView>.Fail(ConfigStatus.Conflict, "local_party_already_linked");
        }
    }

    private async Task MarkDecided(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, PartyProposalStatus status, PartyProposalResolution? resolution, Guid? outcome, Guid actor, string? note, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand(
            $"UPDATE {PP.Table} SET {PP.Status} = @s, {PP.Resolution} = @r, {PP.OutcomeGlobalPartyId} = @o, {PP.DecidedBy} = @a, {PP.DecidedAtUtc} = @now, {PP.DecisionNote} = @n, {PP.Revision} = {PP.Revision} + 1 WHERE {PP.Id} = @id", conn, tx);
        upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("s", (short)status);
        upd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Smallint) { Value = resolution is { } rr ? (short)rr : DBNull.Value });
        upd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Uuid) { Value = outcome is { } o ? o : DBNull.Value });
        upd.Parameters.AddWithValue("a", actor); upd.Parameters.AddWithValue("now", Now); upd.Parameters.AddWithValue("n", Db(note));
        await upd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertLink(NpgsqlConnection conn, NpgsqlTransaction tx, PartyProposalView p, Guid global, CancellationToken ct)
    {
        await using var ins = new NpgsqlCommand($"INSERT INTO {PL.Table} ({PL.CompanyId}, {PL.LocalPartyId}, {PL.GlobalPartyId}, {PL.ProposalId}, {PL.LinkedAtUtc}) VALUES (@c, @l, @g, @p, @now)", conn, tx);
        ins.Parameters.AddWithValue("c", p.CompanyId); ins.Parameters.AddWithValue("l", p.LocalPartyId); ins.Parameters.AddWithValue("g", global); ins.Parameters.AddWithValue("p", p.Id); ins.Parameters.AddWithValue("now", Now);
        await ins.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Approves a pending proposal. A new-identity proposal either creates a new shared identity from the proposed values (<paramref name="linkTo"/> null) or links the local party to an existing ACTIVE
    /// identity; a change proposal applies the proposed values to its (still linked) target. Duplicate identifiers are refused (<c>identifier_in_use</c>). One system transaction with its audit event.
    /// </summary>
    public Task<ConfigResult<PartyProposalView>> ApproveAsync(Guid actor, Guid id, long? expectedRevision, Guid? linkTo, CancellationToken ct = default) =>
        Decide(actor, id, expectedRevision, async (conn, tx, p) =>
        {
            if (p.Kind == (int)PartyRequestKind.NewIdentity)
            {
                Guid global;
                PartyProposalResolution resolution;
                if (linkTo is { } existing)
                {
                    await using (var q = new NpgsqlCommand($"SELECT {GP.Status} FROM {GP.Table} WHERE {GP.Id} = @g FOR SHARE", conn, tx))
                    {
                        q.Parameters.AddWithValue("g", existing);
                        var st = await q.ExecuteScalarAsync(ct);
                        if (st is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", [new FieldError("linkTo", "global_party_not_found")]);
                        if (Convert.ToInt16(st) != (short)GlobalPartyStatus.Active) throw new Abort(ConfigStatus.Conflict, "global_party_inactive");
                    }
                    global = existing;
                    resolution = PartyProposalResolution.Linked;
                }
                else
                {
                    global = Guid.NewGuid();
                    await using var ins = new NpgsqlCommand(
                        $"INSERT INTO {GP.Table} ({GP.Id}, {GP.PartyKind}, {GP.LegalNameAr}, {GP.LegalNameEn}, {GP.TaxNumber}, {GP.RegistrationNumber}, {GP.Status}, {GP.Revision}, {GP.CreatedBy}, {GP.CreatedAtUtc}, {GP.UpdatedAtUtc}) " +
                        "VALUES (@id, @k, @ar, @en, @tax, @reg, 1, 1, @a, @now, @now)", conn, tx);
                    ins.Parameters.AddWithValue("id", global); ins.Parameters.AddWithValue("k", (short)p.PartyKind); ins.Parameters.AddWithValue("ar", p.LegalNameAr); ins.Parameters.AddWithValue("en", Db(p.LegalNameEn));
                    ins.Parameters.AddWithValue("tax", Db(p.TaxNumber)); ins.Parameters.AddWithValue("reg", Db(p.RegistrationNumber)); ins.Parameters.AddWithValue("a", actor); ins.Parameters.AddWithValue("now", Now);
                    await ins.ExecuteNonQueryAsync(ct);
                    resolution = PartyProposalResolution.Created;
                }
                await InsertLink(conn, tx, p, global, ct);
                await MarkDecided(conn, tx, p.Id, PartyProposalStatus.Approved, resolution, global, actor, null, ct);
                return await AuditAsync(conn, tx, resolution == PartyProposalResolution.Created ? PartyAuditEvents.ApprovedNewIdentity : PartyAuditEvents.ApprovedByLinking, actor, p.Id,
                    $"party proposal approved company={p.CompanyId:N} global={global:N} resolution={(short)resolution}", ct);
            }
            if (linkTo is not null) throw new Abort(ConfigStatus.Invalid, "validation_failed", [new FieldError("linkTo", "not_allowed")]);
            var target = p.TargetGlobalPartyId!.Value;
            await using (var q = new NpgsqlCommand($"SELECT 1 FROM {PL.Table} WHERE {PL.CompanyId} = @c AND {PL.LocalPartyId} = @l AND {PL.GlobalPartyId} = @g", conn, tx))
            {
                q.Parameters.AddWithValue("c", p.CompanyId); q.Parameters.AddWithValue("l", p.LocalPartyId); q.Parameters.AddWithValue("g", target);
                if (await q.ExecuteScalarAsync(ct) is null) throw new Abort(ConfigStatus.Conflict, "link_missing");
            }
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {GP.Table} SET {GP.PartyKind} = @k, {GP.LegalNameAr} = @ar, {GP.LegalNameEn} = @en, {GP.TaxNumber} = @tax, {GP.RegistrationNumber} = @reg, {GP.Status} = @s, " +
                $"{GP.Revision} = {GP.Revision} + 1, {GP.UpdatedAtUtc} = @now WHERE {GP.Id} = @id", conn, tx))
            {
                upd.Parameters.AddWithValue("id", target); upd.Parameters.AddWithValue("k", (short)p.PartyKind); upd.Parameters.AddWithValue("ar", p.LegalNameAr); upd.Parameters.AddWithValue("en", Db(p.LegalNameEn));
                upd.Parameters.AddWithValue("tax", Db(p.TaxNumber)); upd.Parameters.AddWithValue("reg", Db(p.RegistrationNumber)); upd.Parameters.AddWithValue("s", (short)p.ProposedStatus); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await MarkDecided(conn, tx, p.Id, PartyProposalStatus.Approved, PartyProposalResolution.Changed, target, actor, null, ct);
            return await AuditAsync(conn, tx, PartyAuditEvents.ApprovedChange, actor, p.Id, $"party change approved company={p.CompanyId:N} global={target:N}", ct);
        }, ct);

    /// <summary>Rejects a pending proposal with a mandatory note (at most 500 characters).</summary>
    public Task<ConfigResult<PartyProposalView>> RejectAsync(Guid actor, Guid id, long? expectedRevision, string? note, CancellationToken ct = default)
    {
        var n = note?.Trim();
        if (string.IsNullOrEmpty(n)) return Task.FromResult(ConfigResult<PartyProposalView>.Invalid([new("note", "required")]));
        if (n.Length > 500) return Task.FromResult(ConfigResult<PartyProposalView>.Invalid([new("note", "too_long")]));
        return Decide(actor, id, expectedRevision, async (conn, tx, p) =>
        {
            await MarkDecided(conn, tx, p.Id, PartyProposalStatus.Rejected, null, null, actor, n, ct);
            return await AuditAsync(conn, tx, PartyAuditEvents.ProposalRejectedCentrally, actor, p.Id, $"party proposal rejected company={p.CompanyId:N}", ct);
        }, ct);
    }
}
