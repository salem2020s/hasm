using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ac = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using Ca = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashAccount;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using CV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashVoucher;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using VE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashVoucherEvent;
using VL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashVoucherLine;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.14 slice S2a: cash vouchers WITHOUT any financial posting, over real HTTP and by direct SQL. There is no withdrawal: from submitted the only move is the cancellation. The API runs with the PRODUCTION usage probe of the cash account master (the real one since this slice), so the freeze and the
/// open-document rule of an account are exercised end to end. The permission codes are in no preset role, so every member holds them through a role the test creates (see <see cref="CashAccountTestBase.Holder"/>).
/// </summary>
public abstract class CashVoucherTestBase : CashAccountTestBase
{
    protected const string V = "/api/v1/company/cash-vouchers";
    protected static readonly string[] Clerk = [PermissionCatalog.Codes.CashVouchersView, PermissionCatalog.Codes.CashVouchersCreate, PermissionCatalog.Codes.CashVouchersUpdate, PermissionCatalog.Codes.CashVouchersSubmit, PermissionCatalog.Codes.CashVouchersCancel];
    protected static readonly string Hash = new('a', 64);
    private string? manager;

    /// <summary>The API with the production composition of the cash module (no switchable probe) and the base currency JOD (three minor units).</summary>
    protected async Task StartV()
    {
        Keys = new InMemoryKekRing("k1", TestIdentityKeys.Master);
        await StartApi(configure: b => { b.UseSetting("Hasm:Encryption:IdentityPreviousKeyIds", "k1,k2,k3"); b.ConfigureServices(s =>
        {
            s.AddSingleton<KekRing>(Keys);
            s.AddSingleton(new EncryptionOptions { AllowInsecureFilePermissions = true });
        }); });
        await Sql($"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
    }

    protected async Task<string> AccountManager() => manager ??= await Manager();

    /// <summary>A cashbox on a new asset account of the chart, in the given branch.</summary>
    protected async Task<Guid> Box(string code, Guid branch, string gl) => IdOf(await Create(await AccountManager(), Cashbox(code, branch, await Gl(gl))));

    protected static object Line(Guid account, decimal amount, string? description = null) => new { accountId = account, amount, description };
    protected static object Body(string? kind, Guid? account, string? key, params object[] lines) => new { kind, cashAccountId = account, voucherDate = "2026-03-10", idempotencyKey = key, lines };

    protected async Task<ApiResponse> Post(string cookie, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var r = await Send(HttpMethod.Post, V, body, cookie);
        Assert.True(expected == r.Status, $"{(int)r.Status} {r.Body}");
        return r;
    }
    protected Task<ApiResponse> Act(string cookie, Guid id, string action, long revision, object? body = null) => Send(HttpMethod.Post, $"{V}/{id}/{action}", body ?? new { }, cookie, ifMatch: $"\"{revision}\"");
    protected Task<ApiResponse> Put(string cookie, Guid id, long revision, object body) => Send(HttpMethod.Put, $"{V}/{id}", body, cookie, ifMatch: $"\"{revision}\"");
    protected Task<ApiResponse> Read(string cookie, Guid id) => Send(HttpMethod.Get, $"{V}/{id}", cookie: cookie);

    protected static string StatusOf(ApiResponse r) => r.Json.GetProperty("status").GetString()!;
    protected static long Rev(ApiResponse r) => r.Json.GetProperty("revision").GetInt64();
    protected static (HttpStatusCode, string?) Refusal(ApiResponse r) => (r.Status, r.Error);
    /// <summary>The code of the field error on <paramref name="field"/> (a 400 with fields), or the plain error.</summary>
    protected static string? FieldCode(ApiResponse r, string field)
    {
        if (!r.Json.TryGetProperty("fields", out var fields)) return $"{(int)r.Status}:{r.Error}";
        foreach (var f in fields.EnumerateArray()) if (f.GetProperty("field").GetString() == field) return f.GetProperty("code").GetString();
        return $"no error on {field}: {r.Body}";
    }
    protected static short[] EventKinds(ApiResponse r) => [.. r.Json.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetInt16())];

    protected Task<long> Rows(string table, string where = "true") => Sql<long>($"SELECT count(*) FROM {table} WHERE {where}");
    protected Task<short> DbStatus(Guid id) => Sql<short>($"SELECT {CV.Status} FROM {CV.Table} WHERE {CV.Id} = '{id}'");
    protected Task<long> DbRevision(Guid id) => Sql<long>($"SELECT {CV.Revision} FROM {CV.Table} WHERE {CV.Id} = '{id}'");

    protected async Task<Guid> Party(string code)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{id}', '{code}', 'party {code}', true, 1, 1, '{Admin.Id}', now(), now())");
        return id;
    }

    // ---- direct SQL (the owner of the disposable database): what PostgreSQL itself accepts and refuses

    protected static string VoucherSql(Guid id, Guid account, Guid branch, Guid by, string key, short status = 1, long revision = 1, string currency = "JOD", long linesRevision = 1, string? submittedBy = null, Guid? updatedBy = null) =>
        $"INSERT INTO {CV.Table} ({CV.Id}, {CV.Kind}, {CV.CashAccountId}, {CV.BranchId}, {CV.Currency}, {CV.VoucherDate}, {CV.Status}, {CV.LinesRevision}, {CV.IdempotencyKey}, {CV.PayloadHash}, {CV.SubmittedBy}, {CV.UpdatedBy}, {CV.Revision}, {CV.CreatedBy}, {CV.CreatedAtUtc}, {CV.UpdatedAtUtc}) " +
        $"VALUES ('{id}', 1, '{account}', '{branch}', '{currency}', '2026-03-10', {status}, {linesRevision}, '{key}', '{Hash}', {(submittedBy is null ? "NULL" : $"'{submittedBy}'")}, '{updatedBy ?? by}', {revision}, '{by}', now(), now())";
    /// <summary>A history event by direct SQL. <paramref name="reasonSql"/> is the SQL expression of the reason; when it is not given, a cancellation (kind 5) carries one and every other event carries none.</summary>
    protected static string EventSql(Guid voucher, short kind, short? from, short to, long revision, string? reasonSql = null) =>
        $"INSERT INTO {VE.Table} ({VE.Id}, {VE.VoucherId}, {VE.Kind}, {VE.FromStatus}, {VE.ToStatus}, {VE.Revision}, {VE.ActorUserId}, {VE.Reason}, {VE.AtUtc}) VALUES (gen_random_uuid(), '{voucher}', {kind}, {(from is null ? "NULL" : from.ToString())}, {to}, {revision}, gen_random_uuid(), {reasonSql ?? (kind == 5 ? "'by direct SQL'" : "NULL")}, now())";
    protected static string LineSql(Guid voucher, long revision, int no, Guid account, string amount = "10") =>
        $"INSERT INTO {VL.Table} ({VL.Id}, {VL.VoucherId}, {VL.Revision}, {VL.LineNo}, {VL.AccountId}, {VL.Amount}) VALUES (gen_random_uuid(), '{voucher}', {revision}, {no}, '{account}', {amount})";
    /// <summary>A status change by direct SQL, with the revision advanced (and nothing else changed unless <paramref name="extra"/> says so).</summary>
    protected static string MoveSql(Guid id, short status, string? submitter = null, string extra = "") =>
        $"UPDATE {CV.Table} SET {CV.Status} = {status}, {CV.SubmittedBy} = {(submitter is null ? "NULL" : $"'{submitter}'")}, {CV.Revision} = {CV.Revision} + 1{extra} WHERE {CV.Id} = '{id}'";
}

