using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 3.14 slice S1, no database needed: the validation and masking rules, the derivation and shape of the migration (company 0035, provisional: the number follows the merge order), and the guard that the cash module stays
/// clear of the ledger and the numbering (D-214: financial posting for cash and bank is closed until the independent accounting and security reviews).
/// </summary>
public class CashAccountUnitTests
{
    private const string Number = "0035";   // a renumbering changes this constant, the generator constant and regenerates the files

    private static readonly Guid B = Guid.NewGuid(), A = Guid.NewGuid();
    private static CashAccountInput In(string? kind = "bank", string? code = "nb-1", string? ar = "البنك", string? en = null, string? bank = "National", string? branch = "Down", string? number = "SA03 8000 0000 6080 1016 7519") =>
        new(kind, code, ar, en, B, A, bank, branch, number);

    [Fact]
    public void A_valid_bank_input_is_normalized_the_code_upper_cased_and_the_number_stripped_of_separators()
    {
        Assert.True(CashAccountRules.TryNormalize(In(), null, null, out var d, out var n, out var errors), string.Join(",", errors));
        Assert.Equal(("NB-1", CashAccountKind.Bank, "SA0380000000608010167519"), (d.Code, d.Kind, n));
        Assert.Equal("7519", CashAccountRules.Tail(n!));
        Assert.Equal("****7519", CashAccountRules.Mask("7519"));
    }

    [Theory]
    [InlineData("12")] [InlineData("ab")] [InlineData("12345!67")] [InlineData("****7519")] [InlineData("••••7519")] [InlineData("1234567890123456789012345678901234567")]
    public void A_number_that_is_not_a_plausible_account_number_or_that_looks_masked_is_refused_and_never_stored(string number)
    {
        Assert.False(CashAccountRules.TryNormalize(In(number: number), null, null, out _, out var n, out var errors));
        Assert.Null(n);
        Assert.Contains(errors, e => e.Field == "accountNumber");
    }

    [Theory]
    [InlineData(4, "", "****")] [InlineData(5, "", "****")] [InlineData(7, "", "****")]
    [InlineData(8, "78", "****78")] [InlineData(9, "89", "****89")] [InlineData(11, "01", "****01")]
    [InlineData(12, "9012", "****9012")] [InlineData(13, "0123", "****0123")] [InlineData(34, "6789", "****6789")]
    public void The_mask_reveals_no_more_than_a_fixed_safe_share_of_the_number(int length, string tail, string masked)
    {
        var number = string.Concat(Enumerable.Range(0, length).Select(i => (char)('0' + (i + 1) % 10)));            // 1234567890123...
        Assert.Equal(length, CashAccountRules.NormalizeNumber(number)!.Length);                                   // every length from 4 to 34 is still ACCEPTED (the rule changes the mask, not the accepted length)
        var expectedTail = length >= 12 ? number[^4..] : length >= 8 ? number[^2..] : "";
        Assert.Equal(expectedTail, CashAccountRules.Tail(number));
        Assert.Equal(tail.Length, CashAccountRules.Tail(number).Length);
        Assert.Equal(masked.Length - 4, CashAccountRules.Mask(CashAccountRules.Tail(number)).Length - 4);
        Assert.True(CashAccountRules.Mask(CashAccountRules.Tail(number)).Length - 4 <= Math.Max(0, length / 3));  // never more than a third of the number is shown
    }

    [Fact]
    public void A_short_number_is_not_exposed_by_its_mask_in_any_form_a_reader_can_see()
    {
        foreach (var n in new[] { "1234", "12345", "1234567" })
        {
            Assert.True(CashAccountRules.TryNormalize(In(number: n), null, null, out _, out var number, out _));
            var mask = CashAccountRules.Mask(CashAccountRules.Tail(number!));
            Assert.Equal("****", mask);
            Assert.DoesNotContain(n[^1].ToString(), mask);
        }
        Assert.Equal("****", CashAccountRules.Mask(""));
    }

