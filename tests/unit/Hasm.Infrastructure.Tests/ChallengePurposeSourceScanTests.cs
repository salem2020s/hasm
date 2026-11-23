using System.Text.RegularExpressions;
using Hasm.Infrastructure.Identity;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Identity integrity S-2d (plan section 5.1): a challenge opens a flow ONLY through a key-backed gate (Login: the TOTP code of the user; Enrol: the one-time code of the verified user row). The purpose RecoveryRequest has NO
/// caller, and a new purpose or a new caller must not appear silently. The scan covers EVERY source file, the challenge store included, and counts every call whatever its arguments (review L1 of PR 237): each call that
/// begins an attempt or issues a challenge must name a literal Login or Enrol, nothing may cast to or parse the purpose enum, and the enum has exactly its three members. A future caller must name its key-backed gate in
/// its own plan and then change this test deliberately. The scanner itself is proven on negative controls.
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

    /// <summary>The calls found in one text and the violations among them (a call with a non-literal purpose, a cast, a parse, a purpose other than Login or Enrol).</summary>
    internal static (int Calls, List<string> Violations) Scan(string text)
    {
        var violations = new List<string>(); var calls = 0;
        foreach (Match m in Regex.Matches(text, @"(\.TryBeginAttemptAsync|Challenges\.IssueAsync)\(([^;]*?)\);"))
        {
            calls++;
            if (!Regex.IsMatch(m.Value, @"MfaChallengePurpose\.(Login|Enrol)\b")) violations.Add("call without a literal Login or Enrol purpose: " + m.Value.Trim());
        }
        foreach (Match m in Regex.Matches(text, @"MfaChallengePurpose\.(\w+)"))
            if (m.Groups[1].Value is not ("Login" or "Enrol")) violations.Add("names MfaChallengePurpose." + m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"\(\s*MfaChallengePurpose\s*\)|Enum\.(Try)?Parse(<\s*MfaChallengePurpose\s*>|\s*\(\s*typeof\s*\(\s*MfaChallengePurpose\s*\))|Enum\.ToObject\s*\(\s*typeof\s*\(\s*MfaChallengePurpose"))
            violations.Add("casts or parses the purpose: " + m.Value);
        return (calls, violations);
    }

    [Fact]
    public void The_purpose_enum_has_exactly_the_three_known_members()
    {
        Assert.Equal(["Login", "Enrol", "RecoveryRequest"], Enum.GetNames<MfaChallengePurpose>());
    }

    [Fact]
    public void Every_call_in_the_source_names_login_or_enrol_literally_and_nothing_casts_or_parses_the_purpose()
    {
        var calls = 0;
        foreach (var (file, text) in Sources().Where(s => !s.File.EndsWith("ChallengePurposeSourceScanTests.cs", StringComparison.Ordinal)))
        {
            var (n, violations) = Scan(text);
            calls += n;
            Assert.True(violations.Count == 0, $"{Path.GetFileName(file)}: {string.Join(" | ", violations)}");
        }
        Assert.Equal(5, calls);                                                                                      // 2 issue sites and 3 attempt sites today (AuthService): a new site must be added here deliberately
    }

    [Theory]
    [InlineData("var c = await mfa.Challenges.IssueAsync(user.Id, purposeVariable, client, source, ct);")]                          // a variable purpose: the call is counted AND flagged (the first version skipped it)
    [InlineData("var a = await mfa.Challenges.TryBeginAttemptAsync(token, (MfaChallengePurpose)2, client, source, ct);")]           // a cast to RecoveryRequest
    [InlineData("var a = await store.TryBeginAttemptAsync(token, MfaChallengePurpose.RecoveryRequest, client, source);")]           // a purpose without a gate
    [InlineData("var p = Enum.Parse<MfaChallengePurpose>(request.Purpose);")]                                                       // from input
    [InlineData("var p = (MfaChallengePurpose)Enum.ToObject(typeof(MfaChallengePurpose), 2);")]
    public void The_scanner_flags_what_a_silent_new_caller_would_do(string source)
    {
        Assert.NotEmpty(Scan(source).Violations);
    }

    [Theory]
    [InlineData("var c = await mfa.Challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, client, source, ct);", 1)]
    [InlineData("var a = await mfa.Challenges.TryBeginAttemptAsync(challengeToken, MfaChallengePurpose.Enrol, client, source, ct);", 1)]
    public void The_scanner_accepts_the_two_reviewed_callers(string source, int calls)
    {
        var (n, violations) = Scan(source);
        Assert.Equal(calls, n);
        Assert.Empty(violations);
    }
}
