using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Parties;
using Npgsql;
using BizAudit = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using SysAudit = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using GP = Hasm.Infrastructure.SystemDatabase.SystemSchema.GlobalParty;
using PP = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyProposal;
using PL = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyLink;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRequest;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.9A party registry (system migration 0023, company migration 0021) against real PostgreSQL: shared identity in the system database, local record and request log (outbox) in the company
/// database, central proposal and approval, idempotent retry and reconciliation across the two databases, company isolation, permissions, audit and the database guards. Disposable cluster only.
/// </summary>
public abstract class PartyTestBase : HttpApiTestBase
{
    protected PartyRegistryStore Registry = null!;
    protected PartyService Parties = null!;
    protected PartyManagementService PMgmt = null!;
    protected UserRecord Central = null!;
    private int seq;

    protected async Task PrepareParties()
    {
        await PrepareRbac();
        Registry = new PartyRegistryStore(Options.BuildSystemConnectionString(admin: true), Audit, Clock);
        Parties = new PartyService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Registry, Clock);
        PMgmt = new PartyManagementService(Rbac, Parties);
        Central = await NewUser($"central{Interlocked.Increment(ref seq)}", admin: true);
    }

    /// <summary>A second ready company with its own administrator, reachable through the same resolver (its own credential).</summary>
    protected async Task<(CompanyRecord Company, UserRecord Admin)> OtherCompany()
    {
        var b = await ReadyCompany();
        var adminB = await NewAdminOf(b);
        SinglePrincipalResolver(b);
        return (b, adminB);
    }

    protected static T Ok<T>(ConfigResult<T> r) { Assert.True(r.IsOk, $"{r.Status} {r.Code} {string.Join(",", r.Fields?.Select(f => f.Field + ":" + f.Code) ?? [])}"); return r.Value!; }
    protected static void Fails<T>(ConfigResult<T> r, ConfigStatus status, string code) => Assert.Equal((status, code), (r.Status, r.Code));
    protected static void Invalid<T>(ConfigResult<T> r, string field, string code) { Assert.Equal(ConfigStatus.Invalid, r.Status); Assert.Contains(r.Fields!, f => f.Field == field && f.Code == code); }

    protected static SharedIdentityInput Id_(string nameAr = "شركة النور", string? tax = "300-111", string? reg = null, int kind = 1, int? status = null, string? note = null) =>
        new(kind, nameAr, "Al Noor Co", tax, reg, status, note);
    protected static LocalPartyInput Local(string code, SharedIdentityInput? identity = null, string name = "النور") => new(code, name, "local note", true, identity ?? Id_());

    protected Task<LocalPartyView> NewParty(string code, SharedIdentityInput? identity = null, Guid? company = null, UserRecord? by = null) =>
        Parties.CreateAsync((by ?? Admin).Id, company ?? Company.Id, Local(code, identity)).ContinueWith(t => Ok(t.Result));

    protected async Task<Guid> ProposalOf(Guid requestId, Guid? company = null) =>
        (await Registry.GetProposalStateAsync(company ?? Company.Id, requestId))!.Id;

    protected async Task<PartyProposalView> Approve(Guid requestId, Guid? linkTo = null, Guid? company = null)
    {
        var p = Ok(await Registry.GetProposalAsync(Central.Id, await ProposalOf(requestId, company)));
        return Ok(await Registry.ApproveAsync(Central.Id, p.Id, p.Revision, linkTo));
    }

    protected Task<long> SysCount(string table, string where = "true") => Scalar<long>($"SELECT count(*) FROM {table} WHERE {where}");
    protected Task<long> CoCount(string table, CompanyRecord? c = null) => Sql<long>($"SELECT count(*) FROM {table}", c);
    protected Task<long> SysEvents(short type) => Scalar<long>($"SELECT count(*) FROM {SysAudit.Table} WHERE {SysAudit.EventType} = {type}");
    protected Task<long> CoEvents(short type, CompanyRecord? c = null) => Sql<long>($"SELECT count(*) FROM {BizAudit.Table} WHERE {BizAudit.EventType} = {type}", c);
}