    [Theory]
    [InlineData("1234567\u0131")] [InlineData("ABCD\u017F1234")] [InlineData("\u0661\u0662\u0663\u0664\u0665")] [InlineData("12345678\u00E9")] [InlineData("ABCDEFGH\uFF21")]
    public void A_non_ASCII_character_is_refused_before_upper_casing_so_the_dotless_i_and_the_long_s_never_become_ASCII_letters(string number)
    {
        Assert.Null(CashAccountRules.NormalizeNumber(number));
        Assert.False(CashAccountRules.TryNormalize(In(number: number), null, null, out _, out var n, out var errors));
        Assert.Null(n);
        Assert.Contains(errors, e => e is { Field: "accountNumber", Code: "invalid" });
        Assert.Equal("ABCD1234", CashAccountRules.NormalizeNumber("abcd 12-34"));                                  // plain ASCII is unchanged
    }

    [Fact]
    public void A_non_ASCII_code_is_refused_for_the_same_reason()
    {
        Assert.False(CashAccountRules.TryNormalize(In(code: "NB\u0131"), null, null, out _, out _, out var errors));
        Assert.Contains(errors, e => e is { Field: "code", Code: "invalid" });
        Assert.True(CashAccountRules.TryNormalize(In(code: "nb-1"), null, null, out var d, out _, out _));
        Assert.Equal("NB-1", d.Code);
    }

    [Fact]
    public void The_masked_form_of_the_current_number_means_unchanged_and_an_omitted_number_too_on_an_update()
    {
        var existing = new CashAccountData(CashAccountKind.Bank, "NB-1", "x", null, B, A, "National", "Down");
        Assert.True(CashAccountRules.TryNormalize(In(number: "****7519"), existing, "****7519", out _, out var n1, out _));
        Assert.Null(n1);
        Assert.True(CashAccountRules.TryNormalize(In(number: null), existing, "****7519", out _, out var n2, out _));
        Assert.Null(n2);
        Assert.False(CashAccountRules.TryNormalize(In(number: "****0000"), existing, "****7519", out _, out _, out var e));
        Assert.Contains(e, x => x is { Field: "accountNumber", Code: "masked_value" });
        Assert.True(CashAccountRules.TryNormalize(In(number: "GB29 NWBK 6016 1331 9268 19"), existing, "****7519", out _, out var n3, out _));
        Assert.Equal("GB29NWBK60161331926819", n3);
    }

    [Fact]
    public void The_kind_and_the_code_are_immutable_and_a_cashbox_carries_no_bank_details()
    {
        var existing = new CashAccountData(CashAccountKind.Cashbox, "C1", "x", null, B, A, null, null);
        Assert.False(CashAccountRules.TryNormalize(In(kind: "bank", code: "C1", bank: null, branch: null, number: null), existing, null, out _, out _, out var kindErrors));
        Assert.Contains(kindErrors, e => e is { Field: "kind", Code: "immutable" });
        Assert.False(CashAccountRules.TryNormalize(In(kind: "cashbox", code: "C2", bank: null, branch: null, number: null), existing, null, out _, out _, out var codeErrors));
        Assert.Contains(codeErrors, e => e is { Field: "code", Code: "immutable" });
        Assert.False(CashAccountRules.TryNormalize(In(kind: "cashbox", code: "C1", bank: "B", branch: null, number: null), existing, null, out _, out _, out var bankErrors));
        Assert.Contains(bankErrors, e => e.Code == "not_allowed_for_cashbox");
        Assert.True(CashAccountRules.TryNormalize(In(kind: null, code: null, bank: null, branch: null, number: null), existing, null, out var d, out _, out _));   // omitted kind and code mean "as stored"
        Assert.Equal(("C1", CashAccountKind.Cashbox), (d.Code, d.Kind));
    }

    [Fact]
    public void Names_are_required_in_at_least_one_language_and_limited_in_length()
    {
        Assert.False(CashAccountRules.TryNormalize(In(ar: null, en: " "), null, null, out _, out _, out var e1));
        Assert.Contains(e1, e => e is { Field: "nameAr", Code: "required" });
        Assert.False(CashAccountRules.TryNormalize(In(en: new string('x', 201)), null, null, out _, out _, out var e2));
        Assert.Contains(e2, e => e is { Field: "nameEn", Code: "too_long" });
        Assert.True(CashAccountRules.TryNormalize(In(ar: null, en: "Bank"), null, null, out _, out _, out _));
        Assert.False(CashAccountRules.TryNormalize(null, null, null, out _, out _, out _));
    }

