using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using AP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicy;
using PI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicyIdentity;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The duplicate-policy refusal as the REAL per-company PostgreSQL role (role-capable disposable cluster only; company migration 0033, owner decision D-209): the identity triggers run as the writer, so the runtime role
/// needs SELECT, INSERT and UPDATE on the identity table (the guard resets whatever it writes) and never DELETE or TRUNCATE. A direct duplicate is refused at COMMIT and a service create answers 409, both through the role.
/// </summary>
public class ApprovalPolicyRuntimeRoleTests : CompanyRuntimeTestBase
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

    private static async Task<(string? State, string? Constraint)> Commit(NpgsqlConnection conn, params string[] statements)
    {
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
            return (null, null);
        }
        catch (PostgresException ex) { return (ex.SqlState, ex.ConstraintName); }
    }

    private static string Raw(Guid id, string min = "0") =>
        $"INSERT INTO {AP.Table} ({AP.Id}, {AP.DocType}, {AP.Source}, {AP.BranchId}, {AP.MinAmount}, {AP.RequiredApprovals}, {AP.NameEn}, {AP.IsActive}, {AP.Revision}, {AP.CreatedAtUtc}, {AP.UpdatedAtUtc}) " +
        $"VALUES ('{id}', 'journal_entry', 3, NULL, {min}, 1, 'raw', true, 1, now(), now())";

    [PostgresRoleFact]
    public async Task As_the_runtime_role_a_direct_duplicate_is_refused_at_commit_and_a_service_create_answers_409_while_the_identity_table_is_never_deleted_or_truncated()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await Provisioner().GrantAsync(c.Id);
        await using var rt = await AsRuntime(c);
        var role = CompanyRuntimeRole.NameFor(c.PhysicalDatabaseName);
        Assert.Empty(await CompanyRuntimePrivileges.FindViolationsAsync(rt, role));
        Assert.True(await CompanyRuntimePrivileges.HasRequiredAccessAsync(rt, role));

        // a direct statement through the role: the identity triggers write the identity as the writer, so the first policy commits and an identical one fails at COMMIT with the unique index
        Assert.Equal((null, null), await Commit(rt, Raw(Guid.NewGuid())));
        Assert.Equal(("23505", PI.UqActive), await Commit(rt, Raw(Guid.NewGuid())));
        Assert.Equal((null, null), await Commit(rt, Raw(Guid.NewGuid(), min: "5")));
        Assert.Equal(2L, await Scalar<long>(rt, $"SELECT count(*) FROM {AP.Table}"));
        Assert.Equal(2L, await Scalar<long>(rt, $"SELECT count(*) FROM {PI.Table}"));

        // the service through the real resolver: a duplicate is a 409, a distinct policy is created
        var resolver = RealResolver();
        var policies = new ApprovalPolicyService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), Clock);
        ApprovalPolicyInput In(decimal min) => new("journal_entry", "any", null, min, 1, "سياسة", "Policy");
        var duplicate = await policies.CreateAsync(Guid.NewGuid(), c.Id, In(0m));
        Assert.Equal((ConfigStatus.Conflict, ApprovalPolicyService.DuplicateCode), (duplicate.Status, duplicate.Code));
        var created = await policies.CreateAsync(Guid.NewGuid(), c.Id, In(9m));
        Assert.True(created.IsOk);
        Assert.Equal(3L, await Scalar<long>(rt, $"SELECT count(*) FROM {PI.Table}"));

        // the table is derived state: no removal and no truncation through the role (the guard would also refuse), and no catalog tampering
        Assert.Equal("42501", (await Commit(rt, $"DELETE FROM {PI.Table}")).State);
        Assert.Equal("42501", (await Commit(rt, $"TRUNCATE {PI.Table}")).State);
        Assert.Equal("42501", (await Commit(rt, $"ALTER TABLE {PI.Table} DISABLE TRIGGER ALL")).State);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
