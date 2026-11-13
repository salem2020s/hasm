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

/// <summary>Fiscal-calendar RBAC (company-wide, separate permissions, segregation of duties) and the HTTP API (system migration 0009, Phase 3.1D).</summary>
public abstract class FiscalAccessTestBase : HttpApiTestBase
{
    protected FiscalCalendarService Domain2 { get; private set; } = null!;
    protected FiscalCalendarManagementService FMgmt { get; private set; } = null!;

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

    private int rawPeople;

    /// <summary>
    /// A company-wide holder of permissions the preset administrator does NOT hold (the two exceptional reopenings). The service's "cannot grant what
    /// you do not possess" rule correctly stops an administrator from granting them, so the role is arranged directly in the system database (as a
    /// platform operator would): this is test setup only.
    /// </summary>
    protected async Task<UserRecord> RawHolder(params string[] permissions)
    {
        var n = Interlocked.Increment(ref rawPeople);
        var user = await NewUser($"rawholder{n}");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, user.Login)).IsOk);
        var role = Guid.NewGuid();
        await Exec($"INSERT INTO {SystemSchema.Role.Table} ({SystemSchema.Role.Id},{SystemSchema.Role.CompanyId},{SystemSchema.Role.PresetKey},{SystemSchema.Role.Name},{SystemSchema.Role.IsActive},{SystemSchema.Role.CreatedAtUtc},{SystemSchema.Role.CreatedBy},{SystemSchema.Role.UpdatedAtUtc}) VALUES (@i,@c,NULL,@n,true,now(),NULL,now())",
            ("i", role), ("c", Company.Id), ("n", $"raw-role-{n}"));
        foreach (var p in permissions)
            await Exec($"INSERT INTO {SystemSchema.RolePermission.Table} ({SystemSchema.RolePermission.RoleId},{SystemSchema.RolePermission.Permission}) VALUES (@r,@p)", ("r", role), ("p", p));
        await Exec($"INSERT INTO {SystemSchema.RoleAssignment.Table} ({SystemSchema.RoleAssignment.Id},{SystemSchema.RoleAssignment.UserId},{SystemSchema.RoleAssignment.CompanyId},{SystemSchema.RoleAssignment.RoleId},{SystemSchema.RoleAssignment.ScopeKind},{SystemSchema.RoleAssignment.GrantedAtUtc}) VALUES (@i,@u,@c,@r,0,now())",
            ("i", Guid.NewGuid()), ("u", user.Id), ("c", Company.Id), ("r", role));
        return user;
    }
}