    [Fact]
    public void The_audit_codes_are_the_allocated_block_and_the_permissions_are_classified()
    {
        short[] codes = [CashAuditEvents.AccountCreated, CashAuditEvents.AccountUpdated, CashAuditEvents.AccountDeactivated, CashAuditEvents.AccountReactivated, CashAuditEvents.BankDetailsChanged, CashAuditEvents.AccountNumberRevealed, CashAuditEvents.NumberRewrapped, CashAuditEvents.NumberRewrapFailed];
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, c => Assert.InRange(c, (short)2100, (short)2199));
        Assert.True(PermissionCatalog.Default.TryGet("company.cashaccounts.view", out var view));
        Assert.Equal(ScopeKind.Branch, view.Resource.Scope);
        Assert.True(PermissionCatalog.Default.TryGet("company.cashaccountssensitive.view", out var sensitive));
        Assert.Equal(ScopeKind.Branch, sensitive.Resource.Scope);
        Assert.True(PrivilegedAccess.IsPrivilegedPermission("company.cashaccountssensitive.view"));
        Assert.True(PrivilegedAccess.IsPrivilegedPermission("company.cashaccounts.update"));
        Assert.True(PrivilegedAccess.IsPrivilegedPermission("company.cashaccounts.activate"));
        Assert.False(PrivilegedAccess.IsPrivilegedPermission("company.cashaccounts.view"));
        Assert.False(PrivilegedAccess.IsPrivilegedPermission("company.cashaccounts.create"));
        Assert.True(PrivilegedAccess.PrivilegedResources.Contains("company.cashaccountssensitive"));
    }

    // ---- the migration

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"hasm.company.{Number}.cash-bank.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] == "CashAccount");

    [Fact]
    public void Every_cash_account_name_is_the_deterministic_derivation_of_its_logical_token_and_collides_with_no_other_name()
    {
        var entries = OwnEntries().ToList();
        Assert.True(entries.Count > 25);
        Assert.Equal(1, entries.Count(e => !e.Key.Contains('.')));
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
        var names = entries.Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] != "CashAccount").Select(kv => kv.Value).ToHashSet();
        Assert.Empty(names.Where(others.Contains));
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.Empty(names.Where(system.Contains));
    }

    [Fact]
    public void The_migration_is_additive_creates_one_table_with_its_guards_seeds_nothing_and_never_touches_the_ledger()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == int.Parse(Number)).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bALTER\s+TABLE\b", sql);
        Assert.DoesNotMatch(@"\b(INSERT\s+INTO|DELETE\s+FROM)\s+t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));
        Assert.Single(Regex.Matches(sql, @"\bCREATE TABLE\b"));
        var table = CompanySchema.Map["CashAccount"];
        Assert.Contains($"REVOKE ALL ON TABLE {table} FROM PUBLIC", sql);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.Map["CashAccount.TrRow"]} BEFORE INSERT OR UPDATE OR DELETE ON {table}", sql);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.Map["CashAccount.TrTruncate"]} BEFORE TRUNCATE ON {table}", sql);
        Assert.Contains($"CREATE UNIQUE INDEX {CompanySchema.Map["CashAccount.UqAccount"]}", sql);                    // one general-ledger account serves one cash or bank account
        Assert.Contains($"CREATE UNIQUE INDEX {CompanySchema.Map["CashAccount.UqCode"]}", sql);
        foreach (var ledger in new[] { CompanySchema.JournalEntry.Table, CompanySchema.NumberIssue.Table }) Assert.DoesNotContain(ledger, sql);   // no reference to a journal or a numbering table
        Assert.DoesNotMatch(@"\b(numeric|decimal|money|float)\b", sql);                                               // no balance is stored
        Assert.Contains("bytea", sql);
    }

    // ---- the closed posting gate (D-214)

    [Fact]
    public void The_cash_account_code_references_neither_the_journal_service_nor_the_numbering_service()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "backend", "Hasm.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var files = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "backend", "Hasm.Infrastructure", "Cash"), "*.cs")
            .Append(Path.Combine(dir.FullName, "src", "backend", "Hasm.Api", "Company", "CashAccountEndpoints.cs")).ToList();
        Assert.True(files.Count >= 4);
        var forbidden = new Regex(@"\b(JournalService|JournalManagementService|NumberingService|NumberingManagementService|FiscalPeriodGate|DocumentSource|CreateInWorkAsync|PostAsync|ReverseAsync|DocumentTypes)\b");
        var offenders = files.SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, n: i + 1)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && !x.line.TrimStart().StartsWith("///") && forbidden.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.n}").ToList();
        Assert.Empty(offenders);
    }
}
