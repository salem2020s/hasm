using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Ad = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using Je = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;

namespace Hasm.Database.IntegrationTests;

/// <summary>The General Ledger probes that replace the placeholders of the chart, the financial-history lock and the period close.</summary>
public class JournalProbeTests : JournalTestBase
{
    private ChartOfAccountsService RealChart() => new(Resolver, Tx, new AccountUsageGuard([new JournalAccountUsageProbe()]), new FinancialHistoryGuard(), Clock);

    [PostgresFact]
    public async Task Posted_history_freezes_the_type_like_attributes_of_an_account_but_drafts_do_not()
    {
        await PrepareJournal();
        var chart = RealChart();
        var details = new AccountDetailsInput("نقد", "Cash", "credit", null, null, null);               // a nature change
        await Draft();                                                                                  // a draft is not history
        Assert.Equal("credit", Ok(await chart.UpdateDetailsAsync(Admin.Id, Company.Id, AnyAcc.Id, 1, details with { Nature = "credit" })).Nature.ToString().ToLowerInvariant());
        await Posted();
        var frozen = await chart.UpdateDetailsAsync(Admin.Id, Company.Id, Cash.Id, 1, details);
        Assert.Equal((ConfigStatus.Conflict, "account_has_postings"), (frozen.Status, frozen.Code));
        Assert.Equal(1L, (await chart.GetAsync(Company.Id, Cash.Id))!.Revision);                           // nothing changed
        Assert.Empty(new AccountUsageGuard([new JournalAccountUsageProbe()]).UnimplementedDomains);       // the placeholder is gone: usage checks are really implemented
        // Names are not type-like attributes and stay editable.
        Assert.Equal("Cash box", Ok(await chart.UpdateDetailsAsync(Admin.Id, Company.Id, Cash.Id, 1, new AccountDetailsInput("نقد", "Cash box", "debit", null, null, null))).NameEn);
    }

    [PostgresFact]
    public async Task A_period_with_unposted_entries_cannot_be_closed_and_a_posted_one_can()
    {
        await PrepareJournal();
        var cal = new FiscalCalendarService(Resolver, Tx, new PeriodCloseGuard([new PendingJournalPeriodCloseProbe()]), new FinancialHistoryGuard(), Clock);
        var mar = Year.Periods[2];
        var d = await Draft(Simple("2026-03-10"));
        var blocked = await cal.SoftCloseAsync(Admin.Id, Company.Id, mar.Id, mar.Revision);
        Assert.Equal((ConfigStatus.Conflict, "period_close_blocked"), (blocked.Status, blocked.Code));
        Ok(await Journal.CancelAsync(Maker, Company.Id, d.Header.Id, 1));
        await Posted(Simple("2026-03-11"));
        Assert.Equal(FiscalPeriodState.SoftClosed, Ok(await cal.SoftCloseAsync(Admin.Id, Company.Id, mar.Id, mar.Revision)).State);
        Assert.Contains("pending_adjustments", new PeriodCloseGuard([new PendingJournalPeriodCloseProbe()]).UnimplementedDomains);   // other domains are still honestly unimplemented
        Assert.DoesNotContain("pending_accounting_postings", new PeriodCloseGuard([new PendingJournalPeriodCloseProbe()]).UnimplementedDomains);
    }

    [PostgresFact]
    public async Task Posted_entries_lock_the_base_currency_through_the_general_ledger_financial_history_probe()
    {
        await PrepareJournal();
        var guard = new FinancialHistoryGuard([new JournalFinancialHistoryProbe()]);
        var svc = MakeService(Company, null, guard);
        var before = await svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 1, true, true);
        Assert.NotEqual("base_currency_locked", before.Code);                                           // no history yet (a draft is not history)
        await Posted();
        var after = await svc.SetCurrencyAsync(Admin.Id, Company.Id, "EUR", 1, true, true);
        Assert.Equal((ConfigStatus.Conflict, "base_currency_locked"), (after.Status, after.Code));
        Assert.DoesNotContain("general_ledger", guard.UnimplementedDomains);
    }
}

/// <summary>Journal RBAC (branch-scoped, separate permissions, re-read on every call), approval enforcement through the facade and the HTTP API.</summary>
public class JournalAccessTests : JournalTestBase
{
    private async Task<(Hasm.Infrastructure.Identity.UserRecord User, Guid RoleId)> Branch(Guid branchId, params string[] permissions) => await Member(permissions, ScopeKind.Branch, branchId);

