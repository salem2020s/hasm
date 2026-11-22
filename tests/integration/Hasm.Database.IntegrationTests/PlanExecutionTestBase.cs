using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using AU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanApproval;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanPosition;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using CX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanExecution;
using EL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochEntryLineage;
using EM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochMember;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.8D step 4: the X1-A database layer (company migration 0035, provisional number) against real PostgreSQL, driven by SQL through the REAL per-company runtime role, exactly as design section 8 describes the
/// N-tests. No executor exists: the tests write the plan, approval, execution and epoch rows themselves, which is also what a hostile holder of the runtime role's grants could try. The history is built by the real
/// services (owner connection, as in every service suite); everything that probes the new rules runs as the runtime role, and an execution runs inside the real audited company transaction, so its business audit
/// event is a genuine, key-authenticated event written in the same transaction. Role-capable disposable cluster only.
/// </summary>
public abstract class PlanExecutionTestBase : NegativeStockTestBase
{
    protected override bool RolesRequired => true;

    protected CompanyAuditedTransactions RuntimeTx = null!;
    protected readonly Guid Planner = Guid.NewGuid(), PlanApprover = Guid.NewGuid(), Executor = Guid.NewGuid();
    protected static readonly string[] PlanFingerprints = [CP.PlanFingerprint, CP.ComparisonFingerprint, CP.EvidenceFingerprint, CP.PeriodsFingerprint, CP.PositionFingerprint, CP.PolicyFingerprint, CP.AccountingFingerprint];
    protected static readonly string[] ApprovalFingerprints = [CA.PlanFingerprint, CA.ComparisonFingerprint, CA.EvidenceFingerprint, CA.PeriodsFingerprint, CA.PositionFingerprint, CA.PolicyFingerprint, CA.AccountingFingerprint];
    protected const string Refusal = "P0001";   // RAISE EXCEPTION of a guard (never a missing grant, 42501)

    protected sealed class Plan
    {
        public Guid Id = Guid.NewGuid(), Approval = Guid.NewGuid(), Execution = Guid.NewGuid(), Posting = Guid.NewGuid();
        public StockDocumentView Doc = null!;
        public Guid Item, Warehouse;
    }

    /// <summary>The steps of one executing transaction, by name, so a test can drop, replace or extend exactly one of them. The step "audit" appends the business audit event through the real audit trail.</summary>
    protected sealed class Script : List<(string Name, string[] Sql)>
    {
        public short AuditType = CostingAuditEvents.PlanExecuted;
        public Guid? AuditSubject, AuditTarget;
        public Dictionary<string, Func<Task>> Hooks { get; } = [];
        public Script Without(string name) { RemoveAll(s => s.Name == name); return this; }
        public Script Set(string name, params string[] sql) { this[FindIndex(s => s.Name == name)] = (name, sql); return this; }
        public Script After(string name, string added, params string[] sql) { Insert(FindIndex(s => s.Name == name) + 1, (added, sql)); return this; }
        public Script Before(string name, string added, params string[] sql) { Insert(FindIndex(s => s.Name == name), (added, sql)); return this; }
        public Script Until(string name) { var i = FindIndex(s => s.Name == name); RemoveRange(i + 1, Count - i - 1); return this; }
        public string[] Of(string name) => Find(s => s.Name == name).Sql;
    }

    protected async Task PrepareRuntime()
    {
        await PrepareNegative();
        var granted = await new CompanyRuntimeProvisioner(Configured(useRoleAdmin: true), Registry).GrantAsync(Company.Id);
        Assert.True(granted.LoginVerified);
        RuntimeTx = new CompanyAuditedTransactions(RealResolver(), Audit, Clock);
    }

