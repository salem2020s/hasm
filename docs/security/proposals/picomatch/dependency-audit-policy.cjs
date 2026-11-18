"use strict";
// INERT PROPOSAL. Not referenced by any live workflow. Default policy is NOT APPROVED.
// Proposed destination after explicit owner approval: tools/verify/ (already protected by ci-guard).
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync, execFileSync } = require("node:child_process");
const ID = "GHSA-fc5q-39xc-vjpf";
const URL = `https://github.com/advisories/${ID}`;
const TREE_PATHS = ["src/frontend", "src/desktop", "assets/branding"];
const LEVELS = ["info", "low", "moderate", "high", "critical"];
const same = (a, b) => Array.isArray(a) && Array.isArray(b) && a.length === b.length &&
  [...a].sort().every((value, index) => value === [...b].sort()[index]);
const range = value => typeof value === "string" ? value.replace(/\s/g, "") : "";

function evaluate(report, exitCode, lock, policy, context) {
  const failures = [], excepted = [];
  const fail = reason => failures.push(reason);
  if (![0, 1].includes(exitCode) || !report || report.error || report.auditReportVersion !== 2 ||
      !report.vulnerabilities || typeof report.vulnerabilities !== "object" || Array.isArray(report.vulnerabilities))
    return { ok: false, excepted, failures: ["Invalid audit result or audit execution failure"] };
  const rows = Object.entries(report.vulnerabilities);
  const counts = Object.fromEntries(LEVELS.map(level => [level, 0]));
  for (const [name, finding] of rows) {
    if (!finding || finding.name !== name || !LEVELS.includes(finding.severity) || !Array.isArray(finding.via) ||
        !finding.via.length || !Array.isArray(finding.nodes) || !finding.nodes.length ||
        finding.nodes.some(node => typeof node !== "string" || !node.startsWith("node_modules/") || node.includes("..") || node.includes("\\")))
      return { ok: false, excepted, failures: ["Unrecognized audit finding schema"] };
    counts[finding.severity]++;
  }
  if (!report.metadata?.vulnerabilities || LEVELS.some(level => report.metadata.vulnerabilities[level] !== counts[level]) ||
      report.metadata.vulnerabilities.total !== rows.length || (exitCode === 0 && counts.high + counts.critical > 0) ||
      (exitCode === 1 && counts.high + counts.critical === 0))
    return { ok: false, excepted, failures: ["Audit counts/exit status disagree"] };
  const blocking = rows.filter(([, finding]) => ["high", "critical"].includes(finding.severity));
  if (!blocking.length) {
    if (lock?.packages?.["node_modules/picomatch"]?.version === "4.0.7")
      return { ok: false, excepted, failures: ["Known affected matcher unexpectedly missing from audit findings"] };
    return { ok: true, excepted, failures }; // No exception is used.
  }
  if (!same(blocking.map(([name]) => name), Object.keys(policy.findings || {})))
    fail("Exact approved six-package finding set changed");

  const now = Date.parse(context.now), approved = Date.parse(policy.approvedAt), expires = Date.parse(policy.expiresAt);
  if (policy.status !== "APPROVED" || policy.approvedBy !== policy.owner || policy.owner !== "salem2020s" ||
      typeof policy.approvalReference !== "string" || !policy.approvalReference.trim() ||
      !Number.isFinite(now) || !Number.isFinite(approved) || !Number.isFinite(expires) || approved > now ||
      expires <= now || expires - approved > 7 * 24 * 60 * 60 * 1000 || policy.advisory !== ID)
    fail("Exception is unapproved, expired, future-dated or longer than seven days");
  if (context.githubActions !== "true" || context.runnerEnvironment !== "github-hosted" || context.runnerOS !== "Linux" ||
      context.repository !== policy.repository || policy.repository !== "salem2020s/hasm" ||
      context.headRepository !== policy.repository || !["pull_request", "push"].includes(context.event))
    fail("Exception is restricted to this repository's hosted Linux audit job and same-repository heads");
  if (!context.cleanFrozenInputs || TREE_PATHS.some(tree => context.trees?.[tree] !== policy.trees?.[tree]))
    fail("Reviewed frontend/desktop/branding inputs changed or are dirty");
  const advisory = context.advisory;
  const affected = advisory?.vulnerabilities?.filter(item => item.package?.ecosystem === "npm" && item.package?.name === "picomatch");
  if (advisory?.ghsa_id !== ID || advisory.severity !== "high" || !affected || affected.length !== 1 ||
      affected[0].first_patched_version !== null || range(affected[0].vulnerable_version_range) !== policy.vulnerableRange ||
      !Number.isFinite(Date.parse(advisory.updated_at)) || Date.parse(advisory.updated_at) < Date.parse(policy.advisoryUpdatedAt))
    fail("Fresh official advisory is unavailable, changed scope/severity, or announces a patch");
  if (lock?.lockfileVersion !== 3 || !lock.packages) fail("Unexpected lockfile schema");

  function onlyKnownAdvisory(name, seen = new Set()) {
    if (seen.has(name) || !policy.findings[name]) return false;
    const finding = report.vulnerabilities[name];
    if (!finding || finding.severity !== "high" || !same(finding.nodes, policy.findings[name].nodes)) return false;
    const next = new Set(seen).add(name);
    return finding.via.every(via => typeof via === "string" ? onlyKnownAdvisory(via, next) :
      name === "picomatch" && via?.name === "picomatch" && via.dependency === "picomatch" &&
      via.source === policy.source && via.url === URL && via.severity === "high" && range(via.range) === policy.vulnerableRange);
  }
  for (const [name, finding] of blocking) {
    const expected = policy.findings[name];
    const eligible = finding.severity === "high" && expected && same(finding.nodes, expected.nodes) &&
      finding.nodes.every(node => lock?.packages?.[node]?.version === expected.version && lock.packages[node].dev === true) &&
      onlyKnownAdvisory(name);
    if (!eligible) fail(`${name}: blocking vulnerability is outside the exact exception`);
    else excepted.push(name);
  }
  return { ok: failures.length === 0, excepted, failures };
}
module.exports = { evaluate };

