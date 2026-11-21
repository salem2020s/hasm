using System.Globalization;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Npgsql;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using DL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentLink;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.13 slice S2 (decisions D-074..D-077): links between approved commercial documents. The derived quantities equal the sums of the links, over-fulfilment is refused by the service AND by PostgreSQL for a direct
/// statement, 16 concurrent links against the last remaining quantity create exactly the allowed ones (through the service AND through raw concurrent inserts, which proves the source-line lock in the database),
/// incompatible documents are refused, links are append-only and keep their documents approved, and the API is branch-scoped. Disposable cluster only.
/// </summary>
public class DocumentLinkTests : CommercialTestBase
{
    private async Task<(Guid Branch, Guid Party, Guid Approver)> Basics(string branchCode = "BA")
    {
        await Prepare();
        return (await NewBranchId(branchCode), await NewCustomerId("C1"), await NewApprover());
    }

    private static DocumentLinkInput Link(Guid from, int fromLine, Guid to, int toLine, decimal qty, string key, CommercialLinkKind kind = CommercialLinkKind.Fulfils) =>
        new(from, fromLine, to, toLine, (int)kind, qty, key);

    private async Task<Guid> LineId(Guid doc, int lineNo) => await Sql<Guid>($"SELECT {CL.Id} FROM {CL.Table} WHERE {CL.DocumentId} = '{doc}' AND {CL.LineNo} = {lineNo}");

    private async Task<(string? State, string Message)> Run(string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try { await using var cmd = new NpgsqlCommand(sql, conn, tx); await cmd.ExecuteNonQueryAsync(); await tx.CommitAsync(); return (null, ""); }
        catch (PostgresException ex) { return (ex.SqlState, ex.MessageText); }
    }

    private string RawLink(Guid fromDoc, Guid fromLine, Guid toDoc, Guid toLine, int kind, string qty, string key) =>
        $"INSERT INTO {DL.Table} ({DL.Id}, {DL.FromDocumentId}, {DL.FromLineId}, {DL.ToDocumentId}, {DL.ToLineId}, {DL.Kind}, {DL.Quantity}, {DL.IdempotencyKey}, {DL.PayloadHash}, {DL.CreatedBy}, {DL.CreatedAtUtc}) " +
        $"VALUES ('{Guid.NewGuid()}', '{fromDoc}', '{fromLine}', '{toDoc}', '{toLine}', {kind}, {qty}, '{key}', '{new string('a', 64)}', '{Admin.Id}', now())";