public class PartyRegistryTests : PartyTestBase
{
    [PostgresFact]
    public async Task A_new_local_party_proposes_its_shared_identity_and_the_central_approval_creates_and_links_it()
    {
        await PrepareParties();
        var p = await NewParty("nr-1");
        Assert.Equal(("NR-1", (int)PartyLinkState.Pending, (Guid?)null), (p.Code, p.LinkState, p.GlobalPartyId));
        var req = Assert.Single(p.Requests);
        Assert.Equal(((int)PartyRequestKind.NewIdentity, (int)PartyRequestStatus.Delivered, 1, "300-111"), (req.Kind, req.Status, req.Attempts, req.TaxNumber));   // delivered right after the commit
        Assert.Null(p.Identity);
        Assert.Equal(1L, await SysCount(PP.Table));
        Assert.Equal(0L, await SysCount(GP.Table));                                                                       // nothing shared before the central decision

        var queue = Ok(await Registry.ListProposalsAsync(Central.Id, (int)PartyProposalStatus.Pending, 0, 50));
        var item = Assert.Single(queue.Items);
        Assert.Equal((Company.Id, "شركة النور"), (item.CompanyId, item.LegalNameAr));
        var approved = await Approve(req.Id);
        Assert.Equal(((int)PartyProposalStatus.Approved, (int?)PartyProposalResolution.Created, Central.Id), (approved.Status, approved.Resolution, approved.DecidedBy));

        Assert.Equal(new PartySyncResult(0, 1, 0, 0), Ok(await PMgmt.SyncAsync(Admin.Id, Company.Id)));                  // the company pulls the decision
        var linked = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p.Id));
        Assert.Equal(((int)PartyLinkState.Linked, approved.OutcomeGlobalPartyId), (linked.LinkState, linked.GlobalPartyId));
        Assert.Equal(("شركة النور", "300-111", (int)PartyKind.Organization), (linked.Identity!.LegalNameAr, linked.Identity.TaxNumber, linked.Identity.Kind));
        Assert.Equal(((int)PartyRequestStatus.Completed, (int?)PartyRequestOutcome.Approved), (linked.Requests[0].Status, linked.Requests[0].Outcome));
        Assert.Equal(new PartySyncResult(0, 0, 0, 0), Ok(await PMgmt.SyncAsync(Admin.Id, Company.Id)));                  // nothing left to do: idempotent

        // local fields change locally only; the shared identity changes only through a proposal
        var upd = Ok(await PMgmt.UpdateAsync(Admin.Id, Company.Id, p.Id, linked.Revision, new LocalPartyInput("nr-1", "النور للتجارة", null, true)));
        Assert.Equal(("النور للتجارة", "شركة النور"), (upd.DisplayName, upd.Identity!.LegalNameAr));
        Invalid(await PMgmt.UpdateAsync(Admin.Id, Company.Id, p.Id, upd.Revision, Local("nr-1")), "identity", "use_propose");
        Assert.Equal(1L, await SysCount(GP.Table, $"{GP.Revision} = 1"));
    }

    [PostgresFact]
    public async Task Change_proposals_and_rejections_go_through_the_central_queue_with_separation_of_duties()
    {
        await PrepareParties();
        var p = await NewParty("CH-1");
        var g = (await Approve(p.Requests[0].Id)).OutcomeGlobalPartyId!.Value;
        await Parties.SyncAsync(Admin.Id, Company.Id);
        var cur = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p.Id));

        var proposed = Ok(await PMgmt.ProposeAsync(Admin.Id, Company.Id, p.Id, cur.Revision, Id_("شركة النور الحديثة", tax: " 300-111 ", reg: "cr 9", note: "new legal name")));
        var change = proposed.OpenRequest!;
        Assert.Equal(((int)PartyRequestKind.Change, "300-111", "CR9"), (change.Kind, change.TaxNumber, change.RegistrationNumber));        // normalized
        Fails(await PMgmt.ProposeAsync(Admin.Id, Company.Id, p.Id, proposed.Revision, Id_()), ConfigStatus.Conflict, "request_open");      // one open request per party
        Assert.Equal("شركة النور", proposed.Identity!.LegalNameAr);                                                                          // unchanged until approved

        // the proposer may never decide, even as an identity administrator; a company administrator who is not central cannot either
        var pid = await ProposalOf(change.Id);
        var view = Ok(await Registry.GetProposalAsync(Central.Id, pid));
        Assert.Equal(g, view.Target!.GlobalPartyId);
        Fails(await Registry.ApproveAsync(Admin.Id, pid, view.Revision, null), ConfigStatus.Forbidden, "forbidden");
        Fails(await Registry.ListProposalsAsync(Admin.Id, null, 0, 10), ConfigStatus.Forbidden, "forbidden");
        await Exec($"UPDATE {SystemSchemaUser.Table} SET {SystemSchemaUser.IsIdentityAdministrator} = true WHERE {SystemSchemaUser.Id} = @u", ("u", Admin.Id));
        Fails(await Registry.ApproveAsync(Admin.Id, pid, view.Revision, null), ConfigStatus.Conflict, "self_approval_not_allowed");
        Fails(await Registry.ApproveAsync(Central.Id, pid, view.Revision + 7, null), ConfigStatus.PreconditionFailed, "revision_conflict");
        var ok = Ok(await Registry.ApproveAsync(Central.Id, pid, view.Revision, null));
        Fails(await Registry.ApproveAsync(Central.Id, pid, ok.Revision, null), ConfigStatus.Conflict, "invalid_status");                  // decided once
        Assert.Equal((int?)PartyProposalResolution.Changed, ok.Resolution);
        await Parties.SyncAsync(Admin.Id, Company.Id);
        var after = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p.Id));
        Assert.Equal(("شركة النور الحديثة", "CR9", 2L), (after.Identity!.LegalNameAr, after.Identity.RegistrationNumber, after.Identity.Revision));

        // a second local party proposing the same identifier as a NEW identity cannot be approved as new: the approver links it instead
        var dup = await NewParty("CH-2", Id_("النور", tax: "300-111"));
        var dupView = Ok(await Registry.GetProposalAsync(Central.Id, await ProposalOf(dup.Requests[0].Id)));
        Assert.Contains(dupView.Matches, m => m.GlobalPartyId == g);                                                                       // the approver sees the likely duplicate
        Fails(await Registry.ApproveAsync(Central.Id, dupView.Id, dupView.Revision, null), ConfigStatus.Conflict, "identifier_in_use");
        Fails(await Registry.ApproveAsync(Central.Id, dupView.Id, dupView.Revision, g), ConfigStatus.Conflict, "already_linked_in_company"); // one company links an identity once
        Invalid(await Registry.RejectAsync(Central.Id, dupView.Id, dupView.Revision, " "), "note", "required");
        Ok(await Registry.RejectAsync(Central.Id, dupView.Id, dupView.Revision, "duplicate of CH-1"));
        await Parties.SyncAsync(Admin.Id, Company.Id);
        var rejected = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, dup.Id));
        Assert.Equal(((int)PartyLinkState.Rejected, (int?)PartyRequestOutcome.Rejected), (rejected.LinkState, rejected.Requests[0].Outcome));
        // after a rejection a new proposal is possible (and is again a new-identity proposal)
        var again = Ok(await PMgmt.ProposeAsync(Admin.Id, Company.Id, dup.Id, rejected.Revision, Id_("النور فرع", tax: "300-222")));
        Assert.Equal(((int)PartyLinkState.Pending, (int)PartyRequestKind.NewIdentity), (again.LinkState, again.OpenRequest!.Kind));
        Assert.Equal(1L, await SysCount(GP.Table));                                                                                     // still one shared identity
    }

    [PostgresFact]
    public async Task Two_companies_share_one_identity_without_seeing_anything_of_each_other_scenario_21_3()
    {
        await PrepareParties();
        var (b, adminB) = await OtherCompany();
        var a1 = await NewParty("A-1", Id_("شركة مشتركة", tax: "777"));
        var g = (await Approve(a1.Requests[0].Id)).OutcomeGlobalPartyId!.Value;
        await Parties.SyncAsync(Admin.Id, Company.Id);
        var b1 = await NewParty("B-7", Id_("مشتركة", tax: "777"), b.Id, adminB);
        var pid = await ProposalOf(b1.Requests[0].Id, b.Id);
        var pv = Ok(await Registry.GetProposalAsync(Central.Id, pid));
        Ok(await Registry.ApproveAsync(Central.Id, pid, pv.Revision, g));                                                                   // B is linked to the SAME GlobalPartyId
        await Parties.SyncAsync(adminB.Id, b.Id);
        var bView = Ok(await PMgmt.GetAsync(adminB.Id, b.Id, b1.Id));
        Assert.Equal((g, "شركة مشتركة"), (bView.GlobalPartyId, bView.Identity!.LegalNameAr));

        // company A sees only its own local party; B's local id, code, name and notes are never reachable from A (and the reverse)
        var aList = Ok(await PMgmt.ListAsync(Admin.Id, Company.Id, new PartyFilter(null, null, 0, 50)));
        Assert.Equal(["A-1"], aList.Items.Select(i => i.Code));
        Fails(await PMgmt.GetAsync(Admin.Id, Company.Id, b1.Id), ConfigStatus.NotFound, "party_not_found");
        Fails(await PMgmt.GetAsync(adminB.Id, b.Id, a1.Id), ConfigStatus.NotFound, "party_not_found");
        // the registry gives a company identities only through its OWN links: asking with another company's local party id yields nothing
        Assert.Empty(await Registry.GetLinkedIdentitiesAsync(Company.Id, [b1.Id]));
        Assert.Single(await Registry.GetLinkedIdentitiesAsync(b.Id, [b1.Id]));
        Assert.Equal(1L, (await Registry.SnapshotAsync(Company.Id)).LinksByLocalParty.Count);                                                // A's reconciliation input knows A's link only
        // A user of company A who is not a member of B gets nothing of B
        Assert.Equal(ConfigStatus.Forbidden, (await PMgmt.ListAsync(Admin.Id, b.Id, new PartyFilter(null, null, 0, 50))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await PMgmt.GetAsync(Admin.Id, b.Id, b1.Id)).Status);
        // the shared identity holds identifying data only: no balance or company column exists in the system table
        var columns = await Scalar<long>($"SELECT count(*) FROM information_schema.columns WHERE table_name = '{GP.Table}'");
        Assert.Equal(11L, columns);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM information_schema.columns WHERE table_name = '{GP.Table}' AND (data_type = 'numeric' OR column_name = '{PP.CompanyId}')"));
    }

    [PostgresFact]
    public async Task Every_step_is_audited_in_both_chains_and_both_chains_verify()
    {
        await PrepareParties();
        var p = await NewParty("AU-1");
        await Approve(p.Requests[0].Id);
        await Parties.SyncAsync(Admin.Id, Company.Id);
        var cur = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p.Id));
        cur = Ok(await PMgmt.UpdateAsync(Admin.Id, Company.Id, p.Id, cur.Revision, new LocalPartyInput("AU-1", "x", null, false)));
        cur = Ok(await PMgmt.ProposeAsync(Admin.Id, Company.Id, p.Id, cur.Revision, Id_("تغيير")));
        var pid = await ProposalOf(cur.OpenRequest!.Id);
        var v = Ok(await Registry.GetProposalAsync(Central.Id, pid));
        Ok(await Registry.RejectAsync(Central.Id, pid, v.Revision, "no"));
        await Parties.SyncAsync(Admin.Id, Company.Id);

        foreach (var (type, n) in new (short, long)[] { (PartyAuditEvents.LocalPartyCreated, 1), (PartyAuditEvents.LocalPartyUpdated, 1), (PartyAuditEvents.RequestQueued, 2), (PartyAuditEvents.RequestDelivered, 2),
                     (PartyAuditEvents.IdentityLinked, 1), (PartyAuditEvents.ChangeCompleted, 1) })
            Assert.True(n == await CoEvents(type), $"company event {type}");
        foreach (var (type, n) in new (short, long)[] { (PartyAuditEvents.ProposalReceived, 2), (PartyAuditEvents.ApprovedNewIdentity, 1), (PartyAuditEvents.ProposalRejectedCentrally, 1) })
            Assert.True(n == await SysEvents(type), $"system event {type}");
        Assert.Equal(Central.Id, await Scalar<Guid>($"SELECT {SysAudit.ActorUserId} FROM {SysAudit.Table} WHERE {SysAudit.EventType} = {PartyAuditEvents.ApprovedNewIdentity}"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
        Assert.Equal(AuditVerdict.Verified, (await new AuditVerifier(Options.BuildSystemConnectionString(admin: true), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.System)).Verdict);
    }
}

