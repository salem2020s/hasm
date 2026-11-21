"use strict";
// Smoke test of the load-test harness foundation (no database, no real API): determinism, the disposable-target guard, the results verdict,
// the route and request-field contract against the backend sources, and a seed plus short run against an in-process stub of the API.
// It proves the harness mechanics only. It says nothing about product performance and is not a substitute for a run against the real API.
const test = require("node:test");
const assert = require("node:assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const { execFileSync } = require("child_process");

const root = path.resolve(__dirname, "..", "..", "..");
const planLib = require("../lib/plan");
const { percentile } = require("../lib/stats");
const { assertDisposableTarget, ACK_VALUE } = require("../lib/guard");
const { inspectCompany } = require("../lib/company");
const { buildResults, REFERENCE, sanitizeEnvironment } = require("../lib/results");
const { ENDPOINTS, BODIES, QUERIES, ENUMS, RESPONSE_KEYS } = require("../lib/endpoints");
const { Client } = require("../lib/client");
const { seed } = require("../lib/seed");
const { defineScenarios, runLoad } = require("../lib/scenarios");

test("the plan is deterministic per seed, independent of order, and differs between seeds", () => {
  const c = { items: 300, parties: 120, journals: 60, stockDocs: 40 };
  assert.strictEqual(planLib.planDigest({ seed: "a", ...c }), planLib.planDigest({ seed: "a", ...c }));
  assert.notStrictEqual(planLib.planDigest({ seed: "a", ...c }), planLib.planDigest({ seed: "b", ...c }));
  assert.deepStrictEqual(planLib.item("a", 77), planLib.item("a", 77));           // any record regenerates alone
  assert.strictEqual(planLib.item("a", 5).code, "LT-I-000005");
  const j = planLib.journal("a", 3, ["x", "y", "z"], 2026);                       // every generated entry balances exactly
  const cents = (k) => Math.round(j.lines.reduce((s, l) => s + l[k] * 100, 0));
  assert.strictEqual(cents("debit"), cents("credit"));
  assert.ok(j.lines.every((l) => l.debit === 0 || l.credit === 0));
  assert.strictEqual(percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 95), 10);       // nearest rank, no interpolation
});

test("the guard refuses without the disposable acknowledgement, a non-loopback API without an environment file, and port 5432", () => {
  const ok = { apiUrl: "http://127.0.0.1:5000", env: { HASM_LOADTEST_DISPOSABLE: ACK_VALUE } };
  assertDisposableTarget(ok);
  assert.throws(() => assertDisposableTarget({ ...ok, env: {} }), /disposable/);
  assert.throws(() => assertDisposableTarget({ ...ok, apiUrl: "https://hasm.example.com" }), /non-loopback/);
  assert.throws(() => assertDisposableTarget({ ...ok, apiUrl: "http://hasm.example.com", environmentFile: {} }), /must be https/);
  assertDisposableTarget({ ...ok, apiUrl: "https://hasm.example.com", environmentFile: {} });
  assert.throws(() => assertDisposableTarget({ ...ok, databasePort: "5432" }), /5432/);
  assert.throws(() => assertDisposableTarget({ ...ok, env: { ...ok.env, PGPORT: "5432" } }), /5432/);
});

test("a scenario is within target when p95 <= target (inclusive), with every reference condition met", () => {
  const R = REFERENCE; const sc = defineScenarios().filter((x) => x.implemented !== false).map((x) => ({ ...x }));
  const samples = sc.map((x) => ({ scenario: x.id, ms: x.targetP95Ms, ok: true }));
  const res = buildResults({ tool: {}, seed: "s", startedAtUtc: "a", finishedAtUtc: "b", scenarios: sc, samples, load: { sessions: 50, durationSeconds: 3600 },
    data: { declared: {}, measured: { items: R.data.items, parties: R.data.parties, invoices: R.data.invoices, entriesAndStockMovements: R.data.entriesAndStockMovements } },
    declaredEnvironment: { vcpu: 8, ramGb: 32, diskGb: 500, ssd: true, backendAndPostgresTogether: true, companies: 5 } });
  assert.strictEqual(res.verdict, "MEASURED WITHIN TARGETS");                                             // equal to the target counts as within
  samples[0].ms += 1;
  assert.strictEqual(buildResults({ tool: {}, seed: "s", startedAtUtc: "a", finishedAtUtc: "b", scenarios: sc, samples, load: { sessions: 50, durationSeconds: 3600 }, data: res.data, declaredEnvironment: res.environment.declaredReferenceEnvironment }).verdict, "MEASURED, TARGETS NOT MET");
});

