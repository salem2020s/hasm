using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using DL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentLink;
using Ra = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using SC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentShortClose;

namespace Hasm.Database.IntegrationTests;

/// <summary>The fixtures of the short-close tests: an approved order and an approved delivery of the test type, and the fulfilment link that makes the order partially fulfilled.</summary>
public abstract class ShortCloseTestBase : CommercialTestBase
{
    protected Guid Branch, Party, Approver;

    protected async Task Basics()
    {
        await Prepare();
        await Fixtures();
    }

    protected async Task Fixtures()
    {
        Branch = await NewBranchId("BA"); Party = await NewCustomerId("C1"); Approver = await NewApprover();
    }

    protected static DocumentLinkInput Link(Guid from, int fromLine, Guid to, int toLine, decimal qty, string key, CommercialLinkKind kind = CommercialLinkKind.Fulfils) =>
        new(from, fromLine, to, toLine, (int)kind, qty, key);

    protected static ShortCloseInput Whole(string key, string reason = "the customer no longer needs the rest") => new(reason, null, key);
    protected static ShortCloseInput Named(string key, params (int LineNo, decimal Quantity)[] lines) => new("the supplier discontinued the item", [.. lines.Select(l => new ShortCloseLineInput(l.LineNo, l.Quantity))], key);

    protected async Task<Guid> LineId(Guid doc, int lineNo) => await Sql<Guid>($"SELECT {CL.Id} FROM {CL.Table} WHERE {CL.DocumentId} = '{doc}' AND {CL.LineNo} = {lineNo}");

    /// <summary>An approved order of <paramref name="ordered"/> pieces of which <paramref name="fulfilled"/> were delivered (one fulfilment link), so it is partially fulfilled.</summary>
    protected async Task<(CommercialDocumentView Order, CommercialDocumentView Delivery)> PartiallyFulfilled(string tag, Guid item, Guid unit, decimal ordered, decimal fulfilled, Guid? branch = null)
    {
        var order = await NewApproved(branch ?? Branch, Party, $"{tag}-order-001", Approver, CommercialTestType.Name, (item, unit, ordered));
        var delivery = await NewApproved(branch ?? Branch, Party, $"{tag}-deliv-001", Approver, CommercialTestType.Name, (item, unit, ordered));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, delivery.Id, 1, fulfilled, $"{tag}-link-0001")));
        return (order, delivery);
    }

    protected async Task<(string? State, string Message)> Run(string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try { await using var cmd = new NpgsqlCommand(sql, conn, tx); await cmd.ExecuteNonQueryAsync(); await tx.CommitAsync(); return (null, ""); }
        catch (PostgresException ex) { return (ex.SqlState, ex.MessageText); }
    }

    protected string RawClose(Guid doc, Guid line, string qty, string key, string reason = "'raw'") =>
        $"INSERT INTO {SC.Table} ({SC.Id}, {SC.OperationId}, {SC.DocumentId}, {SC.LineId}, {SC.Quantity}, {SC.Reason}, {SC.IdempotencyKey}, {SC.PayloadHash}, {SC.CreatedBy}, {SC.CreatedAtUtc}) " +
        $"VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', '{doc}', '{line}', {qty}, {reason}, '{key}', '{new string('a', 64)}', '{Admin.Id}', now())";

    protected string RawLink(Guid fromDoc, Guid fromLine, Guid toDoc, Guid toLine, int kind, string qty, string key) =>
        $"INSERT INTO {DL.Table} ({DL.Id}, {DL.FromDocumentId}, {DL.FromLineId}, {DL.ToDocumentId}, {DL.ToLineId}, {DL.Kind}, {DL.Quantity}, {DL.IdempotencyKey}, {DL.PayloadHash}, {DL.CreatedBy}, {DL.CreatedAtUtc}) " +
        $"VALUES ('{Guid.NewGuid()}', '{fromDoc}', '{fromLine}', '{toDoc}', '{toLine}', {kind}, {qty}, '{key}', '{new string('a', 64)}', '{Admin.Id}', now())";

    /// <summary>The exact row count of every table of the company database (the test proves which tables a short close writes: its own and the audit trail, nothing else).</summary>
    protected async Task<Dictionary<string, long>> Footprint()
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT c.relname, (xpath('/row/n/text()', query_to_xml(format('SELECT count(*) AS n FROM %I', c.relname), false, true, '')))[1]::text::bigint " +
            "FROM pg_class c JOIN pg_namespace s ON s.oid = c.relnamespace WHERE s.nspname = 'public' AND c.relkind = 'r' ORDER BY 1", conn);
        var map = new Dictionary<string, long>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }
}

