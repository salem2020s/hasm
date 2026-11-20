using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CustomerGroup;
using CGM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CustomerGroupMember;
using PRT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRole;

namespace Hasm.Infrastructure.Pricing;

// Customer groups (Phase 3.11, company migration 0027): the group master and the assignment of a customer to ONE group. Same rules as the rest of the store: audited single transaction, revision-fenced, never removed.
public sealed partial class PricingConfigService
{
    /// <summary>A new price list, discount rule or assignment may only name an ACTIVE group (a later deactivation does not invalidate existing rows: deactivate those lists and rules themselves).</summary>
    private static async Task RequireActiveGroup(CompanyAuditedWork work, Guid? groupId, CancellationToken ct)
    {
        if (groupId is not { } g) return;
        await using var q = new NpgsqlCommand($"SELECT {CG.IsActive} FROM {CG.Table} WHERE {CG.Id} = @g FOR SHARE", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("g", g);
        var v = await q.ExecuteScalarAsync(ct);
        if (v is null) throw Invalid("customerGroupId", "customer_group_not_found");
        if (!(bool)v) throw Invalid("customerGroupId", "customer_group_inactive");
    }

    private const string GroupCols = $"{CG.Id}, {CG.Code}, {CG.NameAr}, {CG.NameEn}, {CG.IsActive}, {CG.Revision}, {CG.UpdatedAtUtc}";

    private static async Task<List<CustomerGroupView>> LoadGroups(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var rows = new List<CustomerGroupView>();
        await using var q = new NpgsqlCommand($"SELECT {GroupCols} FROM {CG.Table}{(id is null ? "" : $" WHERE {CG.Id} = @id")} ORDER BY {CG.Code}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        if (id is { } g) q.Parameters.AddWithValue("id", g);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new CustomerGroupView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetInt64(5), r.GetFieldValue<DateTimeOffset>(6)));
        return rows;
    }

    public async Task<IReadOnlyList<CustomerGroupView>> ListCustomerGroupsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadGroups(conn, null, null, false, ct);
    }

    public async Task<CustomerGroupView?> GetCustomerGroupAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadGroups(conn, null, id, false, ct)).SingleOrDefault();
    }

    public async Task<ConfigResult<CustomerGroupView>> CreateCustomerGroupAsync(Guid actor, Guid companyId, CustomerGroupInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<CustomerGroupView>.Invalid([new FieldError("input", "required")]);
        var errors = CheckNames(input.NameAr, input.NameEn, out var ar, out var en);
        var code = input.Code?.Trim().ToUpperInvariant() ?? "";
        if (!CodePattern().IsMatch(code)) errors.Add(new FieldError("code", "code_invalid"));
        if (errors.Count > 0) return ConfigResult<CustomerGroupView>.Invalid(errors);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {CG.Table} ({CG.Id}, {CG.Code}, {CG.NameAr}, {CG.NameEn}, {CG.IsActive}, {CG.Revision}, {CG.CreatedBy}, {CG.CreatedAtUtc}, {CG.UpdatedAtUtc}) VALUES (@id, @c, @ar, @en, @a, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("c", code); ins.Parameters.AddWithValue("ar", ar); ins.Parameters.AddWithValue("en", Db(en));
                ins.Parameters.AddWithValue("a", input.IsActive ?? true); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.CustomerGroupCreated, ActorUserId: actor, SubjectId: id, Detail: $"customer group created code={code}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<CustomerGroupView>.Ok((await GetCustomerGroupAsync(companyId, id, ct))!) : ConfigResult<CustomerGroupView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    /// <summary>Changes the names and the active flag. The code never changes.</summary>
    public async Task<ConfigResult<CustomerGroupView>> UpdateCustomerGroupAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CustomerGroupInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CustomerGroupView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<CustomerGroupView>.Invalid([new FieldError("input", "required")]);
        var errors = CheckNames(input.NameAr, input.NameEn, out var ar, out var en);
        if (errors.Count > 0) return ConfigResult<CustomerGroupView>.Invalid(errors);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadGroups(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "customer_group_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (input.Code is { } c && !string.Equals(c.Trim(), cur.Code, StringComparison.OrdinalIgnoreCase)) throw Invalid("code", "code_immutable");
            await using (var upd = new NpgsqlCommand($"UPDATE {CG.Table} SET {CG.NameAr} = @ar, {CG.NameEn} = @en, {CG.IsActive} = @a, {CG.Revision} = {CG.Revision} + 1, {CG.UpdatedAtUtc} = @now WHERE {CG.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("ar", ar); upd.Parameters.AddWithValue("en", Db(en)); upd.Parameters.AddWithValue("a", input.IsActive ?? cur.IsActive); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.CustomerGroupUpdated, ActorUserId: actor, SubjectId: id, Detail: $"customer group updated r{cur.Revision + 1} code={cur.Code} active={input.IsActive ?? cur.IsActive}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<CustomerGroupView>.Ok((await GetCustomerGroupAsync(companyId, id, ct))!) : ConfigResult<CustomerGroupView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
    }

    // ---- assignment (one group per customer party)

    // Ids and group data ONLY: the party code and name are customer data that needs the party-role permission, so a pricing-view user never receives them here (the screen joins names from the party-role list it may already read).
    private const string AssignmentSelect = $"SELECT r.{PRT.PartyId}, m.{CGM.CustomerGroupId}, g.{CG.Code}, COALESCE(m.{CGM.Revision}, 0) FROM {PRT.Table} r " +
        $"LEFT JOIN {CGM.Table} m ON m.{CGM.PartyId} = r.{PRT.PartyId} LEFT JOIN {CG.Table} g ON g.{CG.Id} = m.{CGM.CustomerGroupId} " +
        $"WHERE r.{PRT.Role} = 1";

    private static async Task<List<CustomerGroupAssignmentView>> LoadAssignments(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? party, CancellationToken ct)
    {
        var rows = new List<CustomerGroupAssignmentView>();
        await using var q = new NpgsqlCommand($"{AssignmentSelect}{(party is null ? "" : $" AND r.{PRT.PartyId} = @p")} ORDER BY r.{PRT.PartyId}", conn, tx);
        if (party is { } g) q.Parameters.AddWithValue("p", g);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new CustomerGroupAssignmentView(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3)));
        return rows;
    }

    /// <summary>Every customer party (a party with a customer role) with its group, or none (revision 0 = never assigned).</summary>
    public async Task<IReadOnlyList<CustomerGroupAssignmentView>> ListAssignmentsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAssignments(conn, null, null, ct);
    }

    /// <summary>Sets or clears the group of a customer party. <paramref name="expectedRevision"/> is the assignment revision looked at (0 = never assigned). Only a customer party and an active group are accepted.</summary>
    public async Task<ConfigResult<CustomerGroupAssignmentView>> SetAssignmentAsync(Guid actor, Guid companyId, Guid partyId, long? expectedRevision, CustomerGroupAssignmentInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CustomerGroupAssignmentView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<CustomerGroupAssignmentView>.Invalid([new FieldError("input", "required")]);
        var r = await Mutate(companyId, async work =>
        {
            await RequireCustomerRole(work, partyId, ct);
            await RequireActiveGroup(work, input.CustomerGroupId, ct);
            var now = Now;
            long revision = 0; Guid? before = null;
            await using (var cur = new NpgsqlCommand($"SELECT {CGM.Revision}, {CGM.CustomerGroupId} FROM {CGM.Table} WHERE {CGM.PartyId} = @p FOR UPDATE", work.Connection, work.Transaction))
            {
                cur.Parameters.AddWithValue("p", partyId);
                await using var rd = await cur.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) { revision = rd.GetInt64(0); before = rd.IsDBNull(1) ? null : rd.GetGuid(1); }
            }
            if (revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, revision);
            if (revision == 0)
            {
                await using var ins = new NpgsqlCommand($"INSERT INTO {CGM.Table} ({CGM.PartyId}, {CGM.CustomerGroupId}, {CGM.Revision}, {CGM.CreatedBy}, {CGM.CreatedAtUtc}, {CGM.UpdatedAtUtc}) VALUES (@p, @g, 1, @by, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("p", partyId); ins.Parameters.Add(Uuid("g", input.CustomerGroupId)); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var upd = new NpgsqlCommand($"UPDATE {CGM.Table} SET {CGM.CustomerGroupId} = @g, {CGM.Revision} = {CGM.Revision} + 1, {CGM.UpdatedAtUtc} = @now WHERE {CGM.PartyId} = @p", work.Connection, work.Transaction);
                upd.Parameters.AddWithValue("p", partyId); upd.Parameters.Add(Uuid("g", input.CustomerGroupId)); upd.Parameters.AddWithValue("now", now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.CustomerGroupAssigned, ActorUserId: actor, SubjectId: partyId,
                Detail: $"customer group assignment r{revision + 1} from={(before is null ? "none" : before.ToString())} to={(input.CustomerGroupId is null ? "none" : input.CustomerGroupId.ToString())}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<CustomerGroupAssignmentView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<CustomerGroupAssignmentView>.Ok((await LoadAssignments(conn, null, partyId, ct)).Single());
    }
}
