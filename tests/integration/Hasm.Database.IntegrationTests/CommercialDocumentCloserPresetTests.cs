using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using Ra = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using Ro = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using Rp = Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission;
using SC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentShortClose;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// X4-PRESET-ROLE (owner Option A, D-254; plan docs/X4-PRESET-ROLES-PLAN.md sections 3.3, 3.5 and 7): the dedicated preset Commercial Document Closer (the short close of D-201) against real PostgreSQL. It is assigned
/// offline only, with an audit event; a company administrator can neither assign nor remove it nor build a role with its code; it is NOT privileged (no mandatory MFA comes from it); no existing user gains anything.
/// </summary>
public class CommercialDocumentCloserPresetTests : CommercialTestBase
{
    [PostgresFact]
    public async Task The_preset_is_delegated_offline_only_never_by_a_company_administrator_and_every_grant_and_revoke_is_audited()
    {
        await Prepare();
        var member = await NewUser("future-closer");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, member.Login)).IsOk);
        // nobody holds the close code before a grant: not the administrator, not the commercial approver
        Assert.DoesNotContain(PermissionCatalog.Codes.CommercialClose, (await Rbac.GetEffectivePermissionsAsync(Admin.Id, Company.Id)).HeldPermissionCodes);
        Assert.DoesNotContain(PermissionCatalog.Codes.CommercialClose, (await Rbac.GetEffectivePermissionsAsync(await NewApprover(), Company.Id)).HeldPermissionCodes);
        var viaService = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, member.Id, PresetRoles.CommercialDocumentCloserId, ScopeKind.Company, null);
        Assert.Equal((AccessStatus.Forbidden, "cannot_grant_permission"), (viaService.Status, viaService.Code));                                    // an administrator cannot grant what they do not hold
        Assert.False((await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "sneaky-closer", [PermissionCatalog.Codes.CommercialClose])).IsOk);

        var audit = SystemSchema.AccessAudit.Table;
        Task<long> Count(string detail) => Scalar<long>($"SELECT count(*) FROM {audit} WHERE {SystemSchema.AccessAudit.Detail} = '{detail}' AND {SystemSchema.AccessAudit.ActorUserId} IS NULL AND {SystemSchema.AccessAudit.TargetUserId} = @u", ("u", member.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantCommercialDocumentCloserOfflineAsync(member.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.AlreadyApprover, await Rbac.GrantCommercialDocumentCloserOfflineAsync(member.Login.ToUpperInvariant(), Company.Id));
        Assert.Equal(1L, await Count("scope: company; offline commercial document closer delegation"));
        // the holder has exactly the two codes of the preset, company-wide
        Assert.Equal(["company.commercialdocs.close", "company.commercialdocs.view"], (await Rbac.GetEffectivePermissionsAsync(member.Id, Company.Id)).HeldPermissionCodes.Order(StringComparer.Ordinal));
        var assignment = (await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, member.Id)).Value!.Single(a => a.RoleId == PresetRoles.CommercialDocumentCloserId);
        Assert.False((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Id)).IsOk);                                                  // nor can an administrator remove it

        // NOT privileged: holding it does not make MFA mandatory through the privileged-role rule (control: the commercial approver is privileged)
        Assert.False((await Store.GetMfaRequirementAsync(member.Id)).Reasons.HasFlag(MfaMandatoryReason.PrivilegedRole));
        Assert.True((await Store.GetMfaRequirementAsync((await NewApproverUser()).Id)).Reasons.HasFlag(MfaMandatoryReason.PrivilegedRole));

        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeCommercialDocumentCloserOfflineAsync(member.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.NotApprover, await Rbac.RevokeCommercialDocumentCloserOfflineAsync(member.Login, Company.Id));
        Assert.Equal(1L, await Count("scope: company; offline commercial document closer removal"));
        Assert.Empty((await Rbac.GetEffectivePermissionsAsync(member.Id, Company.Id)).HeldPermissionCodes);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId} = @u AND {Ra.RoleId} = @r AND {Ra.RevokedAtUtc} IS NOT NULL", ("u", member.Id), ("r", PresetRoles.CommercialDocumentCloserId)));   // revoked, never deleted

        // the delegation needs an active member of a Ready company and never creates membership
        var stranger = await NewUser("closer-stranger");
        Assert.Equal(AccessService.FiscalApproverStatus.NotMember, await Rbac.GrantCommercialDocumentCloserOfflineAsync(stranger.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.UserNotFound, await Rbac.GrantCommercialDocumentCloserOfflineAsync("nobody-here", Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.CompanyUnavailable, await Rbac.GrantCommercialDocumentCloserOfflineAsync(member.Login, Guid.NewGuid()));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId} = @u", ("u", stranger.Id)));
    }
}

