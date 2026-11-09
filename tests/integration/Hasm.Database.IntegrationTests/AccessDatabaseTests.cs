using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Naming;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Au = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using Mem = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserCompanyAccess;
using Ra = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using Ro = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using Rp = Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission;
using Se = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using Codes = Hasm.Infrastructure.Access.PermissionCatalog.Codes;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Access/RBAC wired to a real, disposable PostgreSQL system database (same safety gate as every PostgreSQL test).
/// Companies are registry rows in the Ready state; no company database is created because access metadata is central.
/// The test catalog adds branch- and warehouse-level resources that exist only in tests (the production catalog contains
/// administrative resources only); roles carrying them are seeded by SQL, standing in for later-phase preset roles.
/// </summary>
public abstract class AccessTestBase : IdentityTestBase
{
    protected static readonly PermissionCatalog TestCatalog = new(
    [
        .. PermissionCatalog.Default.Resources,
        new PermissionResource("docs.sample", ScopeKind.Branch, [PermissionAction.View, PermissionAction.Create, PermissionAction.Approve, PermissionAction.Post]),
        new PermissionResource("stock.sample", ScopeKind.Warehouse, [PermissionAction.View, PermissionAction.Update]),
    ]);

    public static readonly Guid KnownBranchA = Guid.NewGuid(), KnownBranchB = Guid.NewGuid(), KnownWarehouseX = Guid.NewGuid(), KnownWarehouseY = Guid.NewGuid();
    protected static Guid BranchA => KnownBranchA;
    protected static Guid BranchB => KnownBranchB;
    protected static Guid WarehouseX => KnownWarehouseX;
    protected static Guid WarehouseY => KnownWarehouseY;
    protected const string DocView = "docs.sample.view", DocApprove = "docs.sample.approve", DocPost = "docs.sample.post", StockView = "stock.sample.view";

    protected AccessService Access { get; private set; } = null!;
    private int counter;

    protected async Task StartAccess()
    {
        await Start();
        Access = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog, Audit, new KnownScopeValidator());
    }

    public override async Task DisposeAsync()
    {
        if (Access is not null) await Access.DisposeAsync();
        await base.DisposeAsync();
    }

    protected async Task<Guid> NewCompany(ProvisioningState state = ProvisioningState.Ready)
    {
        var id = Guid.NewGuid();
        await Exec($"INSERT INTO {Co.Table} ({Co.Id},{Co.PhysicalDatabaseName},{Co.ProvisioningState},{Co.CreatedAtUtc},{Co.StateChangedAtUtc}) VALUES (@i,@n,@s,now(),now())",
            ("i", id), ("n", PhysicalIdentifier.GenerateDatabaseName()), ("s", (short)state));
        return id;
    }

    protected Task<UserRecord> NewPerson(string prefix = "user") => NewUser($"{prefix}{Interlocked.Increment(ref counter)}");

    /// <summary>A user bootstrapped as Company Administrator (the offline initial-administration path).</summary>
    protected async Task<UserRecord> NewAdmin(Guid company, string prefix = "admin")
    {
        var user = await NewPerson(prefix);
        Assert.Equal(AccessService.BootstrapStatus.Granted, await Access.BootstrapCompanyAdministratorAsync(user.Login, company));
        return user;
    }

    /// <summary>A user who is a member (granted through the service by <paramref name="actor"/>) without any role.</summary>
    protected async Task<UserRecord> NewMember(Guid company, UserRecord actor, string prefix = "member")
    {
        var user = await NewPerson(prefix);
        Ok(await Access.GrantMembershipAsync(actor.Id, company, user.Login));
        return user;
    }

    /// <summary>Directly inserted role (custom when a company is given), standing in for a later-phase preset.</summary>
    protected async Task<Guid> SeedRole(Guid company, string name, params string[] permissions)
    {
        var id = Guid.NewGuid();
        await Exec($"INSERT INTO {Ro.Table} ({Ro.Id},{Ro.CompanyId},{Ro.PresetKey},{Ro.Name},{Ro.IsActive},{Ro.CreatedAtUtc},{Ro.CreatedBy},{Ro.UpdatedAtUtc}) VALUES (@i,@c,NULL,@n,true,now(),NULL,now())",
            ("i", id), ("c", company), ("n", name));
        foreach (var p in permissions)
            await Exec($"INSERT INTO {Rp.Table} ({Rp.RoleId},{Rp.Permission}) VALUES (@r,@p)", ("r", id), ("p", p));
        return id;
    }

    protected async Task SeedAssignment(Guid user, Guid company, Guid role, ScopeKind kind = ScopeKind.Company, Guid? scopeId = null) =>
        await Exec($"INSERT INTO {Ra.Table} ({Ra.Id},{Ra.UserId},{Ra.CompanyId},{Ra.RoleId},{Ra.ScopeKind},{Ra.ScopeId},{Ra.GrantedAtUtc}) VALUES (@i,@u,@c,@r,@k,@s,now())",
            ("i", Guid.NewGuid()), ("u", user), ("c", company), ("r", role), ("k", (short)kind), ("s", (object?)scopeId ?? DBNull.Value));

    /// <summary>Company Administrator that additionally holds every test-catalog permission company-wide (a future "everything" preset).</summary>
    protected async Task<UserRecord> NewSuper(Guid company, string prefix = "super")
    {
        var user = await NewAdmin(company, prefix);
        await SeedAssignment(user.Id, company, await SeedRole(company, "Everything " + prefix, TestCatalog.All.Select(p => p.Code).ToArray()));
        return user;
    }

    protected Task<long> AuditCount(Guid company, AccessEvent? ev = null) =>
        Scalar<long>($"SELECT count(*) FROM {Au.Table} WHERE {Au.CompanyId}=@c AND (@e::smallint IS NULL OR {Au.EventCode}=@e)",
            ("c", company), ("e", ev is null ? DBNull.Value : (short)ev.Value));

    protected static T Ok<T>(AccessResult<T> result)
    {
        Assert.True(result.IsOk, $"expected Ok but was {result.Status}/{result.Code}");
        return result.Value!;
    }

    protected static void Fails<T>(AccessResult<T> result, AccessStatus status, string? code = null)
    {
        Assert.Equal(status, result.Status);
        if (code is not null) Assert.Equal(code, result.Code);
    }

    protected Task<bool> Allowed(UserRecord user, Guid company, string permission, ScopeTarget? target = null) =>
        Access.IsAllowedAsync(user.Id, company, permission, target ?? ScopeTarget.Company);
}

