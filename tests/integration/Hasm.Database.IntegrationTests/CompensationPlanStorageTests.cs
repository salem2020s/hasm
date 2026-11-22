using System.Collections.Immutable;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Inventory;
using AU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanPosition;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanEvent;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanLine;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.8D step 5, slice 1: the plan STORAGE service against real PostgreSQL through the real per-company runtime role. The plan, its closure, its compensation lines and its created event are written in one
/// transaction from the read-only planner output; the closure hash PostgreSQL computes is read back and equals the byte-exact hash computed independently; the planner's refusals are returned unchanged. Nothing executes
/// and no endpoint exists. The plan permission is held by NO preset (owner decision X4 is open): the tests grant it to a test-created role.
/// </summary>
public class CompensationPlanStorageTests : CompensationPlanTestBase
{
    [PostgresRoleFact]
    public async Task A_plan_is_stored_with_its_closure_lines_and_created_event_in_one_transaction_and_the_closure_hash_is_the_byte_exact_one()
    {
        await Prepare();
        var user = await PlanUser();
        var (doc, input) = await Event();
        var before = await Footprint();
        var created = await Service().CreateAsync(user.Id, Company.Id, input);
        Assert.True(created.IsOk, created.Code);
        var plan = created.Value!;
        Assert.Equal(((short)1, 1L, Avg.Id, W1.Id, doc.Id, doc.Revision, user.Id), (plan.Status, plan.Revision, plan.ItemId, plan.WarehouseId, plan.DocumentId, plan.DocumentRevision, plan.CreatedBy));
        // the hash PostgreSQL computed equals the one computed here from the byte-exact rule, and the closure is the item on the root warehouse
        Assert.Equal(ExpectedClosureHash([(Avg.Id, W1.Id)]), plan.ClosureHash);
        Assert.Equal(plan.ClosureHash, (await Sql<string>($"SELECT {CP.ClosureHash}::text FROM {CP.Table} WHERE {CP.Id} = '{plan.Id}'")).Trim());
        Assert.Equal((Avg.Id, W1.Id), Assert.Single(plan.Closure));
        Assert.All(plan.Fingerprints.All, f => Assert.Matches("^[0-9a-f]{64}$", f));
        // the compensation lines of the planner (the issue is re-costed from 10.00 to 15.00 per piece: 4 x 5.00 = 20.00), the created event and exactly one audit event
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {CL.Table} WHERE {CL.PlanId} = '{plan.Id}'"));
        Assert.Equal((20m, 20m), (await Sql<decimal>($"SELECT sum({CL.Debit}) FROM {CL.Table} WHERE {CL.PlanId} = '{plan.Id}'"), await Sql<decimal>($"SELECT sum({CL.Credit}) FROM {CL.Table} WHERE {CL.PlanId} = '{plan.Id}'")));
        Assert.Equal((1L, user.Id), (await Sql<long>($"SELECT count(*) FROM {CE.Table} WHERE {CE.PlanId} = '{plan.Id}' AND {CE.Kind} = 1"), await Sql<Guid>($"SELECT {CE.ActorUserId} FROM {CE.Table} WHERE {CE.PlanId} = '{plan.Id}'")));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {AU.Table} WHERE {AU.EventType} = {CostingAuditEvents.PlanProposed} AND {AU.SubjectId} = '{plan.Id}' AND {AU.ActorUserId} = '{user.Id}'"));
        // nothing else was written: no cost entry, operation, journal or posting; the plan, its one closure row, its two lines, its event and its audit event are the only new rows
        var after = await Footprint();
        Assert.Equal(before[..4], after[..4]);
        Assert.Equal([1L, 1L, 2L, 1L, 1L], after[4..].Zip(before[4..], (a, b) => a - b));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        // the stored plan reads back identically
        await using var conn = await AsRuntime();
        Assert.Equal(plan, await CompensationPlanStore.LoadAsync(conn, null, plan.Id, false, default), new PlanComparer());
    }

    private sealed class PlanComparer : IEqualityComparer<StoredCompensationPlan?>
    {
        public bool Equals(StoredCompensationPlan? a, StoredCompensationPlan? b) => a is not null && b is not null && a.Id == b.Id && a.ClosureHash == b.ClosureHash && a.Fingerprints == b.Fingerprints && a.Closure.SequenceEqual(b.Closure) && a.Payload == b.Payload;
        public int GetHashCode(StoredCompensationPlan? p) => p?.Id.GetHashCode() ?? 0;
    }

    [PostgresRoleFact]
    public async Task Creating_a_plan_needs_the_plan_permission_on_the_root_warehouse_and_no_preset_holds_it()
    {
        await Prepare();
        var (_, input) = await Event();
        var before = await Footprint();
        // the Company Administrator preset does not hold the code (owner decision X4 is open: no preset, no system migration)
        Assert.DoesNotContain(PermissionCatalog.Codes.CompensationPlansCreate, PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions);
        Assert.DoesNotContain(PermissionCatalog.Codes.CompensationPlansApprove, PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions);
        Refused(await Service().CreateAsync(Admin.Id, Company.Id, input), ConfigStatus.Forbidden, "forbidden");
        // and the Administrator cannot hand it out through the application either: nobody holds it
        Assert.Equal("cannot_grant_permission", (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "plan-role-attempt", [PermissionCatalog.Codes.CompensationPlansCreate])).Code);
        // a user who can plan and see everything, but only on ANOTHER warehouse
        var other = await PlanMember(PlannerPermissions, [PermissionCatalog.Codes.CompensationPlansCreate], ScopeKind.Warehouse, W2.Id);
        Refused(await Service().CreateAsync(other.Id, Company.Id, input), ConfigStatus.Forbidden, "forbidden");
        // a user who holds the plan permission but cannot see the stock balance of the root: the planner's own refusal, unchanged
        var blind = await PlanMember([PermissionCatalog.Codes.StockDocsView], [PermissionCatalog.Codes.CompensationPlansCreate]);
        Refused(await Service().CreateAsync(blind.Id, Company.Id, input), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(before, await Footprint());
    }

