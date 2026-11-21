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