public class CashVoucherLifecycleTests : CashVoucherTestBase
{
    [PostgresFact]
    public async Task A_receipt_and_a_payment_are_created_as_drafts_in_the_branch_and_currency_of_their_account_with_one_event_and_one_audit_event_each()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4); var rent = await Gl("5101", 5);
        var clerk = await AsMember(Clerk);
        var receipt = await Post(clerk, new { kind = "receipt", cashAccountId = box, voucherDate = "2026-03-10", reference = "  R-1 ", note = "walk-in", idempotencyKey = "receipt-0001", lines = new[] { Line(sales, 10.5m, "first"), Line(sales, 4.125m) } });
        Assert.Equal("\"1\"", Etag(receipt));
        var j = receipt.Json;
        Assert.Equal(("receipt", "draft", "JOD", 1L, false), (j.GetProperty("kind").GetString(), StatusOf(receipt), j.GetProperty("currency").GetString(), Rev(receipt), j.GetProperty("replayed").GetBoolean()));
        Assert.Equal((box, main, "R-1", 14.625m), (j.GetProperty("cashAccountId").GetGuid(), j.GetProperty("branchId").GetGuid(), j.GetProperty("reference").GetString(), j.GetProperty("total").GetDecimal()));
        Assert.Equal(new (int, decimal, string?)[] { (1, 10.5m, "first"), (2, 4.125m, null) }, j.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("lineNo").GetInt32(), l.GetProperty("amount").GetDecimal(), l.GetProperty("description").GetString())).ToArray());
        Assert.Equal(new short[] { 1 }, EventKinds(receipt));
        Assert.Equal(j.GetProperty("createdBy").GetGuid(), j.GetProperty("updatedBy").GetGuid());             // the creator is the first editor
        Assert.Equal(System.Text.Json.JsonValueKind.Null, j.GetProperty("submittedBy").ValueKind);
        foreach (var absent in new[] { "officialNumber", "number", "approvedBy", "journalEntryId", "accountingState" }) Assert.False(j.TryGetProperty(absent, out _), absent);   // nothing of the later slice is even named

        var payment = await Post(clerk, Body("payment", box, "payment-0001", Line(rent, 3m)));
        Assert.Equal(("payment", "draft"), (payment.Json.GetProperty("kind").GetString(), StatusOf(payment)));
        Assert.Equal((2L, 3L, 2L, 2L), (await Rows(CV.Table), await Rows(VL.Table), await Rows(VE.Table), await Events(CashVoucherAuditEvents.Created)));

        var read = await Read(clerk, IdOf(receipt));
        Assert.Equal((HttpStatusCode.OK, "\"1\"", 14.625m), (read.Status, Etag(read), read.Json.GetProperty("total").GetDecimal()));
        var list = await Send(HttpMethod.Get, V, cookie: clerk);
        Assert.Equal(2, list.Json.GetProperty("vouchers").GetArrayLength());
        var payments = await Send(HttpMethod.Get, $"{V}?kind=payment&status=draft&cashAccountId={box}", cookie: clerk);
        var only = Assert.Single(payments.Json.GetProperty("vouchers").EnumerateArray());
        Assert.Equal((IdOf(payment), 3m), (only.GetProperty("id").GetGuid(), only.GetProperty("total").GetDecimal()));
        Assert.Equal(0, (await Send(HttpMethod.Get, $"{V}?status=cancelled", cookie: clerk)).Json.GetProperty("vouchers").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{V}?status=approved", cookie: clerk)).Status);   // not a state of this slice
    }

    [PostgresFact]
    public async Task A_draft_is_edited_by_a_new_line_set_the_earlier_set_is_kept_and_the_kind_and_the_account_never_change()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var other = await Box("c2", main, "1102"); var sales = await Gl("4101", 4); var fees = await Gl("4102", 4);
        var clerk = await AsMember(Clerk); var editor = await Holder(Clerk); var editorCookie = await SignIn(editor);
        var id = IdOf(await Post(clerk, Body("receipt", box, "edit-key-0001", Line(sales, 10m), Line(sales, 5m))));

        Assert.Equal((HttpStatusCode.PreconditionRequired, "precondition_required"), Refusal(await Send(HttpMethod.Put, $"{V}/{id}", Body(null, null, null, Line(fees, 7m)), editorCookie)));
        var stale = await Put(editorCookie, id, 9, Body(null, null, null, Line(fees, 7m)));
        Assert.Equal((HttpStatusCode.PreconditionFailed, 1L), (stale.Status, stale.Json.GetProperty("currentRevision").GetInt64()));
        Assert.Equal("kind_immutable", FieldCode(await Put(editorCookie, id, 1, Body("payment", null, null, Line(fees, 7m))), "kind"));
        Assert.Equal("cash_account_immutable", FieldCode(await Put(editorCookie, id, 1, Body(null, other, null, Line(fees, 7m))), "cashAccountId"));
        Assert.Equal(1L, await DbRevision(id));                                                              // none of the refusals wrote anything

        var updated = await Put(editorCookie, id, 1, new { voucherDate = "2026-03-11", reference = "R-2", lines = new[] { Line(fees, 7m, "only") } });
        Assert.Equal((HttpStatusCode.OK, "\"2\"", "draft", 7m), (updated.Status, Etag(updated), StatusOf(updated), updated.Json.GetProperty("total").GetDecimal()));
        Assert.Equal((editor.Id, "2026-03-11", "R-2"), (updated.Json.GetProperty("updatedBy").GetGuid(), updated.Json.GetProperty("voucherDate").GetString(), updated.Json.GetProperty("reference").GetString()));
        Assert.Equal(fees, Assert.Single(updated.Json.GetProperty("lines").EnumerateArray()).GetProperty("accountId").GetGuid());
        Assert.Equal(new short[] { 1, 2 }, EventKinds(updated));
        // the first line set is still there (history), the voucher names the second
        Assert.Equal((2L, 1L, 2L), (await Rows(VL.Table, $"{VL.VoucherId} = '{id}' AND {VL.Revision} = 1"), await Rows(VL.Table, $"{VL.VoucherId} = '{id}' AND {VL.Revision} = 2"), await Sql<long>($"SELECT {CV.LinesRevision} FROM {CV.Table} WHERE {CV.Id} = '{id}'")));
        Assert.Equal(1L, await Events(CashVoucherAuditEvents.Updated));
        // an edit may also empty the draft; it is then not submittable
        var emptied = await Put(clerk, id, 2, new { voucherDate = "2026-03-11" });
        Assert.Equal((HttpStatusCode.OK, 0m, 0), (emptied.Status, emptied.Json.GetProperty("total").GetDecimal(), emptied.Json.GetProperty("lines").GetArrayLength()));
        Assert.Equal("lines_required", FieldCode(await Act(clerk, id, "submit", 3), "lines"));
    }

    [PostgresFact]
    public async Task Submit_and_cancel_follow_the_state_machine_a_submitted_voucher_never_returns_to_draft_and_every_step_is_one_revision_one_event_and_one_audit_event()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4);
        var clerk = await Holder(Clerk); var cookie = await SignIn(clerk);
        var id = IdOf(await Post(cookie, Body("receipt", box, "state-key-0001", Line(sales, 10m))));
        object edit = new { voucherDate = "2026-03-12", lines = new[] { Line(sales, 1m) } };
        static bool NoRoute(ApiResponse r) => r.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;

        Assert.True(NoRoute(await Act(cookie, id, "withdraw", 1)));                                              // no withdrawal exists (the approved state machine has no submitted -> draft)
        Assert.Equal((HttpStatusCode.PreconditionRequired, "precondition_required"), Refusal(await Send(HttpMethod.Post, $"{V}/{id}/submit", new { }, cookie)));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Act(cookie, id, "submit", 7)).Status);
        var submitted = await Act(cookie, id, "submit", 1);
        Assert.Equal((HttpStatusCode.OK, "submitted", 2L, clerk.Id), (submitted.Status, StatusOf(submitted), Rev(submitted), submitted.Json.GetProperty("submittedBy").GetGuid()));
        Assert.Equal((HttpStatusCode.Conflict, "cash_voucher_not_draft"), Refusal(await Put(cookie, id, 2, edit)));          // a submitted voucher is not edited
        Assert.Equal((HttpStatusCode.Conflict, "cash_voucher_not_draft"), Refusal(await Act(cookie, id, "submit", 2)));

        // a submitted voucher never returns to draft: there is no route for it, and PostgreSQL refuses the transition by itself (with or without a submitter, with any event kind)
        var withdraw = await Act(cookie, id, "withdraw", 2);
        Assert.True(NoRoute(withdraw), $"{(int)withdraw.Status} {withdraw.Body}");
        Assert.NotEqual("cash_voucher_not_found", withdraw.Error);                                                // the refusal is the missing route, not the voucher service
        const string Illegal = "illegal cash voucher status transition";
        Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(id, 1)));
        Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(id, 1, clerk.Id.ToString())));
        Assert.Equal(Illegal, await SqlErrorAsync($"{MoveSql(id, 1)}; {EventSql(id, 4, 2, 1, 3)}"));
        Assert.Equal(Illegal, await SqlErrorAsync($"{MoveSql(id, 1)}; {EventSql(id, 2, 1, 1, 3)}"));
        Assert.Equal(((short)2, 2L, clerk.Id), (await DbStatus(id), await DbRevision(id), await Sql<Guid>($"SELECT {CV.SubmittedBy} FROM {CV.Table} WHERE {CV.Id} = '{id}'")));   // the submitter of record is never cleared

        // from submitted the only move is the cancellation
        Assert.Equal("reason_required", FieldCode(await Act(cookie, id, "cancel", 2), "reason"));
        Assert.Equal("reason_required", FieldCode(await Act(cookie, id, "cancel", 2, new { reason = "  " }), "reason"));
        var cancelled = await Act(cookie, id, "cancel", 2, new { reason = "entered twice" });
        Assert.Equal((HttpStatusCode.OK, "cancelled", 3L, clerk.Id), (cancelled.Status, StatusOf(cancelled), Rev(cancelled), cancelled.Json.GetProperty("submittedBy").GetGuid()));   // the submitter record is kept
        Assert.Equal(new short[] { 1, 3, 5 }, EventKinds(cancelled));
        Assert.Equal("entered twice", cancelled.Json.GetProperty("events")[2].GetProperty("reason").GetString());
        // cancelled is final
        Assert.Equal((HttpStatusCode.Conflict, "cash_voucher_not_draft"), Refusal(await Put(cookie, id, 3, edit)));
        Assert.Equal((HttpStatusCode.Conflict, "cash_voucher_not_draft"), Refusal(await Act(cookie, id, "submit", 3)));
        Assert.True(NoRoute(await Act(cookie, id, "withdraw", 3)));
        Assert.Equal((HttpStatusCode.Conflict, "cash_voucher_not_cancellable"), Refusal(await Act(cookie, id, "cancel", 3, new { reason = "again" })));
        Assert.Equal((3L, (short)5, 3L), (await DbRevision(id), await DbStatus(id), await Rows(VE.Table, $"{VE.VoucherId} = '{id}'")));
        Assert.Equal((1L, 0L, 1L, 1L), (await Events(CashVoucherAuditEvents.Created), await Events(CashVoucherAuditEvents.Updated), await Events(CashVoucherAuditEvents.Submitted), await Events(CashVoucherAuditEvents.Cancelled)));
        Assert.Equal((0L, 0L), (await Events(2113), await Rows(VE.Table, $"{VE.Kind} = 4")));                    // the unused audit code and event kind of a withdrawal are never written
        // a draft is cancelled directly too
        var draft = IdOf(await Post(cookie, Body("payment", box, "state-key-0002")));
        Assert.Equal("cancelled", StatusOf(await Act(cookie, draft, "cancel", 1, new { reason = "not needed" })));
    }

    [PostgresFact]
    public async Task There_is_no_withdrawal_approval_rejection_numbering_posting_reversal_or_delete_route()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4);
        var cookie = await AsMember(Clerk);
        var id = IdOf(await Post(cookie, Body("receipt", box, "noroute-key-01", Line(sales, 10m))));
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, id, "submit", 1)).Status);
        foreach (var later in new[] { "withdraw", "withdrawal", "return", "reopen", "approve", "reject", "decision", "post", "posting", "reverse", "reversal", "number", "journal", "void" })
            Assert.True((await Act(cookie, id, later, 2, new { reason = "x" })).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, later);
        Assert.True((await Send(HttpMethod.Delete, $"{V}/{id}", cookie: cookie, ifMatch: "\"2\"")).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(((short)2, 2L), (await DbStatus(id), await DbRevision(id)));
    }
}

