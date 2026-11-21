using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Identity;

public sealed class AuthConfigurationException(string message) : Exception(message);

/// <summary>Authentication settings, section "Hasm:Auth" (environment variables Hasm__Auth__*). All have safe defaults.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Hasm:Auth";

    public int SessionIdleMinutes { get; init; } = 30;
    public int SessionAbsoluteHours { get; init; } = 12;
    public int Pbkdf2Iterations { get; init; } = PasswordService.DefaultIterations;
    /// <summary>Failed attempts for one (login identifier, client address) pair within the window before that pair is blocked.
    /// Blocks only that pair, so a third party cannot lock a user out of sign-in from other addresses.</summary>
    public int MaxFailuresPerLoginSource { get; init; } = 5;
    /// <summary>Failed attempts for one login identifier across ALL addresses within the window before the identifier is blocked for
    /// every client (last-resort ceiling against distributed guessing; deliberately far above the per-pair limit).</summary>
    public int MaxFailuresPerLoginGlobal { get; init; } = 100;
    /// <summary>Failed attempts per source address within the window before it is blocked (credential stuffing).</summary>
    public int MaxFailuresPerSource { get; init; } = 20;
    public int FailureWindowMinutes { get; init; } = 15;
    public int BlockMinutes { get; init; } = 15;
    /// <summary>How long a second-factor proof (login or step-up) stays fresh for operations that require fresh MFA (D-103: default 15, bounded 5..60; D-160).</summary>
    public int MfaFreshnessMinutes { get; init; } = 15;

    public TimeSpan SessionIdle => TimeSpan.FromMinutes(SessionIdleMinutes);
    public TimeSpan SessionAbsolute => TimeSpan.FromHours(SessionAbsoluteHours);
    public TimeSpan FailureWindow => TimeSpan.FromMinutes(FailureWindowMinutes);
    public TimeSpan Block => TimeSpan.FromMinutes(BlockMinutes);
    public TimeSpan MfaFreshness => TimeSpan.FromMinutes(MfaFreshnessMinutes);

    public static AuthOptions FromConfiguration(IConfiguration configuration)
    {
        var s = configuration.GetSection(SectionName);
        var o = new AuthOptions
        {
            SessionIdleMinutes = Int(s, nameof(SessionIdleMinutes), 30),
            SessionAbsoluteHours = Int(s, nameof(SessionAbsoluteHours), 12),
            Pbkdf2Iterations = Int(s, nameof(Pbkdf2Iterations), PasswordService.DefaultIterations),
            MaxFailuresPerLoginSource = Int(s, nameof(MaxFailuresPerLoginSource), 5),
            MaxFailuresPerLoginGlobal = Int(s, nameof(MaxFailuresPerLoginGlobal), 100),
            MaxFailuresPerSource = Int(s, nameof(MaxFailuresPerSource), 20),
            FailureWindowMinutes = Int(s, nameof(FailureWindowMinutes), 15),
            BlockMinutes = Int(s, nameof(BlockMinutes), 15),
            MfaFreshnessMinutes = Int(s, nameof(MfaFreshnessMinutes), 15),
        };
        o.EnsureValid();
        return o;
    }

    private static int Int(IConfigurationSection section, string key, int fallback)
    {
        var text = section[key];
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        return int.TryParse(text, out var v)
            ? v
            : throw new AuthConfigurationException($"Invalid authentication configuration: {SectionName}:{key} must be an integer.");
    }

    public void EnsureValid()
    {
        var errors = new List<string>();
        void Range(string name, int v, int min, int max) { if (v < min || v > max) errors.Add($"{SectionName}:{name} must be between {min} and {max}."); }
        Range(nameof(SessionIdleMinutes), SessionIdleMinutes, 1, 24 * 60);
        Range(nameof(SessionAbsoluteHours), SessionAbsoluteHours, 1, 24 * 30);
        Range(nameof(Pbkdf2Iterations), Pbkdf2Iterations, 100_000, 10_000_000);   // floor protects against weak configuration
        Range(nameof(MaxFailuresPerLoginSource), MaxFailuresPerLoginSource, 3, 100);
        Range(nameof(MaxFailuresPerLoginGlobal), MaxFailuresPerLoginGlobal, 10, 100_000);
        if (MaxFailuresPerLoginGlobal < MaxFailuresPerLoginSource * 4)
            errors.Add($"{SectionName}:{nameof(MaxFailuresPerLoginGlobal)} must be at least four times {nameof(MaxFailuresPerLoginSource)}.");
        Range(nameof(MaxFailuresPerSource), MaxFailuresPerSource, 5, 10_000);
        Range(nameof(FailureWindowMinutes), FailureWindowMinutes, 1, 24 * 60);
        Range(nameof(BlockMinutes), BlockMinutes, 1, 24 * 60);
        Range(nameof(MfaFreshnessMinutes), MfaFreshnessMinutes, 5, 60);
        if (errors.Count > 0) throw new AuthConfigurationException("Invalid authentication configuration: " + string.Join(" ", errors));
    }
}
