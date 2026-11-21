using System.Security.Claims;
using System.Text.Json;
using Hasm.Api.Auth;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Api.Tests;

/// <summary>
/// Phase 4.1 slice S3a (D-160..D-163): the endpoint-table test for the explicit privileged set (a new route under a privileged prefix cannot silently skip the MFA policy, and a new
/// route under an admin prefix must be classified), and the policy handlers with the mandatory pass-through for users without MFA (D-161).
/// </summary>
public class MfaEnforcementTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private IReadOnlyList<RouteEndpoint> Routes() =>
        factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();

    private static string Pattern(RouteEndpoint e) => "/" + (e.RoutePattern.RawText ?? "").TrimStart('/');
    private static bool UnderPrefix(string pattern, string prefix) => pattern.Equals(prefix, StringComparison.OrdinalIgnoreCase) || pattern.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    private static bool HasPolicy(RouteEndpoint e, string policy) => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == policy);

    private static IEnumerable<string> Methods(RouteEndpoint e) => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];

    [Fact]
    public void Every_route_under_a_privileged_prefix_carries_the_MfaVerified_or_MfaFresh_policy()
    {
        var checkedRoutes = 0;
        foreach (var e in Routes().Where(r => MfaEnforcement.PrivilegedPrefixes.Any(p => UnderPrefix(Pattern(r), p))))
        {
            checkedRoutes++;
            Assert.True(HasPolicy(e, AuthDefaults.MfaVerifiedPolicy) || HasPolicy(e, AuthDefaults.MfaFreshPolicy), $"{string.Join("/", Methods(e))} {Pattern(e)} has no MFA policy");
        }
        Assert.True(checkedRoutes >= 15, $"only {checkedRoutes} privileged routes were found; the enumeration is broken");
        foreach (var prefix in MfaEnforcement.PrivilegedPrefixes) Assert.Contains(Routes(), r => UnderPrefix(Pattern(r), prefix));   // no stale prefix
    }

    [Fact]
    public void The_named_fresh_operations_exist_and_carry_the_MfaFresh_policy()
    {
        foreach (var (method, pattern) in MfaEnforcement.FreshOperations)
        {
            var route = Assert.Single(Routes(), r => Pattern(r).Equals(pattern, StringComparison.OrdinalIgnoreCase) && Methods(r).Contains(method, StringComparer.OrdinalIgnoreCase));
            Assert.True(HasPolicy(route, AuthDefaults.MfaFreshPolicy), $"{method} {pattern} lacks the MfaFresh policy");
        }
        // and no other route claims MfaFresh: the set is exactly the plan's named operations
        var fresh = Routes().Where(r => HasPolicy(r, AuthDefaults.MfaFreshPolicy)).SelectMany(r => Methods(r).Select(m => (m.ToUpperInvariant(), Pattern(r)))).ToHashSet();
        Assert.Equal(MfaEnforcement.FreshOperations.Select(o => (o.Method, o.Pattern)).ToHashSet(), fresh);
    }

    [Fact]
    public void A_route_under_an_admin_prefix_must_be_classified_as_privileged_or_listed_as_a_candidate()
    {
        foreach (var e in Routes().Where(r => UnderPrefix(Pattern(r), "/api/v1/admin")))
        {
            var p = Pattern(e);
            Assert.True(MfaEnforcement.PrivilegedPrefixes.Concat(MfaEnforcement.UnclassifiedCandidatePrefixes).Any(x => UnderPrefix(p, x)),
                $"{p} is an admin route that is neither enforced nor listed as an MFA candidate; classify it in MfaEnforcement");
        }
        foreach (var candidate in MfaEnforcement.UnclassifiedCandidatePrefixes)
            Assert.Contains(Routes(), r => UnderPrefix(Pattern(r), candidate));                                                   // no stale candidate
    }

    [Fact]
    public async Task Every_protected_route_in_the_whole_table_is_gated_by_the_MfaVerified_requirement()
    {
        // S3b-1 (D-166): the plan says "every privileged endpoint requires MfaVerified/MfaFresh"; the strongest form is EVERY authenticated route, so a new route cannot skip it whatever prefix it uses.
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var offenders = new List<string>();
        var protectedRoutes = 0;
        foreach (var e in Routes())
        {
            var data = e.Metadata.GetOrderedMetadata<IAuthorizeData>();
            if (data.Count == 0 || e.Metadata.GetMetadata<IAllowAnonymous>() is not null) continue;
            protectedRoutes++;
            var gated = false;
            foreach (var d in data)
            {
                var policy = string.IsNullOrEmpty(d.Policy) ? await provider.GetDefaultPolicyAsync() : await provider.GetPolicyAsync(d.Policy);
                if (policy is not null && policy.Requirements.Any(r => r is MfaVerifiedRequirement)) gated = true;
            }
            if (!gated) offenders.Add($"{string.Join("/", Methods(e))} {Pattern(e)}");
        }
        Assert.True(protectedRoutes > 100, $"only {protectedRoutes} protected routes were found; the enumeration is broken");
        Assert.True(offenders.Count == 0, "protected routes without the MfaVerified requirement: " + string.Join(", ", offenders));
    }

    /// <summary>The only authenticated routes exempt from the selected company's MFA policy: a user must always be able to list and change company, change the password and step up (S3b-1b, D-167).</summary>
    private static readonly HashSet<string> CompanyPolicyExempt = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /api/v1/companies", "POST /api/v1/session/company", "DELETE /api/v1/session/company", "POST /api/v1/auth/change-password", "POST /api/v1/auth/mfa/step-up",
    };

    [Fact]
    public async Task Every_protected_route_is_gated_by_the_company_MFA_policy_except_the_named_exempt_routes()
    {
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var offenders = new List<string>();
        var exemptSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Routes())
        {
            var data = e.Metadata.GetOrderedMetadata<IAuthorizeData>();
            if (data.Count == 0 || e.Metadata.GetMetadata<IAllowAnonymous>() is not null) continue;
            var gated = false;
            foreach (var d in data)
            {
                var policy = string.IsNullOrEmpty(d.Policy) ? await provider.GetDefaultPolicyAsync() : await provider.GetPolicyAsync(d.Policy);
                if (policy is not null && policy.Requirements.Any(r => r is MfaCompanyPolicyRequirement)) gated = true;
            }
            foreach (var m in Methods(e))
            {
                var key = $"{m.ToUpperInvariant()} {Pattern(e)}";
                if (CompanyPolicyExempt.Contains(key)) { exemptSeen.Add(key); if (gated) offenders.Add(key + " (exempt route carries the company policy)"); }
                else if (!gated) offenders.Add(key);
            }
        }
        Assert.True(offenders.Count == 0, "company-policy gating mismatches: " + string.Join(", ", offenders));
        Assert.Equal(CompanyPolicyExempt.Count, exemptSeen.Count);                                                                    // no stale exemption
    }

    // ---- company policy handler

    private sealed class FakePolicies(bool requires) : ICompanyMfaPolicyEvaluator
    {
        public int Calls;
        public Task<bool> RequiresMfaAsync(Guid userId, Guid companyId, CancellationToken ct = default) { Calls++; return Task.FromResult(requires); }
    }

    private static AuthenticatedSession InCompany(MfaState state, bool proof, Guid? company) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "u", "U", false, SessionClient.Web, Now.AddMinutes(30), Now.AddHours(12), proof ? Now : null, state, company);

    private static async Task<(bool Succeeded, int Queries)> CompanyPolicy(AuthenticatedSession session, bool policyRequires)
    {
        var fake = new FakePolicies(policyRequires);
        var (ctx, _) = Context(new MfaCompanyPolicyRequirement(), session);
        await new MfaCompanyPolicyHandler(fake).HandleAsync(ctx);
        return (ctx.HasSucceeded, fake.Calls);
    }

    [Fact]
    public async Task The_company_policy_refuses_only_a_user_without_an_authenticator_in_a_company_that_requires_MFA()
    {
        var company = Guid.NewGuid();
        Assert.True((await CompanyPolicy(InCompany(MfaState.None, false, company), policyRequires: false)).Succeeded);   // pass-through when the company has no requirement
        Assert.False((await CompanyPolicy(InCompany(MfaState.None, false, company), policyRequires: true)).Succeeded);   // required and no authenticator
        Assert.True((await CompanyPolicy(InCompany(MfaState.Enrolled, true, company), policyRequires: true)).Succeeded); // carries the proof
        Assert.False((await CompanyPolicy(InCompany(MfaState.Enrolled, false, company), policyRequires: true)).Succeeded);   // defence in depth: enrolled but no proof
    }

    [Fact]
    public async Task No_selected_company_means_no_company_policy_and_a_session_with_the_proof_costs_no_query()
    {
        Assert.Equal((true, 0), await CompanyPolicy(InCompany(MfaState.None, false, null), policyRequires: true));
        Assert.Equal((true, 0), await CompanyPolicy(InCompany(MfaState.Enrolled, true, Guid.NewGuid()), policyRequires: true));
        Assert.Equal((false, 1), await CompanyPolicy(InCompany(MfaState.None, false, Guid.NewGuid()), policyRequires: true));
    }

    [Fact]
    public async Task A_company_policy_refusal_is_403_mfa_enrolment_required_and_never_step_up()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test"));
        var (status, body) = await Handle(user, [new MfaCompanyPolicyRequirement()]);
        Assert.Equal(403, status);
        Assert.Equal("mfa_enrolment_required", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    // ---- handlers

    private static (AuthorizationHandlerContext Context, HttpContext Http) Context(IAuthorizationRequirement requirement, AuthenticatedSession? session)
    {
        var http = new DefaultHttpContext();
        if (session is not null) http.Items[AuthDefaults.SessionItemKey] = session;
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test"));
        return (new AuthorizationHandlerContext([requirement], user, http), http);
    }

    private static AuthenticatedSession Session(MfaState state, TimeSpan? proofAge) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "u", "U", false, SessionClient.Web, Now.AddMinutes(30), Now.AddHours(12), proofAge is { } a ? Now - a : null, state);

    private static async Task<bool> Verified(AuthenticatedSession? s)
    {
        var (ctx, _) = Context(new MfaVerifiedRequirement(), s);
        await new MfaVerifiedHandler().HandleAsync(ctx);
        return ctx.HasSucceeded;
    }

    private static async Task<bool> Fresh(AuthenticatedSession? s, TimeSpan window)
    {
        var (ctx, _) = Context(new MfaFreshRequirement(), s);
        await new MfaFreshHandler(new AuthOptions { MfaFreshnessMinutes = (int)window.TotalMinutes }, new FixedClock(Now)).HandleAsync(ctx);
        return ctx.HasSucceeded;
    }

    [Fact]
    public async Task A_user_without_MFA_passes_both_policies_with_or_without_a_proof()
    {
        Assert.True(await Verified(Session(MfaState.None, null)));
        Assert.True(await Fresh(Session(MfaState.None, null), TimeSpan.FromMinutes(15)));
        Assert.True(await Fresh(Session(MfaState.None, TimeSpan.FromDays(3)), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task An_enrolled_user_needs_a_proof_and_the_fresh_policy_needs_it_within_the_window()
    {
        Assert.False(await Verified(Session(MfaState.Enrolled, null)));
        Assert.True(await Verified(Session(MfaState.Enrolled, TimeSpan.FromHours(5))));
        Assert.False(await Fresh(Session(MfaState.Enrolled, null), TimeSpan.FromMinutes(15)));
        Assert.True(await Fresh(Session(MfaState.Enrolled, TimeSpan.FromMinutes(14)), TimeSpan.FromMinutes(15)));
        Assert.False(await Fresh(Session(MfaState.Enrolled, TimeSpan.FromMinutes(16)), TimeSpan.FromMinutes(15)));
        Assert.False(await Fresh(Session(MfaState.EnrolmentRequired, TimeSpan.FromHours(1)), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task No_session_on_the_request_never_satisfies_either_requirement()
    {
        Assert.False(await Verified(null));
        Assert.False(await Fresh(null, TimeSpan.FromMinutes(15)));
    }

    // ---- the stable answer

    private async Task<(int Status, string Body)> Handle(ClaimsPrincipal user, IAuthorizationRequirement[] failed)
    {
        using var scope = factory.Services.CreateScope();
        var http = new DefaultHttpContext { User = user, RequestServices = scope.ServiceProvider };
        http.Response.Body = new MemoryStream();
        var nextCalled = false;
        var result = PolicyAuthorizationResult.Forbid(AuthorizationFailure.Failed(failed));
        await new MfaAuthorizationResultHandler().HandleAsync(_ => { nextCalled = true; return Task.CompletedTask; }, http, new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(), result);
        Assert.False(nextCalled);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task An_authenticated_caller_refused_only_for_MFA_gets_401_mfa_step_up_required()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test"));
        var (status, body) = await Handle(user, [new MfaFreshRequirement()]);
        Assert.Equal(401, status);
        Assert.Equal("mfa_step_up_required", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Other_refusals_keep_their_default_answer()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test"));
        var (status, body) = await Handle(user, [new MfaFreshRequirement(), new Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement("hasm:identity_admin", ["true"])]);
        Assert.Equal(403, status);                                                                                                    // a non-administrator is still a plain 403
        Assert.DoesNotContain("mfa_step_up_required", body);
        var (anonStatus, anonBody) = await Handle(new ClaimsPrincipal(new ClaimsIdentity()), [new MfaVerifiedRequirement()]);
        Assert.NotEqual(200, anonStatus);
        Assert.DoesNotContain("mfa_step_up_required", anonBody);                                                                      // an unauthenticated caller is never told about step-up
    }
}
