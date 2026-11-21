using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Commercial;

/// <summary>
/// Authorized entry point for commercial documents (the only thing the API calls). Permissions are BRANCH-scoped like the journal (SRS 3.5, 4.2): an operation needs the permission company-wide or for the
/// document's branch, re-read from Hasm_System on EVERY call. A caller who cannot see a document gets the same refusal whether or not it exists (nothing is revealed); only a company-wide viewer is told NotFound.
/// Business rules (state machine, revision fences, idempotency, immutability) live in <see cref="CommercialDocumentService"/> and PostgreSQL.
/// </summary>
public sealed class CommercialDocumentManagementService(AccessService access, CommercialDocumentService documents)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private async Task<bool> CoversAsync(Guid user, Guid company, string code, Guid branchId, CancellationToken ct)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        return eff.Has(code, ScopeTarget.Company) || eff.Has(code, ScopeTarget.Branch(branchId));
    }

    private static bool HoldsAnywhere(EffectivePermissions eff, string code) =>
        eff.Has(code, ScopeTarget.Company) || eff.Grants.Any(g => g.Scope == ScopeKind.Branch && g.Permissions.Contains(code));

    /// <summary>Loads the document and authorizes one permission on its branch. A missing or invisible document is the same refusal for a branch-scoped caller.</summary>
    private async Task<ConfigResult<CommercialDocumentView>> Authorized(Guid user, Guid company, Guid id, string code, CancellationToken ct)
    {
        if (!HoldsAnywhere(await access.GetEffectivePermissionsAsync(user, company, ct), code)) return Denied<CommercialDocumentView>();   // a caller with no grant for this permission never makes the company database answer
        var view = await documents.GetAsync(company, id, ct);
        if (view is null) return await access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct) ? ConfigResult<CommercialDocumentView>.Fail(ConfigStatus.NotFound, "commercial_document_not_found") : Denied<CommercialDocumentView>();
        return await CoversAsync(user, company, code, view.BranchId, ct) ? ConfigResult<CommercialDocumentView>.Ok(view) : Denied<CommercialDocumentView>();
    }

    /// <summary>The approval plan of the document's current revision and the decisions cast on it (view permission on the document's branch).</summary>
    public async Task<ConfigResult<CommercialApprovalProgress>> ApprovalProgressAsync(Guid user, Guid company, Guid id, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, id, PermissionCatalog.Codes.CommercialView, ct);
        if (!a.IsOk) return ConfigResult<CommercialApprovalProgress>.Fail(a.Status, a.Code!);
        var progress = await documents.ApprovalProgressAsync(company, id, ct);
        return progress is null ? ConfigResult<CommercialApprovalProgress>.Fail(ConfigStatus.NotFound, "commercial_document_not_found") : ConfigResult<CommercialApprovalProgress>.Ok(progress);
    }

    /// <summary>The registered document types (names only). Allowed when the caller holds <c>view</c> or <c>create</c> on any scope, otherwise the same refusal as every other call.</summary>
    public async Task<ConfigResult<IReadOnlyList<string>>> TypesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        return HoldsAnywhere(eff, PermissionCatalog.Codes.CommercialView) || HoldsAnywhere(eff, PermissionCatalog.Codes.CommercialCreate)
            ? ConfigResult<IReadOnlyList<string>>.Ok(documents.RegisteredTypes) : Denied<IReadOnlyList<string>>();
    }

    public async Task<ConfigResult<CommercialDocumentView>> GetAsync(Guid user, Guid company, Guid id, CancellationToken ct = default) =>
        await Authorized(user, company, id, PermissionCatalog.Codes.CommercialView, ct);

    /// <summary>Lists the documents of the branches the caller may view (the restriction runs inside the query, before paging).</summary>
    public async Task<ConfigResult<IReadOnlyList<CommercialDocumentView>>> ListAsync(Guid user, Guid company, CommercialDocumentFilter filter, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        IReadOnlyList<Guid>? allowed = null;   // null = company-wide view
        if (!eff.Has(PermissionCatalog.Codes.CommercialView, ScopeTarget.Company))
        {
            allowed = [.. eff.Grants.Where(g => g.Scope == ScopeKind.Branch && g.ScopeId is not null && g.Permissions.Contains(PermissionCatalog.Codes.CommercialView)).Select(g => g.ScopeId!.Value).Distinct()];
            if (allowed.Count == 0) return Denied<IReadOnlyList<CommercialDocumentView>>();
        }
        return ConfigResult<IReadOnlyList<CommercialDocumentView>>.Ok(await documents.ListAsync(company, filter with { AllowedBranches = allowed }, ct));
    }

    public async Task<ConfigResult<CommercialDocumentView>> CreateAsync(Guid user, Guid company, CommercialDocumentInput? input, CancellationToken ct = default)
    {
        if (input?.BranchId is { } branch)
        {
            if (!await CoversAsync(user, company, PermissionCatalog.Codes.CommercialCreate, branch, ct)) return Denied<CommercialDocumentView>();
        }
        else if (!HoldsAnywhere(await access.GetEffectivePermissionsAsync(user, company, ct), PermissionCatalog.Codes.CommercialCreate)) return Denied<CommercialDocumentView>();
        var created = await documents.CreateAsync(user, company, input, ct);   // a missing branch is then a validation error for a caller who may create somewhere
        // a REPLAY returns the current state of the document: a caller who may create but not view it receives ONLY the document id and the replayed flag (decided in S1b; nothing else of the document is disclosed)
        if (created.IsOk && created.Value!.Replayed && !await CoversAsync(user, company, PermissionCatalog.Codes.CommercialView, created.Value.BranchId, ct))
            return ConfigResult<CommercialDocumentView>.Ok(Redacted(created.Value));
        return created;
    }

    public async Task<ConfigResult<CommercialDocumentView>> UpdateAsync(Guid user, Guid company, Guid id, long? revision, CommercialDocumentInput? input, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, id, PermissionCatalog.Codes.CommercialUpdate, ct);
        return a.IsOk ? await documents.UpdateAsync(user, company, id, revision, input, ct) : a;
    }

    public async Task<ConfigResult<CommercialDocumentView>> SubmitAsync(Guid user, Guid company, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, id, PermissionCatalog.Codes.CommercialSubmit, ct);
        return a.IsOk ? await documents.SubmitAsync(user, company, id, revision, ct) : a;
    }

    /// <summary>Withdrawal needs the submit permission (the reverse of submitting).</summary>
    public async Task<ConfigResult<CommercialDocumentView>> WithdrawAsync(Guid user, Guid company, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, id, PermissionCatalog.Codes.CommercialSubmit, ct);
        return a.IsOk ? await documents.WithdrawAsync(user, company, id, revision, ct) : a;
    }

    /// <summary>The only thing a creator without the view permission learns from a replay: the id and the replayed flag.</summary>
    private static CommercialDocumentView Redacted(CommercialDocumentView v) =>
        new(v.Id, "", Guid.Empty, Guid.Empty, "", default, 0, 0, null, null, 0, Guid.Empty, default, default, [], [], true);

    /// <summary>Approval (<c>company.commercialdocs.approve</c> on the document's branch) is separate from every other permission and is not in the Company Administrator preset; the service additionally requires the approver to be independent.</summary>
    public async Task<ConfigResult<CommercialDocumentView>> ApproveAsync(Guid user, Guid company, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, id, PermissionCatalog.Codes.CommercialApprove, ct);
        if (!a.IsOk) return a;
        // a policy stage bound to a role takes decisions only from holders of that role: the roles are read from the authorization store on this very call (S5a)
        var roles = (await access.GetEffectivePermissionsAsync(user, company, ct)).Grants.Select(g => g.RoleId).ToHashSet();
        return await documents.ApproveAsync(user, company, id, revision, ct, roles);
    }

    public async Task<ConfigResult<CommercialDocumentView>> RejectAsync(Guid user, Guid company, Guid id, long? revision, string? reason, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, id, PermissionCatalog.Codes.CommercialApprove, ct);
        return a.IsOk ? await documents.RejectAsync(user, company, id, revision, reason, ct) : a;
    }

    public async Task<ConfigResult<CommercialDocumentView>> CancelAsync(Guid user, Guid company, Guid id, long? revision, string? reason, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, id, PermissionCatalog.Codes.CommercialCancel, ct);
        return a.IsOk ? await documents.CancelAsync(user, company, id, revision, reason, ct) : a;
    }
}
