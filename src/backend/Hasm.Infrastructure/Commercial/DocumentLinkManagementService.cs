using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Commercial;

/// <summary>
/// Authorized entry point for document links (the only thing the API calls; D-074). A link is created with <c>company.commercialdocs.update</c> on the TARGET document's branch (no new permission code); both documents must
/// be of that branch (the service refuses another branch as "document not found", so nothing of another branch is revealed). Reading links and derived quantities needs <c>company.commercialdocs.view</c> on the document's
/// branch. Permissions are re-read from Hasm_System on EVERY call; a caller who cannot see a document gets the same refusal whether or not it exists (only a company-wide holder is told NotFound).
/// </summary>
public sealed class DocumentLinkManagementService(AccessService access, DocumentLinkService links, CommercialDocumentService documents)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private async Task<bool> CoversAsync(Guid user, Guid company, string code, Guid branchId, CancellationToken ct)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        return eff.Has(code, ScopeTarget.Company) || eff.Has(code, ScopeTarget.Branch(branchId));
    }

    private static bool HoldsAnywhere(EffectivePermissions eff, string code) =>
        eff.Has(code, ScopeTarget.Company) || eff.Grants.Any(g => g.Scope == ScopeKind.Branch && g.Permissions.Contains(code));

    /// <summary>Authorizes one permission on the branch of the document; a missing or invisible document is the same refusal for a branch-scoped caller.</summary>
    private async Task<ConfigResult<Guid>> Authorized(Guid user, Guid company, Guid documentId, string code, CancellationToken ct)
    {
        if (!HoldsAnywhere(await access.GetEffectivePermissionsAsync(user, company, ct), code)) return Denied<Guid>();
        var view = await documents.GetAsync(company, documentId, ct);
        if (view is null) return await access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct) ? ConfigResult<Guid>.Fail(ConfigStatus.NotFound, "commercial_document_not_found") : Denied<Guid>();
        return await CoversAsync(user, company, code, view.BranchId, ct) ? ConfigResult<Guid>.Ok(view.BranchId) : Denied<Guid>();
    }

    public async Task<ConfigResult<DocumentLinkView>> CreateAsync(Guid user, Guid company, DocumentLinkInput? input, CancellationToken ct = default)
    {
        if (input?.ToDocumentId is not { } target || target == Guid.Empty)
        {
            // nothing to authorize against: a caller who holds the permission nowhere is refused, anyone else gets the validation error
            if (!HoldsAnywhere(await access.GetEffectivePermissionsAsync(user, company, ct), PermissionCatalog.Codes.CommercialUpdate)) return Denied<DocumentLinkView>();
            return await links.CreateAsync(user, company, input, ct);
        }
        var a = await Authorized(user, company, target, PermissionCatalog.Codes.CommercialUpdate, ct);
        if (!a.IsOk) return ConfigResult<DocumentLinkView>.Fail(a.Status, a.Code!);
        var created = await links.CreateAsync(user, company, input, ct);
        // a REPLAY reveals only what the caller may already see: the caller needs the view permission on that branch to receive the link back
        if (created.IsOk && created.Value!.Replayed && !await CoversAsync(user, company, PermissionCatalog.Codes.CommercialView, a.Value, ct))
            return ConfigResult<DocumentLinkView>.Ok(new DocumentLinkView(created.Value.Id, Guid.Empty, 0, Guid.Empty, 0, 0, 0, Guid.Empty, default, true));
        return created;
    }

    /// <summary>Every link that touches the document, as source or as target (needs the view permission on the document's branch).</summary>
    public async Task<ConfigResult<IReadOnlyList<DocumentLinkView>>> ListAsync(Guid user, Guid company, Guid documentId, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, documentId, PermissionCatalog.Codes.CommercialView, ct);
        return a.IsOk ? ConfigResult<IReadOnlyList<DocumentLinkView>>.Ok(await links.ListAsync(company, documentId, ct)) : ConfigResult<IReadOnlyList<DocumentLinkView>>.Fail(a.Status, a.Code!);
    }

    /// <summary>The derived ordered, fulfilled, invoiced and returned quantities of every line (needs the view permission on the document's branch).</summary>
    public async Task<ConfigResult<IReadOnlyList<LineQuantityView>>> QuantitiesAsync(Guid user, Guid company, Guid documentId, CancellationToken ct = default)
    {
        var a = await Authorized(user, company, documentId, PermissionCatalog.Codes.CommercialView, ct);
        return a.IsOk ? ConfigResult<IReadOnlyList<LineQuantityView>>.Ok(await links.QuantitiesAsync(company, documentId, ct)) : ConfigResult<IReadOnlyList<LineQuantityView>>.Fail(a.Status, a.Code!);
    }
}
