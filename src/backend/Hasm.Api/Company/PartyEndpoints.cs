using Hasm.Api.Access;
using Hasm.Api.Auth;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Parties;
using Microsoft.AspNetCore.Http.Metadata;
using Npgsql;

namespace Hasm.Api.Company;

public static class PartySetup
{
    public static IServiceCollection AddHasmParties(this IServiceCollection services, IConfiguration configuration)
    {
        // The registry lives in the system database and is reached with the runtime (least-privilege) system account; the connection string is built lazily.
        services.AddSingleton(sp => new PartyRegistryStore(
            () =>
            {
                var options = HasmDatabaseOptions.FromConfiguration(configuration);
                options.EnsureValid(requireAdmin: false);
                return options.BuildSystemConnectionString(admin: false);
            },
            sp.GetRequiredService<AuditTrail>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PartyService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<PartyRegistryStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PartyManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<PartyService>()));
        return services;
    }
}

/// <summary>
/// Party registry (Phase 3.9A). <c>/api/v1/company/parties</c>: the local parties of the SESSION company and the shared identities it is linked to (company-wide <c>company.parties.*</c> permissions);
/// <c>/api/v1/admin/parties</c>: the central approval queue, identity administrators only (policy here, re-checked in the deciding transaction). Responses are <c>no-store</c>; changes pass the anti-CSRF
/// middleware and use <c>If-Match</c>. No endpoint returns a balance, another company's local data or the list of companies linked to a shared identity (the central queue shows the proposing company only).
/// </summary>
public static class PartyEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 64 * 1024; }
    public sealed record ApproveBody(Guid? LinkTo);
    public sealed record NoteBody(string? Note);

    public static void MapPartyEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/parties").RequireAuthorization();
        g.MapGet("/capabilities", Capabilities);
        g.MapGet("", List);
        g.MapPost("", Create).WithMetadata(new BodyLimit());
        g.MapPost("/sync", Sync);
        g.MapPost("/reconcile", Reconcile);
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}", Update).WithMetadata(new BodyLimit());
        g.MapPost("/{id:guid}/propose", Propose).WithMetadata(new BodyLimit());

        var a = app.MapGroup("/api/v1/admin/parties").RequireAuthorization(AuthDefaults.AdministratorPolicy);
        a.MapGet("/proposals", ListProposals);
        a.MapGet("/proposals/{id:guid}", GetProposal);
        a.MapPost("/proposals/{id:guid}/approve", Approve).WithMetadata(new BodyLimit());
        a.MapPost("/proposals/{id:guid}/reject", Reject).WithMetadata(new BodyLimit());
        a.MapGet("/search", Search);
    }

    private static object Identity(SharedIdentityView i) => new
    {
        globalPartyId = i.GlobalPartyId, kind = i.Kind, legalNameAr = i.LegalNameAr, legalNameEn = i.LegalNameEn, taxNumber = i.TaxNumber, registrationNumber = i.RegistrationNumber, status = i.Status, revision = i.Revision,
    };

    private static object Body(LocalPartyView v) => new
    {
        id = v.Id, code = v.Code, displayName = v.DisplayName, notes = v.Notes, isActive = v.IsActive, linkState = v.LinkState, globalPartyId = v.GlobalPartyId, revision = v.Revision,
        createdBy = v.CreatedBy, createdAtUtc = v.CreatedAtUtc, updatedAtUtc = v.UpdatedAtUtc, identity = v.Identity is null ? null : Identity(v.Identity),
        requests = v.Requests.Select(r => new
        {
            id = r.Id, kind = r.Kind, status = r.Status, outcome = r.Outcome, partyKind = r.PartyKind, legalNameAr = r.LegalNameAr, legalNameEn = r.LegalNameEn, taxNumber = r.TaxNumber,
            registrationNumber = r.RegistrationNumber, proposedStatus = r.ProposedStatus, note = r.Note, attempts = r.Attempts, lastAttemptAtUtc = r.LastAttemptAtUtc, lastErrorCode = r.LastErrorCode,
            createdBy = r.CreatedBy, createdAtUtc = r.CreatedAtUtc, deliveredAtUtc = r.DeliveredAtUtc, completedAtUtc = r.CompletedAtUtc,
            basedOnRevision = r.BasedOnRevision, decisionNote = r.DecisionNote,
        }),
        registryUnavailable = v.RegistryUnavailable,
    };

    private static object ProposalBody(PartyProposalView p) => new
    {
        id = p.Id, companyId = p.CompanyId, companyNameAr = p.CompanyNameAr, companyNameEn = p.CompanyNameEn, localPartyId = p.LocalPartyId, kind = p.Kind, targetGlobalPartyId = p.TargetGlobalPartyId,
        partyKind = p.PartyKind, legalNameAr = p.LegalNameAr, legalNameEn = p.LegalNameEn, taxNumber = p.TaxNumber, registrationNumber = p.RegistrationNumber, proposedStatus = p.ProposedStatus, note = p.Note,
        proposedBy = p.ProposedBy, proposedByName = p.ProposedByName, proposedAtUtc = p.ProposedAtUtc, status = p.Status, resolution = p.Resolution, outcomeGlobalPartyId = p.OutcomeGlobalPartyId,
        decidedBy = p.DecidedBy, decidedAtUtc = p.DecidedAtUtc, decisionNote = p.DecisionNote, revision = p.Revision,
        target = p.Target is null ? null : Identity(p.Target), matches = p.Matches.Select(Identity), basedOnRevision = p.BasedOnRevision,
    };

    /// <summary>The registry (system database) cannot be reached or the runtime account lacks its grants: a stable 503 with a code the clients translate (review M2).</summary>
    private static IResult Unavailable() => Results.Json(new { error = "registry_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult Map<T>(ConfigResult<T> r, Func<T, object> body, Func<T, long?>? revision = null) =>
        r.Status == ConfigStatus.Conflict && r.Code == "registry_unavailable" ? Unavailable() : CompanyConfigurationEndpoints.Map(r, body, revision);

    private static async Task<IResult> Central(Func<Task<IResult>> run)
    {
        try { return await run(); }
        catch (NpgsqlException) { return Unavailable(); }
    }

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

    // ---- company side

    private static async Task<IResult> Capabilities(HttpContext http, AccessService access, PartyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var c = await svc.CapabilitiesAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new { view = c.View, create = c.Create, update = c.Update, submit = c.Submit, configure = c.Configure });
    }

    private static async Task<IResult> List(HttpContext http, AccessService access, PartyManagementService svc, string? search, int? linkState, int? offset, int? limit)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListAsync(actor!.UserId, actor.CompanyId, new PartyFilter(search, linkState, offset ?? 0, limit ?? 50), http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, p => new
        {
            total = p.Total,
            parties = p.Items.Select(x => new { id = x.Id, code = x.Code, displayName = x.DisplayName, isActive = x.IsActive, linkState = x.LinkState, legalNameAr = x.LegalNameAr, legalNameEn = x.LegalNameEn, hasOpenRequest = x.HasOpenRequest, revision = x.Revision, updatedAtUtc = x.UpdatedAtUtc }),
            registryUnavailable = p.RegistryUnavailable,
        });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AccessService access, PartyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Create(LocalPartyInput? body, HttpContext http, AccessService access, PartyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/parties/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body);
    }

    private static async Task<IResult> Update(Guid id, LocalPartyInput? body, HttpContext http, AccessService access, PartyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.UpdateAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Propose(Guid id, SharedIdentityInput? body, HttpContext http, AccessService access, PartyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return Map(await svc.ProposeAsync(actor!.UserId, actor.CompanyId, id, expected, body, http.RequestAborted), Body, v => v.Revision);
    }

    private static async Task<IResult> Sync(HttpContext http, AccessService access, PartyManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.SyncAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), s => new { delivered = s.Delivered, applied = s.Applied, failed = s.Failed, waiting = s.Waiting, registryUnavailable = s.RegistryUnavailable });
    }

    private static async Task<IResult> Reconcile(HttpContext http, AccessService access, PartyManagementService svc, bool? repair)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ReconcileAsync(actor!.UserId, actor.CompanyId, repair == true, http.RequestAborted), r => new
        {
            repairRequested = r.RepairRequested, registryUnavailable = r.RegistryUnavailable,
            findings = r.Findings.Select(f => new { code = f.Code, localPartyId = f.LocalPartyId, requestId = f.RequestId, globalPartyId = f.GlobalPartyId, repairable = f.Repairable, repaired = f.Repaired, proposalId = f.ProposalId }),
        });
    }

    // ---- central side (identity administrators)

    private static Guid? Actor(HttpContext http) => http.Items[AuthDefaults.SessionItemKey] is AuthenticatedSession s ? s.UserId : null;

    private static async Task<IResult> ListProposals(HttpContext http, PartyRegistryStore registry, int? status, int? offset, int? limit)
    {
        if (Actor(http) is not { } actor) return CompanyConfigurationEndpoints.Error(401, "unauthenticated");
        return await Central(async () => CompanyConfigurationEndpoints.Map(await registry.ListProposalsAsync(actor, status, offset ?? 0, limit ?? 50, http.RequestAborted), p => new
        {
            total = p.Total,
            proposals = p.Items.Select(x => new { id = x.Id, companyId = x.CompanyId, companyNameAr = x.CompanyNameAr, companyNameEn = x.CompanyNameEn, kind = x.Kind, legalNameAr = x.LegalNameAr, legalNameEn = x.LegalNameEn, taxNumber = x.TaxNumber, registrationNumber = x.RegistrationNumber, proposedAtUtc = x.ProposedAtUtc, status = x.Status, revision = x.Revision }),
        }));
    }

    private static async Task<IResult> GetProposal(Guid id, HttpContext http, PartyRegistryStore registry)
    {
        if (Actor(http) is not { } actor) return CompanyConfigurationEndpoints.Error(401, "unauthenticated");
        return await Central(async () => CompanyConfigurationEndpoints.Map(await registry.GetProposalAsync(actor, id, http.RequestAborted), ProposalBody, v => v.Revision));
    }

    private static async Task<IResult> Approve(Guid id, ApproveBody? body, HttpContext http, PartyRegistryStore registry)
    {
        if (Actor(http) is not { } actor) return CompanyConfigurationEndpoints.Error(401, "unauthenticated");
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return await Central(async () => CompanyConfigurationEndpoints.Map(await registry.ApproveAsync(actor, id, expected, body?.LinkTo, http.RequestAborted), ProposalBody, v => v.Revision));
    }

    private static async Task<IResult> Reject(Guid id, NoteBody? body, HttpContext http, PartyRegistryStore registry)
    {
        if (Actor(http) is not { } actor) return CompanyConfigurationEndpoints.Error(401, "unauthenticated");
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return await Central(async () => CompanyConfigurationEndpoints.Map(await registry.RejectAsync(actor, id, expected, body?.Note, http.RequestAborted), ProposalBody, v => v.Revision));
    }

    private static async Task<IResult> Search(HttpContext http, PartyRegistryStore registry, string? term)
    {
        if (Actor(http) is not { } actor) return CompanyConfigurationEndpoints.Error(401, "unauthenticated");
        return await Central(async () => CompanyConfigurationEndpoints.Map(await registry.SearchIdentitiesAsync(actor, term, http.RequestAborted), l => new { identities = l.Select(Identity) }));
    }
}
