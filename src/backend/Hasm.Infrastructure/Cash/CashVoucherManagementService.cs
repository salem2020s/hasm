using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Cash;

/// <summary>
/// Authorized entry point for cash vouchers (the only thing the API calls). Permissions are re-read from Hasm_System on every call and are BRANCH-scoped: a voucher belongs to the branch of its cash or bank account, and a voucher of
/// a branch the caller does not hold is never listed, never returned and never changed, and answers exactly like an unknown id (404). A caller who holds the permission of the action nowhere gets 403 before the company database
/// is asked anything. Creating needs <c>create</c> on the branch of the chosen cash account (an account of another branch answers like an unknown account); updating needs <c>update</c>; submitting needs
/// <c>submit</c>; cancelling needs <c>cancel</c>. The business rules live in <see cref="CashVoucherService"/> and PostgreSQL; the client is never trusted for any of them. Nothing here approves, numbers or posts (D-214).
/// </summary>
public sealed class CashVoucherManagementService(AccessService access, CashVoucherService vouchers)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private static ConfigResult<T> Unknown<T>() => ConfigResult<T>.Fail(ConfigStatus.NotFound, "cash_voucher_not_found");

    /// <summary>403 when the caller holds the permission nowhere; 404 when the voucher does not exist or belongs to a branch the caller does not hold it on; otherwise null (authorized). The branch of a voucher never changes.</summary>
    private async Task<ConfigResult<CashVoucherView>?> Refusal(Guid userId, Guid companyId, Guid id, string code, CancellationToken ct)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(code)) return Denied<CashVoucherView>();
        var current = await vouchers.GetAsync(companyId, id, ct);
        return current is null || !eff.Has(code, ScopeTarget.Branch(current.BranchId)) ? Unknown<CashVoucherView>() : null;
    }

    public async Task<ConfigResult<CashVoucherView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashVouchersView)) return Denied<CashVoucherView>();
        var v = await vouchers.GetAsync(companyId, id, ct);
        return v is null || !eff.Has(PermissionCatalog.Codes.CashVouchersView, ScopeTarget.Branch(v.BranchId)) ? Unknown<CashVoucherView>() : ConfigResult<CashVoucherView>.Ok(v);
    }

    /// <summary>Lists the vouchers of the branches the caller may view (the restriction runs inside the query, before paging).</summary>
    public async Task<ConfigResult<IReadOnlyList<CashVoucherView>>> ListAsync(Guid userId, Guid companyId, CashVoucherFilter filter, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        IReadOnlyList<Guid>? allowed = null;   // null = company-wide view
        if (!eff.Has(PermissionCatalog.Codes.CashVouchersView, ScopeTarget.Company))
        {
            allowed = [.. eff.Grants.Where(g => g.Scope == ScopeKind.Branch && g.ScopeId is not null && g.Permissions.Contains(PermissionCatalog.Codes.CashVouchersView)).Select(g => g.ScopeId!.Value).Distinct()];
            if (allowed.Count == 0) return Denied<IReadOnlyList<CashVoucherView>>();
        }
        return ConfigResult<IReadOnlyList<CashVoucherView>>.Ok(await vouchers.ListAsync(companyId, filter with { AllowedBranches = allowed }, ct));
    }

    public async Task<ConfigResult<CashVoucherView>> CreateAsync(Guid userId, Guid companyId, CashVoucherInput? input, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashVouchersCreate)) return Denied<CashVoucherView>();
        // the branch is the cash account's: it is authorized inside the transaction, on the branch read under the lock, and an account of a branch the caller does not hold answers like an unknown account
        var created = await vouchers.CreateAsync(userId, companyId, input, branch => eff.Has(PermissionCatalog.Codes.CashVouchersCreate, ScopeTarget.Branch(branch)), ct);
        // a REPLAY returns the current state of the voucher: a caller who may create but no longer view it (or create on its branch) receives only the identifiers, never the party, texts, lines or history
        if (created.IsOk && created.Value!.Replayed && !(eff.Has(PermissionCatalog.Codes.CashVouchersView, ScopeTarget.Branch(created.Value.BranchId)) && eff.Has(PermissionCatalog.Codes.CashVouchersCreate, ScopeTarget.Branch(created.Value.BranchId))))
            return ConfigResult<CashVoucherView>.Ok(created.Value with { PartyId = null, Reference = null, Note = null, SubmittedBy = null, Total = 0m, Lines = [], Events = [] });
        return created;
    }

    public async Task<ConfigResult<CashVoucherView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? revision, CashVoucherInput? input, CancellationToken ct = default) =>
        await Refusal(userId, companyId, id, PermissionCatalog.Codes.CashVouchersUpdate, ct) ?? await vouchers.UpdateAsync(userId, companyId, id, revision, input, ct);

    public async Task<ConfigResult<CashVoucherView>> SubmitAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Refusal(userId, companyId, id, PermissionCatalog.Codes.CashVouchersSubmit, ct) ?? await vouchers.SubmitAsync(userId, companyId, id, revision, ct);

    public async Task<ConfigResult<CashVoucherView>> CancelAsync(Guid userId, Guid companyId, Guid id, long? revision, string? reason, CancellationToken ct = default) =>
        await Refusal(userId, companyId, id, PermissionCatalog.Codes.CashVouchersCancel, ct) ?? await vouchers.CancelAsync(userId, companyId, id, revision, reason, ct);
}