public class PartyOutboxTests : PartyTestBase
{
    [PostgresFact]
    public async Task A_crash_after_the_registry_committed_is_retried_without_creating_a_duplicate()
    {
        await PrepareParties();
        Parties.Fault = point => point == "after_registry" ? throw new IOException("connection lost") : Task.CompletedTask;
        var p = await NewParty("RT-1");                                                                         // the creation itself succeeds: delivery is best effort
        var req = Assert.Single(p.Requests);
        Assert.Equal(((int)PartyRequestStatus.Pending, 1, "delivery_interrupted"), (req.Status, req.Attempts, req.LastErrorCode));
        Assert.Equal(1L, await SysCount(PP.Table));                                                              // the registry DID receive it
        Assert.Equal(1L, await CoEvents(PartyAuditEvents.DeliveryFailed));
        // repeat the crash twice more: the registry still holds exactly one proposal
        Assert.Equal(new PartySyncResult(0, 0, 1, 0), await Parties.SyncAsync(Admin.Id, Company.Id));
        Assert.Equal(new PartySyncResult(0, 0, 1, 0), await Parties.SyncAsync(Admin.Id, Company.Id));
        Assert.Equal(1L, await SysCount(PP.Table));
        Assert.Equal(1L, await SysEvents(PartyAuditEvents.ProposalReceived));
        Parties.Fault = null;
        Assert.Equal(new PartySyncResult(1, 0, 0, 0), await Parties.SyncAsync(Admin.Id, Company.Id));             // recovered: the existing proposal is recorded
        var after = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p.Id));
        Assert.Equal(((int)PartyRequestStatus.Delivered, 4, (string?)null), (after.Requests[0].Status, after.Requests[0].Attempts, after.Requests[0].LastErrorCode));
        Assert.Equal(1L, await SysCount(PP.Table));
        // concurrent retries of the same request never duplicate either
        await Approve(req.Id);
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Parties.SyncAsync(Admin.Id, Company.Id)));
        Assert.Equal(1, runs.Sum(r => r.Applied));
        Assert.Equal((1L, 1L), (await SysCount(GP.Table), await SysCount(PL.Table)));
        Assert.Equal(1L, await CoEvents(PartyAuditEvents.IdentityLinked));
    }

    [PostgresFact]
    public async Task An_unreachable_registry_leaves_the_request_pending_and_the_local_change_consistent()
    {
        await PrepareParties();
        Parties.Fault = point => point == "before_registry" ? throw new NpgsqlException("registry down") : Task.CompletedTask;
        var p = await NewParty("DN-1");
        Assert.Equal(((int)PartyLinkState.Pending, (int)PartyRequestStatus.Pending, "registry_unavailable"), (p.LinkState, p.Requests[0].Status, p.Requests[0].LastErrorCode));
        Assert.Equal(0L, await SysCount(PP.Table));
        Assert.Equal((1L, 1L), (await CoCount(LP.Table), await CoCount(PR.Table)));                                 // the local event is complete and consistent
        var report = await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: false);
        Assert.Equal(["request_not_delivered"], report.Findings.Select(f => f.Code));
        Assert.Equal(1L, await CoEvents(PartyAuditEvents.DeliveryFailed));                                            // a read-only reconciliation writes nothing
        Parties.Fault = null;
        var repaired = await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: true);
        Assert.True(Assert.Single(repaired.Findings).Repaired);
        Assert.Equal(1L, await SysCount(PP.Table));
        Assert.Equal(1L, await CoEvents(PartyAuditEvents.ReconciliationRepaired));
        Assert.Empty((await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: false)).Findings);
    }

    [PostgresFact]
    public async Task Reconciliation_repairs_unapplied_outcomes_and_reports_what_the_registry_does_not_confirm()
    {
        await PrepareParties();
        var p1 = await NewParty("RC-1", Id_("واحد", tax: "1"));
        var p2 = await NewParty("RC-2", Id_("اثنان", tax: "2"));
        await Approve(p1.Requests[0].Id);
        var pv = Ok(await Registry.GetProposalAsync(Central.Id, await ProposalOf(p2.Requests[0].Id)));
        Ok(await Registry.RejectAsync(Central.Id, pv.Id, pv.Revision, "no"));
        var report = await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: false);
        Assert.Equal(["outcome_not_applied", "outcome_not_applied"], report.Findings.Select(f => f.Code));
        var fixedUp = await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: true);
        Assert.All(fixedUp.Findings, f => Assert.True(f.Repaired));
        Assert.Equal((int)PartyLinkState.Linked, Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p1.Id)).LinkState);
        Assert.Equal((int)PartyLinkState.Rejected, Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p2.Id)).LinkState);
        Assert.Empty((await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: false)).Findings);

        // the registry knows a request and a link this company database does not have (e.g. the company was restored from an older backup): reported, never fabricated or deleted
        var ghostLocal = Guid.NewGuid(); var ghostKey = Guid.NewGuid();
        var g1 = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p1.Id)).GlobalPartyId!.Value;
        var g = await Scalar<Guid>($"INSERT INTO {GP.Table} ({GP.Id}, {GP.PartyKind}, {GP.LegalNameAr}, {GP.CreatedBy}, {GP.CreatedAtUtc}, {GP.UpdatedAtUtc}) VALUES (gen_random_uuid(), 1, 'ghost', '{Central.Id}', now(), now()) RETURNING {GP.Id}");
        var pid = await Scalar<Guid>($"INSERT INTO {PP.Table} ({PP.Id}, {PP.CompanyId}, {PP.RequestKey}, {PP.LocalPartyId}, {PP.Kind}, {PP.PartyKind}, {PP.LegalNameAr}, {PP.ProposedBy}, {PP.ProposedAtUtc}) " +
                                     $"VALUES (gen_random_uuid(), '{Company.Id}', '{ghostKey}', '{ghostLocal}', 1, 1, 'ghost', '{Admin.Id}', now()) RETURNING {PP.Id}");
        await Exec($"INSERT INTO {PL.Table} ({PL.CompanyId}, {PL.LocalPartyId}, {PL.GlobalPartyId}, {PL.ProposalId}, {PL.LinkedAtUtc}) VALUES ('{Company.Id}', '{ghostLocal}', '{g}', '{pid}', now())");
        var ghostReport = (await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: true)).Findings;
        var codes = ghostReport.Select(f => (f.Code, f.Repairable, f.Repaired)).OrderBy(x => x.Code).ToList();
        Assert.Equal([("orphan_system_link", false, false), ("proposal_unknown_locally", false, false)], codes);
        Assert.Equal(pid, ghostReport.Single(f => f.Code == "proposal_unknown_locally").ProposalId);          // review M4: the identity administrator can find and decide it
        Assert.Equal(g1, Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p1.Id)).Identity!.GlobalPartyId);
    }
}

