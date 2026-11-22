using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ac = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using Ca = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashAccount;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>A usage probe the tests switch (slices S2 and S3 will provide the real one).</summary>
public sealed class SwitchableCashProbe : ICashAccountUsageProbe
{
    public CashAccountUsage Usage { get; set; } = new(false, false);
    public Task<CashAccountUsage> GetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid cashAccountId, CancellationToken ct) => Task.FromResult(Usage);
}

/// <summary>
/// Phase 3.14 slice S1: the cash and bank account master over real HTTP. The permission codes are in the code catalog; the system migration that grants them to the preset roles waits for the owner, so every member here
/// holds them through a role the test creates.
/// </summary>
/// <summary>SQL that puts a bank account row (with a number envelope under a named master key) into a freshly provisioned company, for the tests of the key inventory (the row does not need a real envelope: only its key id is read).</summary>
public static class CashTestSql
{
    public static string BankRow(string code, string glCode, string keyId) =>
        $"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now()) ON CONFLICT DO NOTHING; " +
        $"INSERT INTO {Ac.Table} ({Ac.Id}, {Ac.Code}, {Ac.NameEn}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.IsGroup}, {Ac.IsActive}, {Ac.CurrencyPolicy}, {Ac.Revision}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('{Guid.NewGuid()}', '{glCode}', 'gl', 1, 1, 1, false, true, 1, 1, now(), now()); " +
        $"INSERT INTO {Ca.Table} ({Ca.Id}, {Ca.Kind}, {Ca.Code}, {Ca.NameEn}, {Ca.BranchId}, {Ca.AccountId}, {Ca.Currency}, {Ca.BankName}, {Ca.BankBranchName}, {Ca.NumberCipher}, {Ca.NumberKeyId}, {Ca.NumberTail}, {Ca.IsActive}, {Ca.Revision}, {Ca.CreatedBy}, {Ca.CreatedAtUtc}, {Ca.UpdatedAtUtc}) " +
        $"VALUES ('{Guid.NewGuid()}', 2, '{code}', 'Bank', (SELECT {CompanySchema.Branch.Id} FROM {CompanySchema.Branch.Table} WHERE {CompanySchema.Branch.IsMain}), (SELECT {Ac.Id} FROM {Ac.Table} WHERE {Ac.Code} = '{glCode}'), 'JOD', 'B', 'BB', '\\x00ff'::bytea, '{keyId}', '7519', true, 1, '{Guid.NewGuid()}', now(), now())";
}

public abstract class CashAccountTestBase : HttpApiTestBase
{
    protected const string Base = "/api/v1/company/cash-accounts";
    protected static readonly string[] Manage = [PermissionCatalog.Codes.CashAccountsView, PermissionCatalog.Codes.CashAccountsCreate, PermissionCatalog.Codes.CashAccountsUpdate, PermissionCatalog.Codes.CashAccountsActivate];
    protected static readonly string[] Sensitive = [PermissionCatalog.Codes.CashAccountsSensitiveView];
    protected readonly SwitchableCashProbe Probe = new();
    protected InMemoryKekRing Keys = new("k1");

