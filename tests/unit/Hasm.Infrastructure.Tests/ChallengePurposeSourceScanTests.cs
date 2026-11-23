using System.Text.RegularExpressions;
using Hasm.Infrastructure.Identity;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Identity integrity S-2d (plan section 5.1): a challenge opens a flow ONLY through a key-backed gate (Login: the TOTP code of the user; Enrol: the one-time code of the verified user row). The purpose RecoveryRequest has NO
/// caller, and a new purpose or a new caller must not appear silently: this scan fails when any source file other than the challenge store itself names a purpose that is not Login or Enrol, when a new purpose is added to the
/// enum, or when a call site begins an attempt for a purpose outside that set. A future caller must name its key-backed gate in its own plan and then change this test deliberately.
/// </summary>
public class ChallengePurposeSourceScanTests
{
    private static string BackendRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "backend");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/backend not found");
    }

    private static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(BackendRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)));

    [Fact]
    public void The_purpose_enum_has_exactly_the_three_known_members_and_only_login_and_enrol_are_referenced_outside_the_store()
    {
        Assert.Equal(["Login", "Enrol", "RecoveryRequest"], Enum.GetNames<MfaChallengePurpose>());
        foreach (var (file, text) in Sources().Where(s => !s.File.EndsWith("MfaChallengeStore.cs", StringComparison.Ordinal)))
            foreach (Match m in Regex.Matches(text, @"MfaChallengePurpose\.(\w+)"))
                Assert.True(m.Groups[1].Value is "Login" or "Enrol", $"{Path.GetFileName(file)} names MfaChallengePurpose.{m.Groups[1].Value}: a purpose without a reviewed key-backed gate");
    }

    [Fact]
    public void Every_call_that_begins_an_attempt_or_issues_a_challenge_names_login_or_enrol_literally()
    {
        var calls = 0;
        foreach (var (file, text) in Sources().Where(s => !s.File.EndsWith("MfaChallengeStore.cs", StringComparison.Ordinal)))
            foreach (Match m in Regex.Matches(text, @"\.(TryBeginAttemptAsync|IssueAsync)\(([^;]*?)\);"))
            {
                if (!m.Value.Contains("MfaChallengePurpose", StringComparison.Ordinal)) continue;                    // not a challenge call (another IssueAsync)
                calls++;
                Assert.Matches(@"MfaChallengePurpose\.(Login|Enrol)\b", m.Value);
            }
        Assert.Equal(5, calls);                                                                                      // 2 issue sites and 3 attempt sites today (AuthService): a new site must be added here deliberately
    }
}
