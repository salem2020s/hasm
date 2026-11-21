namespace Hasm.Infrastructure.Identity;

/// <summary>
/// The hash domains of EVERY throttle key (D-121). A throttle key is <c>SHA-256(domain + content)</c>; the domains are prefix-free (none is a prefix of another, a unit test pins it), so two keys of different domains can
/// never be the same string and therefore never the same hash input, whatever the content (including attacker-chosen login text). The second-factor counters (<c>mfa-pair:</c>, <c>mfa-user:</c>) share the login throttle
/// table (scope <see cref="ThrottleScope.Login"/>; migrations may not DROP, so its scope check is not widened) but can never collide with, count toward or reset a login, source or change-password counter.
/// </summary>
public static class ThrottleKeyDomains
{
    public const string LoginIdentifier = "login:";
    public const string LoginPair = "pair:";
    public const string LoginSource = "source:";
    public const string ChangePassword = "chpw:";
    public const string MfaPair = "mfa-pair:";
    public const string MfaUser = "mfa-user:";

    public static IReadOnlyList<string> All { get; } = [LoginIdentifier, LoginPair, LoginSource, ChangePassword, MfaPair, MfaUser];
}
