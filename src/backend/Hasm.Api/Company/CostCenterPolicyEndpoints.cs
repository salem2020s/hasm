using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/cost-center-policies: which accounts REQUIRE a cost center and which branches a cost center is eligible for (Phase 3.2C S1). Company from the server-side session only;
/// authorized inside <see cref="CostCenterPolicyManagementService"/> (company-wide <c>company.costcenterpolicies.view|configure</c>); changes use If-Match with the rule revision (0 = never configured).
/// </summary>
public static class CostCenterPolicyEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 64 * 1024; }

    public static void MapCostCenterPolicyEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/cost-center-policies").RequireAuthorization();
        g.MapGet("/accounts", ListAccounts);
        g.MapGet("/accounts/{accountId:guid}", GetAccount);
        g.MapPut("/accounts/{accountId:guid}", SetAccount).WithMetadata(new BodyLimit());
        g.MapGet("/branches", ListBranches);
        g.MapGet("/branches/{costCenterId:guid}", GetBranch);
        g.MapPut("/branches/{costCenterId:guid}", SetBranch).WithMetadata(new BodyLimit());
    }

    private static object AccountBody(AccountCostCenterRuleView v) => new { accountId = v.AccountId, code = v.Code, nameAr = v.NameAr, nameEn = v.NameEn, required = v.Required, revision = v.Revision };
    private static object BranchBody(CostCenterBranchRuleView v) => new { costCenterId = v.CostCenterId, restricted = v.Restricted, branchIds = v.BranchIds, revision = v.Revision };

    private static async Task<IResult> ListAccounts(HttpContext http, AccessService access, CostCenterPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListRequiredAccountsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { accounts = r.Value!.Select(AccountBody), can = new { configure = eff.Has(PermissionCatalog.Codes.CostCenterPoliciesConfigure, ScopeTarget.Company) } });
    }

    private static async Task<IResult> GetAccount(Guid accountId, HttpContext http, AccessService access, CostCenterPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAccountRuleAsync(actor!.UserId, actor.CompanyId, accountId, http.RequestAborted), AccountBody, v => v.Revision);
    }

    private static async Task<IResult> SetAccount(Guid accountId, AccountCostCenterRuleInput? body, HttpContext http, AccessService access, CostCenterPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.SetAccountRuleAsync(actor!.UserId, actor.CompanyId, accountId, expected, body, http.RequestAborted), AccountBody, v => v.Revision);
    }

    private static async Task<IResult> ListBranches(HttpContext http, AccessService access, CostCenterPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListRestrictedAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { costCenters = r.Value!.Select(BranchBody), can = new { configure = eff.Has(PermissionCatalog.Codes.CostCenterPoliciesConfigure, ScopeTarget.Company) } });
    }

    private static async Task<IResult> GetBranch(Guid costCenterId, HttpContext http, AccessService access, CostCenterPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetBranchRuleAsync(actor!.UserId, actor.CompanyId, costCenterId, http.RequestAborted), BranchBody, v => v.Revision);
    }

    private static async Task<IResult> SetBranch(Guid costCenterId, CostCenterBranchRuleInput? body, HttpContext http, AccessService access, CostCenterPolicyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.SetBranchRuleAsync(actor!.UserId, actor.CompanyId, costCenterId, expected, body, http.RequestAborted), BranchBody, v => v.Revision);
    }
}