    [PostgresFact]
    public async Task The_administrator_preset_holds_everything_except_approval_and_the_journal_approver_preset_only_views_and_approves()
    {
        await PrepareJournal();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "view", "create", "update", "submit", "post", "reverse" }) Assert.Contains($"company.journal.{c}", admin.Permissions);
        Assert.DoesNotContain("company.journal.approve", admin.Permissions);
        foreach (var c in new[] { "company.journalsoftclosed.post", "company.journalrate.override", "company.ledgersettings.view", "company.ledgersettings.configure" }) Assert.Contains(c, admin.Permissions);
        var approver = roles.Single(r => r.PresetKey == "journal-approver");
        Assert.Equal(["company.journal.approve", "company.journal.view"], approver.Permissions.Order());
        Assert.DoesNotContain(roles.Where(r => r.PresetKey is not ("company-administrator" or "journal-approver")).SelectMany(r => r.Permissions), p => p.StartsWith("company.journal") || p.StartsWith("company.ledgersettings"));
    }

    [PostgresFact]
    public async Task Every_journal_operation_is_a_separate_permission_and_posting_and_approval_are_never_implied()
    {
        await PrepareJournal();
        var d = await Draft();
        var (viewer, _) = await Holder(PermissionCatalog.Codes.JournalView);
        var (creator, _) = await Holder(PermissionCatalog.Codes.JournalCreate);
        var (updater, _) = await Holder(PermissionCatalog.Codes.JournalUpdate);
        var (submitter, _) = await Holder(PermissionCatalog.Codes.JournalSubmit);
        var (poster, _) = await Holder(PermissionCatalog.Codes.JournalPost);
        var (reverser, _) = await Holder(PermissionCatalog.Codes.JournalReverse);
        Assert.Equal(d.Header.Id, Ok(await JMg.GetAsync(viewer.Id, Company.Id, d.Header.Id)).Header.Id);
        foreach (var u in new[] { creator, updater, submitter, poster, reverser }) Assert.Equal(ConfigStatus.Forbidden, (await JMg.GetAsync(u.Id, Company.Id, d.Header.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAsync(viewer.Id, Company.Id, Simple())).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.UpdateAsync(creator.Id, Company.Id, d.Header.Id, 1, Simple())).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.SubmitAsync(updater.Id, Company.Id, d.Header.Id, 1)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CancelAsync(submitter.Id, Company.Id, d.Header.Id, 1)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.PostAsync(submitter.Id, Company.Id, d.Header.Id, 1)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.DecideAsync(poster.Id, Company.Id, d.Header.Id, 1, new ApprovalDecisionInput(true, null))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.ReverseAsync(poster.Id, Company.Id, d.Header.Id, 1, new ReversalInput(new DateOnly(2026, 4, 1), null))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAutomaticAsync(viewer.Id, Company.Id, new AutomaticJournalInput("k1", Simple()))).Status);
        var made = Ok(await JMg.CreateAsync(creator.Id, Company.Id, Simple()));
        Assert.Equal(2L, Ok(await JMg.UpdateAsync(updater.Id, Company.Id, made.Header.Id, 1, Simple(amount: 5m))).Header.Revision);
        var sub = Ok(await JMg.SubmitAsync(submitter.Id, Company.Id, made.Header.Id, 2));
        Assert.Equal(JournalStatus.Posted, Ok(await JMg.PostAsync(poster.Id, Company.Id, made.Header.Id, sub.Header.Revision)).Header.Status);
        Assert.Equal(JournalType.Reversal, Ok(await JMg.ReverseAsync(reverser.Id, Company.Id, made.Header.Id, sub.Header.Revision, new ReversalInput(new DateOnly(2026, 4, 1), null))).Header.Type);
    }

    [PostgresFact]
    public async Task Branch_scoped_grants_reach_only_their_branch_and_the_list_hides_other_branches()
    {
        await PrepareJournal();
        var mainEntry = await Draft(Simple());
        var b2Entry = await Draft(new JournalInput(new DateOnly(2026, 3, 10), "b2", B2, [Dr(Cash.Id, 5m, branch: B2), Cr(Sales.Id, 5m, branch: B2)], null));
        var (mainUser, _) = await Branch(Main, PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalCreate);
        Assert.Equal(mainEntry.Header.Id, Ok(await JMg.GetAsync(mainUser.Id, Company.Id, mainEntry.Header.Id)).Header.Id);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.GetAsync(mainUser.Id, Company.Id, b2Entry.Header.Id)).Status);
        Assert.Equal([mainEntry.Header.Id], Ok(await JMg.ListAsync(mainUser.Id, Company.Id, new JournalFilter(null, null, null, null, null))).Select(e => e.Id));
        Assert.Equal(2, Ok(await JMg.ListAsync(Admin.Id, Company.Id, new JournalFilter(null, null, null, null, null))).Count);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAsync(mainUser.Id, Company.Id, new JournalInput(new DateOnly(2026, 3, 10), "x", B2, [Dr(Cash.Id, 1m), Cr(Sales.Id, 1m)], null))).Status);           // header branch B2
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAsync(mainUser.Id, Company.Id, Entry("2026-03-10", Dr(Cash.Id, 1m, branch: B2), Cr(Sales.Id, 1m)))).Status);                                 // a line in B2
        Assert.Equal(JournalStatus.Draft, Ok(await JMg.CreateAsync(mainUser.Id, Company.Id, Simple())).Header.Status);
        var nobody = await NewUser("jnobody");
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.ListAsync(nobody.Id, Company.Id, new JournalFilter(null, null, null, null, null))).Status);
    }

    [PostgresFact]
    public async Task Approval_is_enforced_through_the_facade_with_separation_of_duties_and_revocation_takes_effect_before_the_next_decision()
    {
        await PrepareJournal();
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(DocumentTypes.JournalEntry, "any", null, 0m, 1, "سياسة", "Policy")));
        var (maker, _) = await Holder(PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalSubmit, PermissionCatalog.Codes.JournalPost);
        // Nobody inside the company can hand out journal approval, not even the administrator: it comes from the offline delegation only.
        Assert.Equal("cannot_grant_permission", (await Rbac.AssignRoleAsync(Admin.Id, Company.Id, maker.Id, PresetRoles.JournalApproverId, ScopeKind.Company, null)).Code);
        Assert.Equal("cannot_grant_permission", (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "approvers", [PermissionCatalog.Codes.JournalApprove])).Code);
        var approver = await NewUser("japprover");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, approver.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(approver.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.AlreadyApprover, await Rbac.GrantJournalApproverOfflineAsync(approver.Login, Company.Id));
        // A user who both holds approval and created the entry still cannot approve it.
        var both = await NewUser("jboth");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, both.Login)).IsOk);
        var role = (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "maker2", [PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalSubmit])).Value!;
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, both.Id, role.Id, ScopeKind.Company, null)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(both.Login, Company.Id));
        var own = Ok(await JMg.CreateAsync(both.Id, Company.Id, Simple()));
        Ok(await JMg.SubmitAsync(both.Id, Company.Id, own.Header.Id, 1));
        Assert.Equal("separation_of_duties", (await JMg.DecideAsync(both.Id, Company.Id, own.Header.Id, 1, new ApprovalDecisionInput(true, null))).Code);

        var d = Ok(await JMg.CreateAsync(maker.Id, Company.Id, Simple(amount: 7m)));
        var s = Ok(await JMg.SubmitAsync(maker.Id, Company.Id, d.Header.Id, 1));
        Assert.Equal(JournalStatus.PendingApproval, s.Header.Status);
        Assert.Equal("entry_not_approved", (await JMg.PostAsync(maker.Id, Company.Id, d.Header.Id, 1)).Code);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.PostAsync(approver.Id, Company.Id, d.Header.Id, 1)).Status);                                // the approver preset cannot post
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAsync(approver.Id, Company.Id, Simple())).Status);                                     // nor create
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.DecideAsync(maker.Id, Company.Id, d.Header.Id, 1, new ApprovalDecisionInput(true, null))).Status);   // the maker lacks the permission
        // Revoking the delegation takes effect before the very next decision.
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeJournalApproverOfflineAsync(approver.Login, Company.Id));
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.DecideAsync(approver.Id, Company.Id, d.Header.Id, 1, new ApprovalDecisionInput(true, null))).Status);
        Assert.Equal(0L, await Count(Company, Ad.Table));
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(approver.Login, Company.Id));
        Assert.Equal(JournalStatus.Approved, Ok(await JMg.DecideAsync(approver.Id, Company.Id, d.Header.Id, 1, new ApprovalDecisionInput(true, null))).Header.Status);
        Assert.Equal(JournalStatus.Posted, Ok(await JMg.PostAsync(maker.Id, Company.Id, d.Header.Id, 1)).Header.Status);
    }

    [PostgresFact]
    public async Task Posting_into_a_soft_closed_period_and_the_rate_override_need_their_own_permissions()
    {
        await PrepareJournal();
        var (poster, _) = await Holder(PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalSubmit, PermissionCatalog.Codes.JournalPost);
        var (soft, _) = await Holder(PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalSubmit, PermissionCatalog.Codes.JournalPost, PermissionCatalog.Codes.JournalSoftClosedPost, PermissionCatalog.Codes.JournalRateOverride);
        var apr = Year.Periods[3];
        var a = Ok(await JMg.CreateAsync(poster.Id, Company.Id, Simple("2026-04-10")));
        var b = Ok(await JMg.CreateAsync(poster.Id, Company.Id, Simple("2026-04-11")));
        Ok(await JMg.SubmitAsync(poster.Id, Company.Id, a.Header.Id, 1)); Ok(await JMg.SubmitAsync(poster.Id, Company.Id, b.Header.Id, 1));
        Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, apr.Id, apr.Revision));
        Assert.Equal("fiscal_period_soft_closed", (await JMg.PostAsync(poster.Id, Company.Id, a.Header.Id, 1)).Code);
        var p = Ok(await JMg.PostAsync(soft.Id, Company.Id, b.Header.Id, 1));
        Assert.True(p.Header.PostedInSoftClosedPeriod);
        var overrideInput = Entry("2026-03-10", new JournalLineInput(UsdOnly.Id, null, "USD", 100m, null, null, 0.8m, "agreed with the bank", null), Cr(Sales.Id, 80m));
        Assert.Equal("rate_override_forbidden", (await JMg.CreateAsync(poster.Id, Company.Id, overrideInput)).Code);
        Assert.Equal(0.8m, Ok(await JMg.CreateAsync(soft.Id, Company.Id, overrideInput)).Lines.Single(l => l.Currency == "USD").Rate);
    }

    [PostgresFact]
    public async Task Automatic_entries_through_the_facade_need_the_create_permission_and_stay_subject_to_approval()
    {
        await PrepareJournal();
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(DocumentTypes.JournalEntry, "automatic", null, 0m, 1, "سياسة", "Auto")));
        var auto = Ok(await JMg.CreateAutomaticAsync(Admin.Id, Company.Id, new AutomaticJournalInput("pos:77", Simple())));
        Assert.Equal(DocumentSource.Automatic, auto.Header.Source);
        Assert.Equal(auto.Header.Id, Ok(await JMg.CreateAutomaticAsync(Admin.Id, Company.Id, new AutomaticJournalInput("pos:77", Simple()))).Header.Id);
        var s = Ok(await JMg.SubmitAsync(Admin.Id, Company.Id, auto.Header.Id, 1));
        Assert.Equal(JournalStatus.PendingApproval, s.Header.Status);
        Assert.Equal("entry_not_approved", (await JMg.PostAsync(Admin.Id, Company.Id, auto.Header.Id, 1)).Code);
        Assert.Equal(ConfigStatus.Invalid, (await JMg.CreateAutomaticAsync(Admin.Id, Company.Id, new AutomaticJournalInput(" ", Simple()))).Status);
    }

    [PostgresFact]
    public async Task Ledger_settings_have_separate_view_and_configure_permissions_and_accept_only_postable_base_accounts()
    {
        await PrepareJournal();
        var (viewer, _) = await Holder(PermissionCatalog.Codes.LedgerSettingsView);
        var (configurer, _) = await Holder(PermissionCatalog.Codes.LedgerSettingsConfigure);
        Assert.False(Ok(await LMg.GetAsync(viewer.Id, Company.Id)).Exists);
        Assert.Equal(ConfigStatus.Forbidden, (await LMg.GetAsync(configurer.Id, Company.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await LMg.PutAsync(viewer.Id, Company.Id, 0, new LedgerSettingsInput(Rounding.Id))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await LMg.PutAsync(configurer.Id, Company.Id, 0, new LedgerSettingsInput(GroupAcc.Id))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await LMg.PutAsync(configurer.Id, Company.Id, 0, new LedgerSettingsInput(Dead.Id))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await LMg.PutAsync(configurer.Id, Company.Id, 0, new LedgerSettingsInput(Guid.NewGuid()))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await LMg.PutAsync(configurer.Id, Company.Id, 0, new LedgerSettingsInput(UsdOnly.Id))).Status);                 // cannot absorb base-currency amounts
        var v = Ok(await LMg.PutAsync(configurer.Id, Company.Id, 0, new LedgerSettingsInput(Rounding.Id)));
        Assert.Equal((Rounding.Id, 1L), (v.RoundingAccountId, v.Revision));
        Assert.Equal(ConfigStatus.PreconditionFailed, (await LMg.PutAsync(configurer.Id, Company.Id, 0, new LedgerSettingsInput(null))).Status);
        Assert.Null(Ok(await LMg.PutAsync(configurer.Id, Company.Id, 1, new LedgerSettingsInput(null))).RoundingAccountId);
        Assert.Equal(2L, Ok(await LMg.PutAsync(configurer.Id, Company.Id, 2, new LedgerSettingsInput(null))).Revision);                               // unchanged: no new revision
    }

    [PostgresFact]
    public async Task The_endpoints_need_authentication_and_run_the_whole_workflow_end_to_end_with_etags_and_exact_amounts()
    {
        await StartApi();
        BuildChartFacade();
        BuildJournalFacade();
        await PrepareJournalForApi();
        const string Base = "/api/v1/company/journal";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, Base)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, Base, new { })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"{Base}/{Guid.NewGuid()}/post", null, cookie: "forged", ifMatch: "\"1\"")).Status);
        var cookie = await SignIn(Admin);
        var empty = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Contains("no-store", empty.Headers.CacheControl!.ToString());
        Assert.Equal(0, empty.Json.GetProperty("entries").GetArrayLength());
        Assert.True(empty.Json.GetProperty("can").GetProperty("post").GetBoolean());
        Assert.False(empty.Json.GetProperty("can").GetProperty("approve").GetBoolean());                      // not held by the administrator preset
        var body = new { entryDate = "2026-03-10", description = "API entry", branchId = Main, lines = new object[]
        {
            new { accountId = Cash.Id, debit = 100.125m },
            new { accountId = Sales.Id, credit = 100.125m, description = "sale" },
        } };
        var created = await Send(HttpMethod.Post, Base, body, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        Assert.Equal("100.1250", created.Json.GetProperty("baseDebitTotal").GetString());
        Assert.True(created.Json.GetProperty("balanced").GetBoolean());
        var id = created.Json.GetProperty("entry").GetProperty("id").GetGuid();
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, cookie)).Status);
        Assert.Equal((HttpStatusCode)412, (await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, cookie, ifMatch: "\"7\"")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Base, new { entryDate = "2026-03-10", branchId = Main, lines = new object[] { new { accountId = Cash.Id, debit = -1m } } }, cookie)).Status);
        var submitted = await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, cookie, ifMatch: "\"1\"");
        Assert.Equal("approved", submitted.Json.GetProperty("entry").GetProperty("status").GetString());
        var posted = await Send(HttpMethod.Post, $"{Base}/{id}/post", null, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, posted.Status);
        Assert.Equal("posted", posted.Json.GetProperty("entry").GetProperty("status").GetString());
        Assert.Equal("JV-2026-000001", posted.Json.GetProperty("entry").GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{id}/post", null, cookie, ifMatch: "\"1\"")).Status);   // idempotent retry
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Put, $"{Base}/{id}", body, cookie, ifMatch: "\"1\"")).Status);     // a posted entry cannot be edited
        var rev = await Send(HttpMethod.Post, $"{Base}/{id}/reverse", new { entryDate = "2026-04-02" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, rev.Status);
        Assert.Equal("reversal", rev.Json.GetProperty("entry").GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}?status=bogus", cookie: cookie)).Status);
        var list = await Send(HttpMethod.Get, $"{Base}?status=posted&from=2026-03-01&to=2026-03-31", cookie: cookie);
        Assert.Equal(1, list.Json.GetProperty("entries").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", cookie: cookie)).Status);
        var s = await Send(HttpMethod.Get, "/api/v1/company/ledger-settings", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, s.Status);
        var put = await Send(HttpMethod.Put, "/api/v1/company/ledger-settings", new { roundingAccountId = Rounding.Id }, cookie, ifMatch: "\"0\"");
        Assert.Equal(Rounding.Id, put.Json.GetProperty("roundingAccountId").GetGuid());
    }

    /// <summary>The base preparation, reusing the API host that StartApi already created.</summary>
    private async Task PrepareJournalForApi()
    {
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        foreach (var (code, isBase, enabled) in new[] { ("JOD", true, true), ("USD", false, true) })
            await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('{code}', {(isBase ? "true" : "false")}, {(enabled ? "true" : "false")}, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset"); Sales = await NewAccount("4101", "revenue"); Rounding = await NewAccount("5206", "expense", policy: "any");
        Main = await MainBranch();
    }
}
