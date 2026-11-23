using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
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
    /// <summary>S3b-1b: an authenticated session with the second-factor proof where MFA applies, WITHOUT the selected company's policy: the routes a user must always reach to leave or change company or to enrol (company list and selection, change password, step-up).</summary>
    public const string AuthenticatedPolicy = "AuthenticatedMfaVerified";
    /// <summary>The fresh proof WITHOUT the selected company's policy: the fresh operations of identity administration, which is not company-scoped (review L2 of PR 181).</summary>
    public const string AuthenticatedFreshPolicy = "AuthenticatedMfaFresh";
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
        services.AddSingleton(sp => new IdentityKeys(sp.GetService<KekRing>() ?? new NoKeyRing(), EncryptionOptions.FromConfiguration(configuration).IdentityPreviousKeyIds));
        services.AddSingleton(sp => new IdentityStore(
            () =>
            {
                var options = HasmDatabaseOptions.FromConfiguration(configuration);
                options.EnsureValid(requireAdmin: false);
                return options.BuildSystemConnectionString(admin: false);
            },
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AuditTrail>(),
            sp.GetRequiredService<IdentityKeys>()));
        // Phase 4.1: the second factor. The secrets are protected by the existing key ring (KekRing, fail closed when it is not configured); every component uses the same runtime connection as the identity store.
        string SystemConnection() { var options = HasmDatabaseOptions.FromConfiguration(configuration); options.EnsureValid(requireAdmin: false); return options.BuildSystemConnectionString(admin: false); }
        // owner decision D-235: the runtime account must be least-privilege for the TOTP secret store. The gate refuses otherwise (the API does not start; the MFA store never opens a
        // connection), unless the explicit opt-out for disposable single-account test environments is set, which is logged as critical and never silent.
        services.AddSingleton(sp =>
        {
            bool allow;
            try { allow = HasmDatabaseOptions.FromConfiguration(configuration).AllowOverprivilegedSystemRuntime; } catch (DatabaseConfigurationException) { allow = false; }
            return new SystemRuntimeGate(allow, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SystemRuntimeGate>>());
        });
        services.AddSingleton(sp => new MfaStore(SystemConnection, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AuditTrail>(), new TotpSecretProtector(sp.GetService<KekRing>() ?? new NoKeyRing()), sp.GetRequiredService<SystemRuntimeGate>(), sp.GetRequiredService<IdentityKeys>()));
        services.AddHostedService(sp => new SystemRuntimeGateService(SystemConnection, sp.GetRequiredService<SystemRuntimeGate>(), sp.GetRequiredService<ILogger<SystemRuntimeGateService>>()));
        services.AddSingleton(sp => new MfaChallengeStore(SystemConnection, sp.GetRequiredService<TimeProvider>(), null, sp.GetRequiredService<IdentityKeys>(), id => sp.GetRequiredService<IdentityStore>().AuditRejectedChallengeAsync(id)));
        services.AddSingleton<IMfaAttemptLimiter>(sp => new MfaAttemptLimiter(sp.GetRequiredService<IdentityStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MfaVerifier(sp.GetRequiredService<MfaStore>(), sp.GetRequiredService<IMfaAttemptLimiter>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MfaLoginServices(sp.GetRequiredService<MfaChallengeStore>(), sp.GetRequiredService<MfaVerifier>(), sp.GetRequiredService<MfaStore>(), sp.GetRequiredService<IMfaAttemptLimiter>()));
        services.AddSingleton<AuthService>();
        services.AddHostedService<IdentityHousekeepingService>();

        services.AddAuthentication(AuthDefaults.Scheme).AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(AuthDefaults.Scheme, null);
        services.AddSingleton<IAuthorizationHandler, MfaVerifiedHandler>();
        services.AddSingleton<IAuthorizationHandler, MfaFreshHandler>();
        services.AddSingleton<IAuthorizationHandler, MfaCompanyPolicyHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, MfaAuthorizationResultHandler>();
        services.AddAuthorizationBuilder()
            // the default policy of every plain RequireAuthorization(): an authenticated session that carries the second-factor proof where MFA applies (S3b-1, D-166; pass-through for state None, D-161)
            .SetDefaultPolicy(new AuthorizationPolicyBuilder().AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().AddRequirements(new MfaVerifiedRequirement(), new MfaCompanyPolicyRequirement()).Build())
            .AddPolicy(AuthDefaults.AdministratorPolicy, p =>
                p.AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().RequireClaim(AuthDefaults.AdministratorClaim, "true"))
            .AddPolicy(AuthDefaults.AuthenticatedPolicy, p =>
                p.AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().AddRequirements(new MfaVerifiedRequirement()))
            .AddPolicy(AuthDefaults.AuthenticatedFreshPolicy, p =>
                p.AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().AddRequirements(new MfaVerifiedRequirement(), new MfaFreshRequirement()))
            .AddPolicy(AuthDefaults.MfaVerifiedPolicy, p =>
                p.AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().AddRequirements(new MfaVerifiedRequirement(), new MfaCompanyPolicyRequirement()))
            .AddPolicy(AuthDefaults.MfaFreshPolicy, p =>
                p.AddAuthenticationSchemes(AuthDefaults.Scheme).RequireAuthenticatedUser().AddRequirements(new MfaVerifiedRequirement(), new MfaFreshRequirement(), new MfaCompanyPolicyRequirement()));
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
            _ when Hasm.Api.Hosting.HasmExceptionMapping.IsInvalidCharacter(error) => (StatusCodes.Status400BadRequest, "invalid_request"),   // defence in depth: a character the database cannot store (22021) is the caller's data, not an outage
            AuditUnavailableException a => Audit(context, a),
            EncryptionUnavailableException e => Encryption(context, e),
            CompanyDatabaseException c => Company(context, c),
            DatabaseConfigurationException or AuthConfigurationException or NpgsqlException or SystemRuntimePrivilegeException => (StatusCodes.Status503ServiceUnavailable, "service_unavailable"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error"),
        };
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = code });
    }));
}

