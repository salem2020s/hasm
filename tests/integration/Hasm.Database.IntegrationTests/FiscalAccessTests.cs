using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Fy = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using Fp = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;

namespace Hasm.Database.IntegrationTests;

/// <summary>Fiscal-calendar RBAC (company-wide, separate permissions), the reopening approval workflow and the offline approver delegation (migration 0009).</summary>
public abstract class FiscalAccessTestBase : HttpApiTestBase
{
    protected FiscalCalendarService Domain2 { get; private set; } = null!;
    protected FiscalCalendarManagementService FMgmt { get; private set; } = null!;
    protected const string Why = "Correction of a posting error found in audit";

    protected async Task PrepareCalendar(bool configureTimeZone = true)
    {
        await PrepareRbac();
        if (configureTimeZone) Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        BuildFacade();
    }

    protected void BuildFacade()
    {
        Domain2 = new FiscalCalendarService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new PeriodCloseGuard(), Clock);
        FMgmt = new FiscalCalendarManagementService(Rbac, Domain2);
    }

    protected static FiscalYearInput Year(string name, int year, string type = "monthly") => new(name, new DateOnly(year, 1, 1), type);
    protected Task<(UserRecord User, Guid RoleId)> Holder(params string[] permissions) => Member(permissions, ScopeKind.Company, null);

    private int approvers;

    /// <summary>A company member who holds the preset Fiscal Approver role, delegated the supported way (offline tool): the only way such a role can be assigned.</summary>
    protected async Task<UserRecord> NewApprover()
    {
        var user = await NewUser($"fiscalapprover{Interlocked.Increment(ref approvers)}");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, user.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantFiscalApproverOfflineAsync(user.Login, Company.Id));
        return user;
    }

    /// <summary>Soft closes and final closes period <paramref name="index"/> as <paramref name="closer"/>; returns the period at its current revision.</summary>
    protected async Task<FiscalPeriodView> FinalClosed(FiscalYearView y, Guid closer, int index = 0)
    {
        var soft = Ok(await Domain2.SoftCloseAsync(closer, Company.Id, y.Periods[index].Id, 1));
        return Ok(await Domain2.FinalCloseAsync(closer, Company.Id, y.Periods[index].Id, soft.Revision));
    }
}