    protected async Task Start(bool keyUnavailable = false, bool baseCurrency = true)
    {
        Keys = new InMemoryKekRing("k1") { Unavailable = keyUnavailable };
        await StartApi(configure: b => b.ConfigureServices(s =>
        {
            s.AddSingleton<KekRing>(Keys);
            s.AddSingleton(new EncryptionOptions { AllowInsecureFilePermissions = true });
            s.AddSingleton<ICashAccountUsageProbe>(Probe);
        }));
        if (baseCurrency) await Sql($"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
    }

    /// <summary>A general-ledger account (type 1 asset by default) inserted directly, with an optional currency policy (1 base, 2 any, 3 specific + currency).</summary>
    protected async Task<Guid> Gl(string code, int type = 1, bool group = false, bool active = true, int policy = 1, string? currency = null)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {Ac.Table} ({Ac.Id}, {Ac.Code}, {Ac.NameEn}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.IsGroup}, {Ac.IsActive}, {Ac.CurrencyPolicy}, {Ac.CurrencyCode}, {Ac.Revision}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) " +
            $"VALUES ('{id}', '{code}', 'gl {code}', {type}, {(type is 1 or 5 ? 1 : 2)}, {(type <= 3 ? 1 : 2)}, {(group ? "true" : "false")}, {(active ? "true" : "false")}, {policy}, {(currency is null ? "NULL" : $"'{currency}'")}, 1, now(), now())");
        return id;
    }

    protected static object Cashbox(string code, Guid branch, Guid account, string? ar = "الصندوق الرئيسي") => new { kind = "cashbox", code, nameAr = ar, nameEn = "Main cash", branchId = branch, accountId = account };
    protected static object Bank(string code, Guid branch, Guid account, string? number = "SA03 8000 0000 6080 1016 7519") => new { kind = "bank", code, nameAr = "البنك", nameEn = "The bank", branchId = branch, accountId = account, bankName = "National Bank", bankBranchName = "Downtown", accountNumber = number };

    protected async Task<string> AsAdmin() => await SignIn(Admin);
    private int holders;

    /// <summary>
    /// A member holding the permissions through a role the TEST creates. The Company Administrator preset does not hold the cash codes until the system migration that grants them lands, and an administrator cannot grant what the preset
    /// lacks, so the role is created with a permission the administrator holds and the cash codes are added, and the assignment made, directly in the system database (test infrastructure only).
    /// </summary>
    protected async Task<UserRecord> Holder(string[] permissions, ScopeKind scope = ScopeKind.Company, Guid? scopeId = null)
    {
        var n = Interlocked.Increment(ref holders);
        var user = await NewUser($"cashholder{n}");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, user.Login)).IsOk);
        var role = await Rbac.CreateRoleAsync(Admin.Id, Company.Id, $"cashrole{n}", [PermissionCatalog.Codes.ConfigView]);
        Assert.True(role.IsOk, role.Code);
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        foreach (var code in permissions)
        {
            await using var rp = new NpgsqlCommand($"INSERT INTO {SystemSchema.RolePermission.Table} ({SystemSchema.RolePermission.RoleId}, {SystemSchema.RolePermission.Permission}) VALUES (@r, @p)", conn);
            rp.Parameters.AddWithValue("r", role.Value!.Id); rp.Parameters.AddWithValue("p", code);
            await rp.ExecuteNonQueryAsync();
        }
        var A = SystemSchema.RoleAssignment.Table;
        await using var ins = new NpgsqlCommand(
            $"INSERT INTO {A} ({SystemSchema.RoleAssignment.Id}, {SystemSchema.RoleAssignment.UserId}, {SystemSchema.RoleAssignment.CompanyId}, {SystemSchema.RoleAssignment.RoleId}, {SystemSchema.RoleAssignment.ScopeKind}, {SystemSchema.RoleAssignment.ScopeId}, " +
            $"{SystemSchema.RoleAssignment.ScopeEpoch}, {SystemSchema.RoleAssignment.GrantedAtUtc}, {SystemSchema.RoleAssignment.GrantedBy}) VALUES (@i, @u, @c, @r, @k, @s, @e, now(), @g)", conn);
        ins.Parameters.AddWithValue("i", Guid.NewGuid()); ins.Parameters.AddWithValue("u", user.Id); ins.Parameters.AddWithValue("c", Company.Id); ins.Parameters.AddWithValue("r", role.Value!.Id);
        ins.Parameters.AddWithValue("k", (short)scope); ins.Parameters.AddWithValue("s", (object?)scopeId ?? DBNull.Value); ins.Parameters.AddWithValue("e", scope == ScopeKind.Branch ? 1L : DBNull.Value); ins.Parameters.AddWithValue("g", Admin.Id);
        await ins.ExecuteNonQueryAsync();
        return user;
    }

    protected async Task<string> AsMember(string[] permissions, ScopeKind scope = ScopeKind.Company, Guid? scopeId = null) => await SignIn(await Holder(permissions, scope, scopeId));
    protected Task<long> Events(short type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");
    protected Task<long> AccountRows() => Sql<long>($"SELECT count(*) FROM {Ca.Table}");
    protected async Task<Guid> MainBranch() => (await Domain.ListAsync(Company.Id)).Single(b => b.IsMain).Id;

    /// <summary>A member who holds the manage permissions (company-wide).</summary>
    protected async Task<string> Manager() => await AsMember(Manage);
    protected async Task<ApiResponse> Create(string cookie, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var r = await Send(HttpMethod.Post, Base, body, cookie);
        Assert.Equal(expected, r.Status);
        return r;
    }
    protected static Guid IdOf(ApiResponse r) => r.Json.GetProperty("id").GetGuid();
    protected async Task<string> SqlErrorAsync(string sql)
    {
        try { await Sql(sql); return ""; }
        catch (PostgresException ex) { return ex.MessageText; }
    }
}

