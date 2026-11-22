using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;

namespace Hasm.Infrastructure.SystemDatabase;

/// <summary>One system table and exactly what the API runtime account may do with it. <paramref name="UpdateColumns"/> (when set) makes UPDATE a COLUMN privilege on those columns only.</summary>
public sealed record SystemRuntimeGrant(string Logical, string Table, string[] Privileges, string[]? UpdateColumns = null);

/// <summary>What the checker found: <see cref="Violations"/> are refused by the API gate (the TOTP secret store and the runtime account itself), <see cref="Notes"/> are reported only.</summary>
public sealed record SystemRuntimeFindings(IReadOnlyList<string> Violations, IReadOnlyList<string> Notes)
{
    public bool IsLeastPrivilege => Violations.Count == 0;
}

/// <summary>
/// The least privileges of the API runtime account (<c>Hasm:Database:Username</c>) on the SYSTEM database, as reviewed code (owner decision D-235, register item M2-TOTP; the system twin of
/// <see cref="CompanyRuntimePrivileges"/>). A migration cannot carry these grants: the account name is deployment configuration, a migration may not build SQL dynamically, and the migration account
/// cannot create a fixed group role (roles are cluster-wide and it is NOCREATEROLE). So the catalog is code, applied by <c>Hasm.Admin grant-system-runtime</c> under the owner account and checked by
/// the API (<see cref="SystemRuntimeGate"/>).
///
/// The TOTP secret store: SELECT and INSERT, and UPDATE ONLY on the state, confirmation time, last accepted step and failure counter. The runtime account can therefore never change the protected
/// material (cipher, nonce, tag, key id) or the identity of a secret, whatever the session flag <c>hasm.totp_rewrap</c> says, and never DELETE or TRUNCATE; rewrap is an owner-account operation.
/// Every other table carries what docs/DEVELOPMENT.md documents for the runtime account; a table with no privilege is one only the administrative tool uses.
/// </summary>
public static class SystemRuntimePrivileges
{
    private static readonly string[] Read = ["SELECT"];
    private static readonly string[] Append = ["SELECT", "INSERT"];
    private static readonly string[] Change = ["SELECT", "INSERT", "UPDATE"];
    private static readonly string[] None = [];