public class PartyAccessTests : PartyTestBase
{
    [PostgresFact]
    public async Task Each_party_operation_needs_its_own_company_permission_and_the_preset_holds_them_all()
    {
        await PrepareParties();
        var admin = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!.Single(r => r.PresetKey == "company-administrator");
        foreach (var a in new[] { "view", "create", "update", "submit", "configure" }) Assert.Contains($"company.parties.{a}", admin.Permissions);
        Assert.DoesNotContain(admin.Permissions, x => x.StartsWith("company.parties.approve"));
        var p = await NewParty("PM-1");
        var (viewer, _) = await Member([PermissionCatalog.Codes.PartiesView], ScopeKind.Company, null);
        var (creator, _) = await Member([PermissionCatalog.Codes.PartiesCreate], ScopeKind.Company, null);
        var (updater, _) = await Member([PermissionCatalog.Codes.PartiesUpdate], ScopeKind.Company, null);
        var (configurer, _) = await Member([PermissionCatalog.Codes.PartiesConfigure], ScopeKind.Company, null);
        var (none, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Company, null);

        Assert.Equal(new PartyCapabilities(true, false, false, false, false), await PMgmt.CapabilitiesAsync(viewer.Id, Company.Id));
        Assert.Equal(p.Id, Ok(await PMgmt.GetAsync(viewer.Id, Company.Id, p.Id)).Id);
        Fails(await PMgmt.CreateAsync(viewer.Id, Company.Id, Local("PM-2")), ConfigStatus.Forbidden, "forbidden");
        Fails(await PMgmt.UpdateAsync(viewer.Id, Company.Id, p.Id, p.Revision, Local("PM-1")), ConfigStatus.Forbidden, "forbidden");
        Fails(await PMgmt.SyncAsync(viewer.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await PMgmt.ReconcileAsync(viewer.Id, Company.Id, false), ConfigStatus.Forbidden, "forbidden");
        Fails(await PMgmt.GetAsync(creator.Id, Company.Id, p.Id), ConfigStatus.Forbidden, "forbidden");
        Ok(await PMgmt.CreateAsync(creator.Id, Company.Id, Local("PM-2", Id_(tax: "9"))));
        Ok(await PMgmt.SyncAsync(creator.Id, Company.Id));
        Fails(await PMgmt.ProposeAsync(updater.Id, Company.Id, p.Id, p.Revision, Id_()), ConfigStatus.Forbidden, "forbidden");
        Ok(await PMgmt.UpdateAsync(updater.Id, Company.Id, p.Id, p.Revision, new LocalPartyInput("PM-1", "y", null, true)));
        Ok(await PMgmt.ReconcileAsync(configurer.Id, Company.Id, true));
        Fails(await PMgmt.ListAsync(none.Id, Company.Id, new PartyFilter(null, null, 0, 10)), ConfigStatus.Forbidden, "forbidden");
        // validation and uniqueness of the company-local code
        Invalid(await PMgmt.CreateAsync(Admin.Id, Company.Id, Local("pm-1", Id_(tax: "8"))), "code", "code_taken");
        Invalid(await PMgmt.CreateAsync(Admin.Id, Company.Id, new LocalPartyInput("bad code!", "", null, true, new SharedIdentityInput(3, "", null, "#$", null))), "code", "invalid_code");
        var many = await PMgmt.CreateAsync(Admin.Id, Company.Id, new LocalPartyInput("ok", "", null, true, new SharedIdentityInput(3, "", null, "#$", null)));
        foreach (var (f, c) in new[] { ("displayName", "required"), ("identity.kind", "invalid_kind"), ("identity.legalNameAr", "required"), ("identity.taxNumber", "invalid_identifier") }) Invalid(many, f, c);
    }
}

public class PartyDatabaseGuardTests : PartyTestBase
{
    private async Task<string?> SysFails(string sql)
    {
        try { await Exec(sql); return null; } catch (PostgresException e) { return e.MessageText; }
    }

