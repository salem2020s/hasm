using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// Authorized entry point for the journal (the only thing the API and the automatic-posting modules call). Permissions are re-read from Hasm_System on EVERY call
/// (no cache), so a revocation takes effect before the next approval or posting executes. Journal permissions are branch-scoped: an operation needs the permission for the
/// entry's branch and for every distinct line branch (a company-wide grant covers all). Soft-closed posting and the exchange-rate override are separate company-wide permissions.
/// Business rules (balance, approvals bound to the revision, period, accounts, rates, cost centers, immutability) live in <see cref="JournalService"/> and PostgreSQL.
/// </summary>
public sealed class JournalManagementService(AccessService access, JournalService journal)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private async Task<bool> AllBranches(Guid userId, Guid companyId, string code, IEnumerable<Guid> branches, CancellationToken ct)
    {
        foreach (var b in branches.Distinct())
            if (!await access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Branch(b), ct)) return false;
        return true;
    }

    private static IEnumerable<Guid> BranchesOf(JournalInput input) => new[] { input.BranchId }.Concat((input.Lines ?? []).Select(l => l.BranchId)).Where(b => b is not null).Select(b => b!.Value);

    private static IEnumerable<Guid> BranchesOf(JournalEntryView v) => new[] { v.Header.BranchId }.Concat(v.Lines.Select(l => l.BranchId));

    private Task<bool> MayOverrideRate(Guid userId, Guid companyId, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.JournalRateOverride, ScopeTarget.Company, ct);

    /// <summary>Loads the entry and authorizes it against one permission on every branch it touches. NotFound is only reported to callers who could have seen entries at all.</summary>
    private async Task<ConfigResult<JournalEntryView>> Authorized(Guid userId, Guid companyId, Guid id, string permission, CancellationToken ct)
    {
        var view = await journal.GetAsync(companyId, id, ct);
        if (view is null) return await access.IsAllowedAsync(userId, companyId, permission, ScopeTarget.Company, ct)
            ? ConfigResult<JournalEntryView>.Fail(ConfigStatus.NotFound, "journal_entry_not_found") : Denied<JournalEntryView>();
        return await AllBranches(userId, companyId, permission, BranchesOf(view), ct) ? ConfigResult<JournalEntryView>.Ok(view) : Denied<JournalEntryView>();
    }

    public async Task<ConfigResult<JournalEntryView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default) =>
        await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalView, ct);

    /// <summary>Lists the entries whose branch the caller may view (a branch-scoped viewer never sees other branches).</summary>
    public async Task<ConfigResult<IReadOnlyList<JournalEntrySummary>>> ListAsync(Guid userId, Guid companyId, JournalFilter filter, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        IReadOnlyList<Guid>? allowed = null; // null = company-wide view permission
        if (!eff.Has(PermissionCatalog.Codes.JournalView, ScopeTarget.Company))
        {
            allowed = eff.Grants.Where(g => g.Scope == ScopeKind.Branch && g.ScopeId is not null && g.Permissions.Contains(PermissionCatalog.Codes.JournalView)).Select(g => g.ScopeId!.Value).Distinct().ToList();
            if (allowed.Count == 0) return Denied<IReadOnlyList<JournalEntrySummary>>();
        }
        // The restriction runs inside the query, before ORDER BY / LIMIT / OFFSET, so paging only ever walks entries the caller may see.
        return ConfigResult<IReadOnlyList<JournalEntrySummary>>.Ok(await journal.ListAsync(companyId, filter with { AllowedBranches = allowed }, ct));
    }

    public async Task<ConfigResult<JournalEntryView>> CreateAsync(Guid userId, Guid companyId, JournalInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<JournalEntryView>.Invalid([new("body", "required")]);
        var branches = BranchesOf(input).ToList();
        var allowed = branches.Count == 0 ? await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.JournalCreate, ScopeTarget.Company, ct) : await AllBranches(userId, companyId, PermissionCatalog.Codes.JournalCreate, branches, ct);
        if (!allowed) return Denied<JournalEntryView>();
        if (input.AdjustsId is { } adj)
        {
            var target = await journal.GetAsync(companyId, adj, ct);
            if (target is not null && !await AllBranches(userId, companyId, PermissionCatalog.Codes.JournalView, BranchesOf(target), ct)) return Denied<JournalEntryView>();
        }
        return await journal.CreateDraftAsync(userId, companyId, input, await MayOverrideRate(userId, companyId, ct), ct);
    }

    /// <summary>An automatic entry from a business event, on behalf of the initiating user (who needs the create permission for the branches). Same draft, approval and posting rules as a manual entry.</summary>
    public async Task<ConfigResult<JournalEntryView>> CreateAutomaticAsync(Guid userId, Guid companyId, AutomaticJournalInput? input, CancellationToken ct = default)
    {
        if (input?.Entry is null) return ConfigResult<JournalEntryView>.Invalid([new("entry", "required")]);
        var branches = BranchesOf(input.Entry).ToList();
        if (branches.Count == 0 || !await AllBranches(userId, companyId, PermissionCatalog.Codes.JournalCreate, branches, ct)) return Denied<JournalEntryView>();
        return await journal.CreateAutomaticAsync(userId, companyId, input, await MayOverrideRate(userId, companyId, ct), ct);
    }

    public async Task<ConfigResult<JournalEntryView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? revision, JournalInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<JournalEntryView>.Invalid([new("body", "required")]);
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalUpdate, ct);
        if (!cur.IsOk) return cur;
        if (BranchesOf(input).Any() && !await AllBranches(userId, companyId, PermissionCatalog.Codes.JournalUpdate, BranchesOf(input), ct)) return Denied<JournalEntryView>();
        return await journal.UpdateDraftAsync(userId, companyId, id, revision, input, await MayOverrideRate(userId, companyId, ct), ct);
    }

    public async Task<ConfigResult<JournalEntryView>> SubmitAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalSubmit, ct);
        return cur.IsOk ? await journal.SubmitAsync(userId, companyId, id, revision, ct) : cur;
    }

    public async Task<ConfigResult<JournalEntryView>> WithdrawAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalSubmit, ct);
        return cur.IsOk ? await journal.WithdrawAsync(userId, companyId, id, revision, ct) : cur;
    }

    public async Task<ConfigResult<JournalEntryView>> CancelAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalUpdate, ct);
        return cur.IsOk ? await journal.CancelAsync(userId, companyId, id, revision, ct) : cur;
    }

    /// <summary>Approve or reject. The approve permission is re-checked here on every call, immediately before the decision is recorded.</summary>
    public async Task<ConfigResult<JournalEntryView>> DecideAsync(Guid userId, Guid companyId, Guid id, long? revision, ApprovalDecisionInput? input, CancellationToken ct = default)
    {
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalApprove, ct);
        return cur.IsOk ? await journal.DecideAsync(userId, companyId, id, revision, input, ct) : cur;
    }

    public async Task<ConfigResult<JournalEntryView>> PostAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalPost, ct);
        if (!cur.IsOk) return cur;
        var soft = await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.JournalSoftClosedPost, ScopeTarget.Company, ct);
        return await journal.PostAsync(userId, companyId, id, revision, soft, ct);
    }

    public async Task<ConfigResult<JournalEntryView>> ReverseAsync(Guid userId, Guid companyId, Guid id, long? revision, ReversalInput? input, CancellationToken ct = default)
    {
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalReverse, ct);
        return cur.IsOk ? await journal.ReverseAsync(userId, companyId, id, revision, input, ct) : cur;
    }
}
