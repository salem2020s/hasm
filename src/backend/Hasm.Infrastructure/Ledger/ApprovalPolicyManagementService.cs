using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>Authorized entry point for approval policies (the only thing the API calls). Company-wide permissions, re-read on every call: view, create, update and activate/deactivate are separate. Approving a DOCUMENT is a different, later permission.</summary>
public sealed class ApprovalPolicyManagementService(AccessService access, ApprovalPolicyService policies)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<IReadOnlyList<ApprovalPolicyView>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ApprovalPoliciesView, ct) ? ConfigResult<IReadOnlyList<ApprovalPolicyView>>.Ok(await policies.ListAsync(companyId, ct)) : Denied<IReadOnlyList<ApprovalPolicyView>>();

    public async Task<ConfigResult<ApprovalPolicyView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.ApprovalPoliciesView, ct)) return Denied<ApprovalPolicyView>();
        var p = await policies.GetAsync(companyId, id, ct);
        return p is null ? ConfigResult<ApprovalPolicyView>.Fail(ConfigStatus.NotFound, "approval_policy_not_found") : ConfigResult<ApprovalPolicyView>.Ok(p);
    }

    /// <summary>A stage bound to a role needs a role that exists in this company, is active and holds the journal approval permission (otherwise nobody could ever complete the stage).</summary>
    private async Task<ConfigResult<ApprovalPolicyView>?> CheckStageRolesAsync(Guid companyId, IReadOnlyList<ApprovalStageInput>? stages, CancellationToken ct)
    {
        for (var i = 0; i < (stages?.Count ?? 0); i++)
            if (stages![i].RoleId is { } role && role != Guid.Empty && !await access.RoleHoldsPermissionAsync(companyId, role, PermissionCatalog.Codes.JournalApprove, ct))
                return ConfigResult<ApprovalPolicyView>.Invalid([new($"stages[{i}].roleId", "role_cannot_approve")]);
        return null;
    }

    public async Task<ConfigResult<ApprovalPolicyView>> CreateAsync(Guid userId, Guid companyId, ApprovalPolicyInput? input, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.ApprovalPoliciesCreate, ct)) return Denied<ApprovalPolicyView>();
        return await CheckStageRolesAsync(companyId, input?.Stages, ct) ?? await policies.CreateAsync(userId, companyId, input, ct);
    }

    public async Task<ConfigResult<ApprovalPolicyView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? revision, ApprovalPolicyDetailsInput? input, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.ApprovalPoliciesUpdate, ct)) return Denied<ApprovalPolicyView>();
        return await CheckStageRolesAsync(companyId, input?.Stages, ct) ?? await policies.UpdateAsync(userId, companyId, id, revision, input, ct);
    }

    public async Task<ConfigResult<ApprovalPolicyView>> DeactivateAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ApprovalPoliciesActivate, ct) ? await policies.DeactivateAsync(userId, companyId, id, revision, ct) : Denied<ApprovalPolicyView>();

    public async Task<ConfigResult<ApprovalPolicyView>> ReactivateAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ApprovalPoliciesActivate, ct) ? await policies.ReactivateAsync(userId, companyId, id, revision, ct) : Denied<ApprovalPolicyView>();
}