public class AccessMigrationTests : AccessTestBase
{
    [PostgresFact]
    public async Task Migration_0004_is_applied_once_and_repeating_is_a_noop()
    {
        var first = await new SystemDatabaseInitializer(Options).InitializeAsync();
        Assert.Contains(4, first.AppliedMigrations);
        var checksum = await Scalar<string>($"SELECT checksum FROM {SystemSchema.MigrationJournalTable} WHERE version = 4");
        await Exec($"INSERT INTO {Co.Table} ({Co.Id},{Co.PhysicalDatabaseName},{Co.ProvisioningState},{Co.CreatedAtUtc},{Co.StateChangedAtUtc}) VALUES (@i,'dkeepme',2,now(),now())", ("i", Guid.NewGuid()));

        var second = await new SystemDatabaseInitializer(Options).InitializeAsync();

        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(checksum, await Scalar<string>($"SELECT checksum FROM {SystemSchema.MigrationJournalTable} WHERE version = 4"));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Co.Table}"));                // data preserved
        Assert.Equal(3L, await Scalar<long>($"SELECT count(*) FROM {Ro.Table}"));                // presets seeded exactly once
    }

    [PostgresFact]
    public async Task Physical_access_schema_matches_the_logical_map_and_is_opaque()
    {
        await StartAccess();
        var expected = new Dictionary<string, string[]>
        {
            [Ro.Table] = [Ro.Id, Ro.CompanyId, Ro.PresetKey, Ro.Name, Ro.IsActive, Ro.CreatedAtUtc, Ro.CreatedBy, Ro.UpdatedAtUtc],
            [Rp.Table] = [Rp.RoleId, Rp.Permission],
            [Ra.Table] = [Ra.Id, Ra.UserId, Ra.CompanyId, Ra.RoleId, Ra.ScopeKind, Ra.ScopeId, Ra.GrantedAtUtc, Ra.GrantedBy, Ra.RevokedAtUtc, Ra.RevokedBy],
            [Au.Table] = [Au.Id, Au.OccurredAtUtc, Au.ActorUserId, Au.CompanyId, Au.TargetUserId, Au.RoleId, Au.EventCode, Au.Detail],
        };
        foreach (var (table, columns) in expected)
        {
            Assert.Equal(columns.Length, (int)await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_name=@t", ("t", table)));
            foreach (var c in columns)
                Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_name=@t AND column_name=@c", ("t", table), ("c", c)));
        }
        foreach (var k in new[] { Ro.PrimaryKey, Ro.ForeignKeyCompany, Ro.ForeignKeyCreatedBy, Ro.CheckPreset, Ro.CheckName, Ro.UniquePresetKey, Ro.UniqueName,
                     Rp.PrimaryKey, Rp.ForeignKeyRole, Rp.CheckPermission,
                     Ra.PrimaryKey, Ra.ForeignKeyMembership, Ra.ForeignKeyRole, Ra.ForeignKeyGrantedBy, Ra.ForeignKeyRevokedBy, Ra.CheckScope, Ra.CheckRevocation, Ra.UniqueActive, Ra.IndexMember,
                     Au.PrimaryKey, Au.ForeignKeyActor, Au.ForeignKeyCompany, Au.ForeignKeyTarget, Au.CheckEvent, Au.CheckDetail, Au.IndexCompany,
                     Mem.ForeignKeyGrantedBy, Mem.ForeignKeyRevokedBy, Se.ForeignKeySelectedCompany })
            Assert.True(await Scalar<long>("SELECT (SELECT count(*) FROM pg_constraint WHERE conname=@n) + (SELECT count(*) FROM pg_class WHERE relname=@n)", ("n", k)) > 0, k);
        foreach (var trigger in new[] { Ro.GuardTrigger, Rp.GuardTrigger, Ra.GuardTrigger, Au.GuardTrigger })
            Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_trigger WHERE tgname=@n", ("n", trigger)));

        var names = await Scalar<string>("SELECT string_agg(table_name || ',' || column_name, ';') FROM information_schema.columns WHERE table_name = ANY(@t)", ("t", expected.Keys.ToArray()));
        foreach (var word in new[] { "role", "permission", "assignment", "audit", "scope", "actor", "preset" })
            Assert.DoesNotContain(word, names, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Preset_roles_in_the_database_equal_the_code_definitions()
    {
        await StartAccess();
        foreach (var (id, (key, permissions)) in PresetRoles.Definitions)
        {
            Assert.Equal(key, await Scalar<string>($"SELECT {Ro.PresetKey} FROM {Ro.Table} WHERE {Ro.Id}=@i AND {Ro.CompanyId} IS NULL AND {Ro.IsActive}", ("i", id)));
            var stored = await Scalar<string[]>($"SELECT array_agg({Rp.Permission} ORDER BY {Rp.Permission}) FROM {Rp.Table} WHERE {Rp.RoleId}=@i", ("i", id));
            Assert.Equal(permissions.Order(StringComparer.Ordinal), stored);
        }
        Assert.Equal(PresetRoles.Definitions.Count, (int)await Scalar<long>($"SELECT count(*) FROM {Ro.Table}"));
    }

    [PostgresFact]
    public async Task Database_guards_hold_even_when_the_application_is_bypassed()
    {
        await StartAccess();
        var company = await NewCompany();
        var other = await NewCompany();
        var admin = await NewAdmin(company);
        var member = await NewMember(company, admin);
        var custom = await SeedRole(company, "Custom");
        var foreign = await SeedRole(other, "Foreign");
        var preset = PresetRoles.AccessAuditorId;

        // presets are immutable; roles are never deleted; identity fields are fixed
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Ro.Table} SET {Ro.Name}='x' WHERE {Ro.Id}=@i", ("i", preset)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Ro.Table} SET {Ro.IsActive}=false WHERE {Ro.Id}=@i", ("i", preset)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {Ro.Table} WHERE {Ro.Id}=@i", ("i", custom)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Ro.Table} SET {Ro.CompanyId}=@c WHERE {Ro.Id}=@i", ("i", custom), ("c", other)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Rp.Table} ({Rp.RoleId},{Rp.Permission}) VALUES (@r,'access.audit.view')", ("r", PresetRoles.AccessManagerId)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {Rp.Table} WHERE {Rp.RoleId}=@r", ("r", preset)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Ro.Table} ({Ro.Id},{Ro.CompanyId},{Ro.PresetKey},{Ro.Name},{Ro.IsActive},{Ro.CreatedAtUtc},{Ro.UpdatedAtUtc}) VALUES (@i,NULL,'rogue','Rogue',true,now(),now())", ("i", Guid.NewGuid())));
        // preset/company consistency and name rules
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Ro.Table} ({Ro.Id},{Ro.CompanyId},{Ro.PresetKey},{Ro.Name},{Ro.IsActive},{Ro.CreatedAtUtc},{Ro.UpdatedAtUtc}) VALUES (@i,@c,'p','Named',true,now(),now())", ("i", Guid.NewGuid()), ("c", company)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Ro.Table} ({Ro.Id},{Ro.CompanyId},{Ro.Name},{Ro.IsActive},{Ro.CreatedAtUtc},{Ro.UpdatedAtUtc}) VALUES (@i,@c,'custom',true,now(),now())", ("i", Guid.NewGuid()), ("c", company))); // duplicate (case-insensitive)
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Ro.Table} ({Ro.Id},{Ro.CompanyId},{Ro.Name},{Ro.IsActive},{Ro.CreatedAtUtc},{Ro.UpdatedAtUtc}) VALUES (@i,@c,'  ',true,now(),now())", ("i", Guid.NewGuid()), ("c", company)));
        // permission codes must be structured
        foreach (var bad in new[] { "NoDots", "Upper.case", "a", "a.b.c.d.e", "a..b", "a.b;drop" })
            await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Rp.Table} ({Rp.RoleId},{Rp.Permission}) VALUES (@r,@p)", ("r", custom), ("p", bad)));
        // assignments: a role of another company is rejected, membership is required, scope shape is enforced, duplicates are rejected
        await Assert.ThrowsAsync<PostgresException>(() => SeedAssignment(member.Id, company, foreign));
        var stranger = await NewPerson();
        await Assert.ThrowsAsync<PostgresException>(() => SeedAssignment(stranger.Id, company, custom));
        await Assert.ThrowsAsync<PostgresException>(() => SeedAssignment(member.Id, company, custom, ScopeKind.Branch, null));
        await Assert.ThrowsAsync<PostgresException>(() => SeedAssignment(member.Id, company, custom, ScopeKind.Company, BranchA));
        await Assert.ThrowsAsync<PostgresException>(() => SeedAssignment(member.Id, company, custom, (ScopeKind)7, BranchA));
        await SeedAssignment(member.Id, company, custom, ScopeKind.Branch, BranchA);
        await Assert.ThrowsAsync<PostgresException>(() => SeedAssignment(member.Id, company, custom, ScopeKind.Branch, BranchA));
        await SeedAssignment(member.Id, company, custom, ScopeKind.Branch, BranchB);                  // another scope is fine
        await SeedAssignment(member.Id, company, PresetRoles.AccessAuditorId);                         // presets are assignable anywhere
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {Ra.Table}"));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Ra.Table} SET {Ra.ScopeKind}=0, {Ra.ScopeId}=NULL WHERE {Ra.ScopeKind}=1"));
        // audit is append-only
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Au.Table} SET {Au.Detail}='tampered'"));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {Au.Table}"));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Au.Table} ({Au.OccurredAtUtc},{Au.CompanyId},{Au.EventCode}) VALUES (now(),@c,99)", ("c", company)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"INSERT INTO {Au.Table} ({Au.OccurredAtUtc},{Au.CompanyId},{Au.EventCode}) VALUES (now(),@c,1)", ("c", Guid.NewGuid()))); // unknown company
    }
}

