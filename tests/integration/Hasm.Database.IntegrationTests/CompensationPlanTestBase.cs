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

/// <summary>Shared arrangement of the plan storage and approval service tests (Phase 3.8D step 5): real runtime role, history with posted journals, users of test-created roles that hold the plan codes.</summary>
public abstract class CompensationPlanTestBase : PlanExecutionTestBase
{
    protected static readonly string[] PlannerPermissions = [PermissionCatalog.Codes.StockBalanceView, PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.AccountsView, PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.CostCentersView];

    private protected CompensationPlanService Service() => new(RealResolver(), Rbac, RuntimeTx, new CostFlowSnapshotService(RealResolver(), Rbac, null), Clock);

    /// <summary>A member of a test-created role. No preset holds the plan codes (owner decision X4 is open), so the Company Administrator cannot grant them through the application (cannot_grant_permission, which is itself
    /// the intended consequence): the test adds the codes to the role's permission rows directly, as an owner of the system database would.</summary>
    protected async Task<UserRecord> PlanMember(string[] ordinary, string[] planCodes, ScopeKind scope = ScopeKind.Company, Guid? scopeId = null)
    {
        var (user, roleId) = await Member(ordinary, scope, scopeId);
        foreach (var code in planCodes) await Exec($"INSERT INTO {Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission.Table} ({Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission.RoleId}, {Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission.Permission}) VALUES (@r, @p)", ("r", roleId), ("p", code));
        return user;
    }

    protected Task<UserRecord> PlanUser(params string[] extra) => PlanMember([.. PlannerPermissions, .. extra], [PermissionCatalog.Codes.CompensationPlansCreate]);

    /// <summary>History of AVG1 in W1 with both journals Posted (the planner refuses an unposted effect): receipt of 10 for 100.00 on 10 March, issue of 4 on 12 March.</summary>
    protected async Task Prepare()
    {
        await PrepareRuntime();
        await PostJournal(await ReceiveDoc(Avg, 10m, 100m, "2026-03-10"));
        await PostJournal(await IssueDoc(Avg, 4m, "2026-03-12"));
    }

    /// <summary>The approved receipt document of <paramref name="quantity"/> pieces of AVG1 into W1 and the event the plan proposes for it.</summary>
    private protected async Task<(StockDocumentView Doc, CompensationPlanInput Input)> Event(string date = "2026-03-11", decimal quantity = 10m, decimal amount = 200m)
    {
        var doc = await Approved(Receipt(W1, date, L(Avg, Pcs, quantity)));
        return (doc, new CompensationPlanInput(Avg.Id, W1.Id, doc.Id, new ProposedFlowOperation(Guid.NewGuid(), DateOnly.Parse(date), CostOperationKind.Receipt, [new ProposedFlowLine(W1.Id, quantity, amount)])));
    }

    protected async Task<long[]> Footprint() =>
        [await Rows(EN.Table), await Rows(OP.Table), await Rows(JE.Table), await Rows(SP.Table), await Rows(CP.Table), await Rows(CC.Table), await Rows(CL.Table), await Rows(CE.Table), await Rows(AU.Table)];

    protected static void Refused<T>(ConfigResult<T> r, ConfigStatus status, string code) { Assert.Equal((status, code), (r.Status, r.Code)); Assert.Null(r.Value); }

    private protected CompensationPlanApprovalService Approval() => new(RealResolver(), Rbac, RuntimeTx, new CostFlowSnapshotService(RealResolver(), Rbac, null), Clock);

    /// <summary>A user who can approve plans (and run the planner) on every warehouse; a different person from the creator and from the people of the event document.</summary>
    protected Task<UserRecord> ApproverUser() => PlanMember(PlannerPermissions, [PermissionCatalog.Codes.CompensationPlansApprove]);

    /// <summary>A proposed plan created by <paramref name="creator"/> for a fresh approved receipt document of 10 pieces on 11 March (event amount 200.00).</summary>
    private protected async Task<(StockDocumentView Doc, CompensationPlanInput Input, StoredCompensationPlan Plan)> Proposed(UserRecord creator, string date = "2026-03-11")
    {
        var (doc, input) = await Event(date);
        var created = await Service().CreateAsync(creator.Id, Company.Id, input);
        Assert.True(created.IsOk, created.Code);
        return (doc, input, created.Value!);
    }

    protected Task<short> PlanStatus(Guid plan) => Sql<short>($"SELECT {CP.Status} FROM {CP.Table} WHERE {CP.Id} = '{plan}'");
    protected Task<long> PlanRevision(Guid plan) => Sql<long>($"SELECT {CP.Revision} FROM {CP.Table} WHERE {CP.Id} = '{plan}'");
    protected Task<long> EventCount(Guid plan, short kind) => Sql<long>($"SELECT count(*) FROM {CE.Table} WHERE {CE.PlanId} = '{plan}' AND {CE.Kind} = {kind}");
    protected Task<long> AuditCount(short type, Guid plan) => Sql<long>($"SELECT count(*) FROM {AU.Table} WHERE {AU.EventType} = {type} AND {AU.SubjectId} = '{plan}'");
}
