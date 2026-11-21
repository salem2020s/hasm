"use strict";
// Seeds reference data THROUGH THE REAL API, so every server-side rule (validation, RBAC, numbering, audit, period and approval rules,
// optimistic concurrency) applies exactly as for a user. Every record is idempotent by its code, description or reference, and the multi-step
// flows (journal entry: create, submit, post; stock document: create, submit, approve) are driven by the entity's CURRENT state read from the
// API, one step at a time: a failed or ambiguous call is never answered by repeating the whole chain or by creating a second record.
const plan = require("./plan");
const { ApiError } = require("./client");
const { assertLoadTestOnly } = require("./company");

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const isTransient = (e) => !(e instanceof ApiError) || e.status === 429 || e.status >= 500;

async function pool(n, from, to, fn, onProgress) {
  let next = from, done = 0, firstError = null;
  const worker = async () => {
    while (!firstError) {
      const i = next++; if (i > to) return;
      try { await fn(i); } catch (e) { firstError = e; return; }
      done++; if (onProgress && done % 500 === 0) onProgress(done, to - from + 1);
    }
  };
  await Promise.all(Array.from({ length: Math.max(1, n) }, worker));
  if (firstError) throw firstError;
}

/** Retries ONE call on a transient failure. */
async function attempt(fn, stats, tries = 4) {
  for (let a = 1; ; a++) {
    try { return await fn(); } catch (e) { if (!isTransient(e) || a >= tries) throw e; stats.retries++; await sleep(200 * a); }
  }
}

const HINTS = { base_currency_not_selected: "select the company base currency first", no_fiscal_period: "create a fiscal year that covers the entry dates (--year) first", fiscal_period_soft_closed: "the target period is closed" };
const hinted = (fn) => async (...a) => { try { return await fn(...a); } catch (e) { if (e instanceof ApiError && HINTS[e.code]) e.message += ` (precondition: ${HINTS[e.code]})`; throw e; } };

async function ensureFirst(client, listName, key, createName, body, code) {
  const found = (await client.call(listName)).json[key].find((x) => x.code === code);
  return found || (await client.call(createName, undefined, { body })).json;
}

/** Creates a record whose lookup is by exact text; after a transient/ambiguous failure it looks the record up before ever creating again. */
async function createOnce({ find, create, stats }) {
  const existing = await attempt(find, stats); if (existing) return { record: existing, created: false };
  for (let a = 1; ; a++) {
    try { return { record: await create(), created: true }; }
    catch (e) {
      if (!isTransient(e) || a >= 4) throw e;
      stats.retries++; await sleep(200 * a);
      const now = await attempt(find, stats); if (now) return { record: now, created: false };   // the failed create may have committed
    }
  }
}

/** Drives an entity to its final state one step at a time from the state the API reports. `steps` maps a state to { call, client }. */
async function drive({ id, get, done, steps, stats }) {
  for (let n = 0; n < 12; n++) {
    const cur = await attempt(get, stats);
    if (done(cur)) return cur;
    const step = steps(cur); if (!step) throw new Error(`entity ${id} is in a state the harness cannot advance (${JSON.stringify({ status: cur.status })})`);
    try { await step(cur); }
    catch (e) { if (e instanceof ApiError && e.status === 412) { stats.retries++; continue; } if (isTransient(e)) { stats.retries++; await sleep(200); continue; } throw e; }   // re-read the state, never repeat blindly
  }
  throw new Error(`entity ${id} did not reach its final state`);
}

