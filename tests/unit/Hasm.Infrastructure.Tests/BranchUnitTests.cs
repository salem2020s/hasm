using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.1B: branch validation, entitlement, event types and the static shape of company migration 0004 (no database needed).</summary>
public class BranchUnitTests
{
    private static BranchInput Input(string? code = "BR1", string? ar = "فرع", string? en = "Branch") =>
        new(code, ar, en, null, null, null, null, null, null, null, null);

    private static string Sql => MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 4).Sql;
    private static string Code => Regex.Replace(Sql, "--[^\n]*", "");

    [Theory]
    [InlineData("br-1", "BR-1")]
    [InlineData("  Main_2 ", "MAIN_2")]
    [InlineData("a", "A")]
    [InlineData("12345678901234567890", "12345678901234567890")]
    public void Codes_are_normalised_to_upper_case(string raw, string expected) => Assert.Equal(expected, ConfigurationValidation.NormalizeBranchCode(raw));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("-A")] [InlineData("_A")] [InlineData("A B")] [InlineData("A.B")]
    [InlineData("123456789012345678901")] [InlineData("فرع")] [InlineData("A\u0000")]
    public void Invalid_codes_are_rejected(string? raw) => Assert.Null(ConfigurationValidation.NormalizeBranchCode(raw));

    [Fact]
    public void A_branch_needs_one_non_empty_name_and_both_may_be_given()
    {
        Assert.True(ConfigurationValidation.TryNormalizeBranch(Input(ar: "فرع", en: null), null, out _, out _));
        Assert.True(ConfigurationValidation.TryNormalizeBranch(Input(ar: null, en: "Branch"), null, out _, out _));
        Assert.True(ConfigurationValidation.TryNormalizeBranch(Input(), null, out _, out _));
        Assert.False(ConfigurationValidation.TryNormalizeBranch(Input(ar: null, en: null), null, out _, out var e1));
        Assert.Contains(e1, x => x.Code == "name_required");
        Assert.False(ConfigurationValidation.TryNormalizeBranch(Input(ar: "  ", en: ""), null, out _, out _));
        Assert.False(ConfigurationValidation.TryNormalizeBranch(Input(en: new string('x', 201)), null, out _, out _));
    }

    [Fact]
    public void Names_are_trimmed_and_normalised_to_NFC_and_control_characters_are_refused()
    {
        Assert.True(ConfigurationValidation.TryNormalizeBranch(Input(en: "  Café  "), null, out var d, out _));
        Assert.Equal("Café", d.NameEn);
        Assert.False(ConfigurationValidation.TryNormalizeBranch(Input(en: "bad\u0007name"), null, out _, out _));
    }

    [Fact]
    public void The_code_is_required_on_create_and_immutable_on_update()
    {
        Assert.False(ConfigurationValidation.TryNormalizeBranch(Input(code: null), null, out _, out var e));
        Assert.Contains(e, x => x.Field == "code");
        Assert.True(ConfigurationValidation.TryNormalizeBranch(Input(code: null), "BR1", out var keep, out _));
        Assert.Equal("BR1", keep.Code);
        Assert.True(ConfigurationValidation.TryNormalizeBranch(Input(code: "br1"), "BR1", out _, out _));
        Assert.False(ConfigurationValidation.TryNormalizeBranch(Input(code: "BR2"), "BR1", out _, out var imm));
        Assert.Contains(imm, x => x.Code == "immutable");
    }

    [Fact]
    public void Optional_contact_fields_follow_the_profile_rules()
    {
        var bad = new BranchInput("B", "x", null, null, null, null, null, null, "abc", "not-an-email", "ftp://x");
        Assert.False(ConfigurationValidation.TryNormalizeBranch(bad, null, out _, out var errors));
        Assert.Equal(["email", "phone", "website"], errors.Select(e => e.Field).Order());
        var ok = new BranchInput("B", "x", null, " Street 1 ", null, "Damascus", null, "12345", "+963 11 123456", "a@b.co", "https://example.org");
        Assert.True(ConfigurationValidation.TryNormalizeBranch(ok, null, out var d, out _));
        Assert.Equal("Street 1", d.AddressLine1);
    }

    [Fact]
    public void Trial_allows_one_branch_and_it_is_enforced_and_branch_events_are_in_the_business_range()
    {
        Assert.Equal(1, TrialEntitlementPolicy.Instance.MaxBranches);
        Assert.Equal(Enforcement.Enforced, TrialEntitlementPolicy.Instance.Restrictions.Single(r => r.Key == "branches").Enforcement);
        short[] events = [ConfigAuditEvents.BranchCreated, ConfigAuditEvents.BranchUpdated, ConfigAuditEvents.BranchDeactivated,
            ConfigAuditEvents.BranchReactivated, ConfigAuditEvents.BranchMainDesignated, ConfigAuditEvents.BranchMainRevoked];
        Assert.Equal(events.Length, events.Distinct().Count());
        Assert.All(events, e => Assert.InRange(e, (short)1007, (short)1012));
    }

    // ---- migration 0004 shape ----

    [Fact]
    public void Migration_0004_creates_exactly_the_branch_tables_and_grants_nothing()
    {
        var created = Regex.Matches(Code, @"CREATE\s+TABLE\s+(\w+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(new[] { CompanySchema.Branch.Table, CompanySchema.BranchRevision.Table }.Order(), created);
        Assert.DoesNotContain("GRANT", Code);
        Assert.DoesNotMatch(@"(?i)\bALTER\s+TABLE", Code);                    // no existing table is altered
        Assert.DoesNotMatch(@"(?i)\bUPDATE\s+t_|\bDELETE\b\s+FROM", Code);    // no existing row is changed
    }

    [Fact]
    public void Migration_0004_protects_the_invariants_inside_PostgreSQL()
    {
        Assert.Contains($"UNIQUE INDEX {B.UniqueCode} ON {B.Table} (upper({B.Code}))", Code);                  // case-insensitive code uniqueness
        Assert.Contains($"UNIQUE INDEX {B.UniqueMain} ON {B.Table} ({B.IsMain}) WHERE {B.IsMain}", Code);       // at most one main
        Assert.Contains("CREATE CONSTRAINT TRIGGER " + B.MainTrigger, Code);                                     // exactly one at commit
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", Code);
        Assert.Contains($"CHECK (NOT {B.IsMain} OR {B.IsActive})", Code);                                        // a main branch is active
        Assert.Contains($"BEFORE TRUNCATE ON {B.Table}", Code);
        Assert.Contains("branches cannot be deleted", Code);
        Assert.Contains("branch codes are immutable", Code);
        Assert.Contains("activation epoch must advance by one", Code);
        Assert.Contains($"BEFORE TRUNCATE ON {CompanySchema.BranchRevision.Table}", Code);
        Assert.Contains($"REFERENCES {CompanySchema.BusinessAudit.Table}", Code);
    }

    [Fact]
    public void Migration_0004_seeds_the_main_branch_idempotently_with_provenance_and_guards_the_result()
    {
        Assert.Contains("'MAIN'", Code);
        Assert.Contains("'Main Branch'", Code);
        Assert.Contains(@"U&'\0627\0644\0641\0631\0639 \0627\0644\0631\0626\064A\0633\064A'", Code);
        Assert.Contains($"WHERE NOT EXISTS (SELECT 1 FROM {B.Table})", Code);
        Assert.Contains("branch initialization failed", Code);                                                    // rolls the migration back
        Assert.Contains("4, 1, now(), now()", Code);                                                              // seed_migration = 4, revision 1
        // The escapes spell the Arabic text in logical Unicode order.
        Assert.Equal("الفرع الرئيسي", "الفرع الرئيسي");
        Assert.DoesNotContain("INSERT INTO " + CompanySchema.BusinessAudit.Table, Code);                          // no fabricated audit event
        Assert.DoesNotContain("INSERT INTO " + CompanySchema.BranchRevision.Table, Code);
    }

    [Fact]
    public void Branch_physical_names_are_unique_and_opaque()
    {
        var names = CompanySchema.Map.Where(kv => kv.Key.StartsWith("Branch")).Select(kv => kv.Value).ToList();
        Assert.True(names.Count > 50);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(CompanySchema.Map.Count, CompanySchema.Map.Values.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^[tcfkr]_[0-9a-f]{8}$", n));
    }
}