    protected async Task<NpgsqlConnection> AsRuntime(string? database = null)
    {
        var credential = (await new FileCompanyCredentialStore(CredentialDirectory).GetAsync(Company.Id))!;
        var cs = new NpgsqlConnectionStringBuilder(Configured().BuildCompanyRuntimeConnectionString(database ?? Company.PhysicalDatabaseName, credential.Username, credential.Password)) { Pooling = false };
        var conn = new NpgsqlConnection(cs.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    protected static string Describe(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) if (e is PostgresException pg) return pg.SqlState + ": " + pg.MessageText;
        return ex.GetType().Name + ": " + ex.Message;
    }

    /// <summary>Runs the statements in ONE transaction as the RUNTIME ROLE. Null when it committed; otherwise "SQLSTATE: message" of the refusal (deferred guards answer at the commit).</summary>
    protected async Task<string?> Rt(params string[] statements)
    {
        await using var conn = await AsRuntime();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
            return null;
        }
        catch (PostgresException ex) { return Describe(ex); }
    }

    /// <summary>Runs a script as the RUNTIME ROLE inside the real audited company transaction (business change and audit event commit together). Null when it committed.</summary>
    protected async Task<string?> Run(Script script)
    {
        try
        {
            await RuntimeTx.ExecuteAsync(Company.Id, async work =>
            {
                var audit = "gen_random_uuid()";
                foreach (var (name, sql) in script.ToList())
                {
                    if (name == "audit")
                    {
                        var receipt = await work.AppendAuditAsync(new AuditEventData(script.AuditType, Executor, script.AuditTarget, script.AuditSubject, "compensation plan executed"));
                        audit = $"(SELECT {AU.EventId} FROM {AU.Table} WHERE {AU.StreamKey} = '{receipt.StreamKey}' AND {AU.Seq} = {receipt.Seq})";
                    }
                    foreach (var s in sql) { await using var cmd = new NpgsqlCommand(s.Replace("@AUDIT@", audit), work.Connection, work.Transaction); await cmd.ExecuteNonQueryAsync(); }
                    if (script.Hooks.TryGetValue(name, out var hook)) await hook();
                }
                return 0;
            });
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>The refusal of an epoch entry in a period that is not Open: either the plan guard's own ("period_not_open") or the message of the stock-period guard (STOCK-PERIOD-DB, company migration 0034), whichever of the
    /// two triggers on the cost entry table fires first. Both messages are named; any other text fails.</summary>
    protected static void RefusedByPeriod(string stockPeriodMessage, string? outcome)
    {
        Assert.NotNull(outcome);
        Assert.StartsWith(Refusal, outcome);
        Assert.True(outcome.Contains("period_not_open", StringComparison.Ordinal) || outcome.Contains(stockPeriodMessage, StringComparison.Ordinal), $"neither the plan guard nor the stock-period guard refused: {outcome}");
    }

    protected const string SoftClosedMessage = "the fiscal period is soft closed", FinalClosedMessage = "the fiscal period is final closed", NoPeriodMessage = "no fiscal period covers the posting date";

    protected static void RefusedWith(string expected, string? outcome) { Assert.NotNull(outcome); Assert.StartsWith(Refusal, outcome); Assert.Contains(expected, outcome); }

    // ---------------------------------------------------------------------------------------------------------------- SQL of the rows an executor would write
    private static string N(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => $"DATE '{d:yyyy-MM-dd}'";
    protected static string Fp(char c = 'a') => "'" + new string(c, 64) + "'";

    protected string PlanInsert(Plan p, Guid? creator = null, string extraColumns = "", string extraValues = "") =>
        $"INSERT INTO {CP.Table} ({CP.Id}, {CP.ItemId}, {CP.WarehouseId}, {CP.DocumentId}, {CP.DocumentRevision}, {CP.Payload}, {string.Join(", ", PlanFingerprints)}, {CP.Status}, {CP.Revision}, {CP.CreatedBy}, {CP.CreatedAtUtc}, {CP.UpdatedAtUtc}{extraColumns}) " +
        $"VALUES ('{p.Id}', '{p.Item}', '{p.Warehouse}', '{p.Doc.Id}', {p.Doc.Revision}, '{{\"event\":\"receipt\"}}', {string.Join(", ", PlanFingerprints.Select((_, i) => Fp((char)('1' + i))))}, 1, 1, '{creator ?? Planner}', now(), now(){extraValues})";

    protected static string Closure(Plan p, Guid item, Guid warehouse) => $"INSERT INTO {CC.Table} ({CC.PlanId}, {CC.ItemId}, {CC.WarehouseId}) VALUES ('{p.Id}', '{item}', '{warehouse}')";

    /// <summary>The approval row: it repeats the plan's fingerprints (the closure hash is read back from the plan, where PostgreSQL computed it). <paramref name="differs"/> names one fingerprint column to falsify.</summary>
    protected string ApprovalInsert(Plan p, Guid? approver = null, string? differs = null, long revision = 1, Guid? id = null)
    {
        var values = ApprovalFingerprints.Select((c, i) => c == differs ? Fp('9') : Fp((char)('1' + i)));
        var closure = differs == CA.ClosureHash ? Fp('9') : $"(SELECT {CP.ClosureHash} FROM {CP.Table} WHERE {CP.Id} = '{p.Id}')";
        return $"INSERT INTO {CA.Table} ({CA.Id}, {CA.PlanId}, {CA.PlanRevision}, {string.Join(", ", ApprovalFingerprints)}, {CA.ClosureHash}, {CA.Approver}, {CA.ApprovedAtUtc}) " +
               $"VALUES ('{id ?? p.Approval}', '{p.Id}', {revision}, {string.Join(", ", values)}, {closure}, '{approver ?? PlanApprover}', now())";
    }

    protected string Approve(Plan p, Guid? approver = null, Guid? approval = null) =>
        $"UPDATE {CP.Table} SET {CP.Status} = 2, {CP.ApprovalId} = '{approval ?? p.Approval}', {CP.ApprovedBy} = '{approver ?? PlanApprover}', {CP.ApprovedAtUtc} = now(), {CP.Revision} = {CP.Revision} + 1, {CP.UpdatedAtUtc} = now() WHERE {CP.Id} = '{p.Id}'";

    protected static string SetStatus(Plan p, int status) => $"UPDATE {CP.Table} SET {CP.Status} = {status}, {CP.Revision} = {CP.Revision} + 1, {CP.UpdatedAtUtc} = now() WHERE {CP.Id} = '{p.Id}'";

    protected string ExecutionInsert(Plan p, Guid? by = null, string audit = "@AUDIT@", Guid? id = null, Guid? posting = null) =>
        $"INSERT INTO {CX.Table} ({CX.Id}, {CX.PlanId}, {CX.PostingId}, {CX.AuditEventId}, {CX.ExecutedBy}, {CX.ExecutedAtUtc}) VALUES ('{id ?? p.Execution}', '{p.Id}', '{posting ?? p.Posting}', {audit.Replace("@RANDOM@", "gen_random_uuid()")}, '{by ?? Executor}', now())";

    protected string Op(Guid id, int kind, DateOnly date, Guid? reverses = null) =>
        $"INSERT INTO {OP.Table} ({OP.Id}, {OP.Key}, {OP.Kind}, {OP.RequestHash}, {OP.EffectiveDate}, {OP.CreatedBy}, {OP.CreatedAtUtc}, {OP.ReversesOperationId}) " +
        $"VALUES ('{id}', 'epoch-{id:N}', {kind}, '{new string('0', 64)}', {D(date)}, '{Executor}', now(), {(reverses is null ? "NULL" : $"'{reverses}'")})";

    protected static string Member(Guid operation, Guid execution, int role, Guid? original) =>
        $"INSERT INTO {EM.Table} ({EM.OperationId}, {EM.ExecutionId}, {EM.Role}, {EM.OriginalOperationId}) VALUES ('{operation}', '{execution}', {role}, {(original is null ? "NULL" : $"'{original}'")})";

    /// <summary>One entry of a position and the position's move, as the engine writes them: next sequence number, running figures, and the last date kept with GREATEST (so the position trigger of 0014 is satisfied).</summary>
    protected static string[] Move(Guid operation, Guid item, Guid warehouse, int kind, int direction, DateOnly date, decimal quantity, decimal amount, long? reverses = null, Guid? writeItem = null, Guid? writeWarehouse = null)
    {
        string q = N(quantity), a = N(amount), dir = $"({direction})", where = $"{PO.ItemId} = '{item}' AND {PO.WarehouseId} = '{warehouse}'";
        return
        [
            $"INSERT INTO {EN.Table} ({EN.OperationId}, {EN.PositionId}, {EN.ItemId}, {EN.WarehouseId}, {EN.Kind}, {EN.Direction}, {EN.EffectiveDate}, {EN.Quantity}, {EN.Amount}, {EN.QuantityAfter}, {EN.ValueAfter}, {EN.Seq}, {EN.ReversesEntryId}, {EN.CreatedAtUtc}) " +
            $"SELECT '{operation}', {PO.Id}, '{writeItem ?? item}', '{writeWarehouse ?? warehouse}', {kind}, {direction}, {D(date)}, {q}, {a}, {PO.Quantity} + {dir} * {q}, {PO.Value} + {dir} * {a}, {PO.LastSeq} + 1, {(reverses is null ? "NULL" : reverses.Value.ToString(CultureInfo.InvariantCulture))}, now() FROM {PO.Table} WHERE {where}",
            $"UPDATE {PO.Table} SET {PO.Quantity} = {PO.Quantity} + {dir} * {q}, {PO.Value} = {PO.Value} + {dir} * {a}, {PO.LastSeq} = {PO.LastSeq} + 1, {PO.LastEffectiveDate} = GREATEST({PO.LastEffectiveDate}, {D(date)}), {PO.UpdatedAtUtc} = now(), " +
            $"{PO.LastAverageQuantity} = CASE WHEN {PO.Quantity} + {dir} * {q} > 0 THEN {PO.Quantity} + {dir} * {q} ELSE {PO.LastAverageQuantity} END, " +
            $"{PO.LastAverageValue} = CASE WHEN {PO.Quantity} + {dir} * {q} > 0 THEN {PO.Value} + {dir} * {a} ELSE {PO.LastAverageValue} END WHERE {where}",
        ];
    }

    protected static string Lineage(long original, Guid stornoOperation, Guid rebookOperation, Guid execution) =>
        $"INSERT INTO {EL.Table} ({EL.OriginalEntryId}, {EL.StornoEntryId}, {EL.RebookEntryId}, {EL.ExecutionId}) VALUES ({original}, " +
        $"(SELECT {EN.Id} FROM {EN.Table} WHERE {EN.ReversesEntryId} = {original} AND {EN.OperationId} = '{stornoOperation}'), " +
        $"(SELECT {EN.Id} FROM {EN.Table} WHERE {EN.OperationId} = '{rebookOperation}' AND {EN.PositionId} = (SELECT {EN.PositionId} FROM {EN.Table} WHERE {EN.Id} = {original})), '{execution}')";

    protected string PostingRow(Plan p, Guid operation, DateOnly date, decimal total, Guid? id = null) =>
        $"INSERT INTO {SP.Table} ({SP.Id}, {SP.DocumentId}, {SP.DocType}, {SP.EffectiveDate}, {SP.PostedAtUtc}, {SP.PostedBy}, {SP.CostOperationId}, {SP.TotalValue}) VALUES ('{id ?? p.Posting}', '{p.Doc.Id}', 1, {D(date)}, now(), '{Executor}', '{operation}', {N(total)})";

    protected static string PeriodStatus(string date, int status, string extra = "") =>
        $"UPDATE {FP.Table} SET {FP.Status} = {status}, {FP.Revision} = {FP.Revision} + 1{extra} WHERE {FP.StartDate} <= DATE '{date}' AND {FP.EndDate} >= DATE '{date}'";

    // ---------------------------------------------------------------------------------------------------------------- scenarios
    protected async Task<long> EntryOf(Guid operation, WarehouseView? warehouse = null) =>
        await Sql<long>($"SELECT {EN.Id} FROM {EN.Table} WHERE {EN.OperationId} = '{operation}'" + (warehouse is null ? "" : $" AND {EN.WarehouseId} = '{warehouse.Id}'"));

    /// <summary>A plan for an approved receipt document of <paramref name="item"/> into W1 on <paramref name="date"/>, written with its closure in one transaction by the runtime role (not yet approved).</summary>
    protected async Task<Plan> ProposedPlan(ItemView item, string date, params (Guid Item, Guid Warehouse)[] alsoInClosure)
    {
        var p = new Plan { Doc = await Approved(Receipt(W1, date, L(item, Pcs, 10m))), Item = item.Id, Warehouse = W1.Id };
        Assert.Null(await Rt([PlanInsert(p), Closure(p, item.Id, W1.Id), .. alsoInClosure.Select(c => Closure(p, c.Item, c.Warehouse))]));
        return p;
    }

    protected async Task<Plan> ApprovedPlan(ItemView item, string date, params (Guid Item, Guid Warehouse)[] alsoInClosure)
    {
        var p = await ProposedPlan(item, date, alsoInClosure);
        Assert.Null(await Rt(ApprovalInsert(p), Approve(p)));
        return p;
    }

    protected sealed record Simple(Plan Plan, Script Script, StockPostingView Receipt, StockPostingView Issue, long IssueEntry, Guid Storno, Guid Event, Guid Rebook);

    /// <summary>
    /// The smallest replay epoch, with stated figures. History of the moving-average item AVG1 in W1 (real documents): receipt 10 for 100.00 on 10 March, issue 4 on 12 March at 10.00 = 40.00 (position 6 / 60.00).
    /// Back-dated event: a receipt of 10 for 200.00 on 11 March. Replay: 20 / 300.00 after the event (average 15.00), so the issue of 4 costs 60.00 and the position ends at 16 / 240.00.
    /// Epoch rows: storno of the issue entry (+4 / 40.00, dated 12 March), the event (+10 / 200.00, dated 11 March, back-dated), the rebook of the issue (-4 / 60.00, dated 12 March), one lineage row.
    /// </summary>
    protected async Task<Simple> SimpleEpoch(ItemView? of = null)
    {
        var item = of ?? Avg;
        var receipt = await ReceiveDoc(item, 10m, 100m, "2026-03-10");
        var issue = await IssueDoc(item, 4m, "2026-03-12");
        var issueEntry = await EntryOf(issue.CostOperationId);
        var p = await ApprovedPlan(item, "2026-03-11");
        Guid s = Guid.NewGuid(), e = Guid.NewGuid(), b = Guid.NewGuid();
        var script = new Script { AuditSubject = p.Id, AuditTarget = p.Execution };
        script.Add(("audit", []));
        script.Add(("execution", [ExecutionInsert(p)]));
        script.Add(("storno", [Op(s, 2, D3), Member(s, p.Execution, 1, issue.CostOperationId), .. Move(s, item.Id, W1.Id, 2, 1, D3, 4m, 40m, issueEntry)]));
        script.Add(("event", [Op(e, 1, D2), Member(e, p.Execution, 2, null), .. Move(e, item.Id, W1.Id, 1, 1, D2, 10m, 200m), PostingRow(p, e, D2, 200m)]));
        script.Add(("rebook", [Op(b, 2, D3), Member(b, p.Execution, 3, issue.CostOperationId), .. Move(b, item.Id, W1.Id, 2, -1, D3, 4m, 60m)]));
        script.Add(("lineage", [Lineage(issueEntry, s, b, p.Execution)]));
        script.Add(("executed", [SetStatus(p, 3)]));
        return new Simple(p, script, receipt, issue, issueEntry, s, e, b);
    }

    /// <summary>A digest of every row of a table (text form, ordered), so "unchanged" means byte-for-byte.</summary>
    protected Task<string> Digest(string table, string? where = null, string? database = null) =>
        InCompany<string>(database ?? Company.PhysicalDatabaseName, $"SELECT count(*)::text || ':' || coalesce(md5(string_agg(x::text, ',' ORDER BY x::text)), '') FROM {table} x" + (where is null ? "" : $" WHERE {where}"));

    /// <summary>The expected closure hash, computed independently of PostgreSQL from the byte-exact rule of the design (review L1).</summary>
    protected static string ExpectedClosureHash(IEnumerable<(Guid Item, Guid Warehouse)> pairs)
    {
        // uuid order in PostgreSQL is the order of the 16 bytes, which is the ordinal order of the lowercase hyphenated text
        var lines = pairs.Select(x => (I: x.Item.ToString("D").ToLowerInvariant(), W: x.Warehouse.ToString("D").ToLowerInvariant())).OrderBy(x => x.I, StringComparer.Ordinal).ThenBy(x => x.W, StringComparer.Ordinal).Select(x => x.I + ":" + x.W);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    /// <summary>
    /// The definitions of every function, trigger (with its enabled state), constraint, index and column of the public schema, keyed by a name that survives a dump and restore (an internal foreign-key trigger is keyed by
    /// its constraint, not by its oid-bearing name).
    /// </summary>
    protected async Task<SortedDictionary<string, string>> Catalog(string database)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(database));
        await conn.OpenAsync();
        const string sql = """
            SELECT 'function ' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')', pg_get_functiondef(p.oid) || ' | volatile=' || p.provolatile::text || ' definer=' || p.prosecdef || ' config=' || coalesce(p.proconfig::text, '') || ' acl=' || coalesce(p.proacl::text, '')
              FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace AND p.prokind = 'f'
            UNION ALL
            SELECT 'trigger ' || c.relname || '.' || CASE WHEN t.tgisinternal THEN 'internal:' || coalesce(k.conname, '?') || ':' || t.tgtype::text || ':' || t.tgfoid::regproc ELSE t.tgname END,
                   CASE WHEN t.tgisinternal THEN 'internal foreign-key trigger' ELSE pg_get_triggerdef(t.oid, false) END || ' | enabled=' || t.tgenabled::text || ' function=' || t.tgfoid::regproc
              FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid LEFT JOIN pg_constraint k ON k.oid = t.tgconstraint WHERE c.relnamespace = 'public'::regnamespace
            UNION ALL
            SELECT 'constraint ' || c.relname || '.' || k.conname, pg_get_constraintdef(k.oid) || ' | validated=' || k.convalidated || ' deferrable=' || k.condeferrable || ' deferred=' || k.condeferred
              FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid WHERE k.connamespace = 'public'::regnamespace
            UNION ALL
            SELECT 'index ' || tablename || '.' || indexname, indexdef FROM pg_indexes WHERE schemaname = 'public'
            UNION ALL
            SELECT 'column ' || c.relname || '.' || a.attname, format_type(a.atttypid, a.atttypmod) || ' notnull=' || a.attnotnull || ' default=' || coalesce(pg_get_expr(d.adbin, d.adrelid), '') || ' identity=' || a.attidentity::text || ' generated=' || a.attgenerated::text
              FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
            UNION ALL
            SELECT 'table ' || c.relname, 'rls=' || c.relrowsecurity || ' force=' || c.relforcerowsecurity || ' persistence=' || c.relpersistence::text FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'r'
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map.Add(r.GetString(0), r.GetString(1));
        return map;
    }
}