/// <summary>The Hasm.Admin commands for the preset (real tool, real file-based audit keys shared with the tool): grant, repeat, revoke, refusals, usage, and the audit events.</summary>
public class CommercialDocumentCloserToolTests : FileEvidenceTestBase
{
    [PostgresFact]
    public async Task The_Admin_commands_grant_and_revoke_the_closer_with_an_audit_event_and_print_no_secret()
    {
        var (company, admin, _, service) = await Written();
        await using var _s = service;
        var env = new Dictionary<string, string?> { ["Hasm__Audit__KeyDirectory"] = KeyDir, ["Hasm__Audit__ActiveKeyId"] = "k1", ["Hasm__Audit__CheckpointDirectory"] = CpDir };
        var member = await NewPerson("cli-closer");
        Assert.True((await service.GrantMembershipAsync(admin.Id, company, member.Login)).IsOk);
        async Task<List<Guid>> Held() => (await service.ListAssignmentsAsync(admin.Id, company, member.Id)).Value!.Select(a => a.RoleId).ToList();
        var grant = await AdminTool.RunWith(Options, env, "grant-commercial-document-closer", member.Login, company.ToString());
        Assert.True(grant.Exit == 0, grant.All);
        Assert.Contains("The Commercial Document Closer role was granted (company-wide).", grant.Out);
        Assert.Contains(PresetRoles.CommercialDocumentCloserId, await Held());
        var again = await AdminTool.RunWith(Options, env, "grant-commercial-document-closer", member.Login, company.ToString());
        Assert.Equal(0, again.Exit);
        Assert.Contains("already", again.Out);
        var revoke = await AdminTool.RunWith(Options, env, "revoke-commercial-document-closer", member.Login, company.ToString());
        Assert.True(revoke.Exit == 0, revoke.All);
        Assert.Contains("The Commercial Document Closer role was removed.", revoke.Out);
        Assert.DoesNotContain(PresetRoles.CommercialDocumentCloserId, await Held());
        var missing = await AdminTool.RunWith(Options, env, "grant-commercial-document-closer", "nobody-here", company.ToString());
        Assert.Equal(1, missing.Exit);
        Assert.Equal(2, (await AdminTool.RunWith(Options, env, "grant-commercial-document-closer", member.Login)).Exit);                             // usage
        Assert.Equal(2, (await AdminTool.RunWith(Options, env, "revoke-commercial-document-closer", member.Login, "not-a-guid")).Exit);
        foreach (var text in new[] { grant.All, again.All, revoke.All, missing.All }) { Assert.DoesNotContain(Options.AdminPassword!, text); Assert.DoesNotContain(Options.SystemDatabaseName!, text); }
        var audit = SystemSchema.AccessAudit.Table;
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {audit} WHERE {SystemSchema.AccessAudit.Detail} = 'scope: company; offline commercial document closer delegation' AND {SystemSchema.AccessAudit.ActorUserId} IS NULL"));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {audit} WHERE {SystemSchema.AccessAudit.Detail} = 'scope: company; offline commercial document closer removal' AND {SystemSchema.AccessAudit.ActorUserId} IS NULL"));
    }
}

/// <summary>
/// System migration 0041 itself, on a system database that already holds a company, an administrator, a member and an offline approver: it is an UPGRADE of an existing installation. It creates exactly the one preset
/// with exactly its two permissions, leaves both immutability triggers enabled, and changes no pre-existing preset, role assignment or membership (nobody gains a permission).
/// </summary>
public class CommercialDocumentCloserMigrationTests : PostgresTestBase
{
    private static readonly IReadOnlyList<MigrationScript> All = MigrationScript.LoadEmbedded();
    private static readonly MigrationScript Preset = All.Single(s => s.Name == "0041_commercial_document_closer.sql");

