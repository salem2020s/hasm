using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyDatabase;
using Microsoft.Extensions.DependencyInjection;
using Ca = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashAccount;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The bank account number is sensitive (D-155, D-211 Q8): masked in every response, list and audit detail; the full value only for the holder of the sensitive permission on the branch, every reveal audited; stored only as an
/// envelope under the key ring; a masked value sent back never overwrites the stored number; a change is audited with masked values.
/// </summary>
public class CashAccountSensitiveTests : CashAccountTestBase
{
    /// <summary>
    /// The text without its GUID-shaped identifiers (the 32-hex form and the dashed form). The log, audit and detail lines legitimately carry RANDOM ids (the account id, request and user ids), and a random
    /// GUID contains any given four hex digits now and then: "0380" appeared inside one and failed a test that was meant to prove the account number's digits (and the injected exception's digits) are absent.
    /// That was a collision, not a leak. Identifiers are removed before a SHORT digit run is searched; the full number, its tail, the exception message and every other assertion still search the raw text,
    /// and a number or message that leaked would not look like a GUID.
    /// </summary>
    private static string WithoutIdentifiers(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"(?<![0-9a-fA-F])(?:[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32})(?![0-9a-fA-F])", "<id>");

    private const string Number = "SA0380000000608010167519";

    private async Task<string> Details() => await Sql<string>($"SELECT string_agg(coalesce({Audit_.Detail}, ''), ' | ') FROM {Audit_.Table}");

    [PostgresFact]
    public async Task The_full_number_appears_in_no_list_detail_audit_or_log_and_the_stored_value_is_an_envelope()
    {
        await Start();
        var main = await MainBranch(); var cookie = await Manager();
        var created = await Create(cookie, Bank("nb", main, await Gl("1101"), Number));
        var id = IdOf(created);
        foreach (var r in new[] { created, await Send(HttpMethod.Get, Base, cookie: cookie), await Send(HttpMethod.Get, $"{Base}/{id}", cookie: cookie) })
        {
            Assert.DoesNotContain(Number, r.Body);
            Assert.DoesNotContain("0380000000", r.Body);
            Assert.Contains("****7519", r.Body);
        }
        Assert.DoesNotContain("0380", WithoutIdentifiers(await Details()));
        Assert.DoesNotContain(Number, string.Join("\n", apiLogs.Entries));
        var stored = await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'");
        Assert.DoesNotContain("0380000000", Encoding.UTF8.GetString(stored));
        Assert.Equal("HFE1", Encoding.ASCII.GetString(stored, 0, 4));
        Assert.Equal("k1", await Sql<string>($"SELECT {Ca.NumberKeyId} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'"));
        Assert.Equal("7519", await Sql<string>($"SELECT {Ca.NumberTail} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'"));
    }