/// <summary>
/// Phase 3.13 short close (owner decision D-201): "An explicitly authorised short-close action may close the remaining quantity with a reason and audit, but must not invent deliveries, invoices or stock movements."
/// Only an approved, partially fulfilled document of a closable type is closed; the remaining quantity is closed in one append-only operation with a mandatory reason, an audit event and an idempotency key; nothing but
/// the short-close record and the audit trail is written (no link, no stock document, no journal entry, the document row itself is untouched); after it no further fulfilment or invoice is possible (service AND
/// PostgreSQL); a close racing links never lets fulfilled plus closed exceed the ordered quantity. Disposable cluster only.
/// </summary>
public class ShortCloseTests : ShortCloseTestBase
{
    [PostgresFact]
    public async Task A_partially_fulfilled_approved_order_is_closed_short_with_a_reason_and_an_audit_event_and_nothing_else_is_written()
    {
        await Basics();
        var (item, unit) = await NewItemAndUnit("SC1"); var (item2, unit2) = await NewItemAndUnit("SC2");
        var order = await NewApproved(Branch, Party, "sc-order-0001", Approver, CommercialTestType.Name, (item, unit, 10m), (item2, unit2, 5m));
        var delivery = await NewApproved(Branch, Party, "sc-deliv-0001", Approver, CommercialTestType.Name, (item, unit, 10m), (item2, unit2, 5m));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, delivery.Id, 1, 4m, "sc-link-00001")));
        var before = await Footprint();
        var orderRow = await Sql<string>($"SELECT d::text FROM {CD.Table} d WHERE {CD.Id} = '{order.Id}'");

        // refusals: validation, precondition, existence, and the open quantity; nothing is written by any of them
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, new ShortCloseInput("  ", null, "sc-key-000001")), "reason", "reason_required");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, new ShortCloseInput("a\nb", null, "sc-key-000001")), "reason", "reason_required");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, new ShortCloseInput(new string('x', 501), null, "sc-key-000001")), "reason", "reason_required");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Whole("short")), "idempotencyKey", "idempotency_key_invalid");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001")), "lines", "out_of_range");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001", (1, 0m))), "lines", "out_of_range");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001", (1, 1.00000000001m))), "lines", "out_of_range");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001", (1, 1m), (1, 1m))), "lines", "duplicate_line");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001", (99, 1m))), "lines", "line_not_found");
        Invalid(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, null), "input", "required");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, null, Whole("sc-key-000001")), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision + 1, Whole("sc-key-000001")), ConfigStatus.PreconditionFailed, "revision_conflict");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, Whole("sc-key-000001")), ConfigStatus.NotFound, "commercial_document_not_found");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001", (1, 6.5m))), ConfigStatus.Conflict, "exceeds_open_quantity");   // 10 ordered - 4 delivered = 6 open
        Assert.Equal(before, await Footprint());

        // a part of the open remainder of one line
        var part = Ok(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001", (1, 2m))));
        Assert.Equal((order.Id, Admin.Id, false), (part.DocumentId, part.CreatedBy, part.Replayed));
        Assert.Equal("the supplier discontinued the item", part.Reason);
        Assert.Equal([new ShortCloseLineView(1, 2m)], part.Lines);
        var q = Ok(await LinkMgmt.QuantitiesAsync(Admin.Id, Company.Id, order.Id)).OrderBy(l => l.LineNo).ToList();
        Assert.Equal((10m, 4m, 2m, 4m, 8m, 4m), (q[0].Ordered, q[0].Fulfilled, q[0].ShortClosed, q[0].RemainingToFulfil, q[0].RemainingToInvoice, q[0].Open));
        Assert.Equal((5m, 0m, 0m, 5m, 5m), (q[1].Ordered, q[1].Fulfilled, q[1].ShortClosed, q[1].RemainingToFulfil, q[1].Open));

        // idempotency: the same key and payload is the same operation; the same key with another payload is refused; nothing more is written
        var replay = Ok(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001", (1, 2m))));
        Assert.Equal((part.OperationId, true), (replay.OperationId, replay.Replayed));
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000001", (1, 3m))), ConfigStatus.Conflict, "idempotency_key_reused");
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SC.Table}"));

        // the whole open remainder of every line: 4 of line 1 and 5 of line 2
        var whole = Ok(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Whole("sc-key-000002")));
        Assert.Equal([new ShortCloseLineView(1, 4m), new ShortCloseLineView(2, 5m)], whole.Lines);
        q = [.. Ok(await LinkMgmt.QuantitiesAsync(Admin.Id, Company.Id, order.Id)).OrderBy(l => l.LineNo)];
        Assert.Equal((6m, 0m, 0m), (q[0].ShortClosed, q[0].RemainingToFulfil, q[0].Open));
        Assert.Equal((5m, 0m, 0m), (q[1].ShortClosed, q[1].RemainingToFulfil, q[1].Open));
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Whole("sc-key-000003")), ConfigStatus.Conflict, "nothing_to_close");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sc-key-000004", (2, 1m))), ConfigStatus.Conflict, "exceeds_open_quantity");
        Assert.Equal(new[] { part.OperationId, whole.OperationId }.Order(), (await Links.ListShortClosesAsync(Company.Id, order.Id)).Select(o => o.OperationId).Order());   // the test clock stands still, so the two share a timestamp

        // audit: one event per operation, naming the operation, the line count and the quantity, never the reason text
        Assert.Equal(2L, await Events(CommercialAuditEvents.ShortClosed));
        var details = new List<string>();
        foreach (var op in new[] { part.OperationId, whole.OperationId })
            details.Add(await Sql<string>($"SELECT {Audit_.Detail} FROM {Audit_.Table} WHERE {Audit_.EventType} = {CommercialAuditEvents.ShortClosed} AND {Audit_.SubjectId} = '{order.Id}' AND {Audit_.ActorUserId} = '{Admin.Id}' AND {Audit_.Detail} LIKE '%{op}%'"));
        Assert.Contains("lines=1 quantity=2", details[0]);
        Assert.Contains("lines=2 quantity=9", details[1]);
        Assert.All(details, d => { Assert.DoesNotContain("supplier", d); Assert.DoesNotContain("customer", d); });

        // D-201 "must not invent deliveries, invoices or stock movements": only the short-close records and the audit trail grew; no link, no document, no stock or ledger row; the order row is byte-identical
        var after = await Footprint();
        var changed = after.Where(kv => before[kv.Key] != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value - before[kv.Key]);
        Assert.Equal(new Dictionary<string, long> { [SC.Table] = 3, [Audit_.Table] = 2 }.OrderBy(kv => kv.Key), changed.OrderBy(kv => kv.Key));
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.Equal(orderRow, await Sql<string>($"SELECT d::text FROM {CD.Table} d WHERE {CD.Id} = '{order.Id}'"));

        // no further fulfilment or invoice after the close (the service answers with the stable code; PostgreSQL refuses a direct statement), and the document stays approved
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, delivery.Id, 2, 1m, "sc-link-00002")), ConfigStatus.Conflict, "over_fulfilment");
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, delivery.Id, 2, 1m, "sc-link-00003", CommercialLinkKind.Invoices)), ConfigStatus.Conflict, "over_fulfilment");
        var raw = await Run(RawLink(order.Id, await LineId(order.Id, 2), delivery.Id, await LineId(delivery.Id, 2), 1, "1", "sc-raw-000001"));
        Assert.Equal("P0001", raw.State);
        Assert.Contains("closed short", raw.Message);
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, delivery.Id, 1, 4m, "sc-link-00004", CommercialLinkKind.Invoices)));   // what WAS delivered can still be invoiced: 4 of line 1
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, delivery.Id, 2, 1m, "sc-link-00005", CommercialLinkKind.Invoices)), ConfigStatus.Conflict, "over_fulfilment");
        var current = Ok(await DocMgmt.GetAsync(Admin.Id, Company.Id, order.Id));
        Assert.Equal((CommercialStatus.Approved, order.Revision), (current.Status, current.Revision));
        Fails(await Docs.CancelAsync(Admin.Id, Company.Id, order.Id, order.Revision, "no longer needed"), ConfigStatus.Conflict, "document_has_links");   // a closed document always has links (it was partially fulfilled), so the D-075 refusal answers first
        Assert.Contains("cannot leave the approved status", (await Run($"UPDATE {CD.Table} SET {CD.Status} = 5, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{order.Id}'")).Message);
    }

    [PostgresFact]
    public async Task Only_an_approved_partially_fulfilled_document_of_a_closable_type_is_closed_short()
    {
        await Basics();
        var (item, unit) = await NewItemAndUnit("ST1");
        var draft = await NewDraft(Branch, Party, item, unit, "st-draft-0001", 10m);
        var submitted = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, (await NewDraft(Branch, Party, item, unit, "st-subm-00001", 10m)).Id, 1));
        var untouched = await NewApproved(Branch, Party, "st-untouched1", Approver, CommercialTestType.Name, (item, unit, 10m));
        var otherType = await NewApproved(Branch, Party, "st-othertype1", Approver, CommercialTestType.Other, (item, unit, 10m));
        var cancelled = Ok(await Docs.CancelAsync(Admin.Id, Company.Id, (await NewApproved(Branch, Party, "st-cancel-001", Approver, CommercialTestType.Name, (item, unit, 10m))).Id, 3, "not needed"));
        var before = await Footprint();

        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, draft.Id, draft.Revision, Whole("st-key-000001")), ConfigStatus.Conflict, "document_not_approved");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, submitted.Id, submitted.Revision, Whole("st-key-000002")), ConfigStatus.Conflict, "document_not_approved");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, cancelled.Id, cancelled.Revision, Whole("st-key-000003")), ConfigStatus.Conflict, "document_not_approved");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, otherType.Id, otherType.Revision, Whole("st-key-000004")), ConfigStatus.Conflict, "short_close_not_allowed_for_type");
        // approved, closable, but nothing was delivered or invoiced from it: such a document is cancelled (its own permission), never closed short
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, untouched.Id, untouched.Revision, Whole("st-key-000005")), ConfigStatus.Conflict, "document_not_partially_fulfilled");
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, untouched.Id, untouched.Revision, Named("st-key-000006", (1, 1m))), ConfigStatus.Conflict, "document_not_partially_fulfilled");
        Assert.Equal(before, await Footprint());
        Ok(await Docs.CancelAsync(Admin.Id, Company.Id, untouched.Id, untouched.Revision, "nothing was delivered"));   // the route for a document without any fulfilment

        // a return or an amendment is not a fulfilment: a document with only such links is still not partially fulfilled
        var returnedFrom = await NewApproved(Branch, Party, "st-returned-1", Approver, CommercialTestType.Name, (item, unit, 10m));
        var target = await NewApproved(Branch, Party, "st-target-001", Approver, CommercialTestType.Name, (item, unit, 10m));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(returnedFrom.Id, 1, target.Id, 1, 1m, "st-link-00001", CommercialLinkKind.Returns)));
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, returnedFrom.Id, returnedFrom.Revision, Whole("st-key-000007")), ConfigStatus.Conflict, "document_not_partially_fulfilled");
        // the TARGET of a fulfilment is not partially fulfilled by it either (a link counts for the document it leaves)
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(returnedFrom.Id, 1, target.Id, 1, 3m, "st-link-00002")));
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, target.Id, target.Revision, Whole("st-key-000008")), ConfigStatus.Conflict, "document_not_partially_fulfilled");
        // an invoice link alone makes the source partially fulfilled; the open quantity is ordered minus the LARGER of fulfilled and invoiced
        var invoiced = await NewApproved(Branch, Party, "st-invoiced-1", Approver, CommercialTestType.Name, (item, unit, 10m));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(invoiced.Id, 1, target.Id, 1, 7m, "st-link-00003", CommercialLinkKind.Invoices)));
        Assert.Equal([new ShortCloseLineView(1, 3m)], Ok(await Links.ShortCloseAsync(Admin.Id, Company.Id, invoiced.Id, invoiced.Revision, Whole("st-key-000009"))).Lines);
        // a fully fulfilled document has nothing to close
        var full = await NewApproved(Branch, Party, "st-full-00001", Approver, CommercialTestType.Name, (item, unit, 2m));
        var fullTarget = await NewApproved(Branch, Party, "st-fulltgt-01", Approver, CommercialTestType.Name, (item, unit, 2m));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(full.Id, 1, fullTarget.Id, 1, 2m, "st-link-00004")));
        Fails(await Links.ShortCloseAsync(Admin.Id, Company.Id, full.Id, full.Revision, Whole("st-key-000010")), ConfigStatus.Conflict, "nothing_to_close");
    }

    [PostgresFact]
    public async Task PostgreSQL_itself_refuses_every_rule_for_a_direct_statement_and_short_closes_are_append_only()
    {
        await Basics();
        var (item, unit) = await NewItemAndUnit("SR1");
        var (order, delivery) = await PartiallyFulfilled("sr", item, unit, 10m, 4m);
        var draft = await NewDraft(Branch, Party, item, unit, "sr-draft-0001", 10m);
        var untouched = await NewApproved(Branch, Party, "sr-untouched1", Approver, CommercialTestType.Name, (item, unit, 10m));
        Guid oL = await LineId(order.Id, 1), dL = await LineId(delivery.Id, 1), draftL = await LineId(draft.Id, 1), uL = await LineId(untouched.Id, 1);

        // the service is bypassed: each rule is the database's own
        Assert.Contains("only an approved document", (await Run(RawClose(draft.Id, draftL, "1", "sr-raw-000001"))).Message);
        Assert.Contains("only a partially fulfilled document", (await Run(RawClose(untouched.Id, uL, "1", "sr-raw-000002"))).Message);
        Assert.Contains("only a partially fulfilled document", (await Run(RawClose(delivery.Id, dL, "1", "sr-raw-000003"))).Message);                    // the target of the link is not the fulfilled side
        Assert.Contains("a line of its own document", (await Run(RawClose(order.Id, dL, "1", "sr-raw-000004"))).Message);
        Assert.Contains("never exceeds the open quantity", (await Run(RawClose(order.Id, oL, "6.0000000001", "sr-raw-000005"))).Message);              // 10 - 4 = 6 open
        Assert.Equal("23514", (await Run(RawClose(order.Id, oL, "0", "sr-raw-000006"))).State);                                                       // quantity must be positive
        Assert.Equal("23514", (await Run(RawClose(order.Id, oL, "1", "sr-raw-000007", "''"))).State);                                                 // a reason is mandatory
        Assert.Equal("23514", (await Run(RawClose(order.Id, oL, "1", "sr-raw-000008", "'   '"))).State);
        Assert.Equal("23514", (await Run(RawClose(order.Id, oL, "1", "sr-raw-000009", "E'a\\nb'"))).State);                                           // no control characters
        Assert.Equal("23514", (await Run(RawClose(order.Id, oL, "1", "short"))).State);                                                               // the idempotency key
        Assert.Contains("a line of its own document", (await Run(RawClose(order.Id, Guid.NewGuid(), "1", "sr-raw-000010"))).Message);                      // a line that does not exist
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {SC.Table}"));

        Assert.Null((await Run(RawClose(order.Id, oL, "2.5", "sr-raw-000011"))).State);
        Assert.Contains("never exceeds the open quantity", (await Run(RawClose(order.Id, oL, "3.6", "sr-raw-000012"))).Message);                      // 6 open - 2.5 closed = 3.5
        Assert.Equal("23505", (await Run(RawClose(order.Id, oL, "1", "sr-raw-000011"))).State);                                                       // the same key of the same creator on the same line
        Assert.Null((await Run(RawClose(order.Id, oL, "3.5", "sr-raw-000013"))).State);
        Assert.Equal(6m, await Sql<decimal>($"SELECT sum({SC.Quantity}) FROM {SC.Table}"));

        // append-only: no UPDATE, DELETE or TRUNCATE
        Assert.Contains("append-only", (await Run($"UPDATE {SC.Table} SET {SC.Quantity} = 1")).Message);
        Assert.Contains("append-only", (await Run($"UPDATE {SC.Table} SET {SC.Reason} = 'rewritten'")).Message);
        Assert.Contains("append-only", (await Run($"DELETE FROM {SC.Table}")).Message);
        Assert.Contains("append-only", (await Run($"TRUNCATE {SC.Table}")).Message);
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {SC.Table}"));

        // tolerance 0 after the close: nothing more can be fulfilled or invoiced beyond ordered minus closed; a return of what was delivered is not capped by the close
        var second = await NewApproved(Branch, Party, "sr-deliv-0002", Approver, CommercialTestType.Name, (item, unit, 10m));
        var sL = await LineId(second.Id, 1);
        var over = await Run(RawLink(order.Id, oL, second.Id, sL, 1, "0.0000000001", "sr-raw-000014"));
        Assert.Equal("P0001", over.State);
        Assert.Contains("closed short", over.Message);
        Assert.Contains("closed short", (await Run(RawLink(order.Id, oL, second.Id, sL, 2, "4.5", "sr-raw-000015"))).Message);                        // invoices: at most 10 - 6 = 4
        Assert.Null((await Run(RawLink(order.Id, oL, second.Id, sL, 2, "4", "sr-raw-000016"))).State);
        Assert.Null((await Run(RawLink(order.Id, oL, second.Id, sL, 3, "1", "sr-raw-000017"))).State);                                                // a return
        // the closed document stays approved
        Assert.Contains("cannot leave the approved status", (await Run($"UPDATE {CD.Table} SET {CD.Status} = 5, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{order.Id}'")).Message);
        Assert.Contains("cannot leave the approved status", (await Run($"UPDATE {CD.Table} SET {CD.Status} = 1, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{order.Id}'")).Message);
    }

    [PostgresFact]
    public async Task A_short_close_racing_sixteen_links_never_lets_fulfilled_plus_closed_exceed_the_ordered_quantity()
    {
        await Basics();
        var (item, unit) = await NewItemAndUnit("SX1");
        var (orderA, _) = await PartiallyFulfilled("sxa", item, unit, 20m, 2m);
        var (orderB, _) = await PartiallyFulfilled("sxb", item, unit, 20m, 2m);
        var deliveries = new List<CommercialDocumentView>();
        for (var i = 0; i < 16; i++) deliveries.Add(await NewApproved(Branch, Party, $"sx-deliv-{i:0000}", Approver, CommercialTestType.Name, (item, unit, 2m)));

        // through the service: 16 links of 1 piece and ONE close of the whole remainder, all at once. The close takes whatever is open when it holds the line; every link after it is refused with the stable code.
        var linkTasks = deliveries.Select((d, i) => Links.CreateAsync(Admin.Id, Company.Id, Link(orderA.Id, 1, d.Id, 1, 1m, $"sx-svc-{i:0000}"))).ToList();
        var closeTask = Links.ShortCloseAsync(Admin.Id, Company.Id, orderA.Id, orderA.Revision, Whole("sx-close-0001"));
        var links = await Task.WhenAll(linkTasks);
        var close = Ok(await closeTask);
        Assert.All(links.Where(r => !r.IsOk), r => Assert.Equal("over_fulfilment", r.Code));
        var a = Assert.Single(await Links.QuantitiesAsync(Company.Id, orderA.Id));
        Assert.Equal(2m + links.Count(r => r.IsOk), a.Fulfilled);
        Assert.Equal(close.Lines.Single().Quantity, a.ShortClosed);
        Assert.Equal(20m, a.Fulfilled + a.ShortClosed);                                                                                              // exactly the ordered quantity: nothing lost, nothing exceeded
        Assert.Equal(0m, a.Open);
        Assert.Equal(1L, await Events(CommercialAuditEvents.ShortClosed));

        // through raw concurrent statements (no service check): the database alone serializes the close and the links on the source line. The close of the 18 open pieces either comes first (then no link fits) or
        // finds fewer than 18 open and is refused; the ordered quantity is never exceeded either way.
        Guid bL = await LineId(orderB.Id, 1);
        var lines = new List<Guid>(); foreach (var d in deliveries) lines.Add(await LineId(d.Id, 1));
        var rawLinks = deliveries.Select((d, i) => Run(RawLink(orderB.Id, bL, d.Id, lines[i], 1, "1", $"sx-raw-{i:0000}"))).ToList();
        var rawClose = Run(RawClose(orderB.Id, bL, "18", "sx-rawclose-1"));
        var rawResults = await Task.WhenAll(rawLinks);
        var closed = await rawClose;
        var linked = rawResults.Count(r => r.State is null);
        Assert.All(rawResults.Where(r => r.State is not null), r => { Assert.Equal("P0001", r.State); Assert.Contains("closed short", r.Message); });
        if (closed.State is null) Assert.Equal(0, linked);
        else { Assert.Equal("P0001", closed.State); Assert.Contains("never exceeds the open quantity", closed.Message); Assert.Equal(16, linked); }
        var b = Assert.Single(await Links.QuantitiesAsync(Company.Id, orderB.Id));
        Assert.True(b.Fulfilled + b.ShortClosed <= 20m, $"fulfilled {b.Fulfilled} + closed {b.ShortClosed} exceeds the ordered 20");
        Assert.Equal((2m + linked, closed.State is null ? 18m : 0m), (b.Fulfilled, b.ShortClosed));
    }

    [PostgresFact]
    public async Task Parallel_closes_with_one_key_are_one_operation_and_parallel_closes_with_different_keys_close_the_remainder_exactly_once()
    {
        await Basics();
        var (item, unit) = await NewItemAndUnit("SP1");
        var (order, _) = await PartiallyFulfilled("spa", item, unit, 10m, 4m);
        var same = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Named("sp-same-00001", (1, 2m)))));
        Assert.All(same, r => Assert.True(r.IsOk, $"{r.Status} {r.Code}"));
        Assert.Single(same.Select(r => r.Value!.OperationId).Distinct());
        Assert.Equal(1, same.Count(r => !r.Value!.Replayed));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SC.Table}"));
        Assert.Equal(1L, await Events(CommercialAuditEvents.ShortClosed));

        // eight different requests for the whole remainder (4 are open): exactly one closes it, the others find nothing to close
        var different = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Links.ShortCloseAsync(Admin.Id, Company.Id, order.Id, order.Revision, Whole($"sp-diff-{i:00000}"))));
        Assert.Equal(1, different.Count(r => r.IsOk));
        Assert.All(different.Where(r => !r.IsOk), r => Assert.Equal((ConfigStatus.Conflict, "nothing_to_close"), (r.Status, r.Code)));
        Assert.Equal(6m, await Sql<decimal>($"SELECT sum({SC.Quantity}) FROM {SC.Table} WHERE {SC.DocumentId} = '{order.Id}'"));
        Assert.Equal(2L, await Events(CommercialAuditEvents.ShortClosed));
        // the same key by ANOTHER user is another operation's key (the key belongs to its creator), and that user finds nothing to close
        Fails(await Links.ShortCloseAsync(Approver, Company.Id, order.Id, order.Revision, Named("sp-same-00001", (1, 2m))), ConfigStatus.Conflict, "exceeds_open_quantity");
    }

    [PostgresFact]
    public async Task Short_close_needs_the_explicit_close_permission_on_the_documents_branch_and_reveals_nothing_across_branches()
    {
        await Basics();
        var branchB = await NewBranchId("BB");
        var (item, unit) = await NewItemAndUnit("SZ1");
        var (orderA, _) = await PartiallyFulfilled("sza", item, unit, 10m, 4m);
        var (orderB, _) = await PartiallyFulfilled("szb", item, unit, 10m, 4m, branchB);
        var before = await Footprint();

        // the close code is held by no administrator-made role: an administrator cannot put it into a role, and holds it nowhere
        Assert.False((await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "sneaky-closer", [PermissionCatalog.Codes.CommercialView, PermissionCatalog.Codes.CommercialClose])).IsOk);
        var every = new[] { PermissionCatalog.Codes.CommercialView, PermissionCatalog.Codes.CommercialCreate, PermissionCatalog.Codes.CommercialUpdate, PermissionCatalog.Codes.CommercialSubmit, PermissionCatalog.Codes.CommercialCancel };
        var (clerk, _) = await Member(every, ScopeKind.Company, null);
        var outsider = await NewUser("closeoutsider");
        // never implied by update, cancel or approve; the administrator and the approver are refused like anyone else; nothing is written
        foreach (var user in new[] { Admin.Id, Approver, clerk.Id, outsider.Id })
        {
            Fails(await LinkMgmt.ShortCloseAsync(user, Company.Id, orderA.Id, orderA.Revision, Whole("sz-key-000001")), ConfigStatus.Forbidden, "forbidden");
            Fails(await LinkMgmt.ShortCloseAsync(user, Company.Id, Guid.NewGuid(), 1, Whole("sz-key-000001")), ConfigStatus.Forbidden, "forbidden");
        }
        Assert.Equal(before, await Footprint());

        // a holder scoped to branch A (the assignment is planted directly: no service route can grant the preset to a branch today) closes on A only; branch B and a missing document are the same refusal
        var scoped = await NewUser("closer-branch-a");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, scoped.Login)).IsOk);
        await Exec($"INSERT INTO {Ra.Table} ({Ra.Id},{Ra.UserId},{Ra.CompanyId},{Ra.RoleId},{Ra.ScopeKind},{Ra.ScopeId},{Ra.ScopeEpoch},{Ra.GrantedAtUtc}) VALUES ('{Guid.NewGuid()}','{scoped.Id}','{Company.Id}','{PresetRoles.CommercialDocumentCloserId}',{(short)ScopeKind.Branch},'{Branch}',1,now())");   // epoch 1: the activation epoch of the branch
        Fails(await LinkMgmt.ShortCloseAsync(scoped.Id, Company.Id, orderB.Id, orderB.Revision, Whole("sz-key-000002")), ConfigStatus.Forbidden, "forbidden");
        Fails(await LinkMgmt.ShortCloseAsync(scoped.Id, Company.Id, Guid.NewGuid(), 1, Whole("sz-key-000002")), ConfigStatus.Forbidden, "forbidden");
        Fails(await LinkMgmt.ListShortClosesAsync(scoped.Id, Company.Id, orderB.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(before, await Footprint());
        var closedA = Ok(await LinkMgmt.ShortCloseAsync(scoped.Id, Company.Id, orderA.Id, orderA.Revision, Whole("sz-key-000002")));
        Assert.Equal((scoped.Id, 6m), (closedA.CreatedBy, closedA.Lines.Single().Quantity));
        Assert.Single(Ok(await LinkMgmt.ListShortClosesAsync(scoped.Id, Company.Id, orderA.Id)));

        // the company-wide holder (the offline preset) closes on any branch and may be told that a document does not exist
        var closer = await NewCloserUser();
        Fails(await LinkMgmt.ShortCloseAsync(closer.Id, Company.Id, Guid.NewGuid(), 1, Whole("sz-key-000003")), ConfigStatus.NotFound, "commercial_document_not_found");
        Assert.Equal(6m, Ok(await LinkMgmt.ShortCloseAsync(closer.Id, Company.Id, orderB.Id, orderB.Revision, Whole("sz-key-000003"))).Lines.Single().Quantity);
        // reading the operations needs the view permission on the document's branch; the reason is shown to a viewer of that branch only
        var (viewerA, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Branch, Branch);
        Assert.Equal("the customer no longer needs the rest", Ok(await LinkMgmt.ListShortClosesAsync(viewerA.Id, Company.Id, orderA.Id)).Single().Reason);
        Fails(await LinkMgmt.ListShortClosesAsync(viewerA.Id, Company.Id, orderB.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await LinkMgmt.ListShortClosesAsync(outsider.Id, Company.Id, orderA.Id), ConfigStatus.Forbidden, "forbidden");
        // a revoked preset closes nothing more
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeCommercialDocumentCloserOfflineAsync(closer.Login, Company.Id));
        var (orderC, _) = await PartiallyFulfilled("szc", item, unit, 10m, 4m);
        Fails(await LinkMgmt.ShortCloseAsync(closer.Id, Company.Id, orderC.Id, orderC.Revision, Whole("sz-key-000004")), ConfigStatus.Forbidden, "forbidden");
    }
}

