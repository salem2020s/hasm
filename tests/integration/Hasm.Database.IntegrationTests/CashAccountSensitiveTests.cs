using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyDatabase;
using Ca = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashAccount;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The bank account number is sensitive (D-155, D-211 Q8): masked in every response, list and audit detail; the full value only for the holder of the sensitive permission on the branch, every reveal audited; stored only as an
/// envelope under the key ring; a masked value sent back never overwrites the stored number; a change is audited with masked values.
/// </summary>
public class CashAccountSensitiveTests : CashAccountTestBase
{
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
        Assert.DoesNotContain("0380", await Details());
        Assert.DoesNotContain(Number, string.Join("\n", apiLogs.Entries));
        var stored = await Sql<byte[]>($"SELECT {Ca.NumberCipher} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'");
        Assert.DoesNotContain("0380000000", Encoding.UTF8.GetString(stored));
        Assert.Equal("HFE1", Encoding.ASCII.GetString(stored, 0, 4));
        Assert.Equal("k1", await Sql<string>($"SELECT {Ca.NumberKeyId} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'"));
        Assert.Equal("7519", await Sql<string>($"SELECT {Ca.NumberLast4} FROM {Ca.Table} WHERE {Ca.Id} = '{id}'"));
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
}
