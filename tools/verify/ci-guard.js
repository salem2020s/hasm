#!/usr/bin/env node
// CI weakening guard (Phase 4.0). Compares a base revision with the working tree and fails when a change could silently weaken verification:
//  * the PostgreSQL expected-count registry in backend.yml loses a class, lowers a count, repeats a key or becomes unparseable (fail closed);
//  * a workflow ADDS (occurrence counts, not presence) continue-on-error, a constant-false/never condition, an ignored failure (|| true, || exit 0) or a dotnet test --filter, narrows a paths filter,
//    adds a job-level name (which renames its check), removes a required job, is deleted or is renamed away;
//  * the number of test attributes/cases (PostgreSQL/xUnit, frontend, Playwright) decreases, or skipped/ignored/focused tests are added;
//  * protected verification files (this guard, the check manifest, the secret allow list, the verification workflow, CODEOWNERS) change without the owner approval flag (env CI_CHANGE_APPROVED=1, set from an owner label).
// Limits (documented, not hidden): without a GitHub ruleset that requires these checks nothing enforces them at merge time; label approval is not bound to a commit.
// Usage: node tools/verify/ci-guard.js --base <git ref> [--root <dir>]   Exit 0 = clean, 1 = findings.
"use strict";
const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const pipe = { encoding: "utf8", maxBuffer: 1 << 28, stdio: ["ignore", "pipe", "ignore"] };
function git(root, args) { return execFileSync("git", ["-C", root, ...args], pipe); }
function showBase(root, base, file) { try { return git(root, ["show", `${base}:${file}`]); } catch { return null; } }
function gitGrepCount(root, ref, pattern, pathspec) {
  try {
    const out = git(root, ["grep", "-E", "-c", ...(ref ? [] : ["--untracked"]), pattern, ...(ref ? [ref] : []), "--", ...pathspec]);
    return out.split("\n").filter(Boolean).reduce((n, l) => n + Number(l.slice(l.lastIndexOf(":") + 1)), 0);
  } catch { return 0; }      // git grep exits 1 when nothing matches
}

/** Strips a trailing YAML/Python comment (a # outside quotes) from one line. */
function stripComment(line) {
  let q = null;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (q) { if (c === q) q = null; }
    else if (c === '"' || c === "'") q = c;
    else if (c === "#" && (i === 0 || /\s/.test(line[i - 1]))) return line.slice(0, i);
  }
  return line;
}

/** Per-class expected counts of the EXPECTED = { ... } block. Returns {map, errors}: duplicate keys and malformed entries are errors (fail closed). */
function parseRegistry(text) {
  if (!text) return null;
  const m = text.match(/EXPECTED\s*=\s*\{([\s\S]*?)\n\s*\}\s*\n/);
  if (!m) return null;
  const map = {}; const errors = [];
  const body = m[1].split("\n").map(stripComment).join("\n");
  const entries = body.split(",").map(s => s.trim()).filter(Boolean);
  for (const e of entries) {
    const x = e.match(/^"([A-Za-z0-9_]+)"\s*:\s*(\d+)$/);
    if (!x) { errors.push(`unparseable registry entry: ${e.slice(0, 60)}`); continue; }
    if (x[1] in map) errors.push(`duplicate registry key ${x[1]}`);
    map[x[1]] = Number(x[2]);
  }
  return { map, errors };
}