/// <summary>The short close over real HTTP, by a user who holds ONLY the dedicated preset (M1 of the X4 plan): authentication, company selection, the If-Match precondition, the stable refusal codes, replay, the reads, and no change or delete route.</summary>
public class ShortCloseApiTests : ShortCloseTestBase
{
    private const string Base = "/api/v1/company/commercial-documents";

    [PostgresFact]
    public async Task A_user_holding_only_the_closer_preset_closes_a_partially_fulfilled_order_over_http_and_can_do_nothing_else()
    {
        await StartApi(configure: b => b.ConfigureServices(RegisterTestType));
        await ComposeAsync();
        await Fixtures();
        var (item, unit) = await NewItemAndUnit("SH1");
        var (order, delivery) = await PartiallyFulfilled("sh", item, unit, 10m, 4m);
        var routes = new (HttpMethod, string)[] { (HttpMethod.Post, $"{Base}/{order.Id}/short-close"), (HttpMethod.Get, $"{Base}/{order.Id}/short-closes") };
        foreach (var (method, path) in routes)
        {
            object? body = method == HttpMethod.Get ? null : new { };
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body, cookie: "forged")).Status);
        }
        var noCompany = await SignIn(Admin, selectCompany: false);
        foreach (var (method, path) in routes) Assert.Equal("company_not_selected", (await Send(method, path, method == HttpMethod.Get ? null : new { }, noCompany, ifMatch: "\"3\"")).Error);

        var closerUser = await NewCloserUser();
        var cookie = await SignIn(closerUser);
        // the preset reads the document it closes (its revision is the precondition) and the registered closable types
        var read = await Send(HttpMethod.Get, $"{Base}/{order.Id}", null, cookie);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        var etag = $"\"{read.Json.GetProperty("revision").GetInt64()}\"";
        var types = await Send(HttpMethod.Get, $"{Base}/types", null, cookie);
        Assert.Equal(["commercial_test"], types.Json.GetProperty("shortCloseTypes").EnumerateArray().Select(x => x.GetString()!));
        var open = (await Send(HttpMethod.Get, $"{Base}/{order.Id}/line-quantities", null, cookie)).Json.GetProperty("lines")[0];
        Assert.Equal((0m, 6m), (open.GetProperty("shortClosed").GetDecimal(), open.GetProperty("open").GetDecimal()));

        object Body(string key, string? reason = "the customer cancelled the rest") => new { reason, idempotencyKey = key, createdBy = Guid.NewGuid(), documentId = delivery.Id };   // unknown fields are ignored
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", Body("sh-key-000001"), cookie)).Status);                  // If-Match is required
        var stale = await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", Body("sh-key-000001"), cookie, ifMatch: "\"99\"");
        Assert.Equal((HttpStatusCode.PreconditionFailed, "revision_conflict"), (stale.Status, stale.Error));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", Body("sh-key-000001", null), cookie, ifMatch: etag)).Status);   // a reason is mandatory
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", Body("short"), cookie, ifMatch: etag)).Status);
        var missing = await Send(HttpMethod.Post, $"{Base}/{Guid.NewGuid()}/short-close", Body("sh-key-000001"), cookie, ifMatch: etag);
        Assert.Equal((HttpStatusCode.NotFound, "commercial_document_not_found"), (missing.Status, missing.Error));
        var tooMany = await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", new { reason = "r", idempotencyKey = "sh-key-000001", lines = new[] { new { lineNo = 1, quantity = 7m } } }, cookie, ifMatch: etag);
        Assert.Equal((HttpStatusCode.Conflict, "exceeds_open_quantity"), (tooMany.Status, tooMany.Error));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {SC.Table}"));

        var closed = await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", Body("sh-key-000001"), cookie, ifMatch: etag);
        Assert.True(closed.Status == HttpStatusCode.OK, $"{(int)closed.Status} {closed.Body}");
        Assert.Equal((order.Id, closerUser.Id, false), (closed.Json.GetProperty("documentId").GetGuid(), closed.Json.GetProperty("createdBy").GetGuid(), closed.Json.GetProperty("replayed").GetBoolean()));   // the session user and the routed document, never a body field
        Assert.Equal((1, 6m), (closed.Json.GetProperty("lines")[0].GetProperty("lineNo").GetInt32(), closed.Json.GetProperty("lines")[0].GetProperty("quantity").GetDecimal()));
        var replay = await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", Body("sh-key-000001"), cookie, ifMatch: etag);
        Assert.Equal((HttpStatusCode.OK, true), (replay.Status, replay.Json.GetProperty("replayed").GetBoolean()));
        Assert.Equal(closed.Json.GetProperty("operationId").GetGuid(), replay.Json.GetProperty("operationId").GetGuid());
        var reused = await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", Body("sh-key-000001", "another reason"), cookie, ifMatch: etag);
        Assert.Equal((HttpStatusCode.Conflict, "idempotency_key_reused"), (reused.Status, reused.Error));
        var nothing = await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", Body("sh-key-000002"), cookie, ifMatch: etag);
        Assert.Equal((HttpStatusCode.Conflict, "nothing_to_close"), (nothing.Status, nothing.Error));

        // the reads: the operation with its reason, and the derived quantities; the document itself is unchanged (same revision, still approved)
        var list = await Send(HttpMethod.Get, $"{Base}/{order.Id}/short-closes", null, cookie);
        Assert.Equal("the customer cancelled the rest", list.Json.GetProperty("shortCloses")[0].GetProperty("reason").GetString());
        Assert.Equal(1, list.Json.GetProperty("shortCloses").GetArrayLength());
        var q = (await Send(HttpMethod.Get, $"{Base}/{order.Id}/line-quantities", null, cookie)).Json.GetProperty("lines")[0];
        Assert.Equal((6m, 0m, 0m), (q.GetProperty("shortClosed").GetDecimal(), q.GetProperty("open").GetDecimal(), q.GetProperty("remainingToFulfil").GetDecimal()));
        var again = await Send(HttpMethod.Get, $"{Base}/{order.Id}", null, cookie);
        Assert.Equal((order.Revision, (int)CommercialStatus.Approved), (again.Json.GetProperty("revision").GetInt64(), again.Json.GetProperty("status").GetInt32()));
        Assert.Equal(1L, await Events(CommercialAuditEvents.ShortClosed));
        // a short close is append-only: no change or delete route exists
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            Assert.True((await Send(method, $"{Base}/{order.Id}/short-close", method == HttpMethod.Delete ? null : new { }, cookie, ifMatch: etag)).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
            Assert.True((await Send(method, $"{Base}/{order.Id}/short-closes/{closed.Json.GetProperty("operationId").GetGuid()}", method == HttpMethod.Delete ? null : new { }, cookie, ifMatch: etag)).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        }

        // the preset is nothing but view and close: every other commercial action and every other resource is refused
        var docBody = new { docType = CommercialTestType.Name, branchId = Branch, partyId = Party, currency = "JOD", documentDate = "2026-06-15", mode = 1, idempotencyKey = "sh-doc-000001", lines = new[] { new { itemId = item, unitId = unit, quantity = 1m } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base, docBody, cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"{Base}/{order.Id}", docBody, cookie, ifMatch: etag)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{order.Id}/cancel", new { reason = "x" }, cookie, ifMatch: etag)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{order.Id}/approve", null, cookie, ifMatch: etag)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/links", new { fromDocumentId = order.Id, fromLineNo = 1, toDocumentId = delivery.Id, toLineNo = 1, kind = 2, quantity = 1m, idempotencyKey = "sh-link-00002" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/v1/company/warehouses", null, cookie)).Status);

        // the administrator (who holds every other commercial code but approve) is refused the close; a viewer reads the operations but cannot close
        var (order2, _) = await PartiallyFulfilled("sh2", item, unit, 10m, 4m);
        var adminCookie = await SignIn(Admin);
        var refused = await Send(HttpMethod.Post, $"{Base}/{order2.Id}/short-close", Body("sh-key-000003"), adminCookie, ifMatch: $"\"{order2.Revision}\"");
        Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), (refused.Status, refused.Error));
        var (viewer, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Branch, Branch);
        var viewerCookie = await SignIn(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{order2.Id}/short-close", Body("sh-key-000004"), viewerCookie, ifMatch: $"\"{order2.Revision}\"")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/{order.Id}/short-closes", null, viewerCookie)).Status);
        var (nobody, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{order.Id}/short-closes", null, await SignIn(nobody))).Status);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SC.Table}"));
    }

    [PostgresFact]
    public async Task Production_registers_no_closable_type_so_the_action_is_refused_until_a_concrete_order_type_exists()
    {
        // the API with the test type registered as a document type but NOT as closable (the production composition registers no type at all, so nothing is closable there either)
        await StartApi(configure: b => b.ConfigureServices(s => s.AddSingleton<ICommercialDocumentTypeRegistry>(new CommercialDocumentTypeRegistry([CommercialTestType.Name],
            [(CommercialTestType.Name, CommercialTestType.Name, CommercialLinkKind.Fulfils)]))));
        await ComposeAsync();
        await Fixtures();
        var (item, unit) = await NewItemAndUnit("SN1");
        var (order, _) = await PartiallyFulfilled("sn", item, unit, 10m, 4m);
        var cookie = await SignIn(await NewCloserUser());
        Assert.Empty((await Send(HttpMethod.Get, $"{Base}/types", null, cookie)).Json.GetProperty("shortCloseTypes").EnumerateArray());
        var refused = await Send(HttpMethod.Post, $"{Base}/{order.Id}/short-close", new { reason = "r", idempotencyKey = "sn-key-000001" }, cookie, ifMatch: $"\"{order.Revision}\"");
        Assert.Equal((HttpStatusCode.Conflict, "short_close_not_allowed_for_type"), (refused.Status, refused.Error));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {SC.Table}"));
        Assert.Empty(new CommercialDocumentTypeRegistry([]).ShortCloseTypes);
    }
}

