using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hasm.Api.Auth;

public static class AuthDefaults
{
    public const string Scheme = "HasmSession";
    /// <summary>__Host- prefix: browsers only accept it with Secure, Path=/ and no Domain attribute.</summary>
    public const string CookieName = "__Host-hasm_session";
    /// <summary>Required on every state-changing API request (CSRF defence: forces a CORS preflight cross-origin).</summary>
    public const string RequestHeader = "X-Hasm-Request";
    public const string AdministratorPolicy = "IdentityAdministrator";
    /// <summary>Phase 4.1 S3a (D-160): the session carries a second-factor proof (pass-through for users without MFA, D-161).</summary>
    public const string MfaVerifiedPolicy = "MfaVerified";
    /// <summary>Phase 4.1 S3a (D-160): the proof is within the freshness window (pass-through for users without MFA, D-161).</summary>
    public const string MfaFreshPolicy = "MfaFresh";
    public const string AdministratorClaim = "hasm:identity_admin";
    public const string SessionIdClaim = "hasm:session";
    public const string SessionItemKey = "hasm.session";
}

/// <summary>
/// Reads the opaque session token from <c>Authorization: Bearer</c> (Desktop) or the HttpOnly session cookie (Web)
/// and validates it against the server-side session table on every request. The database is only touched when a
/// token is actually presented.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IServiceProvider services)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken(Request);
        if (token is null) return AuthenticateResult.NoResult();

        var session = await services.GetRequiredService<AuthService>().AuthenticateAsync(token, Context.RequestAborted);
        if (session is null) return AuthenticateResult.NoResult();

        Context.Items[AuthDefaults.SessionItemKey] = session;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new Claim(ClaimTypes.Name, session.Login),
            new Claim(AuthDefaults.SessionIdClaim, session.SessionId.ToString()),
            .. session.IsIdentityAdministrator ? [new Claim(AuthDefaults.AdministratorClaim, "true")] : Array.Empty<Claim>(),
        ], AuthDefaults.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthDefaults.Scheme));
    }

    private static string? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header["Bearer ".Length..].Trim();
        return request.Cookies[AuthDefaults.CookieName];
    }
}

public static class AuthenticationSetup
{
    /// <summary>Stands in when no key ring is registered: every MFA operation that needs a secret fails closed, and users without MFA are not affected (the singleton graph still builds).</summary>
    private sealed class NoKeyRing : KekRing
    {
        public override KekHandle GetActive() => throw new EncryptionUnavailableException("encryption_key_unavailable", "No encryption key ring is configured.");
        public override KekHandle? Find(string keyId) => null;
    }