const bool = String.raw`["']?\s*(?:true|yes|on|1)\s*["']?`;
const WEAKENERS = [
  [new RegExp(String.raw`continue-on-error\s*:\s*(?:${bool}|\$\{\{\s*(?:true|1)\s*\}\})`, "gi"), "continue-on-error enabled"],
  [/^\s*(?:-\s*)?if\s*:\s*["']?\s*(?:false|0|never|1\s*==\s*2|\$\{\{\s*(?:false|0)\s*\}\})\s*["']?\s*$/gim, "a job or step disabled with a constant-false condition"],
  [/\|\|\s*(?:true|exit\s+0|:)(?=\s|$|;|\))/g, "a command that ignores failure"],
  [/dotnet\s+test[\s\S]{0,400}?--filter/gi, "dotnet test with --filter"],
  [/--no-verify|--no-gpg-sign/g, "a bypass flag"],
  [/^\s*pull_request_target\s*:/gm, "pull_request_target (it would run pull-request code with repository secrets and, on the self-hosted runner, on the development machine)"],
];
const SKIPS = [/\[(?:Fact|Theory|PostgresFact|PostgresTheory)\s*\(\s*Skip\s*=/g, /\bSkip\s*=\s*"/g, /\b(?:it|test|describe)\.(?:skip|todo|fixme|skipIf|runIf)\s*\(/g, /\b(?:xit|xdescribe)\s*\(/g, /\.only\s*\(/g,
  /\[Ignore\b/g, /\[Explicit\b/g, /Assert\.Skip/g, /\[SkippableFact/g];
const PROTECTED = [".github/required-checks.json", ".github/secret-scan-allow.json", ".github/workflows/verification.yml", ".github/CODEOWNERS"];

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
const norm = t => t.replace(/\r\n/g, "\n").split("\n").map(stripComment).join("\n");
const occurrences = (text, re) => (norm(text).match(new RegExp(re.source, re.flags.includes("g") ? re.flags : re.flags + "g")) || []).length;
const countMatches = (text, res) => res.reduce((n, re) => n + ((text.match(re) || []).length), 0);
/** The paths entries of every `paths:` filter of a workflow (flat list). */
function pathFilters(text) { return [...norm(text).matchAll(/paths\s*:\s*\[([^\]]*)\]/g)].flatMap(m => m[1].split(",").map(s => s.trim().replace(/^["']|["']$/g, "")).filter(Boolean)); }
const jobLevelNames = text => (norm(text).match(/^    name\s*:/gm) || []).length;

function run(root, base, env = process.env) {
  const findings = [];
  const changed = [...new Set(git(root, ["diff", "--no-renames", "--name-only", `${base}...HEAD`]).split("\n").concat(git(root, ["diff", "--no-renames", "--name-only"]).split("\n")).filter(Boolean))];
  // 1. registry (fail closed)
  const wf = ".github/workflows/backend.yml";
  const beforeText = showBase(root, base, wf);
  const before = parseRegistry(beforeText);
  const nowText = fs.existsSync(path.join(root, wf)) ? fs.readFileSync(path.join(root, wf), "utf8") : null;
  const after = parseRegistry(nowText);
  if (before) {
    if (!after) findings.push(`${wf}: the expected-count registry disappeared or could not be parsed`);
    else {
      after.errors.forEach(e => findings.push(`${wf}: ${e}`));
      for (const [k, v] of Object.entries(before.map)) {
        if (!(k in after.map)) findings.push(`${wf}: test class ${k} (expected ${v}) was removed from the registry`);
        else if (after.map[k] < v) findings.push(`${wf}: expected count of ${k} was lowered from ${v} to ${after.map[k]}`);
      }
      const sum = o => Object.values(o).reduce((a, b) => a + b, 0);
      if (sum(after.map) < sum(before.map)) findings.push(`${wf}: total expected PostgreSQL tests decreased from ${sum(before.map)} to ${sum(after.map)}`);
    }
  }
  // 2. workflows
  const manifestText = fs.existsSync(path.join(root, ".github/required-checks.json")) ? fs.readFileSync(path.join(root, ".github/required-checks.json"), "utf8") : null;
  const required = manifestText ? JSON.parse(manifestText).checks.map(c => c.name) : [];
  const wfDir = path.join(root, ".github/workflows");
  const files = fs.existsSync(wfDir) ? fs.readdirSync(wfDir).filter(f => /\.ya?ml$/.test(f)) : [];
  const baseWorkflows = git(root, ["ls-tree", "-r", "--name-only", base, ".github/workflows/"]).split("\n").filter(f => /\.ya?ml$/.test(f));
  for (const b of baseWorkflows) if (!fs.existsSync(path.join(root, b))) findings.push(`${b}: workflow file deleted or renamed away`);
  for (const f of files) {
    const rel = `.github/workflows/${f}`; const text = fs.readFileSync(path.join(wfDir, f), "utf8");
    const old = showBase(root, base, rel) || "";
    for (const [re, label] of WEAKENERS) if (occurrences(text, re) > occurrences(old, re)) findings.push(`${rel}: added ${label}`);
    if (old) {
      jobNames(old).filter(j => !jobNames(text).includes(j) && required.includes(j)).forEach(j => findings.push(`${rel}: required job ${j} was removed`));
      const oldPaths = pathFilters(old), newPaths = pathFilters(text);
      oldPaths.filter(p => !newPaths.includes(p)).forEach(p => findings.push(`${rel}: paths filter entry ${p} was removed (the job may stop running)`));
      if (jobLevelNames(text) > jobLevelNames(old)) findings.push(`${rel}: a job-level name was added (it renames the check run)`);
    }
  }
  // 3. skipped tests and shrinking test inventory
  for (const f of changed) {
    if (!/(\.cs|\.tsx?|\.js)$/.test(f) || !/(test|spec|Tests)/i.test(f)) continue;
    if (f.startsWith("tools/verify/")) continue;   // the guard self-tests hold the skip patterns as sample text; those files are protected by the approval flag instead
    const p = path.join(root, f); if (!fs.existsSync(p)) continue;
    const now = fs.readFileSync(p, "utf8"); const old = showBase(root, base, f) || "";
    if (countMatches(now, SKIPS) > countMatches(old, SKIPS)) findings.push(`${f}: adds skipped, ignored or focused tests`);
  }
  const inventories = [
    ["PostgreSQL/xUnit test attributes", String.raw`\[(PostgresFact|PostgresTheory|Fact|Theory)(\]|\()`, ["tests/*.cs"]],
    ["frontend test cases", String.raw`^\s*(it|test)(\.each)?\(`, ["src/frontend/*.test.ts", "src/frontend/*.test.tsx"]],
    ["browser smoke tests", String.raw`^\s*test\(`, ["src/frontend/e2e/*.spec.ts"]],
  ];
  const approved = env.CI_CHANGE_APPROVED === "1";
  for (const [label, pat, spec] of inventories) {
    const was = gitGrepCount(root, base, pat, spec), now = gitGrepCount(root, null, pat, spec);
    if (now < was && !approved) findings.push(`${label} decreased from ${was} to ${now} (a deliberate removal needs the owner approval flag)`);
  }
  // 4. protected files
  const protectedChanged = changed.filter(f => PROTECTED.includes(f) || f.startsWith("tools/verify/"));
  const guardExistedOnBase = showBase(root, base, "tools/verify/ci-guard.js") !== null;   // the PR that introduces the guard cannot be judged by it
  if (guardExistedOnBase && protectedChanged.length && !approved)
    findings.push(`protected verification files changed without the owner approval flag (CI_CHANGE_APPROVED): ${protectedChanged.join(", ")}`);
  return findings;
}

module.exports = { run, parseRegistry, jobNames, stripComment, pathFilters };

if (require.main === module) {
  const a = process.argv.slice(2); const get = k => { const i = a.indexOf(k); return i >= 0 ? a[i + 1] : null; };
  const base = get("--base"); const root = path.resolve(get("--root") || ".");
  if (!base) { console.error("usage: ci-guard.js --base <ref> [--root <dir>]"); process.exit(2); }
  const findings = run(root, base);
  if (findings.length) { console.error("CI GUARD FAILED:\n - " + findings.join("\n - ")); process.exit(1); }
  console.log("CI guard: no weakening of verification found against " + base);
}
