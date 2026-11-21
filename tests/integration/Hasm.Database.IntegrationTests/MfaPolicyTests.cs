using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using Ra = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using Ro = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using Pm = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaPolicy;
using Pmr = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaPolicyRole;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 4.1 slice S3b-1b against real PostgreSQL: the company MFA policy service (storage, permission, validation, the lock-out guard, audit and evaluation). Disposable cluster only.</summary>
public class MfaPolicyTests : AccessTestBase
{
    private MfaPolicyService Policies = null!;

    private async Task Setup()
    {
        await StartAccess();
        Policies = new MfaPolicyService(Options.BuildSystemConnectionString(admin: true), Clock, Audit, Access, Store);
    }

    public override async Task DisposeAsync()
    {
        if (Policies is not null) await Policies.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SetMfaState(UserRecord user, int state) => Exec($"UPDATE {U.Table} SET {M.State} = @s WHERE {U.Id} = @i", ("s", (short)state), ("i", user.Id));
    private Task<long> PolicyEvents() => Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.EventType} = @t", ("t", MfaAuditEvents.PolicyChanged));

    private Task<long> Revision(Guid company) => Scalar<long>($"SELECT coalesce((SELECT {Pm.Revision} FROM {Pm.Table} WHERE {Pm.CompanyId} = @c), 0)", ("c", company));
    private Task<short> StoredKind(Guid company) => Scalar<short>($"SELECT {Pm.Policy} FROM {Pm.Table} WHERE {Pm.CompanyId} = @c", ("c", company));
    private Task<long> StoredRoles(Guid company) => Scalar<long>($"SELECT count(*) FROM {Pmr.Table} WHERE {Pmr.CompanyId} = @c", ("c", company));

    /// <summary>The policy change details of the company in audit order (the "before -> after" text the service writes).</summary>
    private async Task<List<string>> PolicyDetails(Guid company)
    {
        var list = new List<string>();
        await using var conn = new Npgsql.NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT {Ev.Detail} FROM {Ev.Table} WHERE {Ev.EventType} = @t AND {Ev.Detail} LIKE @c ORDER BY {Ev.Seq}", conn);
        cmd.Parameters.AddWithValue("t", MfaAuditEvents.PolicyChanged);
        cmd.Parameters.AddWithValue("c", $"company {company:N} %");
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>A change made with the CURRENT revision (what a client that has just read the policy sends); the concurrency tests call the service with an explicit revision.</summary>
    private async Task<AccessResult<CompanyMfaPolicy>> Set(Guid actor, Guid company, CompanyMfaPolicyKind kind, IReadOnlyList<Guid>? roles) =>
        await Policies.SetAsync(actor, company, await Revision(company), kind, roles);

    /// <summary>A company administrator who has an authenticator (so the lock-out guard lets them set any policy).</summary>
    private async Task<UserRecord> EnrolledAdmin(Guid company)
    {
        var admin = await NewAdmin(company, "enrolled");
        await SetMfaState(admin, 2);
        return admin;
    }

    [PostgresFact]
    public async Task A_company_without_a_stored_policy_is_Off_and_only_holders_of_the_view_permission_may_read_it()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var plain = await NewMember(company, admin, "plain");
        var read = Ok(await Policies.GetAsync(admin.Id, company));
        Assert.Equal(CompanyMfaPolicyKind.Off, read.Kind);
        Assert.Empty(read.RoleIds);
        Fails(await Policies.GetAsync(plain.Id, company), AccessStatus.Forbidden);                                   // a member without access.mfapolicy.view
        Fails(await Set(plain.Id, company, CompanyMfaPolicyKind.Off, null), AccessStatus.Forbidden);   // and without update
        Assert.False(await Policies.RequiresMfaAsync(plain.Id, company));
    }

    [PostgresFact]
    public async Task Setting_the_policy_stores_it_audits_it_and_evaluates_it_for_all_and_for_roles()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await EnrolledAdmin(company);
        var clerk = await NewMember(company, admin, "clerk");
        var other = await NewMember(company, admin, "other");
        var role = await SeedRole(company, "Needs MFA", "company.items.view");
        await SeedAssignment(clerk.Id, company, role);