public class CashVoucherValidationTests : CashVoucherTestBase
{
    [PostgresFact]
    public async Task The_body_of_a_voucher_is_validated_and_nothing_is_written_for_a_refused_one()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4);
        var cookie = await AsMember(Clerk);
        async Task<ApiResponse> Bad(object body) => await Post(cookie, body, HttpStatusCode.BadRequest);

        Assert.Equal("required", FieldCode(await Bad(Body(null, box, "valid-key-0001")), "kind"));
        Assert.Equal("invalid_value", FieldCode(await Bad(Body("transfer", box, "valid-key-0001")), "kind"));
        Assert.Equal("required", FieldCode(await Bad(Body("receipt", null, "valid-key-0001")), "cashAccountId"));
        Assert.Equal("idempotency_key_invalid", FieldCode(await Bad(Body("receipt", box, "short")), "idempotencyKey"));
        Assert.Equal("idempotency_key_invalid", FieldCode(await Bad(Body("receipt", box, null)), "idempotencyKey"));
        Assert.Equal("required", FieldCode(await Bad(new { kind = "receipt", cashAccountId = box, idempotencyKey = "valid-key-0001" }), "voucherDate"));
        Assert.Equal("out_of_range", FieldCode(await Bad(new { kind = "receipt", cashAccountId = box, voucherDate = "1999-12-31", idempotencyKey = "valid-key-0001" }), "voucherDate"));
        Assert.Equal("reference_invalid", FieldCode(await Bad(new { kind = "receipt", cashAccountId = box, voucherDate = "2026-03-10", reference = new string('r', 101), idempotencyKey = "valid-key-0001" }), "reference"));
        Assert.Equal("note_invalid", FieldCode(await Bad(new { kind = "receipt", cashAccountId = box, voucherDate = "2026-03-10", note = "a\u0007b", idempotencyKey = "valid-key-0001" }), "note"));
        // amounts: required, positive, within the journal's range, and no more decimals than the base currency (JOD: three)
        Assert.Equal("required", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", new { accountId = sales })), "lines[0].amount"));
        Assert.Equal("required", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", new { amount = 1m })), "lines[0].accountId"));
        Assert.Equal("out_of_range", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", Line(sales, 1m), Line(sales, 0m))), "lines[1].amount"));
        Assert.Equal("out_of_range", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", Line(sales, -5m))), "lines[0].amount"));
        Assert.Equal("out_of_range", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", Line(sales, 1_000_000_000_000_000m))), "lines[0].amount"));
        Assert.Equal("too_many_decimals", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", Line(sales, 1.0001m))), "lines[0].amount"));
        Assert.Equal("total_out_of_range", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", Line(sales, 999_999_999_999_999m), Line(sales, 1m))), "lines"));
        Assert.Equal("too_many_lines", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", [.. Enumerable.Range(0, 500).Select(_ => Line(sales, 1m))])), "lines"));
        Assert.Equal("description_invalid", FieldCode(await Bad(Body("receipt", box, "valid-key-0001", Line(sales, 1m, new string('d', 501)))), "lines[0].description"));
        Assert.Equal((0L, 0L, 0L, 0L), (await Rows(CV.Table), await Rows(VL.Table), await Rows(VE.Table), await Events(CashVoucherAuditEvents.Created)));
        // the largest voucher the later entry can carry is accepted: 499 lines, three decimals
        var full = await Post(cookie, Body("receipt", box, "valid-key-0001", [.. Enumerable.Range(0, 499).Select(_ => Line(sales, 0.001m))]));
        Assert.Equal((499, 0.499m), (full.Json.GetProperty("lines").GetArrayLength(), full.Json.GetProperty("total").GetDecimal()));
    }

    [PostgresFact]
    public async Task A_voucher_needs_a_known_active_cash_account_and_an_inactive_account_takes_no_new_voucher()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var idle = await Box("c2", main, "1102"); var sales = await Gl("4101", 4);
        var cookie = await AsMember(Clerk);
        Assert.Equal("cash_account_not_found", FieldCode(await Post(cookie, Body("receipt", Guid.NewGuid(), "account-key-01", Line(sales, 1m)), HttpStatusCode.BadRequest), "cashAccountId"));
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{idle}/deactivate", new { }, await AccountManager(), ifMatch: "\"1\"")).Status);   // no voucher refers to it: allowed
        Assert.Equal("cash_account_inactive", FieldCode(await Post(cookie, Body("receipt", idle, "account-key-02", Line(sales, 1m)), HttpStatusCode.BadRequest), "cashAccountId"));
        Assert.Equal(HttpStatusCode.Created, (await Post(cookie, Body("receipt", box, "account-key-03", Line(sales, 1m)))).Status);
        Assert.Equal(1L, await Rows(CV.Table));
        // PostgreSQL refuses the same by itself: an unknown or inactive account, another branch than the account's, another currency than the base currency
        var other = (await Make("B2")).Id; var by = Guid.NewGuid();
        Assert.Equal("cash_voucher_account_inactive", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), idle, main, by, "sql-key-0001")));
        Assert.Equal("cash_voucher_branch_mismatch", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, other, by, "sql-key-0002")));
        Assert.Equal("cash_voucher_currency_not_base", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "sql-key-0003", currency: "USD")));
        Assert.Equal("cash_voucher_account_not_found", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), Guid.NewGuid(), main, by, "sql-key-0004")));
        Assert.Equal(1L, await Rows(CV.Table));
    }

    [PostgresFact]
    public async Task A_counter_account_is_postable_active_admits_the_base_currency_is_not_bound_to_a_cash_account_and_is_not_a_party_control_account()
    {
        await StartV();
        await Sql($"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('USD', false, true, 1, now())");
        var main = await MainBranch(); var ownGl = await Gl("1101"); var otherGl = await Gl("1102");
        var manager = await AccountManager();
        var box = IdOf(await Create(manager, Cashbox("c1", main, ownGl))); IdOf(await Create(manager, Cashbox("c2", main, otherGl)));
        var group = await Gl("4000", 4, group: true); var inactive = await Gl("4001", 4, active: false); var usd = await Gl("4002", 4, policy: 3, currency: "USD");
        var any = await Gl("4003", 4, policy: 2); var jod = await Gl("4004", 4, policy: 3, currency: "JOD"); var sales = await Gl("4101", 4);
        var control = await Gl("1201"); var party = await Party("CUS1");
        var roles = new PartyRoleService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        var role = await roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(party, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, control, null));
        Assert.True(role.IsOk, role.Code);
        var cookie = await AsMember(Clerk);
        var n = 0;
        async Task<string?> Refused(Guid account) => FieldCode(await Post(cookie, Body("payment", box, $"counter-key-{++n:0000}", Line(sales, 1m), Line(account, 1m)), HttpStatusCode.BadRequest), "lines[1].accountId");

        Assert.Equal("account_not_found", await Refused(Guid.NewGuid()));
        Assert.Equal("account_not_postable", await Refused(group));
        Assert.Equal("account_not_postable", await Refused(inactive));
        Assert.Equal("account_currency_not_base", await Refused(usd));
        Assert.Equal("counter_account_is_cash_account", await Refused(ownGl));       // the voucher's own cash account
        Assert.Equal("counter_account_is_cash_account", await Refused(otherGl));     // another cash account: a transfer in disguise
        Assert.Equal("counter_account_is_control_account", await Refused(control));  // a receivable or payable control account: not decided (Q3)
        Assert.Equal(0L, await Rows(CV.Table));
        var ok = await Post(cookie, Body("payment", box, "counter-key-ok01", Line(sales, 1m), Line(any, 2m), Line(jod, 3m)));
        Assert.Equal(6m, ok.Json.GetProperty("total").GetDecimal());
        // the same rules for an edit
        Assert.Equal("counter_account_is_control_account", FieldCode(await Put(cookie, IdOf(ok), 1, new { voucherDate = "2026-03-10", lines = new[] { Line(control, 1m) } }), "lines[0].accountId"));

        // PostgreSQL refuses each of them by itself (a line written by direct SQL into a fresh draft, in one transaction with its voucher)
        var by = Guid.NewGuid(); var k = 0;
        async Task<string> Direct(Guid account) { var v = Guid.NewGuid(); return await SqlErrorAsync($"{VoucherSql(v, box, main, by, $"sql-counter-{++k:000}")}; {LineSql(v, 1, 1, account)}; {EventSql(v, 1, null, 1, 1)}"); }
        Assert.Equal("cash_voucher_counter_not_postable", await Direct(group));
        Assert.Equal("cash_voucher_counter_not_postable", await Direct(inactive));
        Assert.Equal("cash_voucher_counter_currency", await Direct(usd));
        Assert.Equal("cash_voucher_counter_is_cash_account", await Direct(ownGl));
        Assert.Equal("cash_voucher_counter_is_cash_account", await Direct(otherGl));
        Assert.Equal("cash_voucher_counter_is_control_account", await Direct(control));
        Assert.Equal("", await Direct(sales));
        Assert.Equal(2L, await Rows(CV.Table));
    }

    [PostgresFact]
    public async Task Submission_checks_the_account_and_the_counter_accounts_again()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4); var fees = await Gl("4102", 4); var later = await Gl("1103");
        var cookie = await AsMember(Clerk);
        var first = IdOf(await Post(cookie, Body("receipt", box, "resubmit-key-01", Line(sales, 1m), Line(fees, 1m))));
        var second = IdOf(await Post(cookie, Body("receipt", box, "resubmit-key-02", Line(later, 1m))));
        // after the drafts were written: one counter account is deactivated, another becomes the ledger account of a new cash account
        await Sql($"UPDATE {Ac.Table} SET {Ac.IsActive} = false, {Ac.Revision} = {Ac.Revision} + 1 WHERE {Ac.Id} = '{fees}'");
        IdOf(await Create(await AccountManager(), Cashbox("c9", main, later)));
        Assert.Equal("account_not_postable", FieldCode(await Act(cookie, first, "submit", 1), "lines[1].accountId"));
        Assert.Equal("counter_account_is_cash_account", FieldCode(await Act(cookie, second, "submit", 1), "lines[0].accountId"));
        // and PostgreSQL refuses the transition by itself
        Assert.Equal("cash_voucher_counter_not_postable", await SqlErrorAsync(MoveSql(first, 2, Guid.NewGuid().ToString())));
        Assert.Equal("cash_voucher_counter_is_cash_account", await SqlErrorAsync(MoveSql(second, 2, Guid.NewGuid().ToString())));
        Assert.Equal(((short)1, 1L, (short)1, 1L), (await DbStatus(first), await DbRevision(first), await DbStatus(second), await DbRevision(second)));
        // corrected drafts are submitted
        Assert.Equal(HttpStatusCode.OK, (await Put(cookie, first, 1, new { voucherDate = "2026-03-10", lines = new[] { Line(sales, 2m) } })).Status);
        Assert.Equal("submitted", StatusOf(await Act(cookie, first, "submit", 2)));
    }

    /// <summary>Review L4 (ii): the service rule <c>branch_inactive</c> (the precedent of the journal and of the cash account master; a service rule, PostgreSQL has no such guard).</summary>
    [PostgresFact]
    public async Task A_voucher_is_neither_written_nor_submitted_on_an_account_of_an_inactive_branch_and_can_still_be_cancelled()
    {
        await StartV();
        var b2 = await Make("B2"); var box = await Box("c2", b2.Id, "1102"); var sales = await Gl("4101", 4);
        var cookie = await AsMember(Clerk);
        var draft = IdOf(await Post(cookie, Body("receipt", box, "branch-off-0001", Line(sales, 1m))));
        var waiting = IdOf(await Post(cookie, Body("receipt", box, "branch-off-0002", Line(sales, 2m))));
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, waiting, "submit", 1)).Status);
        Assert.True((await Domain.DeactivateAsync(Admin.Id, Company.Id, b2.Id, b2.Revision)).IsOk);

        Assert.Equal("branch_inactive", FieldCode(await Post(cookie, Body("receipt", box, "branch-off-0003", Line(sales, 1m)), HttpStatusCode.BadRequest), "cashAccountId"));
        Assert.Equal("branch_inactive", FieldCode(await Put(cookie, draft, 1, new { voucherDate = "2026-03-11", lines = new[] { Line(sales, 5m) } }), "cashAccountId"));
        Assert.Equal("branch_inactive", FieldCode(await Act(cookie, draft, "submit", 1), "cashAccountId"));
        Assert.Equal((2L, 2L, 3L, (short)1, 1L), (await Rows(CV.Table), await Rows(VL.Table), await Rows(VE.Table), await DbStatus(draft), await DbRevision(draft)));   // no refused request wrote anything
        Assert.Equal(HttpStatusCode.OK, (await Read(cookie, draft)).Status);                                   // still readable
        // cancelling needs no active branch: both are cancelled
        Assert.Equal("cancelled", StatusOf(await Act(cookie, draft, "cancel", 1, new { reason = "branch closed" })));
        Assert.Equal("cancelled", StatusOf(await Act(cookie, waiting, "cancel", 2, new { reason = "branch closed" })));
    }

    /// <summary>
    /// Review L4 (iii), choice (f): a voucher of S2a is not financial history, so it does not lock the base currency. The plan (5a) says what then holds: PostgreSQL and the service refuse to WRITE or SUBMIT a voucher whose
    /// currency is not the base currency. The plan is silent on the rest; the behaviour as built is pinned here: the draft stays readable in its own currency, and it can still be cancelled.
    /// </summary>
    [PostgresFact]
    public async Task After_a_change_of_the_base_currency_an_earlier_draft_is_neither_edited_nor_submitted_and_can_still_be_cancelled()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4);
        var cookie = await AsMember(Clerk);
        var draft = IdOf(await Post(cookie, Body("receipt", box, "base-change-001", Line(sales, 1.125m))));
        var second = IdOf(await Post(cookie, Body("payment", box, "base-change-002", Line(sales, 2m))));
        // the base currency moves from JOD to USD (nothing of S2a prevents it: no voucher is financial history)
        await Sql($"UPDATE {Cc.Table} SET {Cc.IsBase} = false, {Cc.Revision} = {Cc.Revision} + 1 WHERE {Cc.Code} = 'JOD'; INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('USD', true, true, 1, now())");

        object edit = new { voucherDate = "2026-03-11", lines = new[] { Line(sales, 5m) } };
        Assert.Equal("cash_account_currency_not_base", FieldCode(await Put(cookie, draft, 1, edit), "cashAccountId"));
        Assert.Equal("cash_account_currency_not_base", FieldCode(await Act(cookie, draft, "submit", 1), "cashAccountId"));
        Assert.Equal("cash_account_currency_not_base", FieldCode(await Post(cookie, Body("receipt", box, "base-change-003", Line(sales, 1m)), HttpStatusCode.BadRequest), "cashAccountId"));
        // PostgreSQL refuses the same by itself: the submission, and a new voucher in the old or in the new base currency on the account of the old one
        var by = Guid.NewGuid();
        Assert.Equal("cash_voucher_currency_not_base", await SqlErrorAsync($"{MoveSql(draft, 2, by.ToString())}; {EventSql(draft, 3, 1, 2, 2)}"));
        Assert.Equal("cash_voucher_currency_not_base", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "base-sql-0001")));
        Assert.Equal("cash_voucher_currency_not_base", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "base-sql-0002", currency: "USD")));
        Assert.Equal((2L, (short)1, 1L, 2L), (await Rows(CV.Table), await DbStatus(draft), await DbRevision(draft), await Rows(VL.Table)));
        // the draft is still read, in the currency it was written in, and listed
        var read = await Read(cookie, draft);
        Assert.Equal((HttpStatusCode.OK, "JOD", "draft", 1.125m), (read.Status, read.Json.GetProperty("currency").GetString(), StatusOf(read), read.Json.GetProperty("total").GetDecimal()));
        Assert.Equal(2, (await Send(HttpMethod.Get, V, cookie: cookie)).Json.GetProperty("vouchers").GetArrayLength());
        // and it can be cancelled (the only way out), by the service and by direct SQL
        Assert.Equal("cancelled", StatusOf(await Act(cookie, draft, "cancel", 1, new { reason = "base currency changed" })));
        Assert.Equal("", await SqlErrorAsync($"{MoveSql(second, 5)}; {EventSql(second, 5, 1, 5, 2)}"));
        // back in the old base currency the account takes vouchers again
        await Sql($"UPDATE {Cc.Table} SET {Cc.IsBase} = ({Cc.Code} = 'JOD'), {Cc.Revision} = {Cc.Revision} + 1");
        Assert.Equal(HttpStatusCode.Created, (await Post(cookie, Body("receipt", box, "base-change-004", Line(sales, 1m)))).Status);
    }

    [PostgresFact]
    public async Task The_party_is_an_optional_reference_that_must_exist()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4); var party = await Party("P1");
        var cookie = await AsMember(Clerk);
        var unknown = await Post(cookie, new { kind = "receipt", cashAccountId = box, voucherDate = "2026-03-10", partyId = Guid.NewGuid(), idempotencyKey = "party-key-0001", lines = new[] { Line(sales, 1m) } }, HttpStatusCode.BadRequest);
        Assert.Equal("party_not_found", FieldCode(unknown, "partyId"));
        Assert.Equal(0L, await Rows(CV.Table));
        var with = await Post(cookie, new { kind = "receipt", cashAccountId = box, voucherDate = "2026-03-10", partyId = party, idempotencyKey = "party-key-0002", lines = new[] { Line(sales, 1m) } });
        Assert.Equal(party, with.Json.GetProperty("partyId").GetGuid());
        var without = await Put(cookie, IdOf(with), 1, new { voucherDate = "2026-03-10", lines = new[] { Line(sales, 1m) } });
        Assert.Equal(System.Text.Json.JsonValueKind.Null, without.Json.GetProperty("partyId").ValueKind);
        Assert.Equal("submitted", StatusOf(await Act(cookie, IdOf(with), "submit", 2)));                      // a voucher without a party is complete
    }
}

