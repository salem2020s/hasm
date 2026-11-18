"use strict";
// Synthetic approved metadata in these fixtures is NOT owner approval or an active exception.
const test = require("node:test");
const assert = require("node:assert/strict");
const proposed = require("./picomatch-exception.json");
const { evaluate, scopeHash, verifyActivationEvidence, verifyProofInputs } = require("./dependency-audit-policy.cjs");
const copy = value => structuredClone(value);
const ID = proposed.advisory;
function counts(report) {
  const values = { info: 0, low: 0, moderate: 0, high: 0, critical: 0, total: 0 };
  for (const row of Object.values(report.vulnerabilities)) { values[row.severity]++; values.total++; }
  report.metadata = { vulnerabilities: values };
}
function fixture() {
  const policy = { ...copy(proposed), status: "APPROVED", approvedBy: "salem2020s", approvedAt: "2026-11-17T22:00:00Z", approvalReference: "SYNTHETIC TEST FIXTURE ONLY" };
  const via = { picomatch: [{ source: policy.source, name: "picomatch", dependency: "picomatch", url: `https://github.com/advisories/${ID}`, severity: "high", range: "<=4.0.7" }],
    fdir: ["picomatch"], tinyglobby: ["fdir", "picomatch"], vite: ["picomatch", "tinyglobby"], vitest: ["picomatch", "tinyglobby", "vite"], "@vitejs/plugin-react": ["vite"] };
  const report = { auditReportVersion: 2, vulnerabilities: {} };
  const lock = { lockfileVersion: 3, packages: {} };
  for (const [name, value] of Object.entries(policy.findings)) {
    report.vulnerabilities[name] = { name, severity: "high", nodes: [...value.nodes], via: via[name] };
    for (const node of value.nodes) lock.packages[node] = { version: value.version, dev: true };
  }
  counts(report);
  const context = { now: "2026-11-18T00:00:00Z", githubActions: "true", runnerEnvironment: "github-hosted", runnerOS: "Linux",
    repository: "salem2020s/hasm", headRepository: "salem2020s/hasm", event: "pull_request", cleanFrozenInputs: true, trees: copy(policy.trees),
    advisory: { ghsa_id: ID, severity: "high", updated_at: policy.advisoryUpdatedAt, vulnerabilities: [{ package: { ecosystem: "npm", name: "picomatch" }, vulnerable_version_range: "<= 4.0.7", first_patched_version: null }] } };
  return { report, exit: 1, lock, policy, context };
}
const run = f => evaluate(f.report, f.exit, f.lock, f.policy, f.context);
const denied = f => assert.equal(run(f).ok, false);

