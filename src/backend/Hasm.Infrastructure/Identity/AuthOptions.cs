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
    /// <summary>Failed attempts per login identifier (existing or not) within the window before it is blocked.</summary>
    public int MaxFailuresPerLogin { get; init; } = 5;
    /// <summary>Failed attempts per source address within the window before it is blocked (credential stuffing).</summary>
    public int MaxFailuresPerSource { get; init; } = 20;
    public int FailureWindowMinutes { get; init; } = 15;
    public int BlockMinutes { get; init; } = 15;

    public TimeSpan SessionIdle => TimeSpan.FromMinutes(SessionIdleMinutes);
    public TimeSpan SessionAbsolute => TimeSpan.FromHours(SessionAbsoluteHours);
    public TimeSpan FailureWindow => TimeSpan.FromMinutes(FailureWindowMinutes);
    public TimeSpan Block => TimeSpan.FromMinutes(BlockMinutes);

    public static AuthOptions FromConfiguration(IConfiguration configuration)
    {
        var s = configuration.GetSection(SectionName);
        var o = new AuthOptions
        {
            SessionIdleMinutes = Int(s, nameof(SessionIdleMinutes), 30),
            SessionAbsoluteHours = Int(s, nameof(SessionAbsoluteHours), 12),
            Pbkdf2Iterations = Int(s, nameof(Pbkdf2Iterations), PasswordService.DefaultIterations),
            MaxFailuresPerLogin = Int(s, nameof(MaxFailuresPerLogin), 5),
            MaxFailuresPerSource = Int(s, nameof(MaxFailuresPerSource), 20),
            FailureWindowMinutes = Int(s, nameof(FailureWindowMinutes), 15),
            BlockMinutes = Int(s, nameof(BlockMinutes), 15),
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
        Range(nameof(MaxFailuresPerLogin), MaxFailuresPerLogin, 3, 100);
        Range(nameof(MaxFailuresPerSource), MaxFailuresPerSource, 5, 10_000);
        Range(nameof(FailureWindowMinutes), FailureWindowMinutes, 1, 24 * 60);
        Range(nameof(BlockMinutes), BlockMinutes, 1, 24 * 60);
        if (errors.Count > 0) throw new AuthConfigurationException("Invalid authentication configuration: " + string.Join(" ", errors));
    }
}