    [PostgresFact]
    public async Task Derived_quantities_equal_the_sum_of_the_links_across_several_documents_and_every_rule_has_its_stable_code()
    {
        var (branch, party, approver) = await Basics();
        var (item1, unit1) = await NewItemAndUnit("LK1"); var (item2, unit2) = await NewItemAndUnit("LK2");
        var order = await NewApproved(branch, party, "lk-order-0001", approver, CommercialTestType.Name, (item1, unit1, 10m), (item2, unit2, 5m));
        var d1 = await NewApproved(branch, party, "lk-deliv-0001", approver, CommercialTestType.Name, (item1, unit1, 4m));
        var d2 = await NewApproved(branch, party, "lk-deliv-0002", approver, CommercialTestType.Name, (item1, unit1, 6m), (item2, unit2, 2m));
        var d3 = await NewApproved(branch, party, "lk-deliv-0003", approver, CommercialTestType.Name, (item1, unit1, 1m));
        var inv = await NewApproved(branch, party, "lk-invoice-001", approver, CommercialTestType.Name, (item1, unit1, 4m));
        var ret = await NewApproved(branch, party, "lk-return-0001", approver, CommercialTestType.Name, (item1, unit1, 1m));

        // partial fulfilment across several documents: 4 + 6 of line 1, 2 of line 2; the delivery D1 is invoiced and one piece of the invoice is returned
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, d1.Id, 1, 4m, "lk-key-000001")));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, d2.Id, 1, 6m, "lk-key-000002")));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, d2.Id, 2, 2m, "lk-key-000003")));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(d1.Id, 1, inv.Id, 1, 4m, "lk-key-000004", CommercialLinkKind.Invoices)));
        Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(inv.Id, 1, ret.Id, 1, 1m, "lk-key-000005", CommercialLinkKind.Returns)));
        var q = await Links.QuantitiesAsync(Company.Id, order.Id);
        Assert.Equal([(1, 10m, 10m, 0m, 0m), (2, 5m, 2m, 0m, 0m)], q.Select(l => (l.LineNo, l.Ordered, l.Fulfilled, l.Invoiced, l.Returned)));
        Assert.Equal([0m, 3m], q.Select(l => l.RemainingToFulfil));
        var qd1 = Assert.Single(await Links.QuantitiesAsync(Company.Id, d1.Id));
        Assert.Equal((4m, 0m, 4m, 0m), (qd1.Ordered, qd1.Fulfilled, qd1.Invoiced, qd1.Returned));
        var qi = Assert.Single(await Links.QuantitiesAsync(Company.Id, inv.Id));
        Assert.Equal((4m, 1m, 3m), (qi.Ordered, qi.Returned, qi.RemainingToReturn));
        Assert.Equal(3, (await Links.ListAsync(Company.Id, order.Id)).Count);                                                  // the order is the source of three links
        Assert.Equal(2, (await Links.ListAsync(Company.Id, inv.Id)).Count);                                                    // the invoice: one in (invoiced from D1), one out (returned)
        Assert.Equal(5L, await Events(CommercialAuditEvents.LinkCreated));

        // the rules, each with its stable code and nothing written
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, d3.Id, 1, 1m, "lk-key-000006")), ConfigStatus.Conflict, "over_fulfilment");          // the order line is fully fulfilled
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, d3.Id, 1, 3m, "lk-key-000008")), "quantity", "exceeds_line_quantity");   // 2 + 3 fits the order line (5) but 3 is more than the delivery line (1)
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, d1.Id, 1, 1m, "lk-key-000009")), ConfigStatus.Conflict, "link_exists");                // one link per (source line, target line, kind)
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, order.Id, 2, 1m, "lk-key-000010")), "toDocumentId", "same_document");
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 9, d3.Id, 1, 1m, "lk-key-000011")), "fromLineNo", "line_not_found");
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(Guid.NewGuid(), 1, d3.Id, 1, 1m, "lk-key-000012")), "fromDocumentId", "document_not_found");
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, d3.Id, 1, 0m, "lk-key-000013")), "quantity", "out_of_range");
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, d3.Id, 1, 1.00000000001m, "lk-key-000014")), "quantity", "out_of_range");   // more than 10 decimals
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, d3.Id, 1, 1m, "short") ), "idempotencyKey", "idempotency_key_invalid");
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, new DocumentLinkInput(order.Id, 1, d3.Id, 1, 7, 1m, "lk-key-000015")), "kind", "out_of_range");

        // a draft or submitted document, another party and an incompatible type are refused
        var draft = await NewDraft(branch, party, item1, unit1, "lk-draft-0001");
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, draft.Id, 1, 1m, "lk-key-000016")), ConfigStatus.Conflict, "document_not_approved");
        var otherParty = await NewCustomerId("C2");
        var foreign = await NewApproved(branch, otherParty, "lk-foreign-001", approver, CommercialTestType.Name, (item2, unit2, 3m));
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, foreign.Id, 1, 1m, "lk-key-000017")), ConfigStatus.Conflict, "party_mismatch");
        var other = await NewApproved(branch, party, "lk-other-00001", approver, CommercialTestType.Other, (item2, unit2, 3m));
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, other.Id, 1, 1m, "lk-key-000018")), "kind", "link_not_allowed");                   // no registered pair for the second type
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(other.Id, 1, order.Id, 2, 1m, "lk-key-000019")), "kind", "link_not_allowed");
        // another branch: the document is simply not found (nothing is revealed)
        var branch2 = await NewBranchId("BB");
        var elsewhere = await NewApproved(branch2, party, "lk-branch2-001", approver, CommercialTestType.Name, (item2, unit2, 3m));
        Invalid(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, elsewhere.Id, 1, 1m, "lk-key-000020")), "fromDocumentId", "document_not_found");
        Assert.Equal(5L, await Sql<long>($"SELECT count(*) FROM {DL.Table}"));                                                  // every refusal wrote nothing
        Assert.Equal(5L, await Events(CommercialAuditEvents.LinkCreated));

        // idempotent: the same key and payload returns the original, another payload with the key is refused
        var again = Ok(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 1, d1.Id, 1, 4m, "lk-key-000001")));
        Assert.True(again.Replayed);
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(order.Id, 2, d1.Id, 1, 1m, "lk-key-000001")), ConfigStatus.Conflict, "idempotency_key_reused");
        Assert.Equal(5L, await Sql<long>($"SELECT count(*) FROM {DL.Table}"));
        Assert.Equal(5L, await Events(CommercialAuditEvents.LinkCreated));                                                      // a replay writes no audit event
    }

    [PostgresFact]
    public async Task Another_currency_is_refused_by_the_service_and_the_database()
    {
        var (branch, party, approver) = await Basics();
        var (item, unit) = await NewItemAndUnit("CU1");
        var usdList = Ok(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, new Hasm.Infrastructure.Pricing.PriceListInput("PL-CU1USD", "قائمة", "List", 11, "USD", new DateOnly(2026, 1, 1), null, true, null)));
        Ok(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, usdList.Id, new Hasm.Infrastructure.Pricing.PriceEntryInput(item, null, unit, 0m, 14m, null)));
        var jod = await NewApproved(branch, party, "cu-jod-000001", approver, CommercialTestType.Name, (item, unit, 5m));
        var usdInput = Doc(branch, party, item, unit, "cu-usd-000001") with { Currency = "USD" };
        var usdDraft = await Docs.CreateAsync(Admin.Id, Company.Id, usdInput);
        if (!usdDraft.IsOk) { Assert.Fail($"a USD document could not be created in this fixture: {usdDraft.Status} {usdDraft.Code}"); }
        var submitted = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, usdDraft.Value!.Id, usdDraft.Value.Revision));
        var usd = Ok(await Docs.ApproveAsync(approver, Company.Id, submitted.Id, submitted.Revision));
        Fails(await Links.CreateAsync(Admin.Id, Company.Id, Link(jod.Id, 1, usd.Id, 1, 1m, "cu-key-000001")), ConfigStatus.Conflict, "currency_mismatch");
        var (state, message) = await Run(RawLink(jod.Id, await LineId(jod.Id, 1), usd.Id, await LineId(usd.Id, 1), 1, "1", "cu-raw-000001"));
        Assert.Equal("P0001", state);
        Assert.Contains("share the party, the currency and the branch", message);
    }

    [PostgresFact]
    public async Task PostgreSQL_refuses_over_fulfilment_and_every_other_rule_for_a_direct_statement_and_links_are_append_only()
    {
        var (branch, party, approver) = await Basics();
        var (item, unit) = await NewItemAndUnit("RW1");
        var order = await NewApproved(branch, party, "rw-order-0001", approver, CommercialTestType.Name, (item, unit, 10m));
        var d1 = await NewApproved(branch, party, "rw-deliv-0001", approver, CommercialTestType.Name, (item, unit, 9m));
        var d2 = await NewApproved(branch, party, "rw-deliv-0002", approver, CommercialTestType.Name, (item, unit, 8m));
        var draft = await NewDraft(branch, party, item, unit, "rw-draft-0001");
        var otherParty = await NewCustomerId("C2");
        var foreign = await NewApproved(branch, otherParty, "rw-foreign-001", approver, CommercialTestType.Name, (item, unit, 8m));
        Guid oL = await LineId(order.Id, 1), d1L = await LineId(d1.Id, 1), d2L = await LineId(d2.Id, 1), draftL = await LineId(draft.Id, 1), fL = await LineId(foreign.Id, 1);

        // over-fulfilment: 8 fits, 8 + 8 > 10 is refused by PostgreSQL itself (the service is bypassed)
        Assert.Null((await Run(RawLink(order.Id, oL, d1.Id, d1L, 1, "8", "rw-raw-000001"))).State);
        var (state, message) = await Run(RawLink(order.Id, oL, d2.Id, d2L, 1, "3", "rw-raw-000002"));
        Assert.Equal("P0001", state);
        Assert.Contains("over-fulfilment", message);
        Assert.Equal("P0001", (await Run(RawLink(order.Id, oL, d2.Id, d2L, 3, "11", "rw-raw-000003"))).State);                                                  // more than the line itself
        // the target line capacity: the delivery line of 9 already holds 8, so 2 more from another source line would exceed it
        Assert.Contains("over-fulfilment", (await Run(RawLink(d2.Id, d2L, d1.Id, d1L, 1, "2", "rw-raw-000004"))).Message);
        // the other rules
        Assert.Contains("must be approved", (await Run(RawLink(order.Id, oL, draft.Id, draftL, 1, "1", "rw-raw-000005"))).Message);
        Assert.Contains("share the party", (await Run(RawLink(order.Id, oL, foreign.Id, fL, 1, "1", "rw-raw-000006"))).Message);
        Assert.Contains("lines of its own documents", (await Run(RawLink(order.Id, d1L, d2.Id, d2L, 1, "1", "rw-raw-000007"))).Message);                         // a line that is not the document's own
        Assert.Equal("23514", (await Run(RawLink(order.Id, oL, order.Id, oL, 1, "1", "rw-raw-000008"))).State);                                                // a link to itself
        Assert.Equal("23514", (await Run(RawLink(order.Id, oL, d2.Id, d2L, 1, "0", "rw-raw-000009"))).State);                                                  // quantity must be positive
        Assert.Equal("23514", (await Run(RawLink(order.Id, oL, d2.Id, d2L, 5, "1", "rw-raw-000010"))).State);                                                  // an unknown kind
        Assert.Equal("23505", (await Run(RawLink(order.Id, oL, d1.Id, d1L, 1, "1", "rw-raw-000011"))).State);                                                  // the same pair and kind (unique index)
        Assert.Equal("23505", (await Run(RawLink(order.Id, oL, d2.Id, d2L, 2, "1", "rw-raw-000001"))).State);                                                  // the same idempotency key of the same creator
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {DL.Table}"));                                                                                  // only the first statement committed

        // append-only: no UPDATE, DELETE or TRUNCATE
        Assert.Contains("append-only", (await Run($"UPDATE {DL.Table} SET {DL.Quantity} = 1")).Message);
        Assert.Contains("append-only", (await Run($"DELETE FROM {DL.Table}")).Message);
        Assert.Contains("append-only", (await Run($"TRUNCATE {DL.Table}")).Message);
        // the linked documents stay approved: PostgreSQL refuses a cancellation or a return to draft by a direct statement
        Assert.Contains("cannot leave the approved status", (await Run($"UPDATE {CD.Table} SET {CD.Status} = 5, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{order.Id}'")).Message);
        Assert.Contains("cannot leave the approved status", (await Run($"UPDATE {CD.Table} SET {CD.Status} = 1, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{d1.Id}'")).Message);
        // and through the service: cancel and a material edit are refused, a note edit is not (it keeps the document approved)
        Fails(await Docs.CancelAsync(Admin.Id, Company.Id, order.Id, order.Revision, "no longer needed"), ConfigStatus.Conflict, "document_has_links");
        Fails(await Docs.UpdateAsync(Admin.Id, Company.Id, d1.Id, d1.Revision, Doc(branch, party, item, unit, "ignored-key-xx", qty: 10m)), ConfigStatus.Conflict, "document_has_links");
        Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, d1.Id, d1.Revision, Doc(branch, party, item, unit, "ignored-key-xx", qty: 9m, note: "a note only")));
        // a document with no link is still cancellable (the restriction is only for linked documents)
        Ok(await Docs.CancelAsync(Admin.Id, Company.Id, d2.Id, d2.Revision, "not delivered"));
    }

    [PostgresFact]
    public async Task Sixteen_concurrent_links_against_the_last_remaining_quantity_create_exactly_the_allowed_ones()
    {
        var (branch, party, approver) = await Basics();
        var (item, unit) = await NewItemAndUnit("CC1");
        var orderA = await NewApproved(branch, party, "cc-orderA-001", approver, CommercialTestType.Name, (item, unit, 10m));
        var orderB = await NewApproved(branch, party, "cc-orderB-001", approver, CommercialTestType.Name, (item, unit, 10m));
        var deliveries = new List<CommercialDocumentView>();
        for (var i = 0; i < 16; i++) deliveries.Add(await NewApproved(branch, party, $"cc-deliv-{i:0000}", approver, CommercialTestType.Name, (item, unit, 2m)));

        // through the service: 16 deliveries of 1 piece each against an order line of 10 (the lines are locked first, so the refusals are the clean over_fulfilment)
        var results = await Task.WhenAll(deliveries.Select((d, i) => Links.CreateAsync(Admin.Id, Company.Id, Link(orderA.Id, 1, d.Id, 1, 1m, $"cc-svc-{i:0000}"))));
        Assert.Equal(10, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal("over_fulfilment", r.Code));
        Assert.Equal(10m, Assert.Single(await Links.QuantitiesAsync(Company.Id, orderA.Id)).Fulfilled);

        // through raw concurrent inserts (each in its own transaction, no service check): the database alone must serialize on the source line
        Guid bL = await LineId(orderB.Id, 1);
        var lines = new List<Guid>(); foreach (var d in deliveries) lines.Add(await LineId(d.Id, 1));
        var raw = await Task.WhenAll(deliveries.Select((d, i) => Run(RawLink(orderB.Id, bL, d.Id, lines[i], 1, "1", $"cc-raw-{i:0000}"))));
        Assert.Equal(10, raw.Count(r => r.State is null));
        Assert.All(raw.Where(r => r.State is not null), r => { Assert.Equal("P0001", r.State); Assert.Contains("over-fulfilment", r.Message); });
        Assert.Equal(10m, Assert.Single(await Links.QuantitiesAsync(Company.Id, orderB.Id)).Fulfilled);
        Assert.Equal(20L, await Sql<long>($"SELECT count(*) FROM {DL.Table}"));
        // no delivery line holds more than its quantity (2): each took one piece from each order, at most
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM (SELECT {DL.ToLineId} FROM {DL.Table} GROUP BY {DL.ToLineId}, {DL.Kind} HAVING sum({DL.Quantity}) > 2) x"));
    }

    [PostgresFact]
    public async Task Concurrent_links_into_one_target_line_and_a_concurrent_cancel_never_exceed_or_orphan()
    {
        var (branch, party, approver) = await Basics();
        var (item, unit) = await NewItemAndUnit("CT1");
        var delivery = await NewApproved(branch, party, "ct-deliv-0001", approver, CommercialTestType.Name, (item, unit, 3m));
        var orders = new List<CommercialDocumentView>();
        for (var i = 0; i < 8; i++) orders.Add(await NewApproved(branch, party, $"ct-order-{i:0000}", approver, CommercialTestType.Name, (item, unit, 5m)));
        // eight orders each try to fulfil 1 piece INTO one delivery line of 3: the target capacity is enforced under the same lock
        var results = await Task.WhenAll(orders.Select((o, i) => Links.CreateAsync(Admin.Id, Company.Id, Link(o.Id, 1, delivery.Id, 1, 1m, $"ct-svc-{i:0000}"))));
        Assert.Equal(3, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal("over_fulfilment", r.Code));
        // a cancel racing a link: whichever commits first wins and the other is refused; a cancelled document is never linked and a linked one is never cancelled
        var a = await NewApproved(branch, party, "ct-race-a-0001", approver, CommercialTestType.Name, (item, unit, 5m));
        var b = await NewApproved(branch, party, "ct-race-b-0001", approver, CommercialTestType.Name, (item, unit, 5m));
        var link = Links.CreateAsync(Admin.Id, Company.Id, Link(a.Id, 1, b.Id, 1, 1m, "ct-race-link-1"));
        var cancel = Docs.CancelAsync(Admin.Id, Company.Id, b.Id, b.Revision, "race");
        await Task.WhenAll(link, cancel);
        var linked = (await link).IsOk; var cancelled = (await cancel).IsOk;
        Assert.NotEqual(linked, cancelled);                                                                                     // exactly one of the two
        var status = (await Docs.GetAsync(Company.Id, b.Id))!.Status;
        Assert.Equal(cancelled ? CommercialStatus.Cancelled : CommercialStatus.Approved, status);
        Assert.Equal(linked ? 1L : 0L, await Sql<long>($"SELECT count(*) FROM {DL.Table} WHERE {DL.ToDocumentId} = '{b.Id}'"));
    }

    [PostgresFact]
    public async Task A_link_racing_a_material_edit_never_deadlocks_has_exactly_one_winner_and_keeps_every_invariant()
    {
        var (branch, party, approver) = await Basics();
        var (item, unit) = await NewItemAndUnit("RE1");
        for (var round = 0; round < 12; round++)
        {
            var source = await NewApproved(branch, party, $"re-src-{round:0000}", approver, CommercialTestType.Name, (item, unit, 5m));
            var target = await NewApproved(branch, party, $"re-tgt-{round:0000}", approver, CommercialTestType.Name, (item, unit, 5m));
            var editTarget = round % 2 == 0;                                                                                    // the edit hits the target of the link in even rounds, the source in odd rounds
            var edited = editTarget ? target : source;
            var link = Links.CreateAsync(Admin.Id, Company.Id, Link(source.Id, 1, target.Id, 1, 1m, $"re-link-{round:0000}"));
            var edit = Docs.UpdateAsync(Admin.Id, Company.Id, edited.Id, edited.Revision, Doc(branch, party, item, unit, "ignored-key-xx", qty: 6m));
            await Task.WhenAll(link, edit);
            var l = await link; var e = await edit;
            Assert.False(l.Code is "invariant_violation", $"round {round}: link {l.Status} {l.Code}");                           // no unmapped failure (a deadlock was a 40P01 before the fix)
            Assert.False(e.Code is "invariant_violation", $"round {round}: edit {e.Status} {e.Code}");
            Assert.NotEqual(l.IsOk, e.IsOk);                                                                                    // exactly one winner
            var links = await Sql<long>($"SELECT count(*) FROM {DL.Table} WHERE {DL.FromDocumentId} = '{source.Id}' AND {DL.ToDocumentId} = '{target.Id}'");
            var status = (await Docs.GetAsync(Company.Id, edited.Id))!.Status;
            if (l.IsOk) { Assert.Equal(1L, links); Assert.Equal(CommercialStatus.Approved, status); Assert.Equal("document_has_links", e.Code); }   // the link won: the edited document stays approved and unchanged
            else { Assert.Equal(0L, links); Assert.Equal(CommercialStatus.Draft, status); Assert.Equal("document_not_approved", l.Code); }          // the edit won: the document went back to draft, so it can no longer be linked
        }
    }

    [PostgresFact]
    public async Task Linking_is_branch_scoped_needs_update_on_the_target_and_reveals_nothing_across_branches_or_companies()
    {
        var (branchA, party, approver) = await Basics();
        var branchB = await NewBranchId("BB");
        var (item, unit) = await NewItemAndUnit("AZ1");
        var o = await NewApproved(branchA, party, "az-order-0001", approver, CommercialTestType.Name, (item, unit, 5m));
        var d = await NewApproved(branchA, party, "az-deliv-0001", approver, CommercialTestType.Name, (item, unit, 5m));
        var ob = await NewApproved(branchB, party, "az-orderB-001", approver, CommercialTestType.Name, (item, unit, 5m));
        var db = await NewApproved(branchB, party, "az-delivB-001", approver, CommercialTestType.Name, (item, unit, 5m));
        var viewCode = PermissionCatalog.Codes.CommercialView; var updateCode = PermissionCatalog.Codes.CommercialUpdate;
        var (linkerA, _) = await Member([viewCode, updateCode], ScopeKind.Branch, branchA);
        var (viewerA, _) = await Member([viewCode], ScopeKind.Branch, branchA);
        var outsider = await NewUser("linkoutsider");

        // update on the target's branch links; view alone, another branch and no permission are refused with the same refusal
        var ok = Ok(await LinkMgmt.CreateAsync(linkerA.Id, Company.Id, Link(o.Id, 1, d.Id, 1, 2m, "az-key-000001")));
        Assert.Equal(2m, ok.Quantity);
        Fails(await LinkMgmt.CreateAsync(viewerA.Id, Company.Id, Link(o.Id, 1, d.Id, 1, 1m, "az-key-000002")), ConfigStatus.Forbidden, "forbidden");
        Fails(await LinkMgmt.CreateAsync(outsider.Id, Company.Id, Link(o.Id, 1, d.Id, 1, 1m, "az-key-000003")), ConfigStatus.Forbidden, "forbidden");
        Fails(await LinkMgmt.CreateAsync(linkerA.Id, Company.Id, Link(ob.Id, 1, db.Id, 1, 1m, "az-key-000004")), ConfigStatus.Forbidden, "forbidden");          // both documents are of branch B
        Fails(await LinkMgmt.CreateAsync(linkerA.Id, Company.Id, Link(o.Id, 1, Guid.NewGuid(), 1, 1m, "az-key-000005")), ConfigStatus.Forbidden, "forbidden");  // a missing target looks the same to a branch-scoped caller
        Invalid(await LinkMgmt.CreateAsync(linkerA.Id, Company.Id, Link(ob.Id, 1, d.Id, 1, 1m, "az-key-000006")), "fromDocumentId", "document_not_found");      // a source of another branch is simply not found
        Invalid(await LinkMgmt.CreateAsync(linkerA.Id, Company.Id, Link(ob.Id, 99, d.Id, 1, 1m, "az-key-000009")), "fromDocumentId", "document_not_found");      // L1: the branch is compared BEFORE any line is looked up, so a missing line of another branch is not told apart
        Fails(await LinkMgmt.CreateAsync(Admin.Id, Company.Id, Link(o.Id, 1, Guid.NewGuid(), 1, 1m, "az-key-000007")), ConfigStatus.NotFound, "commercial_document_not_found");   // a company-wide holder may be told
        // reading: view on the document's branch only
        Assert.Single(Ok(await LinkMgmt.ListAsync(viewerA.Id, Company.Id, d.Id)));
        Assert.Equal(2m, Ok(await LinkMgmt.QuantitiesAsync(viewerA.Id, Company.Id, o.Id)).Single().Fulfilled);
        Fails(await LinkMgmt.ListAsync(viewerA.Id, Company.Id, db.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await LinkMgmt.QuantitiesAsync(outsider.Id, Company.Id, o.Id), ConfigStatus.Forbidden, "forbidden");
        // a replay for a caller who may link but not view returns only the id and the replayed flag
        var (updaterOnly, _) = await Member([updateCode], ScopeKind.Branch, branchA);
        var o2 = await NewApproved(branchA, party, "az-order2-001", approver, CommercialTestType.Name, (item, unit, 5m));
        var d4 = await NewApproved(branchA, party, "az-deliv2-001", approver, CommercialTestType.Name, (item, unit, 5m));
        var first = Ok(await LinkMgmt.CreateAsync(updaterOnly.Id, Company.Id, Link(o2.Id, 1, d4.Id, 1, 1m, "az-key-000008")));
        var replay = Ok(await LinkMgmt.CreateAsync(updaterOnly.Id, Company.Id, Link(o2.Id, 1, d4.Id, 1, 1m, "az-key-000008")));
        Assert.Equal((first.Id, true, Guid.Empty, 0m), (replay.Id, replay.Replayed, replay.FromDocumentId, replay.Quantity));

        // L2: another REAL company never sees or links company A's documents (the ids do not exist in its database)
        var other = await ReadyCompany();
        var resolverB = SinglePrincipalResolver(other);
        var linksB = new DocumentLinkService(resolverB, new Hasm.Infrastructure.CompanyDatabase.CompanyAuditedTransactions(resolverB, Audit, Clock), Clock, TestRegistry());
        Invalid(await linksB.CreateAsync(Admin.Id, other.Id, Link(o.Id, 1, d.Id, 1, 1m, "az-key-000010")), "fromDocumentId", "document_not_found");
        Assert.Empty(await linksB.ListAsync(other.Id, d.Id));
        Assert.Empty(await linksB.QuantitiesAsync(other.Id, o.Id));
        Assert.Equal(2m, Assert.Single(await Links.QuantitiesAsync(Company.Id, o.Id)).Fulfilled);                              // company A is untouched
    }
}
