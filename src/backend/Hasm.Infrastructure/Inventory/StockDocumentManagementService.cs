using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Inventory;

/// <summary>What a caller may do with stock documents: per action, the warehouses on which the permission is held (a company-wide grant covers every warehouse of the company).</summary>
public sealed record StockDocumentCapabilities(IReadOnlyList<Guid> View, IReadOnlyList<Guid> Create, IReadOnlyList<Guid> Update, IReadOnlyList<Guid> Submit, IReadOnlyList<Guid> Approve, IReadOnlyList<Guid> Cancel, IReadOnlyList<Guid> Post, IReadOnlyList<Guid> Reverse);

/// <summary>
/// Authorized entry point for stock documents (the only thing the API calls). Permissions are re-read on every call (no cache) and are WAREHOUSE-scoped: a document needs the permission on EVERY warehouse it involves
/// (a transfer on both), except viewing, which needs it on at least one. There is no inheritance from branches. A document the caller may not view is reported as not found-equivalent (forbidden), never disclosed.
/// Separation of duties (the approver is not the submitter) is enforced by <see cref="StockDocumentService"/> and by PostgreSQL.
/// </summary>
public sealed class StockDocumentManagementService(AccessService access, StockDocumentService documents)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private static IEnumerable<Guid> Involved(StockDocumentView d) => new[] { d.SourceWarehouseId, d.TargetWarehouseId }.Where(x => x is not null).Select(x => x!.Value);

    private async Task<bool> AllAsync(Guid user, Guid company, string code, IEnumerable<Guid> warehouses, CancellationToken ct)
    {
        var any = false;
        foreach (var w in warehouses.Distinct())
        {
            any = true;
            if (!await access.IsAllowedAsync(user, company, code, ScopeTarget.Warehouse(w), ct)) return false;
        }
        return any;
    }

    private async Task<bool> AnyAsync(Guid user, Guid company, string code, IEnumerable<Guid> warehouses, CancellationToken ct)
    {
        foreach (var w in warehouses.Distinct()) if (await access.IsAllowedAsync(user, company, code, ScopeTarget.Warehouse(w), ct)) return true;
        return false;
    }

    public async Task<StockDocumentCapabilities> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        var all = (await documents.ListWarehousesAsync(company, ct)).Select(w => w.Id).ToList();
        IReadOnlyList<Guid> Of(string code) => [.. all.Where(w => eff.Has(code, ScopeTarget.Warehouse(w)))];
        return new StockDocumentCapabilities(Of(PermissionCatalog.Codes.StockDocsView), Of(PermissionCatalog.Codes.StockDocsCreate), Of(PermissionCatalog.Codes.StockDocsUpdate),
            Of(PermissionCatalog.Codes.StockDocsSubmit), Of(PermissionCatalog.Codes.StockDocsApprove), Of(PermissionCatalog.Codes.StockDocsCancel), Of(PermissionCatalog.Codes.StockDocsPost), Of(PermissionCatalog.Codes.StockDocsReverse));
    }

    /// <summary>The warehouses the caller may see or use in documents (view, create or update on that warehouse).</summary>
    public async Task<ConfigResult<IReadOnlyList<StockWarehouseRef>>> WarehousesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var caps = await CapabilitiesAsync(user, company, ct);
        var allowed = caps.View.Concat(caps.Create).Concat(caps.Update).ToHashSet();
        if (allowed.Count == 0) return Denied<IReadOnlyList<StockWarehouseRef>>();
        return ConfigResult<IReadOnlyList<StockWarehouseRef>>.Ok([.. (await documents.ListWarehousesAsync(company, ct)).Where(w => allowed.Contains(w.Id))]);
    }

    /// <summary>Items a line may use; only for callers who can create or edit documents somewhere.</summary>
    public async Task<ConfigResult<IReadOnlyList<StockItemRef>>> SearchItemsAsync(Guid user, Guid company, string? search, CancellationToken ct = default)
    {
        var caps = await CapabilitiesAsync(user, company, ct);
        if (caps.Create.Count == 0 && caps.Update.Count == 0) return Denied<IReadOnlyList<StockItemRef>>();
        return ConfigResult<IReadOnlyList<StockItemRef>>.Ok(await documents.SearchItemsAsync(company, search, ct));
    }

    public async Task<ConfigResult<StockDocumentPage>> ListAsync(Guid user, Guid company, StockDocumentFilter filter, CancellationToken ct = default)
    {
        var caps = await CapabilitiesAsync(user, company, ct);
        if (caps.View.Count == 0) return Denied<StockDocumentPage>();
        // a requested warehouse the caller cannot view is not disclosed: the list is simply restricted to what they may see
        return ConfigResult<StockDocumentPage>.Ok(await documents.ListAsync(company, filter with { VisibleWarehouses = caps.View }, ct));
    }

    public async Task<ConfigResult<StockDocumentView>> GetAsync(Guid user, Guid company, Guid id, CancellationToken ct = default)
    {
        var doc = await documents.GetAsync(company, id, ct);
        if (doc is null) return await AnyViewAsync(user, company, ct) ? ConfigResult<StockDocumentView>.Fail(ConfigStatus.NotFound, "document_not_found") : Denied<StockDocumentView>();
        return await AnyAsync(user, company, PermissionCatalog.Codes.StockDocsView, Involved(doc), ct) ? ConfigResult<StockDocumentView>.Ok(doc) : Denied<StockDocumentView>();
    }

    private async Task<bool> AnyViewAsync(Guid user, Guid company, CancellationToken ct) => (await CapabilitiesAsync(user, company, ct)).View.Count > 0;

    public async Task<ConfigResult<StockDocumentView>> CreateAsync(Guid user, Guid company, StockDocumentInput? input, CancellationToken ct = default)
    {
        if (input is null) return await documents.CreateAsync(user, company, null, ct);   // validation error, nothing to authorize against
        var ws = new[] { input.SourceWarehouseId, input.TargetWarehouseId }.Where(x => x is not null && x != Guid.Empty).Select(x => x!.Value).ToList();
        if (ws.Count == 0) return Denied<StockDocumentView>() is var d && await AnyCreateAsync(user, company, ct) ? await documents.CreateAsync(user, company, input, ct) : d;
        return await AllAsync(user, company, PermissionCatalog.Codes.StockDocsCreate, ws, ct) ? await documents.CreateAsync(user, company, input, ct) : Denied<StockDocumentView>();
    }

    private async Task<bool> AnyCreateAsync(Guid user, Guid company, CancellationToken ct) => (await CapabilitiesAsync(user, company, ct)).Create.Count > 0;

    private async Task<ConfigResult<StockDocumentView>> OnAsync(Guid user, Guid company, Guid id, string code, Func<Task<ConfigResult<StockDocumentView>>> run, CancellationToken ct)
    {
        var doc = await documents.GetAsync(company, id, ct);
        if (doc is null) return await AnyViewAsync(user, company, ct) ? ConfigResult<StockDocumentView>.Fail(ConfigStatus.NotFound, "document_not_found") : Denied<StockDocumentView>();
        // the caller must be able to see the document at all (no existence probing), then hold the specific permission on every involved warehouse
        if (!await AnyAsync(user, company, PermissionCatalog.Codes.StockDocsView, Involved(doc), ct)) return Denied<StockDocumentView>();
        return await AllAsync(user, company, code, Involved(doc), ct) ? await run() : Denied<StockDocumentView>();
    }

    public Task<ConfigResult<StockDocumentView>> UpdateAsync(Guid user, Guid company, Guid id, long? rev, StockDocumentInput? input, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StockDocsUpdate, async () =>
        {
            // moving a draft to other warehouses needs the permission on those too
            var ws = new[] { input?.SourceWarehouseId, input?.TargetWarehouseId }.Where(x => x is not null && x != Guid.Empty).Select(x => x!.Value).ToList();
            return ws.Count == 0 || await AllAsync(user, company, PermissionCatalog.Codes.StockDocsUpdate, ws, ct) ? await documents.UpdateAsync(user, company, id, rev, input, ct) : Denied<StockDocumentView>();
        }, ct);

    public Task<ConfigResult<StockDocumentView>> SubmitAsync(Guid user, Guid company, Guid id, long? rev, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StockDocsSubmit, () => documents.SubmitAsync(user, company, id, rev, ct), ct);

    public Task<ConfigResult<StockDocumentView>> WithdrawAsync(Guid user, Guid company, Guid id, long? rev, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StockDocsUpdate, () => documents.WithdrawAsync(user, company, id, rev, ct), ct);

    public Task<ConfigResult<StockDocumentView>> ApproveAsync(Guid user, Guid company, Guid id, long? rev, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StockDocsApprove, () => documents.ApproveAsync(user, company, id, rev, ct), ct);

    public Task<ConfigResult<StockDocumentView>> RejectAsync(Guid user, Guid company, Guid id, long? rev, string? note, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StockDocsApprove, () => documents.RejectAsync(user, company, id, rev, note, ct), ct);

    public Task<ConfigResult<StockDocumentView>> CancelAsync(Guid user, Guid company, Guid id, long? rev, string? reason, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StockDocsCancel, () => documents.CancelAsync(user, company, id, rev, reason, ct), ct);
}
