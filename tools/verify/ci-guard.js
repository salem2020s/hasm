#!/usr/bin/env node
// CI weakening guard (Phase 4.0). Compares a base revision with the working tree and fails when a change could silently weaken verification:
//  * the PostgreSQL expected-count registry in .github/workflows/backend.yml loses a class or lowers a class count;
//  * a workflow adds continue-on-error, "if: false", "|| true" on a command, a dotnet test --filter, or removes a job that is listed in .github/required-checks.json;
//  * test sources gain skipped tests ([Fact(Skip...], Skip =, it.skip, test.skip, describe.skip, xit, xdescribe);
//  * the guard itself or the required-check manifest changes without the explicit approval flag (env CI_CHANGE_APPROVED=1, set from an owner label).
// Usage: node tools/verify/ci-guard.js --base <git ref> [--root <dir>]   Exit 0 = clean, 1 = findings.
"use strict";
const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

function git(root, args) { return execFileSync("git", ["-C", root, ...args], { encoding: "utf8", maxBuffer: 1 << 28 }); }
function showBase(root, base, file) { try { return execFileSync("git", ["-C", root, "show", `${base}:${file}`], { encoding: "utf8", maxBuffer: 1 << 28, stdio: ["ignore", "pipe", "ignore"] }); } catch { return null; } }

/** Per-class expected counts from the EXPECTED = { ... } block of backend.yml. */
function parseRegistry(text) {
  if (!text) return null;
  const m = text.match(/EXPECTED\s*=\s*\{([\s\S]*?)\n\s*\}\s*\n/);
  if (!m) return null;
  const out = {}; const re = /"([A-Za-z0-9_]+)"\s*:\s*(\d+)/g; let x;
  const body = m[1].split("\n").filter(l => !l.trim().startsWith("#")).join("\n");
  while ((x = re.exec(body))) out[x[1]] = Number(x[2]);
  return out;
}

const WEAKENERS = [
  [/continue-on-error\s*:\s*true/i, "continue-on-error: true"],
  [/^\s*(?:-\s*)?if\s*:\s*(false|\$\{\{\s*false\s*\}\})\s*$/im, "a job or step disabled with if: false"],
  [/\|\|\s*true\b/, "a command that ignores failure (|| true)"],
  [/dotnet\s+test[^\n]*--filter/i, "dotnet test with --filter"],
];
const SKIPS = [/\[(Fact|Theory|PostgresFact|PostgresTheory)\s*\(\s*Skip\s*=/g, /\bSkip\s*=\s*"/g, /\b(it|test|describe)\.skip\s*\(/g, /\b(xit|xdescribe)\s*\(/g, /\.only\s*\(/g];

function jobNames(text) {
  const names = []; let inJobs = false;
  for (const line of text.split("\n")) {
    if (/^jobs\s*:/.test(line)) { inJobs = true; continue; }
    if (inJobs && /^\S/.test(line) && !/^\s*#/.test(line)) inJobs = false;
    const m = inJobs && line.match(/^  ([A-Za-z0-9_-]+)\s*:\s*$/);
    if (m) names.push(m[1]);
  }
  return names;
}
const countMatches = (text, res) => res.reduce((n, re) => n + ((text.match(re) || []).length), 0);

function run(root, base, env = process.env) {
  const findings = [];
  const changed = git(root, ["diff", "--name-only", `${base}...HEAD`]).split("\n").filter(Boolean)
    .concat(git(root, ["diff", "--name-only"]).split("\n").filter(Boolean));
  // 1. registry
  const wf = ".github/workflows/backend.yml";
  const before = parseRegistry(showBase(root, base, wf));
  const nowText = fs.existsSync(path.join(root, wf)) ? fs.readFileSync(path.join(root, wf), "utf8") : null;
  const after = parseRegistry(nowText);
  if (before) {
    if (!after) findings.push(`${wf}: the expected-count registry disappeared or could not be parsed`);
    else {
      for (const [k, v] of Object.entries(before)) {
        if (!(k in after)) findings.push(`${wf}: test class ${k} (expected ${v}) was removed from the registry`);
        else if (after[k] < v) findings.push(`${wf}: expected count of ${k} was lowered from ${v} to ${after[k]}`);
      }
      const sum = o => Object.values(o).reduce((a, b) => a + b, 0);
      if (sum(after) < sum(before)) findings.push(`${wf}: total expected PostgreSQL tests decreased from ${sum(before)} to ${sum(after)}`);
    }
  }
  // 2. workflows
  const manifestText = fs.existsSync(path.join(root, ".github/required-checks.json")) ? fs.readFileSync(path.join(root, ".github/required-checks.json"), "utf8") : null;
  const required = manifestText ? JSON.parse(manifestText).checks.map(c => c.name) : [];
  const wfDir = path.join(root, ".github/workflows");
  const files = fs.existsSync(wfDir) ? fs.readdirSync(wfDir).filter(f => /\.ya?ml$/.test(f)) : [];
  for (const f of files) {
    const rel = `.github/workflows/${f}`; const text = fs.readFileSync(path.join(wfDir, f), "utf8");
    const old = showBase(root, base, rel) || "";
    for (const [re, label] of WEAKENERS) if (re.test(text) && !re.test(old)) findings.push(`${rel}: added ${label}`);
    if (old) {
      const gone = jobNames(old).filter(j => !jobNames(text).includes(j) && required.includes(j));
      gone.forEach(j => findings.push(`${rel}: required job ${j} was removed`));
    }
  }
  for (const f of git(root, ["diff", "--name-only", "--diff-filter=D", `${base}...HEAD`]).split("\n").filter(Boolean))
    if (f.startsWith(".github/workflows/")) findings.push(`${f}: workflow file deleted`);
  // 3. skipped tests (compare skip counts per changed test file)
  for (const f of new Set(changed)) {
    if (!/(\.cs|\.tsx?|\.js)$/.test(f) || !/(test|spec|Tests)/i.test(f)) continue;
    const p = path.join(root, f); if (!fs.existsSync(p)) continue;
    const now = fs.readFileSync(p, "utf8"); const old = showBase(root, base, f) || "";
    if (countMatches(now, SKIPS) > countMatches(old, SKIPS)) findings.push(`${f}: adds skipped or focused tests`);
  }
  // 4. protected files
  const protectedFiles = changed.filter(f => f === ".github/required-checks.json" || f.startsWith("tools/verify/"));
  const guardExistedOnBase = showBase(root, base, "tools/verify/ci-guard.js") !== null;   // the PR that introduces the guard cannot be judged by it
  if (guardExistedOnBase && protectedFiles.length && env.CI_CHANGE_APPROVED !== "1")
    findings.push(`protected verification files changed without the owner approval flag (CI_CHANGE_APPROVED): ${[...new Set(protectedFiles)].join(", ")}`);
  return findings;
}

module.exports = { run, parseRegistry, jobNames };

if (require.main === module) {
  const a = process.argv.slice(2); const get = k => { const i = a.indexOf(k); return i >= 0 ? a[i + 1] : null; };
  const base = get("--base"); const root = path.resolve(get("--root") || ".");
  if (!base) { console.error("usage: ci-guard.js --base <ref> [--root <dir>]"); process.exit(2); }
  const findings = run(root, base);
  if (findings.length) { console.error("CI GUARD FAILED:\n - " + findings.join("\n - ")); process.exit(1); }
  console.log("CI guard: no weakening of verification found against " + base);
}
