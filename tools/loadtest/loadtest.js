#!/usr/bin/env node
"use strict";
// HASM load-test harness, stage 1 (foundation). See docs/DEVELOPMENT.md "Load-test harness".
//   node tools/loadtest/loadtest.js plan  [--seed S] [--items N] [--parties N] [--journals N] [--stock-docs N]
//   node tools/loadtest/loadtest.js seed  --api URL --company ID [counts] [--year 2026] [--concurrency 4]
//   node tools/loadtest/loadtest.js run   --api URL --company ID [counts as seeded] [--iterations N | --duration-seconds S] [--environment FILE] [--out FILE]
// Credentials only from the environment: HASM_LT_LOGIN, HASM_LT_PASSWORD (maker) and HASM_LT_APPROVER_LOGIN, HASM_LT_APPROVER_PASSWORD.
const fs = require("fs");
const path = require("path");
const cp = require("child_process");
const { Client } = require("./lib/client");
const { assertDisposableTarget } = require("./lib/guard");
const planLib = require("./lib/plan");
const { seed } = require("./lib/seed");
const { defineScenarios, runLoad } = require("./lib/scenarios");
const { buildResults } = require("./lib/results");

function parseArgs(argv) {
  const out = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith("--")) { const k = argv[i].slice(2); const v = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : "true"; out[k] = v; } else out._.push(argv[i]);
  }
  return out;
}
const num = (v, d) => (v === undefined ? d : Number(v));
const counts = (a) => ({ items: num(a.items, 20000), parties: num(a.parties, 10000), journals: num(a.journals, 1000), stockDocs: num(a["stock-docs"], 500) });
const log = (m) => process.stderr.write(m + "\n");

async function signedIn(a, prefix) {
  const login = process.env[`HASM_LT_${prefix}LOGIN`], pw = process.env[`HASM_LT_${prefix}PASSWORD`];
  if (!login || !pw) return null;
  const c = new Client({ baseUrl: a.api }); await c.login(login, pw); await c.selectCompany(a.company); return c;
}

async function main() {
  const a = parseArgs(process.argv.slice(2)); const cmd = a._[0]; const seedValue = a.seed ?? "hasm-loadtest-1";
  if (cmd === "plan") {
    const n = counts(a);
    process.stdout.write(JSON.stringify({ seed: seedValue, counts: n, digest: planLib.planDigest({ seed: seedValue, items: n.items, parties: n.parties, journals: n.journals, stockDocs: n.stockDocs, year: num(a.year, 2026) }) }, null, 2) + "\n");
    return;
  }
  if (cmd !== "seed" && cmd !== "run") { log("usage: loadtest.js plan|seed|run (see docs/DEVELOPMENT.md)"); process.exit(2); }
  if (!a.api || !a.company) { log("--api and --company are required"); process.exit(2); }
  const envFile = a.environment ? JSON.parse(fs.readFileSync(a.environment, "utf8")) : null;
  assertDisposableTarget({ apiUrl: a.api, databasePort: a["database-port"], environmentFile: envFile });
  const maker = await signedIn(a, ""); if (!maker) { log("set HASM_LT_LOGIN and HASM_LT_PASSWORD"); process.exit(2); }
  const approver = await signedIn(a, "APPROVER_");
  const year = num(a.year, 2026); const n = counts(a);

  if (cmd === "seed") {
    const r = await seed({ maker, approver, counts: n, seed: seedValue, year, concurrency: num(a.concurrency, 4), log });
    const manifest = { schema: "hasm-loadtest-seed/1", seed: seedValue, year, counts: n, planDigest: planLib.planDigest({ seed: seedValue, items: n.items, parties: n.parties, journals: n.journals, stockDocs: n.stockDocs, warehouses: r.warehouseCount, accounts: r.accountCount, year }), ...r };
    process.stdout.write(JSON.stringify(manifest, null, 2) + "\n"); return;
  }

  // run
  const branches = (await maker.call("branches")).json.branches;
  const accounts = (await maker.call("accounts")).json.accounts.filter((x) => x.postable && x.isActive && ["base", "any"].includes(x.currencyPolicy));
  const ctx = { client: maker, seed: seedValue, counts: n, year, accountIds: accounts.map((x) => x.id), branchId: (branches.find((b) => b.isMain) || branches[0]).id };
  const scenarios = defineScenarios();
  const startedAtUtc = new Date().toISOString();
  const { samples, elapsedSeconds } = await runLoad({ ctx, scenarios, concurrency: num(a.concurrency, 4), iterations: a["duration-seconds"] ? undefined : num(a.iterations, 100), durationSeconds: num(a["duration-seconds"], 0) || undefined, warmup: num(a.warmup, 10) });
  let commit = "unknown"; try { commit = cp.execFileSync("git", ["rev-parse", "HEAD"], { cwd: __dirname, encoding: "utf8" }).trim(); } catch { /* not a git checkout */ }
  const results = buildResults({
    tool: { name: "hasm-loadtest", stage: 1, commit }, seed: seedValue, startedAtUtc, finishedAtUtc: new Date().toISOString(), scenarios, samples, declaredEnvironment: envFile,
    data: { ...n, invoices: 0, entriesAndStockMovements: n.journals + n.stockDocs }, load: { sessions: 1, concurrency: num(a.concurrency, 4), durationSeconds: Math.round(elapsedSeconds), mode: a["duration-seconds"] ? "duration" : "iterations" },
  });
  const out = a.out || path.join("loadtest-results", `run-${startedAtUtc.replace(/[:.]/g, "-")}.json`);
  fs.mkdirSync(path.dirname(out), { recursive: true }); fs.writeFileSync(out, JSON.stringify(results, null, 2) + "\n");
  process.stdout.write(`${results.verdict}${results.verdictReasons.length ? " (" + results.verdictReasons.length + " reasons, see file)" : ""}\n`);
  for (const s of results.scenarios) process.stdout.write(`  ${s.id.padEnd(24)} n=${String(s.samples).padStart(5)} err=${s.errors} p95=${s.p95 === null ? "-" : s.p95.toFixed(0) + "ms"}${s.implemented ? "" : "  (not implemented)"}${results.verdict === "NOT VERIFIED" ? "  [NOT VERIFIED, not evidence]" : ""}\n`);
  process.stdout.write(`results: ${out}\n`);
}
main().catch((e) => { log(e.message || String(e)); process.exit(e.code === "target_not_disposable" ? 3 : 1); });
