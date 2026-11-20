"use strict";
// Owner-conditionally-approved implementation. The policy stays INACTIVE until all validation passes.
// This file and its policy are protected by ci-guard; the required audit is unchanged during validation.
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync, execFileSync } = require("node:child_process");
const { createHash } = require("node:crypto");
const ID = "GHSA-fc5q-39xc-vjpf";
const URL = `https://github.com/advisories/${ID}`;
const TREE_PATHS = ["src/frontend", "src/desktop", "assets/branding"];
const LEVELS = ["info", "low", "moderate", "high", "critical"];
const APPROVED_SCOPE = "974e885031fcfca8eff952c48b7791be9cacc9d934ddafdfaae264252e3eaa23";
const canonical = value => Array.isArray(value) ? value.map(canonical) : value && typeof value === "object"
  ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
const scopeHash = policy => createHash("sha256").update(JSON.stringify(canonical(Object.fromEntries(
  ["owner", "repository", "advisory", "source", "vulnerableRange", "expiresAt", "trees", "findings"].map(key => [key, policy[key]]))))).digest("hex");
// The retirement is a reviewed, owner-approved state. Its identifying fields are bound to a hash so that editing the status to the retired value (which also lifts the frozen-tree pins) cannot be done together
// with a different owner, approver, repository or advisory without failing here. The hash cannot make the edit itself impossible: review of tools/verify/** remains the control (see docs/security).
const RETIRED_STATUS = "RETIRED-ADVISORY-WITHDRAWN";
const RETIRED_SCOPE = "772acd5353b772d972277b38c7c592f1e0f2ed603ed9786d5cd2ad7c543f3f68";
const retiredHash = policy => createHash("sha256").update(JSON.stringify(canonical(Object.fromEntries(
  ["status", "owner", "approvedBy", "repository", "advisory"].map(key => [key, policy[key]]))))).digest("hex");
function assertRetirement(policy) { if (retiredHash(policy) !== RETIRED_SCOPE) throw new Error("Retirement record differs from the owner-approved retirement"); }
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
  // Permanent strict mode: retirement authorizes ZERO exceptions, including this advisory.
  if (policy?.status === "RETIRED-ADVISORY-WITHDRAWN")
    return { ok: blocking.length === 0, excepted, failures: blocking.map(([name]) => `${name}: High/Critical vulnerability blocks strict audit`) };
  if (!blocking.length) {
    if (lock?.packages?.["node_modules/picomatch"]?.version === "4.0.7")
      return { ok: false, excepted, failures: ["Known affected matcher unexpectedly missing from audit findings"] };
    return { ok: true, excepted, failures }; // No exception is used.
  }
  if (!same(blocking.map(([name]) => name), Object.keys(policy.findings || {})))
    fail("Exact approved six-package finding set changed");
  if (scopeHash(policy) !== APPROVED_SCOPE) fail("Owner-approved scope or hard expiry changed");

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
async function fetchJson(url) {
  const headers = { "User-Agent": "HASM-dependency-policy", Accept: "application/vnd.github+json" };
  if (process.env.GH_TOKEN) headers.Authorization = `Bearer ${process.env.GH_TOKEN}`;
  const response = await fetch(url, { headers, signal: AbortSignal.timeout(15000) });
  if (!response.ok) throw new Error("Required GitHub evidence unavailable");
  const text = await response.text();
  if (Buffer.byteLength(text) > 1024 * 1024) throw new Error("GitHub evidence exceeds limit");
  return JSON.parse(text);
}

function localContext(root, advisory) {
  const trees = Object.fromEntries(TREE_PATHS.map(tree => [tree, execFileSync("git", ["rev-parse", `HEAD:${tree}`], { cwd: root, encoding: "utf8", timeout: 10000 }).trim()]));
  const cleanFrozenInputs = !execFileSync("git", ["status", "--porcelain", "--untracked-files=all", "--", ...TREE_PATHS], { cwd: root, encoding: "utf8", timeout: 10000 }).trim();
  return { now: new Date().toISOString(), trees, cleanFrozenInputs, advisory,
    githubActions: process.env.GITHUB_ACTIONS, runnerEnvironment: process.env.RUNNER_ENVIRONMENT,
    runnerOS: process.env.RUNNER_OS, repository: process.env.GITHUB_REPOSITORY,
    headRepository: process.env.HASM_AUDIT_HEAD_REPOSITORY, event: process.env.GITHUB_EVENT_NAME };
}

