using System.Net;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using Ra = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using Ro = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using Rp = Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// X4-PRESET-ROLE (owner Option A, 2026-11-22; plan docs/X4-PRESET-ROLES-PLAN.md section 3.2, review of PR 226): the two dedicated cash and bank account presets against real PostgreSQL and the real API. Each preset is
/// held ALONE by a user and does its job; both are privileged (mandatory MFA); they are assigned offline only, with an audit event; Company Administrator and every pre-existing preset are unchanged by the migration.
/// </summary>
public class CashPresetRolesApiTests : CashAccountTestBase
{
    private const string Number = "SA03 8000 0000 6080 1016 7519";
    private int holders2;

    /// <summary>A member who holds ONLY the preset, assigned the only way it can be: offline.</summary>
    private async Task<UserRecord> OnlyPreset(Func<string, Task<AccessService.FiscalApproverStatus>> grant)
    {
        var user = await NewUser($"presetholder{Interlocked.Increment(ref holders2)}");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, user.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await grant(user.Login));
        return user;
    }

    private Task<UserRecord> OnlyManager() => OnlyPreset(login => Rbac.GrantCashAccountManagerOfflineAsync(login, Company.Id));
    private Task<UserRecord> OnlyViewer() => OnlyPreset(login => Rbac.GrantCashAccountSensitiveViewerOfflineAsync(login, Company.Id));

    [PostgresFact]
    public async Task A_user_holding_only_Cash_Account_Manager_creates_updates_lists_and_deactivates_and_never_sees_a_full_number()
    {
        await Start();
        var main = await MainBranch(); var g = await Gl("1101");
        var cookie = await SignIn(await OnlyManager());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Base, cookie: cookie)).Status);                                                  // view
        var created = await Create(cookie, Bank("nb", main, g, Number));                                                                              // create (with a number)
        var id = IdOf(created);
        var body = new { kind = "bank", code = "nb", nameAr = "البنك", nameEn = "Renamed bank", branchId = main, accountId = g, bankName = "National Bank", bankBranch = (string?)null, accountNumber = (string?)null, iban = (string?)null };
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/{id}", body, cookie, ifMatch: "\"1\"")).Status);                      // update
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie, ifMatch: "\"2\"")).Status);        // activate / deactivate
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: cookie)).Status);                 // the full number needs the other preset
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/v1/company/warehouses", cookie: cookie)).Status);                  // nothing else: no other permission came with the preset
    }

    [PostgresFact]
    public async Task A_user_holding_only_Cash_Account_Sensitive_Viewer_reads_the_full_number_and_changes_nothing()
    {
        await Start();
        var main = await MainBranch(); var g = await Gl("1101");
        var id = IdOf(await Create(await Manager(), Bank("nb", main, g, Number)));
        var cookie = await SignIn(await OnlyViewer());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Base, cookie: cookie)).Status);                                                  // the view it needs
        var reveal = await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: cookie);                                                       // the full number, with view AND sensitive
        Assert.Equal(HttpStatusCode.OK, reveal.Status);
        Assert.Equal(Number.Replace(" ", ""), reveal.Json.GetProperty("accountNumber").GetString());
        Assert.Equal(1L, await Events(Hasm.Infrastructure.Cash.CashAuditEvents.AccountNumberRevealed));                                              // the reveal is audited
        var body = new { kind = "bank", code = "nb", nameAr = "البنك", nameEn = "Changed", branchId = main, accountId = g, bankName = "National Bank", bankBranch = (string?)null, accountNumber = (string?)null, iban = (string?)null };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base, Cashbox("c2", main, await Gl("1102")), cookie)).Status);          // no create
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"{Base}/{id}", body, cookie, ifMatch: "\"1\"")).Status);                // no update
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie, ifMatch: "\"1\"")).Status);  // no activate
    }

    [PostgresFact]
    public async Task Both_presets_are_privileged_so_assigning_either_makes_MFA_mandatory()
    {
        await Start();
        var manager = await OnlyManager(); var viewer = await OnlyViewer();
        foreach (var user in new[] { manager, viewer })
        {
            var requirement = await Store.GetMfaRequirementAsync(user.Id);
            Assert.True(requirement.Mandatory);
            Assert.True(requirement.Reasons.HasFlag(MfaMandatoryReason.PrivilegedRole));
        }
        // control: a holder of the ordinary view code only is not subject to the privileged-role rule
        var plain = await Holder([Hasm.Infrastructure.Access.PermissionCatalog.Codes.CashAccountsView]);
        Assert.False((await Store.GetMfaRequirementAsync(plain.Id)).Reasons.HasFlag(MfaMandatoryReason.PrivilegedRole));
    }

    [PostgresFact]
    public async Task The_presets_are_delegated_offline_only_never_by_a_company_administrator_and_every_grant_and_revoke_is_audited()
    {
        await Start();
        var member = await NewUser("future-cash-manager");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, member.Login)).IsOk);
        foreach (var preset in new[] { PresetRoles.CashAccountManagerId, PresetRoles.CashAccountSensitiveViewerId })
        {
            var viaService = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, member.Id, preset, ScopeKind.Company, null);
            Assert.Equal((AccessStatus.Forbidden, "cannot_grant_permission"), (viaService.Status, viaService.Code));                                // an administrator cannot grant what they do not hold
        }
        Assert.False((await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "sneaky-sensitive", [PermissionCatalog.Codes.CashAccountsSensitiveView])).IsOk);

        var audit = SystemSchema.AccessAudit.Table;
        Task<long> Count(string detail) => Scalar<long>($"SELECT count(*) FROM {audit} WHERE {SystemSchema.AccessAudit.Detail} = '{detail}' AND {SystemSchema.AccessAudit.ActorUserId} IS NULL AND {SystemSchema.AccessAudit.TargetUserId} = @u", ("u", member.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantCashAccountManagerOfflineAsync(member.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.AlreadyApprover, await Rbac.GrantCashAccountManagerOfflineAsync(member.Login.ToUpperInvariant(), Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantCashAccountSensitiveViewerOfflineAsync(member.Login, Company.Id));
        Assert.Equal(1L, await Count("scope: company; offline cash account manager delegation"));
        Assert.Equal(1L, await Count("scope: company; offline cash account sensitive viewer delegation"));
        var held = (await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, member.Id)).Value!.Select(a => a.RoleId).ToList();
        Assert.Contains(PresetRoles.CashAccountManagerId, held); Assert.Contains(PresetRoles.CashAccountSensitiveViewerId, held);
        var assignment = (await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, member.Id)).Value!.Single(a => a.RoleId == PresetRoles.CashAccountManagerId);
        var revoke = await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Id);
        Assert.False(revoke.IsOk);                                                                                                                    // nor can an administrator remove one

        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeCashAccountManagerOfflineAsync(member.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.NotApprover, await Rbac.RevokeCashAccountManagerOfflineAsync(member.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeCashAccountSensitiveViewerOfflineAsync(member.Login, Company.Id));
        Assert.Equal(1L, await Count("scope: company; offline cash account manager removal"));
        Assert.Equal(1L, await Count("scope: company; offline cash account sensitive viewer removal"));
        Assert.Empty((await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, member.Id)).Value!.Where(a => a.RoleId == PresetRoles.CashAccountManagerId));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId} = @u AND {Ra.RoleId} = @r AND {Ra.RevokedAtUtc} IS NOT NULL", ("u", member.Id), ("r", PresetRoles.CashAccountManagerId)));   // revoked, never deleted
    }

    [PostgresFact]
    public async Task The_offline_delegation_needs_an_active_member_of_a_ready_company_and_never_creates_membership()
    {
        await Start();
        var stranger = await NewUser("stranger");                                                                                                      // a user, not a member
        Assert.Equal(AccessService.FiscalApproverStatus.NotMember, await Rbac.GrantCashAccountManagerOfflineAsync(stranger.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.NotMember, await Rbac.GrantCashAccountSensitiveViewerOfflineAsync(stranger.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.UserNotFound, await Rbac.GrantCashAccountManagerOfflineAsync("nobody-here", Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.CompanyUnavailable, await Rbac.GrantCashAccountManagerOfflineAsync(stranger.Login, Guid.NewGuid()));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId} = @u", ("u", stranger.Id)));                            // nothing was created
    }
}

/// <summary>The Hasm.Admin commands for the two presets (real tool, real file-based audit keys shared with the tool): grant, repeat, revoke, refusals, usage, and the audit events.</summary>
public class CashPresetRolesToolTests : FileEvidenceTestBase
{
    [PostgresFact]
    public async Task The_Admin_commands_grant_and_revoke_the_presets_with_an_audit_event_and_print_no_secret()
    {
        var (company, admin, _, service) = await Written();
        await using var _s = service;
        var env = new Dictionary<string, string?> { ["Hasm__Audit__KeyDirectory"] = KeyDir, ["Hasm__Audit__ActiveKeyId"] = "k1", ["Hasm__Audit__CheckpointDirectory"] = CpDir };
        var member = await NewPerson("cli-cash");
        Assert.True((await service.GrantMembershipAsync(admin.Id, company, member.Login)).IsOk);
        async Task<List<Guid>> Held() => (await service.ListAssignmentsAsync(admin.Id, company, member.Id)).Value!.Select(a => a.RoleId).ToList();
        foreach (var (name, roleName, preset) in new[] { ("cash-account-manager", "Cash Account Manager", PresetRoles.CashAccountManagerId), ("cash-account-sensitive-viewer", "Cash Account Sensitive Viewer", PresetRoles.CashAccountSensitiveViewerId) })
        {
            var grant = await AdminTool.RunWith(Options, env, $"grant-{name}", member.Login, company.ToString());
            Assert.True(grant.Exit == 0, grant.All);
            Assert.Contains($"The {roleName} role was granted (company-wide).", grant.Out);
            Assert.Contains(preset, await Held());
            var again = await AdminTool.RunWith(Options, env, $"grant-{name}", member.Login, company.ToString());
            Assert.Equal(0, again.Exit);
            Assert.Contains("already", again.Out);
            var revoke = await AdminTool.RunWith(Options, env, $"revoke-{name}", member.Login, company.ToString());
            Assert.True(revoke.Exit == 0, revoke.All);
            Assert.Contains($"The {roleName} role was removed.", revoke.Out);
            Assert.DoesNotContain(preset, await Held());
            var missing = await AdminTool.RunWith(Options, env, $"grant-{name}", "nobody-here", company.ToString());
            Assert.Equal(1, missing.Exit);
            Assert.Equal(2, (await AdminTool.RunWith(Options, env, $"grant-{name}", member.Login)).Exit);                                          // usage
            foreach (var text in new[] { grant.All, again.All, revoke.All, missing.All }) { Assert.DoesNotContain(Options.AdminPassword!, text); Assert.DoesNotContain(Options.SystemDatabaseName!, text); }
        }
        var audit = SystemSchema.AccessAudit.Table;
        Assert.Equal(2L, await Scalar<long>($"SELECT count(*) FROM {audit} WHERE {SystemSchema.AccessAudit.Detail} LIKE 'scope: company; offline cash account % delegation' AND {SystemSchema.AccessAudit.ActorUserId} IS NULL"));
        Assert.Equal(2L, await Scalar<long>($"SELECT count(*) FROM {audit} WHERE {SystemSchema.AccessAudit.Detail} LIKE 'scope: company; offline cash account % removal' AND {SystemSchema.AccessAudit.ActorUserId} IS NULL"));
    }
}

/// <summary>
/// The migration itself, on a system database that already holds a company, an administrator and an offline approver: it is an UPGRADE of an existing installation. It creates exactly the two presets with exactly their
/// permissions, leaves both immutability triggers enabled, and changes no pre-existing preset, role assignment or membership.
/// </summary>
public class CashPresetRolesMigrationTests : PostgresTestBase
{
    private static readonly IReadOnlyList<MigrationScript> All = MigrationScript.LoadEmbedded();
    private static readonly MigrationScript Presets = All.Single(s => s.Name == "0036_cash_account_presets.sql");
    private static readonly string[] OldPresetIds = [.. Enumerable.Range(1, 8).Select(i => $"5c1b0f6e-0000-4a00-8000-{i:x12}")];

    /// <summary>Every row of a table as text (column order is the table's), as a set-comparable list.</summary>
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

    [PostgresFact]
    public async Task The_migration_creates_exactly_the_two_presets_and_changes_no_existing_preset_assignment_or_membership_and_leaves_every_trigger_enabled()
    {
        // an installation at the schema BEFORE the migration (every earlier system migration), with data written through the real services
        await using (var maintenance = new NpgsqlConnection(Options.BuildMaintenanceConnectionString()))
        {
            await maintenance.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(Options.SystemDatabaseName!)}", maintenance);
            await create.ExecuteNonQueryAsync();
        }
        var connection = Options.BuildSystemConnectionString(admin: true);
        var before = All.Where(s => s.Version < Presets.Version).ToList();
        Assert.Equal(Presets.Version - 1, before.Count);
        Assert.Equal(before.Count, (await new MigrationRunner(connection).ApplyAsync(before)).Applied.Count);

        var clock = new FakeClock();
        var audit = new Hasm.Infrastructure.Audit.AuditTrail(new Hasm.Infrastructure.Audit.AuditServices(new Hasm.Infrastructure.Audit.InMemoryAuditKeyRing(), new Hasm.Infrastructure.Audit.InMemoryCheckpointStore(), 0), clock);
        var store = new IdentityStore(connection, clock, audit);
        var auth = new AuthService(store, new PasswordService(1000), new AuthOptions());
        var company = Guid.NewGuid();
        await using var conn0 = new NpgsqlConnection(connection);
        await conn0.OpenAsync();
        await using (var ins = new NpgsqlCommand($"INSERT INTO {Co.Table} ({Co.Id},{Co.PhysicalDatabaseName},{Co.ProvisioningState},{Co.CreatedAtUtc},{Co.StateChangedAtUtc}) VALUES (@i,@n,@s,now(),now())", conn0))
        {
            ins.Parameters.AddWithValue("i", company); ins.Parameters.AddWithValue("n", Hasm.Infrastructure.Naming.PhysicalIdentifier.GenerateDatabaseName()); ins.Parameters.AddWithValue("s", (short)ProvisioningState.Ready);
            await ins.ExecuteNonQueryAsync();
        }
        await using var access = new AccessService(connection, clock, PermissionCatalog.Default, audit);
        var admin = (await auth.CreateUserAsync("upgrade-admin", "Admin", "Correct-Horse-Battery-9", false)).User!;
        Assert.Equal(AccessService.BootstrapStatus.Granted, await access.BootstrapCompanyAdministratorAsync(admin.Login, company));
        var approver = (await auth.CreateUserAsync("upgrade-approver", "Approver", "Correct-Horse-Battery-9", false)).User!;
        Assert.True((await access.GrantMembershipAsync(admin.Id, company, approver.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await access.GrantFiscalApproverOfflineAsync(approver.Login, company));

        var rolesBefore = await Rows(Ro.Table);
        var permissionsBefore = await Rows(Rp.Table);
        var assignmentsBefore = await Rows(Ra.Table);
        var membershipsBefore = await Rows(SystemSchema.UserCompanyAccess.Table);
        Assert.Equal(8, rolesBefore.Count);
        Assert.NotEmpty(assignmentsBefore);

        // the upgrade: the real initializer applies exactly the pending migration (this one)
        var result = await new SystemDatabaseInitializer(Options).InitializeAsync();
        Assert.Equal([Presets.Version], result.AppliedMigrations);

        // every pre-existing preset, permission row, assignment and membership is byte-identical: rows were only ADDED (2 roles, 6 permission rows)
        var rolesAfter = await Rows(Ro.Table);
        var permissionsAfter = await Rows(Rp.Table);
        Assert.Equal(10, rolesAfter.Count);
        Assert.Empty(rolesBefore.Except(rolesAfter));
        Assert.Empty(permissionsBefore.Except(permissionsAfter));
        Assert.Equal(permissionsBefore.Count + 6, permissionsAfter.Count);
        Assert.Equal(assignmentsBefore, await Rows(Ra.Table));
        Assert.Equal(membershipsBefore, await Rows(SystemSchema.UserCompanyAccess.Table));

        // exactly the two presets, with exactly their permissions, active, equal to the code definition
        foreach (var id in new[] { PresetRoles.CashAccountManagerId, PresetRoles.CashAccountSensitiveViewerId })
        {
            Assert.Equal(PresetRoles.Definitions[id].Key, await Scalar<string>($"SELECT {Ro.PresetKey} FROM {Ro.Table} WHERE {Ro.Id} = @i", ("i", id)));
            Assert.True(await Scalar<bool>($"SELECT {Ro.IsActive} FROM {Ro.Table} WHERE {Ro.Id} = @i", ("i", id)));
            Assert.Equal(PresetRoles.Definitions[id].Permissions.Order(StringComparer.Ordinal), await Permissions(id));
        }
        // nothing was granted to anyone: no assignment of either preset exists
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.RoleId} IN (@a, @b)", ("a", PresetRoles.CashAccountManagerId), ("b", PresetRoles.CashAccountSensitiveViewerId)));
        // every pre-existing preset keeps exactly the permissions of the code definition, and Company Administrator gained no cash code
        foreach (var (id, (_, permissions)) in PresetRoles.Definitions.Where(d => OldPresetIds.Contains(d.Key.ToString())))
            Assert.Equal(permissions.Order(StringComparer.Ordinal), await Permissions(id));
        Assert.DoesNotContain(await Permissions(PresetRoles.CompanyAdministratorId), c => c.StartsWith("company.cashaccounts", StringComparison.Ordinal));

        // both immutability triggers are enabled again and no trigger is switched off anywhere
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgenabled <> 'O'"));
        foreach (var (table, trigger) in new[] { (Ro.Table, Ro.GuardTrigger), (Rp.Table, Rp.GuardTrigger) })
            Assert.Equal("O", await Scalar<string>($"SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = '{table}'::regclass AND t.tgname = '{trigger}'"));
        // and the preset roles are immutable again: a later change is refused
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Rp.Table} ({Rp.RoleId},{Rp.Permission}) VALUES (@r,'company.cashaccountssensitive.view')", ("r", PresetRoles.CashAccountManagerId)));
        Assert.Contains(await Rows(Ra.Table), row => row.Contains(approver.Id.ToString(), StringComparison.Ordinal));
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
}