async function main() {
  if (process.argv.length !== 4 || process.argv[2] !== "--root") throw new Error("Usage: node dependency-audit-policy.cjs --root <repository>");
  const root = path.resolve(process.argv[3]);
  const frontend = path.join(root, "src/frontend");
  const reportDir = path.join(process.env.RUNNER_TEMP || path.join(root, "test-results"), "hasm-dependency-audit");
  fs.mkdirSync(reportDir, { recursive: true });
  // Hosted Linux only; do not turn an unsupported local/self-hosted execution into a passing exception.
  if (process.platform !== "linux") throw new Error("Proposed audit runner supports hosted Linux only");
  const audit = spawnSync("npm", ["audit", "--json", "--audit-level=high", "--registry=https://registry.npmjs.org"],
    { cwd: frontend, encoding: "utf8", timeout: 120000, maxBuffer: 16 * 1024 * 1024 });
  fs.writeFileSync(path.join(reportDir, "full-audit.json"), audit.stdout || "");
  process.stdout.write(audit.stdout || ""); // Unfiltered disclosure is retained even when an exception is eligible.
  if (audit.stderr) process.stderr.write(audit.stderr);
  if (audit.error || ![0, 1].includes(audit.status)) throw new Error("Full audit failed to execute");
  const report = JSON.parse(audit.stdout);
  const lock = JSON.parse(fs.readFileSync(path.join(frontend, "package-lock.json"), "utf8"));
  const policy = JSON.parse(fs.readFileSync(path.join(__dirname, "picomatch-exception.json"), "utf8"));
  const trees = Object.fromEntries(TREE_PATHS.map(tree => [tree, execFileSync("git", ["rev-parse", `HEAD:${tree}`], { cwd: root, encoding: "utf8", timeout: 10000 }).trim()]));
  const cleanFrozenInputs = !execFileSync("git", ["status", "--porcelain", "--untracked-files=all", "--", ...TREE_PATHS], { cwd: root, encoding: "utf8", timeout: 10000 }).trim();
  let advisory = null;
  try {
    const response = await fetch(`https://api.github.com/advisories/${ID}`, { headers: { "User-Agent": "HASM-dependency-policy", Accept: "application/vnd.github+json" }, signal: AbortSignal.timeout(15000) });
    if (!response.ok) throw new Error("Official advisory unavailable");
    advisory = await response.json();
    fs.writeFileSync(path.join(reportDir, "official-advisory.json"), JSON.stringify(advisory, null, 2));
  } catch { /* A missing fresh advisory fails eligibility below; the complete audit is already disclosed. */ }
  const result = evaluate(report, audit.status, lock, policy, {
    now: new Date().toISOString(), trees, cleanFrozenInputs, advisory,
    githubActions: process.env.GITHUB_ACTIONS, runnerEnvironment: process.env.RUNNER_ENVIRONMENT,
    runnerOS: process.env.RUNNER_OS, repository: process.env.GITHUB_REPOSITORY,
    headRepository: process.env.HASM_AUDIT_HEAD_REPOSITORY, event: process.env.GITHUB_EVENT_NAME
  });
  fs.writeFileSync(path.join(reportDir, "policy-result.json"), JSON.stringify(result, null, 2));
  if (!result.ok) { process.stderr.write(result.failures.join("\n") + "\n"); process.exitCode = 1; }
  else if (result.excepted.length) console.log(`::warning::UNRESOLVED HIGH ${ID}: exact dev-only exception; owner ${policy.owner}; expires ${policy.expiresAt}; findings ${result.excepted.join(", ")}`);
}
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
