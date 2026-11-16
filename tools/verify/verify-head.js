#!/usr/bin/env node
// Head-SHA verification (Phase 4.0): proves every required check is green on the EXACT head commit of a pull request.
// Usage: node tools/verify/verify-head.js --pr <number> [--expect-sha <sha>] [--manifest .github/required-checks.json] [--input <json>]
// Required checks come from the manifest; a check is required for a PR when the PR touches one of its paths (the manifest mirrors the workflow path filters; check-docs verifies that).
// Refuses when: the head SHA differs from --expect-sha, a required check is missing, pending, skipped, cancelled or failed, a check exists only for an older SHA, or the run was not created by GitHub Actions.
// --input lets tests supply {headSha, files, checkRuns:[{name,status,conclusion,headSha,appId}]} instead of calling GitHub.
"use strict";
const { execFileSync } = require("child_process");
const fs = require("fs");

const GITHUB_ACTIONS_APP_ID = 15368;

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
    // only runs created by GitHub Actions count: any other app or token able to create a check run of the same name must not satisfy a requirement
    const runs = checkRuns.filter(r => r.name === c.name && (r.appId === undefined || r.appId === GITHUB_ACTIONS_APP_ID));
    // A check name can be defined by two jobs, one per runner mode (HASM_RUNNER_MODE); the job of the other mode is SKIPPED. A skipped run never counts as passing:
    // it is ignored only when the required number of runs of that name really ran, so a check that was skipped everywhere is refused.
    const isSkipped = r => r.status === "completed" && r.conclusion === "skipped";
    const all = runs.filter(r => r.headSha === headSha);
    const onHead = all.filter(r => !isSkipped(r));
    if (onHead.length === 0 && all.length > 0) { problems.push(`${c.name}: conclusion skipped (no run of this check executed on ${headSha.slice(0, 7)})`); continue; }
    if (onHead.length < (c.count || 1)) { problems.push(`${c.name}: expected ${c.count || 1} run(s) on ${headSha.slice(0, 7)}, found ${onHead.length}${runs.length > onHead.length ? " (others belong to an older SHA)" : ""}`); continue; }
    for (const r of onHead) if (r.status !== "completed") problems.push(`${c.name}: still ${r.status}`);
    else if (r.conclusion !== "success") problems.push(`${c.name}: conclusion ${r.conclusion}`);
  }
  return problems;
}

function gh(args) { return execFileSync("gh", args, { encoding: "utf8", maxBuffer: 1 << 27 }); }
/** gh api --paginate with --slurp returns an array of pages. */
function ghPages(endpoint, pick) { return JSON.parse(gh(["api", "--paginate", "--slurp", endpoint])).flatMap(pick); }

function main() {
  const a = process.argv.slice(2); const get = k => { const i = a.indexOf(k); return i >= 0 ? a[i + 1] : null; };
  const manifest = JSON.parse(fs.readFileSync(get("--manifest") || ".github/required-checks.json", "utf8"));
  let data;
  if (get("--input")) data = JSON.parse(fs.readFileSync(get("--input"), "utf8"));
  else {
    const pr = get("--pr"); if (!pr) { console.error("--pr or --input required"); process.exit(2); }
    const view = JSON.parse(gh(["pr", "view", pr, "--json", "headRefOid"]));
    const repo = JSON.parse(gh(["repo", "view", "--json", "nameWithOwner"])).nameWithOwner;
    const files = ghPages(`repos/${repo}/pulls/${pr}/files?per_page=100`, p => p.map(f => f.filename));
    const runs = ghPages(`repos/${repo}/commits/${view.headRefOid}/check-runs?per_page=100`, p => p.check_runs);
    data = { headSha: view.headRefOid, files, checkRuns: runs.map(r => ({ name: r.name, status: r.status, conclusion: r.conclusion, headSha: r.head_sha, appId: r.app && r.app.id })) };
  }
  const problems = evaluate({ ...data, expectSha: get("--expect-sha"), manifest });
  if (problems.length) { console.error("HEAD VERIFICATION FAILED:\n - " + problems.join("\n - ")); process.exit(1); }
  console.log(`All required checks are green on ${data.headSha.slice(0, 7)}`);
}
module.exports = { evaluate, globToRe, touches, GITHUB_ACTIONS_APP_ID };
if (require.main === module) main();
