using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;

namespace Hasm.Database.IntegrationTests;

public class CostReadBudgetTests : CostingTestBase
{
    /// <summary>The runtime privilege check of the schema gate is ONE set-based command whatever the number of tables (review L2 of PR 48); it used to be one command per table.</summary>
    private const int PrivilegeChecks = 1;

    private CompanyDatabaseResolver WarmResolver() => new(Configured(true),
        new CompanyRegistry(Options.BuildSystemConnectionString(admin: true)), Credentials, Clock)
    {
        ResolutionLifetime = TimeSpan.FromMinutes(1), SchemaCheckLifetime = TimeSpan.FromMinutes(1),
        ConnectionStringOverride = (db, _, _) => Options.BuildCompanyAdminConnectionString(db)
    };
    private sealed class UnmeteredValidator : IScopeTargetValidator
    {
        public Task<bool> ExistsAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct) => Task.FromResult(true);
    }

    [PostgresFact]
    public async Task Every_scoped_grant_is_charged_and_exhaustion_cannot_be_swallowed_as_an_inert_grant()
    {
        await PrepareCosting(); var resolver = WarmResolver(); await using (var warm = await resolver.OpenAsync(Company.Id)) { }
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Company, null);
        var role = await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "unrelated", [PermissionCatalog.Codes.StockDocsView]);
        Assert.True(role.IsOk, role.Code);
        var warehouses = new[] { W1, W2, await NewWarehouse("W3"), await NewWarehouse("W4"), await NewWarehouse("W5") };
        foreach (var warehouse in warehouses)
            Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, viewer.Id, role.Value!.Id, ScopeKind.Warehouse, warehouse.Id)).IsOk);
        await using var access = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, PermissionCatalog.Default, Audit,
            new CompanyBranchScopeValidator(() => resolver));
        Assert.True(await access.IsAllowedAsync(viewer.Id, Company.Id, PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(W1.Id)));
        var insufficient = new ReadCommandBudget(6, default);
        var exhausted = await Assert.ThrowsAsync<ReadCommandBudgetException>(() => access.IsAllowedAsync(viewer.Id, Company.Id,
            PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(W1.Id), insufficient.Reserve, default));
        Assert.Equal(ReadBudgetFailure.Exhausted, exhausted.Failure); Assert.Equal(6, insufficient.Used);
        var exact = new ReadCommandBudget(7, default); // Two system reads + every one of the five unrelated scope probes.
        Assert.True(await access.IsAllowedAsync(viewer.Id, Company.Id, PermissionCatalog.Codes.StockBalanceView,
            ScopeTarget.Warehouse(W1.Id), exact.Reserve, default)); Assert.Equal(7, exact.Used);
        Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, viewer.Id)).IsOk);
        var fresh = new ReadCommandBudget(7, default);
        Assert.False(await access.IsAllowedAsync(viewer.Id, Company.Id, PermissionCatalog.Codes.StockBalanceView,
            ScopeTarget.Warehouse(W1.Id), fresh.Reserve, default)); Assert.Equal(2, fresh.Used);
    }

    [PostgresFact]
    public async Task Unknown_unbudgeted_validators_fail_closed_only_for_opted_in_operations()
    {
        await PrepareCosting(); var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Warehouse, W1.Id);
        await using var access = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock,
            PermissionCatalog.Default, Audit, new UnmeteredValidator());
        Assert.True(await access.IsAllowedAsync(viewer.Id, Company.Id, PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(W1.Id)));
        var quota = new ReadCommandBudget(1000, default);
        Assert.Equal(ReadBudgetFailure.UnsupportedValidator, (await Assert.ThrowsAsync<ReadCommandBudgetException>(() =>
            access.IsAllowedAsync(viewer.Id, Company.Id, PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(W1.Id), quota.Reserve, default))).Failure);
        var result = await new CostFlowSnapshotService(Resolver, access).LoadAsync(viewer.Id, Company.Id, Fifo.Id, W1.Id);
        Assert.Equal((ConfigStatus.Conflict, "impact_query_budget_unavailable"), (result.Status, result.Code));
        Assert.Null(result.Value); Assert.Null(result.Fields);
    }

    [PostgresFact]
    public async Task Cold_resolver_schema_queries_are_charged_and_incomplete_verification_is_never_cached()
    {
        await PrepareCosting(); var resolver = WarmResolver(); var small = new ReadCommandBudget(3, default);
        Assert.Equal(ReadBudgetFailure.Exhausted, (await Assert.ThrowsAsync<ReadCommandBudgetException>(() =>
            resolver.OpenAsync(Company.Id, small.Reserve, default))).Failure); Assert.Equal(3, small.Used);
        var finish = new ReadCommandBudget(1000, default);
        await using (var connection = await resolver.OpenAsync(Company.Id, finish.Reserve, default)) { }
        Assert.Equal(PrivilegeChecks + 5, finish.Used); // Cached registry, complete cold schema/privilege/main-branch checks (the privilege check is ONE set-based command).
        var warm = new ReadCommandBudget(1, default);
        await using (var connection = await resolver.OpenAsync(Company.Id, warm.Reserve, default)) { }
        Assert.Equal(0, warm.Used);
        resolver.Forget(Company.Id); var cold = new ReadCommandBudget(1000, default);
        await using (var connection = await resolver.OpenAsync(Company.Id, cold.Reserve, default)) { }
        Assert.Equal(PrivilegeChecks + 6, cold.Used); // Registry is now cold too.
    }

    [PostgresFact]
    public async Task Final_authorization_consumes_the_same_quota_and_cannot_return_unchecked_cost_data()
    {
        await PrepareCosting(); await Receive(Fifo, 2, 2); var resolver = WarmResolver();
        await using (var warm = await resolver.OpenAsync(Company.Id)) { }
        var proposal = new ProposedFlowOperation(Guid.NewGuid(), D2, CostOperationKind.Issue, [new(W1.Id, 1)]);
        async Task<(ConfigResult<CostFlowComparisonResult> Result, bool Final)> Run(int limit)
        {
            var reachedFinal = false; var reader = new CostFlowSnapshotService(resolver, Rbac, new(Commands: limit));
            reader.BeforeFinalAuthorization = _ => { reachedFinal = true; return Task.CompletedTask; };
            return (await reader.PreviewAsync(Admin.Id, Company.Id, Fifo.Id, proposal), reachedFinal);
        }
        var upper = await Run(1000); Assert.True(upper.Result.IsOk, upper.Result.Code);
        var low = 1; var high = 1000;
        while (low < high)
        {
            var middle = (low + high) / 2; var attempt = await Run(middle);
            if (attempt.Result.IsOk) high = middle;
            else { Assert.Equal("impact_history_too_large", attempt.Result.Code); low = middle + 1; }
        }
        var boundary = await Run(low - 1);
        Assert.True(boundary.Final); Assert.Equal((ConfigStatus.Conflict, "impact_history_too_large"), (boundary.Result.Status, boundary.Result.Code));
        Assert.Null(boundary.Result.Value); Assert.Null(boundary.Result.Fields);
        var exact = await Run(low); Assert.True(exact.Result.IsOk, exact.Result.Code);
        Assert.Equal(upper.Result.Value!.ComparisonFingerprint, exact.Result.Value!.ComparisonFingerprint); await AssertConsistent();
    }
}