    [PostgresRoleFact]
    public async Task The_event_document_must_exist_be_approved_unposted_and_exactly_the_proposed_event()
    {
        await Prepare();
        var user = await PlanUser();
        var service = Service();
        var (doc, input) = await Event();
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { DocumentId = Guid.NewGuid() }), ConfigStatus.NotFound, "document_not_found");
        // a draft document
        var draft = await Draft(Receipt(W1, "2026-03-11", L(Avg, Pcs, 10m)));
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { DocumentId = draft.Id }), ConfigStatus.Conflict, "document_not_approved");
        // the proposal is not the event of the document: quantity, date, kind and warehouse
        var proposal = input.Proposal;
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { Proposal = proposal with { Lines = [new ProposedFlowLine(W1.Id, 9m, 200m)] } }), ConfigStatus.Conflict, "document_plan_mismatch");
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { Proposal = proposal with { EffectiveDate = DateOnly.Parse("2026-03-12") } }), ConfigStatus.Conflict, "document_plan_mismatch");
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { Proposal = proposal with { Kind = CostOperationKind.Issue, Lines = [new ProposedFlowLine(W1.Id, 10m)] } }), ConfigStatus.Conflict, "document_plan_mismatch");
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { Proposal = proposal with { Lines = [new ProposedFlowLine(W1.Id, 5m, 100m), new ProposedFlowLine(W1.Id, 5m, 100m)] } }), ConfigStatus.Conflict, "document_plan_mismatch");
        // a document that is already posted
        var posted = await Approved(Receipt(W1, "2026-03-12", L(Avg, Pcs, 1m)));
        Ok(await Post(posted, Amounts(5m)));
        Refused(await service.CreateAsync(user.Id, Company.Id, new(Avg.Id, W1.Id, posted.Id, new ProposedFlowOperation(Guid.NewGuid(), DateOnly.Parse("2026-03-12"), CostOperationKind.Receipt, [new ProposedFlowLine(W1.Id, 1m, 5m)]))), ConfigStatus.Conflict, "document_already_posted");
        Assert.Equal(0L, await Rows(CP.Table));
        // a document is bound to one live plan: the second plan for it is refused (and the first one stands)
        Assert.True((await service.CreateAsync(user.Id, Company.Id, input)).IsOk);
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { Proposal = proposal with { Id = Guid.NewGuid() } }), ConfigStatus.Conflict, "plan_exists_for_document");
        Assert.Equal(1L, await Rows(CP.Table));
        Assert.Equal(doc.Id, await Sql<Guid>($"SELECT {CP.DocumentId} FROM {CP.Table}"));
    }

    [PostgresRoleFact]
    public async Task A_planner_refusal_is_returned_unchanged_and_nothing_is_written()
    {
        await Prepare();
        var user = await PlanUser();
        var (_, input) = await Event();
        await Sql(PeriodStatus("2026-03-12", 2));   // March Soft Closed: the compensation dates lie in it
        var before = await Footprint();
        Refused(await Service().CreateAsync(user.Id, Company.Id, input), ConfigStatus.Conflict, PostingPeriodCheck.SoftClosed);
        Assert.Equal(before, await Footprint());
    }

    [PostgresRoleFact]
    public async Task A_position_of_an_executed_epoch_is_refused_with_plan_on_epoch_position()
    {
        await PrepareRuntime();
        var epoch = await SimpleEpoch();
        Assert.Null(await Run(epoch.Script));
        var user = await PlanUser();
        var (_, input) = await Event("2026-03-11", 5m, 100m);
        var before = await Footprint();
        Refused(await Service().CreateAsync(user.Id, Company.Id, input), ConfigStatus.Conflict, "plan_on_epoch_position");
        Assert.Equal(before, await Footprint());
    }

    [PostgresRoleFact]
    public async Task Invalid_input_is_refused_before_anything_is_read()
    {
        await Prepare();
        var user = await PlanUser();
        var (_, input) = await Event();
        var service = Service();
        Refused(await service.CreateAsync(user.Id, Company.Id, null!), ConfigStatus.Invalid, "invalid_plan_input");
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { ItemId = Guid.Empty }), ConfigStatus.Invalid, "invalid_plan_input");
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { WarehouseId = Guid.Empty }), ConfigStatus.Invalid, "invalid_plan_input");
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { DocumentId = Guid.Empty }), ConfigStatus.Invalid, "invalid_plan_input");
        Refused(await service.CreateAsync(user.Id, Company.Id, input with { Proposal = input.Proposal with { Lines = ImmutableArray<ProposedFlowLine>.Empty } }), ConfigStatus.Invalid, "invalid_plan_input");
        Assert.Equal(0L, await Rows(CP.Table));
    }
}
