using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Npgsql;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanPosition;
using EM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochMember;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The entry-level guard of company migration 0033 (design 3.3) for ORDINARY writers: the gap F1 is closed for every entry kind and for every writer, no setting opens it, and the ordinary application paths answer
/// exactly as before. Tests N1, N4, N6, N7, N12, N16, N22, N23 and N25 of design section 8.
/// </summary>
public class PlanExecutionGuardTests : PlanExecutionTestBase
{
    private const string BackDated = "a back-dated cost entry is accepted only inside the execution of an approved compensation plan";

    /// <summary>AVG1 in W1: 10 for 100.00 on 10 March, issue of 4 on 12 March; and 5 for 50.00 in W2 on 12 March. Every position's last date is 12 March.</summary>
    private async Task<(Guid ReceiptOp, long ReceiptEntry, long IssueEntry)> History()
    {
        await PrepareRuntime();
        var r = await ReceiveDoc(Avg, 10m, 100m, "2026-03-10");
        var i = await IssueDoc(Avg, 4m, "2026-03-12");
        await ReceiveDoc(Avg, 5m, 50m, "2026-03-12", W2);
        return (r.CostOperationId, await EntryOf(r.CostOperationId), await EntryOf(i.CostOperationId));
    }

    [PostgresRoleFact]
    public async Task N01_a_direct_sql_back_dated_entry_of_an_ordinary_operation_is_refused_for_the_runtime_role_even_when_the_position_date_is_kept_with_greatest()
    {
        await History();
        var before = (await Digest(EN.Table), await Digest(PO.Table), await Digest(OP.Table));
        var op = Guid.NewGuid();
        // the exact statements that pass PostgreSQL on main today (gap F1): an ordinary receipt dated 11 March on a position whose last date is 12 March, the position's date kept with GREATEST
        RefusedWith(BackDated, await Rt([Op(op, 1, D2), .. Move(op, Avg.Id, W1.Id, 1, 1, D2, 10m, 200m)]));
        Assert.Equal(before, (await Digest(EN.Table), await Digest(PO.Table), await Digest(OP.Table)));                       // nothing was written
        // the guard refuses exactly the back-dated case: the same statements dated on the position's last date are an ordinary entry and commit
        var same = Guid.NewGuid();
        Assert.Null(await Rt([Op(same, 1, D3), .. Move(same, Avg.Id, W1.Id, 1, 1, D3, 10m, 200m)]));
        Assert.Equal((16m, 260m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }

    [PostgresRoleFact]
    public async Task N04_a_member_or_a_back_dated_entry_without_an_execution_is_refused_even_while_an_approved_plan_exists_for_the_position()
    {
        await History();
        var p = await ApprovedPlan(Avg, "2026-03-11");                                                                         // approved, but nothing executes it
        var op = Guid.NewGuid();
        RefusedWith(BackDated, await Rt([Op(op, 1, D2), .. Move(op, Avg.Id, W1.Id, 1, 1, D2, 10m, 200m)]));
        // a member row needs an execution written in this transaction: an unknown execution and no execution are both refused before the foreign key is even consulted
        RefusedWith("an epoch member belongs to the execution", await Rt(Op(op, 1, D2), Member(op, Guid.NewGuid(), 2, null)));
        RefusedWith("an epoch member belongs to the execution", await Rt(Op(op, 1, D2), Member(op, p.Execution, 2, null)));
        Assert.Equal(0L, await Rows(EM.Table));
    }

    [PostgresRoleFact]
    public async Task N06_lowering_the_last_date_of_a_position_is_still_refused_by_the_unchanged_guard_of_0014_also_inside_an_execution()
    {
        await History();
        var lower = $"UPDATE {PO.Table} SET {PO.LastEffectiveDate} = DATE '2026-03-10', {PO.LastSeq} = {PO.LastSeq} + 1 WHERE {PO.ItemId} = '{Avg.Id}' AND {PO.WarehouseId} = '{W1.Id}'";
        RefusedWith("a cost position does not accept back-dated entries", await Rt(lower));
        RefusedWith("a cost position does not accept back-dated entries", await Rt(lower.Replace("DATE '2026-03-10'", "NULL")));
        var p = await ApprovedPlan(Avg, "2026-03-11");
        RefusedWith("a cost position does not accept back-dated entries", await Rt(ExecutionInsert(p, audit: "gen_random_uuid()"), lower));   // an execution opens nothing on the position row
    }

    [PostgresRoleFact]
    public async Task N07_a_reversal_link_in_an_operation_that_is_not_a_reversal_is_refused_without_storno_membership()
    {
        var h = await History();
        const string only = "outside a reversal operation only an epoch storno takes back an entry";
        var op = Guid.NewGuid();
        // an ordinary issue-kind operation that "takes back" the issue entry: not back-dated, figures plausible, refused
        RefusedWith(only, await Rt([Op(op, 2, D3), .. Move(op, Avg.Id, W1.Id, 2, 1, D3, 4m, 40m, h.IssueEntry)]));
        // inside a live execution the event member and the rebook member may not carry the link either: only the storno role
        var p = await ApprovedPlan(Avg, "2026-03-11");
        RefusedWith(only, await Rt([ExecutionInsert(p, audit: "gen_random_uuid()"), Op(op, 1, D3), Member(op, p.Execution, 2, null), .. Move(op, Avg.Id, W1.Id, 1, -1, D3, 10m, 100m, h.ReceiptEntry)]));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {EN.Table} WHERE {EN.ReversesEntryId} IS NOT NULL"));
    }

    [PostgresRoleFact]
    public async Task N12_no_session_setting_opens_the_guard_and_the_runtime_role_cannot_switch_triggers_off()
    {
        await History();
        var op = Guid.NewGuid();
        string[] insert = [Op(op, 1, D2), .. Move(op, Avg.Id, W1.Id, 1, 1, D2, 10m, 200m)];
        foreach (var set in new[]
                 {
                     "SET LOCAL hasm.replay = 'on'", "SET LOCAL hasm.backdating = 'allowed'", "SELECT set_config('hasm.execution', gen_random_uuid()::text, true)", "SET LOCAL application_name = 'hasm-plan-executor'",
                     "SET LOCAL search_path = pg_temp, public", "SET LOCAL check_function_bodies = off", "SET LOCAL row_security = off", "SET CONSTRAINTS ALL DEFERRED",
                 })
            RefusedWith(BackDated, await Rt([set, .. insert]));
        // the one setting that would silence triggers is not available to the runtime role at all
        Assert.StartsWith("42501", await Rt(["SET LOCAL session_replication_role = replica", .. insert]));
        Assert.StartsWith("42501", await Rt($"ALTER TABLE {EN.Table} DISABLE TRIGGER {CompanySchema.EpochGuard.TrEntry}"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {OP.Table} WHERE {OP.Id} = '{op}'"));
    }

    [PostgresRoleFact]
    public async Task N16_ordinary_back_dated_requests_keep_their_existing_codes_on_every_application_path_after_the_migration()
    {
        await History();
        var entries = await Rows(EN.Table);
        // the costing engine: receipt, issue, transfer and reversal
        Assert.Equal("backdated_not_supported", (await Refused(() => Receive(Avg, 1m, 1m, date: D1))).Code);
        Assert.Equal("backdated_not_supported", (await Refused(() => IssueOf(Avg, 1m, date: D1))).Code);
        Assert.Equal("backdated_not_supported", (await Refused(() => Move(Avg, 1m, date: D1))).Code);
        var key = Key("late"); await Receive(Avg, 1m, 10m, date: D3, key: key);
        Assert.Equal("backdated_not_supported", (await Refused(() => Undo(key, D1))).Code);
        // the stock document posting path: a receipt and an issue document dated before the last movement
        Fails(await Post(await Approved(Receipt(W1, "2026-03-11", L(Avg, Pcs, 2m))), Amounts(20m)), ConfigStatus.Conflict, "backdated_not_supported");
        Fails(await Post(await Approved(Issue(W1, "2026-03-11", L(Avg, Pcs, 1m)))), ConfigStatus.Conflict, "backdated_not_supported");
        Assert.Equal(entries + 1, await Rows(EN.Table));                                                                        // only the ordinary receipt of 12 March
        // and the ordinary paths still work: same-date and later-date movements, a transfer and a reversal
        var again = Key("again"); await Receive(Avg, 2m, 20m, date: D3, key: again); await Undo(again, D3); await IssueOf(Avg, 1m, date: D3); await Move(Avg, 1m, date: D3);
        Ok(await Post(await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 2m))), Amounts(20m)));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }

    [PostgresRoleFact]
    public async Task N22_the_back_dated_refusal_holds_for_every_entry_kind_receipt_issue_transfer_out_transfer_in_and_reversal()
    {
        var h = await History();
        var before = await Digest(EN.Table);
        Guid Next() => Guid.NewGuid();
        var op = Next();
        RefusedWith(BackDated, await Rt([Op(op, 1, D2), .. Move(op, Avg.Id, W1.Id, 1, 1, D2, 1m, 10m)]));                       // receipt
        op = Next();
        RefusedWith(BackDated, await Rt([Op(op, 2, D2), .. Move(op, Avg.Id, W1.Id, 2, -1, D2, 1m, 10m)]));                      // issue
        op = Next();
        RefusedWith(BackDated, await Rt([Op(op, 3, D2), .. Move(op, Avg.Id, W1.Id, 3, -1, D2, 1m, 10m)]));                      // transfer out
        op = Next();
        // transfer in: the out leg dated on the last date is accepted, the in leg into W2 dated before W2's last date is refused
        RefusedWith(BackDated, await Rt([Op(op, 3, D3), .. Move(op, Avg.Id, W1.Id, 3, -1, D3, 1m, 10m), .. Move(op, Avg.Id, W2.Id, 4, 1, D2, 1m, 10m)]));
        op = Next();
        // a reversal operation (kind 4) whose entry is dated before the last date
        RefusedWith(BackDated, await Rt([Op(op, 4, D2, h.ReceiptOp), .. Move(op, Avg.Id, W1.Id, 1, -1, D2, 10m, 100m, h.ReceiptEntry)]));
        Assert.Equal(before, await Digest(EN.Table));
    }

    [PostgresRoleFact]
    public async Task N23_the_application_barrier_answers_before_the_database_guard_for_an_ordinary_request()
    {
        await History();
        // the engine refuses with ITS exception and code before any statement reaches PostgreSQL: a refusal by the new trigger would surface as a PostgresException instead
        var refused = await Assert.ThrowsAsync<CostingException>(() => Receive(Avg, 1m, 1m, date: D1));
        Assert.Equal("backdated_not_supported", refused.Code);
        Assert.IsNotType<PostgresException>(refused.InnerException);
        Assert.Equal("backdated_not_supported", (await Assert.ThrowsAsync<CostingException>(() => IssueOf(Avg, 1m, date: D1))).Code);
        // EXECUTOR-LEVEL VARIANT (step 5, not written yet): the barrier accepts an earlier date only for an operation that is a member of an execution of the same transaction, and still answers
        // backdated_not_supported for everything else; this test then also drives the event's posting through PostInWorkAsync.
    }

    [PostgresRoleFact]
    public async Task N25_the_optional_definer_of_design_3_6_is_absent_and_the_runtime_role_can_neither_shadow_a_guard_object_nor_write_back_dated_directly()
    {
        await History();
        // 3.6 (a single SECURITY DEFINER writer) is an optional hardening that this slice does not ship: no function of the schema is a definer function
        Assert.Equal(0L, await Sql<long>("SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND prosecdef"));
        // without it, a direct back-dated write by the runtime role is refused by the entry guard itself (N1)
        var op = Guid.NewGuid();
        RefusedWith(BackDated, await Rt([Op(op, 1, D2), .. Move(op, Avg.Id, W1.Id, 1, 1, D2, 10m, 200m)]));
        // a search-path hijack needs an object that shadows a guard's table or function: the runtime role can create none, anywhere
        Assert.StartsWith("42501", await Rt($"CREATE TEMPORARY TABLE {EM.Table} (LIKE public.{EM.Table})"));
        Assert.StartsWith("42501", await Rt($"CREATE TABLE public.shadow_{EM.Table} (a int)"));
        Assert.StartsWith("42501", await Rt("CREATE SCHEMA shadow"));
        Assert.StartsWith("42501", await Rt($"CREATE FUNCTION public.{CompanySchema.EpochGuard.FnLivePlan}(text) RETURNS uuid LANGUAGE sql AS 'SELECT gen_random_uuid()'"));
        Assert.StartsWith("42501", await Rt($"CREATE FUNCTION pg_temp.{CompanySchema.EpochGuard.FnLivePlan}(uuid) RETURNS uuid LANGUAGE sql AS 'SELECT gen_random_uuid()'"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CC.Table}"));
        // WITH 3.6 (a later hardening, if the owner wants it): the same three attempts against the definer function, and EXECUTE not granted to PUBLIC.
    }
}
