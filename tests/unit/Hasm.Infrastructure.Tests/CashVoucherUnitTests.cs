using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 3.14 slice S2a, no database needed: the rules of a voucher body, the permission codes and their classification, the runtime grants, the derivation and shape of company migration 0040, and the guards that the slice has
/// NO approval, numbering, journal or reversal surface and stays clear of the ledger (D-214: financial posting for cash and bank is closed until the independent accounting and security reviews).
/// </summary>
public class CashVoucherUnitTests
{
    private const string Number = "0040";   // a renumbering changes this constant, the generator constant and regenerates the files
    private static readonly string[] Entities = ["CashVoucher", "CashVoucherLine", "CashVoucherEvent", "CashVoucherAccountGuard"];

    [Theory]
    [InlineData("receipt", CashVoucherKind.Receipt)]
    [InlineData(" Payment ", CashVoucherKind.Payment)]
    public void The_two_kinds_of_the_plan_are_parsed(string raw, CashVoucherKind expected) => Assert.Equal(expected, CashVoucherRules.ParseKind(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("transfer")]
    [InlineData("1")]
    public void Anything_else_is_not_a_kind(string? raw) => Assert.Null(CashVoucherRules.ParseKind(raw));

    [Fact]
    public void An_amount_is_positive_within_the_journal_range_and_has_no_more_decimals_than_the_base_currency()
    {
        Assert.Null(CashVoucherRules.AmountError(0.001m, 3));
        Assert.Null(CashVoucherRules.AmountError(CashVoucherRules.MaxAmount, 2));
        Assert.Equal("required", CashVoucherRules.AmountError(null, 2));
        Assert.Equal("out_of_range", CashVoucherRules.AmountError(0m, 2));
        Assert.Equal("out_of_range", CashVoucherRules.AmountError(-1m, 2));
        Assert.Equal("out_of_range", CashVoucherRules.AmountError(CashVoucherRules.MaxAmount + 0.01m, 2));
        Assert.Equal("too_many_decimals", CashVoucherRules.AmountError(1.001m, 2));
        Assert.Equal("too_many_decimals", CashVoucherRules.AmountError(1.5m, 0));
        Assert.Equal(499, CashVoucherRules.MaxLines);                                    // one less than a journal entry admits: the cash line of the later entry
        Assert.Equal(Hasm.Infrastructure.Ledger.JournalValidation.MaxLines - 1, CashVoucherRules.MaxLines);
    }

    [Fact]
    public void A_text_is_trimmed_empty_is_null_and_a_long_or_control_character_text_is_refused()
    {
        Assert.True(CashVoucherRules.TryText("  ref 1  ", 100, out var t)); Assert.Equal("ref 1", t);
        Assert.True(CashVoucherRules.TryText("   ", 100, out t)); Assert.Null(t);
        Assert.True(CashVoucherRules.TryText(null, 100, out t)); Assert.Null(t);
        Assert.False(CashVoucherRules.TryText(new string('x', 101), 100, out _));
        Assert.False(CashVoucherRules.TryText("a\nb", 100, out _));
    }

    // ---- permissions, classification, audit

    [Fact]
    public void The_resource_carries_exactly_the_actions_that_have_a_route_and_no_approve_or_reverse_code_exists()
    {
        var codes = PermissionCatalog.Default.All.Where(p => p.Resource.Key == "company.cashvouchers").Select(p => p.Code).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["company.cashvouchers.cancel", "company.cashvouchers.create", "company.cashvouchers.submit", "company.cashvouchers.update", "company.cashvouchers.view"], codes);
        Assert.All(PermissionCatalog.Default.All.Where(p => p.Resource.Key == "company.cashvouchers"), p => Assert.Equal(ScopeKind.Branch, p.Resource.Scope));
        foreach (var later in new[] { "approve", "reverse", "post", "reject" }) Assert.False(PermissionCatalog.Default.TryGet("company.cashvouchers." + later, out _), later);
        Assert.False(PermissionCatalog.Default.TryGet("company.cashtransfers.view", out _));   // transfers are slice S3
        Assert.All(codes, code => Assert.DoesNotContain(PresetRoles.Definitions, d => d.Value.Permissions.Contains(code)));   // in no preset role (no system migration in this slice)
    }