    public static readonly IReadOnlyList<SystemRuntimeGrant> Tables =
    [
        // the registry: read, plus the projected display names only (system migration 0006); provisioning state belongs to the administrative tool
        new("Company", SystemSchema.Company.Table, Read, [SystemSchema.Company.DisplayNameAr, SystemSchema.Company.DisplayNameEn, SystemSchema.Company.DisplayNameRevision]),
        new("User", SystemSchema.User.Table, Change),
        new("Session", SystemSchema.Session.Table, ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        new("LoginThrottle", SystemSchema.LoginThrottle.Table, ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        new("UserCompanyAccess", SystemSchema.UserCompanyAccess.Table, Change),
        new("Role", SystemSchema.Role.Table, Change),
        new("RolePermission", SystemSchema.RolePermission.Table, ["SELECT", "INSERT", "DELETE"]),
        new("RoleAssignment", SystemSchema.RoleAssignment.Table, Change),
        new("CostCenterAssignment", SystemSchema.CostCenterAssignment.Table, Append, [SystemSchema.CostCenterAssignment.RevokedAtUtc, SystemSchema.CostCenterAssignment.RevokedBy]),   // not documented before this slice: what AccessService does (assign, and end an assignment: only the two revocation columns change); never DELETE
        new("AccessAudit", SystemSchema.AccessAudit.Table, Append),
        new("AuditEvent", SystemSchema.AuditEvent.Table, Append),
        new("GlobalParty", SystemSchema.GlobalParty.Table, Change),
        new("PartyProposal", SystemSchema.PartyProposal.Table, Change),
        new("PartyLink", SystemSchema.PartyLink.Table, Append),
        new("UserTotp", SystemSchema.UserTotp.Table, Append,
            [SystemSchema.UserTotp.State, SystemSchema.UserTotp.ConfirmedAtUtc, SystemSchema.UserTotp.LastStep, SystemSchema.UserTotp.FailedConfirmations]),
        new("MfaChallenge", SystemSchema.MfaChallenge.Table, ["SELECT", "INSERT", "DELETE"], [SystemSchema.MfaChallenge.Attempts, SystemSchema.MfaChallenge.ConsumedAtUtc]),   // not documented before this slice: what MfaChallengeStore does (short-lived login challenges: count an attempt, consume, purge expired ones through the housekeeping)
        new("BackupLegalHold", SystemSchema.BackupLegalHold.Table, None),                      // administrative tool only (owner account)
        new("BackupRetentionPin", SystemSchema.BackupRetentionPin.Table, None),                // administrative tool only
        new("MigrationJournal", SystemSchema.MigrationJournalTable, None),                     // the API never reads the system journal
    ];

    public static SystemRuntimeGrant Totp => Tables.Single(t => t.Logical == "UserTotp");

    internal static readonly string[] AllTablePrivileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];

    private static string Q(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    /// <summary>
    /// Statements run inside the SYSTEM database by the object owner. They strip everything the runtime account and PUBLIC hold on the tables and sequences of the schema (a table REVOKE also removes
    /// the column privileges) and then grant exactly the catalog, so re-running converges whatever was granted before. Nothing here names the owner: its privileges are never touched.
    /// </summary>
    public static IReadOnlyList<string> SchemaStatements(string roleName)
    {
        var role = Q(roleName);
        var list = new List<string>
        {
            "REVOKE ALL ON SCHEMA public FROM PUBLIC",                                          // as the company twin: PUBLIC holds neither USAGE nor CREATE on the schema (a role that needs USAGE is granted it explicitly)
            "REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC",
            $"REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {role} CASCADE",                    // CASCADE: a grant the runtime account passed on WITH GRANT OPTION is removed with it, so the command converges
            "REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC",
            $"REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM {role} CASCADE",
            $"REVOKE ALL ON SCHEMA public FROM {role} CASCADE",
            $"GRANT USAGE ON SCHEMA public TO {role}",
        };
        foreach (var t in Tables)
        {
            if (t.Privileges.Length > 0) list.Add($"GRANT {string.Join(", ", t.Privileges)} ON TABLE {Q(t.Table)} TO {role}");
            if (t.UpdateColumns is { Length: > 0 }) list.Add($"GRANT UPDATE ({string.Join(", ", t.UpdateColumns.Select(Q))}) ON TABLE {Q(t.Table)} TO {role}");
        }
        return list;
    }

    // Refused by the gate. The runtime account: no superuser or BYPASSRLS, no pg_read_all_data or pg_write_all_data, no role membership at all, no CREATEDB, CREATEROLE or REPLICATION, no ownership of the database or of an object, no CREATE on the schema. The secret store: not owned by the runtime account (or a role it belongs to),
    // nothing beyond the catalog for it (table or column level), no other grantee at all (PUBLIC included; this is what "reporting roles" means: every role other than the owner and the runtime
    // account), no other role that can act as the owner or the runtime account through membership, and no other role that reads or writes every table through pg_read_all_data or pg_write_all_data.
    private const string ViolationSql = """
        WITH t AS (SELECT c.oid, c.relowner FROM pg_class c WHERE c.oid = to_regclass(@totp)),
             rt AS (SELECT oid FROM pg_roles WHERE rolname = @r::name)
        SELECT code FROM (
          SELECT 'runtime_account_unknown' AS code WHERE NOT EXISTS (SELECT 1 FROM rt)
          UNION ALL SELECT 'runtime_superuser' FROM pg_roles WHERE rolname = @r::name AND (rolsuper OR rolbypassrls)
          UNION ALL SELECT 'runtime_reads_all_data' FROM rt WHERE pg_has_role(rt.oid, @reader::name, 'MEMBER') OR pg_has_role(rt.oid, @writer::name, 'MEMBER')
          UNION ALL SELECT 'runtime_role_membership' FROM rt WHERE EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = rt.oid)          -- as the company twin: ANY membership (it can SET ROLE to, or inherit from, a role it should not be)
          UNION ALL SELECT 'runtime_role_attributes' FROM pg_roles WHERE rolname = @r::name AND (rolcreatedb OR rolcreaterole OR rolreplication)
          UNION ALL SELECT 'runtime_owns_objects' FROM rt WHERE EXISTS (SELECT 1 FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relowner = rt.oid)
          UNION ALL SELECT 'runtime_owns_database' FROM rt WHERE EXISTS (SELECT 1 FROM pg_database d WHERE d.datname = current_database() AND d.datdba = rt.oid)
          UNION ALL SELECT 'runtime_schema_create' FROM rt WHERE has_schema_privilege(rt.oid, 'public', 'CREATE')
          UNION ALL SELECT 'totp_owned_by_runtime' FROM t, rt WHERE pg_has_role(rt.oid, t.relowner, 'MEMBER')
          UNION ALL SELECT 'totp_runtime_overgrant' FROM t, rt WHERE EXISTS (SELECT 1 FROM unnest(@forbidden::text[]) p WHERE has_table_privilege(rt.oid, t.oid, p))
              OR EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped AND a.attname <> ALL (@update_columns::name[]) AND has_column_privilege(rt.oid, t.oid, a.attnum, 'UPDATE'))
              OR EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped AND has_column_privilege(rt.oid, t.oid, a.attnum, 'REFERENCES'))
              OR EXISTS (SELECT 1 FROM unnest(@all::text[]) p WHERE has_table_privilege(rt.oid, t.oid, p || ' WITH GRANT OPTION'))                       -- a grantable privilege lets the runtime account pass the store on to another role
              OR EXISTS (SELECT 1 FROM pg_attribute a, unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'REFERENCES']) p WHERE a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped AND has_column_privilege(rt.oid, t.oid, a.attnum, p || ' WITH GRANT OPTION'))
          UNION ALL SELECT 'totp_other_grantee' FROM t JOIN pg_class c ON c.oid = t.oid, aclexplode(c.relacl) x
              WHERE x.grantee <> t.relowner AND x.grantee IS DISTINCT FROM (SELECT oid FROM rt)
          UNION ALL SELECT 'totp_other_grantee' FROM t JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped, aclexplode(a.attacl) x
              WHERE x.grantee <> t.relowner AND x.grantee IS DISTINCT FROM (SELECT oid FROM rt)
          UNION ALL SELECT 'totp_reader_all_data' FROM t, pg_roles m
              WHERE NOT m.rolsuper AND m.rolname NOT LIKE 'pg\_%' AND m.oid <> t.relowner AND m.oid IS DISTINCT FROM (SELECT oid FROM rt)
                AND m.rolname <> @reader::name AND (pg_has_role(m.oid, @reader::name, 'USAGE') OR pg_has_role(m.oid, @reader::name, 'SET'))
          UNION ALL SELECT 'totp_writer_all_data' FROM t, pg_roles m
              WHERE NOT m.rolsuper AND m.rolname NOT LIKE 'pg\_%' AND m.oid <> t.relowner AND m.oid IS DISTINCT FROM (SELECT oid FROM rt)
                AND m.rolname <> @writer::name AND (pg_has_role(m.oid, @writer::name, 'USAGE') OR pg_has_role(m.oid, @writer::name, 'SET'))
          UNION ALL SELECT 'totp_reader_by_membership' FROM t, pg_roles m
              WHERE NOT m.rolsuper AND m.rolname NOT LIKE 'pg\_%' AND m.oid <> t.relowner AND m.oid IS DISTINCT FROM (SELECT oid FROM rt)
                AND (pg_has_role(m.oid, t.relowner, 'USAGE') OR pg_has_role(m.oid, t.relowner, 'SET')
                     OR EXISTS (SELECT 1 FROM rt WHERE pg_has_role(m.oid, rt.oid, 'USAGE') OR pg_has_role(m.oid, rt.oid, 'SET')))
        ) v
        """;

    // Reported, never refused: more than the catalog on another system table and access to a table the catalog does not know.
    private const string NoteSql = """
        WITH rt AS (SELECT oid FROM pg_roles WHERE rolname = @r::name)
        SELECT code FROM (
          SELECT 'overgrant:' || g.logical AS code FROM rt, unnest(@names::text[], @logical::text[], @allowed::text[], @columns::text[]) AS g(name, logical, allowed, columns)
              WHERE to_regclass(g.name) IS NOT NULL AND g.name <> @totp
                AND (EXISTS (SELECT 1 FROM unnest(@all::text[]) p WHERE p <> ALL (string_to_array(g.allowed, ',')) AND has_table_privilege(rt.oid, to_regclass(g.name), p))
                     OR EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = to_regclass(g.name) AND a.attnum > 0 AND NOT a.attisdropped
                                AND a.attname::text <> ALL (string_to_array(g.columns, ',')) AND 'UPDATE' <> ALL (string_to_array(g.allowed, ','))
                                AND has_column_privilege(rt.oid, a.attrelid, a.attnum, 'UPDATE')))
          UNION ALL SELECT 'uncatalogued_table_access' FROM rt, pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm')
                AND c.relname::text <> ALL (@names::text[]) AND has_table_privilege(rt.oid, c.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
        ) v
        """;

    /// <summary>
    /// Reports how <paramref name="roleName"/> and the TOTP secret store deviate from least privilege in the connected SYSTEM database (codes only, logical names, never a physical name).
    /// Run by the grant command with the owner account and by the API gate with the runtime account itself (the catalogs it reads are readable by every role).
    /// </summary>
    public static Task<SystemRuntimeFindings> FindAsync(NpgsqlConnection conn, string roleName, CancellationToken ct = default) => FindAsync(conn, roleName, PredefinedReader, PredefinedWriter, ct);

    internal const string PredefinedReader = "pg_read_all_data", PredefinedWriter = "pg_write_all_data";

    /// <summary>
    /// As <see cref="FindAsync(NpgsqlConnection, string, CancellationToken)"/> with the names of the two "reads/writes every table" roles given. Production always uses the predefined roles
    /// <c>pg_read_all_data</c> and <c>pg_write_all_data</c>; a test whose role administrator cannot grant a predefined role passes stand-in roles to exercise the same decision at database level.
    /// </summary>
    public static async Task<SystemRuntimeFindings> FindAsync(NpgsqlConnection conn, string roleName, string readAllDataRole, string writeAllDataRole, CancellationToken ct = default)
    {
        var totp = Totp;
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(ViolationSql, conn))
        {
            cmd.Parameters.AddWithValue("r", roleName);
            cmd.Parameters.AddWithValue("totp", totp.Table);
            cmd.Parameters.AddWithValue("forbidden", AllTablePrivileges.Except(totp.Privileges).ToArray());
            cmd.Parameters.AddWithValue("update_columns", totp.UpdateColumns!);
            cmd.Parameters.AddWithValue("all", AllTablePrivileges);
            cmd.Parameters.AddWithValue("reader", readAllDataRole);
            cmd.Parameters.AddWithValue("writer", writeAllDataRole);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) violations.Add(r.GetString(0));
        }
        var notes = new SortedSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(NoteSql, conn))
        {
            cmd.Parameters.AddWithValue("r", roleName);
            cmd.Parameters.AddWithValue("totp", totp.Table);
            cmd.Parameters.AddWithValue("names", Tables.Select(t => t.Table).ToArray());
            cmd.Parameters.AddWithValue("logical", Tables.Select(t => t.Logical).ToArray());
            cmd.Parameters.AddWithValue("allowed", Tables.Select(t => string.Join(",", t.Privileges)).ToArray());
            cmd.Parameters.AddWithValue("columns", Tables.Select(t => string.Join(",", t.UpdateColumns ?? [])).ToArray());
            cmd.Parameters.AddWithValue("all", AllTablePrivileges);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) notes.Add(r.GetString(0));
        }
        return new SystemRuntimeFindings([.. violations], [.. notes]);
    }

    /// <summary>True when the role holds every privilege of the catalog (what the API needs to work at all).</summary>
    public static async Task<bool> HasRequiredAccessAsync(NpgsqlConnection conn, string roleName, CancellationToken ct = default)
    {
        foreach (var t in Tables)
        {
            foreach (var p in t.Privileges)
            {
                await using var cmd = new NpgsqlCommand("SELECT has_table_privilege(@r::name, to_regclass(@t), @p)", conn);
                cmd.Parameters.AddWithValue("r", roleName); cmd.Parameters.AddWithValue("t", t.Table); cmd.Parameters.AddWithValue("p", p);
                if (await cmd.ExecuteScalarAsync(ct) is not true) return false;
            }
            foreach (var c in t.UpdateColumns ?? [])
            {
                await using var cmd = new NpgsqlCommand("SELECT has_column_privilege(@r::name, to_regclass(@t), @c, 'UPDATE')", conn);
                cmd.Parameters.AddWithValue("r", roleName); cmd.Parameters.AddWithValue("t", t.Table); cmd.Parameters.AddWithValue("c", c);
                if (await cmd.ExecuteScalarAsync(ct) is not true) return false;
            }
        }
        return true;
    }
}

