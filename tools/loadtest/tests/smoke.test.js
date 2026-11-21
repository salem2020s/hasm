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
const { buildResults, REFERENCE } = require("../lib/results");
const { ENDPOINTS, BODIES } = require("../lib/endpoints");
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
  assert.throws(() => assertDisposableTarget({ ...ok, databasePort: "5432" }), /5432/);
  assert.throws(() => assertDisposableTarget({ ...ok, env: { ...ok.env, PGPORT: "5432" } }), /5432/);
});

test("the verdict is NOT VERIFIED unless every reference condition holds, and no number is ever invented", () => {
  const scenarios = defineScenarios();
  const samples = [{ scenario: "item_search", ms: 12, ok: true }, { scenario: "item_search", ms: 30, ok: true }];
  const base = { tool: {}, seed: "s", startedAtUtc: "t0", finishedAtUtc: "t1", scenarios, samples, load: { sessions: 1, durationSeconds: 5 } };
  const small = buildResults({ ...base, data: { items: 10, parties: 5, invoices: 0, entriesAndStockMovements: 3 }, declaredEnvironment: null });
  assert.strictEqual(small.verdict, "NOT VERIFIED");
  assert.ok(small.verdictReasons.some((r) => /no reference environment declared/.test(r)));
  assert.strictEqual(small.scenarios.find((s) => s.id === "report_trial_balance").p95, null);       // no samples: null, not an estimate
  assert.ok(small.scenarios.find((s) => s.id === "invoice_save_post").implemented === false);
  const R = REFERENCE;
  const full = buildResults({ ...base, data: { items: R.data.items, parties: R.data.parties, invoices: R.data.invoices, entriesAndStockMovements: R.data.entriesAndStockMovements },
    declaredEnvironment: { vcpu: 8, ramGb: 32, diskGb: 500, ssd: true, backendAndPostgresTogether: true, companies: 5 }, load: { sessions: 50, durationSeconds: 3600 } });
  assert.ok(full.verdictReasons.every((r) => /not implemented/.test(r)), "only the unimplemented scenarios may still block");   // stage 1 can never reach a verified verdict
  assert.strictEqual(full.verdict, "NOT VERIFIED");
});