    private async Task<string?> CoFails(string sql)
    {
        try { await InCompany(Company.PhysicalDatabaseName, sql); return null; } catch (PostgresException e) { return e.MessageText; }
    }

    [PostgresFact]
    public async Task PostgreSQL_refuses_removal_rewriting_of_decided_proposals_links_and_requests_and_self_decision()
    {
        await PrepareParties();
        var p = await NewParty("DB-1");
        var other = await NewParty("DB-2", Id_(tax: "55"));
        await Approve(p.Requests[0].Id);
        await Parties.SyncAsync(Admin.Id, Company.Id);
        Assert.Contains("never removed", await SysFails($"DELETE FROM {GP.Table}"));
        Assert.NotNull(await SysFails($"TRUNCATE {PP.Table}"));                                       // refused (referenced by the links; the guard trigger refuses it as well)
        Assert.Contains("append-only", await SysFails($"UPDATE {PL.Table} SET {PL.LinkedAtUtc} = now()"));
        Assert.Contains("append-only", await SysFails($"DELETE FROM {PL.Table}"));
        Assert.Contains("final", await SysFails($"UPDATE {PP.Table} SET {PP.DecisionNote} = 'x', {PP.Revision} = {PP.Revision} + 1 WHERE {PP.Status} = 2"));
        Assert.Contains("immutable", await SysFails($"UPDATE {PP.Table} SET {PP.LegalNameAr} = 'x', {PP.Status} = 3, {PP.DecidedBy} = '{Central.Id}', {PP.DecidedAtUtc} = now(), {PP.DecisionNote} = 'n', {PP.Revision} = {PP.Revision} + 1 WHERE {PP.Status} = 1"));
        Assert.NotNull(await SysFails($"UPDATE {PP.Table} SET {PP.Status} = 3, {PP.DecidedBy} = {PP.ProposedBy}, {PP.DecidedAtUtc} = now(), {PP.DecisionNote} = 'n', {PP.Revision} = {PP.Revision} + 1 WHERE {PP.Status} = 1"));  // separation CHECK
        Assert.NotNull(await SysFails($"UPDATE {GP.Table} SET {GP.TaxNumber} = 'lower-case', {GP.Revision} = {GP.Revision} + 1"));
        Assert.Contains("never removed", await CoFails($"DELETE FROM {LP.Table}"));
        Assert.Contains("never removed", await CoFails($"TRUNCATE {PR.Table}"));
        Assert.Contains("never changes", await CoFails($"UPDATE {LP.Table} SET {LP.GlobalPartyId} = gen_random_uuid(), {LP.Revision} = {LP.Revision} + 1 WHERE {LP.LinkState} = 2"));
        Assert.Contains("final", await CoFails($"UPDATE {PR.Table} SET {PR.Attempts} = {PR.Attempts} + 1, {PR.Revision} = {PR.Revision} + 1 WHERE {PR.Status} = 3"));
        Assert.Contains("immutable", await CoFails($"UPDATE {PR.Table} SET {PR.LegalNameAr} = 'x', {PR.Revision} = {PR.Revision} + 1 WHERE {PR.LocalPartyId} = '{other.Id}'"));
        Assert.NotNull(await CoFails($"INSERT INTO {PR.Table} ({PR.Id}, {PR.LocalPartyId}, {PR.Kind}, {PR.PartyKind}, {PR.LegalNameAr}, {PR.CreatedBy}, {PR.CreatedAtUtc}) VALUES (gen_random_uuid(), '{other.Id}', 1, 1, 'x', '{Admin.Id}', now())"));  // one open request per party
        Assert.Contains("not allowed", await CoFails($"UPDATE {LP.Table} SET {LP.LinkState} = 1, {LP.Revision} = {LP.Revision} + 1 WHERE {LP.LinkState} = 2"));
    }
}

public class PartyApiTests : PartyTestBase
{
    [PostgresFact]
    public async Task Both_clients_create_link_and_read_parties_over_http_and_only_identity_administrators_reach_the_central_queue()
    {
        await StartApi();
        Registry = new PartyRegistryStore(Options.BuildSystemConnectionString(admin: true), Audit, Clock);
        Central = await NewUser("centralapi", admin: true);
        var cookie = await SignIn(Admin);
        var created = await Send(HttpMethod.Post, "/api/v1/company/parties",
            new { code = "api-1", displayName = "عميل", notes = (string?)null, isActive = true, identity = new { kind = 1, legalNameAr = "شركة عبر الواجهة", legalNameEn = (string?)null, taxNumber = "123", registrationNumber = (string?)null } }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Contains("no-store", created.Headers.CacheControl!.ToString());
        var requestId = created.Json.GetProperty("requests")[0].GetProperty("id").GetGuid();
        Assert.Equal(2, created.Json.GetProperty("requests")[0].GetProperty("status").GetInt32());

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/v1/admin/parties/proposals", cookie: cookie)).Status);         // a company administrator is not central
        var central = await SignIn(Central, selectCompany: false);
        var queue = await Send(HttpMethod.Get, "/api/v1/admin/parties/proposals?status=1", cookie: central);
        Assert.Equal(HttpStatusCode.OK, queue.Status);
        var pid = queue.Json.GetProperty("proposals")[0].GetProperty("id").GetGuid();
        var got = await Send(HttpMethod.Get, $"/api/v1/admin/parties/proposals/{pid}", cookie: central);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Post, $"/api/v1/admin/parties/proposals/{pid}/approve", new { }, central)).Status);
        var approved = await Send(HttpMethod.Post, $"/api/v1/admin/parties/proposals/{pid}/approve", new { linkTo = (Guid?)null }, central, ifMatch: Etag(got));
        Assert.Equal(HttpStatusCode.OK, approved.Status);
        Assert.Equal(1, (await Send(HttpMethod.Get, "/api/v1/admin/parties/search?term=123", cookie: central)).Json.GetProperty("identities").GetArrayLength());

