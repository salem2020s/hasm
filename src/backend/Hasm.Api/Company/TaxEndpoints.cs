using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Taxes;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class TaxSetup
{
    public static IServiceCollection AddHasmTaxes(this IServiceCollection services, IConfiguration configuration)
    {
        // Deployment policy only (see TaxConfigOptions): how far ahead a rate version may be dated. The SRS sets no limit; absent = the default of one year, 0 = unlimited.
        var options = new TaxConfigOptions(configuration.GetValue("Tax:MaxRateLeadDays", TaxConfigService.DefaultMaxRateLeadDays));
        services.AddSingleton(sp => new TaxConfigService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>(), options));
        services.AddSingleton(sp => new TaxManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<TaxConfigService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/taxes: tax configuration (Phase 3.10 slice 3). The company comes ONLY from the server-side session; every call is authorized inside
/// <see cref="TaxManagementService"/> (company-wide <c>company.tax.view</c> for reads and resolution, <c>company.tax.configure</c> for writes); changes of taxes, classes and rules use
/// <c>If-Match</c> with the entity revision; rate and policy versions are append-only. There is no DELETE endpoint: nothing here is ever removed.
/// </summary>
public static class TaxEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 64 * 1024; }
    public sealed record ClassAssignment(Guid? ClassId);

    public static void MapTaxEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/taxes").RequireAuthorization();
        g.MapGet("", ListTaxes);
        g.MapPost("", CreateTax).WithMetadata(new BodyLimit());
        g.MapPut("/{id:guid}", UpdateTax).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/rates", AddRate).WithMetadata(new BodyLimit());
        g.MapGet("/classes", ListClasses);
        g.MapPost("/classes", CreateClass).WithMetadata(new BodyLimit());
        g.MapPut("/classes/{id:guid}", UpdateClass).WithMetadata(new BodyLimit());
        g.MapPut("/item-classes/{itemId:guid}", AssignItem).WithMetadata(new BodyLimit());
        g.MapPut("/party-classes/{partyId:guid}", AssignParty).WithMetadata(new BodyLimit());
        g.MapGet("/rules", ListRules);
        g.MapPost("/rules", CreateRule).WithMetadata(new BodyLimit());
        g.MapPut("/rules/{id:guid}", UpdateRule).WithMetadata(new BodyLimit());
        g.MapGet("/policy", GetPolicy);
        g.MapPost("/policy", AddPolicy).WithMetadata(new BodyLimit());
        g.MapGet("/resolve", Resolve);
    }

    private static object TaxBody(TaxView t) => new { id = t.Id, code = t.Code, nameAr = t.NameAr, nameEn = t.NameEn, isActive = t.IsActive, revision = t.Revision, updatedAtUtc = t.UpdatedAtUtc,
        rates = t.Rates.OrderBy(r => r.EffectiveFrom).Select(r => new { effectiveFrom = r.EffectiveFrom, treatment = (short)r.Treatment, ratePercent = r.RatePercent }) };
    private static object ClassBody(TaxClassView c) => new { id = c.Id, kind = (short)c.Kind, code = c.Code, nameAr = c.NameAr, nameEn = c.NameEn, isActive = c.IsActive, revision = c.Revision };
    private static object RuleBody(TaxRuleView v) => new
    {
        id = v.Rule.Id, documentType = v.Rule.DocumentType, operation = v.Rule.Operation, itemClassId = v.Rule.ItemClassId, partyClassId = v.Rule.PartyClassId, taxId = v.Rule.TaxId,
        priority = v.Rule.Priority, includesTaxIds = v.Rule.IncludesTaxIds, validFrom = v.Rule.ValidFrom, validTo = v.Rule.ValidTo, isActive = v.Rule.IsActive,
        treatmentOverride = v.Rule.TreatmentOverride is { } o ? (short?)o : null, revision = v.Revision,
    };
    private static object PolicyBody(TaxPolicyView? p) => p is null ? new { configured = false } : new { configured = true, id = p.Id, version = p.Version, moneyDecimals = p.Policy.MoneyDecimals, mode = (short)p.Policy.Mode, level = (short)p.Policy.Level, createdAtUtc = p.CreatedAtUtc };

    private static async Task<IResult> Read<T>(HttpContext http, AccessService access, TaxManagementService svc, Func<Guid, Guid, Task<ConfigResult<T>>> call, Func<T, bool, object> body)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await call(actor!.UserId, actor.CompanyId);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var caps = await svc.CapabilitiesAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(body(r.Value!, caps.Configure));
    }

    private static Task<IResult> ListTaxes(HttpContext http, AccessService access, TaxManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.ListTaxesAsync(u, c, http.RequestAborted), (v, can) => new { taxes = v.Select(TaxBody), can = new { configure = can } });
    private static Task<IResult> ListClasses(HttpContext http, AccessService access, TaxManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.ListClassesAsync(u, c, http.RequestAborted), (v, can) => new { classes = v.Select(ClassBody), can = new { configure = can } });
    private static Task<IResult> ListRules(HttpContext http, AccessService access, TaxManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.ListRulesAsync(u, c, http.RequestAborted), (v, can) => new { rules = v.Select(RuleBody), can = new { configure = can } });
    private static Task<IResult> GetPolicy(HttpContext http, AccessService access, TaxManagementService svc) =>
        Read(http, access, svc, (u, c) => svc.GetPolicyAsync(u, c, http.RequestAborted), (v, can) => new { policy = PolicyBody(v), can = new { configure = can } });

    private static async Task<IResult> Write<T>(HttpContext http, AccessService access, bool needsRevision, Func<Guid, Guid, long?, Task<ConfigResult<T>>> call, Func<T, object> body, Func<T, long?>? revision)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        long? expected = null;
        if (needsRevision && !CompanyConfigurationEndpoints.TryReadIfMatch(http, out expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await call(actor!.UserId, actor.CompanyId, expected), body, revision);
    }

    private static Task<IResult> CreateTax(TaxInput? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreateTaxAsync(u, c, body, http.RequestAborted), TaxBody, t => t.Revision);
    private static Task<IResult> UpdateTax(Guid id, TaxInput? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdateTaxAsync(u, c, id, rev, body, http.RequestAborted), TaxBody, t => t.Revision);
    private static Task<IResult> AddRate(Guid id, TaxRateInput? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.AddRateAsync(u, c, id, body, http.RequestAborted), TaxBody, t => t.Revision);
    private static Task<IResult> CreateClass(TaxClassInput? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreateClassAsync(u, c, body, http.RequestAborted), ClassBody, t => t.Revision);
    private static Task<IResult> UpdateClass(Guid id, TaxClassInput? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdateClassAsync(u, c, id, rev, body, http.RequestAborted), ClassBody, t => t.Revision);
    private static Task<IResult> AssignItem(Guid itemId, ClassAssignment? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.AssignItemClassAsync(u, c, itemId, body?.ClassId, http.RequestAborted), _ => new { ok = true }, null);
    private static Task<IResult> AssignParty(Guid partyId, ClassAssignment? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.AssignPartyClassAsync(u, c, partyId, body?.ClassId, http.RequestAborted), _ => new { ok = true }, null);
    private static Task<IResult> CreateRule(TaxRuleInput? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.CreateRuleAsync(u, c, body, http.RequestAborted), RuleBody, v => v.Revision);
    private static Task<IResult> UpdateRule(Guid id, TaxRuleInput? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, true, (u, c, rev) => svc.UpdateRuleAsync(u, c, id, rev, body, http.RequestAborted), RuleBody, v => v.Revision);
    private static Task<IResult> AddPolicy(TaxPolicyInput? body, HttpContext http, AccessService access, TaxManagementService svc) =>
        Write(http, access, false, (u, c, _) => svc.AddPolicyAsync(u, c, body, http.RequestAborted), v => PolicyBody(v), null);

    private static async Task<IResult> Resolve(string? documentType, string? operation, Guid? itemId, Guid? partyId, DateOnly? taxDate, HttpContext http, AccessService access, TaxManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (string.IsNullOrWhiteSpace(documentType) || string.IsNullOrWhiteSpace(operation) || taxDate is null) return CompanyConfigurationEndpoints.Error(400, "invalid_request");
        var r = await svc.ResolveAsync(actor!.UserId, actor.CompanyId, documentType, operation, itemId, partyId, taxDate.Value, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new
        {
            policyVersion = v.PolicyVersion, policy = new { moneyDecimals = v.Policy.MoneyDecimals, mode = (short)v.Policy.Mode, level = (short)v.Policy.Level },
            components = v.Components.Select(x => new { taxId = x.TaxId, code = x.Code, treatment = (short)x.Treatment, ratePercent = x.RatePercent, priority = x.Priority, includesInBase = x.IncludesInBase ?? [], ruleRef = x.RuleRef }),
        });
    }
}