    private async Task<List<string>> Rows(string table)
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT x::text FROM {table} x ORDER BY 1", conn);
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }

    private async Task<string[]> Permissions(Guid role)
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {Rp.Permission} FROM {Rp.Table} WHERE {Rp.RoleId} = @r ORDER BY 1", conn);
        cmd.Parameters.AddWithValue("r", role);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return [.. list.Order(StringComparer.Ordinal)];
    }

    [PostgresFact]
    public async Task The_migration_creates_exactly_the_closer_preset_and_changes_no_existing_preset_assignment_or_membership_and_leaves_every_trigger_enabled()
    {
        // an installation at the schema BEFORE the migration (every earlier system migration), with data written through the real services
        await using (var maintenance = new NpgsqlConnection(Options.BuildMaintenanceConnectionString()))
        {
            await maintenance.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(Options.SystemDatabaseName!)}", maintenance);
            await create.ExecuteNonQueryAsync();
        }
        var connection = Options.BuildSystemConnectionString(admin: true);
        var before = All.Where(s => s.Version < Preset.Version).ToList();
        Assert.Equal(Preset.Version - 1, before.Count);
        Assert.Equal(before.Count, (await new MigrationRunner(connection).ApplyAsync(before)).Applied.Count);

        var clock = new FakeClock();
        var audit = new Hasm.Infrastructure.Audit.AuditTrail(new Hasm.Infrastructure.Audit.AuditServices(new Hasm.Infrastructure.Audit.InMemoryAuditKeyRing(), new Hasm.Infrastructure.Audit.InMemoryCheckpointStore(), 0), clock);
        var store = new IdentityStore(connection, clock, audit, keys: TestIdentityKeys.Instance);
        var auth = new AuthService(store, new PasswordService(1000), new AuthOptions());
        var company = Guid.NewGuid();
        await using (var conn0 = new NpgsqlConnection(connection))
        {
            await conn0.OpenAsync();
            await using var ins = new NpgsqlCommand($"INSERT INTO {Co.Table} ({Co.Id},{Co.PhysicalDatabaseName},{Co.ProvisioningState},{Co.CreatedAtUtc},{Co.StateChangedAtUtc}) VALUES (@i,@n,@s,now(),now())", conn0);
            ins.Parameters.AddWithValue("i", company); ins.Parameters.AddWithValue("n", Hasm.Infrastructure.Naming.PhysicalIdentifier.GenerateDatabaseName()); ins.Parameters.AddWithValue("s", (short)ProvisioningState.Ready);
            await ins.ExecuteNonQueryAsync();
        }
        await using var access = new AccessService(connection, clock, PermissionCatalog.Default, audit, keys: TestIdentityKeys.Instance);
        async Task<UserRecord> User(string login)
        {
            var created = await auth.CreateUserAsync(login, "Display " + login, "Correct-Horse-Battery-9", false);
            Assert.Equal(CreateUserStatus.Created, created.Status);
            return created.User!;
        }
        var admin = await User("upgrade-admin");
        Assert.Equal(AccessService.BootstrapStatus.Granted, await access.BootstrapCompanyAdministratorAsync(admin.Login, company));
        var approver = await User("upgrade-approver");
        Assert.True((await access.GrantMembershipAsync(admin.Id, company, approver.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await access.GrantCommercialApproverOfflineAsync(approver.Login, company));
        var custom = await access.CreateRoleAsync(admin.Id, company, "clerk", [PermissionCatalog.Codes.CommercialView, PermissionCatalog.Codes.CommercialUpdate, PermissionCatalog.Codes.CommercialCancel]);
        Assert.True(custom.IsOk, custom.Code);

        var rolesBefore = await Rows(Ro.Table);
        var permissionsBefore = await Rows(Rp.Table);
        var assignmentsBefore = await Rows(Ra.Table);
        var membershipsBefore = await Rows(SystemSchema.UserCompanyAccess.Table);
        Assert.Equal(11, rolesBefore.Count);                                                                                                          // the 10 presets and the custom role
        Assert.NotEmpty(assignmentsBefore);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Rp.Table} WHERE {Rp.Permission} = 'company.commercialdocs.close'"));

        // the upgrade: the real initializer applies exactly the pending migrations (this one, and any later system migration)
        var result = await new SystemDatabaseInitializer(Options).InitializeAsync();
        Assert.Equal(All.Where(s => s.Version >= Preset.Version).Select(s => s.Version).ToList(), result.AppliedMigrations);
        Assert.Equal(Preset.Checksum, await Scalar<string>($"SELECT checksum FROM {SystemSchema.MigrationJournalTable} WHERE version = {Preset.Version}"));

        // every pre-existing role, permission row, assignment and membership is byte-identical: rows were only ADDED (1 role, 2 permission rows)
        var rolesAfter = await Rows(Ro.Table);
        var permissionsAfter = await Rows(Rp.Table);
        Assert.Equal(12, rolesAfter.Count);
        Assert.Empty(rolesBefore.Except(rolesAfter));
        Assert.Empty(permissionsBefore.Except(permissionsAfter));
        Assert.Equal(permissionsBefore.Count + 2, permissionsAfter.Count);
        Assert.Equal(assignmentsBefore, await Rows(Ra.Table));
        Assert.Equal(membershipsBefore, await Rows(SystemSchema.UserCompanyAccess.Table));

        // exactly the preset, with exactly its permissions, active, equal to the code definition
        var id = PresetRoles.CommercialDocumentCloserId;
        Assert.Equal("commercial-document-closer", await Scalar<string>($"SELECT {Ro.PresetKey} FROM {Ro.Table} WHERE {Ro.Id} = @i", ("i", id)));
        Assert.True(await Scalar<bool>($"SELECT {Ro.IsActive} FROM {Ro.Table} WHERE {Ro.Id} = @i", ("i", id)));
        Assert.Equal(PresetRoles.Definitions[id].Permissions.Order(StringComparer.Ordinal), await Permissions(id));
        // the close code went to the new preset ONLY: no existing role (Company Administrator, the approver, the custom role) gained it, and nobody was assigned the preset
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Rp.Table} WHERE {Rp.Permission} = 'company.commercialdocs.close'"));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.RoleId} = @r", ("r", id)));
        foreach (var user in new[] { admin, approver })
            Assert.DoesNotContain(PermissionCatalog.Codes.CommercialClose, (await access.GetEffectivePermissionsAsync(user.Id, company)).HeldPermissionCodes);
        foreach (var (preset, (_, permissions)) in PresetRoles.Definitions)
            Assert.Equal(permissions.Order(StringComparer.Ordinal), await Permissions(preset));                                                      // every preset equals its code definition after the upgrade

        // both immutability triggers are enabled again and no trigger is switched off anywhere; the preset is immutable again
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgenabled <> 'O'"));
        foreach (var (table, trigger) in new[] { (Ro.Table, Ro.GuardTrigger), (Rp.Table, Rp.GuardTrigger) })
            Assert.Equal("O", await Scalar<string>($"SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = '{table}'::regclass AND t.tgname = '{trigger}'"));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Rp.Table} ({Rp.RoleId},{Rp.Permission}) VALUES (@r,'company.commercialdocs.approve')", ("r", id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {Rp.Table} WHERE {Rp.RoleId} = @r", ("r", id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Ro.Table} SET {Ro.IsActive} = false WHERE {Ro.Id} = @r", ("r", id)));
    }
}