public class MembershipTests : AccessTestBase
{
    [PostgresFact]
    public async Task Authentication_alone_grants_no_company_access()
    {
        await StartAccess();
        var company = await NewCompany();
        await NewAdmin(company);
        var user = await NewPerson("plain");
        var login = await LoginOk(user.Login);

        Assert.Empty(await Access.ListAccessibleCompaniesAsync(user.Id));
        Assert.False(await Access.SelectCompanyAsync(login.SessionId, user.Id, company));
        Assert.Empty((await Access.GetEffectivePermissionsAsync(user.Id, company)).Grants);
        foreach (var p in TestCatalog.All) Assert.False(await Allowed(user, company, p.Code), p.Code);
        Fails(await Access.ListMembersAsync(user.Id, company), AccessStatus.Forbidden);
        Fails(await Access.ListRolesAsync(user.Id, company), AccessStatus.Forbidden);
        Fails(await Access.GrantMembershipAsync(user.Id, company, user.Login), AccessStatus.Forbidden);
        Assert.Null((await Access.GetSessionContextAsync(login.SessionId))!.CompanyId);
    }

    [PostgresFact]
    public async Task An_identity_administrator_has_no_company_access_by_virtue_of_that_flag()
    {
        await StartAccess();
        var company = await NewCompany();
        await NewAdmin(company);
        var idAdmin = await NewUser("idadmin", admin: true);
        Assert.False(await Allowed(idAdmin, company, Codes.MembersView));
        Fails(await Access.GrantMembershipAsync(idAdmin.Id, company, idAdmin.Login), AccessStatus.Forbidden);
    }

    [PostgresFact]
    public async Task Membership_is_granted_by_an_authorized_actor_audited_and_grants_no_roles()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewPerson("newbie");

        var view = Ok(await Access.GrantMembershipAsync(admin.Id, company, "  " + user.Login.ToUpperInvariant() + " ")); // normalized like login
        Assert.Equal(user.Id, view.UserId);

        Assert.Equal([company], await Access.ListAccessibleCompaniesAsync(user.Id));
        Assert.Empty((await Access.GetEffectivePermissionsAsync(user.Id, company)).Grants);       // membership alone: no permissions
        Assert.Equal(2L, await AuditCount(company, AccessEvent.MembershipGranted));
        Fails(await Access.GrantMembershipAsync(admin.Id, company, user.Login), AccessStatus.Conflict, "already_member");
        Assert.Equal(2L, await AuditCount(company, AccessEvent.MembershipGranted));              // failed attempt left no trace
        Assert.Contains(Ok(await Access.ListMembersAsync(admin.Id, company)), m => m.UserId == user.Id);
    }

    [PostgresFact]
    public async Task Granting_membership_to_unknown_disabled_or_locked_users_is_indistinguishable()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var disabled = await NewPerson("off");
        await Auth.SetUserStateAsync(admin.Id, disabled.Id, UserState.Disabled);
        var locked = await NewPerson("locked");
        await Auth.SetUserStateAsync(admin.Id, locked.Id, UserState.Locked);

        foreach (var login in new[] { "does.not.exist", disabled.Login, locked.Login, "x", "", "bad login!" })
            Fails(await Access.GrantMembershipAsync(admin.Id, company, login), AccessStatus.NotFound, "user_not_found");
        Assert.Equal(1L, await AuditCount(company, AccessEvent.MembershipGranted));
    }

    [PostgresFact]
    public async Task Membership_requires_a_ready_company()
    {
        await StartAccess();
        var pending = await NewCompany(ProvisioningState.Pending);
        var ready = await NewCompany();
        var admin = await NewAdmin(ready);
        var user = await NewPerson();
        Assert.Equal(AccessService.BootstrapStatus.CompanyUnavailable, await Access.BootstrapCompanyAdministratorAsync(user.Login, pending));
        Assert.Equal(AccessService.BootstrapStatus.CompanyUnavailable, await Access.BootstrapCompanyAdministratorAsync(user.Login, Guid.NewGuid()));
        Fails(await Access.GrantMembershipAsync(admin.Id, pending, user.Login), AccessStatus.Forbidden); // the admin has nothing in that company
    }

    [PostgresFact]
    public async Task A_user_with_several_company_memberships_is_isolated_per_company()
    {
        await StartAccess();
        var a = await NewCompany();
        var b = await NewCompany();
        var c = await NewCompany();
        var adminA = await NewAdmin(a, "adminA");
        var adminB = await NewAdmin(b, "adminB");
        var multi = await NewPerson("multi");
        Ok(await Access.GrantMembershipAsync(adminA.Id, a, multi.Login));
        Ok(await Access.GrantMembershipAsync(adminB.Id, b, multi.Login));
        Ok(await Access.AssignRoleAsync(adminA.Id, a, multi.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));

        Assert.Equal(new[] { a, b }.Order(), (await Access.ListAccessibleCompaniesAsync(multi.Id)).Order());
        Assert.True(await Allowed(multi, a, Codes.AuditView));       // role held in A ...
        Assert.False(await Allowed(multi, b, Codes.AuditView));      // ... does not apply in B
        Assert.False(await Allowed(multi, c, Codes.AuditView));      // ... nor in a company they have no membership of
        Fails(await Access.ListAuditAsync(multi.Id, b), AccessStatus.Forbidden);
        Assert.DoesNotContain(Ok(await Access.ListAuditAsync(multi.Id, a)), e => e.ActorUserId == adminB.Id);

        // A's admin has no authority in B and vice versa
        Fails(await Access.GrantMembershipAsync(adminA.Id, b, adminA.Login), AccessStatus.Forbidden);
        Assert.DoesNotContain(Ok(await Access.ListMembersAsync(adminA.Id, a)), m => m.UserId == adminB.Id);
        Assert.DoesNotContain(Ok(await Access.ListMembersAsync(adminB.Id, b)), m => m.UserId == adminA.Id);
    }

    [PostgresFact]
    public async Task Revoking_membership_ends_roles_and_company_selection_and_regrant_starts_clean()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewMember(company, admin);
        Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var session = await LoginOk(user.Login);
        Assert.True(await Access.SelectCompanyAsync(session.SessionId, user.Id, company));
        Assert.True(await Allowed(user, company, Codes.AuditView));

        Assert.True(Ok(await Access.RevokeMembershipAsync(admin.Id, company, user.Id)));

        Assert.False(await Allowed(user, company, Codes.AuditView));                              // effective immediately, no new login
        Assert.Empty(await Access.ListAccessibleCompaniesAsync(user.Id));
        Assert.Null((await Access.GetSessionContextAsync(session.SessionId))!.CompanyId);          // selection cleared
        Assert.False(await Access.SelectCompanyAsync(session.SessionId, user.Id, company));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId}=@u AND {Ra.RevokedAtUtc} IS NULL", ("u", user.Id)));
        Assert.Equal(1L, await AuditCount(company, AccessEvent.MembershipRevoked));
        Fails(await Access.RevokeMembershipAsync(admin.Id, company, user.Id), AccessStatus.NotFound, "member_not_found");

        Ok(await Access.GrantMembershipAsync(admin.Id, company, user.Login));                      // re-grant
        Assert.Empty((await Access.GetEffectivePermissionsAsync(user.Id, company)).Grants);        // old roles do not come back
        Assert.False(await Allowed(user, company, Codes.AuditView));
    }

    [PostgresFact]
    public async Task Membership_cannot_be_revoked_by_the_unauthorized_from_oneself_or_from_a_more_privileged_user()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var manager = await NewMember(company, admin, "manager");
        Ok(await Access.AssignRoleAsync(admin.Id, company, manager.Id, PresetRoles.AccessManagerId, ScopeKind.Company, null));
        var auditor = await NewMember(company, admin, "auditor");
        Ok(await Access.AssignRoleAsync(admin.Id, company, auditor.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var plain = await NewMember(company, admin, "plain");

        Fails(await Access.RevokeMembershipAsync(plain.Id, company, auditor.Id), AccessStatus.Forbidden, "forbidden");                      // no permission
        Fails(await Access.RevokeMembershipAsync(admin.Id, company, admin.Id), AccessStatus.Forbidden, "cannot_modify_self");
        Fails(await Access.RevokeMembershipAsync(manager.Id, company, admin.Id), AccessStatus.Forbidden, "target_has_higher_privileges");   // admin holds more
        Fails(await Access.RevokeMembershipAsync(manager.Id, company, auditor.Id), AccessStatus.Forbidden, "target_has_higher_privileges"); // audit.view is not held by the manager
        Ok(await Access.RevokeMembershipAsync(manager.Id, company, plain.Id));                                                               // equal-or-lower is fine
        Assert.True(await Allowed(admin, company, Codes.MembersView));                                                                       // nothing changed for admin
    }
}