test("unapproved policy cannot authorize an exception", () => {
  const f = fixture(); f.policy = { ...copy(proposed), status: "PROPOSED-NOT-APPROVED", approvedBy: null, approvedAt: null, approvalReference: null }; denied(f);
});
test("only the exact six inherited High findings are eligible with synthetic approval", () => {
  const f = fixture(), before = copy(f); const result = run(f);
  assert.equal(result.ok, true); assert.deepEqual(result.excepted.sort(), Object.keys(f.policy.findings).sort()); assert.deepEqual(f, before);
});
test("another advisory on picomatch blocks it and its inherited findings", () => {
  const f = fixture(); f.report.vulnerabilities.picomatch.via.push({ ...f.report.vulnerabilities.picomatch.via[0], url: "https://github.com/advisories/GHSA-other", source: 999 }); denied(f);
});
test("a missing inherited finding cannot silently shrink the approved exception set", () => {
  const f = fixture(); delete f.report.vulnerabilities.fdir; counts(f.report); denied(f);
});
test("all other High and Critical findings remain blocking", () => {
  for (const severity of ["high", "critical"]) {
    const f = fixture(); f.report.vulnerabilities.other = { name: "other", severity, nodes: ["node_modules/other"], via: [{ url: "https://github.com/advisories/GHSA-other" }] }; counts(f.report); denied(f);
  }
  const f = fixture(); f.report.vulnerabilities.picomatch.severity = "critical"; counts(f.report); denied(f);
});
test("package versions, development classification and exact node paths are mandatory", () => {
  for (const change of [f => f.lock.packages["node_modules/picomatch"].version = "4.0.8", f => f.lock.packages["node_modules/picomatch"].dev = false,
    f => delete f.lock.packages["node_modules/picomatch"].dev, f => f.report.vulnerabilities.picomatch.nodes.push("node_modules/vite/node_modules/picomatch"),
    f => f.report.vulnerabilities.picomatch.nodes = ["node_modules/../picomatch"], f => f.lock.lockfileVersion = 2]) { const f = fixture(); change(f); denied(f); }
});
test("runtime/build source trees must remain frozen and clean", () => {
  for (const tree of Object.keys(proposed.trees)) { const f = fixture(); f.context.trees[tree] = "changed"; denied(f); }
  const f = fixture(); f.context.cleanFrozenInputs = false; denied(f);
});
test("self-hosted, foreign, forked and unsupported contexts never receive the exception", () => {
  for (const [key, value] of [["githubActions", "false"], ["runnerEnvironment", "self-hosted"], ["runnerOS", "Windows"],
    ["repository", "someone/else"], ["headRepository", "someone/fork"], ["event", "workflow_dispatch"]]) { const f = fixture(); f.context[key] = value; denied(f); }
});
test("expiry, future approval, absent owner record and excessive duration fail closed", () => {
  for (const change of [f => f.context.now = f.policy.expiresAt, f => f.policy.approvedAt = "2026-11-19T00:00:00Z", f => f.policy.approvalReference = null,
    f => f.policy.approvedBy = "someone", f => f.policy.expiresAt = "2026-12-01T00:00:00Z", f => f.context.now = "invalid"]) { const f = fixture(); change(f); denied(f); }
});
test("an announced patch or missing/changed official advisory ends eligibility", () => {
  for (const change of [f => f.context.advisory = null, f => f.context.advisory.vulnerabilities[0].first_patched_version = "4.0.8",
    f => f.context.advisory.severity = "critical", f => f.context.advisory.updated_at = "invalid", f => f.context.advisory.updated_at = "2026-01-01T00:00:00Z",
    f => f.context.advisory.vulnerabilities[0].vulnerable_version_range = "<=4.0.8"]) { const f = fixture(); change(f); denied(f); }
});
test("network/command errors, unexpected schemas and inconsistent counts cannot pass", () => {
  for (const change of [f => f.exit = 2, f => f.exit = null, f => f.exit = 0, f => f.report.error = { code: "network-error" },
    f => f.report.auditReportVersion = 3, f => delete f.report.metadata, f => f.report.metadata.vulnerabilities.high = 0,
    f => f.report.vulnerabilities.picomatch.via = [], f => f.report.vulnerabilities.picomatch.severity = "unknown"]) { const f = fixture(); change(f); denied(f); }
});
test("unknown or cyclic inherited-advisory chains are rejected", () => {
  for (const via of [["unknown"], ["vite"]]) { const f = fixture(); f.report.vulnerabilities.picomatch.via = via; denied(f); }
});
test("lower findings remain reported with the existing High threshold", () => {
  const f = fixture(); f.report.vulnerabilities.other = { name: "other", severity: "moderate", nodes: ["node_modules/other"], via: [{ url: "https://github.com/advisories/GHSA-other" }] }; counts(f.report);
  const result = run(f); assert.equal(result.ok, true); assert.equal(result.excepted.includes("other"), false); assert.equal(f.report.vulnerabilities.other.severity, "moderate");
});
test("a clean audit uses no exception; an unexpectedly absent known advisory is not silently accepted", () => {
  const f = fixture(); f.report.vulnerabilities = {}; counts(f.report); f.exit = 0; denied(f);
  f.lock.packages = {}; f.policy = copy(proposed); const result = run(f); assert.equal(result.ok, true); assert.deepEqual(result.excepted, []);
});