public class CashVoucherIdempotencyTests : CashVoucherTestBase
{
    [PostgresFact]
    public async Task A_repeated_key_returns_the_original_voucher_another_payload_is_refused_and_a_retry_never_duplicates()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4);
        var cookie = await AsMember(Clerk);
        var body = Body("receipt", box, "retry-key-0001", Line(sales, 10m), Line(sales, 2.5m));
        var first = await Post(cookie, body);
        var again = await Post(cookie, body, HttpStatusCode.OK);                                               // a retry after a lost answer
        Assert.Equal((IdOf(first), true, "\"1\""), (IdOf(again), again.Json.GetProperty("replayed").GetBoolean(), Etag(again)));
        var different = await Post(cookie, Body("receipt", box, "retry-key-0001", Line(sales, 10m), Line(sales, 2.6m)), HttpStatusCode.Conflict);
        Assert.Equal("idempotency_key_reused", different.Error);
        Assert.Equal("idempotency_key_reused", (await Post(cookie, Body("payment", box, "retry-key-0001", Line(sales, 10m), Line(sales, 2.5m)), HttpStatusCode.Conflict)).Error);
        Assert.Equal((1L, 2L, 1L, 1L), (await Rows(CV.Table), await Rows(VL.Table), await Rows(VE.Table), await Events(CashVoucherAuditEvents.Created)));
        // the replay answers the CURRENT state of the original (here: after it moved on), and still writes nothing
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, IdOf(first), "submit", 1)).Status);
        var later = await Post(cookie, body, HttpStatusCode.OK);
        Assert.Equal(("submitted", 2L, true), (StatusOf(later), Rev(later), later.Json.GetProperty("replayed").GetBoolean()));
        Assert.Equal((1L, 2L), (await Rows(CV.Table), await Rows(VE.Table)));
        // the key belongs to its creator: another member's same key is another voucher
        var other = await AsMember(Clerk);
        Assert.NotEqual(IdOf(first), IdOf(await Post(other, body)));
        Assert.Equal(2L, await Rows(CV.Table));
        Assert.Contains("duplicate key", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, await Sql<Guid>($"SELECT {CV.CreatedBy} FROM {CV.Table} WHERE {CV.Id} = '{IdOf(first)}'"), "retry-key-0001")));   // PostgreSQL holds the uniqueness itself
    }

    [PostgresFact]
    public async Task Parallel_creates_with_one_key_produce_exactly_one_voucher()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4);
        var cookie = await AsMember(Clerk);
        var body = Body("payment", box, "parallel-key-01", Line(sales, 10m), Line(sales, 5m));
        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Send(HttpMethod.Post, V, body, cookie))));
        Assert.All(answers, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)r.Status} {r.Body}"));
        Assert.Equal(1, answers.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Single(answers.Select(IdOf).Distinct());
        Assert.All(answers.Where(r => r.Status == HttpStatusCode.OK), r => Assert.True(r.Json.GetProperty("replayed").GetBoolean()));
        Assert.Equal((1L, 2L, 1L, 1L), (await Rows(CV.Table), await Rows(VL.Table), await Rows(VE.Table), await Events(CashVoucherAuditEvents.Created)));
        // parallel creates with one key and DIFFERENT payloads: one voucher, every other answer is the refusal
        var mixed = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => Send(HttpMethod.Post, V, Body("payment", box, "parallel-key-02", Line(sales, i + 1m)), cookie))));
        Assert.Equal(1, mixed.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(mixed.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal((HttpStatusCode.Conflict, "idempotency_key_reused"), Refusal(r)));
        Assert.Equal(2L, await Rows(CV.Table));
    }
}

public class CashVoucherConcurrencyTests : CashVoucherTestBase
{
    private async Task<(string Cookie, Guid Box, Guid Sales)> Fixture()
    {
        await StartV();
        var main = await MainBranch();
        return (await AsMember(Clerk), await Box("c1", main, "1101"), await Gl("4101", 4));
    }

    private static void OneWinner(ApiResponse[] answers, params string[] refusals)
    {
        Assert.Equal(1, answers.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(answers.Where(r => r.Status != HttpStatusCode.OK), r => Assert.Contains($"{(int)r.Status}:{r.Error}", refusals));
    }

    [PostgresFact]
    public async Task Parallel_submits_of_one_revision_submit_once()
    {
        var (cookie, box, sales) = await Fixture();
        var id = IdOf(await Post(cookie, Body("receipt", box, "race-submit-01", Line(sales, 10m))));
        OneWinner(await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Act(cookie, id, "submit", 1)))), "412:revision_conflict");
        Assert.Equal(((short)2, 2L, 2L, 1L), (await DbStatus(id), await DbRevision(id), await Rows(VE.Table, $"{VE.VoucherId} = '{id}'"), await Events(CashVoucherAuditEvents.Submitted)));
    }

    [PostgresFact]
    public async Task Parallel_cancellations_of_a_submitted_voucher_cancel_it_once_and_it_never_returns_to_draft()
    {
        var (cookie, box, sales) = await Fixture();
        var id = IdOf(await Post(cookie, Body("receipt", box, "race-leave-001", Line(sales, 10m))));
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, id, "submit", 1)).Status);
        // cancellations race each other and requests for a withdrawal, which has no route: exactly one cancellation wins and nothing else is written
        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => i % 2 == 0 ? Act(cookie, id, "withdraw", 2) : Act(cookie, id, "cancel", 2, new { reason = $"race {i}" }))));
        Assert.All(answers.Where((_, i) => i % 2 == 0), r => Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{(int)r.Status} {r.Body}"));
        OneWinner([.. answers.Where((_, i) => i % 2 == 1)], "412:revision_conflict");
        Assert.Equal(((short)5, 3L, 3L), (await DbStatus(id), await DbRevision(id), await Rows(VE.Table, $"{VE.VoucherId} = '{id}'")));
        Assert.Equal((1L, 0L), (await Events(CashVoucherAuditEvents.Cancelled), await Events(2113)));
        var winner = answers.Single(r => r.Status == HttpStatusCode.OK);
        Assert.Equal(winner.Json.GetProperty("events")[2].GetProperty("reason").GetString(), await Sql<string>($"SELECT {VE.Reason} FROM {VE.Table} WHERE {VE.VoucherId} = '{id}' AND {VE.Kind} = 5"));
    }

    [PostgresFact]
    public async Task An_edit_racing_a_submit_or_a_cancel_never_leaves_a_submitted_voucher_with_other_lines_than_the_submitted_ones()
    {
        var (cookie, box, sales) = await Fixture();
        var id = IdOf(await Post(cookie, Body("receipt", box, "race-edit-0001", Line(sales, 10m))));
        object Edit(int i) => new { voucherDate = "2026-03-10", lines = new[] { Line(sales, 20m + i), Line(sales, 1m) } };
        var answers = await Task.WhenAll(Enumerable.Range(0, 9).Select(i => Task.Run(() => (i % 3) switch { 0 => Put(cookie, id, 1, Edit(i)), 1 => Act(cookie, id, "submit", 1), _ => Act(cookie, id, "cancel", 1, new { reason = "race" }) })));
        OneWinner(answers, "412:revision_conflict");
        Assert.Equal((2L, 2L), (await DbRevision(id), await Rows(VE.Table, $"{VE.VoucherId} = '{id}'")));
        var linesRevision = await Sql<long>($"SELECT {CV.LinesRevision} FROM {CV.Table} WHERE {CV.Id} = '{id}'");
        var status = await DbStatus(id);
        // an edit won: two line sets, the voucher is a draft naming the second; a submit or a cancel won: the one original set, untouched
        Assert.Equal(status == 1 ? (2L, 3L) : (1L, 1L), (linesRevision, await Rows(VL.Table, $"{VL.VoucherId} = '{id}'")));
        Assert.Equal(1L, await Rows(VL.Table, $"{VL.VoucherId} = '{id}' AND {VL.Revision} = 1"));
    }
}

public class CashVoucherAccessTests : CashVoucherTestBase
{
    [PostgresFact]
    public async Task A_branch_scoped_user_sees_and_changes_only_the_vouchers_of_that_branch_and_another_branch_answers_like_an_unknown_id()
    {
        await StartV();
        var a = await MainBranch(); var b = (await Make("B2")).Id;
        var boxA = await Box("ca", a, "1101"); var boxB = await Box("cb", b, "1102"); var sales = await Gl("4101", 4);
        var wide = await AsMember(Clerk);
        var inA = IdOf(await Post(wide, Body("receipt", boxA, "branch-key-a001", Line(sales, 1m))));
        var inB = IdOf(await Post(wide, Body("receipt", boxB, "branch-key-b001", Line(sales, 1m))));

        var cookie = await AsMember(Clerk, ScopeKind.Branch, a);
        var list = await Send(HttpMethod.Get, V, cookie: cookie);
        Assert.Equal([inA], list.Json.GetProperty("vouchers").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(0, (await Send(HttpMethod.Get, $"{V}?branchId={b}", cookie: cookie)).Json.GetProperty("vouchers").GetArrayLength());   // a filter never widens the scope
        Assert.Equal(HttpStatusCode.OK, (await Read(cookie, inA)).Status);
        var other = await Read(cookie, inB); var unknown = await Read(cookie, Guid.NewGuid());
        Assert.Equal((HttpStatusCode.NotFound, "cash_voucher_not_found"), Refusal(other));
        Assert.Equal(Refusal(other), Refusal(unknown));                                                        // no probing of other branches
        object edit = new { voucherDate = "2026-03-10", lines = new[] { Line(sales, 9m) } };
        Assert.Equal(Refusal(unknown), Refusal(await Put(cookie, inB, 1, edit)));
        foreach (var action in new[] { "submit", "cancel" }) Assert.Equal(Refusal(unknown), Refusal(await Act(cookie, inB, action, 1, new { reason = "x" })));
        Assert.Equal((1L, (short)1), (await DbRevision(inB), await DbStatus(inB)));
        // a voucher on an account of a branch the caller does not hold: exactly the answer of an unknown account, and nothing is written
        var foreign = await Post(cookie, Body("receipt", boxB, "branch-key-b002", Line(sales, 1m)), HttpStatusCode.BadRequest);
        var missing = await Post(cookie, Body("receipt", Guid.NewGuid(), "branch-key-b003", Line(sales, 1m)), HttpStatusCode.BadRequest);
        Assert.Equal("cash_account_not_found", FieldCode(foreign, "cashAccountId"));
        Assert.Equal(foreign.Body, missing.Body);
        Assert.Equal(2L, await Rows(CV.Table));
        // the same grant works on its own branch
        Assert.Equal(HttpStatusCode.OK, (await Put(cookie, inA, 1, edit)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, inA, "submit", 2)).Status);
        Assert.Equal(HttpStatusCode.Created, (await Post(cookie, Body("payment", boxA, "branch-key-a002", Line(sales, 1m)))).Status);
    }

    [PostgresFact]
    public async Task The_permissions_are_separate_and_a_member_without_one_is_forbidden_before_anything_is_looked_up()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4);
        var all = await AsMember(Clerk);
        var id = IdOf(await Post(all, Body("receipt", box, "perm-key-00001", Line(sales, 1m))));
        object edit = new { voucherDate = "2026-03-10", lines = new[] { Line(sales, 2m) } };
        var create = Body("receipt", box, "perm-key-00002", Line(sales, 1m));
        const string View = PermissionCatalog.Codes.CashVouchersView;

        async Task Forbidden(string cookie, bool get = false, bool post = false, bool put = false, bool submit = false, bool cancel = false)
        {
            if (get) { Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, V, cookie: cookie)).Status); Assert.Equal(HttpStatusCode.Forbidden, (await Read(cookie, id)).Status); Assert.Equal(HttpStatusCode.Forbidden, (await Read(cookie, Guid.NewGuid())).Status); }
            if (post) Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), Refusal(await Send(HttpMethod.Post, V, create, cookie)));
            if (put) { Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), Refusal(await Put(cookie, id, 1, edit))); Assert.Equal(HttpStatusCode.Forbidden, (await Put(cookie, Guid.NewGuid(), 1, edit)).Status); }
            if (submit) Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), Refusal(await Act(cookie, id, "submit", 1)));
            if (cancel) Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), Refusal(await Act(cookie, id, "cancel", 1, new { reason = "x" })));
        }
        await Forbidden(await AsMember([PermissionCatalog.Codes.WarehousesView]), get: true, post: true, put: true, submit: true, cancel: true);
        await Forbidden(await AsMember(Manage), get: true, post: true, put: true, submit: true, cancel: true);    // managing cash ACCOUNTS grants nothing on vouchers
        await Forbidden(await AsMember([View]), post: true, put: true, submit: true, cancel: true);
        await Forbidden(await AsMember([View, PermissionCatalog.Codes.CashVouchersCreate]), put: true, submit: true, cancel: true);
        await Forbidden(await AsMember([View, PermissionCatalog.Codes.CashVouchersUpdate]), post: true, submit: true, cancel: true);
        await Forbidden(await AsMember([View, PermissionCatalog.Codes.CashVouchersSubmit]), post: true, put: true, cancel: true);
        await Forbidden(await AsMember([View, PermissionCatalog.Codes.CashVouchersCancel]), post: true, put: true, submit: true);
        Assert.Equal((1L, 1L, (short)1), (await Rows(CV.Table), await DbRevision(id), await DbStatus(id)));       // no refused request wrote anything

        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, V, create, await AsMember([PermissionCatalog.Codes.CashVouchersCreate]))).Status);   // create needs no view
        Assert.Equal(HttpStatusCode.OK, (await Put(await AsMember([View, PermissionCatalog.Codes.CashVouchersUpdate]), id, 1, edit)).Status);
        var submitter = await AsMember([View, PermissionCatalog.Codes.CashVouchersSubmit]);
        Assert.Equal(HttpStatusCode.OK, (await Act(submitter, id, "submit", 2)).Status);
        Assert.True((await Act(submitter, id, "withdraw", 3)).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);   // the submit permission opens no way back to draft: the route does not exist
        Assert.Equal(HttpStatusCode.OK, (await Act(await AsMember([View, PermissionCatalog.Codes.CashVouchersCancel]), id, "cancel", 3, new { reason = "x" })).Status);
    }

    [PostgresFact]
    public async Task A_replayed_create_answers_only_the_identifiers_to_a_caller_who_may_not_view_the_voucher()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4); var party = await Party("P1");
        var creator = await AsMember([PermissionCatalog.Codes.CashVouchersCreate]);
        var body = new { kind = "receipt", cashAccountId = box, voucherDate = "2026-03-10", partyId = party, reference = "secret", note = "private", idempotencyKey = "redact-key-001", lines = new[] { Line(sales, 12m) } };
        var first = await Post(creator, body);
        var replay = await Post(creator, body, HttpStatusCode.OK);
        Assert.Equal((IdOf(first), true, 0m, 0, 0), (IdOf(replay), replay.Json.GetProperty("replayed").GetBoolean(), replay.Json.GetProperty("total").GetDecimal(), replay.Json.GetProperty("lines").GetArrayLength(), replay.Json.GetProperty("events").GetArrayLength()));
        foreach (var hidden in new[] { "partyId", "reference", "note" }) Assert.Equal(System.Text.Json.JsonValueKind.Null, replay.Json.GetProperty(hidden).ValueKind);
        Assert.DoesNotContain("secret", replay.Body); Assert.DoesNotContain("private", replay.Body);
    }

    [PostgresFact]
    public async Task Another_company_never_sees_the_vouchers_and_nothing_is_visible_without_a_selected_company()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4);
        var holder = await Holder(Clerk);
        var id = IdOf(await Post(await SignIn(holder), Body("receipt", box, "company-key-01", Line(sales, 1m))));
        var foreign = await ReadyCompany();
        var resolver = SinglePrincipalResolver(foreign);
        var service = new CashVoucherService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), Clock);
        Assert.Empty(await service.ListAsync(foreign.Id, new CashVoucherFilter()));
        Assert.Null(await service.GetAsync(foreign.Id, id));
        // the other company's database knows neither the voucher nor its cash account: nothing of this company can be referenced from there
        Assert.Equal((ConfigStatus.NotFound, "cash_voucher_not_found"), ((await service.SubmitAsync(holder.Id, foreign.Id, id, 1)).Status, (await service.SubmitAsync(holder.Id, foreign.Id, id, 1)).Code));
        var cross = await service.CreateAsync(holder.Id, foreign.Id, new CashVoucherInput("receipt", box, new DateOnly(2026, 3, 10), null, null, null, "company-key-02", [new CashVoucherLineInput(sales, 1m, null)]));
        Assert.False(cross.IsOk);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CV.Table}", foreign));
        var none = await SignIn(await Holder(Clerk), selectCompany: false);
        Assert.Equal((HttpStatusCode.Conflict, "company_not_selected"), Refusal(await Send(HttpMethod.Get, V, cookie: none)));
        Assert.Equal((HttpStatusCode.Conflict, "company_not_selected"), Refusal(await Send(HttpMethod.Post, V, Body("receipt", box, "company-key-03", Line(sales, 1m)), none)));
        Assert.Equal(1L, await Rows(CV.Table));
    }
}