        var all = Ok(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForAll, null));
        Assert.Equal(CompanyMfaPolicyKind.RequiredForAll, all.Kind);
        Assert.True(await Policies.RequiresMfaAsync(clerk.Id, company));
        Assert.True(await Policies.RequiresMfaAsync(other.Id, company));
        Assert.Equal(1L, await PolicyEvents());

        var roles = Ok(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [role]));
        Assert.Equal([role], roles.RoleIds);
        Assert.True(await Policies.RequiresMfaAsync(clerk.Id, company));                                            // holds a listed role
        Assert.False(await Policies.RequiresMfaAsync(other.Id, company));                                           // does not
        Assert.Equal([role], Ok(await Policies.GetAsync(admin.Id, company)).RoleIds);

        Ok(await Set(admin.Id, company, CompanyMfaPolicyKind.Off, null));
        Assert.False(await Policies.RequiresMfaAsync(clerk.Id, company));
        Assert.Empty(Ok(await Policies.GetAsync(admin.Id, company)).RoleIds);                                       // the list does not outlive the kind
        Assert.Equal(3L, await PolicyEvents());
    }

    [PostgresFact]
    public async Task A_revoked_assignment_no_longer_counts_for_a_roles_policy_and_another_company_is_unaffected()
    {
        await Setup();
        var company = await NewCompany();
        var otherCompany = await NewCompany();
        var admin = await EnrolledAdmin(company);
        var clerk = await NewMember(company, admin, "clerk");
        var role = await SeedRole(company, "Needs MFA", "company.items.view");
        await SeedAssignment(clerk.Id, company, role);
        Ok(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [role]));
        Assert.True(await Policies.RequiresMfaAsync(clerk.Id, company));
        Assert.False(await Policies.RequiresMfaAsync(clerk.Id, otherCompany));                                      // the policy belongs to ONE company
        await Exec($"UPDATE {Ra.Table} SET {Ra.RevokedAtUtc} = now() WHERE {Ra.UserId} = @u", ("u", clerk.Id));
        Assert.False(await Policies.RequiresMfaAsync(clerk.Id, company));
    }

    [PostgresFact]
    public async Task Invalid_requests_are_refused_and_change_nothing()
    {
        await Setup();
        var company = await NewCompany();
        var otherCompany = await NewCompany();
        var admin = await EnrolledAdmin(company);
        var good = await SeedRole(company, "Good", "company.items.view");
        var foreign = await SeedRole(otherCompany, "Foreign", "company.items.view");
        var inactive = await SeedRole(company, "Inactive", "company.items.view");
        await Exec($"UPDATE {Ro.Table} SET {Ro.IsActive} = false WHERE {Ro.Id} = @r", ("r", inactive));

        Fails(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, []), AccessStatus.Invalid, "invalid_request");     // roles kind needs roles
        Fails(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForAll, [good]), AccessStatus.Invalid, "invalid_request");   // the other kinds take none
        Fails(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [foreign]), AccessStatus.Invalid, "unknown_role");
        Fails(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [inactive]), AccessStatus.Invalid, "unknown_role");
        Fails(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [Guid.NewGuid()]), AccessStatus.Invalid, "unknown_role");
        Fails(await Set(admin.Id, company, (CompanyMfaPolicyKind)7, null), AccessStatus.Invalid, "invalid_request");
        Fails(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, Enumerable.Range(0, MfaPolicyService.MaxRoles + 1).Select(_ => Guid.NewGuid()).ToList()), AccessStatus.Invalid, "invalid_request");
        Assert.Equal(CompanyMfaPolicyKind.Off, Ok(await Policies.GetAsync(admin.Id, company)).Kind);
        Assert.Equal(0L, await PolicyEvents());
        // a PRESET role (no company) may be listed
        var preset = Ok(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [PresetRoles.AccessManagerId]));
        Assert.Equal([PresetRoles.AccessManagerId], preset.RoleIds);
    }

    [PostgresFact]
    public async Task The_lock_out_guard_refuses_a_policy_that_would_require_MFA_of_an_administrator_without_an_authenticator()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await NewAdmin(company);                                                                         // MFA state None
        var role = await SeedRole(company, "Others", "company.items.view");
        var adminRole = (await Access.ListRolesAsync(admin.Id, company)).Value!.Single(r => r.PresetKey == PresetRoles.CompanyAdministratorKey).Id;

        Fails(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForAll, null), AccessStatus.Conflict, "mfa_policy_requires_own_enrolment");
        Fails(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [adminRole]), AccessStatus.Conflict, "mfa_policy_requires_own_enrolment");
        Assert.Equal(CompanyMfaPolicyKind.Off, Ok(await Policies.GetAsync(admin.Id, company)).Kind);
        Assert.Equal(0L, await PolicyEvents());

        Ok(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [role]));              // a role the administrator does not hold is fine
        Ok(await Set(admin.Id, company, CompanyMfaPolicyKind.Off, null));                             // and turning it off is always allowed

        await SetMfaState(admin, 2);                                                                                 // once the administrator has an authenticator the same change is allowed
        Ok(await Set(admin.Id, company, CompanyMfaPolicyKind.RequiredForAll, null));
    }

    [PostgresFact]
    public async Task The_company_administrator_preset_holds_the_policy_permissions_and_the_access_manager_does_not()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var manager = await NewMember(company, admin, "manager");
        Ok(await Access.AssignRoleAsync(admin.Id, company, manager.Id, PresetRoles.AccessManagerId, ScopeKind.Company, null));
        Assert.True(await Allowed(admin, company, PermissionCatalog.Codes.MfaPolicyView));
        Assert.True(await Allowed(admin, company, PermissionCatalog.Codes.MfaPolicyUpdate));
        Assert.False(await Allowed(manager, company, PermissionCatalog.Codes.MfaPolicyView));
        Assert.False(await Allowed(manager, company, PermissionCatalog.Codes.MfaPolicyUpdate));
    }

    // ---- optimistic concurrency (review M1 of PR 181)

    [PostgresFact]
    public async Task The_revision_is_0_without_a_policy_1_after_the_first_write_and_a_missing_or_stale_revision_is_refused_and_writes_nothing()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await EnrolledAdmin(company);
        Assert.Equal(0L, Ok(await Policies.GetAsync(admin.Id, company)).Revision);

        Fails(await Policies.SetAsync(admin.Id, company, null, CompanyMfaPolicyKind.RequiredForAll, null), AccessStatus.PreconditionRequired, "precondition_required");
        var early = await Policies.SetAsync(admin.Id, company, 5, CompanyMfaPolicyKind.RequiredForAll, null);        // a revision that does not exist yet
        Fails(early, AccessStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(0L, early.CurrentRevision);
        Assert.Equal(0L, await PolicyEvents());
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Pm.Table}"));

        Assert.Equal(1L, Ok(await Policies.SetAsync(admin.Id, company, 0, CompanyMfaPolicyKind.RequiredForAll, null)).Revision);
        var stale = await Policies.SetAsync(admin.Id, company, 0, CompanyMfaPolicyKind.Off, null);                   // a second "first write": the screen is stale
        Fails(stale, AccessStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(1L, stale.CurrentRevision);
        Assert.Equal((short)CompanyMfaPolicyKind.RequiredForAll, await StoredKind(company));                         // the stale change wrote nothing
        Assert.Equal(1L, await PolicyEvents());

        var second = Ok(await Policies.SetAsync(admin.Id, company, 1, CompanyMfaPolicyKind.Off, null));
        Assert.Equal(2L, second.Revision);
        var read = Ok(await Policies.GetAsync(admin.Id, company));
        Assert.Equal((2L, CompanyMfaPolicyKind.Off), (read.Revision, read.Kind));
        var older = await Policies.SetAsync(admin.Id, company, 1, CompanyMfaPolicyKind.RequiredForAll, null);
        Fails(older, AccessStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(2L, older.CurrentRevision);
        Assert.Equal(["Off -> RequiredForAll", "RequiredForAll -> Off"], (await PolicyDetails(company)).Select(Transition).ToArray());
        // a caller without the permission learns nothing about the revision
        var plain = await NewMember(company, admin, "plain");
        Fails(await Policies.SetAsync(plain.Id, company, 0, CompanyMfaPolicyKind.Off, null), AccessStatus.Forbidden);
    }

    private static string Transition(string detail) => System.Text.RegularExpressions.Regex.Match(detail, @"mfa policy (\w+ -> \w+)").Groups[1].Value;

    [PostgresFact]
    public async Task Two_parallel_first_writes_with_the_same_revision_give_exactly_one_winner_and_the_loser_writes_nothing()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await EnrolledAdmin(company);
        var role = await SeedRole(company, "Needs MFA", "company.items.view");

        // the start barrier holds BOTH racers after they read revision 0 (no row, so nothing is locked) and before either writes: the lost race is deterministic, not probable
        var gate = new StartBarrier(2, TimeSpan.FromSeconds(30));
        Policies.AfterRevisionReadForTests = gate.WaitAsync;
        var results = await Task.WhenAll(
            Task.Run(() => Policies.SetAsync(admin.Id, company, 0, CompanyMfaPolicyKind.RequiredForAll, null)),
            Task.Run(() => Policies.SetAsync(admin.Id, company, 0, CompanyMfaPolicyKind.RequiredForRoles, [role])));
        Policies.AfterRevisionReadForTests = null;
        Assert.Equal(2, gate.Arrived);

        var winner = Assert.Single(results, r => r.IsOk);
        var loser = Assert.Single(results, r => !r.IsOk);
        Fails(loser, AccessStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(1L, loser.CurrentRevision);
        Assert.Equal(1L, winner.Value!.Revision);
        Assert.Equal(1L, await Revision(company));
        Assert.Equal((short)winner.Value.Kind, await StoredKind(company));                                           // the stored policy is the winner's, whole
        Assert.Equal(winner.Value.Kind == CompanyMfaPolicyKind.RequiredForRoles ? 1L : 0L, await StoredRoles(company));   // the loser's role list (or its deletion) did not land
        Assert.Equal([$"Off -> {winner.Value.Kind}"], (await PolicyDetails(company)).Select(Transition).ToArray());  // ONE audit event, and its "before" is the state the winner replaced
    }

    [PostgresFact]
    public async Task Parallel_changes_of_an_existing_policy_at_the_same_revision_give_one_winner_and_the_audit_before_is_the_state_under_the_row_lock()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await EnrolledAdmin(company);
        var role = await SeedRole(company, "Needs MFA", "company.items.view");
        Ok(await Policies.SetAsync(admin.Id, company, 0, CompanyMfaPolicyKind.RequiredForAll, null));
        var expectedChain = new List<string> { "Off -> RequiredForAll" };
        var current = CompanyMfaPolicyKind.RequiredForAll;

        for (var round = 1; round <= 6; round++)
        {
            var revision = (long)round;
            // three writers with the SAME If-Match, each to a different policy; the row lock serializes them, so exactly one sees its revision
            var results = await Task.WhenAll(
                Task.Run(() => Policies.SetAsync(admin.Id, company, revision, CompanyMfaPolicyKind.Off, null)),
                Task.Run(() => Policies.SetAsync(admin.Id, company, revision, CompanyMfaPolicyKind.RequiredForRoles, [role])),
                Task.Run(() => Policies.SetAsync(admin.Id, company, revision, CompanyMfaPolicyKind.RequiredForAll, null)));
            var winner = Assert.Single(results, r => r.IsOk).Value!;
            Assert.Equal(revision + 1, winner.Revision);
            foreach (var lost in results.Where(r => !r.IsOk))
            {
                Fails(lost, AccessStatus.PreconditionFailed, "revision_conflict");
                Assert.Equal(revision + 1, lost.CurrentRevision);
            }
            Assert.Equal(revision + 1, await Revision(company));
            Assert.Equal((short)winner.Kind, await StoredKind(company));
            Assert.Equal(winner.Kind == CompanyMfaPolicyKind.RequiredForRoles ? 1L : 0L, await StoredRoles(company));
            expectedChain.Add($"{current} -> {winner.Kind}");                                                        // "before" is the previous winner, never a racer's stale read
            current = winner.Kind;
        }
        Assert.Equal(expectedChain, (await PolicyDetails(company)).Select(Transition).ToList());                     // one event per revision, an unbroken before/after chain
        Assert.Equal(7L, await PolicyEvents());
    }
}