public class CashAccountTests : CashAccountTestBase
{
    [PostgresFact]
    public async Task A_cashbox_and_a_bank_account_are_created_bound_to_asset_accounts_in_the_base_currency_with_one_audit_event_each()
    {
        await Start();
        var main = await MainBranch(); var g1 = await Gl("1101"); var g2 = await Gl("1102"); var cookie = await Manager();
        var box = await Create(cookie, Cashbox("main-cash", main, g1));
        Assert.Equal("\"1\"", Etag(box));
        Assert.Equal(("cashbox", "MAIN-CASH", "JOD", true, 1L), (box.Json.GetProperty("kind").GetString(), box.Json.GetProperty("code").GetString(), box.Json.GetProperty("currency").GetString(), box.Json.GetProperty("isActive").GetBoolean(), box.Json.GetProperty("revision").GetInt64()));
        Assert.False(box.Json.GetProperty("hasAccountNumber").GetBoolean());
        var bank = await Create(cookie, Bank("nb-1", main, g2));
        Assert.Equal("bank", bank.Json.GetProperty("kind").GetString());
        Assert.Equal("****7519", bank.Json.GetProperty("accountNumberMasked").GetString());
        Assert.Equal(2L, await Events(CashAuditEvents.AccountCreated));
        Assert.Equal(0L, await Events(CashAuditEvents.BankDetailsChanged));
        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(["MAIN-CASH", "NB-1"], list.Json.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToArray());
        Assert.Contains("no-store", list.Headers.CacheControl!.ToString());
        var filtered = await Send(HttpMethod.Get, $"{Base}?kind=bank", cookie: cookie);
        Assert.Equal(["NB-1"], filtered.Json.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToArray());
    }

    [PostgresFact]
    public async Task The_code_and_the_general_ledger_binding_are_unique_and_a_refusal_writes_nothing()
    {
        await Start();
        var main = await MainBranch(); var g1 = await Gl("1101"); var g2 = await Gl("1102"); var cookie = await Manager();
        await Create(cookie, Cashbox("c1", main, g1));
        var dupCode = await Create(cookie, Cashbox("C1", main, g2), HttpStatusCode.Conflict);                  // the code is case-insensitive
        Assert.Equal("cash_account_code_taken", dupCode.Error);
        var dupGl = await Create(cookie, Cashbox("c2", main, g1), HttpStatusCode.Conflict);
        Assert.Equal("gl_account_already_bound", dupGl.Error);
        Assert.Equal(1L, await AccountRows());
        Assert.Equal(1L, await Events(CashAuditEvents.AccountCreated));
    }

