#!/usr/bin/env node
// Head-SHA verification (Phase 4.0): proves every required check is green on the EXACT head commit of a pull request.
// Usage: node tools/verify/verify-head.js --pr <number> [--expect-sha <sha>] [--manifest .github/required-checks.json] [--input <json>]
// Required checks come from the manifest; a check is required for a PR when the PR touches one of its paths (mirrors the path filters of the workflows).
// Refuses when: the head SHA differs from --expect-sha, a required check is missing, pending, skipped, cancelled or failed, or a check exists only for an older SHA.
// --input lets tests supply {headSha, files, checkRuns:[{name,status,conclusion,headSha}]} instead of calling GitHub.
"use strict";
const { execFileSync } = require("child_process");
const fs = require("fs");

function globToRe(g) {
  const esc = g.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*\*/g, "\u0000").replace(/\*/g, "[^/]*").replace(/\u0000/g, ".*");
  return new RegExp("^" + esc + "$");
}
const touches = (files, paths) => paths.some(p => { const re = globToRe(p); return files.some(f => re.test(f)); });

function evaluate({ headSha, expectSha, files, checkRuns, manifest }) {
  const problems = [];
  if (expectSha && !headSha.startsWith(expectSha) && !expectSha.startsWith(headSha)) problems.push(`head SHA ${headSha.slice(0, 7)} differs from the expected ${expectSha.slice(0, 7)}`);
  for (const c of manifest.checks) {
    if (!touches(files, c.paths)) continue;
    const runs = checkRuns.filter(r => r.name === c.name);
    const onHead = runs.filter(r => r.headSha === headSha);
    if (onHead.length < (c.count || 1)) { problems.push(`${c.name}: expected ${c.count || 1} run(s) on ${headSha.slice(0, 7)}, found ${onHead.length}${runs.length > onHead.length ? " (others belong to an older SHA)" : ""}`); continue; }
    for (const r of onHead) if (r.status !== "completed") problems.push(`${c.name}: still ${r.status}`);
    else if (r.conclusion !== "success") problems.push(`${c.name}: conclusion ${r.conclusion}`);
  }
  return problems;
}

function gh(args) { return JSON.parse(execFileSync("gh", args, { encoding: "utf8", maxBuffer: 1 << 26 })); }

function main() {
  const a = process.argv.slice(2); const get = k => { const i = a.indexOf(k); return i >= 0 ? a[i + 1] : null; };
  const manifest = JSON.parse(fs.readFileSync(get("--manifest") || ".github/required-checks.json", "utf8"));
  let data;
  if (get("--input")) data = JSON.parse(fs.readFileSync(get("--input"), "utf8"));
  else {
    const pr = get("--pr"); if (!pr) { console.error("--pr or --input required"); process.exit(2); }
    const view = gh(["pr", "view", pr, "--json", "headRefOid,files"]);
    const repo = gh(["repo", "view", "--json", "nameWithOwner"]).nameWithOwner;
    const runs = gh(["api", `repos/${repo}/commits/${view.headRefOid}/check-runs?per_page=100`]).check_runs;
    data = { headSha: view.headRefOid, files: view.files.map(f => f.path), checkRuns: runs.map(r => ({ name: r.name, status: r.status, conclusion: r.conclusion, headSha: r.head_sha })) };
  }
  const problems = evaluate({ ...data, expectSha: get("--expect-sha"), manifest });
  if (problems.length) { console.error("HEAD VERIFICATION FAILED:\n - " + problems.join("\n - ")); process.exit(1); }
  console.log(`All required checks are green on ${data.headSha.slice(0, 7)}`);
}
module.exports = { evaluate, globToRe, touches };
if (require.main === module) main();