test("receipts come first, cycle through every warehouse, and issues and transfers draw only items of their own warehouse", () => {
  const total = 50, W = 3, items = 120; const receipts = planLib.stockReceiptCount(total, W);
  const docs = Array.from({ length: total }, (_, k) => planLib.stockDoc("s", k + 1, { items, warehouses: W, year: 2026, total }));
  assert.ok(docs.slice(0, receipts).every((d) => d.docType === 1) && docs.slice(receipts).every((d) => d.docType !== 1));
  assert.deepStrictEqual([...new Set(docs.slice(0, receipts).map((d) => d.targetWarehouse))].sort(), [0, 1, 2]);
  for (const d of docs) { const w = d.docType === 1 ? d.targetWarehouse : d.sourceWarehouse; assert.ok(d.lines.every((l) => (l.itemIndex - 1) % W === w)); assert.ok(d.lines.length >= 1); }
  assert.ok(docs.some((d) => d.docType === 3 && d.sourceWarehouse !== d.targetWarehouse));
});

test("the verdict is NOT VERIFIED unless every reference condition holds, and no number is ever invented", () => {
  const scenarios = defineScenarios();
  const samples = [{ scenario: "item_search", ms: 12, ok: true }, { scenario: "item_search", ms: 30, ok: true }];
  const base = { tool: {}, seed: "s", startedAtUtc: "t0", finishedAtUtc: "t1", scenarios, samples, load: { sessions: 1, durationSeconds: 5 } };
  const dataOf = (m) => ({ declared: { items: 999999 }, measured: m });   // the verdict ignores what was declared and uses what was measured
  const small = buildResults({ ...base, data: dataOf({ items: 10, parties: 5, invoices: 0, entriesAndStockMovements: 3 }), declaredEnvironment: null });
  assert.ok(small.verdictReasons.some((r) => /items 10 < 20000/.test(r)));
  assert.strictEqual(small.verdict, "NOT VERIFIED");
  assert.ok(small.verdictReasons.some((r) => /no reference environment declared/.test(r)));
  assert.strictEqual(small.scenarios.find((s) => s.id === "report_trial_balance").p95, null);       // no samples: null, not an estimate
  assert.ok(small.scenarios.find((s) => s.id === "invoice_save_post").implemented === false);
  const R = REFERENCE;
  const full = buildResults({ ...base, data: dataOf({ items: R.data.items, parties: R.data.parties, invoices: R.data.invoices, entriesAndStockMovements: R.data.entriesAndStockMovements }),
    declaredEnvironment: { vcpu: 8, ramGb: 32, diskGb: 500, ssd: true, backendAndPostgresTogether: true, companies: 5, secretNote: "do not copy", token: "abc" }, load: { sessions: 50, durationSeconds: 3600 } });
  assert.deepStrictEqual(Object.keys(full.environment.declaredReferenceEnvironment).sort(), ["backendAndPostgresTogether", "companies", "diskGb", "ramGb", "ssd", "vcpu"]);   // only the six documented keys reach the results
  assert.ok(!JSON.stringify(full).includes("do not copy") && !JSON.stringify(full).includes("abc"));
  assert.deepStrictEqual(sanitizeEnvironment({ vcpu: "8", ssd: true }), { ssd: true });
  assert.ok(full.verdictReasons.every((r) => /not implemented/.test(r)), "only the unimplemented scenarios may still block");   // stage 1 can never reach a verified verdict
  assert.strictEqual(full.verdict, "NOT VERIFIED");
});