test("owner scope and absolute November24 expiry cannot be extended or substituted", () => {
  assert.equal(scopeHash(proposed), "974e885031fcfca8eff952c48b7791be9cacc9d934ddafdfaae264252e3eaa23");
  for (const change of [f => f.policy.expiresAt = "2026-11-25T00:00:00Z", f => f.policy.source++,
    f => f.policy.findings.picomatch.version = "4.0.8", f => f.policy.trees["src/frontend"] = "changed"]) {
    const f = fixture(); change(f); denied(f);
    assert.ok(run(f).failures.includes("Owner-approved scope or hard expiry changed"));
  }
  const f = fixture(); f.context.now = "2026-11-24T00:00:00Z"; denied(f);
  f.context.now = "2026-11-23T23:59:59.999Z"; assert.equal(run(f).ok, true);
});

function evidence() {
  const policy = { ...copy(proposed), activationEvidence: { runId: 123, headSha: "a".repeat(40) } };
  const run = { id: 123, head_sha: policy.activationEvidence.headSha, status: "completed", conclusion: "success",
    path: ".github/workflows/picomatch-validation.yml", repository: { full_name: "salem2020s/hasm" },
    head_repository: { full_name: "salem2020s/hasm" }, event: "pull_request" };
  const jobs = { total_count: 2, jobs: ["picomatch-policy-validation", "picomatch-artifact-inspection"]
    .map(name => ({ name, status: "completed", conclusion: "success" })) };
  return { policy, run, jobs };
}
test("activation requires the matching successful real workflow and both successful jobs", async () => {
  const f = evidence(), urls = [];
  await verifyActivationEvidence(f.policy, async url => { urls.push(url); return url.includes("/jobs?") ? f.jobs : f.run; });
  assert.deepEqual(urls, ["https://api.github.com/repos/salem2020s/hasm/actions/runs/123", "https://api.github.com/repos/salem2020s/hasm/actions/runs/123/jobs?per_page=100"]);
});
test("missing, substituted, incomplete, skipped and inaccessible activation evidence fails closed", async () => {
  for (const change of [f => f.policy.activationEvidence = null, f => f.run.head_sha = "b".repeat(40),
    f => f.run.conclusion = "failure", f => f.run.path = ".github/workflows/verification.yml",
    f => f.run.repository.full_name = "other/repo", f => f.run.head_repository.full_name = "other/fork",
    f => f.run.event = "workflow_dispatch", f => f.jobs.total_count++, f => f.jobs.jobs[0].conclusion = "skipped",
    f => f.jobs.jobs.pop(), f => f.jobs.jobs.push(copy(f.jobs.jobs[0]))]) {
    const f = evidence(); change(f);
    await assert.rejects(verifyActivationEvidence(f.policy, async url => url.includes("/jobs?") ? f.jobs : f.run));
  }
  await assert.rejects(verifyActivationEvidence(evidence().policy, async () => { throw new Error("network unavailable"); }));
});

test("proof commit must preserve frozen sources and all validation implementation blobs", () => {
  const policy = evidence().policy;
  const read = args => args[0] === "show" ? JSON.stringify({ ...proposed, status: "CONDITIONALLY-APPROVED-INACTIVE", activationEvidence: null }) : "c".repeat(40);
  verifyProofInputs(policy, "unused", read);
  for (const input of ["src/frontend", "src/desktop", "assets/branding", ".github/workflows/picomatch-validation.yml",
    "tools/verify/dependency-audit-policy.cjs", "tools/verify/inspect-production-artifacts.cjs", "tools/verify/validate-picomatch-policy.cjs"])
    assert.throws(() => verifyProofInputs(policy, "unused", args => args[0] === "rev-parse" && args[1] === `HEAD:${input}` ? "d".repeat(40) : read(args)));
  assert.throws(() => verifyProofInputs(policy, "unused", args => args[0] === "show" ? JSON.stringify({ ...proposed, status: "APPROVED" }) : read(args)));
  assert.throws(() => verifyProofInputs(policy, "unused", () => { throw new Error("missing commit"); }));
});