public sealed class SystemRuntimeGrantException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record SystemRuntimeGrantResult(SystemRuntimeFindings Findings);

/// <summary>
/// <c>Hasm.Admin grant-system-runtime</c>: converges the runtime account's privileges on the SYSTEM database to <see cref="SystemRuntimePrivileges.Tables"/>, in one transaction under the owner
/// account. Refuses (changing nothing) when the runtime account is the administrative account, a superuser or an owner of system objects (the owner's privileges are never touched), when the
/// account does not exist (this command never creates a role or sets a password), and when the connected database is not a migrated system database (never a company database).
/// Safe to re-run. NOT audited in this slice (the same recorded limitation as <c>grant-company-runtime</c>).
/// </summary>
public static class SystemRuntimeAdministration
{
    public static async Task<SystemRuntimeGrantResult> GrantAsync(Configuration.HasmDatabaseOptions options, CancellationToken ct = default)
    {
        options.EnsureValid(requireAdmin: true);
        var role = options.Username!;
        if (string.Equals(role, options.AdminUsername, StringComparison.Ordinal))
            throw new SystemRuntimeGrantException("runtime_account_is_admin_account", "The runtime account is the administrative account; configure a separate runtime account. Nothing was changed.");
        await using var conn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var check = new NpgsqlCommand("""
            SELECT CASE
              WHEN to_regclass(@company_identity) IS NOT NULL OR to_regclass(@registry) IS NULL OR to_regclass(@journal) IS NULL THEN 'not_a_system_database'
              WHEN EXISTS (SELECT 1 FROM unnest(@tables::text[]) n WHERE to_regclass(n) IS NULL) THEN 'system_database_not_migrated'
              WHEN NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @r::name) THEN 'runtime_account_unknown'
              WHEN (SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = @r::name) THEN 'runtime_account_is_superuser'
              WHEN EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = @r::name) THEN 'runtime_account_has_role_membership'
              WHEN EXISTS (SELECT 1 FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND pg_has_role(@r::name, c.relowner, 'MEMBER')) THEN 'runtime_account_is_owner'
              WHEN EXISTS (SELECT 1 FROM unnest(@tables::text[]) n JOIN pg_class c ON c.oid = to_regclass(n) WHERE NOT pg_has_role(current_user, c.relowner, 'USAGE')) THEN 'not_the_owner_account'
            END
            """, conn, tx))
        {
            check.Parameters.AddWithValue("company_identity", CompanySchema.Identity.Table);
            check.Parameters.AddWithValue("registry", SystemSchema.Company.Table);
            check.Parameters.AddWithValue("journal", SystemSchema.MigrationJournalTable);
            check.Parameters.AddWithValue("tables", SystemRuntimePrivileges.Tables.Select(t => t.Table).ToArray());
            check.Parameters.AddWithValue("r", role);
            if (await check.ExecuteScalarAsync(ct) is string code)
                throw new SystemRuntimeGrantException(code, "The runtime grants were not applied; nothing was changed.");
        }
        foreach (var sql in SystemRuntimePrivileges.SchemaStatements(role))
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (!await SystemRuntimePrivileges.HasRequiredAccessAsync(conn, role, ct))
            throw new SystemRuntimeGrantException("grants_not_effective", "The runtime grants did not take effect; nothing was changed.");
        var findings = await SystemRuntimePrivileges.FindAsync(conn, role, ct);
        await tx.CommitAsync(ct);
        return new SystemRuntimeGrantResult(findings);
    }
}