// ---- contract against the backend sources
const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(d, e.name)) : e.name.endsWith(".cs") ? [path.join(d, e.name)] : []));
const norm = (p) => p.replace(/\{(\w+)(:[^}]*)?\}/g, "{$1}").replace(/\/+$/, "");
function routeTable() {
  const map = new Map();
  for (const f of walk(path.join(root, "src/backend/Hasm.Api"))) {
    const src = fs.readFileSync(f, "utf8"); const prefix = { app: "" };
    for (const m of src.matchAll(/var (\w+) = (\w+)\.MapGroup\("([^"]*)"\)/g)) prefix[m[1]] = (prefix[m[2]] ?? "") + m[3];
    for (const m of src.matchAll(/\b(\w+)\.Map(Get|Post|Put|Delete)\("([^"]*)"(?:,\s*(\w+)\s*\))?/g)) if (m[1] in prefix) map.set(`${m[2].toUpperCase()} ${norm(prefix[m[1]] + m[3])}`, { file: f, handler: m[4] || null, src });
  }
  return map;
}
/** Parameter names of a handler method (top-level commas only). */
function handlerParams(src, name) {
  const m = src.match(new RegExp(`static (?:async )?[\\w<>?,\\[\\] ]+? ${name}\\(`)); if (!m) return null;
  let i = m.index + m[0].length, depth = 0, cur = "", out = [];
  for (; i < src.length; i++) { const ch = src[i]; if (ch === "(" || ch === "<") depth++; if (ch === ")" || ch === ">") { if (depth === 0) break; depth--; } if (ch === "," && depth === 0) { out.push(cur); cur = ""; } else cur += ch; }
  out.push(cur); return out.map((p) => p.trim().split(/\s+/).pop()).filter(Boolean);
}
function recordFields(name) {
  for (const f of [...walk(path.join(root, "src/backend/Hasm.Api")), ...walk(path.join(root, "src/backend/Hasm.Infrastructure"))]) {
    const m = fs.readFileSync(f, "utf8").match(new RegExp(`record ${name}\\(([^)]*(?:\\([^)]*\\)[^)]*)*)\\)`));
    if (m) return [...m[1].matchAll(/\b[\w<>?,.\[\] ]+? (\w+)(?:\s*=\s*[^,]+)?(?:,|$)/g)].map((x) => x[1][0].toLowerCase() + x[1].slice(1));
  }
  return null;
}
test("every route and request field the harness uses exists in the backend sources", () => {
  const routes = routeTable();
  for (const [name, [method, p]] of Object.entries(ENDPOINTS)) assert.ok(routes.has(`${method} ${norm(p)}`), `${name}: ${method} ${p} is not in the backend route table`);
  for (const [name, params] of Object.entries(QUERIES)) {          // query parameter names against the handler signature
    const [method, p] = ENDPOINTS[name]; const r = routes.get(`${method} ${norm(p)}`); const have = handlerParams(r.src, r.handler);
    assert.ok(have, `${name}: handler ${r.handler} not found`);
    for (const q of params) assert.ok(have.includes(q), `${name}: query parameter ${q} is not a parameter of ${r.handler} (has: ${have.join(",")})`);
  }
  const allSrc = walk(path.join(root, "src/backend")).map((f) => fs.readFileSync(f, "utf8")).join("\n");
  for (const [en, member, value] of ENUMS) {                        // enum values against the backend enums
    const m = allSrc.match(new RegExp(`enum ${en}\\b[^{]*\\{([^}]*)\\}`)); assert.ok(m, `enum ${en} not found`);
    let next = 0, found = null; for (const part of m[1].split(",").map((x) => x.trim()).filter(Boolean)) { const [n, v] = part.split("=").map((x) => x.trim()); if (v !== undefined) next = Number(v); if (n === member) found = next; next++; }
    assert.strictEqual(found, value, `${en}.${member} is ${found}, the harness uses ${value}`);
  }
  for (const [file, keys] of Object.entries(RESPONSE_KEYS)) {       // response keys against the endpoint sources
    const src = fs.readFileSync(path.join(root, "src/backend/Hasm.Api", file), "utf8");
    for (const k of keys) assert.ok(new RegExp(`\\b${k}\\s*=`).test(src), `${file}: response key ${k} is not produced`);
  }
  for (const [record, fields] of Object.entries(BODIES)) {
    const have = recordFields(record); assert.ok(have, `record ${record} not found`);
    for (const f of fields) assert.ok(have.includes(f), `${record}.${f} is not a field of the backend record (has: ${have.join(",")})`);
  }
});

// ---- seed and run against an in-process stub (mechanics only)
function stubApi() {
  const db = { items: [], parties: [], roles: 0, journals: new Map(), docs: new Map(), nextId: 1, writes: 0, inject: {}, calls: {} };
  const id = () => `00000000-0000-0000-0000-${String(db.nextId++).padStart(12, "0")}`;
  const send = (res, status, body, etag) => { res.writeHead(status, { "content-type": "application/json", ...(etag ? { ETag: `"${etag}"` } : {}) }); res.end(JSON.stringify(body)); };
  const unit = { id: id(), code: "LT-PCS" }, group = { id: id(), code: "LT-G" }; let unitMade = false, groupMade = false;
  const server = http.createServer((req, res) => {
    let raw = ""; req.on("data", (d) => (raw += d)); req.on("end", () => {
      const u = new URL(req.url, "http://x"); const p = u.pathname; const q = u.searchParams; const body = raw ? JSON.parse(raw) : {};
      if (req.method !== "GET" && !req.headers["x-hasm-request"]) return send(res, 403, { error: "missing_request_header" });
      if (p !== "/api/v1/auth/login" && req.headers.authorization !== "Bearer tok") return send(res, 401, { error: "unauthenticated" });
      if (req.method !== "GET" && !["/api/v1/auth/login", "/api/v1/session/company"].includes(p)) db.writes++;
      const injectOnce = (k) => { if (db.inject[k]) { db.inject[k] = false; return true; } return false; };
      const CAP = { parties: 200, documents: 200 };   // the real server caps these two lists at 200 rows per page
      const page = (all, key, text) => { const s = (q.get("search") || "").toLowerCase(); const hit = s ? all.filter((x) => text(x).toLowerCase().includes(s)) : all;
        const o = Number(q.get("offset") || 0), l = Math.min(CAP[key] || 500, Number(q.get("limit") || 50)); return { [key]: hit.slice(o, o + l), total: hit.length }; };
      if (p === "/api/v1/auth/login") return send(res, 200, { token: "tok" });
      if (p === "/api/v1/session/company") return send(res, 200, {});
      if (p === "/api/v1/company/inventory/units") { if (req.method === "POST") { unitMade = true; return send(res, 201, unit, 1); } return send(res, 200, { units: unitMade ? [unit] : [] }); }
      if (p === "/api/v1/company/inventory/groups") { if (req.method === "POST") { groupMade = true; return send(res, 201, group, 1); } return send(res, 200, { groups: groupMade ? [group] : [] }); }
      if (p === "/api/v1/company/inventory/items") {
        if (req.method === "POST") { if (db.items.some((i) => i.code === body.code)) return send(res, 409, { error: "item_code_taken" }); const it = { id: id(), ...body, revision: 1 }; db.items.push(it); return send(res, 201, it, 1); }
        return send(res, 200, page(db.items, "items", (x) => x.code + " " + (x.nameEn || "")));
      }
      if (p === "/api/v1/company/parties") {
        if (req.method === "POST") { if (db.parties.some((x) => x.code === body.code)) return send(res, 400, { error: "invalid_request", fields: [{ field: "code", code: "code_taken" }] }); const x = { id: id(), ...body, revision: 1 }; db.parties.push(x); return send(res, 201, x, 1); }
        return send(res, 200, page(db.parties, "parties", (x) => x.code + " " + (x.displayName || "")));
      }
      if (p === "/api/v1/company/party-roles") { if (req.method === "POST") { db.roles++; return send(res, 201, { id: id() }, 1); } return send(res, 200, { items: [], can: {} }); }
      if (p === "/api/v1/company/branches") return send(res, 200, { branches: [{ id: id(), isMain: true }] });
      if (p === "/api/v1/company/warehouses") return send(res, 200, { warehouses: [{ id: "w1", isActive: true }, { id: "w2", isActive: true }] });
      if (p === "/api/v1/company/accounts") return send(res, 200, { accounts: ["a", "b", "c"].map((c) => ({ id: c, postable: true, isActive: true, currencyPolicy: "base" })) });
      if (p === "/api/v1/company/journal" && req.method === "GET") {
        const t = (q.get("q") || "").toLowerCase(); const all = [...db.journals.values()].filter((e) => !t || e.description.toLowerCase().includes(t));
        const o = Number(q.get("offset") || 0), l = Math.min(500, Number(q.get("limit") || 100)); return send(res, 200, { entries: all.slice(o, o + l) });
      }
      if (p === "/api/v1/company/journal" && req.method === "POST") {
        const debit = body.lines.reduce((s, l) => s + Math.round(l.debit * 100), 0), credit = body.lines.reduce((s, l) => s + Math.round(l.credit * 100), 0);
        if (debit !== credit) return send(res, 400, { error: "entry_not_balanced" });
        const e = { id: id(), revision: 1, status: "draft", description: body.description }; db.journals.set(e.id, e);
        if (injectOnce("createAmbiguous")) return send(res, 503, { error: "unavailable" });             // committed, but the client never learns it
        return send(res, 201, { entry: e }, 1);
      }
      let m = p.match(/^\/api\/v1\/company\/journal\/([^/]+)$/);
      if (m) return send(res, 200, { entry: db.journals.get(m[1]) });
      m = p.match(/^\/api\/v1\/company\/journal\/([^/]+)\/(submit|post)$/);
      if (m) {
        const e = db.journals.get(m[1]); db.calls[m[2]] = (db.calls[m[2]] || 0) + 1;
        if (m[2] === "submit" && injectOnce("submitDown")) return send(res, 503, { error: "unavailable" });      // not applied
        if (req.headers["if-match"] !== `"${e.revision}"`) return send(res, 412, { error: "revision_conflict" });
        e.revision++; e.status = m[2] === "submit" ? "approved" : "posted";
        if (m[2] === "post" && injectOnce("postAmbiguous")) return send(res, 503, { error: "unavailable" });     // applied, answer lost
        return send(res, 200, { entry: e }, e.revision);
      }
      if (p === "/api/v1/company/stock-documents" && req.method === "GET") return send(res, 200, page([...db.docs.values()], "documents", (x) => x.reference));
      if (p === "/api/v1/company/stock-documents" && req.method === "POST") { const d = { id: id(), revision: 1, status: 1, reference: body.reference, docType: body.docType, lines: body.lines }; db.docs.set(d.id, d); return send(res, 201, d, 1); }
      m = p.match(/^\/api\/v1\/company\/stock-documents\/([^/]+)$/);
      if (m) return send(res, 200, db.docs.get(m[1]));
      m = p.match(/^\/api\/v1\/company\/stock-documents\/([^/]+)\/(submit|approve)$/);
      if (m) { const d = db.docs.get(m[1]); if (req.headers["if-match"] !== `"${d.revision}"`) return send(res, 412, { error: "revision_conflict" }); d.revision++; d.status = m[2] === "submit" ? 2 : 3; return send(res, 200, d, d.revision); }
      return send(res, 200, { journal: [], documents: [], total: 0, lines: [] });
    });
  });
  return { server, db };
}

test("seed and a short run against a stub API: counts, idempotency, never a second record after an ambiguous failure, results file shape", async () => {
  const { server, db } = stubApi(); await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const base = `http://127.0.0.1:${server.address().port}`;
  const mk = async () => { const c = new Client({ baseUrl: base }); await c.login("m", "p"); await c.selectCompany("c"); return c; };
  try {
    const counts = { items: 40, parties: 25, journals: 12, stockDocs: 8 };
    db.inject = { createAmbiguous: true, submitDown: true, postAmbiguous: true };
    const r = await seed({ maker: await mk(), approver: await mk(), counts, seed: "smoke", year: 2026, concurrency: 1 });
    assert.deepStrictEqual([r.stats.items, r.stats.parties, r.stats.journalsPosted, r.stats.stockDocsApproved], [40, 25, 12, 8]);
    assert.strictEqual(db.journals.size, 12, "no duplicate entry after an ambiguous create");                  // the lost create answer was resolved by lookup, not by creating again
    assert.ok([...db.journals.values()].every((e) => e.status === "posted" && e.revision === 3), "each entry was submitted once and posted once");
    assert.deepStrictEqual([db.calls.submit, db.calls.post], [13, 12]);   // 12 + the one failed submit (that step alone was repeated); post: the call whose answer was lost is NOT repeated, the state read shows it posted
    assert.ok(r.stats.retries >= 3);
    assert.ok(db.roles > 0 && db.roles < 25);
    const docs = [...db.docs.values()].sort((a, b) => a.reference.localeCompare(b.reference));
    assert.ok(docs.every((d) => d.status === 3));
    assert.deepStrictEqual(docs.map((d) => d.docType), [...docs.map((d) => d.docType)].sort((a, b) => (a === 1 ? 0 : 1) - (b === 1 ? 0 : 1)));   // receipts first
    const again = await seed({ maker: await mk(), approver: await mk(), counts, seed: "smoke", year: 2026 });   // a full re-run creates nothing
    assert.deepStrictEqual([again.stats.items, again.stats.itemsExisting, again.stats.partiesExisting, again.stats.journals, again.stats.journalsExisting, again.stats.stockDocs, again.stats.stockDocsExisting], [0, 40, 25, 0, 12, 0, 8]);
    assert.deepStrictEqual([db.items.length, db.journals.size, db.docs.size], [40, 12, 8]);

    // the company check: case-sensitive prefix, and journals and stock documents count too
    const reject = async (mutate, undo) => { mutate(); try { await assert.rejects(seed({ maker: await mk(), approver: await mk(), counts, seed: "smoke", year: 2026 }), /not load-test data/); } finally { undo(); } };
    for (const code of ["REAL-1", "BOLT-7", "Salt-Free Butter", "lt-I-000001"]) await reject(() => db.items.push({ id: "x", code, nameEn: "n" }), () => db.items.pop());
    await reject(() => db.parties.push({ id: "p", code: "CUST-1", displayName: "x" }), () => db.parties.pop());
    await reject(() => db.journals.set("j", { id: "j", description: "Opening balance", status: "posted" }), () => db.journals.delete("j"));
    await reject(() => db.docs.set("d", { id: "d", reference: "PO-9", status: 3 }), () => db.docs.delete("d"));
    assert.strictEqual((await inspectCompany(await mk())).foreignCount, 0);
    assert.deepStrictEqual((await inspectCompany(await mk())).measured, { items: 40, parties: 25, journals: 12, stockDocs: 8 });

    const client = await mk(); const scenarios = defineScenarios();
    const ctx = { client, seed: "smoke", counts, year: 2026, accountIds: ["a", "b", "c"], branchId: "b1" };
    const { samples } = await runLoad({ ctx, scenarios, concurrency: 3, iterations: 60, warmup: 5 });
    assert.ok(samples.length >= 60 && samples.every((s) => s.ok && typeof s.ms === "number"));
    const res = buildResults({ tool: { name: "t" }, seed: "smoke", startedAtUtc: "a", finishedAtUtc: "b", scenarios, samples, data: { declared: counts, measured: { items: 40, parties: 25, invoices: 0, entriesAndStockMovements: 20 } }, declaredEnvironment: null, load: { sessions: 1, durationSeconds: 1 } });
    assert.strictEqual(res.verdict, "NOT VERIFIED");
    assert.strictEqual(res.environment.declaredReferenceEnvironment, null);
    assert.ok(res.scenarios.find((s) => s.id === "item_search").samples > 0);
    JSON.parse(JSON.stringify(res));
  } finally { server.close(); }
});

test("the company check pages by the rows returned: a foreign record behind a server page cap is found and the counts are exact", async () => {
  const { server, db } = stubApi(); await new Promise((r) => server.listen(0, "127.0.0.1", r));
  try {
    const c = new Client({ baseUrl: `http://127.0.0.1:${server.address().port}` }); await c.login("m", "p"); await c.selectCompany("c");
    for (let i = 1; i <= 1000; i++) db.parties.push({ id: `p${i}`, code: `LT-P-${String(i).padStart(6, "0")}`, displayName: "x" });
    for (let i = 1; i <= 450; i++) db.docs.set(`d${i}`, { id: `d${i}`, reference: `LT-S-${String(i).padStart(6, "0")}`, status: 3 });
    let r = await inspectCompany(c);
    assert.deepStrictEqual([r.measured.parties, r.measured.stockDocs, r.foreignCount], [1000, 450, 0]);          // 200-row pages: all rows counted, none skipped
    db.parties[300].code = "CUST-300"; db.docs.get("d300").reference = "PO-300";                                // rows 200-499 of a 500-row window
    r = await inspectCompany(c);
    assert.strictEqual(r.foreignCount, 2);
    await assert.rejects(require("../lib/company").assertLoadTestOnly(c), /not load-test data/);
  } finally { server.close(); }
});

test("the command line refuses without the acknowledgement; run refuses a company with non-load-test data before any write; plan prints a digest", async () => {
  const cli = path.join(root, "tools/loadtest/loadtest.js");
  const env = { ...process.env }; delete env.HASM_LOADTEST_DISPOSABLE;
  const run = (args, e) => new Promise((resolve) => require("child_process").execFile("node", [cli, ...args], { env: e }, (err, stdout, stderr) => resolve({ status: err ? err.code : 0, stdout, stderr })));
  const refused = await run(["seed", "--api", "http://127.0.0.1:1", "--company", "c"], env);
  assert.ok(refused.status === 3 && /disposable/.test(refused.stderr));

  const OUT = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "hasm-loadtest-test-")), "results.json");
  const { server, db } = stubApi(); await new Promise((r) => server.listen(0, "127.0.0.1", r));
  try {
    const e = { ...env, HASM_LOADTEST_DISPOSABLE: ACK_VALUE, HASM_LT_LOGIN: "m", HASM_LT_PASSWORD: "p" };
    const args = ["run", "--api", `http://127.0.0.1:${server.address().port}`, "--company", "c", "--items", "5", "--parties", "5", "--journals", "5", "--stock-docs", "0", "--iterations", "10", "--out", OUT];
    db.items.push({ id: "x", code: "Salt-Free Butter", nameEn: "real data" });
    const bad = await run(args, e);
    assert.ok(bad.status !== 0 && /not load-test data/.test(bad.stderr), bad.stderr);
    assert.strictEqual(db.writes, 0, "run wrote nothing into a company with foreign data");
    assert.ok(!fs.existsSync(OUT));
    db.items.pop();
    const good = await run(args, e);
    assert.ok(good.status === 0 && /^NOT VERIFIED/.test(good.stdout), good.stderr + good.stdout);
    assert.ok(db.writes > 0);                                                                                      // an empty company is acceptable: the run's own journal entries are load-test data
  } finally { server.close(); fs.rmSync(path.dirname(OUT), { recursive: true, force: true }); }
  const out = JSON.parse(execFileSync("node", [cli, "plan", "--items", "10", "--parties", "5", "--journals", "3", "--stock-docs", "2"], { encoding: "utf8" }));
  assert.match(out.digest, /^[0-9a-f]{64}$/);
});