public class RoleTests : AccessTestBase
{
    [PostgresFact]
    public async Task Preset_roles_are_visible_to_every_company_and_cannot_be_changed_through_the_service()
    {
        await StartAccess();
        var a = await NewCompany();
        var b = await NewCompany();
        var adminA = await NewAdmin(a, "adminA");
        var adminB = await NewAdmin(b, "adminB");

        foreach (var (admin, company) in new[] { (adminA, a), (adminB, b) })
        {
            var roles = Ok(await Access.ListRolesAsync(admin.Id, company));
            Assert.Equal(PresetRoles.Definitions.Keys.Order(), roles.Where(r => r.IsPreset).Select(r => r.Id).Order());
            Assert.All(roles.Where(r => r.IsPreset), r => Assert.Equal(PresetRoles.Definitions[r.Id].Permissions.Order(), r.Permissions));
        }
        Fails(await Access.SetRolePermissionsAsync(adminA.Id, a, PresetRoles.CompanyAdministratorId, []), AccessStatus.Forbidden, "preset_role_immutable");
        Fails(await Access.SetRolePermissionsAsync(adminA.Id, a, PresetRoles.AccessAuditorId, [Codes.RolesCreate]), AccessStatus.Forbidden, "preset_role_immutable");
        Fails(await Access.DeactivateRoleAsync(adminA.Id, a, PresetRoles.AccessAuditorId), AccessStatus.Forbidden, "preset_role_immutable");
        Assert.Equal(11, Ok(await Access.ListRolesAsync(adminB.Id, b)).Single(r => r.Id == PresetRoles.CompanyAdministratorId).Permissions.Count);
    }

    [PostgresFact]
    public async Task Custom_roles_can_be_created_updated_and_deactivated_with_unique_names_per_company()
    {
        await StartAccess();
        var a = await NewCompany();
        var b = await NewCompany();
        var adminA = await NewAdmin(a, "adminA");
        var adminB = await NewAdmin(b, "adminB");

        var role = Ok(await Access.CreateRoleAsync(adminA.Id, a, "  Reviewer ", [Codes.MembersView, Codes.RolesView, Codes.MembersView]));
        Assert.Equal("Reviewer", role.Name);
        Assert.False(role.IsPreset);
        Assert.Equal([Codes.MembersView, Codes.RolesView], role.Permissions);
        Fails(await Access.CreateRoleAsync(adminA.Id, a, "reviewer", []), AccessStatus.Conflict, "role_name_taken");
        Ok(await Access.CreateRoleAsync(adminB.Id, b, "Reviewer", []));                       // same name in another company is fine

        var updated = Ok(await Access.SetRolePermissionsAsync(adminA.Id, a, role.Id, [Codes.AuditView, Codes.RolesView]));
        Assert.Equal([Codes.AuditView, Codes.RolesView], updated.Permissions);
        Assert.Equal([Codes.AuditView, Codes.RolesView], Ok(await Access.ListRolesAsync(adminA.Id, a)).Single(r => r.Id == role.Id).Permissions);

        Assert.True(Ok(await Access.DeactivateRoleAsync(adminA.Id, a, role.Id)));
        Fails(await Access.DeactivateRoleAsync(adminA.Id, a, role.Id), AccessStatus.Conflict, "role_already_inactive");
        Assert.False(Ok(await Access.ListRolesAsync(adminA.Id, a)).Single(r => r.Id == role.Id).IsActive);
        Assert.Equal(1L, await AuditCount(a, AccessEvent.RoleCreated));
        Assert.Equal(1L, await AuditCount(a, AccessEvent.RolePermissionsChanged));
        Assert.Equal(1L, await AuditCount(a, AccessEvent.RoleDeactivated));
    }

    [PostgresFact]
    public async Task Invalid_role_input_is_rejected_without_side_effects()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var before = await AuditCount(company);

        Fails(await Access.CreateRoleAsync(admin.Id, company, "x", ["made.up.permission"]), AccessStatus.Invalid, "unknown_permission");
        Fails(await Access.CreateRoleAsync(admin.Id, company, "x", ["ACCESS.ROLES.VIEW"]), AccessStatus.Invalid, "unknown_permission");
        Fails(await Access.CreateRoleAsync(admin.Id, company, "x", null), AccessStatus.Invalid, "unknown_permission");
        Fails(await Access.CreateRoleAsync(admin.Id, company, "   ", []), AccessStatus.Invalid, "invalid_name");
        Fails(await Access.CreateRoleAsync(admin.Id, company, null, []), AccessStatus.Invalid, "invalid_name");
        Fails(await Access.CreateRoleAsync(admin.Id, company, new string('n', 101), []), AccessStatus.Invalid, "invalid_name");
        Fails(await Access.CreateRoleAsync(admin.Id, company, "bad\nname", []), AccessStatus.Invalid, "invalid_name");
        Assert.Equal(before, await AuditCount(company));
        Assert.Equal(3L, await Scalar<long>($"SELECT count(*) FROM {Ro.Table}"));
    }

    [PostgresFact]
    public async Task Roles_of_another_company_are_invisible_and_cannot_be_used_or_changed()
    {
        await StartAccess();
        var a = await NewCompany();
        var b = await NewCompany();
        var adminA = await NewAdmin(a, "adminA");
        var adminB = await NewAdmin(b, "adminB");
        var secret = Ok(await Access.CreateRoleAsync(adminB.Id, b, "B only", [Codes.AuditView]));
        var userA = await NewMember(a, adminA);

        Assert.DoesNotContain(Ok(await Access.ListRolesAsync(adminA.Id, a)), r => r.Id == secret.Id);
        Fails(await Access.AssignRoleAsync(adminA.Id, a, userA.Id, secret.Id, ScopeKind.Company, null), AccessStatus.NotFound, "role_not_found");
        Fails(await Access.SetRolePermissionsAsync(adminA.Id, a, secret.Id, []), AccessStatus.NotFound, "role_not_found");
        Fails(await Access.DeactivateRoleAsync(adminA.Id, a, secret.Id), AccessStatus.NotFound, "role_not_found");
        Assert.Equal([Codes.AuditView], Ok(await Access.ListRolesAsync(adminB.Id, b)).Single(r => r.Id == secret.Id).Permissions);
    }

    [PostgresFact]
    public async Task Deactivating_a_role_removes_what_it_granted_immediately()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewMember(company, admin);
        var role = Ok(await Access.CreateRoleAsync(admin.Id, company, "Auditors+", [Codes.AuditView, Codes.MembersView]));
        Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, role.Id, ScopeKind.Company, null));
        Assert.True(await Allowed(user, company, Codes.AuditView));

        Ok(await Access.DeactivateRoleAsync(admin.Id, company, role.Id));

        Assert.False(await Allowed(user, company, Codes.AuditView));
        Fails(await Access.AssignRoleAsync(admin.Id, company, (await NewMember(company, admin)).Id, role.Id, ScopeKind.Company, null), AccessStatus.NotFound, "role_not_found");
    }
}

