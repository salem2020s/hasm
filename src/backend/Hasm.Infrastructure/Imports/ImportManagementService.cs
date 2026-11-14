using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Imports;

/// <summary>
/// Authorized entry point of the import framework (the only thing the API calls). Permissions are re-read from the system database on EVERY call. An operation needs the generic permission
/// (<c>company.imports.*</c>) AND the adapter's own business permission, so the framework can never be used to reach data the caller could not reach directly. A caller without access to a batch
/// gets "forbidden", never "not found" (no existence oracle), and a listing is restricted inside the query to the adapters the caller may see.
/// </summary>
public sealed class ImportManagementService(AccessService access, ImportService imports, ImportAdapterRegistry adapters)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    private async Task<bool> MayAsync(Guid userId, Guid companyId, string generic, string adapterPermission, CancellationToken ct) =>
        await Allowed(userId, companyId, generic, ct) && await Allowed(userId, companyId, adapterPermission, ct);

    public ImportAdapterRegistry Adapters => adapters;

    /// <summary>The adapters the caller may use, with what they may do (drives the screen; every call is authorized again).</summary>
    public async Task<ConfigResult<IReadOnlyList<(IImportAdapter Adapter, bool CanUpload, bool CanView)>>> AdaptersAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsView, ct)) return Denied<IReadOnlyList<(IImportAdapter, bool, bool)>>();
        var upload = await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsUpload, ct);
        var list = new List<(IImportAdapter, bool, bool)>();
        foreach (var a in adapters.All.OrderBy(a => a.Code, StringComparer.Ordinal))
        {
            var view = await Allowed(userId, companyId, a.ViewPermission, ct);
            if (!view) continue;
            list.Add((a, upload && await Allowed(userId, companyId, a.UploadPermission, ct), true));
        }
        return ConfigResult<IReadOnlyList<(IImportAdapter, bool, bool)>>.Ok(list);
    }

    public async Task<ConfigResult<ImportBatchView>> UploadAsync(Guid userId, Guid companyId, string? adapterCode, string? fileName, byte[] data, string? idempotencyKey, CancellationToken ct = default)
    {
        var adapter = adapters.Find(adapterCode);
        if (adapter is null) return await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsUpload, ct) ? ConfigResult<ImportBatchView>.Invalid([new("adapter", "unknown_adapter")]) : Denied<ImportBatchView>();
        return await MayAsync(userId, companyId, PermissionCatalog.Codes.ImportsUpload, adapter.UploadPermission, ct) ? await imports.UploadAsync(userId, companyId, adapter.Code, fileName, data, idempotencyKey, ct) : Denied<ImportBatchView>();
    }

    private async Task<ConfigResult<ImportBatchView>> Authorized(Guid userId, Guid companyId, Guid id, string generic, bool adapterUpload, CancellationToken ct)
    {
        var batch = await imports.GetAsync(companyId, id, ct);
        if (batch is null) return await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsView, ct) ? ConfigResult<ImportBatchView>.Fail(ConfigStatus.NotFound, "import_not_found") : Denied<ImportBatchView>();
        var adapter = adapters.Find(batch.AdapterCode);
        if (adapter is null) return Denied<ImportBatchView>();
        return await MayAsync(userId, companyId, generic, adapterUpload ? adapter.UploadPermission : adapter.ViewPermission, ct) ? ConfigResult<ImportBatchView>.Ok(batch) : Denied<ImportBatchView>();
    }

    public async Task<ConfigResult<ImportBatchView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default) => await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsView, false, ct);

    public async Task<ConfigResult<IReadOnlyList<ImportBatchView>>> ListAsync(Guid userId, Guid companyId, ImportListFilter filter, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsView, ct)) return Denied<IReadOnlyList<ImportBatchView>>();
        var visible = new List<string>();
        foreach (var a in adapters.All) if (await Allowed(userId, companyId, a.ViewPermission, ct)) visible.Add(a.Code);
        return ConfigResult<IReadOnlyList<ImportBatchView>>.Ok(await imports.ListAsync(companyId, filter, visible, ct));
    }

    public async Task<ConfigResult<ImportRowsPage>> RowsAsync(Guid userId, Guid companyId, Guid id, int offset, int limit, short? rowStatus, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsView, false, ct);
        if (!a.IsOk) return a.Status == ConfigStatus.Ok ? Denied<ImportRowsPage>() : ConfigResult<ImportRowsPage>.Fail(a.Status, a.Code!);
        var page = await imports.RowsAsync(companyId, id, offset, limit, rowStatus, ct);
        return page is null ? ConfigResult<ImportRowsPage>.Fail(ConfigStatus.NotFound, "import_not_found") : ConfigResult<ImportRowsPage>.Ok(page);
    }

    public async Task<ConfigResult<IReadOnlyList<ImportEventView>>> EventsAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsView, false, ct);
        if (!a.IsOk) return ConfigResult<IReadOnlyList<ImportEventView>>.Fail(a.Status, a.Code!);
        return ConfigResult<IReadOnlyList<ImportEventView>>.Ok(await imports.EventsAsync(companyId, id, ct));
    }

    public async Task<ConfigResult<ImportBatchView>> StageAsync(Guid userId, Guid companyId, Guid id, long? revision, StageInput? input, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsUpload, true, ct);
        return a.IsOk ? await imports.StageAsync(userId, companyId, id, revision, input, ct) : a;
    }

    /// <summary>The adapter's target fields and supported resolutions for this batch (what the mapping screen shows).</summary>
    public async Task<ConfigResult<(IReadOnlyList<ImportField> Fields, IReadOnlyList<string> Resolutions)>> FieldsAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsView, false, ct);
        if (!a.IsOk) return ConfigResult<(IReadOnlyList<ImportField>, IReadOnlyList<string>)>.Fail(a.Status, a.Code!);
        var adapter = adapters.Find(a.Value!.AdapterCode)!;
        return ConfigResult<(IReadOnlyList<ImportField>, IReadOnlyList<string>)>.Ok((adapter.Fields, adapter.Resolutions));
    }

    public async Task<ConfigResult<System.Text.Json.Nodes.JsonObject>> SuggestMappingAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsView, false, ct);
        return a.IsOk ? await imports.SuggestMappingAsync(companyId, id, ct) : ConfigResult<System.Text.Json.Nodes.JsonObject>.Fail(a.Status, a.Code!);
    }

    public async Task<ConfigResult<ImportBatchView>> SetMappingAsync(Guid userId, Guid companyId, Guid id, long? revision, System.Text.Json.Nodes.JsonNode? mapping, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsUpload, true, ct);
        return a.IsOk ? await imports.SetMappingAsync(userId, companyId, id, revision, mapping, ct) : a;
    }

    public async Task<ConfigResult<ImportBatchView>> DryRunAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsUpload, true, ct);
        return a.IsOk ? await imports.DryRunAsync(userId, companyId, id, revision, ct) : a;
    }

    public async Task<ConfigResult<ImportBatchView>> SetResolutionsAsync(Guid userId, Guid companyId, Guid id, long? revision, IReadOnlyList<ResolutionInput>? resolutions, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsUpload, true, ct);
        return a.IsOk ? await imports.SetResolutionsAsync(userId, companyId, id, revision, resolutions, ct) : a;
    }

    public async Task<ConfigResult<PlanCheck>> CheckPlanAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsView, false, ct);
        return a.IsOk ? await imports.CheckPlanAsync(companyId, id, ct) : ConfigResult<PlanCheck>.Fail(a.Status, a.Code!);
    }

    public async Task<ConfigResult<ImportBatchView>> SubmitAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsSubmit, true, ct);
        return a.IsOk ? await imports.SubmitAsync(userId, companyId, id, revision, ct) : a;
    }

    public async Task<ConfigResult<ImportBatchView>> WithdrawAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsSubmit, true, ct);
        return a.IsOk ? await imports.WithdrawAsync(userId, companyId, id, revision, ct) : a;
    }

    /// <summary>
    /// Approve or reject. The approver needs the import approval permission AND the right to see the data being approved (the adapter's view permission), re-read on this very call; the creator and the
    /// submitter of the batch are refused by the service and by PostgreSQL. Administrator rights give no bypass (the administrator preset does not even hold the approval permission).
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> DecideAsync(Guid userId, Guid companyId, Guid id, long? revision, bool? approve, string? comment, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsApprove, false, ct);
        return a.IsOk ? await imports.DecideAsync(userId, companyId, id, revision, approve, comment, ct) : a;
    }

    /// <summary>
    /// Applies an approved batch. Besides the application permissions, the approval it rests on must still be valid: at least one approver whose approving decision it rests on must STILL hold the import
    /// approval permission and the right to see the data. If every such approver lost that right the application is refused and the batch must be withdrawn, submitted and approved again.
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> ApplyAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsApply, true, ct);
        if (!a.IsOk) return a;
        if (a.Value!.Status is ImportStatus.Approved or ImportStatus.Failed)
        {
            var adapter = adapters.Find(a.Value.AdapterCode)!;
            var stillValid = false;
            foreach (var approver in await imports.ApprovingApproversAsync(companyId, id, ct))
                if (await MayAsync(approver, companyId, PermissionCatalog.Codes.ImportsApprove, adapter.ViewPermission, ct)) { stillValid = true; break; }
            if (!stillValid) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "approver_no_longer_authorized");
        }
        return await imports.ApplyAsync(userId, companyId, id, revision, ct);
    }

    public async Task<ConfigResult<IReadOnlyList<Hasm.Infrastructure.Ledger.ApprovalDecisionView>>> DecisionsAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsView, false, ct);
        return a.IsOk ? ConfigResult<IReadOnlyList<Hasm.Infrastructure.Ledger.ApprovalDecisionView>>.Ok(await imports.DecisionsAsync(companyId, id, ct)) : ConfigResult<IReadOnlyList<Hasm.Infrastructure.Ledger.ApprovalDecisionView>>.Fail(a.Status, a.Code!);
    }

    public async Task<ConfigResult<ImportBatchView>> CancelAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsCancel, true, ct);
        return a.IsOk ? await imports.CancelAsync(userId, companyId, id, revision, ct) : a;
    }

    public async Task<ConfigResult<ImportBatchView>> RenewAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default)
    {
        var a = await Authorized(userId, companyId, id, PermissionCatalog.Codes.ImportsUpload, true, ct);
        return a.IsOk ? await imports.RenewAsync(userId, companyId, id, revision, ct) : a;
    }

    public async Task<ConfigResult<ImportSettingsView>> GetSettingsAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsView, ct) ? ConfigResult<ImportSettingsView>.Ok(await imports.GetSettingsAsync(companyId, ct)) : Denied<ImportSettingsView>();

    public async Task<ConfigResult<ImportSettingsView>> PutSettingsAsync(Guid userId, Guid companyId, long? revision, ImportSettingsInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsConfigure, ct) ? await imports.PutSettingsAsync(userId, companyId, revision, input, ct) : Denied<ImportSettingsView>();

    /// <summary>Runs the retention pass now (it also runs on a schedule).</summary>
    public async Task<ConfigResult<ImportMaintenanceResult>> MaintainAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsConfigure, ct) ? ConfigResult<ImportMaintenanceResult>.Ok(await imports.MaintainAsync(userId, companyId, ct)) : Denied<ImportMaintenanceResult>();

    /// <summary>Key rotation: re-wraps the data keys of stored originals under the active master key (audited).</summary>
    public async Task<ConfigResult<ImportMaintenanceResult>> RewrapAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ImportsConfigure, ct) ? ConfigResult<ImportMaintenanceResult>.Ok(await imports.RewrapAsync(userId, companyId, ct)) : Denied<ImportMaintenanceResult>();
}