public class FiscalAccessTests : FiscalAccessTestBase
{
    [PostgresFact]
    public async Task The_company_administrator_may_run_the_calendar_and_request_a_reopening_but_never_approve_it()
    {
        await PrepareCalendar();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "company.fiscalyears.view", "company.fiscalyears.create", "company.fiscalyears.close", "company.fiscalperiods.close", "company.fiscalperiods.finalclose",
                                  "company.fiscalperiods.reopen", "company.fiscalreopen.view", "company.fiscalreopen.create" })
            Assert.Contains(c, admin.Permissions);
        Assert.DoesNotContain("company.fiscalreopen.approve", admin.Permissions);
        var approver = roles.Single(r => r.PresetKey == "fiscal-approver");
        Assert.Equal(["company.fiscalreopen.approve", "company.fiscalreopen.view", "company.fiscalyears.view"], approver.Permissions.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(roles.Where(r => r.PresetKey is not ("company-administrator" or "fiscal-approver")).SelectMany(r => r.Permissions), p => p.StartsWith("company.fiscal"));
        Assert.Empty(Ok(await FMgmt.ListAsync(Admin.Id, Company.Id)));

        var y = Ok(await FMgmt.CreateYearAsync(Admin.Id, Company.Id, Year("FY2026", 2026)));
        var p = await FinalClosed(y, Admin.Id);
        var request = Ok(await FMgmt.RequestPeriodReopenAsync(Admin.Id, Company.Id, p.Id, p.Revision, new ReasonInput(Why)));
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ApproveRequestAsync(Admin.Id, Company.Id, request.Id, request.Revision)).Status);   // no approval right (and never one's own)
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.DenyRequestAsync(Admin.Id, Company.Id, request.Id, request.Revision, null)).Status);
        Assert.Equal(FiscalPeriodState.FinalClosed, (await Domain2.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);
    }

    [PostgresFact]
    public async Task Every_kind_of_closing_and_the_reopening_workflow_are_separate_permissions()
    {
        await PrepareCalendar();
        var y = Ok(await FMgmt.CreateYearAsync(Admin.Id, Company.Id, Year("FY2026", 2026, "quarterly")));
        var (viewer, _) = await Holder(PermissionCatalog.Codes.FiscalYearsView);
        var (softOnly, _) = await Holder(PermissionCatalog.Codes.FiscalPeriodsClose);
        var (finalOnly, _) = await Holder(PermissionCatalog.Codes.FiscalPeriodsFinalClose);
        var (creator, _) = await Holder(PermissionCatalog.Codes.FiscalYearsCreate);
        var (requestOnly, _) = await Holder(PermissionCatalog.Codes.FiscalReopenRequest);
        var (viewRequests, _) = await Holder(PermissionCatalog.Codes.FiscalReopenView);
        var p = y.Periods[0];

        Assert.Equal(y.Id, Assert.Single(Ok(await FMgmt.ListAsync(viewer.Id, Company.Id))).Id);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.CreateYearAsync(viewer.Id, Company.Id, Year("FY2027", 2027))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.SoftCloseAsync(viewer.Id, Company.Id, p.Id, 1)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ListAsync(softOnly.Id, Company.Id)).Status);                              // closing does not include viewing
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.FinalCloseAsync(softOnly.Id, Company.Id, p.Id, 1)).Status);
        Ok(await FMgmt.SoftCloseAsync(softOnly.Id, Company.Id, p.Id, 1));
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ReopenAsync(softOnly.Id, Company.Id, p.Id, 2)).Status);                  // soft close does not include reopening it
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.SoftCloseAsync(finalOnly.Id, Company.Id, y.Periods[1].Id, 1)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.CloseYearAsync(finalOnly.Id, Company.Id, y.Id, 1)).Status);
        var closed = Ok(await FMgmt.FinalCloseAsync(finalOnly.Id, Company.Id, p.Id, 2));
        Ok(await FMgmt.CreateYearAsync(creator.Id, Company.Id, Year("FY2027", 2027)));
        // Requesting needs its own permission; viewing requests needs its own; neither includes the other, nor approving.
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.RequestPeriodReopenAsync(finalOnly.Id, Company.Id, p.Id, closed.Revision, new ReasonInput(Why))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.RequestPeriodReopenAsync(viewRequests.Id, Company.Id, p.Id, closed.Revision, new ReasonInput(Why))).Status);
        var request = Ok(await FMgmt.RequestPeriodReopenAsync(requestOnly.Id, Company.Id, p.Id, closed.Revision, new ReasonInput(Why)));
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ListRequestsAsync(requestOnly.Id, Company.Id)).Status);
        Assert.Equal(request.Id, Assert.Single(Ok(await FMgmt.ListRequestsAsync(viewRequests.Id, Company.Id))).Id);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ApproveRequestAsync(viewRequests.Id, Company.Id, request.Id, request.Revision)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.DenyRequestAsync(requestOnly.Id, Company.Id, request.Id, request.Revision, null)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.CancelRequestAsync(viewRequests.Id, Company.Id, request.Id, request.Revision)).Status);
        Assert.Equal(FiscalReopenStatus.Cancelled, Ok(await FMgmt.CancelRequestAsync(requestOnly.Id, Company.Id, request.Id, request.Revision)).Status);   // the requester withdraws
    }

    [PostgresFact]
    public async Task A_separate_approver_approves_through_the_facade_and_a_requester_who_lost_the_right_is_refused()
    {
        await PrepareCalendar();
        var y = Ok(await FMgmt.CreateYearAsync(Admin.Id, Company.Id, Year("FY2026", 2026)));
        var approver = await NewApprover();
        var requester = await NewAdminOf(Company);
        var p = await FinalClosed(y, Admin.Id);
        var request = Ok(await FMgmt.RequestPeriodReopenAsync(requester.Id, Company.Id, p.Id, p.Revision, new ReasonInput(Why)));

        Assert.Equal(request.Id, Assert.Single(Ok(await FMgmt.ListRequestsAsync(approver.Id, Company.Id))).Id);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.RequestPeriodReopenAsync(approver.Id, Company.Id, p.Id, p.Revision, new ReasonInput(Why))).Status);   // the approver role cannot request
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.SoftCloseAsync(approver.Id, Company.Id, y.Periods[1].Id, 1)).Status);     // and runs no calendar operation
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ApproveRequestAsync(requester.Id, Company.Id, request.Id, request.Revision)).Status);   // the requester holds no approval right

        // The requester's role is revoked: their pending request can no longer be executed by anyone.
        var assignment = (await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, requester.Id)).Value!.Single(a => a.RoleId == PresetRoles.CompanyAdministratorId);
        Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Id)).IsOk);
        var revoked = await FMgmt.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision);
        Assert.Equal((ConfigStatus.Conflict, "request_requester_not_authorized"), (revoked.Status, revoked.Code));
        Assert.Equal(FiscalPeriodState.FinalClosed, (await Domain2.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);
        Assert.Equal(FiscalReopenStatus.Pending, (await Domain2.GetRequestAsync(Company.Id, request.Id))!.Status);
        Assert.Equal(FiscalReopenStatus.Denied, Ok(await FMgmt.DenyRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, "Requester no longer authorized")).Status);
    }

    [PostgresFact]
    public async Task An_approver_who_is_removed_can_no_longer_approve_and_a_current_approver_executes_the_reopening()
    {
        await PrepareCalendar();
        var y = Ok(await FMgmt.CreateYearAsync(Admin.Id, Company.Id, Year("FY2026", 2026)));
        var approver = await NewApprover();
        var removed = await NewApprover();
        var p = await FinalClosed(y, Admin.Id);
        var request = Ok(await FMgmt.RequestPeriodReopenAsync(Admin.Id, Company.Id, p.Id, p.Revision, new ReasonInput(Why)));
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeFiscalApproverOfflineAsync(removed.Login, Company.Id));
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ApproveRequestAsync(removed.Id, Company.Id, request.Id, request.Revision)).Status);   // revocation is immediate (no cache)
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ListRequestsAsync(removed.Id, Company.Id)).Status);
        var done = Ok(await FMgmt.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision));
        Assert.Equal(FiscalReopenStatus.Approved, done.Status);
        Assert.Equal(FiscalPeriodState.SoftClosed, (await Domain2.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);
    }

    [PostgresFact]
    public async Task The_fiscal_approver_role_can_only_be_delegated_offline_never_by_a_company_administrator()
    {
        await PrepareCalendar();
        var member = await NewUser("future-approver");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, member.Login)).IsOk);
        var viaService = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, member.Id, PresetRoles.FiscalApproverId, ScopeKind.Company, null);
        Assert.Equal((AccessStatus.Forbidden, "cannot_grant_permission"), (viaService.Status, viaService.Code));                    // an administrator cannot grant what they do not hold
        var custom = await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "sneaky-approver", [PermissionCatalog.Codes.FiscalReopenApprove]);   // nor smuggle it into a custom role
        Assert.False(custom.IsOk);

        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantFiscalApproverOfflineAsync(member.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.AlreadyApprover, await Rbac.GrantFiscalApproverOfflineAsync(member.Login.ToUpperInvariant(), Company.Id));
        var assignment = (await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, member.Id)).Value!.Single(a => a.RoleId == PresetRoles.FiscalApproverId);
        var revoke = await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Id);
        Assert.Equal((AccessStatus.Forbidden, "target_has_higher_privileges"), (revoke.Status, revoke.Code));                       // nor can an administrator remove one

        var audit = SystemSchema.AccessAudit.Table;
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {audit} WHERE {SystemSchema.AccessAudit.Detail} = 'scope: company; offline fiscal approver delegation' AND {SystemSchema.AccessAudit.ActorUserId} IS NULL AND {SystemSchema.AccessAudit.TargetUserId} = @u", ("u", member.Id)));
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeFiscalApproverOfflineAsync(member.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.NotApprover, await Rbac.RevokeFiscalApproverOfflineAsync(member.Login, Company.Id));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {audit} WHERE {SystemSchema.AccessAudit.Detail} = 'scope: company; offline fiscal approver removal' AND {SystemSchema.AccessAudit.ActorUserId} IS NULL AND {SystemSchema.AccessAudit.TargetUserId} = @u", ("u", member.Id)));
    }

    [PostgresFact]
    public async Task The_offline_delegation_needs_an_active_member_of_a_ready_company_and_never_creates_membership()
    {
        await PrepareCalendar();
        var outsider = await NewUser("outsider-approver");
        Assert.Equal(AccessService.FiscalApproverStatus.NotMember, await Rbac.GrantFiscalApproverOfflineAsync(outsider.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.UserNotFound, await Rbac.GrantFiscalApproverOfflineAsync("nobody-with-this-login", Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.UserNotFound, await Rbac.GrantFiscalApproverOfflineAsync("   ", Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.CompanyUnavailable, await Rbac.GrantFiscalApproverOfflineAsync(outsider.Login, Guid.NewGuid()));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.UserCompanyAccess.Table} WHERE {SystemSchema.UserCompanyAccess.UserId} = @u", ("u", outsider.Id)));   // nothing was created
        Assert.Equal(AccessService.FiscalApproverStatus.UserNotFound, await Rbac.RevokeFiscalApproverOfflineAsync("nobody-with-this-login", Company.Id));
    }

    [PostgresFact]
    public async Task Branch_and_warehouse_grants_never_reach_the_company_wide_calendar_and_strangers_get_nothing()
    {
        await PrepareCalendar();
        Ok(await FMgmt.CreateYearAsync(Admin.Id, Company.Id, Year("FY2026", 2026)));
        var main = await Main();
        var (branchUser, _) = await Member([PermissionCatalog.Codes.FiscalYearsView, PermissionCatalog.Codes.FiscalYearsCreate], ScopeKind.Branch, main.Id);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ListAsync(branchUser.Id, Company.Id)).Status);
        var plain = await NewUser("plain-fiscal");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var stranger = await NewUser("fiscal-stranger");
        foreach (var user in new[] { plain, stranger })
        {
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ListAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.CreateYearAsync(user.Id, Company.Id, Year("X", 2027))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.SoftCloseAsync(user.Id, Company.Id, Guid.NewGuid(), 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.FinalCloseAsync(user.Id, Company.Id, Guid.NewGuid(), 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ReopenAsync(user.Id, Company.Id, Guid.NewGuid(), 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.CloseYearAsync(user.Id, Company.Id, Guid.NewGuid(), 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ListRequestsAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.RequestPeriodReopenAsync(user.Id, Company.Id, Guid.NewGuid(), 1, new ReasonInput(Why))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.RequestYearReopenAsync(user.Id, Company.Id, Guid.NewGuid(), 1, new ReasonInput(Why))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ApproveRequestAsync(user.Id, Company.Id, Guid.NewGuid(), 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.DenyRequestAsync(user.Id, Company.Id, Guid.NewGuid(), 1, null)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.CancelRequestAsync(user.Id, Company.Id, Guid.NewGuid(), 1)).Status);
        }
        var other = await ReadyCompany();
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ListAsync(Admin.Id, other.Id)).Status);                                   // an administrator of company A has nothing in company B
        Assert.Equal(1L, await Count(Company, Fy.Table));
    }
}