    public static IServiceCollection AddHasmAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = AuthOptions.FromConfiguration(configuration);
        services.AddSingleton(auth);
        services.AddSingleton(new PasswordService(auth.Pbkdf2Iterations));
        services.AddSingleton(TimeProvider.System);
        // The runtime (least-privilege) database account is used; the connection string is built lazily so the API
        // starts, and anonymous endpoints work, without a database. Never the provisioning administrator account.
        services.AddSingleton(sp => new IdentityStore(
            () =>
            {
                var options = HasmDatabaseOptions.FromConfiguration(configuration);
                options.EnsureValid(requireAdmin: false);
                return options.BuildSystemConnectionString(admin: false);
            },
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AuditTrail>()));
        // Phase 4.1: the second factor. The secrets are protected by the existing key ring (KekRing, fail closed when it is not configured); every component uses the same runtime connection as the identity store.
        string SystemConnection() { var options = HasmDatabaseOptions.FromConfiguration(configuration); options.EnsureValid(requireAdmin: false); return options.BuildSystemConnectionString(admin: false); }
        services.AddSingleton(sp => new MfaStore(SystemConnection, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AuditTrail>(), new TotpSecretProtector(sp.GetService<KekRing>() ?? new NoKeyRing())));
        services.AddSingleton(sp => new MfaChallengeStore(SystemConnection, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMfaAttemptLimiter>(sp => new MfaAttemptLimiter(sp.GetRequiredService<IdentityStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MfaVerifier(sp.GetRequiredService<MfaStore>(), sp.GetRequiredService<IMfaAttemptLimiter>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MfaLoginServices(sp.GetRequiredService<MfaChallengeStore>(), sp.GetRequiredService<MfaVerifier>(), sp.GetRequiredService<MfaStore>(), sp.GetRequiredService<IMfaAttemptLimiter>()));
        services.AddSingleton<AuthService>();
        services.AddHostedService<IdentityHousekeepingService>();

        services.AddAuthentication(AuthDefaults.Scheme).AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(AuthDefaults.Scheme, null);
        services.AddSingleton<IAuthorizationHandler, MfaVerifiedHandler>();
        services.AddSingleton<IAuthorizationHandler, MfaFreshHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, MfaAuthorizationResultHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthDefaults.AdministratorPolicy, p =>
                p.AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().RequireClaim(AuthDefaults.AdministratorClaim, "true"))
            .AddPolicy(AuthDefaults.MfaVerifiedPolicy, p =>
                p.AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().AddRequirements(new MfaVerifiedRequirement()))
            .AddPolicy(AuthDefaults.MfaFreshPolicy, p =>
                p.AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().AddRequirements(new MfaVerifiedRequirement(), new MfaFreshRequirement()));
        return services;
    }

    /// <summary>Hardening headers for API responses; auth/admin responses are never cached.</summary>
    public static void UseHasmResponseHardening(this WebApplication app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            if (new[] { "/api/v1/auth", "/api/v1/admin", "/api/v1/access", "/api/v1/session", "/api/v1/companies", "/api/v1/company" }.Any(p => context.Request.Path.StartsWithSegments(p)))
                headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        await next();
    });

    /// <summary>State-changing API calls must carry the custom request header (see <see cref="AuthDefaults.RequestHeader"/>).</summary>
    public static void UseHasmCsrfProtection(this WebApplication app) => app.Use(async (context, next) =>
    {
        var method = context.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        if (!safe && context.Request.Path.StartsWithSegments("/api") && !context.Request.Headers.ContainsKey(AuthDefaults.RequestHeader))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "missing_request_header" });
            return;
        }
        await next();
    });

    private static (int, string) Company(HttpContext context, CompanyDatabaseException ex)
    {
        // Only the short code is logged (never names, credentials or SQL); the client sees one generic code.
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hasm.Company").LogError("company.unavailable code={Code}", ex.Code);
        return (StatusCodes.Status503ServiceUnavailable, "company_unavailable");
    }

    private static (int, string) Audit(HttpContext context, AuditUnavailableException ex)
    {
        // Only the short code is logged (never keys, paths or content); the client sees one generic code.
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hasm.Audit").LogError("audit.unavailable code={Code}", ex.Code);
        return (StatusCodes.Status503ServiceUnavailable, "audit_unavailable");
    }

    private static (int, string) Encryption(HttpContext context, EncryptionUnavailableException ex)
    {
        // Only the short code is logged (never keys, paths or content); the client sees one generic code. No plaintext fallback exists: the operation simply does not happen.
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hasm.Encryption").LogError("encryption.unavailable code={Code}", ex.Code);
        return (StatusCodes.Status503ServiceUnavailable, "encryption_unavailable");
    }

    /// <summary>Generic error responses: no exception text, stack trace, connection detail or secret is ever returned.</summary>
    public static void UseHasmExceptionHandling(this WebApplication app) => app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, code) = error switch
        {
            BadHttpRequestException b => (b.StatusCode, "bad_request"),
            AuditUnavailableException a => Audit(context, a),
            EncryptionUnavailableException e => Encryption(context, e),
            CompanyDatabaseException c => Company(context, c),
            _ when Hasm.Api.Hosting.HasmExceptionMapping.IsInvalidCharacter(error) => (StatusCodes.Status400BadRequest, "invalid_request"),   // defence in depth: a character the database cannot store (22021) is the caller's data, not an outage
            DatabaseConfigurationException or AuthConfigurationException or NpgsqlException => (StatusCodes.Status503ServiceUnavailable, "service_unavailable"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error"),
        };
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = code });
    }));
}

/// <summary>Hourly removal of long-expired sessions and stale throttle rows. Failures never log more than the error type.</summary>
public sealed class IdentityHousekeepingService(IdentityStore store, ILogger<IdentityHousekeepingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await store.PurgeAsync(TimeSpan.FromDays(7), TimeSpan.FromDays(1), stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Identity housekeeping failed ({ErrorType}).", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
