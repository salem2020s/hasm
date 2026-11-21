using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Hasm.Api.Auth;

/// <summary>The session carries a second-factor proof, unless MFA is not required of its user (state None passes: D-161). Phase 4.1 S3a, D-160.</summary>
public sealed class MfaVerifiedRequirement : IAuthorizationRequirement { }

/// <summary>The session's second-factor proof is within the freshness window (<see cref="AuthOptions.MfaFreshness"/>), unless MFA is not required of its user (D-161).</summary>
public sealed class MfaFreshRequirement : IAuthorizationRequirement { }

/// <summary>The selected company's MFA policy is satisfied for this session (S3b-1b, D-167): a session whose user is subject to the policy must carry MFA; a user without an authenticator never does, so the answer is 403 <c>mfa_enrolment_required</c>.</summary>
public sealed class MfaCompanyPolicyRequirement : IAuthorizationRequirement { }

/// <summary>Both handlers read the session the authentication handler validated against the database on THIS request (never a client-supplied value).</summary>
public sealed class MfaVerifiedHandler : AuthorizationHandler<MfaVerifiedRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaVerifiedRequirement requirement)
    {
        if (context.Resource is HttpContext http && http.Items[AuthDefaults.SessionItemKey] is AuthenticatedSession s
            && MfaFreshness.RequireVerified(s.MfaState, s.MfaVerifiedAtUtc) == MfaRequirementOutcome.Satisfied)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class MfaCompanyPolicyHandler(ICompanyMfaPolicyEvaluator policies) : AuthorizationHandler<MfaCompanyPolicyRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaCompanyPolicyRequirement requirement)
    {
        if (context.Resource is not HttpContext http || http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession s) return;
        if (s.MfaState != MfaState.None && s.MfaVerifiedAtUtc is not null) { context.Succeed(requirement); return; }  // carries the proof: satisfied whatever the policy says, so no query
        if (s.SelectedCompanyId is not { } company) { context.Succeed(requirement); return; }                         // no company selected: no company policy applies
        if (!await policies.RequiresMfaAsync(s.UserId, company, http.RequestAborted)) { context.Succeed(requirement); return; }
        // required: the session must carry the proof (a user with an authenticator always does, S2; a user without one cannot)
        if (s.MfaState != MfaState.None && s.MfaVerifiedAtUtc is not null) context.Succeed(requirement);
    }
}

public sealed class MfaFreshHandler(AuthOptions options, TimeProvider clock) : AuthorizationHandler<MfaFreshRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaFreshRequirement requirement)
    {
        if (context.Resource is HttpContext http && http.Items[AuthDefaults.SessionItemKey] is AuthenticatedSession s
            && MfaFreshness.RequireFresh(s.MfaState, s.MfaVerifiedAtUtc, clock.GetUtcNow(), options.MfaFreshness) == MfaRequirementOutcome.Satisfied)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>An authenticated caller refused only because of an MFA requirement gets the stable answer 401 <c>mfa_step_up_required</c> (the client then calls the step-up endpoint); everything else keeps the default behaviour.</summary>
public sealed class MfaAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Succeeded && context.User.Identity?.IsAuthenticated == true && result.AuthorizationFailure is { } companyFailure
            && companyFailure.FailedRequirements.Any(r => r is MfaCompanyPolicyRequirement)
            && companyFailure.FailedRequirements.All(r => r is MfaCompanyPolicyRequirement or MfaVerifiedRequirement or MfaFreshRequirement))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;                                              // the company requires MFA of this user and the user has no authenticator
            return context.Response.WriteAsJsonAsync(new { error = "mfa_enrolment_required" });
        }
        if (!result.Succeeded && context.User.Identity?.IsAuthenticated == true
            && result.AuthorizationFailure is { } failure
            && failure.FailedRequirements.Any() && failure.FailedRequirements.All(r => r is MfaVerifiedRequirement or MfaFreshRequirement))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(new { error = "mfa_step_up_required" });
        }
        return fallback.HandleAsync(next, context, policy, result);
    }
}

/// <summary>
/// The explicit privileged endpoint set of Phase 4.1 S3a (D-162), taken ONLY from the approved plan (section 3 "Enforcement points" and "Step-up"): the access administration
/// and user administration groups carry <see cref="AuthDefaults.MfaVerifiedPolicy"/>; the named operations (role grant, state change of an identity administrator) carry
/// <see cref="AuthDefaults.MfaFreshPolicy"/>. The candidates below are NOT enforced here (the plan names the group but this slice does not invent its member routes); they are listed
/// so an endpoint-table test fails when a new route appears under an admin prefix without being classified.
/// </summary>
public static class MfaEnforcement
{
    /// <summary>Every route under these prefixes must carry the MfaVerified policy.</summary>
    public static readonly string[] PrivilegedPrefixes = ["/api/v1/access", "/api/v1/admin/users", "/api/v1/admin/parties"];

    /// <summary>Every one of these operations (method, route pattern as mapped) must carry the MfaFresh policy.</summary>
    public static readonly (string Method, string Pattern)[] FreshOperations =
    [
        ("POST", "/api/v1/access/assignments"),
        ("POST", "/api/v1/admin/users/{id:guid}/state"),
        ("PUT", "/api/v1/access/mfa-policy"),                                                        // S3b-1b: changing a company's MFA policy
    ];

    /// <summary>Admin-style prefixes that are candidates for a later slice (decided with S4's routing of privilege-granting paths); documented, not enforced.</summary>
    public static readonly string[] UnclassifiedCandidatePrefixes = [];
}