public class FiscalAccessTests : FiscalAccessTestBase
{
    [PostgresFact]
    public async Task The_company_administrator_preset_holds_the_routine_fiscal_permissions_but_neither_exceptional_reopening()
    {
        await PrepareCalendar();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "company.fiscalyears.view", "company.fiscalyears.create", "company.fiscalyears.close", "company.fiscalperiods.close", "company.fiscalperiods.finalclose", "company.fiscalperiods.reopen" })
            Assert.Contains(c, admin.Permissions);
        Assert.DoesNotContain("company.fiscalperiods.reopenfinal", admin.Permissions);
        Assert.DoesNotContain("company.fiscalyears.reopen", admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.fiscal"));
        Assert.Empty(Ok(await FMgmt.ListAsync(Admin.Id, Company.Id)));
        // The administrator therefore cannot perform the exceptional reopenings (a custom role must hold them).
        var y = Ok(await FMgmt.CreateYearAsync(Admin.Id, Company.Id, Year("FY2026", 2026)));
        Ok(await FMgmt.SoftCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, 1));
        Ok(await FMgmt.FinalCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, 2));
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ReopenFinalAsync(Admin.Id, Company.Id, y.Periods[0].Id, 3, new ReasonInput("A documented reason here"))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ReopenYearAsync(Admin.Id, Company.Id, y.Id, 1, new ReasonInput("A documented reason here"))).Status);
    }

    [PostgresFact]
    public async Task Every_kind_of_closing_and_reopening_is_a_separate_permission()
    {
        await PrepareCalendar();
        var y = Ok(await FMgmt.CreateYearAsync(Admin.Id, Company.Id, Year("FY2026", 2026, "quarterly")));
        var (viewer, _) = await Holder(PermissionCatalog.Codes.FiscalYearsView);
        var (softOnly, _) = await Holder(PermissionCatalog.Codes.FiscalPeriodsClose);
        var (finalOnly, _) = await Holder(PermissionCatalog.Codes.FiscalPeriodsFinalClose);
        var (reopenOnly, _) = await Holder(PermissionCatalog.Codes.FiscalPeriodsReopen);
        var (creator, _) = await Holder(PermissionCatalog.Codes.FiscalYearsCreate);
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
        Ok(await FMgmt.FinalCloseAsync(finalOnly.Id, Company.Id, p.Id, 2));
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ReopenFinalAsync(reopenOnly.Id, Company.Id, p.Id, 3, new ReasonInput("A documented reason here"))).Status);   // routine reopen is not the exceptional one
        Assert.Equal("invalid_period_state", (await FMgmt.ReopenAsync(reopenOnly.Id, Company.Id, p.Id, 3)).Code);                   // allowed to try; the state machine refuses Final -> Open
        Ok(await FMgmt.CreateYearAsync(creator.Id, Company.Id, Year("FY2027", 2027)));
    }

    [PostgresFact]
    public async Task The_exceptional_reopening_works_for_a_dedicated_holder_who_is_not_the_user_who_final_closed()
    {
        await PrepareCalendar();
        var y = Ok(await FMgmt.CreateYearAsync(Admin.Id, Company.Id, Year("FY2026", 2026)));
        var exceptional = await RawHolder(PermissionCatalog.Codes.FiscalPeriodsReopenFinal, PermissionCatalog.Codes.FiscalPeriodsFinalClose, PermissionCatalog.Codes.FiscalPeriodsClose);
        var (closer, _) = await Holder(PermissionCatalog.Codes.FiscalPeriodsFinalClose, PermissionCatalog.Codes.FiscalPeriodsClose);
        var p = y.Periods[0];
        Ok(await FMgmt.SoftCloseAsync(closer.Id, Company.Id, p.Id, 1));
        Ok(await FMgmt.FinalCloseAsync(closer.Id, Company.Id, p.Id, 2));
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ReopenFinalAsync(closer.Id, Company.Id, p.Id, 3, new ReasonInput("A documented reason here"))).Status);   // no permission
        Ok(await FMgmt.ReopenFinalAsync(exceptional.Id, Company.Id, p.Id, 3, new ReasonInput("A documented reason here")));
        // A holder of BOTH final-closing and the exceptional reopening still cannot undo their own final close.
        Ok(await FMgmt.FinalCloseAsync(exceptional.Id, Company.Id, p.Id, 4));
        var own = await FMgmt.ReopenFinalAsync(exceptional.Id, Company.Id, p.Id, 5, new ReasonInput("A documented reason here"));
        Assert.Equal("segregation_of_duties", own.Code);
    }

    [PostgresFact]
    public async Task Branch_and_warehouse_grants_never_reach_the_company_wide_fiscal_calendar_and_strangers_get_nothing()
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
        }
        var other = await ReadyCompany();
        Assert.Equal(ConfigStatus.Forbidden, (await FMgmt.ListAsync(Admin.Id, other.Id)).Status);                                   // an administrator of company A has nothing in company B
        Assert.Equal(1L, await Count(Company, Fy.Table));
    }
}

/// <summary>End-to-end HTTP tests of /api/v1/company/fiscal-years and /fiscal-periods (both clients use these endpoints).</summary>
public class FiscalApiTests : FiscalAccessTestBase
{
    private const string Years = "/api/v1/company/fiscal-years";
    private const string Periods = "/api/v1/company/fiscal-periods";

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
            (HttpMethod.Get, Years), (HttpMethod.Post, Years), (HttpMethod.Get, $"{Years}/{id}"), (HttpMethod.Post, $"{Years}/{id}/close"), (HttpMethod.Post, $"{Years}/{id}/reopen"),
            (HttpMethod.Post, $"{Periods}/{id}/soft-close"), (HttpMethod.Post, $"{Periods}/{id}/reopen"), (HttpMethod.Post, $"{Periods}/{id}/final-close"), (HttpMethod.Post, $"{Periods}/{id}/reopen-final"),
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
        Assert.False(can.GetProperty("reopenFinal").GetBoolean());
        Assert.False(can.GetProperty("reopenYear").GetBoolean());

