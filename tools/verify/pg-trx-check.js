#!/usr/bin/env node
// Verifies a PostgreSQL integration TRX result against the exact expected count per test class (Phase 4.0 / CI migration).
// Usage: node tools/verify/pg-trx-check.js <results.trx> [workflow file with the registry; default .github/workflows/backend.yml]
// The registry is the block `EXPECTED = { "ClassTests": 10, ... }` in .github/workflows/backend.yml, the single source that ci-guard protects (counts may only rise).
// It FAILS when: a registered class has another count than registered, a test class is not registered, any test is not Passed, or the total differs.
// Theory cases count individually; SafetyGateTests (pure logic, no database) are ignored, exactly as before the migration from the Python step.
"use strict";
const fs = require("fs");
const { parseRegistry } = require("./ci-guard.js");

function attrs(tag) { const o = {}; for (const m of tag.matchAll(/([A-Za-z:]+)="([^"]*)"/g)) o[m[1]] = m[2]; return o; }
const unescape = s => s.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, "&");

function check(trxText, registryText) {
  const parsed = parseRegistry(registryText);
  if (!parsed) return { ok: false, problems: ["the expected-count registry could not be found"] };
  if (parsed.errors.length) return { ok: false, problems: parsed.errors };
  const expected = parsed.map; const total = Object.values(expected).reduce((a, b) => a + b, 0);
  const counts = {}; const outcomes = {}; const unknown = [];
  for (const m of trxText.matchAll(/<UnitTestResult\b[^>]*>/g)) {
    const a = attrs(m[0]); const name = unescape(a.testName || "");
    const cls = (name.match(/^Hasm\.Database\.IntegrationTests\.(\w+)\./) || [])[1] || "";
    if (cls === "SafetyGateTests") continue;
    if (!(cls in expected)) { unknown.push(name); continue; }
    counts[cls] = (counts[cls] || 0) + 1;
    outcomes[a.outcome] = (outcomes[a.outcome] || 0) + 1;
  }
  const problems = [];
  for (const k of new Set([...Object.keys(expected), ...Object.keys(counts)])) if ((counts[k] || 0) !== (expected[k] || 0)) problems.push(`${k}: expected ${expected[k] || 0}, found ${counts[k] || 0}`);
  if ((outcomes.Passed || 0) !== total || Object.keys(outcomes).some(o => o !== "Passed")) problems.push(`outcomes ${JSON.stringify(outcomes)} (expected ${total} Passed and nothing else)`);
  if (unknown.length) problems.push(`unregistered test classes: ${unknown.slice(0, 5).join(", ")}`);
  return { ok: problems.length === 0, problems, found: Object.values(counts).reduce((a, b) => a + b, 0), total, outcomes };
}
module.exports = { check };

if (require.main === module) {
  const file = process.argv[2];
  const registryFile = process.argv[3] || ".github/workflows/backend.yml";
  if (!file) { console.error("usage: node pg-trx-check.js <results.trx> [registry file]"); process.exit(2); }
  const r = check(fs.readFileSync(file, "utf8"), fs.readFileSync(registryFile, "utf8"));
  console.log(`PostgreSQL tests found: ${r.found ?? 0}; expected ${r.total ?? "?"}; outcomes: ${JSON.stringify(r.outcomes ?? {})}`);
  if (!r.ok) { console.error("::error::PostgreSQL test verification FAILED:\n - " + r.problems.join("\n - ")); process.exit(1); }
  console.log("All registered PostgreSQL tests executed and passed (none skipped)");
}
