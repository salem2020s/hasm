using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Inventory;

/// <summary>
/// Authorized entry point for inventory master data (the only thing the API calls). Permissions are re-read from the system database on every call (no cache) and checked
/// company-wide for the selected company (item data is not owned by a branch or warehouse). The business rules live in <see cref="CompanyInventoryService"/> and PostgreSQL;
/// the client is never trusted for any of them.
/// </summary>
public sealed class InventoryManagementService(AccessService access, CompanyInventoryService inventory)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Can(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);

    /// <summary>What the caller may do in inventory master data (for showing or hiding controls; every action is authorized again on the server).</summary>
    public async Task<IReadOnlyDictionary<string, bool>> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        bool H(string code) => eff.Has(code, ScopeTarget.Company);
        return new Dictionary<string, bool>
        {
            ["unitsView"] = H(PermissionCatalog.Codes.UnitsView), ["unitsCreate"] = H(PermissionCatalog.Codes.UnitsCreate), ["unitsUpdate"] = H(PermissionCatalog.Codes.UnitsUpdate), ["unitsActivate"] = H(PermissionCatalog.Codes.UnitsActivate),
            ["groupsView"] = H(PermissionCatalog.Codes.ItemGroupsView), ["groupsCreate"] = H(PermissionCatalog.Codes.ItemGroupsCreate), ["groupsUpdate"] = H(PermissionCatalog.Codes.ItemGroupsUpdate), ["groupsActivate"] = H(PermissionCatalog.Codes.ItemGroupsActivate),
            ["itemsView"] = H(PermissionCatalog.Codes.ItemsView), ["itemsCreate"] = H(PermissionCatalog.Codes.ItemsCreate), ["itemsUpdate"] = H(PermissionCatalog.Codes.ItemsUpdate), ["itemsActivate"] = H(PermissionCatalog.Codes.ItemsActivate),
            ["barcodesCreate"] = H(PermissionCatalog.Codes.BarcodesCreate), ["barcodesActivate"] = H(PermissionCatalog.Codes.BarcodesActivate),
            ["settingsView"] = H(PermissionCatalog.Codes.InventorySettingsView), ["settingsConfigure"] = H(PermissionCatalog.Codes.InventorySettingsConfigure),
        };
    }

    // ---- settings
    public async Task<ConfigResult<InventorySettingsView>> GetSettingsAsync(Guid user, Guid company, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.InventorySettingsView, ct) ? ConfigResult<InventorySettingsView>.Ok(await inventory.GetSettingsAsync(company, ct)) : Denied<InventorySettingsView>();

    public async Task<ConfigResult<InventorySettingsView>> SaveSettingsAsync(Guid user, Guid company, long? expectedRevision, InventorySettingsInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.InventorySettingsConfigure, ct) ? await inventory.SaveSettingsAsync(user, company, expectedRevision, input, ct) : Denied<InventorySettingsView>();

    // ---- units
    public async Task<ConfigResult<IReadOnlyList<UnitView>>> ListUnitsAsync(Guid user, Guid company, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.UnitsView, ct) || await Can(user, company, PermissionCatalog.Codes.ItemsView, ct)   // the item editor needs the unit list
            ? ConfigResult<IReadOnlyList<UnitView>>.Ok(await inventory.ListUnitsAsync(company, ct)) : Denied<IReadOnlyList<UnitView>>();

    /// <summary>The same permission and company scoping as <see cref="ListUnitsAsync"/>; only an optional search and paging are added.</summary>
    public async Task<ConfigResult<UnitPage>> SearchUnitsAsync(Guid user, Guid company, string? search, int? offset, int? limit, IReadOnlyCollection<Guid>? ids = null, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.UnitsView, ct) || await Can(user, company, PermissionCatalog.Codes.ItemsView, ct)
            ? ConfigResult<UnitPage>.Ok(await inventory.SearchUnitsAsync(company, search, offset, limit, ids, ct)) : Denied<UnitPage>();

    public async Task<ConfigResult<UnitView>> CreateUnitAsync(Guid user, Guid company, UnitInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.UnitsCreate, ct) ? await inventory.CreateUnitAsync(user, company, input, ct) : Denied<UnitView>();

    public async Task<ConfigResult<UnitView>> UpdateUnitAsync(Guid user, Guid company, Guid id, long? rev, UnitInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.UnitsUpdate, ct) ? await inventory.UpdateUnitAsync(user, company, id, rev, input, ct) : Denied<UnitView>();

    public async Task<ConfigResult<UnitView>> SetUnitActiveAsync(Guid user, Guid company, Guid id, long? rev, bool active, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.UnitsActivate, ct) ? await inventory.SetUnitActiveAsync(user, company, id, rev, active, ct) : Denied<UnitView>();

    // ---- item groups
    public async Task<ConfigResult<IReadOnlyList<ItemGroupView>>> ListGroupsAsync(Guid user, Guid company, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemGroupsView, ct) || await Can(user, company, PermissionCatalog.Codes.ItemsView, ct)
            ? ConfigResult<IReadOnlyList<ItemGroupView>>.Ok(await inventory.ListGroupsAsync(company, ct)) : Denied<IReadOnlyList<ItemGroupView>>();

    /// <summary>The same permission and company scoping as <see cref="ListGroupsAsync"/>; only an optional search and paging are added.</summary>
    public async Task<ConfigResult<ItemGroupPage>> SearchGroupsAsync(Guid user, Guid company, string? search, int? offset, int? limit, IReadOnlyCollection<Guid>? ids = null, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemGroupsView, ct) || await Can(user, company, PermissionCatalog.Codes.ItemsView, ct)
            ? ConfigResult<ItemGroupPage>.Ok(await inventory.SearchGroupsAsync(company, search, offset, limit, ids, ct)) : Denied<ItemGroupPage>();

    public async Task<ConfigResult<ItemGroupView>> CreateGroupAsync(Guid user, Guid company, ItemGroupInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemGroupsCreate, ct) ? await inventory.CreateGroupAsync(user, company, input, ct) : Denied<ItemGroupView>();

    public async Task<ConfigResult<ItemGroupView>> UpdateGroupAsync(Guid user, Guid company, Guid id, long? rev, ItemGroupInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemGroupsUpdate, ct) ? await inventory.UpdateGroupAsync(user, company, id, rev, input, ct) : Denied<ItemGroupView>();

    public async Task<ConfigResult<ItemGroupView>> SetGroupActiveAsync(Guid user, Guid company, Guid id, long? rev, bool active, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemGroupsActivate, ct) ? await inventory.SetGroupActiveAsync(user, company, id, rev, active, ct) : Denied<ItemGroupView>();

    // ---- items
    public async Task<ConfigResult<CompanyInventoryService.ItemPage>> ListItemsAsync(Guid user, Guid company, CompanyInventoryService.ItemFilter filter, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemsView, ct) ? ConfigResult<CompanyInventoryService.ItemPage>.Ok(await inventory.ListItemsAsync(company, filter, ct)) : Denied<CompanyInventoryService.ItemPage>();

    public async Task<ConfigResult<ItemView>> GetItemAsync(Guid user, Guid company, Guid id, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.ItemsView, ct)) return Denied<ItemView>();
        var item = await inventory.GetItemAsync(company, id, ct);
        return item is null ? ConfigResult<ItemView>.Fail(ConfigStatus.NotFound, "item_not_found") : ConfigResult<ItemView>.Ok(item);
    }

    public async Task<ConfigResult<ItemView>> CreateItemAsync(Guid user, Guid company, ItemInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemsCreate, ct) ? await inventory.CreateItemAsync(user, company, input, ct) : Denied<ItemView>();

    public async Task<ConfigResult<ItemView>> UpdateItemAsync(Guid user, Guid company, Guid id, long? rev, ItemInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemsUpdate, ct) ? await inventory.UpdateItemAsync(user, company, id, rev, input, ct) : Denied<ItemView>();

    public async Task<ConfigResult<ItemView>> SetItemActiveAsync(Guid user, Guid company, Guid id, long? rev, bool active, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemsActivate, ct) ? await inventory.SetItemActiveAsync(user, company, id, rev, active, ct) : Denied<ItemView>();

    // ---- barcodes
    public async Task<ConfigResult<IReadOnlyList<BarcodeView>>> ListBarcodesAsync(Guid user, Guid company, Guid itemId, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.ItemsView, ct) ? ConfigResult<IReadOnlyList<BarcodeView>>.Ok(await inventory.ListBarcodesAsync(company, itemId, ct)) : Denied<IReadOnlyList<BarcodeView>>();

    public async Task<ConfigResult<BarcodeLookup>> LookupBarcodeAsync(Guid user, Guid company, string? barcode, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.ItemsView, ct)) return Denied<BarcodeLookup>();
        var hit = await inventory.LookupBarcodeAsync(company, barcode, ct);
        return hit is null ? ConfigResult<BarcodeLookup>.Fail(ConfigStatus.NotFound, "barcode_not_found") : ConfigResult<BarcodeLookup>.Ok(hit);
    }

    public async Task<ConfigResult<BarcodeView>> AddBarcodeAsync(Guid user, Guid company, Guid itemId, BarcodeInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.BarcodesCreate, ct) ? await inventory.AddBarcodeAsync(user, company, itemId, input, ct) : Denied<BarcodeView>();

    public async Task<ConfigResult<BarcodeView>> SetBarcodeActiveAsync(Guid user, Guid company, Guid itemId, Guid barcodeId, bool active, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.BarcodesActivate, ct) ? await inventory.SetBarcodeActiveAsync(user, company, itemId, barcodeId, active, ct) : Denied<BarcodeView>();
}
