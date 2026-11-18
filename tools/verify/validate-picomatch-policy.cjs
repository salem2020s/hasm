"use strict";
// Additional pre-activation CI proof. This does NOT replace or satisfy dependency-scan.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const { evaluate, localContext } = require("./dependency-audit-policy.cjs");
assert.equal(process.versions.node.split(".")[0], "24", "Validation requires Node.js 24");
assert.equal(process.platform, "linux", "Validation requires Linux");
assert.equal(process.argv.length, 4); assert.equal(process.argv[2], "--root");
const root = path.resolve(process.argv[3]);
const policy = JSON.parse(fs.readFileSync(path.join(__dirname, "picomatch-exception.json"), "utf8"));
assert.ok(["CONDITIONALLY-APPROVED-INACTIVE", "APPROVED"].includes(policy.status));
assert.equal(policy.approvedBy, "salem2020s");
const live = spawnSync(process.execPath, [path.join(__dirname, "dependency-audit-policy.cjs"), "--root", root],
  { encoding: "utf8", timeout: 180000, maxBuffer: 20 * 1024 * 1024 });
process.stdout.write(live.stdout || ""); process.stderr.write(live.stderr || "");
assert.equal(live.error, undefined, "Actual Linux audit CLI must execute successfully");
const reportDir = path.join(process.env.RUNNER_TEMP, "hasm-dependency-audit");
const report = JSON.parse(fs.readFileSync(path.join(reportDir, "full-audit.json"), "utf8"));
const advisory = JSON.parse(fs.readFileSync(path.join(reportDir, "official-advisory.json"), "utf8"));
const actual = JSON.parse(fs.readFileSync(path.join(reportDir, "policy-result.json"), "utf8"));
const lock = JSON.parse(fs.readFileSync(path.join(root, "src/frontend/package-lock.json"), "utf8"));
const candidate = { ...policy, status: "APPROVED" };
const eligible = evaluate(report, 1, lock, candidate, localContext(root, advisory));
assert.equal(eligible.ok, true, JSON.stringify(eligible.failures));
assert.equal(eligible.excepted.length, 6);
if (policy.status === "CONDITIONALLY-APPROVED-INACTIVE") {
  assert.equal(live.status, 1, "The actual unactivated gate must still reject the advisory");
  assert.equal(actual.ok, false);
  assert.deepEqual(actual.failures, ["Exception is unapproved, expired, future-dated or longer than seven days"]);
} else {
  assert.equal(live.status, 0, "The active policy must satisfy real CLI and activation-evidence checks");
  assert.equal(actual.ok, true);
}
fs.writeFileSync(path.join(reportDir, "validation-only.json"), JSON.stringify({ node: process.version, platform: process.platform,
  actualPolicyStatus: policy.status, actualExit: live.status, actualGateResult: actual, candidateEligibility: eligible,
  mandatoryGateReplacedByThisValidation: false }, null, 2));
console.log("Node24/Linux real audit CLI validated; candidate eligibility is not policy activation.");