        var created = await Send(HttpMethod.Post, Years, new { name = "FY 2026", startDate = "2026-01-01", periodType = "quarterly" }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        var year = created.Json;
        var yearId = year.GetProperty("id").GetGuid();
        Assert.Contains($"{Years}/{yearId}", created.Headers.Location!.ToString());
        Assert.Equal("2026-12-31", year.GetProperty("endDate").GetString());
        Assert.Equal("quarterly", year.GetProperty("periodType").GetString());
        Assert.Equal(4, year.GetProperty("periods").GetArrayLength());
        Assert.Equal("open", year.GetProperty("periods")[0].GetProperty("status").GetString());
        Assert.Equal("\"1\"", Etag(await Send(HttpMethod.Get, $"{Years}/{yearId}", cookie: cookie)));

        foreach (var period in year.GetProperty("periods").EnumerateArray())
        {
            var pid = period.GetProperty("id").GetGuid();
            var soft = await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, cookie, ifMatch: "\"1\"");
            Assert.Equal(HttpStatusCode.OK, soft.Status);
            Assert.Equal("soft_closed", soft.Json.GetProperty("status").GetString());
            Assert.Equal("\"2\"", Etag(soft));
            var back = await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen", null, cookie, ifMatch: "\"2\"");
            Assert.Equal("open", back.Json.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, cookie, ifMatch: "\"3\"")).Status);
            var final = await Send(HttpMethod.Post, $"{Periods}/{pid}/final-close", null, cookie, ifMatch: "\"4\"");
            Assert.Equal(HttpStatusCode.OK, final.Status);
            Assert.Equal("final_closed", final.Json.GetProperty("status").GetString());
        }
        var closed = await Send(HttpMethod.Post, $"{Years}/{yearId}/close", null, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, closed.Status);
        Assert.True(closed.Json.GetProperty("closed").GetBoolean());
        Assert.Equal("\"2\"", Etag(closed));
        var reopenYear = await Send(HttpMethod.Post, $"{Years}/{yearId}/reopen", new { reason = "Documented correction after year close" }, cookie, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.Forbidden, reopenYear.Status);                                                                 // the preset administrator lacks it
        Assert.Equal("forbidden", reopenYear.Error);
    }

    [PostgresFact]
    public async Task The_server_enforces_validation_concurrency_the_calendar_rules_and_the_time_zone_requirement()
    {
        await Start(configureTimeZone: false);
        var cookie = await SignIn(Admin);
        var noZone = await Send(HttpMethod.Post, Years, new { name = "FY", startDate = "2026-01-01", periodType = "monthly" }, cookie);
        Assert.Equal(HttpStatusCode.Conflict, noZone.Status);
        Assert.Equal("company_time_zone_not_configured", noZone.Error);
        Assert.Equal(JsonKindNull, (await Send(HttpMethod.Get, Years, cookie: cookie)).Json.GetProperty("today").ValueKind);        // nothing is ever defaulted
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Years, new { name = "FY", startDate = "2026-01-15", periodType = "monthly" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Years, new { name = "", startDate = "2026-01-01", periodType = "monthly" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Years, new { name = "FY", startDate = "2026-01-01", periodType = "weekly" }, cookie)).Status);
        var y = await Send(HttpMethod.Post, Years, new { name = "FY2026", startDate = "2026-01-01", periodType = "monthly" }, cookie);
        Assert.Equal(HttpStatusCode.Created, y.Status);
        var overlap = await Send(HttpMethod.Post, Years, new { name = "Other", startDate = "2026-07-01", periodType = "monthly" }, cookie);
        Assert.Equal((HttpStatusCode.Conflict, "fiscal_year_overlap"), (overlap.Status, overlap.Error));
        var gap = await Send(HttpMethod.Post, Years, new { name = "Gap", startDate = "2028-01-01", periodType = "monthly" }, cookie);
        Assert.Equal("fiscal_year_not_contiguous", gap.Error);

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
        var yearClose = await Send(HttpMethod.Post, $"{Years}/{y.Json.GetProperty("id").GetGuid()}/close", null, cookie, ifMatch: "\"1\"");
        Assert.Equal("fiscal_year_has_unfinalized_periods", yearClose.Error);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Fy.Table}"));
    }

    private const System.Text.Json.JsonValueKind JsonKindNull = System.Text.Json.JsonValueKind.Null;

    [PostgresFact]
    public async Task The_exceptional_reopening_over_http_needs_its_permission_a_reason_and_a_different_user()
    {
        await Start();
        var adminCookie = await SignIn(Admin);
        var y = await Send(HttpMethod.Post, Years, new { name = "FY2026", startDate = "2026-01-01", periodType = "monthly" }, adminCookie);
        var pid = y.Json.GetProperty("periods")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Periods}/{pid}/soft-close", null, adminCookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Periods}/{pid}/final-close", null, adminCookie, ifMatch: "\"2\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-final", new { reason = "Documented correction needed" }, adminCookie, ifMatch: "\"3\"")).Status);

        var holder = await RawHolder(PermissionCatalog.Codes.FiscalPeriodsReopenFinal, PermissionCatalog.Codes.FiscalYearsView);
        var cookie = await SignIn(holder);
        var noReason = await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-final", new { reason = "short" }, cookie, ifMatch: "\"3\"");
        Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
        Assert.Contains("reason", noReason.Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-final", null, cookie, ifMatch: "\"3\"")).Status);
        var ok = await Send(HttpMethod.Post, $"{Periods}/{pid}/reopen-final", new { reason = "Documented correction needed" }, cookie, ifMatch: "\"3\"");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("soft_closed", ok.Json.GetProperty("status").GetString());                                                    // never straight to open
        Assert.Equal("\"4\"", Etag(ok));
        var list = await Send(HttpMethod.Get, Years, cookie: cookie);
        Assert.True(list.Json.GetProperty("can").GetProperty("reopenFinal").GetBoolean());
        Assert.False(list.Json.GetProperty("can").GetProperty("create").GetBoolean());
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

        var plain = await NewUser("api-plain-fiscal");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var cookie = await SignIn(plain);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, Years, null), (HttpMethod.Post, Years, new { name = "X", startDate = "2027-01-01", periodType = "monthly" }),
            (HttpMethod.Post, $"{Periods}/{pid}/soft-close", null), (HttpMethod.Post, $"{Periods}/{pid}/final-close", null),
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