/// <summary>
/// The fixtures of the isolation-level tests (review M1 of PR 244, second review): the two guards of company migration 0039 that SUM (the close guard and the link cap) lock a row and then read the sums, which is
/// race-free only at READ COMMITTED. At REPEATABLE READ or SERIALIZABLE a transaction sums with the snapshot it took before the other transaction committed, so fulfilled plus closed could exceed the ordered quantity
/// (reproduced by the second review through direct SQL). Both guards therefore refuse any other level as their first statement; the link cap fires for every link insert and refuses before its kind test, so the
/// link-after-link form of the 0030 guard is covered as well.
/// </summary>
public abstract class ShortCloseIsolationTestBase : ShortCloseTestBase
{
    protected const string Refusal = "short closes and document links are written at READ COMMITTED only";
    protected Guid Item, Unit;
    protected readonly List<(Guid Doc, Guid Line)> Targets = [];
    private int seq;
    protected string Key(string tag) => $"{tag}-{Interlocked.Increment(ref seq):000000}";

    protected string OwnerCs => new NpgsqlConnectionStringBuilder(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName)) { Pooling = false }.ConnectionString;

    /// <summary>One item and three approved documents with a large quantity (the targets of the links).</summary>
    protected async Task Setup()
    {
        await Basics();
        (Item, Unit) = await NewItemAndUnit("ISO");
        for (var i = 0; i < 3; i++)
        {
            var d = await NewApproved(Branch, Party, Key("target"), Approver, CommercialTestType.Name, (Item, Unit, 100000m));
            Targets.Add((d.Id, await LineId(d.Id, 1)));
        }
    }

    /// <summary>An approved order of 10 of which 4 are fulfilled (to the first target), so 6 are open.</summary>
    protected async Task<(Guid Id, Guid Line, long Revision)> NewOrder()
    {
        var o = await NewApproved(Branch, Party, Key("order"), Approver, CommercialTestType.Name, (Item, Unit, 10m));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(o.Id, 1, Targets[0].Doc, 1, 4m, Key("init"))));
        return (o.Id, await LineId(o.Id, 1), o.Revision);
    }

    protected async Task<(decimal Fulfilled, decimal Invoiced, decimal Closed)> LineState(Guid line) => (
        await Sql<decimal>($"SELECT COALESCE(sum({DL.Quantity}),0) FROM {DL.Table} WHERE {DL.FromLineId} = '{line}' AND {DL.Kind} = 1"),
        await Sql<decimal>($"SELECT COALESCE(sum({DL.Quantity}),0) FROM {DL.Table} WHERE {DL.FromLineId} = '{line}' AND {DL.Kind} = 2"),
        await Sql<decimal>($"SELECT COALESCE(sum({SC.Quantity}),0) FROM {SC.Table} WHERE {SC.LineId} = '{line}'"));

    /// <summary>
    /// One transaction of <paramref name="connectionString"/> at <paramref name="level"/>: it takes its snapshot (a read of the short-close table), then <paramref name="between"/> commits in ANOTHER transaction at the
    /// default level, then the transaction runs <paramref name="sql"/> and commits when it was accepted. Returns the refusal (state and message), or (null, "") when the statement was accepted and committed.
    /// </summary>
    protected async Task<(string? State, string Message)> At(string connectionString, string level, string sql, string? between = null)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var begin = new NpgsqlCommand($"BEGIN ISOLATION LEVEL {level}", conn)) await begin.ExecuteNonQueryAsync();
        await using (var current = new NpgsqlCommand("SELECT current_setting('transaction_isolation')", conn)) Assert.Equal(level.ToLowerInvariant(), (string)(await current.ExecuteScalarAsync())!);
        await using (var snapshot = new NpgsqlCommand($"SELECT count(*) FROM {SC.Table}", conn)) await snapshot.ExecuteScalarAsync();
        if (between is not null) Assert.Null((await Run(between)).State);
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

    /// <summary>
    /// The three interleavings of the second review (close after a committed link, link after a committed close, link after a committed link: each would leave 16 of 10 without the refusal) and the plain statements,
    /// as the principal of <paramref name="cs"/>: refused at REPEATABLE READ, SERIALIZABLE and READ UNCOMMITTED with the stable refusal, decided by the ordinary quantity rules at READ COMMITTED.
    /// </summary>
    protected async Task GuardedWritesAreRefusedAboveReadCommitted(string cs)
    {
        foreach (var level in new[] { "REPEATABLE READ", "SERIALIZABLE" })
        {
            // close after link: the 6 open pieces are linked and committed after this transaction took its snapshot; its close of the same 6 is refused
            var a = await NewOrder();
            var closeAfterLink = await At(cs, level, RawClose(a.Id, a.Line, "6", Key("cal-close")), RawLink(a.Id, a.Line, Targets[1].Doc, Targets[1].Line, 1, "6", Key("cal-link")));
            Assert.Equal(("P0001", Refusal), closeAfterLink);
            Assert.Equal((10m, 0m, 0m), await LineState(a.Line));
            // link after close: the 6 open pieces are closed and committed after the snapshot; a link of 6 more is refused
            var b = await NewOrder();
            var linkAfterClose = await At(cs, level, RawLink(b.Id, b.Line, Targets[1].Doc, Targets[1].Line, 1, "6", Key("lac-link")), RawClose(b.Id, b.Line, "6", Key("lac-close")));
            Assert.Equal(("P0001", Refusal), linkAfterClose);
            Assert.Equal((4m, 0m, 6m), await LineState(b.Line));
            // link after link (the sums of the 0030 guard, no short close involved): refused by the link cap of 0039, which fires for every link insert
            var c = await NewOrder();
            var linkAfterLink = await At(cs, level, RawLink(c.Id, c.Line, Targets[2].Doc, Targets[2].Line, 1, "6", Key("lal-two")), RawLink(c.Id, c.Line, Targets[1].Doc, Targets[1].Line, 1, "6", Key("lal-one")));
            Assert.Equal(("P0001", Refusal), linkAfterLink);
            Assert.Equal((10m, 0m, 0m), await LineState(c.Line));
            // no concurrent writer at all: every guarded write is refused at this level, whatever its kind (the refusal comes before the kind test) and whatever its quantity
            var d = await NewOrder();
            Assert.Equal(("P0001", Refusal), await At(cs, level, RawClose(d.Id, d.Line, "1", Key("plain-close"))));
            foreach (var kind in new[] { 1, 2, 3, 4 })
                Assert.Equal(("P0001", Refusal), await At(cs, level, RawLink(d.Id, d.Line, Targets[1].Doc, Targets[1].Line, kind, "1", Key("plain-link"))));
            Assert.Equal((4m, 0m, 0m), await LineState(d.Line));
        }
        // "any level other than read committed": PostgreSQL treats READ UNCOMMITTED as READ COMMITTED, but the guards name the one supported level and refuse the rest (fail-closed)
        var u = await NewOrder();
        Assert.Equal(("P0001", Refusal), await At(cs, "READ UNCOMMITTED", RawClose(u.Id, u.Line, "1", Key("ru-close"))));
        Assert.Equal(("P0001", Refusal), await At(cs, "READ UNCOMMITTED", RawLink(u.Id, u.Line, Targets[1].Doc, Targets[1].Line, 1, "1", Key("ru-link"))));

        // READ COMMITTED (the level of the application): the same three interleavings are decided by the quantity rules, because each statement reads after its lock was granted
        var e = await NewOrder();
        var late = await At(cs, "READ COMMITTED", RawClose(e.Id, e.Line, "6", Key("rc-close")), RawLink(e.Id, e.Line, Targets[1].Doc, Targets[1].Line, 1, "6", Key("rc-link")));
        Assert.Equal("P0001", late.State);
        Assert.Contains("never exceeds the open quantity", late.Message);
        var f = await NewOrder();
        late = await At(cs, "READ COMMITTED", RawLink(f.Id, f.Line, Targets[1].Doc, Targets[1].Line, 1, "6", Key("rc-link")), RawClose(f.Id, f.Line, "6", Key("rc-close")));
        Assert.Equal("P0001", late.State);
        Assert.Contains("closed short", late.Message);
        var g = await NewOrder();
        late = await At(cs, "READ COMMITTED", RawLink(g.Id, g.Line, Targets[2].Doc, Targets[2].Line, 1, "6", Key("rc-two")), RawLink(g.Id, g.Line, Targets[1].Doc, Targets[1].Line, 1, "6", Key("rc-one")));
        Assert.Equal("P0001", late.State);
        Assert.Contains("over-fulfilment", late.Message);
        // and both guarded writes WORK there, also after another transaction committed in between: 4 fulfilled + 2 linked in between + 1 linked + 3 closed = 10 of 10
        var h = await NewOrder();
        Assert.Null((await At(cs, "READ COMMITTED", RawLink(h.Id, h.Line, Targets[2].Doc, Targets[2].Line, 1, "1", Key("ok-link")), RawLink(h.Id, h.Line, Targets[1].Doc, Targets[1].Line, 1, "2", Key("ok-between")))).State);
        Assert.Null((await At(cs, "READ COMMITTED", RawClose(h.Id, h.Line, "3", Key("ok-close")))).State);
        Assert.Equal((7m, 0m, 3m), await LineState(h.Line));
        Assert.Contains("never exceeds the open quantity", (await At(cs, "READ COMMITTED", RawClose(h.Id, h.Line, "0.0000000001", Key("ok-over")))).Message);

        // nothing in the whole company database exceeds its ordered quantity
        Assert.Equal(0L, await Sql<long>($@"SELECT count(*) FROM {CL.Table} l WHERE
            GREATEST((SELECT COALESCE(sum(k.{DL.Quantity}),0) FROM {DL.Table} k WHERE k.{DL.FromLineId} = l.{CL.Id} AND k.{DL.Kind} = 1),
                     (SELECT COALESCE(sum(k.{DL.Quantity}),0) FROM {DL.Table} k WHERE k.{DL.FromLineId} = l.{CL.Id} AND k.{DL.Kind} = 2))
            + (SELECT COALESCE(sum(s.{SC.Quantity}),0) FROM {SC.Table} s WHERE s.{SC.LineId} = l.{CL.Id}) > l.{CL.Quantity}"));
    }
}

/// <summary>Review M1 of PR 244: the guards refuse a short close or a link at any isolation level other than READ COMMITTED (direct SQL of the owner), and the service is unaffected.</summary>
public class ShortCloseIsolationLevelTests : ShortCloseIsolationTestBase
{
    [PostgresFact]
    public async Task A_direct_close_or_link_is_refused_at_repeatable_read_and_serializable_and_decided_by_the_quantity_rules_at_read_committed()
    {
        await Setup();
        await GuardedWritesAreRefusedAboveReadCommitted(OwnerCs);
        // the append-only refusals do not depend on the level: a change is refused at every level (by the level check or by the append-only rule, never accepted)
        var o = await NewOrder();
        Assert.Null((await Run(RawClose(o.Id, o.Line, "1", Key("keep-close")))).State);
        foreach (var level in new[] { "READ COMMITTED", "REPEATABLE READ", "SERIALIZABLE" })
        {
            Assert.Equal("P0001", (await At(OwnerCs, level, $"UPDATE {SC.Table} SET {SC.Quantity} = 2")).State);
            Assert.Equal("P0001", (await At(OwnerCs, level, $"DELETE FROM {SC.Table}")).State);
            Assert.Equal("P0001", (await At(OwnerCs, level, $"TRUNCATE {SC.Table}")).State);
        }
        Assert.Equal(1m, (await LineState(o.Line)).Closed);
    }

    [PostgresFact]
    public async Task The_service_writes_links_and_short_closes_at_read_committed_even_when_the_server_default_is_another_level()
    {
        await Setup();
        // the application transaction (CompanyAuditedTransactions) reports READ COMMITTED on an ordinary connection
        await using (var conn = await Resolver.OpenAsync(Company.Id, default))
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand("SELECT current_setting('transaction_isolation')", conn, tx);
            Assert.Equal("read committed", (string)(await cmd.ExecuteScalarAsync())!);
        }
        var o = await NewOrder();
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(o.Id, 1, Targets[1].Doc, 1, 1m, Key("svc-link"))));
        Ok(await Links.ShortCloseAsync(Admin.Id, Company.Id, o.Id, o.Revision, Whole(Key("svc-close"))));
        Assert.Equal((5m, 0m, 5m), await LineState(o.Line));

        // a deployment whose sessions DEFAULT to another level (default_transaction_isolation): the same services through such connections
        var skewed = new Hasm.Infrastructure.CompanyDatabase.CompanyDatabaseResolver(Configured(true), new Hasm.Infrastructure.Companies.CompanyRegistry(Options.BuildSystemConnectionString(admin: true)), Credentials, Clock,
            Logs.CreateLogger("Hasm.Company"), CompanyScripts)
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
        var skewedLinks = new DocumentLinkService(skewed, new Hasm.Infrastructure.CompanyDatabase.CompanyAuditedTransactions(skewed, Audit, Clock), Clock, TestRegistry());
        var p = await NewOrder();
        var link = await skewedLinks.CreateAsync(Admin.Id, Company.Id, Link(p.Id, 1, Targets[1].Doc, 1, 1m, Key("skew-link")));
        var close = await skewedLinks.ShortCloseAsync(Admin.Id, Company.Id, p.Id, p.Revision, Whole(Key("skew-close")));
        // the driver begins every application transaction with an explicit READ COMMITTED, so the server default does not reach the guards: the refusal cannot surface through the service
        // (if it ever did, DocumentLinkService maps a guard refusal, SQLSTATE P0001, to 409 invariant_violation with nothing written)
        Assert.Equal("read committed", level);
        Ok(link);
        Ok(close);
        Assert.Equal((5m, 0m, 5m), await LineState(p.Line));
    }
}

