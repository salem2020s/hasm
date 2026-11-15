using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using BizAudit = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using CoIdentity = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;

namespace Hasm.Infrastructure.CompanyDatabase;

/// <summary>
/// The EXACT privileges a company runtime role holds, as code. Enforcement is by PostgreSQL permissions, not by application checks:
/// <list type="bullet">
/// <item>CONNECT on its own company database only (PUBLIC has none, so no other company database accepts it);</item>
/// <item>USAGE on schema public (no CREATE: it cannot alter the schema);</item>
/// <item>SELECT on the identity binding and the migration journal (read by the schema gate);</item>
/// <item>SELECT and INSERT on the business audit table, nothing else (no UPDATE, DELETE, TRUNCATE, REFERENCES or TRIGGER);</item>
/// <item>no ownership of anything, no role attributes, no memberships.</item>
/// </list>
/// Later phases append their business tables here (with the least privileges their services need) and re-run
/// <c>Hasm.Admin grant-company-runtime</c>; a table that is not listed is not accessible to the runtime.
/// </summary>
public static class CompanyRuntimePrivileges
{
    /// <summary>Table-level privileges, by physical table name.</summary>
    public static IReadOnlyList<(string Table, string[] Privileges)> Tables { get; } =
    [
        (CoIdentity.Table, ["SELECT"]),
        (SystemSchema.MigrationJournalTable, ["SELECT"]),
        (BizAudit.Table, ["SELECT", "INSERT"]),
        (CompanySchema.Profile.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.Settings.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.Currency.Table, ["SELECT"]),
        (CompanySchema.CompanyCurrency.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.ConfigRevision.Table, ["SELECT", "INSERT"]),
        // Branches (Phase 3.1B): never DELETE/TRUNCATE (records are permanent; triggers refuse even the owner).
        (CompanySchema.Branch.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.BranchRevision.Table, ["SELECT", "INSERT"]),
        // Warehouses (Phase 3.1C): same rules as branches.
        (CompanySchema.Warehouse.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.WarehouseRevision.Table, ["SELECT", "INSERT"]),
        // Fiscal calendar (Phase 3.1D): same rules; years and periods are never deleted, history is append-only.
        (CompanySchema.FiscalYear.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.FiscalPeriod.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.FiscalHistory.Table, ["SELECT", "INSERT"]),
        (CompanySchema.FiscalReopenRequest.Table, ["SELECT", "INSERT", "UPDATE"]),
        // Chart of Accounts (Phase 3.2A): accounts are never deleted; history is append-only.
        (CompanySchema.Account.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.AccountSettings.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.AccountRevision.Table, ["SELECT", "INSERT"]),
        // General Ledger foundation (Phase 3.2B). Numbering: counters only advance, issued numbers are never deleted, history is append-only.
        (CompanySchema.LedgerHistory.Table, ["SELECT", "INSERT"]),
        (CompanySchema.NumberingRule.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.NumberCounter.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.NumberIssue.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.ExchangeRate.Table, ["SELECT", "INSERT"]),
        (CompanySchema.CostCenter.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.ApprovalPolicy.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.ApprovalDecision.Table, ["SELECT", "INSERT"]),
        // Journal (slice S5). Entries are never deleted; the event log is append-only. DELETE is granted ONLY on the line and allocation tables, whose rows PostgreSQL lets change only while
        // the parent entry is a Draft (a draft edit rewrites its lines); posted, reversed and cancelled entries keep their lines forever.
        (CompanySchema.LedgerSettings.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.JournalEntry.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.JournalLine.Table, ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        (CompanySchema.JournalAllocation.Table, ["SELECT", "INSERT", "DELETE"]),
        (CompanySchema.JournalEvent.Table, ["SELECT", "INSERT"]),
        // General Ledger completion (Phase 3.2C): policies are never deleted; the policy history is append-only.
        (CompanySchema.PolicyHistory.Table, ["SELECT", "INSERT"]),
        (CompanySchema.AccountCostCenterRule.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.CostCenterBranchRule.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.CostCenterBranchEligibility.Table, ["SELECT", "INSERT", "UPDATE"]),
        // Approval stages and bindings (S2) and year-end processing (S3): append-only except the single settings row.
        (CompanySchema.ApprovalPolicyStage.Table, ["SELECT", "INSERT"]),
        (CompanySchema.DocumentApprovalBinding.Table, ["SELECT", "INSERT"]),
        (CompanySchema.ClosingSettings.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.FiscalYearClosing.Table, ["SELECT", "INSERT"]),
        (CompanySchema.FiscalYearCarryforward.Table, ["SELECT", "INSERT"]),
        // Generic import framework (Phase 3.2D): batches are never deleted; the history is append-only. DELETE is granted ONLY on the staged rows, which PostgreSQL lets change only in the states that allow it (restaging, retention purge of a finished batch).
        (CompanySchema.ImportBatch.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.ImportRow.Table, ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        (CompanySchema.ImportEvent.Table, ["SELECT", "INSERT"]),
        (CompanySchema.ImportSettings.Table, ["SELECT", "INSERT", "UPDATE"]),
        // Financial statement classification (Phase 3.3 S3): immutable versions, never updated or deleted.
        (CompanySchema.StatementMappingVersion.Table, ["SELECT", "INSERT"]),
        (CompanySchema.StatementMappingEntry.Table, ["SELECT", "INSERT"]),
        // Inventory master data (Phase 3.4): masters are never deleted (PostgreSQL refuses it); alternate units may be replaced while an item is edited (no stock history exists yet).
        (CompanySchema.InventorySettings.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.Unit.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.ItemGroup.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.Item.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.ItemUnit.Table, ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        (CompanySchema.ItemBarcode.Table, ["SELECT", "INSERT", "UPDATE"]),
        // Stock movement documents (Phase 3.5): documents are never deleted (PostgreSQL refuses it); lines are rewritten only while the document is a draft; the history is append-only.
        (CompanySchema.StockDocument.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.StockDocumentLine.Table, ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        (CompanySchema.StockDocumentEvent.Table, ["SELECT", "INSERT"]),
        // Inventory costing engine (Phase 3.6): the ledger, consumptions and operations are append-only (an operation can only be marked reversed); positions and layers change only through the engine's own figures. Never deleted.
        (CompanySchema.CostOperation.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.CostPosition.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.CostEntry.Table, ["SELECT", "INSERT"]),
        (CompanySchema.CostLayer.Table, ["SELECT", "INSERT", "UPDATE"]),
        (CompanySchema.CostConsumption.Table, ["SELECT", "INSERT"]),
        // Inventory accounting configuration (Phase 3.7A): immutable versions, never updated or deleted. Stock posting records (Phase 3.7B): append-only.
        (CompanySchema.InventoryAccountingVersion.Table, ["SELECT", "INSERT"]),
        (CompanySchema.InventoryAccountingGroup.Table, ["SELECT", "INSERT"]),
        (CompanySchema.StockPosting.Table, ["SELECT", "INSERT"]),
        (CompanySchema.StockPostingLine.Table, ["SELECT", "INSERT"]),
        (CompanySchema.StockPostingEvent.Table, ["SELECT", "INSERT"]),
        (CompanySchema.StockPostingReversal.Table, ["SELECT", "INSERT"]),
    ];

    /// <summary>Privileges that must never reach a runtime role on the audit table or on the schema-state tables.</summary>
    internal static readonly string[] ForbiddenOnAudit = ["UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
    internal static readonly string[] ForbiddenOnBranch = ["DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
    internal static readonly string[] ForbiddenOnDraftTables = ["TRUNCATE", "REFERENCES", "TRIGGER"];
    internal static readonly string[] ForbiddenOnState = ["INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];

    private static string Q(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    /// <summary>Statements run on the maintenance database by the database owner: connect rights for this one database only.</summary>
    public static IReadOnlyList<string> DatabaseStatements(string physicalDatabaseName, string roleName) =>
    [
        $"REVOKE ALL ON DATABASE {Q(physicalDatabaseName)} FROM PUBLIC",
        $"REVOKE ALL ON DATABASE {Q(physicalDatabaseName)} FROM {Q(roleName)}",
        $"GRANT CONNECT ON DATABASE {Q(physicalDatabaseName)} TO {Q(roleName)}",
    ];

    /// <summary>
    /// Statements run inside the company database by the object owner. They first strip everything the role (and PUBLIC) holds and then
    /// grant the catalog, so re-running converges on exactly the catalog whatever earlier builds granted.
    /// </summary>
    public static IReadOnlyList<string> SchemaStatements(string roleName)
    {
        var role = Q(roleName);
        var list = new List<string>
        {
            "REVOKE ALL ON SCHEMA public FROM PUBLIC",
            $"REVOKE ALL ON SCHEMA public FROM {role}",
            "REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC",
            $"REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {role}",
            "REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC",
            $"REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM {role}",
            $"GRANT USAGE ON SCHEMA public TO {role}",
        };
        foreach (var (table, privileges) in Tables)
            list.Add($"GRANT {string.Join(", ", privileges)} ON TABLE {Q(table)} TO {role}");
        return list;
    }

    private const string ViolationSql = """
        SELECT code FROM (
          SELECT 'role_attributes' AS code FROM pg_roles WHERE rolname = @r::name AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
          UNION ALL SELECT 'role_membership' FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = @r::name
          UNION ALL SELECT 'owns_database' FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE d.datname = current_database() AND r.rolname = @r::name
          UNION ALL SELECT 'database_create_or_temp' WHERE has_database_privilege(@r::name, current_database(), 'CREATE') OR has_database_privilege(@r::name, current_database(), 'TEMPORARY')
          UNION ALL SELECT 'schema_create' WHERE has_schema_privilege(@r::name, 'public', 'CREATE')
          UNION ALL SELECT 'owns_objects' FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE r.rolname = @r::name AND c.relnamespace = 'public'::regnamespace
          UNION ALL SELECT 'audit_modifiable' WHERE to_regclass(@audit) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@audit), p))
          UNION ALL SELECT 'history_modifiable' WHERE to_regclass(@history) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@history), p))
          UNION ALL SELECT 'branch_history_modifiable' WHERE to_regclass(@branch_history) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@branch_history), p))
          UNION ALL SELECT 'branch_modifiable' WHERE to_regclass(@branch) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_branch::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@branch), p))
          UNION ALL SELECT 'warehouse_history_modifiable' WHERE to_regclass(@warehouse_history) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@warehouse_history), p))
          UNION ALL SELECT 'warehouse_modifiable' WHERE to_regclass(@warehouse) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_branch::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@warehouse), p))
          UNION ALL SELECT 'fiscal_history_modifiable' WHERE to_regclass(@fiscal_history) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@fiscal_history), p))
          UNION ALL SELECT 'fiscal_modifiable' FROM unnest(@fiscal_tables::text[]) t WHERE to_regclass(t) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_branch::text[]) p WHERE has_table_privilege(@r::name, to_regclass(t), p))
          UNION ALL SELECT 'account_history_modifiable' WHERE to_regclass(@account_history) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@account_history), p))
          UNION ALL SELECT 'account_modifiable' FROM unnest(@account_tables::text[]) t WHERE to_regclass(t) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_branch::text[]) p WHERE has_table_privilege(@r::name, to_regclass(t), p))
          UNION ALL SELECT 'ledger_history_modifiable' WHERE to_regclass(@ledger_history) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@ledger_history), p))
          UNION ALL SELECT 'policy_history_modifiable' WHERE to_regclass(@policy_history) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@policy_history), p))
          UNION ALL SELECT 'ledger_modifiable' FROM unnest(@ledger_tables::text[]) t WHERE to_regclass(t) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_branch::text[]) p WHERE has_table_privilege(@r::name, to_regclass(t), p))
          UNION ALL SELECT 'journal_event_modifiable' WHERE to_regclass(@journal_event) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_audit::text[]) p WHERE has_table_privilege(@r::name, to_regclass(@journal_event), p))
          UNION ALL SELECT 'journal_draft_modifiable' FROM unnest(@journal_draft_tables::text[]) t WHERE to_regclass(t) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_draft::text[]) p WHERE has_table_privilege(@r::name, to_regclass(t), p))
          UNION ALL SELECT 'state_modifiable' FROM unnest(@state_tables::text[]) t
              WHERE to_regclass(t) IS NOT NULL AND EXISTS (SELECT 1 FROM unnest(@forbidden_state::text[]) p WHERE has_table_privilege(@r::name, to_regclass(t), p))
        ) v
        """;

    /// <summary>
    /// Reports every way <paramref name="roleName"/> holds more than the catalog allows in the connected company database
    /// (codes only; empty = least privilege). Run by the grant command with the owner account, and by the API gate with the runtime role itself.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindViolationsAsync(NpgsqlConnection conn, string roleName, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(ViolationSql, conn);
        cmd.Parameters.AddWithValue("r", roleName);
        cmd.Parameters.AddWithValue("audit", BizAudit.Table);
        cmd.Parameters.AddWithValue("forbidden_audit", ForbiddenOnAudit);
        cmd.Parameters.AddWithValue("history", CompanySchema.ConfigRevision.Table);
        cmd.Parameters.AddWithValue("branch", CompanySchema.Branch.Table);
        cmd.Parameters.AddWithValue("branch_history", CompanySchema.BranchRevision.Table);
        cmd.Parameters.AddWithValue("forbidden_branch", ForbiddenOnBranch);
        cmd.Parameters.AddWithValue("warehouse", CompanySchema.Warehouse.Table);
        cmd.Parameters.AddWithValue("warehouse_history", CompanySchema.WarehouseRevision.Table);
        cmd.Parameters.AddWithValue("fiscal_history", CompanySchema.FiscalHistory.Table);
        cmd.Parameters.AddWithValue("account_history", CompanySchema.AccountRevision.Table);
        cmd.Parameters.AddWithValue("ledger_history", CompanySchema.LedgerHistory.Table);
        cmd.Parameters.AddWithValue("policy_history", CompanySchema.PolicyHistory.Table);
        cmd.Parameters.AddWithValue("ledger_tables", new[] { CompanySchema.NumberingRule.Table, CompanySchema.NumberCounter.Table, CompanySchema.NumberIssue.Table, CompanySchema.ExchangeRate.Table, CompanySchema.CostCenter.Table, CompanySchema.ApprovalPolicy.Table, CompanySchema.ApprovalDecision.Table, CompanySchema.LedgerSettings.Table, CompanySchema.JournalEntry.Table, CompanySchema.AccountCostCenterRule.Table, CompanySchema.CostCenterBranchRule.Table, CompanySchema.CostCenterBranchEligibility.Table, CompanySchema.ApprovalPolicyStage.Table, CompanySchema.DocumentApprovalBinding.Table, CompanySchema.ClosingSettings.Table, CompanySchema.FiscalYearClosing.Table, CompanySchema.FiscalYearCarryforward.Table });
        cmd.Parameters.AddWithValue("journal_event", CompanySchema.JournalEvent.Table);
        cmd.Parameters.AddWithValue("journal_draft_tables", new[] { CompanySchema.JournalLine.Table, CompanySchema.JournalAllocation.Table });
        cmd.Parameters.AddWithValue("forbidden_draft", ForbiddenOnDraftTables);
        cmd.Parameters.AddWithValue("account_tables", new[] { CompanySchema.Account.Table, CompanySchema.AccountSettings.Table });
        cmd.Parameters.AddWithValue("fiscal_tables", new[] { CompanySchema.FiscalYear.Table, CompanySchema.FiscalPeriod.Table, CompanySchema.FiscalReopenRequest.Table });
        cmd.Parameters.AddWithValue("state_tables", new[] { CoIdentity.Table, SystemSchema.MigrationJournalTable, CompanySchema.Currency.Table });
        cmd.Parameters.AddWithValue("forbidden_state", ForbiddenOnState);
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) codes.Add(r.GetString(0));
        return [.. codes];
    }

    /// <summary>True when the role holds every privilege of the catalog on every table that exists (what the API needs to work at all).</summary>
    public static async Task<bool> HasRequiredAccessAsync(NpgsqlConnection conn, string roleName, CancellationToken ct = default)
    {
        foreach (var (table, privileges) in Tables)
        {
            await using var cmd = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL AND bool_and(has_table_privilege(@r::name, to_regclass(@t), p)) FROM unnest(@p::text[]) p", conn);
            cmd.Parameters.AddWithValue("r", roleName);
            cmd.Parameters.AddWithValue("t", table);
            cmd.Parameters.AddWithValue("p", privileges);
            // A table the database does not have yet (a migration this backend knows but the database lacks) is reported by the schema gate, not here.
            if (await cmd.ExecuteScalarAsync(ct) is not true) return false;
        }
        return true;
    }
}