public class AssignmentAndScopeTests : AccessTestBase
{
    [PostgresFact]
    public async Task Deny_by_default_for_members_without_roles_unknown_permissions_and_inactive_users()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var member = await NewMember(company, admin);

        foreach (var p in TestCatalog.All) Assert.False(await Allowed(member, company, p.Code), p.Code);
        Assert.False(await Allowed(admin, company, "made.up.permission"));
        Assert.False(await Allowed(admin, company, ""));
        Assert.False(await Allowed(admin, company, Codes.MembersView, ScopeTarget.Branch(BranchA)));                  // company-level resource, branch target
        Assert.False(await Allowed(admin, Guid.NewGuid(), Codes.MembersView));                                         // unknown company
        Assert.False(await Access.IsAllowedAsync(Guid.NewGuid(), company, Codes.MembersView, ScopeTarget.Company));    // unknown user
        Fails(await Access.ListMembersAsync(member.Id, company), AccessStatus.Forbidden);
        Fails(await Access.CreateRoleAsync(member.Id, company, "x", []), AccessStatus.Forbidden);
        Fails(await Access.AssignRoleAsync(member.Id, company, admin.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null), AccessStatus.Forbidden);
        Fails(await Access.ListAuditAsync(member.Id, company), AccessStatus.Forbidden);
    }

    [PostgresFact]
    public async Task A_user_holding_several_roles_gets_the_union_and_each_assignment_is_independently_revocable()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewMember(company, admin);
        var a1 = Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var custom = Ok(await Access.CreateRoleAsync(admin.Id, company, "Role creator", [Codes.RolesCreate]));
        var a2 = Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, custom.Id, ScopeKind.Company, null));

        Assert.True(await Allowed(user, company, Codes.AuditView));
        Assert.True(await Allowed(user, company, Codes.RolesCreate));
        Assert.False(await Allowed(user, company, Codes.RolesDelete));
        Assert.Equal(2, (await Access.GetEffectivePermissionsAsync(user.Id, company)).Grants.Count);
        Assert.Equal(2, Ok(await Access.ListAssignmentsAsync(admin.Id, company, user.Id)).Count);

        Ok(await Access.RevokeAssignmentAsync(admin.Id, company, a1.Id));
        Assert.False(await Allowed(user, company, Codes.AuditView));
        Assert.True(await Allowed(user, company, Codes.RolesCreate));
        Fails(await Access.RevokeAssignmentAsync(admin.Id, company, a1.Id), AccessStatus.NotFound, "assignment_not_found");
        Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null)); // can be assigned again after revoke
        Fails(await Access.AssignRoleAsync(admin.Id, company, user.Id, custom.Id, ScopeKind.Company, null), AccessStatus.Conflict, "already_assigned");
        Assert.Equal(a2.Id, Ok(await Access.ListAssignmentsAsync(admin.Id, company, user.Id)).Single(x => x.RoleId == custom.Id).Id);
    }

    [PostgresFact]
    public async Task Company_branch_and_warehouse_scopes_are_enforced()
    {
        await StartAccess();
        var company = await NewCompany();
        var super = await NewSuper(company);
        var branchUser = await NewMember(company, super, "branch");
        var warehouseUser = await NewMember(company, super, "wh");
        var companyUser = await NewMember(company, super, "co");
        var review = await SeedRole(company, "Doc reviewer", DocView, DocApprove);
        var stock = await SeedRole(company, "Stock viewer", StockView);

        Ok(await Access.AssignRoleAsync(super.Id, company, branchUser.Id, review, ScopeKind.Branch, BranchA));
        Ok(await Access.AssignRoleAsync(super.Id, company, warehouseUser.Id, stock, ScopeKind.Warehouse, WarehouseX));
        Ok(await Access.AssignRoleAsync(super.Id, company, companyUser.Id, review, ScopeKind.Company, null));

        Assert.True(await Allowed(branchUser, company, DocApprove, ScopeTarget.Branch(BranchA)));
        Assert.False(await Allowed(branchUser, company, DocApprove, ScopeTarget.Branch(BranchB)));
        Assert.False(await Allowed(branchUser, company, DocApprove, ScopeTarget.Company));
        Assert.False(await Allowed(branchUser, company, DocPost, ScopeTarget.Branch(BranchA)));
        Assert.True(await Allowed(warehouseUser, company, StockView, ScopeTarget.Warehouse(WarehouseX)));
        Assert.False(await Allowed(warehouseUser, company, StockView, ScopeTarget.Warehouse(WarehouseY)));
        Assert.False(await Allowed(warehouseUser, company, DocView, ScopeTarget.Branch(BranchA)));
        Assert.True(await Allowed(companyUser, company, DocView, ScopeTarget.Branch(BranchA)));
        Assert.True(await Allowed(companyUser, company, DocView, ScopeTarget.Branch(BranchB)));
        Assert.True(await Allowed(companyUser, company, DocView));
        Assert.False(await Allowed(companyUser, company, StockView, ScopeTarget.Warehouse(WarehouseX)));

        // invalid scopes are refused
        Fails(await Access.AssignRoleAsync(super.Id, company, companyUser.Id, stock, ScopeKind.Branch, null), AccessStatus.Invalid, "invalid_scope");
        Fails(await Access.AssignRoleAsync(super.Id, company, companyUser.Id, stock, ScopeKind.Warehouse, Guid.Empty), AccessStatus.Invalid, "invalid_scope");
        Fails(await Access.AssignRoleAsync(super.Id, company, companyUser.Id, stock, ScopeKind.Company, WarehouseX), AccessStatus.Invalid, "invalid_scope");
        Fails(await Access.AssignRoleAsync(super.Id, company, companyUser.Id, stock, (ScopeKind)9, WarehouseX), AccessStatus.Invalid, "invalid_scope");
        Assert.Equal(3L, await AuditCount(company, AccessEvent.RoleAssigned) - 1);                       // the NewSuper seed assignment is SQL, bootstrap is audited
    }

    [PostgresFact]
    public async Task Scope_is_part_of_what_an_actor_may_grant()
    {
        await StartAccess();
        var company = await NewCompany();
        var super = await NewSuper(company);
        var review = await SeedRole(company, "Doc reviewer", DocView, DocApprove);
        var holder = await NewMember(company, super, "holder");
        Ok(await Access.AssignRoleAsync(super.Id, company, holder.Id, PresetRoles.AccessManagerId, ScopeKind.Company, null));   // may assign roles ...
        Ok(await Access.AssignRoleAsync(super.Id, company, holder.Id, review, ScopeKind.Branch, BranchA));                       // ... but holds doc permissions on branch A only
        var target = await NewMember(company, super, "target");

        Fails(await Access.AssignRoleAsync(holder.Id, company, target.Id, review, ScopeKind.Company, null), AccessStatus.Forbidden, "cannot_grant_permission");
        Fails(await Access.AssignRoleAsync(holder.Id, company, target.Id, review, ScopeKind.Branch, BranchB), AccessStatus.Forbidden, "cannot_grant_permission");
        // Branch-level permissions are inert under a warehouse assignment: granting them there escalates nothing and confers nothing.
        Ok(await Access.AssignRoleAsync(holder.Id, company, target.Id, review, ScopeKind.Warehouse, WarehouseX));
        Assert.False(await Allowed(target, company, DocView, ScopeTarget.Branch(BranchA)));
        Assert.False(await Allowed(target, company, DocView, ScopeTarget.Warehouse(WarehouseX)));
    }

    [PostgresFact]
    public async Task Actors_can_grant_within_their_own_scope_and_revoke_only_what_they_hold()
    {
        await StartAccess();
        var company = await NewCompany();
        var super = await NewSuper(company);
        var review = await SeedRole(company, "Doc reviewer", DocView, DocApprove);
        var holder = await NewMember(company, super, "holder");
        Ok(await Access.AssignRoleAsync(super.Id, company, holder.Id, PresetRoles.AccessManagerId, ScopeKind.Company, null));
        Ok(await Access.AssignRoleAsync(super.Id, company, holder.Id, review, ScopeKind.Branch, BranchA));
        var target = await NewMember(company, super, "target");

        var granted = Ok(await Access.AssignRoleAsync(holder.Id, company, target.Id, review, ScopeKind.Branch, BranchA));
        Assert.True(await Allowed(target, company, DocView, ScopeTarget.Branch(BranchA)));
        var other = Ok(await Access.AssignRoleAsync(super.Id, company, target.Id, review, ScopeKind.Branch, BranchB));
        Fails(await Access.RevokeAssignmentAsync(holder.Id, company, other.Id), AccessStatus.Forbidden, "target_has_higher_privileges"); // holder has no authority on branch B
        Ok(await Access.RevokeAssignmentAsync(holder.Id, company, granted.Id));
        Assert.False(await Allowed(target, company, DocView, ScopeTarget.Branch(BranchA)));
        Assert.True(await Allowed(target, company, DocView, ScopeTarget.Branch(BranchB)));
    }

    [PostgresFact]
    public async Task Privilege_escalation_through_roles_and_assignments_is_refused()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var manager = await NewMember(company, admin, "manager");
        Ok(await Access.AssignRoleAsync(admin.Id, company, manager.Id, PresetRoles.AccessManagerId, ScopeKind.Company, null));
        var victim = await NewMember(company, admin, "victim");

        // an access manager cannot hand out the administrator role (they lack roles.create/update/delete and audit.view)
        Fails(await Access.AssignRoleAsync(manager.Id, company, victim.Id, PresetRoles.CompanyAdministratorId, ScopeKind.Company, null), AccessStatus.Forbidden, "cannot_grant_permission");
        Fails(await Access.AssignRoleAsync(manager.Id, company, manager.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null), AccessStatus.Forbidden, "cannot_modify_self");
        Fails(await Access.AssignRoleAsync(admin.Id, company, admin.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null), AccessStatus.Forbidden, "cannot_modify_self");
        // cannot create roles at all without roles.create
        Fails(await Access.CreateRoleAsync(manager.Id, company, "sneaky", [Codes.RolesView]), AccessStatus.Forbidden, "forbidden");

        // a role editor may only shape roles within what they hold
        var editor = await NewMember(company, admin, "editor");
        var editorRole = Ok(await Access.CreateRoleAsync(admin.Id, company, "Role editor", [Codes.RolesView, Codes.RolesCreate, Codes.RolesUpdate]));
        Ok(await Access.AssignRoleAsync(admin.Id, company, editor.Id, editorRole.Id, ScopeKind.Company, null));
        Fails(await Access.CreateRoleAsync(editor.Id, company, "grab", [Codes.MembersDelete]), AccessStatus.Forbidden, "cannot_grant_permission");
        var mine = Ok(await Access.CreateRoleAsync(editor.Id, company, "subset", [Codes.RolesView]));
        Fails(await Access.SetRolePermissionsAsync(editor.Id, company, mine.Id, [Codes.RolesView, Codes.AuditView]), AccessStatus.Forbidden, "cannot_grant_permission"); // adding power they lack
        var powerful = Ok(await Access.CreateRoleAsync(admin.Id, company, "powerful", [Codes.MembersDelete]));
        Fails(await Access.SetRolePermissionsAsync(editor.Id, company, powerful.Id, []), AccessStatus.Forbidden, "cannot_grant_permission");   // reshaping a role carrying power they lack
        Fails(await Access.SetRolePermissionsAsync(editor.Id, company, editorRole.Id, [Codes.RolesView, Codes.RolesCreate, Codes.RolesUpdate, Codes.RolesDelete]), AccessStatus.Forbidden, "cannot_grant_permission"); // editing their own role to add power
        Assert.False(await Allowed(editor, company, Codes.RolesDelete));
        // an editor that holds the permissions may change a role they own the power of
        Assert.Equal([Codes.RolesView], Ok(await Access.SetRolePermissionsAsync(editor.Id, company, mine.Id, [Codes.RolesView])).Permissions);

        // nobody was escalated
        Assert.False(await Allowed(victim, company, Codes.MembersView));
        Assert.Empty((await Access.GetEffectivePermissionsAsync(victim.Id, company)).Grants);
    }

    [PostgresFact]
    public async Task Assignments_of_another_company_cannot_be_found_or_revoked()
    {
        await StartAccess();
        var a = await NewCompany();
        var b = await NewCompany();
        var adminA = await NewAdmin(a, "adminA");
        var adminB = await NewAdmin(b, "adminB");
        var userB = await NewMember(b, adminB);
        var inB = Ok(await Access.AssignRoleAsync(adminB.Id, b, userB.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));

        Fails(await Access.RevokeAssignmentAsync(adminA.Id, a, inB.Id), AccessStatus.NotFound, "assignment_not_found");
        Fails(await Access.AssignRoleAsync(adminA.Id, a, userB.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null), AccessStatus.NotFound, "member_not_found"); // not a member of A
        Fails(await Access.RevokeMembershipAsync(adminA.Id, a, userB.Id), AccessStatus.NotFound, "member_not_found");
        Assert.Empty(Ok(await Access.ListAssignmentsAsync(adminA.Id, a, userB.Id)));
        Assert.True(await Allowed(userB, b, Codes.AuditView));
    }

    [PostgresFact]
    public async Task Assignments_require_an_active_member_target()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var outsider = await NewPerson("outsider");
        var gone = await NewMember(company, admin, "gone");
        Ok(await Access.RevokeMembershipAsync(admin.Id, company, gone.Id));

        Fails(await Access.AssignRoleAsync(admin.Id, company, outsider.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null), AccessStatus.NotFound, "member_not_found");
        Fails(await Access.AssignRoleAsync(admin.Id, company, gone.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null), AccessStatus.NotFound, "member_not_found");
        Fails(await Access.AssignRoleAsync(admin.Id, company, Guid.NewGuid(), PresetRoles.AccessAuditorId, ScopeKind.Company, null), AccessStatus.NotFound, "member_not_found");
    }
}