/// <summary>What PostgreSQL itself refuses, by direct SQL as the owner of the disposable database (the application is not involved): the state machine, the immutability, the append-only line sets and history.</summary>
public class CashVoucherGuardTests : CashVoucherTestBase
{
    private Guid main, box, sales, by;

    private async Task<Guid> Draft(string key, bool line = true)
    {
        var id = Guid.NewGuid();
        Assert.Equal("", await SqlErrorAsync($"{VoucherSql(id, box, main, by, key)}; {(line ? LineSql(id, 1, 1, sales) + "; " : "")}{EventSql(id, 1, null, 1, 1)}"));
        return id;
    }
    private async Task<Guid> Submitted(string key)
    {
        var id = await Draft(key);
        Assert.Equal("", await SqlErrorAsync($"{MoveSql(id, 2, by.ToString())}; {EventSql(id, 3, 1, 2, 2)}"));
        return id;
    }
    private async Task Setup()
    {
        await StartV();
        main = await MainBranch(); box = await Box("c1", main, "1101"); sales = await Gl("4101", 4); by = Guid.NewGuid();
    }

    [PostgresFact]
    public async Task PostgreSQL_admits_only_the_transitions_of_the_slice_and_refuses_approval_rejection_and_every_other_state()
    {
        await Setup();
        var draft = await Draft("guard-key-0001"); var submitted = await Submitted("guard-key-0002");
        var cancelled = await Draft("guard-key-0003");
        Assert.Equal("", await SqlErrorAsync($"{MoveSql(cancelled, 5)}; {EventSql(cancelled, 5, 1, 5, 2)}"));
        const string Illegal = "illegal cash voucher status transition";
        foreach (var later in new short[] { 3, 4, 6, 7, 8, 9 })   // approved, rejected and anything a later slice may add
        {
            Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(draft, later)));
            Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(submitted, later, by.ToString())));
            Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(cancelled, later)));
        }
        Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(submitted, 2, by.ToString())));                      // not submitted twice
        // submitted -> draft (a withdrawal) is not a transition of the approved state machine: refused whatever the submitter column and whatever event is offered with it
        Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(submitted, 1)));
        Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(submitted, 1, by.ToString())));
        foreach (var (kind, from, to) in new (short, short?, short)[] { (4, 2, 1), (2, 1, 1), (2, 2, 1), (1, null, 1) }) Assert.Equal(Illegal, await SqlErrorAsync($"{MoveSql(submitted, 1)}; {EventSql(submitted, kind, from, to, 3)}"));
        foreach (var back in new short[] { 1, 2, 5 }) Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(cancelled, back, back == 2 ? by.ToString() : null)));   // cancelled is final
        Assert.Equal(Illegal, await SqlErrorAsync(MoveSql(draft, 10)));
        // the insert: a voucher starts as a draft at revision 1, without a submitter
        Assert.Equal("a cash voucher is created as a draft", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "guard-key-0010", status: 2, submittedBy: by.ToString())));
        Assert.Equal("a cash voucher is created as a draft", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "guard-key-0011", status: 3)));
        Assert.Equal("a cash voucher is created at revision 1", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "guard-key-0012", revision: 2)));
        Assert.Equal("a draft has no submitter", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "guard-key-0013", submittedBy: by.ToString())));
        Assert.Equal("the creator is the first editor of a cash voucher", await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "guard-key-0014", updatedBy: Guid.NewGuid())));
        // the revision advances by exactly one; the submitter rules
        Assert.Equal("revision must advance by one", await SqlErrorAsync($"UPDATE {CV.Table} SET {CV.Status} = 2, {CV.SubmittedBy} = '{by}' WHERE {CV.Id} = '{draft}'"));
        Assert.Equal("revision must advance by one", await SqlErrorAsync($"UPDATE {CV.Table} SET {CV.Status} = 2, {CV.SubmittedBy} = '{by}', {CV.Revision} = {CV.Revision} + 2 WHERE {CV.Id} = '{draft}'"));
        Assert.Equal("a submitted cash voucher names its submitter", await SqlErrorAsync(MoveSql(draft, 2)));
        Assert.Equal("a draft has no submitter", await SqlErrorAsync($"UPDATE {CV.Table} SET {CV.SubmittedBy} = '{by}', {CV.Note} = 'x', {CV.Revision} = {CV.Revision} + 1, {CV.LinesRevision} = {CV.Revision} + 1 WHERE {CV.Id} = '{draft}'"));
        Assert.Equal("cancellation keeps the submitter record", await SqlErrorAsync(MoveSql(submitted, 5)));
        // a status change edits nothing; a submitted voucher is not edited at all (an "edit" keeps the status, which submitted -> submitted is not)
        foreach (var edit in new[] { $", {CV.VoucherDate} = '2026-04-01'", $", {CV.Note} = 'x'", $", {CV.Reference} = 'x'", $", {CV.UpdatedBy} = gen_random_uuid()", $", {CV.LinesRevision} = 2" })
            Assert.Equal("a status change does not edit the cash voucher", await SqlErrorAsync(MoveSql(draft, 2, by.ToString(), edit)));
        Assert.Equal(Illegal, await SqlErrorAsync($"UPDATE {CV.Table} SET {CV.Note} = 'x', {CV.Revision} = {CV.Revision} + 1, {CV.LinesRevision} = {CV.Revision} + 1 WHERE {CV.Id} = '{submitted}'"));
        Assert.Equal("an edit of a draft writes a new line set under the new revision", await SqlErrorAsync($"UPDATE {CV.Table} SET {CV.Note} = 'x', {CV.Revision} = {CV.Revision} + 1 WHERE {CV.Id} = '{draft}'"));
        // a draft without a line is not submitted
        var empty = await Draft("guard-key-0020", line: false);
        Assert.Equal("cash_voucher_lines_required", await SqlErrorAsync(MoveSql(empty, 2, by.ToString())));
        Assert.Equal(((short)1, 1L, (short)2, 2L, (short)5, 2L), (await DbStatus(draft), await DbRevision(draft), await DbStatus(submitted), await DbRevision(submitted), await DbStatus(cancelled), await DbRevision(cancelled)));
    }

    [PostgresFact]
    public async Task The_identity_of_a_voucher_is_immutable_and_vouchers_lines_and_events_are_never_removed_or_rewritten()
    {
        await Setup();
        var other = await Box("c2", main, "1102"); var b2 = (await Make("B2")).Id;
        var id = await Draft("identity-key-01");
        foreach (var change in new[] { $"{CV.Id} = gen_random_uuid()", $"{CV.Kind} = 2", $"{CV.CashAccountId} = '{other}'", $"{CV.BranchId} = '{b2}'", $"{CV.Currency} = 'USD'", $"{CV.CreatedBy} = gen_random_uuid()",
                     $"{CV.CreatedAtUtc} = now() - interval '1 day'", $"{CV.IdempotencyKey} = 'another-key-01'", $"{CV.PayloadHash} = '{new string('b', 64)}'" })
            Assert.Equal("the identity of a cash voucher is immutable", await SqlErrorAsync($"UPDATE {CV.Table} SET {change}, {CV.Revision} = {CV.Revision} + 1, {CV.LinesRevision} = {CV.Revision} + 1 WHERE {CV.Id} = '{id}'"));
        Assert.Equal("cash vouchers are never removed", await SqlErrorAsync($"DELETE FROM {CV.Table} WHERE {CV.Id} = '{id}'"));
        Assert.Contains(await SqlErrorAsync($"TRUNCATE {CV.Table} CASCADE"), new[] { "cash vouchers are never removed", "cash voucher lines are append-only", "cash voucher events are append-only" });   // whichever of the three guards fires first
        Assert.NotEqual("", await SqlErrorAsync($"TRUNCATE {CV.Table}"));
        foreach (var statement in new[] { $"UPDATE {VL.Table} SET {VL.Amount} = 99", $"UPDATE {VL.Table} SET {VL.AccountId} = {VL.AccountId}", $"DELETE FROM {VL.Table}", $"TRUNCATE {VL.Table}" })
            Assert.Equal("cash voucher lines are append-only", await SqlErrorAsync(statement));
        foreach (var statement in new[] { $"UPDATE {VE.Table} SET {VE.Reason} = 'rewritten'", $"UPDATE {VE.Table} SET {VE.ActorUserId} = gen_random_uuid()", $"DELETE FROM {VE.Table}", $"TRUNCATE {VE.Table}" })
            Assert.Equal("cash voucher events are append-only", await SqlErrorAsync(statement));
        Assert.Equal((1L, 1L, 1L, 10m), (await Rows(CV.Table), await Rows(VL.Table), await Rows(VE.Table), await Sql<decimal>($"SELECT {VL.Amount} FROM {VL.Table}")));
    }

    [PostgresFact]
    public async Task A_line_set_is_written_once_for_the_current_revision_of_a_draft_and_the_history_is_complete_and_cannot_be_forged()
    {
        await Setup();
        var draft = await Draft("lines-key-0001"); var submitted = await Submitted("lines-key-0002");
        // the set of a recorded revision is closed: no line is slipped into a draft without a new revision
        Assert.Equal("the line set of a recorded revision is closed", await SqlErrorAsync(LineSql(draft, 1, 2, sales)));
        Assert.Equal("a line set carries the current revision of its cash voucher", await SqlErrorAsync(LineSql(draft, 2, 1, sales)));
        Assert.Equal("the lines of a cash voucher are written only while it is a draft", await SqlErrorAsync(LineSql(submitted, 2, 1, sales)));
        Assert.Equal("the lines of a cash voucher are written only while it is a draft", await SqlErrorAsync(LineSql(submitted, 1, 2, sales)));
        var zero = Guid.NewGuid(); var negative = Guid.NewGuid();
        Assert.Contains("violates check constraint", await SqlErrorAsync($"{VoucherSql(zero, box, main, by, "lines-key-0003")}; {LineSql(zero, 1, 1, sales, "0")}"));
        Assert.Contains("violates check constraint", await SqlErrorAsync($"{VoucherSql(negative, box, main, by, "lines-key-0004")}; {LineSql(negative, 1, 1, sales, "-1")}"));
        // every revision has its event at commit: a voucher or a change without one is refused when the transaction ends
        const string NoEvent = "every revision of a cash voucher has its history event";
        Assert.Equal(NoEvent, await SqlErrorAsync(VoucherSql(Guid.NewGuid(), box, main, by, "lines-key-0005")));
        Assert.Equal(NoEvent, await SqlErrorAsync(MoveSql(draft, 2, by.ToString())));
        Assert.Equal(NoEvent, await SqlErrorAsync(MoveSql(submitted, 5, by.ToString())));
        // an event carries the CURRENT revision and status of its voucher, is the one event of its revision, and is one of the events of the slice
        const string Current = "a cash voucher event must carry the current revision and status of its voucher";
        Assert.Equal(Current, await SqlErrorAsync(EventSql(draft, 3, 1, 2, 2)));                                // a future submit
        Assert.Equal(Current, await SqlErrorAsync(EventSql(submitted, 5, 2, 5, 3)));                            // a cancellation that did not happen
        Assert.Equal(Current, await SqlErrorAsync(EventSql(Guid.NewGuid(), 1, null, 1, 1)));                    // no such voucher
        Assert.Contains("duplicate key", await SqlErrorAsync(EventSql(submitted, 3, 1, 2, 2)));
        Assert.Equal("the first event of a cash voucher is its creation", await SqlErrorAsync(EventSql(draft, 2, 1, 1, 1)));
        foreach (var (kind, from, to) in new (short, short?, short)[] { (6, 2, 3), (7, 2, 4), (8, 3, 3), (9, 3, 5), (3, 2, 2), (5, 5, 5), (1, 1, 1), (4, 1, 1), (4, 2, 1), (2, 2, 1) })   // approved, rejected, journal created, reversed, the unused kind 4 (withdrawn) and mismatched pairs
            Assert.Equal("unknown cash voucher event", await SqlErrorAsync(EventSql(submitted, kind, from, to, 2)));
        // a proper edit by SQL: new revision, new set, event; then the old set stays closed and the new one closes with its event
        Assert.Equal("", await SqlErrorAsync($"UPDATE {CV.Table} SET {CV.Note} = 'edited', {CV.Revision} = {CV.Revision} + 1, {CV.LinesRevision} = {CV.Revision} + 1 WHERE {CV.Id} = '{draft}'; {LineSql(draft, 2, 1, sales, "7")}; {LineSql(draft, 2, 2, sales, "8")}; {EventSql(draft, 2, 1, 1, 2)}"));
        Assert.Equal("the line set of a recorded revision is closed", await SqlErrorAsync(LineSql(draft, 2, 3, sales)));
        Assert.Equal("a line set carries the current revision of its cash voucher", await SqlErrorAsync(LineSql(draft, 1, 2, sales)));
        Assert.Equal((1L, 2L, 2L), (await Rows(VL.Table, $"{VL.VoucherId} = '{draft}' AND {VL.Revision} = 1"), await Rows(VL.Table, $"{VL.VoucherId} = '{draft}' AND {VL.Revision} = 2"), await Rows(VE.Table, $"{VE.VoucherId} = '{draft}'")));
        Assert.Equal((1L, 2L), (await DbRevision(await Draft("lines-key-0009")), await DbRevision(submitted)));
    }

    [PostgresFact]
    public async Task A_cancellation_event_carries_a_reason_and_an_event_starts_at_the_status_the_previous_event_ended_at()
    {
        await Setup();
        var draft = await Draft("event-key-0001"); var submitted = await Submitted("event-key-0002");
        // review L2 (i): PostgreSQL itself requires the reason of a cancellation (missing, empty or blank), for a draft and for a submitted voucher
        const string NoReason = "a cancellation event carries its reason";
        foreach (var reason in new[] { "NULL", "''", "'   '", "E'\\t \\n'" })
        {
            Assert.Equal(NoReason, await SqlErrorAsync($"{MoveSql(draft, 5)}; {EventSql(draft, 5, 1, 5, 2, reason)}"));
            Assert.Equal(NoReason, await SqlErrorAsync($"{MoveSql(submitted, 5, by.ToString())}; {EventSql(submitted, 5, 2, 5, 3, reason)}"));
        }
        // review L2 (ii): a change is not recorded as another one. The event starts at the status the previous event ended at, so a cancellation of a SUBMITTED voucher is not recorded as the cancellation of a draft, nor the reverse
        const string WrongStart = "a cash voucher event starts at the status its voucher had before the change";
        Assert.Equal(WrongStart, await SqlErrorAsync($"{MoveSql(submitted, 5, by.ToString())}; {EventSql(submitted, 5, 1, 5, 3)}"));
        Assert.Equal(WrongStart, await SqlErrorAsync($"{MoveSql(draft, 5)}; {EventSql(draft, 5, 2, 5, 2)}"));
        // with the status pairs of the slice the kind is thereby the change that happened: a submission or a cancellation is not recorded as an edit, an edit not as a submission
        const string Current = "a cash voucher event must carry the current revision and status of its voucher";
        Assert.Equal(Current, await SqlErrorAsync($"{MoveSql(draft, 2, by.ToString())}; {EventSql(draft, 2, 1, 1, 2)}"));
        Assert.Equal(Current, await SqlErrorAsync($"{MoveSql(draft, 5)}; {EventSql(draft, 2, 1, 1, 2)}"));
        Assert.Equal(Current, await SqlErrorAsync($"UPDATE {CV.Table} SET {CV.Note} = 'x', {CV.Revision} = {CV.Revision} + 1, {CV.LinesRevision} = {CV.Revision} + 1 WHERE {CV.Id} = '{draft}'; {EventSql(draft, 3, 1, 2, 2)}"));
        Assert.Equal("unknown cash voucher event", await SqlErrorAsync($"{MoveSql(submitted, 5, by.ToString())}; {EventSql(submitted, 3, 2, 5, 3)}"));
        Assert.Equal(((short)1, 1L, 1L, (short)2, 2L, 2L), (await DbStatus(draft), await DbRevision(draft), await Rows(VE.Table, $"{VE.VoucherId} = '{draft}'"), await DbStatus(submitted), await DbRevision(submitted), await Rows(VE.Table, $"{VE.VoucherId} = '{submitted}'")));
        // the true records are accepted, and a reason on another event stays allowed (the guard requires one only for a cancellation)
        Assert.Equal("", await SqlErrorAsync($"{MoveSql(submitted, 5, by.ToString())}; {EventSql(submitted, 5, 2, 5, 3, "'duplicate of R-9'")}"));
        Assert.Equal("", await SqlErrorAsync($"{MoveSql(draft, 5)}; {EventSql(draft, 5, 1, 5, 2, "'not needed'")}"));
        Assert.Equal(("duplicate of R-9", "not needed"), (await Sql<string>($"SELECT {VE.Reason} FROM {VE.Table} WHERE {VE.VoucherId} = '{submitted}' AND {VE.Kind} = 5"), await Sql<string>($"SELECT {VE.Reason} FROM {VE.Table} WHERE {VE.VoucherId} = '{draft}' AND {VE.Kind} = 5")));
    }

    [PostgresFact]
    public async Task A_cash_account_is_frozen_once_a_voucher_refers_to_it_and_is_not_deactivated_while_one_of_its_vouchers_is_open()
    {
        await Setup();
        var b2 = (await Make("B2")).Id; var freeGl = await Gl("1109");
        var manager = await AccountManager(); var clerk = await AsMember(Clerk);
        var ownGl = await Sql<Guid>($"SELECT {Ca.AccountId} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'");
        object Account(Guid branch, Guid gl, string en = "Main cash") => new { kind = "cashbox", code = "C1", nameAr = "الصندوق الرئيسي", nameEn = en, branchId = branch, accountId = gl };
        // before any voucher: the binding may still change (and is changed back)
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/{box}", Account(main, freeGl), manager, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/{box}", Account(main, ownGl), manager, ifMatch: "\"2\"")).Status);

        var first = IdOf(await Post(clerk, Body("receipt", box, "freeze-key-0001", Line(sales, 1m))));
        var second = IdOf(await Post(clerk, Body("payment", box, "freeze-key-0002", Line(sales, 1m))));
        Assert.Equal(HttpStatusCode.OK, (await Act(clerk, second, "submit", 1)).Status);
        Assert.Equal((HttpStatusCode.Conflict, "binding_frozen"), Refusal(await Send(HttpMethod.Put, $"{Base}/{box}", Account(main, freeGl), manager, ifMatch: "\"3\"")));
        Assert.Equal((HttpStatusCode.Conflict, "binding_frozen"), Refusal(await Send(HttpMethod.Put, $"{Base}/{box}", Account(b2, ownGl), manager, ifMatch: "\"3\"")));
        Assert.Equal((HttpStatusCode.Conflict, "cash_account_has_open_documents"), Refusal(await Send(HttpMethod.Post, $"{Base}/{box}/deactivate", new { }, manager, ifMatch: "\"3\"")));
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/{box}", Account(main, ownGl, "Renamed"), manager, ifMatch: "\"3\"")).Status);   // what is not the binding still changes
        // PostgreSQL holds both rules itself
        string Change(string set) => $"UPDATE {Ca.Table} SET {set}, {Ca.Revision} = {Ca.Revision} + 1 WHERE {Ca.Id} = '{box}'";
        Assert.Equal("cash_account_binding_frozen", await SqlErrorAsync(Change($"{Ca.AccountId} = '{freeGl}'")));
        Assert.Equal("cash_account_binding_frozen", await SqlErrorAsync(Change($"{Ca.BranchId} = '{b2}'")));
        Assert.Equal("cash_account_has_open_documents", await SqlErrorAsync(Change($"{Ca.IsActive} = false")));

        // one open voucher is enough; once every voucher is cancelled the account may be deactivated, and its binding stays frozen for good
        Assert.Equal(HttpStatusCode.OK, (await Act(clerk, first, "cancel", 1, new { reason = "x" })).Status);
        Assert.Equal((HttpStatusCode.Conflict, "cash_account_has_open_documents"), Refusal(await Send(HttpMethod.Post, $"{Base}/{box}/deactivate", new { }, manager, ifMatch: "\"4\"")));
        Assert.Equal(HttpStatusCode.OK, (await Act(clerk, second, "cancel", 2, new { reason = "x" })).Status);
        Assert.Equal("cash_account_binding_frozen", await SqlErrorAsync(Change($"{Ca.AccountId} = '{freeGl}'")));
        Assert.Equal((HttpStatusCode.Conflict, "binding_frozen"), Refusal(await Send(HttpMethod.Put, $"{Base}/{box}", Account(main, freeGl, "Renamed"), manager, ifMatch: "\"4\"")));
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{box}/deactivate", new { }, manager, ifMatch: "\"4\"")).Status);
        Assert.Equal("cash_account_inactive", FieldCode(await Post(clerk, Body("receipt", box, "freeze-key-0003", Line(sales, 1m)), HttpStatusCode.BadRequest), "cashAccountId"));
        Assert.Equal((ownGl, main, false), (await Sql<Guid>($"SELECT {Ca.AccountId} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"), await Sql<Guid>($"SELECT {Ca.BranchId} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"), await Sql<bool>($"SELECT {Ca.IsActive} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'")));
    }
}

