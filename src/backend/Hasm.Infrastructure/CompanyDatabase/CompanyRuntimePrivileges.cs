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
    ];

    /// <summary>Privileges that must never reach a runtime role on the audit table or on the schema-state tables.</summary>
    internal static readonly string[] ForbiddenOnAudit = ["UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
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
        cmd.Parameters.AddWithValue("state_tables", new[] { CoIdentity.Table, SystemSchema.MigrationJournalTable });
        cmd.Parameters.AddWithValue("forbidden_state", ForbiddenOnState);
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) codes.Add(r.GetString(0));
        return [.. codes];
    }

    /// <summary>True when the role can read the schema state and read and append business audit events (what the API needs to work at all).</summary>
    public static async Task<bool> HasRequiredAccessAsync(NpgsqlConnection conn, string roleName, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT has_table_privilege(@r::name, to_regclass(@journal), 'SELECT') AND has_table_privilege(@r::name, to_regclass(@identity), 'SELECT') " +
            "AND has_table_privilege(@r::name, to_regclass(@audit), 'SELECT') AND has_table_privilege(@r::name, to_regclass(@audit), 'INSERT')", conn);
        cmd.Parameters.AddWithValue("r", roleName);
        cmd.Parameters.AddWithValue("journal", SystemSchema.MigrationJournalTable);
        cmd.Parameters.AddWithValue("identity", CoIdentity.Table);
        cmd.Parameters.AddWithValue("audit", BizAudit.Table);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