/// <summary>
/// The real per-company PostgreSQL role (role-capable disposable cluster only): the short-close table of company migration 0039 is usable through the RUNTIME ROLE itself: SELECT and INSERT, never UPDATE, DELETE or
/// TRUNCATE (append-only), and the statements of the service run without a missing grant.
/// </summary>
public class ShortCloseRuntimeRoleTests : CompanyRuntimeTestBase
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
    public async Task The_runtime_role_can_read_and_insert_a_short_close_but_never_update_delete_or_truncate_it()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await Provisioner().GrantAsync(c.Id);
        await using var rt = await AsRuntime(c);
        var role = CompanyRuntimeRole.NameFor(c.PhysicalDatabaseName);
        Assert.Empty(await CompanyRuntimePrivileges.FindViolationsAsync(rt, role));
        Assert.True(await CompanyRuntimePrivileges.HasRequiredAccessAsync(rt, role));

        // the reads of the service (the operations of a document, the closed sum of a line, the existence check of the document guard), as the runtime role runs them: never 42501
        Assert.Null(await Try(rt, $"SELECT s.{SC.OperationId}, s.{SC.DocumentId}, s.{SC.Reason}, s.{SC.CreatedBy}, s.{SC.CreatedAtUtc}, l.{CL.LineNo}, s.{SC.Quantity} FROM {SC.Table} s JOIN {CL.Table} l ON l.{CL.Id} = s.{SC.LineId} WHERE s.{SC.DocumentId} = gen_random_uuid()"));
        Assert.Null(await Try(rt, $"SELECT COALESCE(sum({SC.Quantity}), 0) FROM {SC.Table} WHERE {SC.LineId} = gen_random_uuid()"));
        Assert.Null(await Try(rt, $"SELECT EXISTS (SELECT 1 FROM {SC.Table} WHERE {SC.DocumentId} = gen_random_uuid())"));
        Assert.Null(await Try(rt, $"SELECT {SC.OperationId}, {SC.PayloadHash} FROM {SC.Table} WHERE {SC.CreatedBy} = gen_random_uuid() AND {SC.IdempotencyKey} = 'runtime-key-0001' LIMIT 1"));

        // the INSERT privilege: a close of a document that does not exist is refused by the database's OWN rules (the guard or a foreign key), never by a missing grant
        var refused = await Try(rt, $"INSERT INTO {SC.Table} ({SC.Id}, {SC.OperationId}, {SC.DocumentId}, {SC.LineId}, {SC.Quantity}, {SC.Reason}, {SC.IdempotencyKey}, {SC.PayloadHash}, {SC.CreatedBy}, {SC.CreatedAtUtc}) " +
            $"VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, 'runtime', 'runtime-key-0001', '{new string('a', 64)}', gen_random_uuid(), now())");
        Assert.NotNull(refused);
        Assert.NotEqual("42501", refused);

        // append-only: no change, no removal, no truncation through the role either
        Assert.Equal("42501", await Try(rt, $"UPDATE {SC.Table} SET {SC.Quantity} = 1"));
        Assert.Equal("42501", await Try(rt, $"DELETE FROM {SC.Table}"));
        Assert.Equal("42501", await Try(rt, $"TRUNCATE {SC.Table}"));
    }
}