async function seed({ maker, approver, counts, seed: seedValue, year, concurrency = 4, log = () => {}, withRoles = true }) {
  const stats = { items: 0, itemsExisting: 0, parties: 0, partiesExisting: 0, roles: 0, journals: 0, journalsExisting: 0, journalsPosted: 0, stockDocs: 0, stockDocsExisting: 0, stockDocsApproved: 0, retries: 0 };
  const t0 = Date.now();
  await assertLoadTestOnly(maker);
  maker.call = hinted(maker.call.bind(maker));

  const unit = await ensureFirst(maker, "units", "units", "createUnit", { code: "LT-PCS", nameAr: "قطعة", nameEn: "Piece", decimals: 0 }, "LT-PCS");
  const group = await ensureFirst(maker, "groups", "groups", "createGroup", { code: "LT-G", nameAr: "مجموعة الحمل", nameEn: "Load test group" }, "LT-G");
  const branches = (await maker.call("branches")).json.branches; const mainBranch = (branches.find((b) => b.isMain) || branches[0]).id;
  const warehouses = (await maker.call("warehouses")).json.warehouses.filter((w) => w.isActive);
  const accounts = (await maker.call("accounts")).json.accounts.filter((a) => a.postable && a.isActive && ["base", "any"].includes(a.currencyPolicy));
  if (counts.journals && accounts.length < 2) throw new Error("precondition: at least two active postable base-currency accounts are needed (apply a chart template first)");
  if (counts.stockDocs && !warehouses.length) throw new Error("precondition: at least one active warehouse is needed for stock documents");
  if (counts.stockDocs && !approver) throw new Error("precondition: stock documents need a second user to approve (separation of duties): set the approver credentials");
  if (counts.stockDocs && counts.items < 1) throw new Error("precondition: stock documents need items");
  const accountIds = accounts.map((a) => a.id);
  log(`prerequisites ok: ${accounts.length} accounts, ${warehouses.length} warehouses`);

  await pool(concurrency, 1, counts.items, async (i) => {
    const s = plan.item(seedValue, i);
    try { await attempt(() => maker.call("createItem", undefined, { body: { ...s, groupId: group.id, baseUnitId: unit.id } }), stats); stats.items++; }
    catch (e) { if (e instanceof ApiError && e.code === "item_code_taken") stats.itemsExisting++; else throw e; }
  }, (d, n) => log(`items ${d}/${n}`));

  await pool(concurrency, 1, counts.parties, async (i) => {
    const s = plan.party(seedValue, i); let id;
    try { id = (await attempt(() => maker.call("createParty", undefined, { body: { code: s.code, displayName: s.displayName, identity: { kind: 1, legalNameAr: s.legalNameAr, legalNameEn: s.legalNameEn } } }), stats)).json.id; stats.parties++; }
    catch (e) {
      const taken = e instanceof ApiError && e.status === 400 && (e.body.fields || []).some((f) => f.field === "code" && f.code === "code_taken");
      if (!taken) throw e; stats.partiesExisting++; return;   // an existing party: its role (if any) was created with it
    }
    if (withRoles && s.customer) { await attempt(() => maker.call("createPartyRole", undefined, { body: { partyId: id, role: 1, paymentMode: 1 } }), stats); stats.roles++; }
  }, (d, n) => log(`parties ${d}/${n}`));

  // journal entries: create, submit, post (the ordinary workflow, by the maker)
  await pool(concurrency, 1, counts.journals, async (i) => {
    const s = plan.journal(seedValue, i, accountIds, year);
    const { record, created } = await createOnce({ stats,
      find: async () => (await maker.call("journalList", undefined, { query: { q: s.description, limit: 50 } })).json.entries.find((e) => e.description === s.description),
      create: async () => (await maker.call("journalCreate", undefined, { body: { ...s, branchId: mainBranch } })).json.entry });
    created ? stats.journals++ : stats.journalsExisting++;
    await drive({ id: record.id, stats, get: async () => (await maker.call("journalGet", { id: record.id })).json.entry, done: (e) => e.status === "posted",
      steps: (e) => e.status === "draft" ? (c) => maker.call("journalSubmit", { id: record.id }, { ifMatch: c.revision }) : e.status === "approved" ? (c) => maker.call("journalPost", { id: record.id }, { ifMatch: c.revision }) : null });
    stats.journalsPosted++;
  }, (d, n) => log(`journals ${d}/${n}`));

  // stock documents: receipts first, then issues and transfers; create, submit (maker), approve (approver). Approval changes no stock figure by itself.
  if (counts.stockDocs) {
    const W = warehouses.length; const idByIndex = new Map(); const wanted = new Set(); const specOf = (i) => plan.stockDoc(seedValue, i, { items: counts.items, warehouses: W, year, total: counts.stockDocs });
    for (let i = 1; i <= counts.stockDocs; i++) for (const l of specOf(i).lines) wanted.add(l.itemIndex);
    for (let offset = 0, total = Infinity; offset < total;) {
      const r = (await maker.call("items", undefined, { query: { search: "LT-I-", offset, limit: 500 } })).json; total = r.total;
      for (const it of r.items) idByIndex.set(Number(it.code.slice(5)), it.id);
      if (r.items.length === 0) break; offset += r.items.length;   // by the rows returned (the server may cap a page)
    }
    for (const idx of wanted) if (!idByIndex.has(idx)) throw new Error(`item LT-I-${String(idx).padStart(6, "0")} is missing: seed the items first`);
    const one = async (i) => {
      const s = specOf(i);
      const { record, created } = await createOnce({ stats,
        find: async () => (await maker.call("stockDocList", undefined, { query: { search: s.reference, limit: 50 } })).json.documents.find((d) => d.reference === s.reference),
        create: async () => (await maker.call("stockDocCreate", undefined, { body: { docType: s.docType, sourceWarehouseId: s.sourceWarehouse === null ? undefined : warehouses[s.sourceWarehouse].id, targetWarehouseId: s.targetWarehouse === null ? undefined : warehouses[s.targetWarehouse].id,
          documentDate: s.documentDate, reference: s.reference, lines: s.lines.map((l) => ({ itemId: idByIndex.get(l.itemIndex), unitId: unit.id, quantity: l.quantity })) } })).json });
      created ? stats.stockDocs++ : stats.stockDocsExisting++;
      await drive({ id: record.id, stats, get: async () => (await maker.call("stockDocGet", { id: record.id })).json, done: (d) => d.status === 3,
        steps: (d) => d.status === 1 ? (c) => maker.call("stockDocSubmit", { id: record.id }, { ifMatch: c.revision }) : d.status === 2 ? (c) => approver.call("stockDocApprove", { id: record.id }, { ifMatch: c.revision }) : null });
      stats.stockDocsApproved++;
    };
    const receipts = plan.stockReceiptCount(counts.stockDocs, Math.max(1, Math.min(W, counts.items)));
    await pool(concurrency, 1, receipts, one, (d, n) => log(`stock receipts ${d}/${n}`));          // every receipt is approved before the first issue or transfer starts
    await pool(concurrency, receipts + 1, counts.stockDocs, one, (d) => log(`stock issues and transfers ${d}`));
  }
  return { stats, elapsedSeconds: Math.round((Date.now() - t0) / 1000), accountCount: accounts.length, warehouseCount: warehouses.length };
}
module.exports = { seed };