public class AccessRevocationTests : AccessTestBase
{
    private async Task<(Guid Company, UserRecord Admin, UserRecord User, Guid Session)> Setup()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewMember(company, admin);
        Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var session = await LoginOk(user.Login);
        Assert.True(await Access.SelectCompanyAsync(session.SessionId, user.Id, company));
        Assert.True(await Allowed(user, company, Codes.AuditView));
        return (company, admin, user, session.SessionId);
    }

    [PostgresFact]
    public async Task Revoking_the_assignment_takes_effect_for_a_live_session_without_a_new_login()
    {
        var (company, admin, user, session) = await Setup();
        var assignment = Ok(await Access.ListAssignmentsAsync(admin.Id, company, user.Id)).Single();

        Ok(await Access.RevokeAssignmentAsync(admin.Id, company, assignment.Id));

        Assert.NotNull(await Access.GetSessionContextAsync(session));                  // still the same, valid session ...
        Assert.False(await Allowed(user, company, Codes.AuditView));                    // ... but no longer authorized
        Fails(await Access.ListAuditAsync(user.Id, company), AccessStatus.Forbidden);
    }

    [PostgresFact]
    public async Task Disabling_or_locking_the_user_removes_authorization_and_enabling_restores_only_what_was_granted()
    {
        var (company, admin, user, _) = await Setup();

        await Auth.SetUserStateAsync(admin.Id, user.Id, UserState.Disabled);
        Assert.False(await Allowed(user, company, Codes.AuditView));
        Assert.Empty(await Access.ListAccessibleCompaniesAsync(user.Id));
        Fails(await Access.ListAuditAsync(user.Id, company), AccessStatus.Forbidden);

        await Auth.SetUserStateAsync(admin.Id, user.Id, UserState.Locked);
        Assert.False(await Allowed(user, company, Codes.AuditView));

        await Auth.SetUserStateAsync(admin.Id, user.Id, UserState.Active);
        Assert.True(await Allowed(user, company, Codes.AuditView));
        Assert.False(await Allowed(user, company, Codes.RolesCreate));
    }

    [PostgresFact]
    public async Task Session_context_disappears_when_the_session_is_revoked_or_the_user_disabled()
    {
        var (_, admin, user, session) = await Setup();
        Assert.NotNull(await Access.GetSessionContextAsync(session));
        await Auth.SetUserStateAsync(admin.Id, user.Id, UserState.Disabled);                // also revokes sessions administratively
        Assert.Null(await Access.GetSessionContextAsync(session));
        Assert.Null(await Access.GetSessionContextAsync(Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task A_company_that_is_no_longer_ready_grants_nothing()
    {
        var (company, _, user, _) = await Setup();
        await Exec($"UPDATE {Co.Table} SET {Co.ProvisioningState}=3 WHERE {Co.Id}=@c", ("c", company));
        Assert.False(await Allowed(user, company, Codes.AuditView));
        Assert.Empty(await Access.ListAccessibleCompaniesAsync(user.Id));
    }

    [PostgresFact]
    public async Task Company_selection_cannot_be_forged_through_another_users_session()
    {
        var (company, admin, user, session) = await Setup();
        var other = await NewPerson("other");
        Assert.False(await Access.SelectCompanyAsync(session, other.Id, company));        // session belongs to someone else
        Assert.True(await Access.SelectCompanyAsync(session, user.Id, null));              // clearing works
        Assert.Null((await Access.GetSessionContextAsync(session))!.CompanyId);
        Assert.False(await Access.SelectCompanyAsync(Guid.NewGuid(), user.Id, company));   // unknown session
        _ = admin;
    }
}

public class AccessAuditTests : AccessTestBase
{
    [PostgresFact]
    public async Task Every_successful_change_is_audited_with_actor_target_role_and_detail_and_nothing_else_is()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewMember(company, admin);
        var role = Ok(await Access.CreateRoleAsync(admin.Id, company, "R", [Codes.RolesView]));
        Ok(await Access.SetRolePermissionsAsync(admin.Id, company, role.Id, [Codes.AuditView]));
        var assignment = Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, role.Id, ScopeKind.Company, null));
        Ok(await Access.RevokeAssignmentAsync(admin.Id, company, assignment.Id));
        Ok(await Access.DeactivateRoleAsync(admin.Id, company, role.Id));
        Ok(await Access.RevokeMembershipAsync(admin.Id, company, user.Id));
        // denied / failed attempts must leave no audit entry
        Fails(await Access.CreateRoleAsync(user.Id, company, "no", []), AccessStatus.Forbidden);
        Fails(await Access.GrantMembershipAsync(admin.Id, company, "nobody.here"), AccessStatus.NotFound);

        var entries = Ok(await Access.ListAuditAsync(admin.Id, company)).Reverse().ToList();
        Assert.Equal(
            [AccessEvent.MembershipGranted, AccessEvent.RoleAssigned,                                   // offline bootstrap of the administrator
             AccessEvent.MembershipGranted, AccessEvent.RoleCreated, AccessEvent.RolePermissionsChanged, AccessEvent.RoleAssigned,
             AccessEvent.RoleAssignmentRevoked, AccessEvent.RoleDeactivated, AccessEvent.MembershipRevoked],
            entries.Select(e => e.Event));
        Assert.All(entries.Take(2), e => Assert.Null(e.ActorUserId));                                   // offline bootstrap has no actor user
        Assert.Contains("bootstrap", entries[1].Detail);
        Assert.All(entries.Skip(2), e => Assert.Equal(admin.Id, e.ActorUserId));
        Assert.Equal(user.Id, entries[2].TargetUserId);
        Assert.Equal(role.Id, entries[3].RoleId);
        Assert.Equal("added: access.audit.view; removed: access.roles.view", entries[4].Detail);
        Assert.Equal("scope: company", entries[5].Detail);
        Assert.Equal(entries.Count, Ok(await Access.ListAuditAsync(admin.Id, company)).Count);
        Assert.Equal(entries.Count, (int)await AuditCount(company));
        Assert.True(entries.Zip(entries.Skip(1)).All(p => p.First.Id < p.Second.Id));
    }

    [PostgresFact]
    public async Task Audit_is_company_isolated_permission_protected_and_paged()
    {
        await StartAccess();
        var a = await NewCompany();
        var b = await NewCompany();
        var adminA = await NewAdmin(a, "adminA");
        var adminB = await NewAdmin(b, "adminB");
        for (var i = 0; i < 5; i++) Ok(await Access.CreateRoleAsync(adminA.Id, a, "role" + i, []));
        var plain = await NewMember(a, adminA);

        Fails(await Access.ListAuditAsync(plain.Id, a), AccessStatus.Forbidden);
        Fails(await Access.ListAuditAsync(adminB.Id, a), AccessStatus.Forbidden);
        var bEntries = Ok(await Access.ListAuditAsync(adminB.Id, b));
        Assert.Equal(2, bEntries.Count);                                              // only B's own bootstrap (membership + role)
        var page1 = Ok(await Access.ListAuditAsync(adminA.Id, a, 3));
        var page2 = Ok(await Access.ListAuditAsync(adminA.Id, a, 3, page1[^1].Id));
        Assert.Equal(3, page1.Count);
        Assert.True(page2.Count >= 3 && page2[0].Id < page1[^1].Id);
        Assert.True(Ok(await Access.ListAuditAsync(adminA.Id, a, 100_000)).Count <= AccessService.MaxPageSize);
        Assert.Single(Ok(await Access.ListAuditAsync(adminA.Id, a, 0)));                // limits are clamped, never unbounded
    }
}


