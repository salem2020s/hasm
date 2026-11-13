using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>/api/v1/company/numbering: document numbering rules. Company from the server-side session only; authorized inside the management service; If-Match with the rule revision.</summary>
public static class NumberingEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 4 * 1024; }

    public static void MapNumberingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/numbering").RequireAuthorization();
        g.MapGet("", List);
        g.MapPut("/{docType}", Put).WithMetadata(new BodyLimit());
    }

    private static object Body(NumberingRuleView r) => new
    {
        docType = r.DocType, prefix = r.Prefix, separator = r.Separator, width = r.Width, perBranch = r.PerBranch, perFiscalYear = r.PerFiscalYear,
        revision = r.Revision, configured = r.Exists, numbersIssued = r.NumbersIssued,
    };

    private static async Task<IResult> List(HttpContext http, AccessService access, NumberingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { rules = r.Value!.Select(Body), can = new { configure = eff.Has(PermissionCatalog.Codes.NumberingConfigure, ScopeTarget.Company) } });
    }

    private static async Task<IResult> Put(string docType, NumberingRuleInput? body, HttpContext http, AccessService access, NumberingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.PutRuleAsync(actor!.UserId, actor.CompanyId, docType, expected, body, http.RequestAborted), Body, v => v.Revision);
    }
}