/// <summary>
/// Start-up half of the least-privilege gate (owner decision D-235): when the system database is reachable and the runtime account or the TOTP secret store violates least privilege, the API
/// does NOT start. When the database is not configured or not reachable the API starts as before (anonymous endpoints work without a database); the MFA store then applies the same gate on its
/// first connection, so the secret store is never used unchecked.
/// </summary>
public sealed class SystemRuntimeGateService(Func<string> connectionStringFactory, SystemRuntimeGate gate, ILogger<SystemRuntimeGateService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionStringFactory()) { Pooling = false, Timeout = 5 }.ConnectionString);
            await conn.OpenAsync(limit.Token);
            await gate.EnsureAsync(conn, limit.Token);
        }
        catch (SystemRuntimePrivilegeException) { throw; }                                   // refuse to start; the gate has logged the codes
        catch (Exception ex) when (ex is DatabaseConfigurationException or NpgsqlException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            logger.LogWarning("The least-privilege check of the system database runtime account could not run at start-up ({ErrorType}); the MFA store applies it on first use.", ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Hourly removal of long-expired sessions and stale throttle rows. Failures never log more than the error type.</summary>
public sealed class IdentityHousekeepingService(IdentityStore store, MfaChallengeStore challenges, TimeProvider clock, ILogger<IdentityHousekeepingService> logger) : BackgroundService
{
    /// <summary>One housekeeping pass: sessions and throttle rows, then expired second-factor login challenges (the DELETE of the runtime account on that table exists for this and nothing else). Each step fails on its own; only the error type is logged.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try { await store.PurgeAsync(TimeSpan.FromDays(7), TimeSpan.FromDays(1), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Identity housekeeping failed ({ErrorType}).", ex.GetType().Name); }
        try { await challenges.PurgeAsync(clock.GetUtcNow() - TimeSpan.FromDays(1), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("MFA challenge housekeeping failed ({ErrorType}).", ex.GetType().Name); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