/// <summary>D-214: the whole life of a voucher in this slice leaves NO row in any journal, ledger, numbering or other table: only the three voucher tables and the business audit stream change.</summary>
public class CashVoucherFootprintTests : CashVoucherTestBase
{
    private const string Counts =
        "SELECT COALESCE(string_agg(c.oid::regclass::text || '=' || (xpath('/row/n/text()', query_to_xml(format('SELECT count(*) AS n FROM %s', c.oid::regclass), false, true, '')))[1]::text, ';' ORDER BY c.oid::regclass::text), '') " +
        "FROM pg_class c JOIN pg_namespace s ON s.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND s.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')";

    private static Dictionary<string, long> Parse(string counts) => counts.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).ToDictionary(p => p[0].Split('.')[^1].Trim('"'), p => long.Parse(p[1]));
    private async Task<Dictionary<string, long>> CompanyCounts() => Parse(await Sql<string>(Counts));
    private async Task<Dictionary<string, long>> SystemCounts()
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var q = new NpgsqlCommand(Counts, conn);
        return Parse((string)(await q.ExecuteScalarAsync())!);
    }
    private static Dictionary<string, long> Changed(Dictionary<string, long> before, Dictionary<string, long> after) =>
        after.Where(kv => before.GetValueOrDefault(kv.Key, -1) != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value - before.GetValueOrDefault(kv.Key));

    /// <summary>
    /// Review L4 (i): the CONTENT of every table, not only its row count: per table "rows:hash", the hash being the MD5 of the ordered MD5s of the text form of each row (stable, independent of the physical order), so an
    /// UPDATE of an existing row anywhere (a balance, a counter, a number, a journal row) is seen although no count moves.
    /// </summary>
    private const string Fingerprints =
        "SELECT COALESCE(string_agg(c.oid::regclass::text || '=' || (xpath('/row/n/text()', query_to_xml(format('SELECT count(*)::text || '':'' || COALESCE(md5(string_agg(md5(x::text), '''' ORDER BY md5(x::text))), ''-'') AS n FROM %s x', c.oid::regclass), false, true, '')))[1]::text, ';' ORDER BY c.oid::regclass::text), '') " +
        "FROM pg_class c JOIN pg_namespace s ON s.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND s.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')";
    private async Task<Dictionary<string, string>> CompanyContent() =>
        (await Sql<string>(Fingerprints)).Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).ToDictionary(p => p[0].Split('.')[^1].Trim('"'), p => p[1]);

    [PostgresFact]
    public async Task The_whole_life_of_a_voucher_writes_only_voucher_rows_and_audit_events_and_no_journal_ledger_or_number_row()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4); var rent = await Gl("5101", 5); var party = await Party("P1");
        var cookie = await AsMember(Clerk);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, V, cookie: cookie)).Status);              // the session is in use before the snapshot
        var company = await CompanyCounts(); var system = await SystemCounts(); var content = await CompanyContent();
        Assert.True(company.Count > 60, $"only {company.Count} company tables were counted");
        Assert.Equal(company.Keys.Order(), content.Keys.Order());
        string[] ledger = [CompanySchema.JournalEntry.Table, CompanySchema.JournalLine.Table, CompanySchema.JournalAllocation.Table, CompanySchema.JournalEvent.Table, CompanySchema.NumberIssue.Table, CompanySchema.NumberCounter.Table, CompanySchema.NumberingRule.Table];
        Assert.All(ledger, t => Assert.True(company.ContainsKey(t), t));
        string[] own = [CV.Table, VL.Table, VE.Table, CompanySchema.BusinessAudit.Table];

        // a receipt: created, edited, submitted and left submitted; a payment: created, replayed, submitted and cancelled; a draft cancelled; refused requests in between
        var receipt = IdOf(await Post(cookie, new { kind = "receipt", cashAccountId = box, voucherDate = "2026-03-10", partyId = party, reference = "R-1", idempotencyKey = "footprint-key-1", lines = new[] { Line(sales, 100m), Line(sales, 15.5m) } }));
        Assert.Equal(HttpStatusCode.OK, (await Put(cookie, receipt, 1, new { voucherDate = "2026-03-11", partyId = party, lines = new[] { Line(sales, 120m) } })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, receipt, "submit", 2)).Status);
        var paymentBody = Body("payment", box, "footprint-key-2", Line(rent, 40m));
        var payment = IdOf(await Post(cookie, paymentBody));
        Assert.Equal(payment, IdOf(await Post(cookie, paymentBody, HttpStatusCode.OK)));
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, payment, "submit", 1)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, payment, "cancel", 2, new { reason = "wrong amount" })).Status);
        var draft = IdOf(await Post(cookie, Body("payment", box, "footprint-key-3")));
        Assert.Equal(HttpStatusCode.OK, (await Act(cookie, draft, "cancel", 1, new { reason = "not needed" })).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Act(cookie, receipt, "cancel", 1, new { reason = "stale" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, V, Body("receipt", box, "footprint-key-4", Line(sales, 0m)), cookie)).Status);

        var changed = Changed(company, await CompanyCounts());
        // 3 vouchers; line sets 2 + 1 (receipt) + 1 (payment) + 0; events 3 + 3 + 2; one audit event per history event
        Assert.Equal(new Dictionary<string, long> { [CV.Table] = 3, [VL.Table] = 4, [VE.Table] = 8, [CompanySchema.BusinessAudit.Table] = 8 }.OrderBy(kv => kv.Key), changed.OrderBy(kv => kv.Key));
        var after = await CompanyCounts();
        Assert.All(ledger, t => Assert.Equal(company[t], after[t]));                                           // explicitly: no journal entry, line, allocation or event, no issued number, no counter, no rule
        Assert.Equal((0L, 0L), (after[CompanySchema.JournalEntry.Table], after[CompanySchema.NumberIssue.Table]));
        Assert.Empty(Changed(system, await SystemCounts()));                                                   // and nothing in Hasm_System
        // the cash account row itself is untouched (no balance, no counter, no revision)
        Assert.Equal(1L, await Sql<long>($"SELECT {Ca.Revision} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"));
        // CONTENT (review L4): outside the three voucher tables and the audit stream no row of ANY company table was added, removed or CHANGED: the journal, its lines, allocations and events, the issued numbers, the
        // counters and the numbering rules by name, and every other table (the chart, the cash account master, the parties, the fiscal calendar, the stock and commercial tables, ...)
        var contentAfter = await CompanyContent();
        Assert.All(ledger, t => Assert.Equal(content[t], contentAfter[t]));
        Assert.Empty(contentAfter.Where(kv => !own.Contains(kv.Key) && content.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key));
        Assert.All(own, t => Assert.NotEqual(content[t], contentAfter[t]));                                    // the comparison does see the four tables that changed
        // the comparison is by content: an UPDATE that moves no count is seen (here on the chart, by direct SQL after the voucher work), and only where it happened
        await Sql($"UPDATE {Ac.Table} SET {Ac.Revision} = {Ac.Revision} + 1 WHERE {Ac.Id} = '{sales}'");
        var touched = await CompanyContent();
        Assert.Equal([Ac.Table], touched.Where(kv => contentAfter[kv.Key] != kv.Value).Select(kv => kv.Key).ToArray());
        Assert.Equal(contentAfter[Ac.Table].Split(':')[0], touched[Ac.Table].Split(':')[0]);                   // same number of rows, another hash
    }
}

