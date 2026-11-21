using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase R slice R1: the attachment-retention API over real HTTP (policy versions, legal holds, the read-only preview): authentication, company selection, RBAC, independence of author and approver, revision fences, standard validation bodies. It never deletes anything.</summary>
public class AttachmentRetentionApiTests : HttpApiTestBase
{
    private const string Base = "/api/v1/company/attachment-retention";

    private async Task<UserRecord> ApproverUser()
    {
        var (user, _) = await Member([PermissionCatalog.Codes.RetentionView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantRetentionApproverOfflineAsync(user.Login, Company.Id));
        return user;
    }

    private static object Draft(string effective, int months = 60) => new { effectiveFrom = effective, note = "api", rules = new[] { new { subjectKind = "*", sensitivity = 0, months = (int?)months, indefinite = false } } };
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    [PostgresFact]
    public async Task The_retention_api_requires_a_session_and_a_company_enforces_permissions_and_independence_and_deletes_nothing()
    {
        await StartApi();
        var admin = await SignIn(Admin);
        // no session, no answer; no selected company, no answer
        foreach (var path in new[] { "/capabilities", "/policies", "/holds", "/preview" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, Base + path)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, Base + "/policies", Draft(Today))).Status);

        // capabilities: the administrator reads, drafts, holds and executes but never approves; the basis is reported as NOT confirmed (owner-pending)
        var caps = (await Send(HttpMethod.Get, Base + "/capabilities", cookie: admin)).Json;
        Assert.Equal((true, true, true, false, true, false), (caps.GetProperty("view").GetBoolean(), caps.GetProperty("configure").GetBoolean(), caps.GetProperty("hold").GetBoolean(), caps.GetProperty("approve").GetBoolean(),
            caps.GetProperty("execute").GetBoolean(), caps.GetProperty("basisConfirmed").GetBoolean()));

        // drafting: 201 with an ETag; a malformed draft answers the standard validation body; the author cannot approve
        var created = await Send(HttpMethod.Post, Base + "/policies", Draft(Today), admin);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", created.Headers.ETag!.Tag);
        var id = created.Json.GetProperty("id").GetGuid();
        var bad = await Send(HttpMethod.Post, Base + "/policies", new { effectiveFrom = "2000-01-01", rules = Array.Empty<object>() }, admin);
        Assert.Equal((HttpStatusCode.BadRequest, "validation_failed"), (bad.Status, bad.Error));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/policies/{id}/approve", new { }, admin, ifMatch: "\"1\"")).Status);

        // approving needs the approve permission (the delegated preset), the revision, and a draft
        var approver = await ApproverUser();
        var other = await SignIn(approver);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/policies/{id}/approve", new { }, other)).Status);                         // no If-Match
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Base}/policies/{id}/approve", new { }, other, ifMatch: "\"9\"")).Status);
        var approved = await Send(HttpMethod.Post, $"{Base}/policies/{id}/approve", new { }, other, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, approved.Status);
        Assert.Equal((2, approver.Id), (approved.Json.GetProperty("status").GetInt32(), approved.Json.GetProperty("approvedBy").GetGuid()));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"{Base}/policies/{id}/approve", new { }, other, ifMatch: "\"2\"")).Status);          // no longer a draft
        Assert.Single((await Send(HttpMethod.Get, Base + "/policies", cookie: admin)).Json.GetProperty("policies").EnumerateArray());

        // holds: placed by the holder, released only by someone else with the approve permission
        var hold = await Send(HttpMethod.Post, Base + "/holds", new { scope = 1, reason = "audit", reference = "CASE-1" }, admin);
        Assert.Equal(HttpStatusCode.Created, hold.Status);
        var holdId = hold.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Base + "/holds", new { scope = 2, subjectKind = "nope", subjectId = Guid.NewGuid(), reason = "x" }, admin)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/holds/{holdId}/release", new { reason = "done" }, admin, ifMatch: "\"1\"")).Status);   // the administrator lacks approve
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/holds/{holdId}/release", new { reason = "closed" }, other, ifMatch: "\"1\"")).Status);
        Assert.Empty((await Send(HttpMethod.Get, Base + "/holds", cookie: admin)).Json.GetProperty("holds").EnumerateArray());
        Assert.Single((await Send(HttpMethod.Get, Base + "/holds?includeReleased=true", cookie: admin)).Json.GetProperty("holds").EnumerateArray());

        // the preview: bounded paging with the standard body for a malformed parameter; no attachment is touched
        var preview = await Send(HttpMethod.Get, Base + "/preview?limit=5&offset=0", cookie: admin);
        Assert.Equal((HttpStatusCode.OK, 0L, false), (preview.Status, preview.Json.GetProperty("total").GetInt64(), preview.Json.GetProperty("basisConfirmed").GetBoolean()));
        var malformed = await Send(HttpMethod.Get, Base + "/preview?limit=abc", cookie: admin);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), (malformed.Status, malformed.Error));

        // a user without the permission gets nothing; no DELETE route exists
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        var blind = await SignIn(outsider);
        foreach (var path in new[] { "/policies", "/holds", "/preview" })
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Base + path, cookie: blind)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base + "/policies", Draft(Today), blind)).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(HttpMethod.Delete, $"{Base}/policies/{id}", cookie: admin)).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(HttpMethod.Delete, $"{Base}/holds/{holdId}", cookie: admin)).Status);
    }
}