// ---- contract against the backend sources
const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(d, e.name)) : e.name.endsWith(".cs") ? [path.join(d, e.name)] : []));
const norm = (p) => p.replace(/\{(\w+)(:[^}]*)?\}/g, "{$1}").replace(/\/+$/, "");
function routeTable() {
  const set = new Set();
  for (const f of walk(path.join(root, "src/backend/Hasm.Api"))) {
    const src = fs.readFileSync(f, "utf8"); const prefix = { app: "" };
    for (const m of src.matchAll(/var (\w+) = (\w+)\.MapGroup\("([^"]*)"\)/g)) prefix[m[1]] = (prefix[m[2]] ?? "") + m[3];
    for (const m of src.matchAll(/\b(\w+)\.Map(Get|Post|Put|Delete)\("([^"]*)"/g)) if (m[1] in prefix) set.add(`${m[2].toUpperCase()} ${norm(prefix[m[1]] + m[3])}`);
  }
  return set;
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
  for (const [record, fields] of Object.entries(BODIES)) {
    const have = recordFields(record); assert.ok(have, `record ${record} not found`);
    for (const f of fields) assert.ok(have.includes(f), `${record}.${f} is not a field of the backend record (has: ${have.join(",")})`);
  }
});

// ---- seed and run against an in-process stub (mechanics only)
function stubApi() {
  const db = { items: [], parties: [], roles: 0, journals: new Map(), docs: new Map(), nextId: 1 };
  const id = () => `00000000-0000-0000-0000-${String(db.nextId++).padStart(12, "0")}`;
  const send = (res, status, body, etag) => { res.writeHead(status, { "content-type": "application/json", ...(etag ? { ETag: `"${etag}"` } : {}) }); res.end(JSON.stringify(body)); };
  const unit = { id: id(), code: "LT-PCS" }, group = { id: id(), code: "LT-G" }; let unitMade = false, groupMade = false;
  const server = http.createServer((req, res) => {
    let raw = ""; req.on("data", (d) => (raw += d)); req.on("end", () => {
      const u = new URL(req.url, "http://x"); const p = u.pathname; const q = u.searchParams; const body = raw ? JSON.parse(raw) : {};
      if (req.method !== "GET" && !req.headers["x-hasm-request"]) return send(res, 403, { error: "missing_request_header" });
      if (p !== "/api/v1/auth/login" && req.headers.authorization !== "Bearer tok") return send(res, 401, { error: "unauthenticated" });
      const page = (all, key) => { const s = (q.get("search") || "").toLowerCase(); const hit = s ? all.filter((x) => (x.code + " " + (x.displayName || x.nameEn || "")).toLowerCase().includes(s)) : all;
        const o = Number(q.get("offset") || 0), l = Number(q.get("limit") || 50); return { [key]: hit.slice(o, o + l), total: hit.length }; };
      if (p === "/api/v1/auth/login") return send(res, 200, { token: "tok" });
      if (p === "/api/v1/session/company") return send(res, 200, {});
      if (p === "/api/v1/company/inventory/units") { if (req.method === "POST") { unitMade = true; return send(res, 201, unit, 1); } return send(res, 200, { units: unitMade ? [unit] : [] }); }
      if (p === "/api/v1/company/inventory/groups") { if (req.method === "POST") { groupMade = true; return send(res, 201, group, 1); } return send(res, 200, { groups: groupMade ? [group] : [] }); }
      if (p === "/api/v1/company/inventory/items") {
        if (req.method === "POST") { if (db.items.some((i) => i.code === body.code)) return send(res, 409, { error: "item_code_taken" }); const it = { id: id(), ...body, revision: 1 }; db.items.push(it); return send(res, 201, it, 1); }
        return send(res, 200, page(db.items, "items"));
      }
      if (p === "/api/v1/company/parties") {
        if (req.method === "POST") { if (db.parties.some((x) => x.code === body.code)) return send(res, 400, { error: "invalid_request", fields: [{ field: "code", code: "code_taken" }] }); const x = { id: id(), ...body, revision: 1 }; db.parties.push(x); return send(res, 201, x, 1); }
        return send(res, 200, page(db.parties, "parties"));
      }
      if (p === "/api/v1/company/party-roles") { if (req.method === "POST") { db.roles++; return send(res, 201, { id: id() }, 1); } return send(res, 200, { items: [], can: {} }); }
      if (p === "/api/v1/company/branches") return send(res, 200, { branches: [{ id: id(), isMain: true }] });
      if (p === "/api/v1/company/warehouses") return send(res, 200, { warehouses: [{ id: "w1", isActive: true }, { id: "w2", isActive: true }] });
      if (p === "/api/v1/company/accounts") return send(res, 200, { accounts: ["a", "b", "c"].map((c) => ({ id: c, postable: true, isActive: true, currencyPolicy: "base" })) });
      if (p === "/api/v1/company/journal" && req.method === "POST") {
        const debit = body.lines.reduce((s, l) => s + Math.round(l.debit * 100), 0), credit = body.lines.reduce((s, l) => s + Math.round(l.credit * 100), 0);
        if (debit !== credit) return send(res, 400, { error: "entry_not_balanced" });
        const e = { id: id(), revision: 1, status: "draft" }; db.journals.set(e.id, e); return send(res, 201, { entry: e }, 1);
      }
      let m = p.match(/^\/api\/v1\/company\/journal\/([^/]+)\/(submit|post)$/);
      if (m) { const e = db.journals.get(m[1]); if (req.headers["if-match"] !== `"${e.revision}"`) return send(res, 412, { error: "revision_conflict" }); e.revision++; e.status = m[2] === "submit" ? "submitted" : "posted"; return send(res, 200, { entry: e }, e.revision); }
      if (p === "/api/v1/company/stock-documents" && req.method === "POST") { const d = { id: id(), revision: 1, status: 1, lines: body.lines }; db.docs.set(d.id, d); return send(res, 201, d, 1); }
      m = p.match(/^\/api\/v1\/company\/stock-documents\/([^/]+)\/(submit|approve)$/);
      if (m) { const d = db.docs.get(m[1]); if (req.headers["if-match"] !== `"${d.revision}"`) return send(res, 412, { error: "revision_conflict" }); d.revision++; d.status = m[2] === "submit" ? 2 : 3; return send(res, 200, d, d.revision); }
      return send(res, 200, { journal: [], documents: [], total: 0, lines: [] });
    });
  });
  return { server, db };
}

test("seed and a short run against a stub API: counts, idempotency, balanced entries, results file shape", async () => {
  const { server, db } = stubApi(); await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const base = `http://127.0.0.1:${server.address().port}`;
  const mk = async () => { const c = new Client({ baseUrl: base }); await c.login("m", "p"); await c.selectCompany("c"); return c; };
  try {
    const counts = { items: 40, parties: 25, journals: 12, stockDocs: 8 };
    const r = await seed({ maker: await mk(), approver: await mk(), counts, seed: "smoke", year: 2026, concurrency: 3 });
    assert.deepStrictEqual([r.stats.items, r.stats.parties, r.stats.journalsPosted, r.stats.stockDocsApproved], [40, 25, 12, 8]);
    assert.ok([...db.journals.values()].every((e) => e.status === "posted"));
    assert.ok(db.roles > 0 && db.roles < 25);
    const again = await seed({ maker: await mk(), approver: await mk(), counts: { ...counts, journals: 0, stockDocs: 0 }, seed: "smoke", year: 2026 });   // idempotent for items and parties
    assert.deepStrictEqual([again.stats.items, again.stats.itemsExisting, again.stats.partiesExisting], [0, 40, 25]);
    assert.strictEqual(db.items.length, 40);
    // a company that holds non-load-test data is refused
    db.items.push({ id: "x", code: "REAL-1", nameEn: "real" });
    await assert.rejects(seed({ maker: await mk(), approver: await mk(), counts, seed: "smoke", year: 2026 }), /not load-test data/);
    db.items.pop();

    const client = await mk(); const scenarios = defineScenarios();
    const ctx = { client, seed: "smoke", counts, year: 2026, accountIds: ["a", "b", "c"], branchId: "b1" };
    const { samples } = await runLoad({ ctx, scenarios, concurrency: 3, iterations: 60, warmup: 5 });
    assert.ok(samples.length >= 60 && samples.every((s) => s.ok && typeof s.ms === "number"));
    const res = buildResults({ tool: { name: "t" }, seed: "smoke", startedAtUtc: "a", finishedAtUtc: "b", scenarios, samples, data: { ...counts, invoices: 0, entriesAndStockMovements: 20 }, declaredEnvironment: null, load: { sessions: 1, durationSeconds: 1 } });
    assert.strictEqual(res.verdict, "NOT VERIFIED");
    assert.strictEqual(res.environment.declaredReferenceEnvironment, null);
    assert.ok(res.scenarios.find((s) => s.id === "item_search").samples > 0);
    JSON.parse(JSON.stringify(res));
  } finally { server.close(); }
});

test("the command line refuses without the acknowledgement and prints the plan digest", () => {
  const cli = path.join(root, "tools/loadtest/loadtest.js");
  const env = { ...process.env }; delete env.HASM_LOADTEST_DISPOSABLE;
  const refused = (() => { try { execFileSync("node", [cli, "seed", "--api", "http://127.0.0.1:1", "--company", "c"], { env, stdio: "pipe" }); return null; } catch (e) { return e; } })();
  assert.ok(refused && refused.status === 3 && /disposable/.test(String(refused.stderr)));
  const out = JSON.parse(execFileSync("node", [cli, "plan", "--items", "10", "--parties", "5", "--journals", "3", "--stock-docs", "2"], { encoding: "utf8" }));
  assert.match(out.digest, /^[0-9a-f]{64}$/);
});
