using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Npgsql;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanExecution;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using CS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;
using RGR2 = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayExecutionGrant;
using RGU2 = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayGrantUse;
using RK2 = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplaySignerKey;
using RE2 = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayEpoch;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// T2 slice A3 (company migration 0039): the commit-time checks of an execution grant and the epoch text rebuilt from the rows. The SQL builder is compared with the independent .NET builder of E1b (<see cref="EpochText"/>) on
/// the rows of real epochs (average and FIFO, with layers, consumptions that restore and consume, remaining figures and position totals); every commit-time rule is shown to refuse; and the effect on the rest of the suite is that
/// every execution now carries a signed grant (the test base writes one automatically). INERT: the runtime role still cannot write a grant or an execution in production.
/// </summary>
public class ReplayEpochCommitTests : PlanExecutionTestBase
{
    private static readonly DateOnly Day1 = new(2026, 3, 10), Day2 = new(2026, 3, 11), Day3 = new(2026, 3, 12);

    private async Task<List<object[]>> Rows(string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await r.ReadAsync()) { var row = new object[r.FieldCount]; r.GetValues(row); rows.Add(row); }
        return rows;
    }

    private string EntryId(Guid operation) => $"(SELECT {EN.Id} FROM {EN.Table} WHERE {EN.OperationId} = '{operation}' AND {EN.WarehouseId} = '{W1.Id}')";

    /// <summary>An epoch operation with the source columns the real writers set: a source type, and for the event the id of the execution's stock posting (which the text writes as <c>execution-posting</c>).</summary>
    private string OpS(Guid id, int kind, DateOnly date, string sourceType, Guid? sourceId) =>
        Op(id, kind, date).Replace($", {OP.ReversesOperationId}) VALUES", $", {OP.ReversesOperationId}, {OP.SourceType}, {OP.SourceId}) VALUES").TrimEnd(')') + $", '{sourceType}', {(sourceId is null ? "NULL" : $"'{sourceId}'")})";

    /// <summary>A FIFO epoch of the smallest size with every layer effect: a receipt of 10 / 100.00 (layer 1), an issue of 4 / 40.00 on the 12th; back-dated receipt of 10 / 200.00 on the 11th (layer 2). Storno of the issue gives its 4 / 40.00 back to layer 1, the event makes layer 2, the rebook consumes layer 1 again.</summary>
    private async Task<(Simple S, Guid Layer1, Guid Storno, Guid Event, Guid Rebook)> FifoEpoch()
    {
        await ReceiveDoc(Fifo, 5m, 50m, "2026-03-09");                                   // a layer that is consumed completely before the epoch: it stays at remaining 0 and is no open layer
        await IssueDoc(Fifo, 5m, "2026-03-09");
        var receipt = await ReceiveDoc(Fifo, 10m, 100m, "2026-03-10");
        var issue = await IssueDoc(Fifo, 4m, "2026-03-12");
        var issueEntry = await EntryOf(issue.CostOperationId);
        var layer1 = await Sql<Guid>($"SELECT {CL.Id} FROM {CL.Table} WHERE {CL.ItemId} = '{Fifo.Id}' AND {CL.OriginalQuantity} = 10");
        var p = await ApprovedPlan(Fifo, "2026-03-11");
        Guid s = Guid.NewGuid(), e = Guid.NewGuid(), b = Guid.NewGuid();
        string Consumption(Guid op, bool restores) => $"INSERT INTO {CS.Table} ({CS.EntryId}, {CS.LayerId}, {CS.Quantity}, {CS.Amount}, {CS.Restores}) VALUES ({EntryId(op)}, '{layer1}', 4, 40, {(restores ? "true" : "false")})";
        string Layer1Delta(string sign) => $"UPDATE {CL.Table} SET {CL.RemainingQuantity} = {CL.RemainingQuantity} {sign} 4, {CL.RemainingAmount} = {CL.RemainingAmount} {sign} 40 WHERE {CL.Id} = '{layer1}'";
        var script = new Script { AuditSubject = p.Id, AuditTarget = p.Execution, GrantPlan = p.Id, GrantExecution = p.Execution };
        script.Add(("audit", [])); script.Add(("execution", [ExecutionInsert(p)]));
        script.Add(("storno", [OpS(s, 2, Day3, "replay-storno", null), Member(s, p.Execution, 1, issue.CostOperationId), .. Move(s, Fifo.Id, W1.Id, 2, 1, Day3, 4m, 40m, issueEntry), Consumption(s, true), Layer1Delta("+")]));
        script.Add(("event", [OpS(e, 1, Day2, "stock-posting", p.Posting), Member(e, p.Execution, 2, null), .. Move(e, Fifo.Id, W1.Id, 1, 1, Day2, 10m, 200m),
            $"INSERT INTO {CL.Table} ({CL.PositionId}, {CL.ItemId}, {CL.WarehouseId}, {CL.EntryId}, {CL.Seq}, {CL.OriginalQuantity}, {CL.OriginalAmount}, {CL.RemainingQuantity}, {CL.RemainingAmount}, {CL.SourceLayerId}, {CL.CreatedAtUtc}) " +
            $"SELECT {PO.Id}, {PO.ItemId}, {PO.WarehouseId}, {EntryId(e)}, 3, 10, 200, 10, 200, '{layer1}', now() FROM {PO.Table} WHERE {PO.ItemId} = '{Fifo.Id}' AND {PO.WarehouseId} = '{W1.Id}'",
            PostingRow(p, e, Day2, 200m)]));
        script.Add(("rebook", [OpS(b, 2, Day3, "replay-rebook", null), Member(b, p.Execution, 3, issue.CostOperationId), .. Move(b, Fifo.Id, W1.Id, 2, -1, Day3, 4m, 40m), Consumption(b, false), Layer1Delta("-")]));
        script.Add(("lineage", [Lineage(issueEntry, s, b, p.Execution)]));
        script.Add(("executed", [SetStatus(p, 3)]));
        return (new Simple(p, script, receipt, issue, issueEntry, s, e, b), layer1, s, e, b);
    }

    /// <summary>The epoch text the .NET builder gives for the FIFO epoch, written by hand from the intended figures (never read from the rows being tested).</summary>
    private EpochTextInput ExpectedFifoText(Simple s, Guid storno, Guid evt, Guid rebook)
    {
        var item = Fifo.Id; var w = W1.Id; var original = s.Issue.CostOperationId;
        return new EpochTextInput(
            [new(1, original, 2, Day3, null, "replay-storno", null, $"epoch-{storno:N}", Executor), new(2, null, 1, Day2, null, "stock-posting", "execution-posting", $"epoch-{evt:N}", Executor), new(3, original, 2, Day3, null, "replay-rebook", null, $"epoch-{rebook:N}", Executor)],
            [new(item, w, 5, 1, s.IssueEntry, 2, 1, Day3, 4m, 40m, 10m, 100m), new(item, w, 6, 2, null, 1, 1, Day2, 10m, 200m, 20m, 300m), new(item, w, 7, 3, s.IssueEntry, 2, -1, Day3, 4m, 40m, 16m, 260m)],
            [new(item, w, 3, 6, 10m, 200m, item, w, 2)],
            [new(item, w, 5, item, w, 2, 4m, 40m, true), new(item, w, 7, item, w, 2, 4m, 40m, false)],
            [new(item, w, 2, 6m, 60m), new(item, w, 3, 10m, 200m)],
            [new(item, w, 4, 7, 16m, 260m, 2)]);
    }

    [PostgresRoleFact]
    public async Task The_sql_epoch_text_of_a_real_fifo_epoch_equals_the_dotnet_builder_and_its_digest_is_the_one_the_commit_compares()
    {
        await PrepareRuntime(executorGrant: true);
        var (s, _, storno, evt, rebook) = await FifoEpoch();
        Assert.Null(await Run(s.Script));                                                                                // commits: the automatic grant signed exactly the digest of these rows
        var expected = EpochText.Build(ExpectedFifoText(s, storno, evt, rebook));
        var sql = await Sql<string>($"SELECT public.{RE2.FnRowsText}('{s.Plan.Execution}')");
        Assert.Equal(expected.Text, sql);
        Assert.Equal(expected.Digest, await Sql<string>($"SELECT encode(sha256(convert_to(public.{RE2.FnRowsText}('{s.Plan.Execution}'), 'UTF8')), 'hex')"));
        Assert.Equal(expected.Digest, await Sql<string>($"SELECT {RGR2.EpochDigest} FROM {RGR2.Table} g JOIN {RGU2.Table} u ON u.{RGU2.GrantId} = g.{RGR2.Id} WHERE u.{RGU2.ExecutionId} = '{s.Plan.Execution}'"));
        Assert.Equal(6, sql.Split('\n').Select(l => l[0]).Distinct().Count());                                          // O, E, L, C, R and P are all present
        // no session setting changes a byte
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        foreach (var setting in new[] { "SET TimeZone = 'Asia/Riyadh'", "SET DateStyle = 'German, DMY'", "SET IntervalStyle = 'iso_8601'", "SET bytea_output = 'escape'", "SET extra_float_digits = -3", "SET lc_numeric = 'C'" })
        {
            await using (var cmd = new NpgsqlCommand(setting, conn)) await cmd.ExecuteNonQueryAsync();
            await using var q = new NpgsqlCommand($"SELECT public.{RE2.FnRowsText}('{s.Plan.Execution}')", conn);
            Assert.Equal(expected.Text, (string)(await q.ExecuteScalarAsync())!);
        }
        // an execution that does not exist has no rows: the empty string, whose digest is the design's
        Assert.Equal("", await Sql<string>($"SELECT public.{RE2.FnRowsText}('{Guid.NewGuid()}')"));
    }

    [PostgresRoleFact]
    public async Task The_sql_epoch_text_of_an_average_epoch_has_no_layers_and_equals_the_dotnet_builder()
    {
        await PrepareRuntime(executorGrant: true);
        var s = await SimpleEpoch();
        Assert.Null(await Run(s.Script));
        var item = Avg.Id; var w = W1.Id; var original = s.Issue.CostOperationId;
        // the figures of SimpleEpoch: the issue entry (4 / 40.00 on the 12th), storno 4 / 40.00, event 10 / 200.00 on the 11th, rebook 4 / 60.00; the position ends at 16 / 240.00
        var expected = EpochText.Build(new EpochTextInput(
            [new(1, original, 2, Day3, null, null, null, $"epoch-{s.Storno:N}", Executor), new(2, null, 1, Day2, null, null, null, $"epoch-{s.Event:N}", Executor), new(3, original, 2, Day3, null, null, null, $"epoch-{s.Rebook:N}", Executor)],
            [new(item, w, 3, 1, s.IssueEntry, 2, 1, Day3, 4m, 40m, 10m, 100m), new(item, w, 4, 2, null, 1, 1, Day2, 10m, 200m, 20m, 300m), new(item, w, 5, 3, s.IssueEntry, 2, -1, Day3, 4m, 60m, 16m, 240m)],
            [], [], [], [new(item, w, 2, 5, 16m, 240m, 0)]));
        Assert.Equal(expected.Text, await Sql<string>($"SELECT public.{RE2.FnRowsText}('{s.Plan.Execution}')"));
        Assert.Equal(expected.Digest, await Sql<string>($"SELECT {RGR2.EpochDigest} FROM {RGR2.Table} g JOIN {RGU2.Table} u ON u.{RGU2.GrantId} = g.{RGR2.Id} WHERE u.{RGU2.ExecutionId} = '{s.Plan.Execution}'"));
    }

    // ------------------------------------------------------------------------------------------------------------ every commit-time rule refuses
    private async Task<(Script Script, Simple S)> Fresh()
    {
        await PrepareRuntime(executorGrant: true);
        var s = await SimpleEpoch();
        return (s.Script, s);
    }

    [PostgresRoleFact]
    public async Task An_execution_without_a_grant_use_or_with_a_grant_that_expired_before_the_commit_is_refused_and_nothing_persists()
    {
        var (script, s) = await Fresh();
        script.AutoGrant = false;
        RefusedWith("execution_needs_exactly_one_grant_use", await Run(script));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CX.Table}"));
        // a grant that expires two seconds from now, and a script that is still running after that
        script.AutoGrant = true; script.GrantNotAfterIn = 2;
        script.Hooks["lineage"] = () => Task.Delay(TimeSpan.FromSeconds(4));
        RefusedWith("grant_expired_at_commit", await Run(script));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CX.Table}"));
        // control: the same script with an hour to go commits
        script.GrantNotAfterIn = 3600; script.Hooks.Clear();
        Assert.Null(await Run(script));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {CX.Table}"));
        _ = s;
    }

    [PostgresRoleFact]
    public async Task A_grant_of_another_epoch_is_refused_whatever_single_figure_differs()
    {
        var (script, s) = await Fresh();
        // the grant signs the digest of the epoch of ANOTHER script: the dry run applies one change to one figure of the rebook (amount, quantity, date) or drops a row; the real run writes the original
        string Rebook(string sql, string from, string to) => sql.Replace(from, to);
        var rebookSql = script.Of("rebook");
        Assert.Contains(rebookSql, q => q.Contains("60"));
        var cases = new (string Name, Func<string, string, string> Change)[]
        {
            ("amount", (step, sql) => step == "rebook" ? Regex.Replace(sql, @"\b60\b", "61") : sql),
            ("another operation key", (step, sql) => step == "event" ? sql.Replace("epoch-", "epoc2-") : sql),
        };
        foreach (var (name, change) in cases)
        {
            script.DryRunSql = change;
            var outcome = await Run(script);
            Assert.True(outcome is not null && outcome.Contains("epoch_differs_from_the_signed_epoch"), name + ": " + outcome);
        }
        script.GrantEpochOverride = new string('b', 64);
        RefusedWith("epoch_differs_from_the_signed_epoch", await Run(script));
        script.DryRunSql = null; script.GrantEpochOverride = null;
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CX.Table}"));
        Assert.Null(await Run(script));                                                                                  // control
        _ = s;
    }

    [PostgresRoleFact]
    public async Task A_row_changed_after_the_grant_or_a_key_revoked_from_before_it_was_accepted_is_refused_at_commit()
    {
        await PrepareRuntime(executorGrant: true);
        var s = await SimpleEpoch();
        // the plan's stored fingerprint is changed after the grant was accepted (as the owner, with the plan's own guards off): the message rebuilt now differs, the signature no longer verifies
        s.Script.Hooks["rebook"] = async () =>
        {
            Assert.Null(await ExecuteOwner($"ALTER TABLE {CP.Table} DISABLE TRIGGER USER"));
        };
        // the script runs as the owner in this test: a plain statement after the epoch alters the plan row
        s.Script.Add(("tamper", [$"ALTER TABLE {CP.Table} DISABLE TRIGGER USER", $"UPDATE {CP.Table} SET {CP.PolicyFingerprint} = '{new string('e', 64)}' WHERE {CP.Id} = '{s.Plan.Id}'", $"ALTER TABLE {CP.Table} ENABLE TRIGGER USER"]));
        s.Script.Hooks.Remove("rebook");
        var tampered = s.Script.ToList(); s.Script.Clear();
        foreach (var step in tampered.Where(t => t.Name != "tamper")) { if (step.Name == "executed") s.Script.Add(tampered.Single(t => t.Name == "tamper")); s.Script.Add(step); }
        RefusedWith("grant_signature_invalid_at_commit", await RunAsOwner(s.Script));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CX.Table}"));
    }

    private async Task<string?> ExecuteOwner(params string[] statements)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        try { foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn); await cmd.ExecuteNonQueryAsync(); } return null; }
        catch (PostgresException ex) { return Describe(ex); }
    }

    [PostgresRoleFact]
    public async Task A_key_revoked_from_before_the_grant_was_accepted_is_refused_at_commit()
    {
        await PrepareRuntime(executorGrant: true);
        var s = await SimpleEpoch();
        // the owner revokes the test key retroactively (design D5) in the same transaction, after the grant was accepted
        s.Script.Add(("revoke", [$"UPDATE {RK2.Table} SET {RK2.RevokeFrom} = clock_timestamp() - interval '1 hour' WHERE {RK2.KeyId} = '{AutoKeyId}'"]));
        var steps = s.Script.ToList(); s.Script.Clear();
        foreach (var step in steps.Where(t => t.Name != "revoke")) { if (step.Name == "executed") s.Script.Add(steps.Single(t => t.Name == "revoke")); s.Script.Add(step); }
        RefusedWith("grant_key_revoked", await RunAsOwner(s.Script));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CX.Table}"));
    }

    // ------------------------------------------------------------------------------------------------------------ inertness and the ordering of the triggers
    [PostgresRoleFact]
    public async Task The_new_checks_are_deferred_constraint_triggers_that_fire_after_the_completeness_check_of_0036_and_the_runtime_role_still_cannot_write_in_production()
    {
        await PrepareRuntime();
        // both are constraint triggers, deferrable and initially deferred, on the execution table and on the grant use table
        foreach (var (table, trigger) in new[] { (CX.Table, RE2.TrCommitExecution), (RGU2.Table, RE2.TrCommitUse) })
            Assert.True(await Sql<bool>($"SELECT t.tgdeferrable AND t.tginitdeferred AND t.tgconstraint <> 0 FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relname = '{table}' AND t.tgname = '{trigger}'"), trigger);
        // they fire in name order within an event: the execution check sorts after the completeness check of 0036 on the same table, so a 0036 refusal is never masked by this one
        var names = (await Rows($"SELECT t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relname = '{CX.Table}' AND t.tgconstraint <> 0 AND NOT t.tgisinternal ORDER BY t.tgname")).Select(r => (string)r[0]).ToList();
        Assert.Equal(CX.TrCommit, names.First(n => string.CompareOrdinal(n, CX.TrCommit) >= 0));
        Assert.True(string.CompareOrdinal(RE2.TrCommitExecution, CX.TrCommit) > 0);
        // production grants: still SELECT only on the grant tables and the execution tables
        foreach (var t in new[] { RGR2.Table, RGU2.Table, CX.Table }) Assert.Equal(["SELECT"], CompanyRuntimePrivileges.Tables.Single(x => x.Table == t).Privileges);
        // the functions are invoker-rights readers like those of A2
        foreach (var f in new[] { RE2.FnRowsText, RE2.FnCheckExecution, RE2.FnCommitExecution, RE2.FnCommitUse })
        {
            Assert.False(await Sql<bool>($"SELECT prosecdef FROM pg_proc WHERE proname = '{f}'"), f);
            Assert.True(await Sql<bool>($"SELECT proconfig IS NULL FROM pg_proc WHERE proname = '{f}'"), f);
        }
    }
}