/// <summary>
/// Review M1 of PR 247: the guard that company migration 0040 puts on the cash account row decides on what it reads of the voucher table, which is race-free only at READ COMMITTED. Any other level is refused first.
/// </summary>
public abstract class CashVoucherIsolationTestBase : CashVoucherTestBase
{
    protected const string LevelRefusal = "cash accounts are changed at READ COMMITTED only";
    private int seq;

    protected string OwnerCs => new NpgsqlConnectionStringBuilder(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName)) { Pooling = false }.ConnectionString;

    private static async Task<(string? State, string Message)> Run(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        try { await using var cmd = new NpgsqlCommand(sql, conn); await cmd.ExecuteNonQueryAsync(); return (null, ""); }
        catch (PostgresException ex) { return (ex.SqlState, ex.MessageText); }
    }

    /// <summary>
    /// One transaction of <paramref name="cs"/> at <paramref name="level"/>: it takes its snapshot (a read of the voucher table), then <paramref name="between"/> commits in ANOTHER transaction of the same principal at the
    /// default level, then the transaction runs <paramref name="sql"/> and commits when it was accepted. Returns the refusal (state and message), or (null, "") when the statement was accepted and committed.
    /// </summary>
    protected static async Task<(string? State, string Message)> At(string cs, string level, string sql, string? between = null)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using (var begin = new NpgsqlCommand($"BEGIN ISOLATION LEVEL {level}", conn)) await begin.ExecuteNonQueryAsync();
        await using (var current = new NpgsqlCommand("SELECT current_setting('transaction_isolation')", conn)) Assert.Equal(level.ToLowerInvariant(), (string)(await current.ExecuteScalarAsync())!);
        await using (var snapshot = new NpgsqlCommand($"SELECT count(*) FROM {CV.Table}", conn)) await snapshot.ExecuteScalarAsync();
        if (between is not null) Assert.Equal((null, ""), await Run(cs, between));
        try
        {
            await using (var cmd = new NpgsqlCommand(sql, conn)) await cmd.ExecuteNonQueryAsync();
            await using (var commit = new NpgsqlCommand("COMMIT", conn)) await commit.ExecuteNonQueryAsync();
            return (null, "");
        }
        catch (PostgresException ex)
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK", conn);
            await rollback.ExecuteNonQueryAsync();
            return (ex.SqlState, ex.MessageText);
        }
    }

    protected static string Change(Guid box, string set) => $"UPDATE {Ca.Table} SET {set}, {Ca.Revision} = {Ca.Revision} + 1 WHERE {Ca.Id} = '{box}'";
    private async Task<(bool Active, Guid Gl, Guid Branch, long Revision)> State(Guid box) => (await Sql<bool>($"SELECT {Ca.IsActive} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"),
        await Sql<Guid>($"SELECT {Ca.AccountId} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"), await Sql<Guid>($"SELECT {Ca.BranchId} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"), await Sql<long>($"SELECT {Ca.Revision} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"));

    /// <summary>
    /// The interleaving of the review (I1 as the owner, I4 as the runtime role): a transaction takes its snapshot, a complete draft voucher on the account commits beside it, then the transaction deactivates the account,
    /// re-binds its ledger account or moves its branch. Before the fix every one of them was ACCEPTED at REPEATABLE READ and SERIALIZABLE and left an open voucher on a deactivated, re-bound or re-branched account.
    /// As the principal of <paramref name="cs"/> (which also writes the voucher): refused at every level other than READ COMMITTED, with or without a concurrent writer and whatever the change; decided by the rules of the
    /// account at READ COMMITTED. The other direction (a voucher written from a snapshot that predates a committed deactivation) holds at every level by the row lock, and is pinned too.
    /// </summary>
    protected async Task AccountChangesAreRefusedAboveReadCommitted(string cs)
    {
        var main = await MainBranch(); var b2 = (await Make("B2")).Id; var sales = await Gl("4101", 4); var by = Guid.NewGuid();
        async Task<(Guid Box, Guid OwnGl, Guid FreeGl)> NewBox()
        {
            var n = Interlocked.Increment(ref seq);
            var box = await Box($"iso{n}", main, $"13{n:00}");
            return (box, await Sql<Guid>($"SELECT {Ca.AccountId} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"), await Gl($"14{n:00}"));
        }
        string Voucher(Guid box, out Guid id) { id = Guid.NewGuid(); return $"{VoucherSql(id, box, main, by, $"iso-key-{Interlocked.Increment(ref seq):00000}")}; {LineSql(id, 1, 1, sales)}; {EventSql(id, 1, null, 1, 1)}"; }
        string[] Changes(Guid freeGl) => [$"{Ca.IsActive} = false", $"{Ca.AccountId} = '{freeGl}'", $"{Ca.BranchId} = '{b2}'", $"{Ca.NameEn} = 'Renamed'"];

        foreach (var level in new[] { "REPEATABLE READ", "SERIALIZABLE" })
        {
            for (var change = 0; change < 4; change++)
            {
                // a voucher commits after the snapshot: the change of its account is refused, the account is exactly as it was and the voucher is there
                var a = await NewBox();
                Assert.Equal(("P0001", LevelRefusal), await At(cs, level, Change(a.Box, Changes(a.FreeGl)[change]), Voucher(a.Box, out var v)));
                Assert.Equal((true, a.OwnGl, main, 1L), await State(a.Box));
                Assert.Equal(1L, await Rows(CV.Table, $"{CV.Id} = '{v}' AND {CV.Status} = 1"));
            }
            // no concurrent writer and no voucher at all: every change of a cash account is refused at this level (the refusal comes before any rule)
            var idle = await NewBox();
            foreach (var set in Changes(idle.FreeGl)) Assert.Equal(("P0001", LevelRefusal), await At(cs, level, Change(idle.Box, set)));
            Assert.Equal((true, idle.OwnGl, main, 1L), await State(idle.Box));
        }
        // "any level other than read committed": PostgreSQL treats READ UNCOMMITTED as READ COMMITTED, but the guard names the one supported level and refuses the rest (fail-closed, as migration 0039)
        var u = await NewBox();
        Assert.Equal(("P0001", LevelRefusal), await At(cs, "READ UNCOMMITTED", Change(u.Box, $"{Ca.NameEn} = 'Renamed'")));

        // READ COMMITTED (the level of the application): the same interleaving is decided by the rules, because the guard reads after the row lock was granted
        var r1 = await NewBox();
        Assert.Equal(("P0001", "cash_account_has_open_documents"), await At(cs, "READ COMMITTED", Change(r1.Box, $"{Ca.IsActive} = false"), Voucher(r1.Box, out _)));
        var r2 = await NewBox();
        Assert.Equal(("P0001", "cash_account_binding_frozen"), await At(cs, "READ COMMITTED", Change(r2.Box, $"{Ca.AccountId} = '{r2.FreeGl}'"), Voucher(r2.Box, out _)));
        var r3 = await NewBox();
        Assert.Equal(("P0001", "cash_account_binding_frozen"), await At(cs, "READ COMMITTED", Change(r3.Box, $"{Ca.BranchId} = '{b2}'"), Voucher(r3.Box, out _)));
        Assert.Equal((true, r1.OwnGl, main, 1L), await State(r1.Box));
        Assert.Equal((true, r2.OwnGl, main, 1L), await State(r2.Box));
        Assert.Equal((true, r3.OwnGl, main, 1L), await State(r3.Box));
        // ... and every change WORKS there: what is not frozen changes beside a committed voucher, and an account without a voucher is re-bound, moved and deactivated
        Assert.Equal((null, ""), await At(cs, "READ COMMITTED", Change(r3.Box, $"{Ca.NameEn} = 'Renamed'"), Voucher(r3.Box, out _)));
        var free = await NewBox();
        Assert.Equal((null, ""), await At(cs, "READ COMMITTED", Change(free.Box, $"{Ca.AccountId} = '{free.FreeGl}'")));
        Assert.Equal((null, ""), await At(cs, "READ COMMITTED", Change(free.Box, $"{Ca.BranchId} = '{b2}'")));
        Assert.Equal((null, ""), await At(cs, "READ COMMITTED", Change(free.Box, $"{Ca.IsActive} = false")));
        Assert.Equal((false, free.FreeGl, b2, 4L), await State(free.Box));

        // THE OTHER DIRECTION needs no level rule (the voucher guards lock a row the other side CHANGES): a voucher written by a transaction whose snapshot predates a committed deactivation of its account is never
        // accepted: refused by the rule at READ COMMITTED, by a serialization failure (40001) at the snapshot levels
        foreach (var level in new[] { "READ COMMITTED", "REPEATABLE READ", "SERIALIZABLE" })
        {
            var d = await NewBox();
            var late = await At(cs, level, Voucher(d.Box, out var v), Change(d.Box, $"{Ca.IsActive} = false"));
            Assert.Equal(level == "READ COMMITTED" ? ("P0001", "cash_voucher_account_inactive") : ("40001", late.Message), late);
            Assert.Equal(0L, await Rows(CV.Table, $"{CV.CashAccountId} = '{d.Box}'"));
        }
        // nothing in the whole company database is an open voucher on an inactive account, or a voucher of another branch than its account's
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CV.Table} v JOIN {Ca.Table} a ON a.{Ca.Id} = v.{CV.CashAccountId} WHERE (v.{CV.Status} <> 5 AND NOT a.{Ca.IsActive}) OR a.{Ca.BranchId} <> v.{CV.BranchId}"));
    }
}

