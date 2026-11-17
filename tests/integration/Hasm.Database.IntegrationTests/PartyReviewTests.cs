using System.Net;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Parties;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using GP = Hasm.Infrastructure.SystemDatabase.SystemSchema.GlobalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Follow-ups of the independent review of PR #46 (Phase 3.9A): no lost update on a shared identity (M1), graceful behaviour when the registry (system database) is unreachable or not granted (M2),
/// the rejection note returned to the proposing company (M3), the minimum registry search term (M4). Disposable cluster only.
/// </summary>
public class PartyReviewTests : PartyTestBase
{
    /// <summary>A registry store pointing at a database that does not exist: every call fails like an unreachable or ungranted system database (NpgsqlException), without touching anything.</summary>
    private PartyRegistryStore BrokenRegistry()
    {
        var b = new NpgsqlConnectionStringBuilder(Options.BuildSystemConnectionString(admin: true)) { Database = PostgresTestEnvironment.NewTestDatabaseName() + "_absent", Timeout = 5 };
        return new PartyRegistryStore(b.ConnectionString, Audit, Clock);
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_companies_changing_the_same_identity_cannot_lose_an_update_and_the_rejected_company_sees_why(bool aFirst)
    {
        await PrepareParties();
        var (b, adminB) = await OtherCompany();
        var a1 = await NewParty("A-1", Id_("مشتركة", tax: "700"));
        var g = (await Approve(a1.Requests[0].Id)).OutcomeGlobalPartyId!.Value;
        await Parties.SyncAsync(Admin.Id, Company.Id);
        var b1 = await NewParty("B-1", Id_("مشتركة", tax: "700"), b.Id, adminB);
        await Approve(b1.Requests[0].Id, g, b.Id);
        await Parties.SyncAsync(adminB.Id, b.Id);

        // both companies propose a change against revision 1 of the same identity
        var curA = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, a1.Id));
        var curB = Ok(await PMgmt.GetAsync(adminB.Id, b.Id, b1.Id));
        var reqA = Ok(await PMgmt.ProposeAsync(Admin.Id, Company.Id, a1.Id, curA.Revision, Id_("اسم أ", tax: "700", reg: "RA"))).OpenRequest!;
        var reqB = Ok(await PMgmt.ProposeAsync(adminB.Id, b.Id, b1.Id, curB.Revision, Id_("اسم ب", tax: "700", reg: "RB"))).OpenRequest!;
        Assert.Equal((1L, 1L), (reqA.BasedOnRevision!.Value, reqB.BasedOnRevision!.Value));
        var winner = aFirst ? (Request: reqA, Company: Company, Admin: Admin, Name: "اسم أ", Reg: "RA") : (Request: reqB, Company: b, Admin: adminB, Name: "اسم ب", Reg: "RB");
        var loser = aFirst ? (Request: reqB, Company: b, Admin: adminB, Party: b1) : (Request: reqA, Company: Company, Admin: Admin, Party: a1);
        await Approve(winner.Request.Id, company: winner.Company.Id);
        var pb = Ok(await Registry.GetProposalAsync(Central.Id, await ProposalOf(loser.Request.Id, loser.Company.Id)));
        Assert.Equal((1L, 2L, winner.Name), (pb.BasedOnRevision!.Value, pb.Target!.Revision, pb.Target.LegalNameAr));          // the approver sees the current data
        Fails(await Registry.ApproveAsync(Central.Id, pb.Id, pb.Revision, null), ConfigStatus.Conflict, "identity_changed");  // B's stale change would overwrite A's
        Assert.Equal((winner.Name, winner.Reg, 2L), (await Scalar<string>($"SELECT {GP.LegalNameAr} FROM {GP.Table}"), await Scalar<string>($"SELECT {GP.RegistrationNumber} FROM {GP.Table}"), await Scalar<long>($"SELECT {GP.Revision} FROM {GP.Table}")));
        Ok(await Registry.RejectAsync(Central.Id, pb.Id, pb.Revision, "based on an outdated version; propose again"));
        await Parties.SyncAsync(loser.Admin.Id, loser.Company.Id);
        var bAfter = Ok(await PMgmt.GetAsync(loser.Admin.Id, loser.Company.Id, loser.Party.Id));
        // The fixed clock deliberately ties both timestamps. UUID tie order is not creation order:
        // select the actual change request, never assume the first history row is the newest operation.
        var rejected = Assert.Single(bAfter.Requests, r => r.Id == loser.Request.Id);
        var original = Assert.Single(bAfter.Requests, r => r.Id == loser.Party.Requests[0].Id);
        Assert.Equal(original.CreatedAtUtc, rejected.CreatedAtUtc);
        Assert.Equal(((int?)PartyRequestOutcome.Approved, (string?)null), (original.Outcome, original.DecisionNote));
        Assert.Equal(((int?)PartyRequestOutcome.Rejected, "based on an outdated version; propose again"), (rejected.Outcome, rejected.DecisionNote));   // M3
        Assert.Equal((winner.Name, 2L), (bAfter.Identity!.LegalNameAr, bAfter.Identity.Revision));
        // a new proposal of B is based on the current revision and can be approved
        var again = Ok(await PMgmt.ProposeAsync(loser.Admin.Id, loser.Company.Id, loser.Party.Id, bAfter.Revision, Id_("اسم جديد", tax: "700", reg: "RC"))).OpenRequest!;
        Assert.Equal(2L, again.BasedOnRevision);
        await Approve(again.Id, company: loser.Company.Id);
        Assert.Equal(3L, await Scalar<long>($"SELECT {GP.Revision} FROM {GP.Table}"));
        // M4: the registry cannot be enumerated with a one-character search
        Fails(await Registry.SearchIdentitiesAsync(Central.Id, " ا "), ConfigStatus.Invalid, "validation_failed");
        Assert.Single(Ok(await Registry.SearchIdentitiesAsync(Central.Id, "700")));
    }

    [PostgresFact]
    public async Task Concurrent_approvals_commit_one_identity_change_and_refuse_the_other_without_losing_either_request()
    {
        await PrepareParties();
        var (b, adminB) = await OtherCompany();
        var a = await NewParty("RACE-A", Id_(tax: "701"));
        var global = (await Approve(a.Requests[0].Id)).OutcomeGlobalPartyId!.Value;
        await Parties.SyncAsync(Admin.Id, Company.Id);
        var other = await NewParty("RACE-B", Id_(tax: "701"), b.Id, adminB);
        await Approve(other.Requests[0].Id, global, b.Id);
        await Parties.SyncAsync(adminB.Id, b.Id);
        var curA = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, a.Id));
        var curB = Ok(await PMgmt.GetAsync(adminB.Id, b.Id, other.Id));
        var reqA = Ok(await PMgmt.ProposeAsync(Admin.Id, Company.Id, a.Id, curA.Revision, Id_("Race A", tax: "701", reg: "RA"))).OpenRequest!;
        var reqB = Ok(await PMgmt.ProposeAsync(adminB.Id, b.Id, other.Id, curB.Revision, Id_("Race B", tax: "701", reg: "RB"))).OpenRequest!;
        Assert.Equal((1L, 1L), (reqA.BasedOnRevision!.Value, reqB.BasedOnRevision!.Value));
        var pa = Ok(await Registry.GetProposalAsync(Central.Id, await ProposalOf(reqA.Id)));
        var pb = Ok(await Registry.GetProposalAsync(Central.Id, await ProposalOf(reqB.Id, b.Id)));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ConfigResult<PartyProposalView>> Decide(PartyProposalView proposal)
        {
            await start.Task;
            return await Registry.ApproveAsync(Central.Id, proposal.Id, proposal.Revision, null);
        }
        var first = Decide(pa);
        var second = Decide(pb);
        start.SetResult();
        var results = await Task.WhenAll(first, second);
        var wonA = results[0].IsOk;
        Assert.Single(results, r => r.IsOk);
        Fails(Assert.Single(results, r => !r.IsOk), ConfigStatus.Conflict, "identity_changed");
        Assert.Equal((wonA ? "Race A" : "Race B", wonA ? "RA" : "RB", 2L),
            (await Scalar<string>($"SELECT {GP.LegalNameAr} FROM {GP.Table}"), await Scalar<string>($"SELECT {GP.RegistrationNumber} FROM {GP.Table}"), await Scalar<long>($"SELECT {GP.Revision} FROM {GP.Table}")));
        var losing = wonA ? pb : pa;
        Ok(await Registry.RejectAsync(Central.Id, losing.Id, losing.Revision, "stale concurrent proposal"));
        await Parties.SyncAsync(Admin.Id, Company.Id);
        await Parties.SyncAsync(adminB.Id, b.Id);
        var afterA = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, a.Id));
        var afterB = Ok(await PMgmt.GetAsync(adminB.Id, b.Id, other.Id));
        var outcomeA = Assert.Single(afterA.Requests, r => r.Id == reqA.Id);
        var outcomeB = Assert.Single(afterB.Requests, r => r.Id == reqB.Id);
        Assert.Equal(((int?) (wonA ? PartyRequestOutcome.Approved : PartyRequestOutcome.Rejected), wonA ? null : "stale concurrent proposal"), (outcomeA.Outcome, outcomeA.DecisionNote));
        Assert.Equal(((int?) (wonA ? PartyRequestOutcome.Rejected : PartyRequestOutcome.Approved), wonA ? "stale concurrent proposal" : null), (outcomeB.Outcome, outcomeB.DecisionNote));
        Assert.Equal(afterA.Identity, afterB.Identity);
        Assert.Equal(2L, afterA.Identity!.Revision);
        Assert.Empty((await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: false)).Findings);
        Assert.Empty((await Parties.ReconcileAsync(adminB.Id, b.Id, repair: false)).Findings);
    }

    [PostgresFact]
    public async Task An_unreachable_or_ungranted_registry_degrades_reads_and_never_aborts_sync_or_reconciliation()
    {
        await PrepareParties();
        var p1 = await NewParty("UN-1", Id_("واحد", tax: "1"));
        await Approve(p1.Requests[0].Id);
        await Parties.SyncAsync(Admin.Id, Company.Id);
        var p2 = await NewParty("UN-2", Id_("اثنان", tax: "2"));
        await Approve(p2.Requests[0].Id);                                                                  // decided centrally, not yet pulled
        var broken = new PartyService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), BrokenRegistry(), Clock);

        var page = await broken.ListAsync(Company.Id, new PartyFilter(null, null, 0, 50));
        Assert.True(page.RegistryUnavailable);
        Assert.Equal(["UN-1", "UN-2"], page.Items.Select(i => i.Code));
        Assert.All(page.Items, i => Assert.Null(i.LegalNameAr));
        var one = (await broken.GetAsync(Company.Id, p1.Id))!;
        Assert.True(one.RegistryUnavailable);
        Assert.Null(one.Identity);

        var p3 = Ok(await broken.CreateAsync(Admin.Id, Company.Id, Local("UN-3", Id_("ثلاثة", tax: "3"))));   // the local change commits; the request waits
        Assert.Equal(((int)PartyRequestStatus.Pending, "registry_unavailable"), (p3.Requests[0].Status, p3.Requests[0].LastErrorCode));
        var sync = await broken.SyncAsync(Admin.Id, Company.Id);
        Assert.Equal(new PartySyncResult(0, 0, 2, 0, true), sync);                                       // both open requests attempted: none aborts the run
        Assert.Equal((int)PartyRequestStatus.Delivered, (await broken.GetAsync(Company.Id, p2.Id))!.Requests[0].Status);
        var report = await broken.ReconcileAsync(Admin.Id, Company.Id, repair: true);
        Assert.True(report.RegistryUnavailable);
        Assert.Equal(["registry_unavailable"], report.Findings.Select(f => f.Code));
        var cur = (await broken.GetAsync(Company.Id, p1.Id))!;
        Fails(await broken.ProposeAsync(Admin.Id, Company.Id, p1.Id, cur.Revision, Id_()), ConfigStatus.Conflict, "registry_unavailable");

        // back online: one synchronization recovers everything
        Assert.Equal(new PartySyncResult(1, 1, 0, 0), await Parties.SyncAsync(Admin.Id, Company.Id));
        Assert.Equal((int)PartyLinkState.Linked, Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p2.Id)).LinkState);
        Assert.Empty((await Parties.ReconcileAsync(Admin.Id, Company.Id, repair: false)).Findings);
    }

    [PostgresFact]
    public async Task The_central_endpoints_answer_a_stable_503_code_when_the_registry_is_unavailable()
    {
        PartyRegistryStore? broken = null;
        await StartApi(configure: b => b.ConfigureServices(s => s.AddSingleton(_ => broken!)));
        broken = BrokenRegistry();
        Central = await NewUser("centralbroken", admin: true);
        var central = await SignIn(Central, selectCompany: false);
        foreach (var path in new[] { "/api/v1/admin/parties/proposals", $"/api/v1/admin/parties/proposals/{Guid.NewGuid()}", "/api/v1/admin/parties/search?term=abc" })
        {
            var r = await Send(HttpMethod.Get, path, cookie: central);
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "registry_unavailable"), (r.Status, r.Error));
        }
        var approve = await Send(HttpMethod.Post, $"/api/v1/admin/parties/proposals/{Guid.NewGuid()}/approve", new { }, central, ifMatch: "\"1\"");
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "registry_unavailable"), (approve.Status, approve.Error));
        // the company side keeps working: synchronizing reports the registry as unavailable instead of failing
        var cookie = await SignIn(Admin);
        var created = await Send(HttpMethod.Post, "/api/v1/company/parties", new { code = "B-1", displayName = "x", isActive = true, identity = new { kind = 1, legalNameAr = "س" } }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("registry_unavailable", created.Json.GetProperty("requests")[0].GetProperty("lastErrorCode").GetString());
        var sync = await Send(HttpMethod.Post, "/api/v1/company/parties/sync", null, cookie);
        Assert.Equal((HttpStatusCode.OK, true), (sync.Status, sync.Json.GetProperty("registryUnavailable").GetBoolean()));
    }
}
