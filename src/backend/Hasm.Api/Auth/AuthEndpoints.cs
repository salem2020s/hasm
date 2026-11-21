using Hasm.Api.Hosting;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Auth;

public sealed record LoginRequest(string? Login, string? Password, string? Client);
public sealed record MfaVerifyRequest(string? Challenge, string? Code, string? Client);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record CreateUserRequest(string? Login, string? DisplayName, string? Password, bool IsIdentityAdministrator);
public sealed record SetStateRequest(string? State);

public static class AuthEndpoints
{
    private const long MaxBodyBytes = 8 * 1024;
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => MaxBodyBytes; }

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/login", Login).WithMetadata(new BodyLimit());
        auth.MapPost("/mfa/verify", MfaVerify).WithMetadata(new BodyLimit());
        auth.MapPost("/logout", Logout);
        auth.MapGet("/status", Status);
        auth.MapPost("/change-password", ChangePassword).RequireAuthorization().WithMetadata(new BodyLimit());

        var admin = app.MapGroup("/api/v1/admin/users").RequireAuthorization(AuthDefaults.AdministratorPolicy);
        admin.MapPost("", CreateUser).WithMetadata(new BodyLimit());
        admin.MapPost("/{id:guid}/state", SetState).WithMetadata(new BodyLimit());
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    private static object UserView(string id, string login, string displayName, bool admin) =>
        new { id, login, displayName, isIdentityAdministrator = admin };

    private static SessionClient? ParseClient(string? client) => (client ?? "web").ToLowerInvariant() switch
    {
        "web" => SessionClient.Web,
        "desktop" => SessionClient.Desktop,
        _ => null,
    };

    /// <summary>The same answers for the password step and the second step; the shared frontend reads <c>status</c> (absent on a completed login).</summary>
    private static IResult LoginResult(LoginOutcome outcome, SessionClient client, HttpContext http)
    {
        switch (outcome)
        {
            case LoginOutcome.Throttled t:
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(t.RetryAfter.TotalSeconds)).ToString();
                return Error(429, "too_many_attempts");
            case LoginOutcome.MfaRequired m:
                return Results.Json(new { status = "mfa_required", challenge = m.Challenge, challengeExpiresAtUtc = m.ChallengeExpiresAtUtc });
            case LoginOutcome.MfaEnrolmentRequired e:
                return Results.Json(new { status = "mfa_enrolment_required", challenge = e.Challenge, challengeExpiresAtUtc = e.ChallengeExpiresAtUtc });
            case LoginOutcome.MfaRecoveryRequired:
                return Results.Json(new { status = "mfa_recovery_required" });
            case LoginOutcome.Success s:
                if (client == SessionClient.Web)
                {
                    // Web: the token only ever exists in an HttpOnly cookie; it is not placed in the response body.
                    http.Response.Cookies.Append(AuthDefaults.CookieName, s.Token, new CookieOptions
                    {
                        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true,
                    });
                }
                return Results.Json(new
                {
                    user = UserView(s.User.Id.ToString(), s.User.Login, s.User.DisplayName, s.User.IsIdentityAdministrator),
                    idleExpiresAtUtc = s.IdleExpiresAtUtc,
                    absoluteExpiresAtUtc = s.AbsoluteExpiresAtUtc,
                    token = client == SessionClient.Desktop ? s.Token : null,
                });
            default:
                return Error(401, "invalid_credentials");
        }
    }

    private static async Task<IResult> Login(LoginRequest? body, HttpContext http, AuthService auth)
    {
        if (body is null || string.IsNullOrEmpty(body.Login) || string.IsNullOrEmpty(body.Password)) return Error(400, "invalid_request");
        if (ParseClient(body.Client) is not { } client) return Error(400, "invalid_request");
        return LoginResult(await auth.LoginAsync(body.Login, body.Password, client, http.ClientSource(), http.RequestAborted), client, http);
    }

    /// <summary>Completes a login that needs the second factor. Identical failure for every refusal; the client and the source must match those the challenge was issued for.</summary>
    private static async Task<IResult> MfaVerify(MfaVerifyRequest? body, HttpContext http, AuthService auth)
    {
        if (body is null || string.IsNullOrEmpty(body.Challenge) || string.IsNullOrEmpty(body.Code)) return Error(400, "invalid_request");
        if (ParseClient(body.Client) is not { } client) return Error(400, "invalid_request");
        return LoginResult(await auth.CompleteMfaLoginAsync(body.Challenge, body.Code, client, http.ClientSource(), http.RequestAborted), client, http);
    }

    private static async Task<IResult> Logout(HttpContext http, AuthService auth)
    {
        // Authentication middleware has already validated the token and stored the session.
        if (http.Items[AuthDefaults.SessionItemKey] is AuthenticatedSession session)
            await auth.LogoutAsync(session.SessionId, http.RequestAborted);
        http.Response.Cookies.Delete(AuthDefaults.CookieName, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
        return Results.NoContent();
    }

    private static IResult Status(HttpContext http)
    {
        if (http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession s) return Results.Json(new { authenticated = false });
        return Results.Json(new
        {
            authenticated = true,
            user = UserView(s.UserId.ToString(), s.Login, s.DisplayName, s.IsIdentityAdministrator),
            session = new { client = s.Client.ToString().ToLowerInvariant(), idleExpiresAtUtc = s.IdleExpiresAtUtc, absoluteExpiresAtUtc = s.AbsoluteExpiresAtUtc },
        });
    }

    private static async Task<IResult> ChangePassword(ChangePasswordRequest? body, HttpContext http, AuthService auth)
    {
        if (body is null || http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession session) return Error(400, "invalid_request");
        var result = await auth.ChangePasswordAsync(session, body.CurrentPassword, body.NewPassword, http.RequestAborted);
        return result.Status switch
        {
            ChangePasswordStatus.Changed => Results.NoContent(),
            ChangePasswordStatus.InvalidCurrentPassword => Error(400, "invalid_current_password"),
            ChangePasswordStatus.Throttled => Error(429, "too_many_attempts"),
            _ => Results.Json(new { error = "weak_password", problems = result.PasswordProblems }, statusCode: 400),
        };
    }

    private static async Task<IResult> CreateUser(CreateUserRequest? body, AuthService auth, HttpContext http)
    {
        if (body is null) return Error(400, "invalid_request");
        var result = await auth.CreateUserAsync(body.Login, body.DisplayName, body.Password, body.IsIdentityAdministrator, http.RequestAborted,
            http.Items[AuthDefaults.SessionItemKey] is AuthenticatedSession actor ? actor.UserId : null);
        return result.Status switch
        {
            CreateUserStatus.Created => Results.Json(
                new { id = result.User!.Id, login = result.User.Login, displayName = result.User.DisplayName, state = "Active", isIdentityAdministrator = result.User.IsIdentityAdministrator },
                statusCode: 201),
            CreateUserStatus.LoginTaken => Error(409, "login_taken"),
            CreateUserStatus.InvalidLogin => Error(400, "invalid_login"),
            CreateUserStatus.InvalidDisplayName => Error(400, "invalid_display_name"),
            _ => Results.Json(new { error = "weak_password", problems = result.PasswordProblems }, statusCode: 400),
        };
    }

    private static async Task<IResult> SetState(Guid id, SetStateRequest? body, HttpContext http, AuthService auth)
    {
        var state = body?.State?.ToLowerInvariant() switch
        {
            "active" => (UserState?)UserState.Active,
            "disabled" => UserState.Disabled,
            "locked" => UserState.Locked,
            _ => null,
        };
        if (state is null || http.Items[AuthDefaults.SessionItemKey] is not AuthenticatedSession actor) return Error(400, "invalid_request");
        return await auth.SetUserStateAsync(actor.UserId, id, state.Value, http.RequestAborted) switch
        {
            AuthService.SetStateStatus.Changed => Results.NoContent(),
            AuthService.SetStateStatus.OwnAccount => Error(409, "cannot_change_own_state"),
            AuthService.SetStateStatus.LastCompanyAdministrator => Error(409, "last_company_administrator"),
            AuthService.SetStateStatus.LastIdentityAdministrator => Error(409, "last_identity_administrator"),
            AuthService.SetStateStatus.ActorNotActive => Error(403, "forbidden"),
            _ => Error(404, "not_found"),
        };
    }
}