    [PostgresFact]
    public async Task The_bound_account_must_be_an_active_postable_asset_account_that_admits_the_base_currency()
    {
        await Start();
        var main = await MainBranch(); var cookie = await Manager();
        async Task Refused(Guid account, string fieldCode, string code = "x")
        {
            var r = await Create(cookie, Cashbox(code, main, account), HttpStatusCode.BadRequest);
            var f = Assert.Single(r.Json.GetProperty("fields").EnumerateArray());
            Assert.Equal(("accountId", fieldCode), (f.GetProperty("field").GetString(), f.GetProperty("code").GetString()));
        }
        await Refused(await Gl("2101", type: 2), "account_not_asset", "a");
        await Refused(await Gl("1201", group: true), "account_not_postable", "b");
        await Refused(await Gl("1202", active: false), "account_not_postable", "c");
        await Refused(await Gl("1203", policy: 3, currency: "USD"), "account_currency_not_base", "d");
        await Refused(Guid.NewGuid(), "account_not_found", "e");
        Assert.Equal(0L, await AccountRows());
        await Create(cookie, Cashbox("ok-any", main, await Gl("1204", policy: 2)));                                // any currency
        await Create(cookie, Cashbox("ok-base", main, await Gl("1205", policy: 1)));
        await Create(cookie, Cashbox("ok-spec", main, await Gl("1206", policy: 3, currency: "JOD")));              // specific, equal to the base currency
    }

    [PostgresFact]
    public async Task Without_a_selected_base_currency_there_is_no_cash_account()
    {
        await Start(baseCurrency: false);
        var main = await MainBranch(); var g = await Gl("1101"); var cookie = await Manager();
        var r = await Create(cookie, Cashbox("c1", main, g), HttpStatusCode.Conflict);
        Assert.Equal("base_currency_not_selected", r.Error);
        Assert.Equal(0L, await AccountRows());
    }

