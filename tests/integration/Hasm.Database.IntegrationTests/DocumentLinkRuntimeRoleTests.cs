using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using DL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentLink;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The real per-company PostgreSQL role (role-capable disposable cluster only): the document-link table must be usable through the RUNTIME ROLE itself, not only through the owner connection the service tests use.
/// Company migration 0030 created the table but the runtime grants did not list it, so the role got 42501 on every link read and write (found after #108 merged). Links are append-only: SELECT and INSERT, never
/// UPDATE, DELETE or TRUNCATE.
/// </summary>
public class DocumentLinkRuntimeRoleTests : CompanyRuntimeTestBase
{
    protected override bool RolesRequired => true;

    private CompanyRuntimeProvisioner Provisioner() => new(Configured(useRoleAdmin: true), Registry);

    private async Task<NpgsqlConnection> AsRuntime(CompanyRecord c)
    {
        var credential = (await new FileCompanyCredentialStore(CredentialDirectory).GetAsync(c.Id))!;
        var cs = new NpgsqlConnectionStringBuilder(Configured().BuildCompanyRuntimeConnectionString(c.PhysicalDatabaseName, credential.Username, credential.Password)) { Pooling = false };
        var conn = new NpgsqlConnection(cs.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<string?> Try(NpgsqlConnection conn, string sql)
    {
        try { await using var cmd = new NpgsqlCommand(sql, conn); await cmd.ExecuteNonQueryAsync(); return null; }
        catch (PostgresException ex) { return ex.SqlState; }
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_can_read_the_links_and_the_derived_quantities_and_insert_a_link_but_never_update_delete_or_truncate_it()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await Provisioner().GrantAsync(c.Id);
        await using var rt = await AsRuntime(c);
        var role = CompanyRuntimeRole.NameFor(c.PhysicalDatabaseName);
        Assert.Empty(await CompanyRuntimePrivileges.FindViolationsAsync(rt, role));
        Assert.True(await CompanyRuntimePrivileges.HasRequiredAccessAsync(rt, role));

        // the statements of GET links and GET line-quantities, as the runtime role runs them: never 42501
        Assert.Null(await Try(rt, $"SELECT k.{DL.Id}, k.{DL.FromDocumentId}, fl.{CL.LineNo}, k.{DL.ToDocumentId}, tl.{CL.LineNo}, k.{DL.Kind}, k.{DL.Quantity}, k.{DL.CreatedBy}, k.{DL.CreatedAtUtc} " +
            $"FROM {DL.Table} k JOIN {CL.Table} fl ON fl.{CL.Id} = k.{DL.FromLineId} JOIN {CL.Table} tl ON tl.{CL.Id} = k.{DL.ToLineId} WHERE k.{DL.FromDocumentId} = gen_random_uuid() OR k.{DL.ToDocumentId} = gen_random_uuid()"));
        Assert.Null(await Try(rt, $"SELECT l.{CL.LineNo}, l.{CL.Quantity}, COALESCE(sum(k.{DL.Quantity}) FILTER (WHERE k.{DL.Kind} = 1), 0) FROM {CL.Table} l LEFT JOIN {DL.Table} k ON k.{DL.FromLineId} = l.{CL.Id} " +
            $"WHERE l.{CL.DocumentId} = gen_random_uuid() GROUP BY l.{CL.Id}, l.{CL.LineNo}, l.{CL.Quantity}"));
        Assert.Null(await Try(rt, $"SELECT EXISTS (SELECT 1 FROM {DL.Table} WHERE {DL.FromDocumentId} = gen_random_uuid() OR {DL.ToDocumentId} = gen_random_uuid())"));   // the document guard's existence check

        // the INSERT privilege: a link to documents that do not exist is refused by the database's OWN rules (the guard or a foreign key), never by a missing grant
        var refused = await Try(rt, $"INSERT INTO {DL.Table} ({DL.Id}, {DL.FromDocumentId}, {DL.FromLineId}, {DL.ToDocumentId}, {DL.ToLineId}, {DL.Kind}, {DL.Quantity}, {DL.IdempotencyKey}, {DL.PayloadHash}, {DL.CreatedBy}, {DL.CreatedAtUtc}) " +
            $"VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, 1, 'runtime-key-0001', '{new string('a', 64)}', gen_random_uuid(), now())");
        Assert.NotNull(refused);
        Assert.NotEqual("42501", refused);

        // append-only: no change, no removal, no truncation through the role either
        Assert.Equal("42501", await Try(rt, $"UPDATE {DL.Table} SET {DL.Quantity} = 1"));
        Assert.Equal("42501", await Try(rt, $"DELETE FROM {DL.Table}"));
        Assert.Equal("42501", await Try(rt, $"TRUNCATE {DL.Table}"));
    }
}