public class OfflineAdministrationTests : AccessTestBase
{
    [PostgresFact]
    public async Task Bootstrap_grants_membership_and_the_administrator_role_is_audited_and_repeatable()
    {
        await StartAccess();
        var company = await NewCompany();
        var user = await NewPerson("first");

        Assert.Equal(AccessService.BootstrapStatus.Granted, await Access.BootstrapCompanyAdministratorAsync(user.Login, company));
        Assert.Equal(AccessService.BootstrapStatus.AlreadyAdministrator, await Access.BootstrapCompanyAdministratorAsync(user.Login.ToUpperInvariant(), company));

        Assert.Equal([company], await Access.ListAccessibleCompaniesAsync(user.Id));
        foreach (var code in PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions) Assert.True(await Allowed(user, company, code), code);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId}=@u AND {Ra.RevokedAtUtc} IS NULL", ("u", user.Id)));
        Assert.Equal(1L, await AuditCount(company, AccessEvent.RoleAssigned));
    }

    [PostgresFact]
    public async Task Bootstrap_repairs_a_revoked_administrator_and_refuses_unknown_disabled_or_missing_targets()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var second = await NewAdmin(company, "second");
        Ok(await Access.RevokeMembershipAsync(admin.Id, company, second.Id));

        Assert.Equal(AccessService.BootstrapStatus.Granted, await Access.BootstrapCompanyAdministratorAsync(second.Login, company)); // recovery path
        Assert.True(await Allowed(second, company, Codes.RolesCreate));

        var disabled = await NewPerson("off");
        await Auth.SetUserStateAsync(admin.Id, disabled.Id, UserState.Disabled);
        Assert.Equal(AccessService.BootstrapStatus.UserNotFound, await Access.BootstrapCompanyAdministratorAsync(disabled.Login, company));
        Assert.Equal(AccessService.BootstrapStatus.UserNotFound, await Access.BootstrapCompanyAdministratorAsync("nobody.at.all", company));
        Assert.Equal(AccessService.BootstrapStatus.UserNotFound, await Access.BootstrapCompanyAdministratorAsync("", company));
    }

    [PostgresFact]
    public async Task Concurrent_bootstraps_create_exactly_one_membership_and_one_assignment()
    {
        await StartAccess();
        var company = await NewCompany();
        var user = await NewPerson("racer");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Access.BootstrapCompanyAdministratorAsync(user.Login, company)));

        Assert.Equal(1, results.Count(r => r == AccessService.BootstrapStatus.Granted));
        Assert.Equal(5, results.Count(r => r == AccessService.BootstrapStatus.AlreadyAdministrator));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId}=@u AND {Ra.RevokedAtUtc} IS NULL", ("u", user.Id)));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Mem.Table} WHERE {Mem.UserId}=@u", ("u", user.Id)));
    }
}