    [PostgresFact]
    public async Task Validation_names_the_field_and_a_cashbox_carries_no_bank_details()
    {
        await Start();
        var main = await MainBranch(); var g = await Gl("1101"); var cookie = await Manager();
        async Task<(string Field, string Code)> Bad(object body)
        {
            var r = await Create(cookie, body, HttpStatusCode.BadRequest);
            var f = r.Json.GetProperty("fields").EnumerateArray().First();
            return (f.GetProperty("field").GetString()!, f.GetProperty("code").GetString()!);
        }
        Assert.Equal(("kind", "required"), await Bad(new { code = "a", nameEn = "x", branchId = main, accountId = g }));
        Assert.Equal(("kind", "invalid"), await Bad(new { kind = "vault", code = "a", nameEn = "x", branchId = main, accountId = g }));
        Assert.Equal(("code", "invalid"), await Bad(new { kind = "cashbox", code = "bad code!", nameEn = "x", branchId = main, accountId = g }));
        Assert.Equal(("nameAr", "required"), await Bad(new { kind = "cashbox", code = "a", branchId = main, accountId = g }));
        Assert.Equal(("branchId", "required"), await Bad(new { kind = "cashbox", code = "a", nameEn = "x", accountId = g }));
        Assert.Equal(("accountId", "required"), await Bad(new { kind = "cashbox", code = "a", nameEn = "x", branchId = main }));
        Assert.Equal(("bankName", "not_allowed_for_cashbox"), await Bad(new { kind = "cashbox", code = "a", nameEn = "x", branchId = main, accountId = g, bankName = "B" }));
        Assert.Equal(("accountNumber", "not_allowed_for_cashbox"), await Bad(new { kind = "cashbox", code = "a", nameEn = "x", branchId = main, accountId = g, accountNumber = "123456" }));
        Assert.Equal(("accountNumber", "invalid"), await Bad(Bank("b", main, g, number: "12")));
        Assert.Equal(("accountNumber", "masked_value"), await Bad(Bank("b", main, g, number: "****7519")));        // a masked-looking value is never stored as a number
        var inactive = await Make("OFF"); Assert.True((await Domain.DeactivateAsync(Admin.Id, Company.Id, inactive.Id, 1)).IsOk);
        var closed = await Create(cookie, Cashbox("a", inactive.Id, g), HttpStatusCode.Conflict);
        Assert.Equal("branch_inactive", closed.Error);
        var unknown = await Create(cookie, Cashbox("a", Guid.NewGuid(), g), HttpStatusCode.BadRequest);
        Assert.Equal("branch_not_found", unknown.Json.GetProperty("fields")[0].GetProperty("code").GetString());
        Assert.Equal(0L, await AccountRows());
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_the_invariants_whatever_the_application_does()
    {
        await Start();
        var main = await MainBranch(); var cookie = await Manager();
        var g = await Gl("1101"); var id = IdOf(await Create(cookie, Cashbox("c1", main, g)));
        var liability = await Gl("2101", type: 2);
        Assert.Equal("cash_account_gl_not_asset", await SqlErrorAsync($"UPDATE {Ca.Table} SET {Ca.AccountId} = '{liability}', {Ca.Revision} = {Ca.Revision} + 1 WHERE {Ca.Id} = '{id}'"));
        Assert.Contains("never removed", await SqlErrorAsync($"DELETE FROM {Ca.Table} WHERE {Ca.Id} = '{id}'"));
        Assert.Contains("never removed", await SqlErrorAsync($"TRUNCATE {Ca.Table}"));
        Assert.Contains("immutable", await SqlErrorAsync($"UPDATE {Ca.Table} SET {Ca.Code} = 'ZZ', {Ca.Revision} = {Ca.Revision} + 1 WHERE {Ca.Id} = '{id}'"));
        Assert.Contains("immutable", await SqlErrorAsync($"UPDATE {Ca.Table} SET {Ca.Kind} = 2, {Ca.Revision} = {Ca.Revision} + 1 WHERE {Ca.Id} = '{id}'"));
        Assert.Contains("revision must advance by one", await SqlErrorAsync($"UPDATE {Ca.Table} SET {Ca.NameEn} = 'q' WHERE {Ca.Id} = '{id}'"));
        Assert.NotEqual("", await SqlErrorAsync($"UPDATE {Ca.Table} SET {Ca.BankName} = 'B', {Ca.Revision} = {Ca.Revision} + 1 WHERE {Ca.Id} = '{id}'"));       // a cashbox with bank details: the CHECK
        Assert.Equal("cash_account_currency_not_base", await SqlErrorAsync($"UPDATE {Ca.Table} SET {Ca.Currency} = 'USD', {Ca.Revision} = {Ca.Revision} + 1 WHERE {Ca.Id} = '{id}'"));
        // a second row for the same general-ledger account, or the same code in another spelling, is refused by the unique indexes
        Assert.NotEqual("", await SqlErrorAsync($"INSERT INTO {Ca.Table} ({Ca.Id}, {Ca.Kind}, {Ca.Code}, {Ca.NameEn}, {Ca.BranchId}, {Ca.AccountId}, {Ca.Currency}, {Ca.IsActive}, {Ca.Revision}, {Ca.CreatedBy}, {Ca.CreatedAtUtc}, {Ca.UpdatedAtUtc}) VALUES ('{Guid.NewGuid()}', 1, 'C9', 'x', '{main}', '{g}', 'JOD', true, 1, '{Admin.Id}', now(), now())"));
        Assert.Equal(1L, await AccountRows());
    }

    [PostgresFact]
    public async Task An_update_changes_the_names_and_the_binding_with_a_revision_and_an_audit_event_and_an_unchanged_update_writes_nothing()
    {
        await Start();
        var main = await MainBranch(); var g1 = await Gl("1101"); var g2 = await Gl("1102"); var cookie = await Manager();
        var id = IdOf(await Create(cookie, Cashbox("c1", main, g1)));
        object Body(string en, Guid acct) => new { kind = "cashbox", code = "c1", nameAr = "الصندوق الرئيسي", nameEn = en, branchId = main, accountId = acct };
        var same = await Send(HttpMethod.Put, $"{Base}/{id}", Body("Main cash", g1), cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, same.Status);
        Assert.Equal("\"1\"", Etag(same));
        Assert.Equal(0L, await Events(CashAuditEvents.AccountUpdated));
        var renamed = await Send(HttpMethod.Put, $"{Base}/{id}", Body("Head cash", g2), cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal(("\"2\"", "Head cash", g2), (Etag(renamed), renamed.Json.GetProperty("nameEn").GetString(), renamed.Json.GetProperty("accountId").GetGuid()));
        Assert.Equal(1L, await Events(CashAuditEvents.AccountUpdated));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, $"{Base}/{id}", Body("Stale", g2), cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Put, $"{Base}/{id}", Body("None", g2), cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/{id}", Body("Bad", g2), cookie, ifMatch: "abc")).Status);
        var kind = await Send(HttpMethod.Put, $"{Base}/{id}", new { kind = "bank", code = "c1", nameEn = "x", branchId = main, accountId = g2 }, cookie, ifMatch: "\"2\"");
        Assert.Equal(("kind", "immutable"), (kind.Json.GetProperty("fields")[0].GetProperty("field").GetString(), kind.Json.GetProperty("fields")[0].GetProperty("code").GetString()));
        var code = await Send(HttpMethod.Put, $"{Base}/{id}", new { kind = "cashbox", code = "other", nameEn = "x", branchId = main, accountId = g2 }, cookie, ifMatch: "\"2\"");
        Assert.Equal("immutable", code.Json.GetProperty("fields")[0].GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task Deactivation_and_reactivation_are_revision_fenced_and_a_reactivation_rechecks_the_ledger_account()
    {
        await Start();
        var main = await MainBranch(); var g = await Gl("1101"); var cookie = await Manager();
        var id = IdOf(await Create(cookie, Cashbox("c1", main, g)));
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie)).Status);
        var off = await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, off.Status);
        Assert.False(off.Json.GetProperty("isActive").GetBoolean());
        Assert.Equal("cash_account_already_inactive", (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie, ifMatch: "\"2\"")).Error);
        await Sql($"UPDATE {Ac.Table} SET {Ac.IsActive} = false, {Ac.Revision} = {Ac.Revision} + 1 WHERE {Ac.Id} = '{g}'");          // the ledger account was retired meanwhile
        var refused = await Send(HttpMethod.Post, $"{Base}/{id}/activate", new { }, cookie, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        await Sql($"UPDATE {Ac.Table} SET {Ac.IsActive} = true, {Ac.Revision} = {Ac.Revision} + 1 WHERE {Ac.Id} = '{g}'");
        var on = await Send(HttpMethod.Post, $"{Base}/{id}/activate", new { }, cookie, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.OK, on.Status);
        Assert.Equal("cash_account_already_active", (await Send(HttpMethod.Post, $"{Base}/{id}/activate", new { }, cookie, ifMatch: "\"3\"")).Error);
        Assert.Equal((1L, 1L), (await Events(CashAuditEvents.AccountDeactivated), await Events(CashAuditEvents.AccountReactivated)));
    }

