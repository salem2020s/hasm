#!/usr/bin/env node
// Documentation and traceability check (Phase 4.0). Always-on in CI.
//  1. generated roadmap documents match the generator (tools/roadmap/generate.js --check);
//  2. required project documents exist;
//  3. relative markdown links in docs/*.md and CLAUDE.md resolve to files;
//  4. every SRS requirement id (section.n, sections 2-19 and 21) and RR-1..12 appears in the traceability matrix exactly once (the generator enforces this too; re-checked independently here from the committed matrix text).
"use strict";
const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");

function check(root) {
  const problems = [];
  const req = ["CLAUDE.md", "docs/SRS.md", "docs/DECISIONS.md", "docs/ARCHITECTURE.md", "docs/ROADMAP.md", "docs/RELEASE1-TRACEABILITY.md", "docs/RELEASE1-DEPENDENCIES.md", "docs/RELEASE1-ACCEPTANCE.md", "docs/AUTONOMOUS-PROGRESS.md", "docs/AUTONOMOUS-STATE.json", ".github/required-checks.json"];
  for (const f of req) if (!fs.existsSync(path.join(root, f))) problems.push(`missing required file ${f}`);
  if (problems.length) return problems;
  try { execFileSync("node", [path.join(root, "tools/roadmap/generate.js"), "--check"], { cwd: root, stdio: "pipe" }); }
  catch (e) { problems.push("generated roadmap documents differ from the generator output: " + String(e.stderr || e.message).trim().split("\n")[0]); }
  try { JSON.parse(fs.readFileSync(path.join(root, "docs/AUTONOMOUS-STATE.json"), "utf8")); } catch { problems.push("docs/AUTONOMOUS-STATE.json is not valid JSON"); }
  // links
  const docs = fs.readdirSync(path.join(root, "docs")).filter(f => f.endsWith(".md")).map(f => "docs/" + f).concat(["CLAUDE.md"]);
  for (const f of docs) {
    const text = fs.readFileSync(path.join(root, f), "utf8");
    for (const m of text.matchAll(/\]\((?!https?:|mailto:|#)([^)\s]+?)(?:#[^)]*)?\)/g)) {
      const target = path.resolve(path.dirname(path.join(root, f)), decodeURI(m[1]));
      if (!fs.existsSync(target)) problems.push(`${f}: broken link ${m[1]}`);
    }
  }
  // the required-check manifest must mirror the path filters of the workflows that produce each check (a drift would silently drop or invent required checks)
  try {
    const manifest = JSON.parse(fs.readFileSync(path.join(root, ".github/required-checks.json"), "utf8"));
    const wfOf = { "backend-build-test": "backend.yml", "postgres-integration": "backend.yml", "frontend-build-test": "frontend.yml", "e2e-smoke": "frontend.yml", "desktop-build": "desktop.yml", "picomatch-policy-validation": "picomatch-validation.yml", "picomatch-artifact-inspection": "picomatch-validation.yml" };
    const filters = f => [...fs.readFileSync(path.join(root, ".github/workflows", f), "utf8").matchAll(/paths\s*:\s*\[([^\]]*)\]/g)].flatMap(m => m[1].split(",").map(s => s.trim().replace(/^["']|["']$/g, "")).filter(Boolean));
    for (const c of manifest.checks) {
      const f = wfOf[c.name];
      if (f) { const want = [...filters(f)].sort().join("|"), have = [...c.paths].sort().join("|"); if (want !== have) problems.push(`required-checks.json: paths of ${c.name} differ from the pull_request paths filter of ${f}`); }
      else if (!fs.readFileSync(path.join(root, ".github/workflows/verification.yml"), "utf8").includes(`  ${c.name}:`)) problems.push(`required-checks.json: no workflow job produces the check ${c.name}`);
    }
  } catch (e) { problems.push("required-checks.json could not be verified against the workflows: " + e.message); }
  // requirement coverage straight from the SRS and the committed matrix
  const srs = fs.readFileSync(path.join(root, "docs/SRS.md"), "utf8").split(/\r?\n/);
  const ids = []; let sec = "", n = 0;
  for (const l of srs) { const h = l.match(/^## (\d+)\./); if (h) { sec = h[1]; n = 0; continue; }
    if (sec && +sec >= 2 && +sec <= 21 && +sec !== 20 && +sec !== 22 && /^(- |\d+\. )/.test(l)) { n++; ids.push(`${sec}.${n}`); } }
  for (let i = 1; i <= 12; i++) ids.push(`RR-${i}`);
  const matrix = fs.readFileSync(path.join(root, "docs/RELEASE1-TRACEABILITY.md"), "utf8");
  const rows = new Map();
  for (const m of matrix.matchAll(/^\| ([0-9]+\.[0-9]+|RR-[0-9]+) \|/gm)) rows.set(m[1], (rows.get(m[1]) || 0) + 1);
  for (const id of ids) if (rows.get(id) !== 1) problems.push(`requirement ${id} appears ${rows.get(id) || 0} times in the traceability matrix`);
  for (const id of rows.keys()) if (!ids.includes(id)) problems.push(`traceability row ${id} matches no SRS requirement`);
  return problems;
}
module.exports = { check };
if (require.main === module) {
  const root = path.resolve(process.argv[2] || ".");
  const p = check(root);
  if (p.length) { console.error("DOCUMENTATION CHECK FAILED:\n - " + p.join("\n - ")); process.exit(1); }
  console.log("Documentation and traceability check passed");
}
