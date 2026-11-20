using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Pricing;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class PricingSetup
{
    public static IServiceCollection AddHasmPricing(this IServiceCollection services)
    {
        services.AddSingleton(sp => new PricingConfigService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PricingManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<PricingConfigService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/pricing: pricing configuration and quotes (Phase 3.11 slice 2c). The company comes ONLY from the server-side session; every call is authorized inside
/// <see cref="PricingManagementService"/> (company-wide <c>company.pricing.view</c> for reads and quotes, <c>company.pricing.configure</c> for writes, <c>company.pricing.override</c> for a manual override
/// in a quote). Changes of lists, entries and rules use <c>If-Match</c> with the entity revision; the policy uses <c>If-Match</c> with the policy VERSION (0 = none saved yet) and is append-only. There is no
/// DELETE endpoint: nothing here is ever removed. A quote stores nothing: it returns the sealed snapshot a document would keep.
/// </summary>
public static class PricingEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 64 * 1024; }

    public static void MapPricingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/pricing").RequireAuthorization();
        g.MapGet("/policy", GetPolicy);
        g.MapPost("/policy", SavePolicy).WithMetadata(new BodyLimit());
        g.MapGet("/price-lists", ListLists);
        g.MapPost("/price-lists", CreateList).WithMetadata(new BodyLimit());
        g.MapPut("/price-lists/{id:guid}", UpdateList).WithMetadata(new BodyLimit());
        g.MapGet("/price-lists/{id:guid}/entries", ListEntries);
        g.MapPost("/price-lists/{id:guid}/entries", CreateEntry).WithMetadata(new BodyLimit());
        g.MapPut("/entries/{id:guid}", UpdateEntry).WithMetadata(new BodyLimit());
        g.MapGet("/discounts", ListDiscounts);
        g.MapPost("/discounts", CreateDiscount).WithMetadata(new BodyLimit());
        g.MapPut("/discounts/{id:guid}", UpdateDiscount).WithMetadata(new BodyLimit());
        g.MapGet("/customer-groups", ListGroups);
        g.MapPost("/customer-groups", CreateGroup).WithMetadata(new BodyLimit());
        g.MapPut("/customer-groups/{id:guid}", UpdateGroup).WithMetadata(new BodyLimit());
        g.MapGet("/customer-assignments", ListAssignments);
        g.MapPut("/customer-assignments/{partyId:guid}", SetAssignment).WithMetadata(new BodyLimit());
        g.MapPost("/quote", Quote).WithMetadata(new BodyLimit());
    }

    private static object PolicyBody(PricingPolicyView p) => new
    {
        version = p.Version, isDefault = p.IsDefault, priceDecimals = p.Policy.PriceDecimals, moneyDecimals = p.Policy.MoneyDecimals, minMarginPercent = p.Policy.MinMarginPercent,
        marginAction = (short)p.Policy.Action, listPrecedence = (short)p.Policy.ListPrecedence, discountPrecedence = (short)p.Policy.DiscountPrecedence, createdAtUtc = p.CreatedAtUtc,
    };
    private static object ListBody(PriceListView l) => new { id = l.Id, code = l.Code, nameAr = l.NameAr, nameEn = l.NameEn, priority = l.Priority, currency = l.Currency, validFrom = l.ValidFrom, validTo = l.ValidTo, isActive = l.IsActive, customerId = l.CustomerId, customerGroupId = l.CustomerGroupId, revision = l.Revision, updatedAtUtc = l.UpdatedAtUtc };
    private static object EntryBody(PriceEntryView e) => new { id = e.Id, priceListId = e.PriceListId, itemId = e.ItemId, itemGroupId = e.ItemGroupId, unitId = e.UnitId, minQuantity = e.MinQuantity, unitPrice = e.UnitPrice, isActive = e.IsActive, revision = e.Revision, updatedAtUtc = e.UpdatedAtUtc };
    private static object DiscountBody(DiscountRuleView d) => new
    {
        id = d.Id, code = d.Code, nameAr = d.NameAr, nameEn = d.NameEn, priority = d.Priority, validFrom = d.ValidFrom, validTo = d.ValidTo, isActive = d.IsActive, percent = d.Percent, minQuantity = d.MinQuantity,
        customerId = d.CustomerId, customerGroupId = d.CustomerGroupId, itemId = d.ItemId, itemGroupId = d.ItemGroupId, revision = d.Revision, updatedAtUtc = d.UpdatedAtUtc,
    };
    private static object GroupBody(CustomerGroupView v) => new { id = v.Id, code = v.Code, nameAr = v.NameAr, nameEn = v.NameEn, isActive = v.IsActive, revision = v.Revision, updatedAtUtc = v.UpdatedAtUtc };
    private static object AssignmentBody(CustomerGroupAssignmentView v) => new { partyId = v.PartyId, partyCode = v.PartyCode, partyName = v.PartyName, customerGroupId = v.CustomerGroupId, customerGroupCode = v.CustomerGroupCode, revision = v.Revision };
    private static object SnapshotBody(PricingSnapshot s) => new
    {
        version = s.Version, contentHash = s.ContentHash,
        policy = new { priceDecimals = s.Policy.PriceDecimals, moneyDecimals = s.Policy.MoneyDecimals, minMarginPercent = s.Policy.MinMarginPercent, marginAction = (short)s.Policy.Action, listPrecedence = (short)s.Policy.ListPrecedence, discountPrecedence = (short)s.Policy.DiscountPrecedence },
        result = new
        {
            listUnitPrice = s.Result.ListUnitPrice, priceRuleRef = s.Result.PriceRuleRef, discountPercent = s.Result.DiscountPercent, discountRuleRef = s.Result.DiscountRuleRef, netUnitPrice = s.Result.NetUnitPrice, lineNet = s.Result.LineNet,
            overridden = s.Result.Overridden, overrideReason = s.Result.OverrideReason, marginPercent = s.Result.MarginPercent, margin = (short)s.Result.Margin, requiresApproval = s.Result.RequiresApproval,
            trace = s.Result.Trace.Select(t => new { kind = t.Kind, ruleRef = t.RuleRef, outcome = t.Outcome }),
        },
    };

    private static async Task<IResult> Read<T>(HttpContext http, AccessService access, PricingManagementService svc, Func<Guid, Guid, Task<ConfigResult<T>>> call, Func<T, bool, object> body)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await call(actor!.UserId, actor.CompanyId);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var caps = await svc.CapabilitiesAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(body(r.Value!, caps.Configure));
    }

    private static Task<IResult> GetPolicy(HttpContext http, AccessService access, PricingManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.GetPolicyAsync(u, c, http.RequestAborted), (v, can) => new { policy = PolicyBody(v), can = new { configure = can } });
    private static Task<IResult> ListLists(HttpContext http, AccessService access, PricingManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.ListPriceListsAsync(u, c, http.RequestAborted), (v, can) => new { priceLists = v.Select(ListBody), can = new { configure = can } });
    private static Task<IResult> ListEntries(Guid id, HttpContext http, AccessService access, PricingManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.ListEntriesAsync(u, c, id, http.RequestAborted), (v, can) => new { entries = v.Select(EntryBody), can = new { configure = can } });
    private static Task<IResult> ListDiscounts(HttpContext http, AccessService access, PricingManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.ListDiscountsAsync(u, c, http.RequestAborted), (v, can) => new { discounts = v.Select(DiscountBody), can = new { configure = can } });

    private static Task<IResult> ListGroups(HttpContext http, AccessService access, PricingManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.ListCustomerGroupsAsync(u, c, http.RequestAborted), (v, can) => new { customerGroups = v.Select(GroupBody), can = new { configure = can } });
    private static Task<IResult> ListAssignments(HttpContext http, AccessService access, PricingManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.ListAssignmentsAsync(u, c, http.RequestAborted), (v, can) => new { assignments = v.Select(AssignmentBody), can = new { configure = can } });

    private static async Task<IResult> Write<T>(HttpContext http, AccessService access, bool needsRevision, Func<Guid, Guid, long?, Task<ConfigResult<T>>> call, Func<T, object> body, Func<T, long?>? revision)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        long? expected = null;
        if (needsRevision && !CompanyConfigurationEndpoints.TryReadIfMatch(http, out expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await call(actor!.UserId, actor.CompanyId, expected), body, revision);
    }

    private static Task<IResult> SavePolicy(PricingPolicyInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, true, (u, c, version) => svc.SavePolicyAsync(u, c, version, body, http.RequestAborted), PolicyBody, p => p.Version);
    private static Task<IResult> CreateList(PriceListInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreatePriceListAsync(u, c, body, http.RequestAborted), ListBody, l => l.Revision);
    private static Task<IResult> UpdateList(Guid id, PriceListInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdatePriceListAsync(u, c, id, rev, body, http.RequestAborted), ListBody, l => l.Revision);
    private static Task<IResult> CreateEntry(Guid id, PriceEntryInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreateEntryAsync(u, c, id, body, http.RequestAborted), EntryBody, e => e.Revision);
    private static Task<IResult> UpdateEntry(Guid id, PriceEntryInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdateEntryAsync(u, c, id, rev, body, http.RequestAborted), EntryBody, e => e.Revision);
    private static Task<IResult> CreateDiscount(DiscountRuleInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreateDiscountAsync(u, c, body, http.RequestAborted), DiscountBody, d => d.Revision);
    private static Task<IResult> UpdateDiscount(Guid id, DiscountRuleInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdateDiscountAsync(u, c, id, rev, body, http.RequestAborted), DiscountBody, d => d.Revision);

    private static Task<IResult> CreateGroup(CustomerGroupInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreateCustomerGroupAsync(u, c, body, http.RequestAborted), GroupBody, v => v.Revision);
    private static Task<IResult> UpdateGroup(Guid id, CustomerGroupInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdateCustomerGroupAsync(u, c, id, rev, body, http.RequestAborted), GroupBody, v => v.Revision);
    /// <summary>If-Match carries the assignment revision looked at (0 = the customer was never assigned).</summary>
    private static Task<IResult> SetAssignment(Guid partyId, CustomerGroupAssignmentInput? body, HttpContext http, AccessService access, PricingManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.SetAssignmentAsync(u, c, partyId, rev, body, http.RequestAborted), AssignmentBody, v => v.Revision);

    /// <summary>The quote body (the customer group is never accepted from the caller: the server derives it from the customer's stored assignment, and the item group from the item): the priced line and an optional override. The override (price and/or discount with a mandatory reason) additionally needs <c>company.pricing.override</c>.</summary>
    public sealed record QuoteRequest(DateOnly? Date, string? Currency, Guid? ItemId, Guid? ItemGroupId, Guid? UnitId, decimal? Quantity, Guid? CustomerId, decimal? UnitCost, PriceOverride? Override);

    private static async Task<IResult> Quote(QuoteRequest? body, HttpContext http, AccessService access, PricingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (body is null || body.Date is null || body.ItemId is null || body.UnitId is null || body.Quantity is null || string.IsNullOrWhiteSpace(body.Currency)) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        var request = new PricingRequest(body.Date.Value, body.Currency ?? "", body.ItemId.Value, body.ItemGroupId, body.UnitId.Value, body.Quantity.Value, body.CustomerId, null, body.UnitCost);
        return CompanyConfigurationEndpoints.Map(await svc.QuoteAsync(actor!.UserId, actor.CompanyId, request, body.Override, http.RequestAborted), SnapshotBody);
    }
}
