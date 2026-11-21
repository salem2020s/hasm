"use strict";
// Seeds reference data THROUGH THE REAL API, so every server-side rule (validation, RBAC, numbering, audit, period and approval rules,
// optimistic concurrency) applies exactly as for a user. Idempotent for items and parties (a repeated code is looked up, not duplicated).
// Journal entries and stock documents are NOT idempotent: a re-run adds more; use a fresh disposable company for a clean reference set.
const plan = require("./plan");
const { ApiError } = require("./client");

async function pool(n, count, fn, onProgress) {
  let next = 1, done = 0, firstError = null;
  const worker = async () => {
    while (!firstError) {
      const i = next++; if (i > count) return;
      try { await fn(i); } catch (e) { firstError = e; return; }
      done++; if (onProgress && done % 500 === 0) onProgress(done, count);
    }
  };
  await Promise.all(Array.from({ length: Math.max(1, n) }, worker));
  if (firstError) throw firstError;
}

async function withRetry(fn, stats, attempts = 3) {
  for (let a = 1; ; a++) {
    try { return await fn(); }
    catch (e) {
      const transient = !(e instanceof ApiError) || e.status === 429 || e.status >= 500;
      if (!transient || a >= attempts) throw e;
      stats.retries++; await new Promise((r) => setTimeout(r, 200 * a));
    }
  }
}

const HINTS = { base_currency_not_selected: "select the company base currency first", no_fiscal_period: "create a fiscal year that covers the entry dates (--year) first", fiscal_period_soft_closed: "the target period is closed" };
const hinted = (fn) => async (...a) => { try { return await fn(...a); } catch (e) { if (e instanceof ApiError && HINTS[e.code]) e.message += ` (precondition: ${HINTS[e.code]})`; throw e; } };

async function pagedAll(client, name, key, query, limit = 500) {
  const out = []; let offset = 0, total = Infinity;
  while (offset < total) {
    const r = await client.call(name, undefined, { query: { ...query, offset, limit } });
    out.push(...r.json[key]); total = r.json.total; offset += limit; if (!r.json[key].length) break;
  }
  return out;
}

/** Refuses a company that holds anything that is not load-test data (best effort, by counts). */
async function assertLoadTestOnly(client) {
  for (const [name, key] of [["items", "items"], ["parties", "parties"]]) {
    const all = (await client.call(name, undefined, { query: { limit: 1 } })).json.total;
    const lt = (await client.call(name, undefined, { query: { search: "LT-", limit: 1 } })).json.total;
    if (all !== lt) { const e = new Error(`refused: the company already holds ${all - lt} ${key} that are not load-test data (code LT-...)`); e.code = "company_not_loadtest_only"; throw e; }
  }
}

async function ensureFirst(client, listName, key, createName, body, code) {
  const list = (await client.call(listName)).json[key];
  const found = list.find((x) => x.code === code);
  return found || (await client.call(createName, undefined, { body })).json;
}

async function seed({ maker, approver, counts, seed: seedValue, year, concurrency = 4, log = () => {}, withRoles = true }) {
  const stats = { items: 0, itemsExisting: 0, parties: 0, partiesExisting: 0, roles: 0, journals: 0, journalsPosted: 0, stockDocs: 0, stockDocsApproved: 0, retries: 0 };
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
  const accountIds = accounts.map((a) => a.id);
  log(`prerequisites ok: ${accounts.length} accounts, ${warehouses.length} warehouses`);

  // items
  await pool(concurrency, counts.items, (i) => withRetry(async () => {
    const s = plan.item(seedValue, i);
    try { await maker.call("createItem", undefined, { body: { ...s, groupId: group.id, baseUnitId: unit.id } }); stats.items++; }
    catch (e) { if (e instanceof ApiError && e.code === "item_code_taken") stats.itemsExisting++; else throw e; }
  }, stats), (d, n) => log(`items ${d}/${n}`));

  // parties (+ customer role)
  await pool(concurrency, counts.parties, (i) => withRetry(async () => {
    const s = plan.party(seedValue, i); let id;
    try { id = (await maker.call("createParty", undefined, { body: { code: s.code, displayName: s.displayName, identity: { kind: 1, legalNameAr: s.legalNameAr, legalNameEn: s.legalNameEn } } })).json.id; stats.parties++; }
    catch (e) {
      const taken = e instanceof ApiError && e.status === 400 && (e.body.fields || []).some((f) => f.field === "code" && f.code === "code_taken");
      if (!taken) throw e;
      stats.partiesExisting++; return;
    }
    if (withRoles && s.customer) { await maker.call("createPartyRole", undefined, { body: { partyId: id, role: 1, paymentMode: 1 } }); stats.roles++; }
  }, stats), (d, n) => log(`parties ${d}/${n}`));

  // journal entries: create, submit, post (the ordinary workflow, by the maker)
  await pool(concurrency, counts.journals, (i) => withRetry(async () => {
    const s = plan.journal(seedValue, i, accountIds, year);
    const created = (await maker.call("journalCreate", undefined, { body: { ...s, branchId: mainBranch } })).json.entry;
    stats.journals++;
    const submitted = (await maker.call("journalSubmit", { id: created.id }, { ifMatch: created.revision })).json.entry;
    await maker.call("journalPost", { id: created.id }, { ifMatch: submitted.revision });
    stats.journalsPosted++;
  }, stats), (d, n) => log(`journals ${d}/${n}`));

  // stock documents: create, submit (maker), approve (approver). Approval of a stock document changes no stock figure by itself; posting is a later stage.
  if (counts.stockDocs) {
    const idByIndex = new Map(); const wanted = new Set();
    for (let i = 1; i <= counts.stockDocs; i++) for (const l of plan.stockDoc(seedValue, i, { items: counts.items, warehouses: warehouses.length, year }).lines) wanted.add(l.itemIndex);
    for (const it of await pagedAll(maker, "items", "items", { search: "LT-I-" })) idByIndex.set(Number(it.code.slice(5)), it.id);
    for (const idx of wanted) if (!idByIndex.has(idx)) throw new Error(`item LT-I-${String(idx).padStart(6, "0")} is missing: seed the items first`);
    await pool(concurrency, counts.stockDocs, (i) => withRetry(async () => {
      const s = plan.stockDoc(seedValue, i, { items: counts.items, warehouses: warehouses.length, year });
      const body = { docType: s.docType, sourceWarehouseId: s.sourceWarehouse === null ? undefined : warehouses[s.sourceWarehouse].id, targetWarehouseId: s.targetWarehouse === null ? undefined : warehouses[s.targetWarehouse].id,
        documentDate: s.documentDate, reference: s.reference, lines: s.lines.map((l) => ({ itemId: idByIndex.get(l.itemIndex), unitId: unit.id, quantity: l.quantity })) };
      const d = (await maker.call("stockDocCreate", undefined, { body })).json; stats.stockDocs++;
      const sub = await maker.call("stockDocSubmit", { id: d.id }, { ifMatch: d.revision });
      await approver.call("stockDocApprove", { id: d.id }, { ifMatch: sub.json.revision });
      stats.stockDocsApproved++;
    }, stats), (d, n) => log(`stock documents ${d}/${n}`));
  }
  return { stats, elapsedSeconds: Math.round((Date.now() - t0) / 1000), accountCount: accounts.length, warehouseCount: warehouses.length };
}
module.exports = { seed, pagedAll, assertLoadTestOnly };