        var sync = await Send(HttpMethod.Post, "/api/v1/company/parties/sync", null, cookie);
        Assert.Equal((HttpStatusCode.OK, 1), (sync.Status, sync.Json.GetProperty("applied").GetInt32()));
        var read = await Send(HttpMethod.Get, $"/api/v1/company/parties/{id}", cookie: cookie);
        var identity = read.Json.GetProperty("identity");
        Assert.Equal("شركة عبر الواجهة", identity.GetProperty("legalNameAr").GetString());
        // the shared identity exposes identifying data only: no balance, company list or other company's data
        Assert.Equal(["globalPartyId", "kind", "legalNameAr", "legalNameEn", "registrationNumber", "revision", "status", "taxNumber"], identity.EnumerateObject().Select(x => x.Name).Order());
        Assert.DoesNotContain("balance", read.Body, StringComparison.OrdinalIgnoreCase);
        var list = await Send(HttpMethod.Get, "/api/v1/company/parties?search=API", cookie: cookie);
        Assert.Equal(1, list.Json.GetProperty("total").GetInt32());
        var rec = await Send(HttpMethod.Post, "/api/v1/company/parties/reconcile?repair=false", null, cookie);
        Assert.Equal(0, rec.Json.GetProperty("findings").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/company/parties/sync", null, cookie, header: false)).Status);  // anti-CSRF
        var stale = await Send(HttpMethod.Put, $"/api/v1/company/parties/{id}", new { code = "API-1", displayName = "x", isActive = true }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        _ = requestId;
    }
}

internal static class SystemSchemaUser
{
    public const string Table = Hasm.Infrastructure.SystemDatabase.SystemSchema.User.Table;
    public const string Id = Hasm.Infrastructure.SystemDatabase.SystemSchema.User.Id;
    public const string IsIdentityAdministrator = Hasm.Infrastructure.SystemDatabase.SystemSchema.User.IsIdentityAdministrator;
}