/// <summary>Review M1 of PR 244, as the REAL per-company runtime role (role-capable disposable cluster only): the role may choose its isolation level, and the guards refuse it.</summary>
public class ShortCloseIsolationLevelRuntimeRoleTests : ShortCloseIsolationTestBase
{
    protected override bool RolesRequired => true;

    [PostgresRoleFact]
    public async Task The_runtime_role_is_refused_a_close_or_a_link_at_repeatable_read_and_serializable_and_writes_them_at_read_committed()
    {
        await Setup();
        await new Hasm.Infrastructure.CompanyDatabase.CompanyRuntimeProvisioner(Configured(useRoleAdmin: true), Registry).GrantAsync(Company.Id);
        var credential = (await new Hasm.Infrastructure.CompanyDatabase.FileCompanyCredentialStore(CredentialDirectory).GetAsync(Company.Id))!;
        var cs = new NpgsqlConnectionStringBuilder(Configured().BuildCompanyRuntimeConnectionString(Company.PhysicalDatabaseName, credential.Username, credential.Password)) { Pooling = false }.ConnectionString;
        var role = Hasm.Infrastructure.CompanyDatabase.CompanyRuntimeRole.NameFor(Company.PhysicalDatabaseName);
        await using (var rt = new NpgsqlConnection(cs))
        {
            await rt.OpenAsync();
            await using (var who = new NpgsqlCommand("SELECT current_user::text", rt)) Assert.Equal(role, (string)(await who.ExecuteScalarAsync())!);
            Assert.Empty(await Hasm.Infrastructure.CompanyDatabase.CompanyRuntimePrivileges.FindViolationsAsync(rt, role));
            Assert.True(await Hasm.Infrastructure.CompanyDatabase.CompanyRuntimePrivileges.HasRequiredAccessAsync(rt, role));
        }
        await GuardedWritesAreRefusedAboveReadCommitted(cs);
    }
}