    [Fact]
    public void View_create_submit_and_cancel_are_ordinary_and_the_unclassified_update_is_privileged()
    {
        Assert.Contains("company.cashvouchers", PrivilegedAccess.OrdinaryResources);
        Assert.DoesNotContain("company.cashvouchers", PrivilegedAccess.PrivilegedResources);
        foreach (var ordinary in new[] { "view", "create", "submit", "cancel" }) Assert.False(PrivilegedAccess.IsPrivilegedPermission("company.cashvouchers." + ordinary), ordinary);
        Assert.True(PrivilegedAccess.IsPrivilegedPermission("company.cashvouchers.update"));     // not classified by the plan: privileged (D-096, when in doubt)
        Assert.True(PrivilegedAccess.IsPrivilegedPermission("company.cashvouchers.approve"));    // a code that does not exist is privileged (fail closed)
        Assert.False(PrivilegedAccess.IsPrivilegedRole(null, ["company.cashvouchers.view", "company.cashvouchers.create", "company.cashvouchers.submit", "company.cashvouchers.cancel"]));
        Assert.True(PrivilegedAccess.IsPrivilegedRole(null, ["company.cashvouchers.view", "company.cashvouchers.update"]));
    }

    [Fact]
    public void The_audit_codes_are_inside_the_allocated_block_and_apart_from_the_account_master()
    {
        short[] codes = [CashVoucherAuditEvents.Created, CashVoucherAuditEvents.Updated, CashVoucherAuditEvents.Submitted, CashVoucherAuditEvents.Withdrawn, CashVoucherAuditEvents.Cancelled];
        Assert.Equal(new short[] { 2110, 2111, 2112, 2113, 2114 }, codes);
        var all = typeof(CashVoucherAuditEvents).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (short)f.GetRawConstantValue()!).ToArray();
        Assert.Equal(codes.Order(), all.Order());                                                // no approved, rejected, journal-created or reversed event exists in this slice
    }

    // ---- no approval, numbering, journal or reversal surface (D-214)

    [Fact]
    public void The_services_expose_exactly_the_operations_of_the_slice()
    {
        string[] expected = ["CancelAsync", "CreateAsync", "GetAsync", "ListAsync", "SubmitAsync", "UpdateAsync", "WithdrawAsync"];
        foreach (var type in new[] { typeof(CashVoucherService), typeof(CashVoucherManagementService) })
            Assert.Equal(expected, type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => m.Name).Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["Cancelled", "Draft", "Submitted"], Enum.GetNames<CashVoucherStatus>().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["Cancelled", "Created", "Submitted", "Updated", "Withdrawn"], Enum.GetNames<CashVoucherEventKind>().Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(typeof(CashVoucherView).GetProperties(), p => Regex.IsMatch(p.Name, "Number|Approv|Journal|Entry|Posted|Revers"));
    }

    private static DirectoryInfo Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "backend", "Hasm.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void The_voucher_code_references_neither_the_journal_nor_the_numbering_nor_the_fiscal_gate_and_maps_no_later_route()
    {
        var root = Root().FullName;
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "backend", "Hasm.Infrastructure", "Cash"), "CashVoucher*.cs").Append(Path.Combine(root, "src", "backend", "Hasm.Api", "Company", "CashVoucherEndpoints.cs")).ToList();
        Assert.Equal(4, files.Count);
        var forbidden = new Regex(@"\b(JournalService|JournalManagementService|NumberingService|NumberingManagementService|FiscalPeriodGate|DocumentSource|CreateInWorkAsync|PostAsync|ReverseAsync|ApproveAsync|RejectAsync|DocumentTypes|JournalEntry|JournalLine|NumberIssue|NumberCounter|NumberingRule)\b");
        var code = files.SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, n: i + 1))).Where(x => !x.line.TrimStart().StartsWith("//")).ToList();
        Assert.Empty(code.Where(x => forbidden.IsMatch(x.line)).Select(x => $"{Path.GetFileName(x.f)}:{x.n}"));
        var routes = code.Where(x => x.f.EndsWith("CashVoucherEndpoints.cs", StringComparison.Ordinal)).SelectMany(x => Regex.Matches(x.line, @"g\.Map(Get|Post|Put|Patch|Delete)\(""([^""]*)""").Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")).ToArray();
        Assert.Equal(["Get ", "Post ", "Get /{id:guid}", "Put /{id:guid}", "Post /{id:guid}/submit", "Post /{id:guid}/withdraw", "Post /{id:guid}/cancel"], routes);
    }

    // ---- runtime grants

    [Fact]
    public void The_runtime_role_updates_only_the_voucher_row_and_never_deletes_anything()
    {
        string[] Of(string table) => [.. CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges.Order(StringComparer.Ordinal)];
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], Of(CompanySchema.CashVoucher.Table));
        Assert.Equal(["INSERT", "SELECT"], Of(CompanySchema.CashVoucherLine.Table));             // line sets are append-only: an edit writes a new set
        Assert.Equal(["INSERT", "SELECT"], Of(CompanySchema.CashVoucherEvent.Table));
    }

    // ---- the migration

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"hasm.company.{Number}.cash-vouchers.v1:" + token)))[..8].ToLowerInvariant();
    }

    [Fact]
    public void Every_cash_voucher_name_is_the_deterministic_derivation_of_its_logical_token_and_collides_with_no_other_name()
    {
        var entries = CompanySchema.Map.Where(kv => Entities.Contains(kv.Key.Split('.')[0])).ToList();
        Assert.True(entries.Count > 60);
        Assert.Equal(3, entries.Count(e => !e.Key.Contains('.')));
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
        var names = entries.Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => !Entities.Contains(kv.Key.Split('.')[0])).Select(kv => kv.Value).ToHashSet();
        Assert.Empty(names.Where(others.Contains));
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.Empty(names.Where(system.Contains));
    }

    [Fact]
    public void The_migration_is_additive_creates_three_guarded_tables_seeds_nothing_and_never_touches_the_ledger_or_the_numbering()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == int.Parse(Number)).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bALTER\b", sql);
        Assert.DoesNotMatch(@"\bCREATE OR REPLACE\b", sql);                                       // no existing function is replaced
        Assert.DoesNotMatch(@"\bSECURITY DEFINER\b", sql);
        Assert.DoesNotMatch(@"\b(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+t_", sql);                 // not even inside a function: the guards read and refuse, they never write
        Assert.Equal(3, Regex.Matches(sql, @"\bCREATE TABLE\b").Count);
        foreach (var entity in new[] { "CashVoucher", "CashVoucherLine", "CashVoucherEvent" })
        {
            var table = CompanySchema.Map[entity];
            Assert.Contains($"REVOKE ALL ON TABLE {table} FROM PUBLIC", sql);
            Assert.Contains($"CREATE TRIGGER {CompanySchema.Map[entity + ".TrRow"]} BEFORE INSERT OR UPDATE OR DELETE ON {table}", sql);
            Assert.Contains($"CREATE TRIGGER {CompanySchema.Map[entity + ".TrTruncate"]} BEFORE TRUNCATE ON {table}", sql);
        }
        Assert.Contains($"CREATE UNIQUE INDEX {CompanySchema.CashVoucher.UqIdempotency}", sql);
        Assert.Contains($"CREATE CONSTRAINT TRIGGER {CompanySchema.CashVoucher.TrHistory} AFTER INSERT OR UPDATE ON {CompanySchema.CashVoucher.Table} DEFERRABLE INITIALLY DEFERRED", sql);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.CashVoucherAccountGuard.TrRow} BEFORE UPDATE ON {CompanySchema.CashAccount.Table}", sql);
        // no reference to a journal, a numbering, a fiscal or a cost-center table: the only existing tables it names are the cash account, the chart, the branch, the party, the party role and the company currency
        string[] allowed = [CompanySchema.CashAccount.Table, CompanySchema.Account.Table, CompanySchema.Branch.Table, CompanySchema.LocalParty.Table, CompanySchema.PartyRole.Table, CompanySchema.CompanyCurrency.Table,
            CompanySchema.CashVoucher.Table, CompanySchema.CashVoucherLine.Table, CompanySchema.CashVoucherEvent.Table];
        Assert.Empty(Regex.Matches(sql, @"\bt_[0-9a-f]{8}\b").Select(m => m.Value).Distinct().Except(allowed));
        // the only status transitions the guard admits (draft 1, submitted 2, cancelled 5); approved 3 and rejected 4 appear nowhere in it
        Assert.Contains($"(OLD.{CompanySchema.CashVoucher.Status} = 1 AND NEW.{CompanySchema.CashVoucher.Status} IN (1, 2, 5)) OR (OLD.{CompanySchema.CashVoucher.Status} = 2 AND NEW.{CompanySchema.CashVoucher.Status} IN (1, 5))", sql);
        Assert.DoesNotMatch(@"(?i)\b(official|approved|posted|journal)\b", sql);
    }
}
