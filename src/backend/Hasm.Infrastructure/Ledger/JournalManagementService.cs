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

    /// <summary>What an operation touches: every branch (header and lines) and, per line, the cost centers it is allocated to.</summary>
    private sealed record Shape(IReadOnlyList<Guid> Branches, IReadOnlyList<IReadOnlyList<Guid>> LineCostCenters);

    /// <summary>
    /// THE one authorization rule for journal entries (used by listing, details, every operation and, through the same grants, the reports). A permission covers a shape when
    /// (1) it is held company-wide, or (2) BRANCH path: it is held for the entry's branch and every line branch, or (3) COST-CENTER path: it is held (cost-center-scoped, covering the
    /// granted cost center and its descendants) and EVERY line is allocated and ALL its allocations lie inside the granted cost centers. The paths are alternatives: a branch grant is
    /// never narrowed by cost-center grants and vice versa; company-wide access stays distinct from both.
    /// </summary>
    private async Task<bool> CoversAsync(Guid userId, Guid companyId, string code, Shape shape, CancellationToken ct)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (eff.Has(code, ScopeTarget.Company)) return true;
        var branches = shape.Branches.Distinct().ToList();
        if (branches.Count > 0 && branches.All(b => eff.Has(code, ScopeTarget.Branch(b)))) return true;
        var granted = CostCenterRoots(eff, code);
        if (granted.Count == 0 || shape.LineCostCenters.Count == 0 || shape.LineCostCenters.Any(l => l.Count == 0)) return false;
        var closure = (await journal.CostCenterClosureAsync(companyId, granted, ct)).ToHashSet();
        return shape.LineCostCenters.All(l => l.All(closure.Contains));
    }

    internal static List<Guid> CostCenterRoots(EffectivePermissions eff, string code) =>
        [.. eff.Grants.Where(g => g.Scope == ScopeKind.CostCenter && g.ScopeId is not null && g.Permissions.Contains(code)).Select(g => g.ScopeId!.Value).Distinct()];

    /// <summary>
    /// The STAGE AUTHORITY of a caller for one entry: the roles held through assignments that cover the entry by the same three paths as a permission (company-wide; for the entry's branch and every line branch;
    /// or cost-center-scoped with every line allocated inside the granted cost centers). A role held only for another branch gives no authority over a stage of this entry: neither to approve nor to reject.
    /// </summary>
    private Task<IReadOnlySet<Guid>> StageRolesAsync(EffectivePermissions eff, Guid companyId, Shape shape, CancellationToken ct) =>
        StageRolesAsync(eff, shape.Branches, shape.LineCostCenters, async roots => (await journal.CostCenterClosureAsync(companyId, roots, ct)).ToHashSet());

    /// <summary>The rule itself, without the database: <paramref name="closureOf"/> answers the granted cost centers with their descendants.</summary>
    internal static async Task<IReadOnlySet<Guid>> StageRolesAsync(EffectivePermissions eff, IReadOnlyList<Guid> touchedBranches, IReadOnlyList<IReadOnlyList<Guid>> lineCostCenters, Func<IReadOnlyList<Guid>, Task<HashSet<Guid>>> closureOf)
    {
        var branches = touchedBranches.Distinct().ToList();
        var roles = new HashSet<Guid>();
        foreach (var held in eff.Grants.GroupBy(g => g.RoleId))
        {
            if (held.Any(g => g.Scope == ScopeKind.Company) || (branches.Count > 0 && branches.All(b => held.Any(g => g.Scope == ScopeKind.Branch && g.ScopeId == b)))) { roles.Add(held.Key); continue; }
            var roots = held.Where(g => g.Scope == ScopeKind.CostCenter && g.ScopeId is not null).Select(g => g.ScopeId!.Value).Distinct().ToList();
            if (roots.Count == 0 || lineCostCenters.Count == 0 || lineCostCenters.Any(l => l.Count == 0)) continue;
            var closure = await closureOf(roots);
            if (lineCostCenters.All(l => l.All(closure.Contains))) roles.Add(held.Key);
        }
        return roles;
    }

    private static Shape ShapeOf(JournalInput input) => new(
        new[] { input.BranchId }.Concat((input.Lines ?? []).Select(l => l.BranchId)).Where(b => b is not null).Select(b => b!.Value).ToList(),
        (input.Lines ?? []).Select(l => (IReadOnlyList<Guid>)(l.Allocations ?? []).Where(a => a.CostCenterId is not null).Select(a => a.CostCenterId!.Value).Distinct().ToList()).ToList());

    private static Shape ShapeOf(JournalEntryView v) => new(
        new[] { v.Header.BranchId }.Concat(v.Lines.Select(l => l.BranchId)).ToList(),
        v.Lines.Select(l => (IReadOnlyList<Guid>)l.Allocations.Select(a => a.CostCenterId).Distinct().ToList()).ToList());

    private Task<bool> MayOverrideRate(Guid userId, Guid companyId, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.JournalRateOverride, ScopeTarget.Company, ct);

    /// <summary>Loads the entry and authorizes it against one permission on every branch it touches. NotFound is only reported to callers who could have seen entries at all.</summary>
    private async Task<ConfigResult<JournalEntryView>> Authorized(Guid userId, Guid companyId, Guid id, string permission, CancellationToken ct)
    {
        var view = await journal.GetAsync(companyId, id, ct);
        if (view is null) return await access.IsAllowedAsync(userId, companyId, permission, ScopeTarget.Company, ct)
            ? ConfigResult<JournalEntryView>.Fail(ConfigStatus.NotFound, "journal_entry_not_found") : Denied<JournalEntryView>();
        return await CoversAsync(userId, companyId, permission, ShapeOf(view), ct) ? ConfigResult<JournalEntryView>.Ok(view) : Denied<JournalEntryView>();
    }

    public async Task<ConfigResult<JournalEntryView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default) =>
        await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalView, ct);

    /// <summary>Lists the entries whose branch the caller may view (a branch-scoped viewer never sees other branches).</summary>
    public async Task<ConfigResult<IReadOnlyList<JournalEntrySummary>>> ListAsync(Guid userId, Guid companyId, JournalFilter filter, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        IReadOnlyList<Guid>? allowed = null, closure = null; // null = company-wide view permission
        if (!eff.Has(PermissionCatalog.Codes.JournalView, ScopeTarget.Company))
        {
            allowed = eff.Grants.Where(g => g.Scope == ScopeKind.Branch && g.ScopeId is not null && g.Permissions.Contains(PermissionCatalog.Codes.JournalView)).Select(g => g.ScopeId!.Value).Distinct().ToList();
            var roots = CostCenterRoots(eff, PermissionCatalog.Codes.JournalView);
            if (allowed.Count == 0 && roots.Count == 0) return Denied<IReadOnlyList<JournalEntrySummary>>();
            if (roots.Count > 0) closure = await journal.CostCenterClosureAsync(companyId, roots, ct);
        }
        // The restriction runs inside the query, before ORDER BY / LIMIT / OFFSET, so paging only ever walks entries the caller may see (same rule as opening an entry).
        return ConfigResult<IReadOnlyList<JournalEntrySummary>>.Ok(await journal.ListAsync(companyId, filter with { AllowedBranches = allowed, AllowedCostCenters = closure }, ct));
    }

    /// <summary>The cost centers the caller may allocate to (active leaves): all of them for company-wide or branch access, otherwise the granted subtrees. Needs a journal view/create/update permission.</summary>
    public async Task<ConfigResult<IReadOnlyList<CostCenterView>>> AllocatableCostCentersAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        string[] codes = [PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalUpdate, PermissionCatalog.Codes.JournalView];
        if (codes.Any(c => eff.Has(c, ScopeTarget.Company) || eff.Grants.Any(g => g.Scope == ScopeKind.Branch && g.Permissions.Contains(c))))
            return ConfigResult<IReadOnlyList<CostCenterView>>.Ok(await journal.AllocatableCostCentersAsync(companyId, null, ct));
        var roots = codes.SelectMany(c => CostCenterRoots(eff, c)).Distinct().ToList();
        if (roots.Count == 0) return Denied<IReadOnlyList<CostCenterView>>();
        return ConfigResult<IReadOnlyList<CostCenterView>>.Ok(await journal.AllocatableCostCentersAsync(companyId, await journal.CostCenterClosureAsync(companyId, roots, ct), ct));
    }

    public async Task<ConfigResult<JournalEntryView>> CreateAsync(Guid userId, Guid companyId, JournalInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<JournalEntryView>.Invalid([new("body", "required")]);
        if (!await CoversAsync(userId, companyId, PermissionCatalog.Codes.JournalCreate, ShapeOf(input), ct)) return Denied<JournalEntryView>();
        if (input.AdjustsId is { } adj)
        {
            var target = await journal.GetAsync(companyId, adj, ct);
            if (target is not null && !await CoversAsync(userId, companyId, PermissionCatalog.Codes.JournalView, ShapeOf(target), ct)) return Denied<JournalEntryView>();
        }
        return await journal.CreateDraftAsync(userId, companyId, input, await MayOverrideRate(userId, companyId, ct), ct);
    }

    /// <summary>An automatic entry from a business event, on behalf of the initiating user (who needs the create permission for the branches). Same draft, approval and posting rules as a manual entry.</summary>
    public async Task<ConfigResult<JournalEntryView>> CreateAutomaticAsync(Guid userId, Guid companyId, AutomaticJournalInput? input, CancellationToken ct = default)
    {
        if (input?.Entry is null) return ConfigResult<JournalEntryView>.Invalid([new("entry", "required")]);
        if (ShapeOf(input.Entry).Branches.Count == 0 || !await CoversAsync(userId, companyId, PermissionCatalog.Codes.JournalCreate, ShapeOf(input.Entry), ct)) return Denied<JournalEntryView>();
        return await journal.CreateAutomaticAsync(userId, companyId, input, await MayOverrideRate(userId, companyId, ct), ct);
    }

    public async Task<ConfigResult<JournalEntryView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? revision, JournalInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<JournalEntryView>.Invalid([new("body", "required")]);
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalUpdate, ct);
        if (!cur.IsOk) return cur;
        var shape = ShapeOf(input);
        if ((shape.Branches.Count > 0 || shape.LineCostCenters.Any(l => l.Count > 0)) && !await CoversAsync(userId, companyId, PermissionCatalog.Codes.JournalUpdate, shape, ct)) return Denied<JournalEntryView>();
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
        if (!cur.IsOk) return cur;
        var roles = await StageRolesAsync(await access.GetEffectivePermissionsAsync(userId, companyId, ct), companyId, ShapeOf(cur.Value!), ct);   // approve AND reject: the stage role must be held for what the entry touches
        return await journal.DecideAsync(userId, companyId, id, revision, input, ct, roles);
    }

    public async Task<ConfigResult<JournalEntryView>> PostAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalPost, ct);
        if (!cur.IsOk) return cur;
        // Year-end documents: every approval that counts must come from an INDEPENDENT approver who is STILL authorized right now (permission for everything the entry touches, and the stage role
        // where one is required). An approver whose access was revoked after approving no longer counts; the entry then has to be withdrawn and approved again. PostgreSQL separately refuses
        // decisions by the creator or submitter and an unsatisfied plan, so the two layers agree.
        if (cur.Value!.Header.Status == JournalStatus.Approved && await journal.IsYearEndAsync(companyId, id, ct))
        {
            var denial = await YearEndApprovalsStillValidAsync(companyId, cur.Value, ct);
            if (denial is not null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.Conflict, denial);
        }
        var soft = await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.JournalSoftClosedPost, ScopeTarget.Company, ct);
        return await journal.PostAsync(userId, companyId, id, revision, soft, ct);
    }

    private async Task<string?> YearEndApprovalsStillValidAsync(Guid companyId, JournalEntryView view, CancellationToken ct)
    {
        var h = view.Header;
        var shape = ShapeOf(view);
        var valid = new Dictionary<Guid, bool>();
        foreach (var plan in view.Plan ?? [])
        {
            var counted = new HashSet<Guid>();
            foreach (var d in view.Approvals.Where(d => d.Approved && d.DocumentRevision == h.Revision && d.Stage == plan.Stage))
            {
                if (d.Approver == h.CreatedBy || d.Approver == h.SubmittedBy) continue;
                var eff = await access.GetEffectivePermissionsAsync(d.Approver, companyId, ct);
                if (!valid.TryGetValue(d.Approver, out var ok))
                    valid[d.Approver] = ok = await CoversAsync(d.Approver, companyId, PermissionCatalog.Codes.JournalApprove, shape, ct);
                if (ok && (plan.RoleId is not { } role || (await StageRolesAsync(eff, companyId, shape, ct)).Contains(role))) counted.Add(d.Approver);
            }
            if (counted.Count < plan.Required) return "approver_no_longer_authorized";
        }
        return null;
    }

    public async Task<ConfigResult<JournalEntryView>> ReverseAsync(Guid userId, Guid companyId, Guid id, long? revision, ReversalInput? input, CancellationToken ct = default)
    {
        var cur = await Authorized(userId, companyId, id, PermissionCatalog.Codes.JournalReverse, ct);
        return cur.IsOk ? await journal.ReverseAsync(userId, companyId, id, revision, input, ct) : cur;
    }
}