public class CashVoucherIsolationLevelTests : CashVoucherIsolationTestBase
{
    [PostgresFact]
    public async Task A_direct_change_of_a_cash_account_is_refused_at_repeatable_read_and_serializable_and_decided_by_the_voucher_rules_at_read_committed()
    {
        await StartV();
        await AccountChangesAreRefusedAboveReadCommitted(OwnerCs);
    }

    /// <summary>
    /// No service path changes a cash account or writes a voucher at another level: every one of them runs in <see cref="CompanyAuditedTransactions"/>, whose transaction is begun at READ COMMITTED explicitly, also when
    /// the sessions of the deployment default to another level. The merged S1 operations on the account (update, deactivate, reactivate, the key re-wrap) and the voucher operations all work through such connections.
    /// </summary>
    [PostgresFact]
    public async Task The_cash_services_change_accounts_and_write_vouchers_at_read_committed_even_when_the_server_default_is_another_level()
    {
        await StartV();
        var main = await MainBranch(); var sales = await Gl("4101", 4); var manager = await AccountManager();
        var box = await Box("c1", main, "1101");
        var bank = IdOf(await Create(manager, Bank("b1", main, await Gl("1102"))));
        await using (var conn = await Resolver.OpenAsync(Company.Id, default))
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand("SELECT current_setting('transaction_isolation')", conn, tx);
            Assert.Equal("read committed", (string)(await cmd.ExecuteScalarAsync())!);
        }
        // a deployment whose sessions DEFAULT to another level (default_transaction_isolation)
        var skewed = new CompanyDatabaseResolver(Configured(true), new Hasm.Infrastructure.Companies.CompanyRegistry(Options.BuildSystemConnectionString(admin: true)), Credentials, Clock, Logs.CreateLogger("Hasm.Company"), CompanyScripts)
        {
            ResolutionLifetime = TimeSpan.Zero, SchemaCheckLifetime = TimeSpan.Zero,
            ConnectionStringOverride = (db, _, _) => new NpgsqlConnectionStringBuilder(Options.BuildCompanyAdminConnectionString(db)) { Options = "-c default_transaction_isolation=serializable" }.ConnectionString,
        };
        string level;
        await using (var conn = await skewed.OpenAsync(Company.Id, default))
        {
            await using (var d = new NpgsqlCommand("SELECT current_setting('default_transaction_isolation')", conn)) Assert.Equal("serializable", (string)(await d.ExecuteScalarAsync())!);
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand("SELECT current_setting('transaction_isolation')", conn, tx);
            level = (string)(await cmd.ExecuteScalarAsync())!;
        }
        Assert.Equal("read committed", level);                                                                 // the driver begins every application transaction with an explicit READ COMMITTED
        var transactions = new CompanyAuditedTransactions(skewed, Audit, Clock);
        var accounts = new CashAccountService(skewed, transactions, new EnvelopeEncryptor(Keys), new CashDocumentUsageProbe(), Clock);
        var vouchers = new CashVoucherService(skewed, transactions, Clock);
        var actor = Guid.NewGuid();

        // the merged S1 paths that UPDATE a cash account row (each fires the new guard): update, deactivate, reactivate, re-wrap
        var renamed = await accounts.UpdateAsync(actor, Company.Id, box, 1, new CashAccountInput("cashbox", "C1", "الصندوق", "Renamed", main, await Sql<Guid>($"SELECT {Ca.AccountId} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"), null, null, null));
        Assert.True(renamed.IsOk, renamed.Code);
        Assert.True((await accounts.DeactivateAsync(actor, Company.Id, box, 2)).IsOk);
        Assert.True((await accounts.ReactivateAsync(actor, Company.Id, box, 3)).IsOk);
        Keys.Add("k2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); Keys.Activate("k2");
        var rewrap = await accounts.RewrapNumbersAsync(actor, Company.Id);
        Assert.Equal((1, 0), (rewrap.Rewrapped, rewrap.Failed));
        Assert.Equal("k2", await Sql<string>($"SELECT {Ca.NumberKeyId} FROM {Ca.Table} WHERE {Ca.Id} = '{bank}'"));
        // the voucher paths, and the two rules of the guard through the service
        var created = await vouchers.CreateAsync(actor, Company.Id, new CashVoucherInput("receipt", box, new DateOnly(2026, 3, 10), null, null, null, "skewed-key-0001", [new CashVoucherLineInput(sales, 10m, null)]));
        Assert.True(created.IsOk, created.Code);
        Assert.True((await vouchers.SubmitAsync(actor, Company.Id, created.Value!.Id, 1)).IsOk);
        var refused = await accounts.DeactivateAsync(actor, Company.Id, box, 4);
        Assert.Equal((ConfigStatus.Conflict, "cash_account_has_open_documents"), (refused.Status, refused.Code));
        Assert.True((await vouchers.CancelAsync(actor, Company.Id, created.Value.Id, 2, "skewed")).IsOk);
        Assert.True((await accounts.DeactivateAsync(actor, Company.Id, box, 4)).IsOk);
        Assert.Equal((false, 5L), (await Sql<bool>($"SELECT {Ca.IsActive} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'"), await Sql<long>($"SELECT {Ca.Revision} FROM {Ca.Table} WHERE {Ca.Id} = '{box}'")));
    }
}

/// <summary>Review M1 of PR 247, as the REAL per-company runtime role (role-capable disposable cluster only): the role holds UPDATE on the cash account table and may choose its isolation level; the guard refuses it.</summary>
public class CashVoucherIsolationLevelRuntimeRoleTests : CashVoucherIsolationTestBase
{
    protected override bool RolesRequired => true;

    [PostgresRoleFact]
    public async Task The_runtime_role_is_refused_a_change_of_a_cash_account_at_repeatable_read_and_serializable_and_makes_it_at_read_committed()
    {
        await StartV();
        await new CompanyRuntimeProvisioner(Configured(useRoleAdmin: true), Registry).GrantAsync(Company.Id);
        var credential = (await new FileCompanyCredentialStore(CredentialDirectory).GetAsync(Company.Id))!;
        var cs = new NpgsqlConnectionStringBuilder(Configured().BuildCompanyRuntimeConnectionString(Company.PhysicalDatabaseName, credential.Username, credential.Password)) { Pooling = false }.ConnectionString;
        var role = CompanyRuntimeRole.NameFor(Company.PhysicalDatabaseName);
        await using (var rt = new NpgsqlConnection(cs))
        {
            await rt.OpenAsync();
            await using (var who = new NpgsqlCommand("SELECT current_user::text", rt)) Assert.Equal(role, (string)(await who.ExecuteScalarAsync())!);
            Assert.Empty(await CompanyRuntimePrivileges.FindViolationsAsync(rt, role));
            Assert.True(await CompanyRuntimePrivileges.HasRequiredAccessAsync(rt, role));
        }
        await AccountChangesAreRefusedAboveReadCommitted(cs);
    }
}

/// <summary>The REAL per-company runtime role (role-capable disposable cluster only): the service runs under it without a missing grant, and the role can neither rewrite nor remove a voucher, a line set or the history.</summary>
public class CashVoucherRuntimeRoleTests : CashVoucherTestBase
{
    protected override bool RolesRequired => true;

    private static async Task<string?> Try(NpgsqlConnection conn, string sql)
    {
        try { await using var cmd = new NpgsqlCommand(sql, conn); await cmd.ExecuteNonQueryAsync(); return null; }
        catch (PostgresException ex) { return ex.SqlState; }
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_runs_the_whole_service_and_can_never_update_a_line_or_an_event_or_delete_or_truncate_anything()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("c1", main, "1101"); var sales = await Gl("4101", 4); var fees = await Gl("4102", 4); var party = await Party("P1");
        await new CompanyRuntimeProvisioner(Configured(useRoleAdmin: true), Registry).GrantAsync(Company.Id);
        var role = CompanyRuntimeRole.NameFor(Company.PhysicalDatabaseName);
        var credential = (await new FileCompanyCredentialStore(CredentialDirectory).GetAsync(Company.Id))!;
        var cs = new NpgsqlConnectionStringBuilder(Configured().BuildCompanyRuntimeConnectionString(Company.PhysicalDatabaseName, credential.Username, credential.Password)) { Pooling = false }.ConnectionString;

        // the service under the runtime role: every statement and every guard (the share locks included) works with the catalog grants
        var resolver = RealResolver();
        var service = new CashVoucherService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), Clock);
        var actor = Guid.NewGuid();
        var input = new CashVoucherInput("receipt", box, new DateOnly(2026, 3, 10), party, "R-1", null, "runtime-key-0001", [new CashVoucherLineInput(sales, 10m, "a"), new CashVoucherLineInput(fees, 2.5m, null)]);
        var created = await service.CreateAsync(actor, Company.Id, input);
        Assert.True(created.IsOk, created.Code);
        var id = created.Value!.Id;
        Assert.True((await service.CreateAsync(actor, Company.Id, input)).Value!.Replayed);
        var updated = await service.UpdateAsync(actor, Company.Id, id, 1, input with { Kind = null, CashAccountId = null, Lines = [new CashVoucherLineInput(fees, 3m, null)] });
        Assert.True(updated.IsOk, updated.Code);
        Assert.True((await service.SubmitAsync(actor, Company.Id, id, 2)).IsOk);
        var cancelled = await service.CancelAsync(actor, Company.Id, id, 3, "runtime");
        Assert.Equal((CashVoucherStatus.Cancelled, 4L, 4), (cancelled.Value!.Status, cancelled.Value.Revision, cancelled.Value.Events.Count));
        Assert.Single(await service.ListAsync(Company.Id, new CashVoucherFilter()));
        var open = (await service.CreateAsync(actor, Company.Id, input with { IdempotencyKey = "runtime-key-0002" })).Value!.Id;
        var waiting = (await service.CreateAsync(actor, Company.Id, input with { IdempotencyKey = "runtime-key-0003" })).Value!.Id;
        Assert.True((await service.SubmitAsync(actor, Company.Id, waiting, 1)).IsOk);
        // the usage probe of the account master, as the runtime role
        await using (var probeConn = await resolver.OpenAsync(Company.Id))
        await using (var tx = await probeConn.BeginTransactionAsync())
            Assert.Equal(new CashAccountUsage(true, true), await new CashDocumentUsageProbe().GetAsync(probeConn, tx, box, default));

        await using var rt = new NpgsqlConnection(cs);
        await rt.OpenAsync();
        await using (var who = new NpgsqlCommand("SELECT current_user::text", rt)) Assert.Equal(role, (string)(await who.ExecuteScalarAsync())!);
        Assert.Empty(await CompanyRuntimePrivileges.FindViolationsAsync(rt, role));
        Assert.True(await CompanyRuntimePrivileges.HasRequiredAccessAsync(rt, role));
        // no privilege to rewrite or remove: the lines and the history are not updatable, nothing is deletable or truncatable, the guards cannot be switched off
        foreach (var table in new[] { VL.Table, VE.Table })
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"UPDATE {table} SET {(table == VL.Table ? VL.Amount : VE.Revision)} = 1"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"SELECT 1 FROM {table} FOR UPDATE"));
        }
        foreach (var table in new[] { CV.Table, VL.Table, VE.Table })
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"DELETE FROM {table}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"TRUNCATE {table} CASCADE"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"ALTER TABLE {table} DISABLE TRIGGER USER"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"DROP TABLE {table} CASCADE"));
        }
        foreach (var function in new[] { CV.FnGuard, CV.FnHistory, VL.FnGuard, VE.FnGuard, CompanySchema.CashVoucherAccountGuard.FnGuard })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"CREATE OR REPLACE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$"));
        // the UPDATE it does hold on the voucher row is fenced by PostgreSQL: no approval, no silent status change, no change without its history event
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, MoveSql(open, 3)));
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, MoveSql(id, 1)));
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, MoveSql(waiting, 1)));                    // submitted -> draft (a withdrawal): refused for the runtime role too, with or without an event
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, $"{MoveSql(waiting, 1)}; {EventSql(waiting, 4, 2, 1, 3)}"));
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, $"{MoveSql(waiting, 5, actor.ToString())}; {EventSql(waiting, 5, 2, 5, 3, "NULL")}"));   // a cancellation without its reason
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, $"{MoveSql(waiting, 5, actor.ToString())}; {EventSql(waiting, 5, 1, 5, 3)}"));           // ... or recorded as the cancellation of a draft
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, MoveSql(open, 5)));                       // legal transition, but no event: refused at commit
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, $"SET CONSTRAINTS ALL IMMEDIATE; {MoveSql(open, 5)}"));
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, LineSql(open, 1, 9, sales)));             // no line slipped into a recorded set
        Assert.Equal(((short)5, 4L, (short)1, 1L, (short)2, 2L), (await DbStatus(id), await DbRevision(id), await DbStatus(open), await DbRevision(open), await DbStatus(waiting), await DbRevision(waiting)));
    }
}