    [PostgresFact]
    public async Task The_mask_shows_a_fixed_safe_share_for_every_accepted_length_in_the_record_the_list_the_audit_and_the_stored_column()
    {
        await Start();
        var main = await MainBranch(); var cookie = await Manager();
        // (length, expected mask): 4, 7 -> nothing; 8, 11 -> the last two; 12, 34 -> the last four
        (string Number, string Mask)[] cases = [("1234", "****"), ("1234567", "****"), ("12345678", "****78"), ("12345678901", "****01"), ("123456789012", "****9012"), ("1234567890123456789012345678901234", "****1234")];
        var n = 0;
        foreach (var (number, mask) in cases)
        {
            var created = await Create(cookie, Bank("nb" + n, main, await Gl("11" + (10 + n++)), number));
            Assert.Equal(mask, created.Json.GetProperty("accountNumberMasked").GetString());
            var id = IdOf(created);
            var tail = await Sql<string>($"SELECT {Ca.NumberTail} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'");
            Assert.Equal(mask[4..], tail);                                                                           // the column holds only what the mask may show
            Assert.True(tail.Length <= 4 && number.EndsWith(tail, StringComparison.Ordinal));
        }
        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(cases.Select(c => c.Mask).Order().ToArray(), list.Json.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("accountNumberMasked").GetString()!).Order().ToArray());
        // a replacement is audited with the masked values only: a short old number and a long new one
        var firstId = list.Json.GetProperty("accounts").EnumerateArray().First(a => a.GetProperty("code").GetString() == "NB0").GetProperty("id").GetGuid();
        var body = new { kind = "bank", code = "nb0", nameAr = "البنك", nameEn = "The bank", branchId = main, accountId = list.Json.GetProperty("accounts").EnumerateArray().First(a => a.GetProperty("code").GetString() == "NB0").GetProperty("accountId").GetGuid(), bankName = "National Bank", bankBranchName = "Downtown", accountNumber = "AB12CD34EF56GH78" };
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/{firstId}", body, cookie, ifMatch: "\"1\"")).Status);
        var details = await Details();
        Assert.Contains("number ****->****GH78", details);
        Assert.DoesNotContain("1234->", details);
        Assert.DoesNotContain("AB12CD34", details);
    }

    [PostgresFact]
    public async Task Only_the_sensitive_permission_on_the_branch_reveals_the_full_number_and_every_reveal_is_audited()
    {
        await Start();
        var main = await MainBranch(); var other = (await Make("B2")).Id;
        var id = IdOf(await Create(await Manager(), Bank("nb", main, await Gl("1101"), Number)));
        var viewAndReveal = await AsMember([PermissionCatalog.Codes.CashAccountsView, .. Sensitive]);
        var r = await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: viewAndReveal);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(Number, r.Json.GetProperty("accountNumber").GetString());
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: viewAndReveal);
        Assert.Equal(2L, await Events(CashAuditEvents.AccountNumberRevealed));                                    // one event per reveal
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {CashAuditEvents.AccountNumberRevealed} AND {Audit_.SubjectId} = '{id}'"));   // searchable by account (L2 of PR 206)
        Assert.DoesNotContain(Number, await Details());
        // sensitive without view on the branch, or view without sensitive: refused
        var onlySensitive = await AsMember(Sensitive);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: onlySensitive)).Status);
        var wrongBranch = await AsMember([PermissionCatalog.Codes.CashAccountsView, .. Sensitive], ScopeKind.Branch, other);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: wrongBranch)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: await AsMember([PermissionCatalog.Codes.CashAccountsView]))).Status);
        Assert.Equal(2L, await Events(CashAuditEvents.AccountNumberRevealed));                                    // refusals are not reveals
        // a cashbox has no number
        var box = IdOf(await Create(await Manager(), Cashbox("c1", main, await Gl("1102"))));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{box}/account-number", cookie: viewAndReveal)).Status);
    }

    [PostgresFact]
    public async Task A_masked_value_sent_back_never_overwrites_the_number_and_a_new_number_is_audited_masked_with_the_bank_details_event()
    {
        await Start();
        var main = await MainBranch(); var g = await Gl("1101"); var cookie = await Manager();
        var id = IdOf(await Create(cookie, Bank("nb", main, g, Number)));
        object Body(string? number, string bankName = "National Bank") => new { kind = "bank", code = "nb", nameAr = "البنك", nameEn = "The bank", branchId = main, accountId = g, bankName, bankBranchName = "Downtown", accountNumber = number };
        var before = await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'");
        var echoed = await Send(HttpMethod.Put, $"{Base}/{id}", Body("****7519"), cookie, ifMatch: "\"1\"");     // the form that never revealed the number sends the mask back
        Assert.Equal(HttpStatusCode.OK, echoed.Status);
        Assert.Equal("\"1\"", Etag(echoed));                                                                      // unchanged: nothing written
        Assert.Equal(before, await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'"));
        var omitted = await Send(HttpMethod.Put, $"{Base}/{id}", Body(null), cookie, ifMatch: "\"1\"");
        Assert.Equal("\"1\"", Etag(omitted));
        var otherMask = await Send(HttpMethod.Put, $"{Base}/{id}", Body("****0000"), cookie, ifMatch: "\"1\"");
        Assert.Equal(("accountNumber", "masked_value"), (otherMask.Json.GetProperty("fields")[0].GetProperty("field").GetString(), otherMask.Json.GetProperty("fields")[0].GetProperty("code").GetString()));
        var replaced = await Send(HttpMethod.Put, $"{Base}/{id}", Body("DE89 3704 0044 0532 0130 00"), cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, replaced.Status);
        Assert.Equal("****3000", replaced.Json.GetProperty("accountNumberMasked").GetString());
        Assert.NotEqual(before, await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'"));
        Assert.Equal(1L, await Events(CashAuditEvents.BankDetailsChanged));
        var details = await Details();
        Assert.Contains("number ****7519->****3000", details);                                                    // masked old and new
        Assert.DoesNotContain("DE89", details); Assert.DoesNotContain(Number, details);
        // a change of the bank name alone is also a bank-details event
        await Send(HttpMethod.Put, $"{Base}/{id}", Body(null, "Other Bank"), cookie, ifMatch: "\"2\"");
        Assert.Equal(2L, await Events(CashAuditEvents.BankDetailsChanged));
        // the new number is what a reveal returns
        var reveal = await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: await AsMember([PermissionCatalog.Codes.CashAccountsView, .. Sensitive]));
        Assert.Equal("DE89370400440532013000", reveal.Json.GetProperty("accountNumber").GetString());
    }

    [PostgresFact]
    public async Task A_missing_key_fails_closed_nothing_is_stored_in_clear_and_a_number_cannot_be_moved_to_another_account()
    {
        await Start(keyUnavailable: true);
        var main = await MainBranch(); var cookie = await Manager();
        var failed = await Send(HttpMethod.Post, Base, Bank("nb", main, await Gl("1101"), Number), cookie);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.Status);
        Assert.Equal(0L, await AccountRows());
        Assert.Equal(0L, await Events(CashAuditEvents.AccountCreated));
        Keys.Unavailable = false;
        var a = IdOf(await Create(cookie, Bank("nb-a", main, await Gl("1102"), Number)));
        var b = IdOf(await Create(cookie, Bank("nb-b", main, await Gl("1103"), "GB29NWBK60161331926819")));
        // the authenticated envelope is bound to its account: copying A's ciphertext onto B makes B's reveal fail authentication, never return A's number
        await Sql($"UPDATE {Ca.Table} SET {Ca.NumberCipher} = (SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{a}'), {Ca.Revision} = {Ca.Revision} + 1 WHERE {Ca.Id} = '{b}'");
        var reveal = await AsMember([PermissionCatalog.Codes.CashAccountsView, .. Sensitive]);
        var moved = await Send(HttpMethod.Get, $"{Base}/{b}/account-number", cookie: reveal);
        Assert.NotEqual(HttpStatusCode.OK, moved.Status);
        Assert.DoesNotContain(Number, moved.Body);
        Keys.Unavailable = true;
        var noKey = await Send(HttpMethod.Get, $"{Base}/{a}/account-number", cookie: reveal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noKey.Status);
        Assert.DoesNotContain(Number, noKey.Body);
        Assert.Equal(0L, await Events(CashAuditEvents.AccountNumberRevealed));                                    // a reveal that failed is not recorded as served
    }

    // ---- the key lifecycle (M2 of PR 206): re-wrap, key usage

    private string Rotate(string newKeyId) { Keys.Add(newKeyId, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); Keys.Activate(newKeyId); return newKeyId; }
    private Task<string> KeyOf(Guid id) => Sql<string>($"SELECT {Ca.NumberKeyId} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'");
    private async Task<(string Admin, string Reveal)> Staff() => (await Manager(), await AsMember([PermissionCatalog.Codes.CashAccountsView, .. Sensitive]));

    [PostgresFact]
    public async Task The_rewrap_moves_every_number_to_the_current_key_once_audits_each_move_and_the_numbers_stay_readable()
    {
        await Start();
        var main = await MainBranch(); var (cookie, reveal) = await Staff();
        var a = IdOf(await Create(cookie, Bank("nb-a", main, await Gl("1101"), Number)));
        var b = IdOf(await Create(cookie, Bank("nb-b", main, await Gl("1102"), "GB29NWBK60161331926819")));
        await Create(cookie, Cashbox("c1", main, await Gl("1103")));                                               // holds no number
        Assert.Equal(("k1", "k1"), (await KeyOf(a), await KeyOf(b)));
        var usage = await UsageAsync();
        Assert.Equal(2, usage["k1"]);
        var cipherBefore = await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{a}'");
        Rotate("k2");
        var r = await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal((2, 0, 0), (r.Json.GetProperty("rewrapped").GetInt32(), r.Json.GetProperty("skipped").GetInt32(), r.Json.GetProperty("failed").GetInt32()));
        Assert.Equal(("k2", "k2"), (await KeyOf(a), await KeyOf(b)));
        Assert.NotEqual(cipherBefore, await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{a}'"));
        Assert.Equal(2L, await Events(CashAuditEvents.NumberRewrapped));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {CashAuditEvents.NumberRewrapped} AND {Audit_.SubjectId} = '{a}'"));
        var details = await Details();
        Assert.Contains("from=k1 to=k2", details); Assert.DoesNotContain(Number, details); Assert.DoesNotContain("0380", WithoutIdentifiers(details));
        Assert.Equal(Number, (await Send(HttpMethod.Get, $"{Base}/{a}/account-number", cookie: reveal)).Json.GetProperty("accountNumber").GetString());   // still readable under the new key
        Assert.Equal(2, (await UsageAsync())["k2"]);
        Assert.False((await UsageAsync()).ContainsKey("k1"));
        // idempotent: nothing moves twice, no further event
        var again = await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, cookie);
        Assert.Equal((0, 2, 0), (again.Json.GetProperty("rewrapped").GetInt32(), again.Json.GetProperty("skipped").GetInt32(), again.Json.GetProperty("failed").GetInt32()));
        Assert.Equal(2L, await Events(CashAuditEvents.NumberRewrapped));
        Assert.Equal(2L, await Sql<long>($"SELECT {Ca.Revision} FROM {Ca.Table} WHERE {Ca.Id} = '{a}'"));          // the row guard advanced the revision once
    }

    [PostgresFact]
    public async Task The_rewrap_is_fail_closed_a_number_whose_old_key_is_gone_is_left_untouched_counted_and_audited_with_a_reason_and_the_others_still_move()
    {
        await Start();
        var main = await MainBranch(); var cookie = await Manager();
        var lost = IdOf(await Create(cookie, Bank("nb-lost", main, await Gl("1101"), Number)));              // under k1
        Rotate("k2");
        var moved = IdOf(await Create(cookie, Bank("nb-new", main, await Gl("1102"), "GB29NWBK60161331926819")));   // under k2 already
        Rotate("k3");
        Assert.True(Keys.Remove("k1"));                                                                         // the old key is gone: its number cannot be re-wrapped
        var cipher = await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{lost}'");
        var r = await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal((1, 0, 1), (r.Json.GetProperty("rewrapped").GetInt32(), r.Json.GetProperty("skipped").GetInt32(), r.Json.GetProperty("failed").GetInt32()));
        Assert.Equal(("k1", "k3"), (await KeyOf(lost), await KeyOf(moved)));
        Assert.Equal(cipher, await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{lost}'"));   // untouched
        Assert.Equal((1L, 1L), (await Events(CashAuditEvents.NumberRewrapped), await Events(CashAuditEvents.NumberRewrapFailed)));
        Assert.Contains("reason=encryption_key_unavailable", await Details());
        // the active key itself unavailable: the whole call fails closed (503) and nothing changes
        Keys.Unavailable = true;
        var down = await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, cookie);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.Status);
        Assert.Equal(("k1", "k3"), (await KeyOf(lost), await KeyOf(moved)));
    }

    [PostgresFact]
    public async Task A_failed_audit_of_a_failed_rewrap_is_logged_with_codes_only_and_the_call_goes_on()
    {
        await Start();
        var main = await MainBranch(); var cookie = await Manager();
        var lost = IdOf(await Create(cookie, Bank("nb-lost", main, await Gl("1101"), Number)));
        await Create(cookie, Cashbox("c1", main, await Gl("1102")));
        Rotate("k2");
        Assert.True(Keys.Remove("k1"));                                                                         // the number cannot be re-wrapped: the failure path runs
        var service = factory!.Services.GetRequiredService<CashAccountService>();
        service.BeforeFailureAuditForTests = _ => throw new InvalidOperationException("audit store secret detail 0380");   // the audit of the failure itself fails
        try
        {
            var r = await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, cookie);
            Assert.Equal(HttpStatusCode.OK, r.Status);                                                          // the call goes on and answers
            Assert.Equal((0, 0, 1), (r.Json.GetProperty("rewrapped").GetInt32(), r.Json.GetProperty("skipped").GetInt32(), r.Json.GetProperty("failed").GetInt32()));
            Assert.Equal(0L, await Events(CashAuditEvents.NumberRewrapFailed));                                 // best effort: no audit row was written
            Assert.Equal("k1", await Sql<string>($"SELECT {Ca.NumberKeyId} FROM {Ca.Table} WHERE {Ca.Id} = '{lost}'"));   // the row is untouched
            var line = Assert.Single(apiLogs.Entries, e => e.Contains("cash.rewrap.failure_audit_failed", StringComparison.Ordinal));
            Assert.Contains($"account={lost:N}", line);
            Assert.Contains("reason=encryption_key_unavailable", line);
            Assert.Contains("error=InvalidOperationException", line);
            var all = string.Join("\n", apiLogs.Entries);
            Assert.DoesNotContain(Number, all); Assert.DoesNotContain("0380", WithoutIdentifiers(all)); Assert.DoesNotContain("secret detail", all);   // no number, no exception message
        }
        finally { service.BeforeFailureAuditForTests = null; }
    }

    [PostgresFact]
    public async Task The_rewrap_needs_the_privileged_update_permission_company_wide()
    {
        await Start();
        var main = await MainBranch(); var other = (await Make("B2")).Id;
        await Create(await Manager(), Bank("nb", main, await Gl("1101"), Number));
        Rotate("k2");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, await AsMember([PermissionCatalog.Codes.CashAccountsView]))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, await AsMember(Manage, ScopeKind.Branch, main))).Status);   // one branch is not company-wide
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, await AsMember([PermissionCatalog.Codes.CashAccountsSensitiveView]))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { })).Status);
        Assert.Equal(0L, await Events(CashAuditEvents.NumberRewrapped));
        _ = other;
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, await AsMember([PermissionCatalog.Codes.CashAccountsUpdate]))).Status);
        Assert.Equal(1L, await Events(CashAuditEvents.NumberRewrapped));
    }

    private async Task<IReadOnlyDictionary<string, int>> UsageAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        return await CashAccountService.KeyUsageAsync(conn);
    }
}
