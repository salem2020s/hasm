using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using Users = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 2.3C-1: no supported operation (access revocation, or disabling/locking an identity) may leave a Ready company without a
/// usable Company Administrator or the system without an Active identity administrator, including under concurrency.
/// </summary>
public class AdministratorContinuityTests : AccessTestBase
{
    private static AuthService.SetStateStatus Changed => AuthService.SetStateStatus.Changed;
    private static AuthService.SetStateStatus LastAdmin => AuthService.SetStateStatus.LastCompanyAdministrator;

    private async Task<UserState> StateOf(UserRecord user) => (UserState)await Scalar<short>($"SELECT {Users.State} FROM {Users.Table} WHERE {Users.Id} = @i", ("i", user.Id));

    private async Task MakeAdminOf(UserRecord user, Guid company) =>
        Assert.Equal(AccessService.BootstrapStatus.Granted, await Access.BootstrapCompanyAdministratorAsync(user.Login, company));

    [PostgresFact]
    public async Task The_only_usable_administrator_cannot_be_disabled_or_locked_and_nothing_changes()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var actor = await NewPerson("actor");
        var session = await LoginOk(admin.Login);
        var audit = await AuditCount(company);

        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, admin.Id, UserState.Disabled));
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, admin.Id, UserState.Locked));

        Assert.Equal(UserState.Active, await StateOf(admin));
        Assert.NotNull(await Auth.AuthenticateAsync(session.Token));       // sessions are not revoked by a refused change
        Assert.Equal(audit, await AuditCount(company));
    }

    [PostgresFact]
    public async Task With_two_administrators_one_can_be_disabled_and_the_survivor_is_then_protected_until_the_other_returns()
    {
        await StartAccess();
        var company = await NewCompany();
        var first = await NewAdmin(company, "first");
        var second = await NewAdmin(company, "second");
        var actor = await NewPerson("actor");

        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, first.Id, UserState.Disabled));
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, second.Id, UserState.Disabled));
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, second.Id, UserState.Locked));

        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, first.Id, UserState.Active));      // re-enabling is never blocked
        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, second.Id, UserState.Locked));     // the first one is usable again
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, first.Id, UserState.Disabled));
    }

    [PostgresFact]
    public async Task An_already_unusable_administrator_can_move_between_non_active_states()
    {
        await StartAccess();
        var company = await NewCompany();
        var live = await NewAdmin(company, "live");
        var sleeper = await NewAdmin(company, "sleeper");
        var actor = await NewPerson("actor");

        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, sleeper.Id, UserState.Disabled));
        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, sleeper.Id, UserState.Locked));       // does not reduce the usable count
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, live.Id, UserState.Disabled));
    }

    [PostgresFact]
    public async Task Administrators_of_other_companies_do_not_count_and_every_company_is_protected()
    {
        await StartAccess();
        var x = await NewCompany();
        var y = await NewCompany();
        var onlyInX = await NewAdmin(x, "xadmin");
        var onlyInY = await NewAdmin(y, "yadmin");
        var actor = await NewPerson("actor");

        // another company's administrator never satisfies this company's requirement
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, onlyInX.Id, UserState.Disabled));
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, onlyInY.Id, UserState.Disabled));

        // a user who administers both is blocked while either company depends on them
        var both = await NewAdmin(x, "both");
        await MakeAdminOf(both, y);
        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, both.Id, UserState.Disabled));       // x and y each keep their own administrator
        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, both.Id, UserState.Active));
    }

    [PostgresFact]
    public async Task A_user_who_is_the_sole_administrator_of_any_single_company_is_protected_even_if_redundant_elsewhere()
    {
        await StartAccess();
        var x = await NewCompany();
        var y = await NewCompany();
        var shared = await NewAdmin(x, "shared");
        await MakeAdminOf(shared, y);
        var yOther = await NewAdmin(y, "yother");               // y has a second administrator, x does not
        var actor = await NewPerson("actor");

        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, shared.Id, UserState.Disabled));
        Assert.Equal(UserState.Active, await StateOf(shared));

        var xOther = await NewAdmin(x, "xother");               // now both companies have a second administrator
        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, shared.Id, UserState.Disabled));
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, xOther.Id, UserState.Disabled));
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, yOther.Id, UserState.Disabled));
    }

    [PostgresFact]
    public async Task Users_who_are_not_usable_administrators_are_never_blocked()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var member = await NewMember(company, admin, "plain");
        var narrow = await NewMember(company, admin, "narrow");
        await SeedAssignment(narrow.Id, company, await SeedRole(company, "Custom", PermissionCatalog.Default.All.Select(p => p.Code).ToArray()));
        var outsider = await NewPerson("outsider");
        var actor = await NewPerson("actor");

        foreach (var u in new[] { member, narrow, outsider })
            Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, u.Id, UserState.Disabled));
    }

    [PostgresFact]
    public async Task A_company_that_already_has_no_usable_administrator_does_not_block_cleanup_and_offline_recovery_still_works()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var actor = await NewPerson("actor");
        await Exec($"UPDATE {Users.Table} SET {Users.State} = 1 WHERE {Users.Id} = @i", ("i", admin.Id));    // legacy/unsupported path: the company has none

        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, admin.Id, UserState.Locked));

        var rescuer = await NewPerson("rescuer");
        Assert.Equal(AccessService.BootstrapStatus.Granted, await Access.BootstrapCompanyAdministratorAsync(rescuer.Login, company));
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, rescuer.Id, UserState.Disabled));     // the recovered administrator is protected again
    }

    [PostgresFact]
    public async Task Administrators_of_a_company_that_is_not_ready_are_not_counted()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var actor = await NewPerson("actor");
        await Exec($"UPDATE {Co.Table} SET {Co.ProvisioningState} = @s WHERE {Co.Id} = @i", ("s", (short)ProvisioningState.Failed), ("i", company));

        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, admin.Id, UserState.Disabled));
    }

    [PostgresFact]
    public async Task Revoking_membership_or_assignment_and_disabling_use_the_same_rule()
    {
        await StartAccess();
        var company = await NewCompany();
        var a = await NewAdmin(company, "adma");
        var b = await NewAdmin(company, "admb");
        var actor = await NewPerson("actor");
        var op = await NewMember(company, a, "op");
        await SeedAssignment(op.Id, company, await SeedRole(company, "All", PermissionCatalog.Default.All.Select(p => p.Code).ToArray()));

        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, a.Id, UserState.Disabled));
        Fails(await Access.RevokeMembershipAsync(op.Id, company, b.Id), AccessStatus.Conflict, "last_administrator");
        Assert.Equal(LastAdmin, await Auth.SetUserStateAsync(actor.Id, b.Id, UserState.Disabled));
    }

    // ------------------------------------------------------------------ concurrency

    [PostgresFact]
    public async Task Two_administrators_disabled_concurrently_never_leave_the_company_without_one()
    {
        await StartAccess();
        for (var round = 0; round < 6; round++)
        {
            var company = await NewCompany();
            var a = await NewAdmin(company, "ca" + round);
            var b = await NewAdmin(company, "cb" + round);
            var op1 = await NewPerson("op1_");
            var op2 = await NewPerson("op2_");

            var results = await Task.WhenAll(
                Auth.SetUserStateAsync(op1.Id, a.Id, UserState.Disabled),
                Auth.SetUserStateAsync(op2.Id, b.Id, UserState.Locked));

            Assert.Equal(1, results.Count(r => r == Changed));
            Assert.Equal(1, results.Count(r => r == LastAdmin));
            var survivors = (await StateOf(a) == UserState.Active ? 1 : 0) + (await StateOf(b) == UserState.Active ? 1 : 0);
            Assert.Equal(1, survivors);
        }
    }

    [PostgresFact]
    public async Task Disabling_one_administrator_while_another_is_revoked_cannot_remove_both()
    {
        await StartAccess();
        for (var round = 0; round < 6; round++)
        {
            var company = await NewCompany();
            var a = await NewAdmin(company, "ra" + round);
            var b = await NewAdmin(company, "rb" + round);
            var op = await NewMember(company, a, "rop" + round);
            await SeedAssignment(op.Id, company, await SeedRole(company, "All " + round, PermissionCatalog.Default.All.Select(p => p.Code).ToArray()));
            var actor = await NewPerson("ractor");

            var disable = Auth.SetUserStateAsync(actor.Id, a.Id, UserState.Disabled);
            var revoke = Access.RevokeMembershipAsync(op.Id, company, b.Id);
            await Task.WhenAll(disable, revoke);

            var disabled = disable.Result == Changed;
            var revoked = revoke.Result.IsOk;
            Assert.True(disabled ^ revoked, $"exactly one of the two removals may succeed (disabled={disabled}, revoked={revoked})");
            Assert.Equal(1L, await Scalar<long>(
                $"SELECT count(*) FROM {SystemSchema.UserCompanyAccess.Table} m JOIN {Users.Table} u ON u.{Users.Id} = m.{SystemSchema.UserCompanyAccess.UserId} " +
                $"WHERE m.{SystemSchema.UserCompanyAccess.CompanyId} = @c AND m.{SystemSchema.UserCompanyAccess.RevokedAtUtc} IS NULL AND u.{Users.State} = 0 AND u.{Users.Id} IN (@a, @b)",
                ("c", company), ("a", a.Id), ("b", b.Id)));
        }
    }

    [PostgresFact]
    public async Task Concurrent_changes_over_overlapping_company_sets_neither_deadlock_nor_lose_the_last_administrator()
    {
        await StartAccess();
        var x = await NewCompany();
        var y = await NewCompany();
        var admins = new List<UserRecord>();
        for (var i = 0; i < 3; i++)
        {
            var u = await NewAdmin(i % 2 == 0 ? x : y, "ov" + i);
            await MakeAdminOf(u, i % 2 == 0 ? y : x);
            admins.Add(u);
        }
        var actors = new[] { await NewPerson("oa"), await NewPerson("ob"), await NewPerson("oc") };

        var results = await Task.WhenAll(admins.Select((u, i) => Auth.SetUserStateAsync(actors[i].Id, u.Id, UserState.Disabled)));

        Assert.Equal(2, results.Count(r => r == Changed));
        Assert.Equal(1, results.Count(r => r == LastAdmin));
    }

    [PostgresFact]
    public async Task Identity_administrators_cannot_disable_each_other_concurrently()
    {
        await StartAccess();
        for (var round = 0; round < 6; round++)
        {
            var one = await NewUser("idroot1x" + round, admin: true);
            var two = await NewUser("idroot2x" + round, admin: true);
            // other identity administrators from earlier rounds must be gone for the check to bite
            await Exec($"UPDATE {Users.Table} SET {Users.State} = 1 WHERE {Users.IsIdentityAdministrator} AND {Users.Id} NOT IN (@a, @b)", ("a", one.Id), ("b", two.Id));

            var results = await Task.WhenAll(
                Auth.SetUserStateAsync(one.Id, two.Id, UserState.Disabled),
                Auth.SetUserStateAsync(two.Id, one.Id, UserState.Disabled));

            Assert.Equal(1, results.Count(r => r == Changed));
            Assert.Equal(1, results.Count(r => r is AuthService.SetStateStatus.LastIdentityAdministrator or AuthService.SetStateStatus.ActorNotActive));
            Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Users.Table} WHERE {Users.IsIdentityAdministrator} AND {Users.State} = 0"));
        }
    }

    [PostgresFact]
    public async Task The_last_active_identity_administrator_cannot_be_disabled_and_a_disabled_actor_cannot_act()
    {
        await StartAccess();
        var root = await NewUser("idroot", admin: true);
        var actor = await NewPerson("actor");
        var other = await NewPerson("other");

        Assert.Equal(AuthService.SetStateStatus.LastIdentityAdministrator, await Auth.SetUserStateAsync(actor.Id, root.Id, UserState.Disabled));

        Assert.Equal(Changed, await Auth.SetUserStateAsync(root.Id, actor.Id, UserState.Disabled));
        Assert.Equal(AuthService.SetStateStatus.ActorNotActive, await Auth.SetUserStateAsync(actor.Id, other.Id, UserState.Disabled));
        Assert.Equal(UserState.Active, await StateOf(other));
    }

    [PostgresFact]
    public async Task Unknown_target_is_still_not_found_and_a_user_cannot_change_their_own_state()
    {
        await StartAccess();
        var actor = await NewPerson("actor");
        Assert.Equal(AuthService.SetStateStatus.NotFound, await Auth.SetUserStateAsync(actor.Id, Guid.NewGuid(), UserState.Disabled));
        Assert.Equal(AuthService.SetStateStatus.OwnAccount, await Auth.SetUserStateAsync(actor.Id, actor.Id, UserState.Disabled));
    }

    [PostgresFact]
    public async Task Disabling_a_user_still_revokes_their_sessions_atomically()
    {
        await StartAccess();
        var company = await NewCompany();
        await NewAdmin(company, "keeper");
        var leaving = await NewAdmin(company, "leaving");
        var actor = await NewPerson("actor");
        var session = await LoginOk(leaving.Login);

        Assert.Equal(Changed, await Auth.SetUserStateAsync(actor.Id, leaving.Id, UserState.Disabled));
        Assert.Null(await Auth.AuthenticateAsync(session.Token));
    }
}