async function verifyActivationEvidence(policy, getJson = fetchJson) {
  const proof = policy.activationEvidence;
  if (!proof || !Number.isSafeInteger(proof.runId) || proof.runId <= 0 || !/^[a-f0-9]{40}$/.test(proof.headSha || ""))
    throw new Error("Activation lacks verified CI evidence");
  const base = "https://api.github.com/repos/salem2020s/hasm/actions/runs/" + proof.runId;
  const run = await getJson(base);
  if (run.id !== proof.runId || run.head_sha !== proof.headSha || run.status !== "completed" || run.conclusion !== "success" ||
      run.path?.split("@")[0] !== ".github/workflows/picomatch-validation.yml" || run.repository?.full_name !== "salem2020s/hasm" ||
      run.head_repository?.full_name !== "salem2020s/hasm" || !["pull_request", "push"].includes(run.event))
    throw new Error("Activation validation run is not a matching successful repository workflow");
  const jobs = await getJson(base + "/jobs?per_page=100");
  if (!Array.isArray(jobs.jobs) || jobs.total_count !== jobs.jobs.length) throw new Error("Incomplete validation job evidence");
  for (const name of ["picomatch-policy-validation", "picomatch-artifact-inspection"])
    if (jobs.jobs.filter(job => job.name === name && job.status === "completed" && job.conclusion === "success").length !== 1)
      throw new Error("Required policy/artifact validation did not pass");
}
function verifyProofInputs(policy, root, readGit = args => execFileSync("git", args, { cwd: root, encoding: "utf8", timeout: 10000 }).trim()) {
  const sha = policy.activationEvidence?.headSha;
  if (!/^[a-f0-9]{40}$/.test(sha || "")) throw new Error("Invalid proof commit");
  const paths = [...TREE_PATHS, ".github/workflows/picomatch-validation.yml", ".github/required-checks.json",
    "tools/verify/dependency-audit-policy.cjs", "tools/verify/dependency-audit-policy.test.cjs",
    "tools/verify/inspect-production-artifacts.cjs", "tools/verify/inspect-production-artifacts.test.cjs",
    "tools/verify/validate-picomatch-policy.cjs", "tools/verify/check-docs.js"];
  for (const input of paths) {
    const before = readGit(["rev-parse", `${sha}:${input}`]), current = readGit(["rev-parse", `HEAD:${input}`]);
    if (!/^[a-f0-9]{40}$/.test(before) || before !== current) throw new Error("Activation proof inputs differ: " + input);
  }
  const prior = JSON.parse(readGit(["show", `${sha}:tools/verify/picomatch-exception.json`]));
  if (scopeHash(prior) !== APPROVED_SCOPE || prior.status !== "CONDITIONALLY-APPROVED-INACTIVE" ||
      prior.approvedBy !== policy.approvedBy || prior.approvedAt !== policy.approvedAt || prior.activationEvidence !== null)
    throw new Error("Proof does not preserve the conditionally approved inactive policy");
}
module.exports = { evaluate, scopeHash, localContext, verifyActivationEvidence, verifyProofInputs, retiredHash, assertRetirement, RETIRED_SCOPE, RETIRED_STATUS };

async function main() {
  if (process.argv.length !== 4 || process.argv[2] !== "--root") throw new Error("Usage: node dependency-audit-policy.cjs --root <repository>");
  const root = path.resolve(process.argv[3]);
  const frontend = path.join(root, "src/frontend");
  const reportDir = path.join(process.env.RUNNER_TEMP || path.join(root, "test-results"), "hasm-dependency-audit");
  fs.mkdirSync(reportDir, { recursive: true });
  if (process.platform !== "linux") throw new Error("Audit runner supports hosted Linux only");
  const audit = spawnSync("npm", ["audit", "--json", "--audit-level=high", "--registry=https://registry.npmjs.org"],
    { cwd: frontend, encoding: "utf8", timeout: 120000, maxBuffer: 16 * 1024 * 1024 });
  fs.writeFileSync(path.join(reportDir, "full-audit.json"), audit.stdout || "");
  process.stdout.write(audit.stdout || ""); // Unfiltered disclosure is retained even when an exception is eligible.
  if (audit.stderr) process.stderr.write(audit.stderr);
  if (audit.error || ![0, 1].includes(audit.status)) throw new Error("Full audit failed to execute");
  const report = JSON.parse(audit.stdout);
  const lock = JSON.parse(fs.readFileSync(path.join(frontend, "package-lock.json"), "utf8"));
  const policy = JSON.parse(fs.readFileSync(path.join(__dirname, "picomatch-exception.json"), "utf8"));
  if (policy.status === RETIRED_STATUS) assertRetirement(policy);
  let advisory = null;
  try {
    advisory = await fetchJson(`https://api.github.com/advisories/${ID}`);
    fs.writeFileSync(path.join(reportDir, "official-advisory.json"), JSON.stringify(advisory, null, 2));
  } catch { /* A missing fresh advisory fails eligibility below; the complete audit is already disclosed. */ }
  const result = evaluate(report, audit.status, lock, policy, localContext(root, advisory));
  if (result.ok && result.excepted.length) {
    try { await verifyActivationEvidence(policy); verifyProofInputs(policy, root); }
    catch (error) { result.ok = false; result.failures.push(error.message); }
  }
  fs.writeFileSync(path.join(reportDir, "policy-result.json"), JSON.stringify(result, null, 2));
  if (!result.ok) { process.stderr.write(result.failures.join("\n") + "\n"); process.exitCode = 1; }
  else if (result.excepted.length) console.log(`::warning::UNRESOLVED HIGH ${ID}: exact dev-only exception; owner ${policy.owner}; expires ${policy.expiresAt}; findings ${result.excepted.join(", ")}`);
}
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