/// <summary>End-to-end HTTP tests of the fiscal endpoints, including the reopening approval workflow (both clients use these endpoints).</summary>
public class FiscalApiTests : FiscalAccessTestBase
{
    private const string Years = "/api/v1/company/fiscal-years";
    private const string Periods = "/api/v1/company/fiscal-periods";
    private const string Requests = "/api/v1/company/fiscal-reopen-requests";

    private async Task Start(bool configureTimeZone = true)
    {
        await StartApi();
        if (configureTimeZone) Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        BuildFacade();
    }

    [PostgresFact]
    public async Task Every_fiscal_endpoint_needs_authentication_and_a_selected_company()
    {
        await Start();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, Years), (HttpMethod.Post, Years), (HttpMethod.Get, $"{Years}/{id}"), (HttpMethod.Post, $"{Years}/{id}/close"), (HttpMethod.Post, $"{Years}/{id}/reopen-requests"),
            (HttpMethod.Post, $"{Periods}/{id}/soft-close"), (HttpMethod.Post, $"{Periods}/{id}/reopen"), (HttpMethod.Post, $"{Periods}/{id}/final-close"), (HttpMethod.Post, $"{Periods}/{id}/reopen-requests"),
            (HttpMethod.Get, Requests), (HttpMethod.Post, $"{Requests}/{id}/approve"), (HttpMethod.Post, $"{Requests}/{id}/deny"), (HttpMethod.Post, $"{Requests}/{id}/cancel"),
        };
        foreach (var (method, path) in routes)
        {
            object? body = method == HttpMethod.Get ? null : new { };
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body, cookie: "forged")).Status);
        }
        var noCompany = await SignIn(Admin, selectCompany: false);
        foreach (var (method, path) in routes)
        {
            var r = await Send(method, path, method == HttpMethod.Get ? null : new { }, noCompany, ifMatch: "\"1\"");
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("company_not_selected", r.Error);
        }
        // The old one-step endpoints no longer exist.
        var cookie = await SignIn(Admin);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Periods}/{id}/reopen-final", new { reason = Why }, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Years}/{id}/reopen", new { reason = Why }, cookie, ifMatch: "\"1\"")).Status);
    }

    [PostgresFact]
    public async Task The_administrator_runs_a_year_end_to_end_with_etags_and_the_caller_capabilities()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var empty = await Send(HttpMethod.Get, Years, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Contains("no-store", empty.Headers.CacheControl!.ToString());
        Assert.Equal(0, empty.Json.GetProperty("years").GetArrayLength());
        Assert.Equal("2026-11-09", empty.Json.GetProperty("today").GetString());
        Assert.False(empty.Json.GetProperty("closeChecksImplemented").GetBoolean());                                               // never presented as verified
        var can = empty.Json.GetProperty("can");
        Assert.True(can.GetProperty("create").GetBoolean());
        Assert.True(can.GetProperty("softClose").GetBoolean());
        Assert.True(can.GetProperty("finalClose").GetBoolean());
        Assert.True(can.GetProperty("requestReopen").GetBoolean());
        Assert.False(can.GetProperty("approveReopen").GetBoolean());                                                                // the administrator never approves

        var created = await Send(HttpMethod.Post, Years, new { name = "FY 2026", startDate = "2026-01-01", periodType = "quarterly" }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        var year = created.Json;
        var yearId = year.GetProperty("id").GetGuid();
        Assert.Contains($"{Years}/{yearId}", created.Headers.Location!.ToString());
        Assert.Equal("2026-12-31", year.GetProperty("endDate").GetString());
        Assert.Equal(4, year.GetProperty("periods").GetArrayLength());
        Assert.Equal("\"1\"", Etag(await Send(HttpMethod.Get, $"{Years}/{yearId}", cookie: cookie)));

        foreach (var period in year.GetProperty("periods").EnumerateArray())
        {
            var pid = period.GetProperty("id").GetGuid();
            var soft = await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, cookie, ifMatch: "\"1\"");
            Assert.Equal(HttpStatusCode.OK, soft.Status);
            Assert.Equal("soft_closed", soft.Json.GetProperty("status").GetString());
            Assert.Equal("\"2\"", Etag(soft));
            Assert.Equal("open", (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen", null, cookie, ifMatch: "\"2\"")).Json.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, cookie, ifMatch: "\"3\"")).Status);
            var final = await Send(HttpMethod.Post, $"{Periods}/{pid}/final-close", null, cookie, ifMatch: "\"4\"");
            Assert.Equal(HttpStatusCode.OK, final.Status);
            Assert.Equal("final_closed", final.Json.GetProperty("status").GetString());
        }
        var closed = await Send(HttpMethod.Post, $"{Years}/{yearId}/close", null, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, closed.Status);
        Assert.True(closed.Json.GetProperty("closed").GetBoolean());
        Assert.Equal("\"2\"", Etag(closed));
    }

    [PostgresFact]
    public async Task The_server_enforces_validation_concurrency_the_calendar_rules_and_the_time_zone_requirement()
    {
        await Start(configureTimeZone: false);
        var cookie = await SignIn(Admin);
        var noZone = await Send(HttpMethod.Post, Years, new { name = "FY", startDate = "2026-01-01", periodType = "monthly" }, cookie);
        Assert.Equal(HttpStatusCode.Conflict, noZone.Status);
        Assert.Equal("company_time_zone_not_configured", noZone.Error);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await Send(HttpMethod.Get, Years, cookie: cookie)).Json.GetProperty("today").ValueKind);   // nothing is ever defaulted
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Years, new { name = "FY", startDate = "2026-01-15", periodType = "monthly" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Years, new { name = "", startDate = "2026-01-01", periodType = "monthly" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Years, new { name = "FY", startDate = "2026-01-01", periodType = "weekly" }, cookie)).Status);
        var y = await Send(HttpMethod.Post, Years, new { name = "FY2026", startDate = "2026-01-01", periodType = "monthly" }, cookie);
        Assert.Equal(HttpStatusCode.Created, y.Status);
        var overlap = await Send(HttpMethod.Post, Years, new { name = "Other", startDate = "2026-07-01", periodType = "monthly" }, cookie);
        Assert.Equal((HttpStatusCode.Conflict, "fiscal_year_overlap"), (overlap.Status, overlap.Error));
        Assert.Equal("fiscal_year_not_contiguous", (await Send(HttpMethod.Post, Years, new { name = "Gap", startDate = "2028-01-01", periodType = "monthly" }, cookie)).Error);

        var pid = y.Json.GetProperty("periods")[0].GetProperty("id").GetGuid();
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, cookie)).Status);
        var stale = await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, cookie, ifMatch: "\"9\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal(1, stale.Json.GetProperty("currentRevision").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, cookie, ifMatch: "abc")).Status);
        var skip = await Send(HttpMethod.Post, $"{Periods}/{pid}/final-close", null, cookie, ifMatch: "\"1\"");
        Assert.Equal((HttpStatusCode.Conflict, "invalid_period_state"), (skip.Status, skip.Error));                                 // Open never jumps to Final Closed
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Periods}/{Guid.NewGuid()}/soft-close", null, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Years}/{Guid.NewGuid()}", cookie: cookie)).Status);
        Assert.Equal("fiscal_year_has_unfinalized_periods", (await Send(HttpMethod.Post, $"{Years}/{y.Json.GetProperty("id").GetGuid()}/close", null, cookie, ifMatch: "\"1\"")).Error);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Fy.Table}"));
    }

    [PostgresFact]
    public async Task The_exceptional_reopening_over_http_is_a_request_that_a_separate_approver_decides()
    {
        await Start();
        var adminCookie = await SignIn(Admin);
        var y = await Send(HttpMethod.Post, Years, new { name = "FY2026", startDate = "2026-01-01", periodType = "monthly" }, adminCookie);
        var pid = y.Json.GetProperty("periods")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, adminCookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Periods}/{pid}/final-close", null, adminCookie, ifMatch: "\"2\"")).Status);
        var requesterUser = await NewAdminOf(Company);                                                                              // the user who closed it is not the requester here
        var requesterCookie = await SignIn(requesterUser);

        // Requesting: needs a reason and the revision the requester saw; nothing changes.
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = "short" }, requesterCookie, ifMatch: "\"3\"")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", null, requesterCookie, ifMatch: "\"3\"")).Status);
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = Why }, requesterCookie)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = Why }, requesterCookie, ifMatch: "\"2\"")).Status);
        var requested = await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = Why }, requesterCookie, ifMatch: "\"3\"");
        Assert.Equal(HttpStatusCode.Created, requested.Status);
        Assert.Equal("\"1\"", Etag(requested));
        var rid = requested.Json.GetProperty("id").GetGuid();
        Assert.Equal("pending", requested.Json.GetProperty("status").GetString());
        Assert.True(requested.Json.GetProperty("mine").GetBoolean());
        Assert.False(requested.Json.GetProperty("canApprove").GetBoolean());
        Assert.Equal(1, requested.Json.GetProperty("periodNumber").GetInt32());
        Assert.False(requested.Json.TryGetProperty("requestedBy", out _));                                                           // no user ids are exposed
        Assert.Equal("reopen_request_pending", (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = Why }, adminCookie, ifMatch: "\"3\"")).Error);

        // The requester can neither approve (no permission, and never their own) nor deny it.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Requests}/{rid}/approve", null, requesterCookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Requests}/{rid}/deny", new { note = "no" }, requesterCookie, ifMatch: "\"1\"")).Status);
        var ownList = await Send(HttpMethod.Get, Requests, cookie: requesterCookie);
        Assert.True(ownList.Json.GetProperty("requests")[0].GetProperty("canCancel").GetBoolean());

        var approver = await NewApprover();
        var cookie = await SignIn(approver);
        var list = await Send(HttpMethod.Get, Requests, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var item = list.Json.GetProperty("requests")[0];
        Assert.True(item.GetProperty("canApprove").GetBoolean());
        Assert.False(item.GetProperty("mine").GetBoolean());
        Assert.Equal(Why, item.GetProperty("reason").GetString());
        var years = await Send(HttpMethod.Get, Years, cookie: cookie);
        Assert.True(years.Json.GetProperty("can").GetProperty("approveReopen").GetBoolean());
        Assert.False(years.Json.GetProperty("can").GetProperty("create").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = Why }, cookie, ifMatch: "\"3\"")).Status);   // an approver cannot request
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Requests}/{rid}/approve", null, cookie)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Requests}/{rid}/approve", null, cookie, ifMatch: "\"7\"")).Status);

        var approved = await Send(HttpMethod.Post, $"{Requests}/{rid}/approve", null, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, approved.Status);
        Assert.Equal("approved", approved.Json.GetProperty("status").GetString());
        Assert.Equal("\"2\"", Etag(approved));
        var after = await Send(HttpMethod.Get, $"{Years}/{y.Json.GetProperty("id").GetGuid()}", cookie: cookie);
        Assert.Equal("soft_closed", after.Json.GetProperty("periods")[0].GetProperty("status").GetString());                      // never straight to open
        var again = await Send(HttpMethod.Post, $"{Requests}/{rid}/approve", null, cookie, ifMatch: "\"2\"");
        Assert.Equal((HttpStatusCode.Conflict, "request_not_pending"), (again.Status, again.Error));
    }

    [PostgresFact]
    public async Task A_denied_or_cancelled_request_over_http_leaves_the_calendar_alone()
    {
        await Start();
        var adminCookie = await SignIn(Admin);
        var y = await Send(HttpMethod.Post, Years, new { name = "FY2026", startDate = "2026-01-01", periodType = "monthly" }, adminCookie);
        var pid = y.Json.GetProperty("periods")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, adminCookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Periods}/{pid}/final-close", null, adminCookie, ifMatch: "\"2\"")).Status);
        var first = await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = Why }, adminCookie, ifMatch: "\"3\"");
        var approver = await NewApprover();
        var cookie = await SignIn(approver);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Requests}/{first.Json.GetProperty("id").GetGuid()}/cancel", null, cookie, ifMatch: "\"1\"")).Status);   // only the requester cancels
        var denied = await Send(HttpMethod.Post, $"{Requests}/{first.Json.GetProperty("id").GetGuid()}/deny", new { note = "Not justified" }, cookie, ifMatch: "\"1\"");
        Assert.Equal("denied", denied.Json.GetProperty("status").GetString());
        Assert.Equal("Not justified", denied.Json.GetProperty("decisionNote").GetString());
        var second = await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = Why }, adminCookie, ifMatch: "\"3\"");
        Assert.Equal(HttpStatusCode.Created, second.Status);
        var cancelled = await Send(HttpMethod.Post, $"{Requests}/{second.Json.GetProperty("id").GetGuid()}/cancel", null, adminCookie, ifMatch: "\"1\"");
        Assert.Equal("cancelled", cancelled.Json.GetProperty("status").GetString());
        Assert.Equal("request_not_pending", (await Send(HttpMethod.Post, $"{Requests}/{second.Json.GetProperty("id").GetGuid()}/approve", null, cookie, ifMatch: "\"2\"")).Error);
        var state = await Send(HttpMethod.Get, $"{Years}/{y.Json.GetProperty("id").GetGuid()}", cookie: cookie);
        Assert.Equal("final_closed", state.Json.GetProperty("periods")[0].GetProperty("status").GetString());
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Fp.Table} WHERE {Fp.Status} = 2"));
    }

    [PostgresFact]
    public async Task State_changes_need_the_csrf_header_and_unauthorized_users_get_403_without_data()
    {
        await Start();
        var admin = await SignIn(Admin);
        var noHeader = await Send(HttpMethod.Post, Years, new { name = "FY", startDate = "2026-01-01", periodType = "monthly" }, admin, header: false);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.Status);
        Assert.Equal("missing_request_header", noHeader.Error);
        var created = await Send(HttpMethod.Post, Years, new { name = "FY2026", startDate = "2026-01-01", periodType = "monthly" }, admin);
        var pid = created.Json.GetProperty("periods")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, admin, header: false, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Requests}/{Guid.NewGuid()}/approve", null, admin, header: false, ifMatch: "\"1\"")).Status);

        var plain = await NewUser("api-plain-fiscal");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var cookie = await SignIn(plain);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, Years, null), (HttpMethod.Get, Requests, null), (HttpMethod.Post, Years, new { name = "X", startDate = "2027-01-01", periodType = "monthly" }),
            (HttpMethod.Post, $"{Periods}/{pid}/soft-close", null), (HttpMethod.Post, $"{Periods}/{pid}/final-close", null),
            (HttpMethod.Post, $"{Periods}/{pid}/reopen-requests", new { reason = Why }), (HttpMethod.Post, $"{Requests}/{Guid.NewGuid()}/approve", null),
        })
        {
            var r = await Send(method, path, body, cookie, ifMatch: "\"1\"");
            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
            Assert.Equal("forbidden", r.Error);
            Assert.DoesNotContain("FY2026", r.Body);
        }
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Fy.Table}"));
        Assert.Equal(12L, await Sql<long>($"SELECT count(*) FROM {Fp.Table} WHERE {Fp.Status} = 1"));
    }
}
