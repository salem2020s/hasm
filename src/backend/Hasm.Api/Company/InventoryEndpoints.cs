using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class InventorySetup
{
    public static IServiceCollection AddHasmInventory(this IServiceCollection services)
    {
        services.AddSingleton(sp => new CompanyInventoryService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new InventoryManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<CompanyInventoryService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/inventory/*: inventory master data (units, item groups, items, barcodes, settings). The company comes ONLY from the server-side session, every call is authorized inside
/// <see cref="InventoryManagementService"/> (company-wide RBAC), responses are <c>no-store</c>, state-changing requests pass the anti-CSRF middleware, and changes use <c>If-Match</c> with
/// the revision (428 missing, 412 stale). Validation, invariants, auditing and concurrency are enforced server-side and by PostgreSQL; Web and Windows Desktop use exactly these endpoints.
/// This API creates no stock movement and no accounting entry.
/// </summary>
public static class InventoryEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 64 * 1024; }

    public static void MapInventoryEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/inventory").RequireAuthorization();
        g.MapGet("/capabilities", Capabilities);
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", PutSettings).WithMetadata(new BodyLimit());
        g.MapGet("/units", ListUnits);
        g.MapPost("/units", CreateUnit).WithMetadata(new BodyLimit());
        g.MapPut("/units/{id:guid}", UpdateUnit).WithMetadata(new BodyLimit());
        g.MapPost("/units/{id:guid}/deactivate", (Guid id, HttpContext h, AccessService a, InventoryManagementService s) => SetUnit(id, false, h, a, s));
        g.MapPost("/units/{id:guid}/activate", (Guid id, HttpContext h, AccessService a, InventoryManagementService s) => SetUnit(id, true, h, a, s));
        g.MapGet("/groups", ListGroups);
        g.MapPost("/groups", CreateGroup).WithMetadata(new BodyLimit());
        g.MapPut("/groups/{id:guid}", UpdateGroup).WithMetadata(new BodyLimit());
        g.MapPost("/groups/{id:guid}/deactivate", (Guid id, HttpContext h, AccessService a, InventoryManagementService s) => SetGroup(id, false, h, a, s));
        g.MapPost("/groups/{id:guid}/activate", (Guid id, HttpContext h, AccessService a, InventoryManagementService s) => SetGroup(id, true, h, a, s));
        g.MapGet("/items", ListItems);
        g.MapPost("/items", CreateItem).WithMetadata(new BodyLimit());
        g.MapGet("/items/{id:guid}", GetItem);
        g.MapPut("/items/{id:guid}", UpdateItem).WithMetadata(new BodyLimit());
        g.MapPost("/items/{id:guid}/deactivate", (Guid id, HttpContext h, AccessService a, InventoryManagementService s) => SetItem(id, false, h, a, s));
        g.MapPost("/items/{id:guid}/activate", (Guid id, HttpContext h, AccessService a, InventoryManagementService s) => SetItem(id, true, h, a, s));
        g.MapGet("/items/{id:guid}/barcodes", ListBarcodes);
        g.MapPost("/items/{id:guid}/barcodes", AddBarcode).WithMetadata(new BodyLimit());
        g.MapPost("/items/{id:guid}/barcodes/{barcodeId:guid}/deactivate", (Guid id, Guid barcodeId, HttpContext h, AccessService a, InventoryManagementService s) => SetBarcode(id, barcodeId, false, h, a, s));
        g.MapPost("/items/{id:guid}/barcodes/{barcodeId:guid}/activate", (Guid id, Guid barcodeId, HttpContext h, AccessService a, InventoryManagementService s) => SetBarcode(id, barcodeId, true, h, a, s));
        g.MapGet("/barcode-lookup", Lookup);
    }

    private static object Body(UnitView u) => new { id = u.Id, code = u.Code, nameAr = u.NameAr, nameEn = u.NameEn, decimals = u.Decimals, isActive = u.IsActive, revision = u.Revision, createdAtUtc = u.CreatedAtUtc, updatedAtUtc = u.UpdatedAtUtc };
    private static object Body(ItemGroupView g) => new { id = g.Id, code = g.Code, nameAr = g.NameAr, nameEn = g.NameEn, parentId = g.ParentId, valuation = g.Valuation, effectiveValuation = g.EffectiveValuation, isActive = g.IsActive, revision = g.Revision, createdAtUtc = g.CreatedAtUtc, updatedAtUtc = g.UpdatedAtUtc };
    private static object Body(ItemView i) => new
    {
        id = i.Id, code = i.Code, nameAr = i.NameAr, nameEn = i.NameEn, itemType = i.ItemType, groupId = i.GroupId, baseUnitId = i.BaseUnitId, purchaseUnitId = i.PurchaseUnitId, salesUnitId = i.SalesUnitId,
        isLot = i.IsLot, isSerial = i.IsSerial, hasExpiry = i.HasExpiry, notes = i.Notes, isActive = i.IsActive, revision = i.Revision, effectiveValuation = i.EffectiveValuation,
        alternateUnits = i.AlternateUnits.Select(a => new { unitId = a.UnitId, unitCode = a.UnitCode, factor = a.Factor }), createdAtUtc = i.CreatedAtUtc, updatedAtUtc = i.UpdatedAtUtc,
    };
    private static object Body(BarcodeView b) => new { id = b.Id, itemId = b.ItemId, unitId = b.UnitId, barcode = b.Barcode, kind = b.Kind, isActive = b.IsActive, createdAtUtc = b.CreatedAtUtc, updatedAtUtc = b.UpdatedAtUtc };
    private static object Body(InventorySettingsView s) => new { defaultValuation = s.DefaultValuation, revision = s.Revision };

    private sealed class CreatedEtag(string location, long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers.ETag = $"\"{revision}\"";
            http.Response.Headers.Location = location;
            http.Response.StatusCode = StatusCodes.Status201Created;
            return http.Response.WriteAsJsonAsync(body);
        }
    }

    private static IResult List<T>(ConfigResult<IReadOnlyList<T>> r, string name, Func<T, object> body) =>
        r.IsOk ? Results.Json(new Dictionary<string, object> { [name] = r.Value!.Select(body).ToList() }) : CompanyConfigurationEndpoints.Map(r, _ => new { });

    private static async Task<IResult> Capabilities(HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return Results.Json(await svc.CapabilitiesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted));
    }

    // ---- settings
    private static async Task<IResult> GetSettings(HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetSettingsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> PutSettings(InventorySettingsInput? body, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.SaveSettingsAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    // ---- units
    /// <summary>Without <c>search</c>, <c>offset</c>, <c>limit</c> or <c>ids</c> exactly the unpaged list (no <c>total</c>); with any of them one bounded page (default 50, at most 500, ordered by code) plus <c>total</c>. <c>ids</c> (repeated, at most 100, every value a GUID, else the standard <c>invalid_request</c> 400) is a by-ids lookup: only those units of the caller's company come back (page default 500). Same permission in every form.</summary>
    private static async Task<IResult> ListUnits(string? search, string? offset, string? limit, string[]? ids, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        if (ListParameters.TryParse(search, offset, limit, out var q) is { } bad) return ListParameters.Invalid(bad);
        if (ListParameters.TryParseIds(ids, out var unitIds) is { } badIds) return ListParameters.Invalid(badIds);
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!q.Requested && unitIds is null) return List(await svc.ListUnitsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), "units", u => Body(u));
        var r = await svc.SearchUnitsAsync(actor!.UserId, actor.CompanyId, q.Search, q.Offset, q.Limit, unitIds, http.RequestAborted);
        return r.IsOk ? Results.Json(new { units = r.Value!.Items.Select(u => Body(u)).ToList(), total = r.Value.Total }) : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> CreateUnit(UnitInput? body, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateUnitAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/inventory/units/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> UpdateUnit(Guid id, UnitInput? body, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.UpdateUnitAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> SetUnit(Guid id, bool active, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.SetUnitActiveAsync(actor!.UserId, actor.CompanyId, id, expected, active, http.RequestAborted), Body, v => v.Revision);
    }

    // ---- groups
    /// <summary>Without <c>search</c>, <c>offset</c>, <c>limit</c> or <c>ids</c> exactly the unpaged list (no <c>total</c>); with any of them one bounded page (default 50, at most 500, ordered by code) plus <c>total</c>. <c>ids</c> (repeated, at most 100, every value a GUID, else the standard <c>invalid_request</c> 400) is a by-ids lookup: only those groups of the caller's company come back (page default 500; the effective valuation still resolves through ancestors). Same permission in every form.</summary>
    private static async Task<IResult> ListGroups(string? search, string? offset, string? limit, string[]? ids, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        if (ListParameters.TryParse(search, offset, limit, out var q) is { } bad) return ListParameters.Invalid(bad);
        if (ListParameters.TryParseIds(ids, out var groupIds) is { } badIds) return ListParameters.Invalid(badIds);
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!q.Requested && groupIds is null) return List(await svc.ListGroupsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), "groups", g => Body(g));
        var r = await svc.SearchGroupsAsync(actor!.UserId, actor.CompanyId, q.Search, q.Offset, q.Limit, groupIds, http.RequestAborted);
        return r.IsOk ? Results.Json(new { groups = r.Value!.Items.Select(g => Body(g)).ToList(), total = r.Value.Total }) : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> CreateGroup(ItemGroupInput? body, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateGroupAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/inventory/groups/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> UpdateGroup(Guid id, ItemGroupInput? body, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.UpdateGroupAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> SetGroup(Guid id, bool active, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.SetGroupActiveAsync(actor!.UserId, actor.CompanyId, id, expected, active, http.RequestAborted), Body, v => v.Revision);
    }

    // ---- items
    private static async Task<IResult> ListItems(HttpContext http, AccessService access, InventoryManagementService svc, string? search, Guid? groupId, bool? active, int? type, int? offset, int? limit)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListItemsAsync(actor!.UserId, actor.CompanyId, new CompanyInventoryService.ItemFilter(search, groupId, active, type, offset ?? 0, limit ?? 50), http.RequestAborted);
        return r.IsOk ? Results.Json(new { items = r.Value!.Items.Select(Body), total = r.Value.Total }) : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> GetItem(Guid id, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetItemAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> CreateItem(ItemInput? body, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateItemAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/inventory/items/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> UpdateItem(Guid id, ItemInput? body, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.UpdateItemAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> SetItem(Guid id, bool active, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.SetItemActiveAsync(actor!.UserId, actor.CompanyId, id, expected, active, http.RequestAborted), Body, v => v.Revision);
    }

    // ---- barcodes
    private static async Task<IResult> ListBarcodes(Guid id, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return List(await svc.ListBarcodesAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), "barcodes", b => Body(b));
    }

    private static async Task<IResult> AddBarcode(Guid id, BarcodeInput? body, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.AddBarcodeAsync(actor!.UserId, actor.CompanyId, id, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/inventory/items/{id}/barcodes/{r.Value!.Id}", 1, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> SetBarcode(Guid id, Guid barcodeId, bool active, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.SetBarcodeActiveAsync(actor!.UserId, actor.CompanyId, id, barcodeId, active, http.RequestAborted), Body);
    }

    private static async Task<IResult> Lookup(string? barcode, HttpContext http, AccessService access, InventoryManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.LookupBarcodeAsync(actor!.UserId, actor.CompanyId, barcode, http.RequestAborted),
            l => new { barcode = Body(l.Barcode), itemCode = l.ItemCode, itemNameAr = l.ItemNameAr, itemNameEn = l.ItemNameEn, unitCode = l.UnitCode, unitFactor = l.UnitFactor });
    }
}