    [PostgresFact]
    public async Task The_binding_freezes_and_deactivation_is_refused_through_the_usage_probe_of_the_later_slices()
    {
        await Start();
        var main = await MainBranch(); var other = await Make("B2"); var g1 = await Gl("1101"); var g2 = await Gl("1102"); var cookie = await Manager();
        var id = IdOf(await Create(cookie, Cashbox("c1", main, g1)));
        object Body(Guid branch, Guid acct) => new { kind = "cashbox", code = "c1", nameAr = "الصندوق الرئيسي", nameEn = "Main cash", branchId = branch, accountId = acct };
        Probe.Usage = new CashAccountUsage(Referenced: true, HasOpenDocuments: true);
        Assert.Equal("binding_frozen", (await Send(HttpMethod.Put, $"{Base}/{id}", Body(main, g2), cookie, ifMatch: "\"1\"")).Error);
        Assert.Equal("binding_frozen", (await Send(HttpMethod.Put, $"{Base}/{id}", Body(other.Id, g1), cookie, ifMatch: "\"1\"")).Error);
        Assert.Equal("cash_account_has_open_documents", (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie, ifMatch: "\"1\"")).Error);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/{id}", new { kind = "cashbox", code = "c1", nameAr = "الصندوق", nameEn = "Renamed", branchId = main, accountId = g1 }, cookie, ifMatch: "\"1\"")).Status);   // names stay editable
        Probe.Usage = new CashAccountUsage(Referenced: true, HasOpenDocuments: false);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie, ifMatch: "\"2\"")).Status);                    // referenced but nothing open
        Probe.Usage = new CashAccountUsage(false, false);
        var moved = await Send(HttpMethod.Put, $"{Base}/{id}", Body(other.Id, g2), cookie, ifMatch: "\"3\"");
        Assert.Equal(HttpStatusCode.OK, moved.Status);                                                                                                       // unreferenced: the binding may still change
        Assert.Equal(other.Id, moved.Json.GetProperty("branchId").GetGuid());
    }

    [PostgresFact]
    public async Task The_candidate_ledger_accounts_are_the_free_active_asset_accounts_that_admit_the_base_currency_and_the_bound_account_is_shown_with_its_code_and_names()
    {
        await Start();
        var main = await MainBranch(); var cookie = await Manager();
        var free1 = await Gl("1101"); var free2 = await Gl("1102", policy: 2); var bound = await Gl("1103");
        await Gl("2101", type: 2); await Gl("1201", group: true); await Gl("1202", active: false); await Gl("1203", policy: 3, currency: "USD"); await Gl("1204", policy: 3, currency: "JOD");
        var created = await Create(cookie, Cashbox("c1", main, bound));
        Assert.Equal(("1103", "gl 1103"), (created.Json.GetProperty("account").GetProperty("code").GetString(), created.Json.GetProperty("account").GetProperty("nameEn").GetString()));
        Assert.Equal(bound, created.Json.GetProperty("account").GetProperty("id").GetGuid());
        var all = await Send(HttpMethod.Get, $"{Base}/gl-candidates", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, all.Status);
        Assert.Equal(["1101", "1102", "1204"], all.Json.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToArray());     // not liability, group, inactive, USD-only or already bound
        Assert.Equal(3L, all.Json.GetProperty("total").GetInt64());
        var found = await Send(HttpMethod.Get, $"{Base}/gl-candidates?search=1102&limit=1", cookie: cookie);
        Assert.Equal(["1102"], found.Json.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToArray());
        var limited = await Send(HttpMethod.Get, $"{Base}/gl-candidates?limit=1", cookie: cookie);
        Assert.Equal((1, 3L), (limited.Json.GetProperty("accounts").GetArrayLength(), limited.Json.GetProperty("total").GetInt64()));              // bounded, with the total
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/gl-candidates?search={new string('x', 101)}", cookie: cookie)).Status);
        Assert.Equal(0L, (await Send(HttpMethod.Get, $"{Base}/gl-candidates?search=%25", cookie: cookie)).Json.GetProperty("total").GetInt64());      // a wildcard is a literal, not a match-all
        var viewer = await AsMember([PermissionCatalog.Codes.CashAccountsView]);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/gl-candidates", cookie: viewer)).Status);                       // the chart is revealed only to those who may bind an account
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Base}/gl-candidates")).Status);
        _ = free1; _ = free2;
    }

    [PostgresFact]
    public async Task The_cash_module_never_touches_the_ledger_or_the_numbering_and_no_balance_is_stored()
    {
        await Start();
        var main = await MainBranch(); var g = await Gl("1101"); var cookie = await Manager();
        var journalBefore = await Sql<long>($"SELECT count(*) FROM {CompanySchema.JournalEntry.Table}");
        var issuedBefore = await Sql<long>($"SELECT count(*) FROM {CompanySchema.NumberIssue.Table}");
        var id = IdOf(await Create(cookie, Bank("nb", main, g)));
        await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie, ifMatch: "\"1\"");
        Assert.Equal(journalBefore, await Sql<long>($"SELECT count(*) FROM {CompanySchema.JournalEntry.Table}"));
        Assert.Equal(issuedBefore, await Sql<long>($"SELECT count(*) FROM {CompanySchema.NumberIssue.Table}"));
        var columns = await Sql<long>($"SELECT count(*) FROM information_schema.columns WHERE table_name = '{Ca.Table}' AND data_type IN ('numeric','double precision','real','money')");
        Assert.Equal(0L, columns);                                                                                   // no numeric column: nothing like a balance
    }
}
