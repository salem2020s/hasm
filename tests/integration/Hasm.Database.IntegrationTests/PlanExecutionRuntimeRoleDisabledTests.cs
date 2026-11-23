using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanEvent;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanApproval;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanPosition;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanLine;
using CX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanExecution;
using EL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochEntryLineage;
using EM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochMember;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The DATABASE layer of "execution cannot be activated or called" (review M1 of PR 48): with the PRODUCTION grants (no test-only executor grant) the per-company runtime role holds SELECT only on the four tables of
/// execution, and an epoch, the database's only door for back-dated cost history, cannot be written by it, however well-formed. The plan storage and approval tables keep exactly the rights the step 5 services use.
/// </summary>
public class PlanExecutionRuntimeRoleDisabledTests : PlanExecutionTestBase
{
    private async Task<bool> Holds(NpgsqlConnection conn, string table, string privilege)
    {
        await using var cmd = new NpgsqlCommand("SELECT has_table_privilege(current_user, @t, @p)", conn);
        cmd.Parameters.AddWithValue("t", table); cmd.Parameters.AddWithValue("p", privilege);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_holds_select_only_on_the_four_execution_tables_and_every_insert_is_refused_with_42501()
    {
        await PrepareRuntime();
        await using var conn = await AsRuntime();
        foreach (var table in ExecutionTables)
        {
            Assert.True(await Holds(conn, table, "SELECT"), table);
            foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE" }) Assert.False(await Holds(conn, table, privilege), $"{table} {privilege}");
            // a bare statement: PostgreSQL checks the privilege before any default, constraint or trigger, so the refusal is the missing grant and nothing else
            await using var tx = await conn.BeginTransactionAsync();
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => { await using var cmd = new NpgsqlCommand($"INSERT INTO {table} DEFAULT VALUES", conn, tx); await cmd.ExecuteNonQueryAsync(); });
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            await tx.RollbackAsync();
        }
        // the plan storage and approval tables keep exactly what the step 5 services need
        foreach (var table in new[] { CC.Table, CL.Table, CE.Table, CA.Table, CP.Table }) Assert.True(await Holds(conn, table, "INSERT"), table);
        Assert.True(await Holds(conn, CP.Table, "UPDATE"));
        foreach (var table in new[] { CC.Table, CL.Table, CE.Table, CA.Table }) Assert.False(await Holds(conn, table, "UPDATE"), table);
        // and the catalog says the same (it is what grant-company-runtime applies)
        foreach (var table in ExecutionTables) Assert.Equal(["SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges);
    }

    [PostgresRoleFact]
    public async Task A_complete_well_formed_epoch_written_by_the_runtime_role_is_refused_and_nothing_persists()
    {
        await PrepareRuntime();
        var s = await SimpleEpoch();          // an approved plan (written by the runtime role: that grant exists) and the complete script of a valid execution, audit event included
        var before = (await Rows(CX.Table), await Rows(EM.Table), await Rows(EL.Table), await Digest(CompanySchema.CostEntry.Table), await Digest(CompanySchema.CostOperation.Table), await Digest(CompanySchema.StockPosting.Table));
        var outcome = await Run(s.Script);
        Assert.NotNull(outcome);
        Assert.StartsWith(PostgresErrorCodes.InsufficientPrivilege, outcome);                                       // the execution row itself is refused: no grant, whatever the plan and the audit event say
        Assert.Equal(before, (await Rows(CX.Table), await Rows(EM.Table), await Rows(EL.Table), await Digest(CompanySchema.CostEntry.Table), await Digest(CompanySchema.CostOperation.Table), await Digest(CompanySchema.StockPosting.Table)));
        Assert.Equal((short)2, await Sql<short>($"SELECT {CP.Status} FROM {CP.Table} WHERE {CP.Id} = '{s.Plan.Id}'"));   // the plan is still approved, never executed
        // the same script commits for a writer that holds the executor grant (the owner here), so the refusal above is the grant and nothing else
        await Sql($"GRANT INSERT ON {string.Join(", ", ExecutionTables)} TO \"{CompanyRuntimeRole.NameFor(Company.PhysicalDatabaseName)}\"");
        Assert.Null(await Run(s.Script));
        Assert.Equal(1L, await Rows(CX.Table));
    }
}