public class ConcurrentAccessChangeTests : AccessTestBase
{
    [PostgresFact]
    public async Task Two_administrators_revoking_each_other_concurrently_cannot_both_succeed()
    {
        await StartAccess();
        for (var round = 0; round < 8; round++)
        {
            var company = await NewCompany();
            var a = await NewAdmin(company, "alpha");
            var b = await NewAdmin(company, "beta");

            var results = await Task.WhenAll(
                Access.RevokeMembershipAsync(a.Id, company, b.Id),
                Access.RevokeMembershipAsync(b.Id, company, a.Id));

            Assert.Equal(1, results.Count(r => r.IsOk));
            Assert.Equal(1, results.Count(r => r.Status == AccessStatus.Forbidden));       // the loser had already lost its authority inside the lock
            Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Mem.Table} WHERE {Mem.CompanyId}=@c AND {Mem.RevokedAtUtc} IS NULL", ("c", company)));
            Assert.Equal(1L, await AuditCount(company, AccessEvent.MembershipRevoked));
        }
    }

    [PostgresFact]
    public async Task Revoking_an_assignment_while_the_holder_is_changing_permissions_is_serialized()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var editor = await NewMember(company, admin, "editor");
        var editorRole = Ok(await Access.CreateRoleAsync(admin.Id, company, "Editor", [Codes.RolesView, Codes.RolesCreate, Codes.RolesUpdate]));
        var assignment = Ok(await Access.AssignRoleAsync(admin.Id, company, editor.Id, editorRole.Id, ScopeKind.Company, null));
        var target = Ok(await Access.CreateRoleAsync(admin.Id, company, "Target", [Codes.RolesView]));

        var revoke = Access.RevokeAssignmentAsync(admin.Id, company, assignment.Id);
        var edits = Enumerable.Range(0, 6).Select(i => Access.SetRolePermissionsAsync(editor.Id, company, target.Id, i % 2 == 0 ? [Codes.RolesView] : [])).ToArray();
        await Task.WhenAll(edits.Cast<Task>().Append(revoke));

        Assert.True(revoke.Result.IsOk);
        Assert.All(edits, e => Assert.True(e.Result.IsOk || e.Result.Status == AccessStatus.Forbidden));
        // after the revocation committed no further edit may succeed
        Fails(await Access.SetRolePermissionsAsync(editor.Id, company, target.Id, [Codes.RolesView]), AccessStatus.Forbidden, "forbidden");
        var count = await AuditCount(company, AccessEvent.RolePermissionsChanged);
        Assert.Equal(edits.Count(e => e.Result.IsOk), count);                  // exactly the successful edits were audited
    }

    [PostgresFact]
    public async Task Concurrent_duplicate_assignments_create_exactly_one()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewMember(company, admin);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Access.AssignRoleAsync(admin.Id, company, user.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null)));

        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(7, results.Count(r => r.Status == AccessStatus.Conflict));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId}=@u AND {Ra.RevokedAtUtc} IS NULL", ("u", user.Id)));
        Assert.Equal(1L, await AuditCount(company, AccessEvent.RoleAssigned) - 1);
    }

    [PostgresFact]
    public async Task Concurrent_role_creation_with_the_same_name_and_membership_grants_are_serialized()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewPerson("joiner");

        var roles = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Access.CreateRoleAsync(admin.Id, company, "Same Name", [])));
        var members = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Access.GrantMembershipAsync(admin.Id, company, user.Login)));

        Assert.Equal(1, roles.Count(r => r.IsOk));
        Assert.Equal(5, roles.Count(r => r.Status == AccessStatus.Conflict));
        Assert.Equal(1, members.Count(r => r.IsOk));
        Assert.Equal(5, members.Count(r => r.Status == AccessStatus.Conflict));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Ro.Table} WHERE lower({Ro.Name})='same name'"));
    }

    [PostgresFact]
    public async Task Concurrent_changes_in_different_companies_do_not_block_or_leak_into_each_other()
    {
        await StartAccess();
        var companies = new List<(Guid Id, UserRecord Admin)>();
        for (var i = 0; i < 4; i++)
        {
            var id = await NewCompany();
            companies.Add((id, await NewAdmin(id, "adm")));
        }

        var work = companies.SelectMany(c => Enumerable.Range(0, 5).Select(i => Access.CreateRoleAsync(c.Admin.Id, c.Id, "role" + i, [Codes.RolesView]))).ToList();
        await Task.WhenAll(work);

        Assert.All(work, w => Assert.True(w.Result.IsOk));
        foreach (var (id, admin) in companies)
            Assert.Equal(5, Ok(await Access.ListRolesAsync(admin.Id, id)).Count(r => !r.IsPreset));
        Assert.Equal(20L, await Scalar<long>($"SELECT count(*) FROM {Ro.Table} WHERE {Ro.CompanyId} IS NOT NULL"));
    }
}

/// <summary>Stands in for the future per-company branch/warehouse lookup: only these ids "exist".</summary>
public sealed class KnownScopeValidator : IScopeTargetValidator
{
    public Task<bool> ExistsAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct) => Task.FromResult(
        (kind == ScopeKind.Branch && (id == AccessTestBase.KnownBranchA || id == AccessTestBase.KnownBranchB)) ||
        (kind == ScopeKind.Warehouse && (id == AccessTestBase.KnownWarehouseX || id == AccessTestBase.KnownWarehouseY)));
}

public sealed class ThrowingScopeValidator : IScopeTargetValidator
{
    public Task<bool> ExistsAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct) => throw new InvalidOperationException("lookup unavailable");
}
