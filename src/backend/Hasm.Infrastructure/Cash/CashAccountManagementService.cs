using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Cash;

/// <summary>
/// Authorized entry point for cash and bank accounts (the only thing the API calls). Permissions are re-read from Hasm_System on every call and are BRANCH-scoped: an account of a branch the caller does not hold is never listed, never
/// returned and never changed, and answers exactly like an unknown id (404), so its existence is not revealed. A caller who holds no relevant permission anywhere gets 403. Creating needs <c>create</c> on the account's branch; updating
/// needs <c>update</c> on its branch (and on the target branch when the branch changes); activating and deactivating need <c>activate</c>; the FULL account number needs <c>company.cashaccountssensitive.view</c> on the branch (plus view).
/// The business rules live in <see cref="CashAccountService"/> and PostgreSQL; the client is never trusted for any of them. This module posts nothing to the ledger (D-214).
/// </summary>
public sealed class CashAccountManagementService(AccessService access, CashAccountService accounts)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private static ConfigResult<T> Unknown<T>() => ConfigResult<T>.Fail(ConfigStatus.NotFound, "cash_account_not_found");

    /// <summary>The accounts of the branches the caller may view (the server authorizes every action again; the endpoint adds what the caller may do per branch as UI hints).</summary>
    public async Task<ConfigResult<IReadOnlyList<CashAccountView>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashAccountsView)) return Denied<IReadOnlyList<CashAccountView>>();
        var all = await accounts.ListAsync(companyId, ct);
        return ConfigResult<IReadOnlyList<CashAccountView>>.Ok([.. all.Where(a => eff.Has(PermissionCatalog.Codes.CashAccountsView, ScopeTarget.Branch(a.Data.BranchId)))]);
    }

    /// <summary>The candidate ledger accounts for a binding: for a caller who may create or update a cash or bank account on some branch (it reveals asset account codes and names, which the caller must choose from).</summary>
    public async Task<ConfigResult<(IReadOnlyList<GlCandidate> Items, long Total)>> CandidatesAsync(Guid userId, Guid companyId, string? search, int limit, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashAccountsCreate) && !eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashAccountsUpdate)) return Denied<(IReadOnlyList<GlCandidate>, long)>();
        return ConfigResult<(IReadOnlyList<GlCandidate>, long)>.Ok(await accounts.CandidatesAsync(companyId, search, Math.Clamp(limit, 1, 50), ct));
    }

    public async Task<ConfigResult<CashAccountView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashAccountsView)) return Denied<CashAccountView>();
        var a = await accounts.GetAsync(companyId, id, ct);
        return a is null || !eff.Has(PermissionCatalog.Codes.CashAccountsView, ScopeTarget.Branch(a.Data.BranchId)) ? Unknown<CashAccountView>() : ConfigResult<CashAccountView>.Ok(a);
    }

    public async Task<ConfigResult<CashAccountView>> CreateAsync(Guid userId, Guid companyId, CashAccountInput? input, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashAccountsCreate)) return Denied<CashAccountView>();
        // the branch is checked BEFORE anything is validated or written: no probing of other branches, no write on a branch the caller does not hold
        if (input?.BranchId is { } b && b != Guid.Empty && !eff.Has(PermissionCatalog.Codes.CashAccountsCreate, ScopeTarget.Branch(b))) return Denied<CashAccountView>();
        return await accounts.CreateAsync(userId, companyId, input, ct);
    }

    public async Task<ConfigResult<CashAccountView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, CashAccountInput? input, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashAccountsUpdate)) return Denied<CashAccountView>();
        var current = await accounts.GetAsync(companyId, id, ct);
        if (current is null || !eff.Has(PermissionCatalog.Codes.CashAccountsUpdate, ScopeTarget.Branch(current.Data.BranchId))) return Unknown<CashAccountView>();
        // the revealed number is a separate permission: a caller without it can still change the other fields and can REPLACE the number with a full new value, but never read the old one (the masked form means "unchanged")
        return await accounts.UpdateAsync(userId, companyId, id, expectedRevision, input, branch => Task.FromResult(eff.Has(PermissionCatalog.Codes.CashAccountsUpdate, ScopeTarget.Branch(branch))), ct);
    }

    public async Task<ConfigResult<CashAccountView>> DeactivateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        await ActivateCheckAsync(userId, companyId, id, ct) is { } denied ? denied : await accounts.DeactivateAsync(userId, companyId, id, expectedRevision, ct);

    public async Task<ConfigResult<CashAccountView>> ReactivateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        await ActivateCheckAsync(userId, companyId, id, ct) is { } denied ? denied : await accounts.ReactivateAsync(userId, companyId, id, expectedRevision, ct);

    private async Task<ConfigResult<CashAccountView>?> ActivateCheckAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashAccountsActivate)) return Denied<CashAccountView>();
        var current = await accounts.GetAsync(companyId, id, ct);
        return current is null || !eff.Has(PermissionCatalog.Codes.CashAccountsActivate, ScopeTarget.Branch(current.Data.BranchId)) ? Unknown<CashAccountView>() : null;
    }

    /// <summary>The FULL account number (audited): needs the sensitive permission on the account's branch AND the view permission there.</summary>
    public async Task<ConfigResult<string>> RevealNumberAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.CashAccountsSensitiveView)) return Denied<string>();
        var a = await accounts.GetAsync(companyId, id, ct);
        if (a is null || !eff.Has(PermissionCatalog.Codes.CashAccountsView, ScopeTarget.Branch(a.Data.BranchId))) return Unknown<string>();
        if (!eff.Has(PermissionCatalog.Codes.CashAccountsSensitiveView, ScopeTarget.Branch(a.Data.BranchId))) return Denied<string>();
        return await accounts.RevealNumberAsync(userId, companyId, id, ct);
    }
}
