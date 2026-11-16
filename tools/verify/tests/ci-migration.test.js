"use strict";
// Tests of the CI migration to the self-hosted runner: the PostgreSQL TRX verification (replaces the former Python step) and the pull_request_target guard.
const test = require("node:test");
const assert = require("node:assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { execFileSync } = require("child_process");
const trx = require("../pg-trx-check.js");
const guard = require("../ci-guard.js");

const REGISTRY = `jobs:
  x:
    steps:
      - env:
          EXPECTED_REGISTRY: |
            EXPECTED = {
                "AlphaTests": 2, "BetaTests": 1,
                # a comment "GammaTests": 99,
            }
        run: true
`;
const result = (cls, name, outcome = "Passed") => `<UnitTestResult executionId="1" testName="Hasm.Database.IntegrationTests.${cls}.${name}" outcome="${outcome}" />`;
const file = (...r) => `<?xml version="1.0"?><TestRun><Results>${r.join("")}</Results></TestRun>`;

test("trx check: exact counts per class and all Passed succeed; SafetyGateTests are ignored", () => {
  const ok = file(result("AlphaTests", "A"), result("AlphaTests", "B(x: 1)"), result("BetaTests", "C"), result("SafetyGateTests", "Pure"));
  const r = trx.check(ok, REGISTRY);
  assert.ok(r.ok, r.problems.join("; "));
  assert.deepStrictEqual([r.found, r.total], [3, 3]);
});

test("trx check: a missing, extra, skipped, failed or unregistered test fails", () => {
  const base = [result("AlphaTests", "A"), result("AlphaTests", "B"), result("BetaTests", "C")];
  assert.ok(trx.check(file(...base.slice(1)), REGISTRY).problems.some(p => /AlphaTests: expected 2, found 1/.test(p)));                   // a test disappeared
  assert.ok(trx.check(file(...base, result("BetaTests", "D")), REGISTRY).problems.some(p => /BetaTests: expected 1, found 2/.test(p)));  // an extra, unregistered count
  assert.ok(trx.check(file(base[0], base[1], result("BetaTests", "C", "NotExecuted")), REGISTRY).problems.some(p => /outcomes/.test(p)));  // skipped
  assert.ok(trx.check(file(base[0], base[1], result("BetaTests", "C", "Failed")), REGISTRY).problems.some(p => /outcomes/.test(p)));
  assert.ok(trx.check(file(...base, result("NewTests", "N")), REGISTRY).problems.some(p => /unregistered test classes/.test(p)));         // a new class must be registered
});

test("trx check: a missing or forged registry fails closed", () => {
  assert.strictEqual(trx.check(file(), "no registry here").ok, false);
  const forged = REGISTRY.replace('"BetaTests": 1,', '"BetaTests": 1, # "AlphaTests": 9,');
  assert.strictEqual(trx.check(file(result("AlphaTests", "A"), result("AlphaTests", "B"), result("BetaTests", "C")), forged).ok, true);   // a forged value after a comment is ignored
  const dup = REGISTRY.replace('"BetaTests": 1,', '"BetaTests": 1, "AlphaTests": 9,');
  assert.ok(trx.check(file(), dup).problems.some(p => /duplicate registry key/.test(p)));
});

test("trx check: the command line tool reads the registry from the workflow file and exits non-zero on a mismatch", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-trx-"));
  fs.writeFileSync(path.join(dir, "wf.yml"), REGISTRY);
  fs.writeFileSync(path.join(dir, "ok.trx"), file(result("AlphaTests", "A"), result("AlphaTests", "B"), result("BetaTests", "C")));
  fs.writeFileSync(path.join(dir, "bad.trx"), file(result("AlphaTests", "A"), result("BetaTests", "C")));
  const tool = path.join(__dirname, "..", "pg-trx-check.js");
  execFileSync("node", [tool, path.join(dir, "ok.trx"), path.join(dir, "wf.yml")], { stdio: "pipe" });
  assert.throws(() => execFileSync("node", [tool, path.join(dir, "bad.trx"), path.join(dir, "wf.yml")], { stdio: "pipe" }));
});

test("the real backend workflow registry parses without errors and the tool accepts its own total", () => {
  const text = fs.readFileSync(path.join(__dirname, "..", "..", "..", ".github", "workflows", "backend.yml"), "utf8");
  const p = guard.parseRegistry(text);
  assert.deepStrictEqual(p.errors, []);
  assert.ok(Object.keys(p.map).length > 100);
});

const git = (cwd, ...a) => execFileSync("git", ["-C", cwd, "-c", "user.email=t@t", "-c", "user.name=t", ...a], { encoding: "utf8" });
test("guard: adding pull_request_target to a workflow is refused", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-prt-"));
  git(root, "init", "-q", "-b", "main");
  fs.mkdirSync(path.join(root, ".github", "workflows"), { recursive: true });
  fs.mkdirSync(path.join(root, "tools", "verify"), { recursive: true });
  fs.writeFileSync(path.join(root, ".github", "workflows", "a.yml"), "name: A\non:\n  pull_request:\njobs:\n  j:\n    runs-on: ubuntu-24.04\n");
  fs.writeFileSync(path.join(root, ".github", "required-checks.json"), JSON.stringify({ checks: [] }));
  fs.writeFileSync(path.join(root, "tools", "verify", "ci-guard.js"), "//\n");
  git(root, "add", "-A"); git(root, "commit", "-q", "-m", "base"); git(root, "checkout", "-q", "-b", "pr");
  fs.writeFileSync(path.join(root, ".github", "workflows", "a.yml"), "name: A\non:\n  pull_request_target:\njobs:\n  j:\n    runs-on: [self-hosted, Windows, X64]\n");
  git(root, "add", "-A"); git(root, "commit", "-q", "-m", "x");
  const f = guard.run(root, "main", { CI_CHANGE_APPROVED: "1" });
  assert.ok(f.some(x => /pull_request_target/.test(x)), f.join("\n"));
});

test("the workflows use only the intended runner labels, never pull_request_target, and gate every job by the runner mode", () => {
  const dir = path.join(__dirname, "..", "..", "..", ".github", "workflows");
  const SAME = "github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository";
  const jobsOf = (text) => text.slice(text.indexOf("\njobs:\n")).split(/\n(?=  [A-Za-z0-9_-]+:\n)/).slice(1).map((j) => ({ id: j.match(/^  ([A-Za-z0-9_-]+):/)[1], text: j }));
  const read = (f) => fs.readFileSync(path.join(dir, f), "utf8").replace(/\r\n/g, "\n");
  for (const f of fs.readdirSync(dir).filter(x => /\.ya?ml$/.test(x))) {
    const text = read(f);
    assert.ok(!/^[^#\n]*\bpull_request_target\b/m.test(text), `${f} uses pull_request_target`);
    assert.ok(!/^    name:/m.test(text.slice(text.indexOf("\njobs:\n"))), `${f}: a job-level name would rename a check`);
    for (const m of text.matchAll(/^\s*runs-on\s*:\s*(.+)$/gm)) assert.ok(/^(\[self-hosted, Windows, X64\]|ubuntu-24\.04|ubuntu-latest|windows-latest)\s*$/.test(m[1]), `${f}: unexpected runs-on ${m[1]}`);
    if (f === "linux-verification.yml") continue;
    const selfHostedFile = f.endsWith("-selfhosted.yml");
    for (const job of jobsOf(text)) {
      if (selfHostedFile) {
        // a job on the self-hosted runner runs only in that mode and only code of THIS repository (never a fork)
        assert.ok(/^    runs-on: \[self-hosted, Windows, X64\]$/m.test(job.text), `${f}: ${job.id} is not on the self-hosted runner`);
        assert.ok(job.text.includes("if: ${{ vars.HASM_RUNNER_MODE == 'self-hosted' && (" + SAME + ") }}"), `${f}: self-hosted job ${job.id} is not gated by the mode and the same-repository condition`);
      } else {
        assert.ok(!/self-hosted, Windows/.test(job.text), `${f}: hosted workflow ${job.id} runs on the self-hosted runner`);
        assert.ok(job.text.includes("if: ${{ vars.HASM_RUNNER_MODE != 'self-hosted' }}"), `${f}: hosted job ${job.id} is not gated by the mode`);
      }
    }
    if (selfHostedFile) assert.deepStrictEqual(jobsOf(text).map(j => j.id), jobsOf(read(f.replace("-selfhosted", ""))).map(j => j.id), `${f}: the job ids (= check names) differ from the hosted twin`);
  }
});

test("guard: pull_request_target is refused in the list and inline forms of the on: key too", () => {
  for (const on of ["on: pull_request_target", "on: [push, pull_request_target]", "on:\n  pull_request_target:\n    types: [opened]"]) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-prt2-"));
    git(root, "init", "-q", "-b", "main");
    fs.mkdirSync(path.join(root, ".github", "workflows"), { recursive: true });
    fs.mkdirSync(path.join(root, "tools", "verify"), { recursive: true });
    fs.writeFileSync(path.join(root, ".github", "workflows", "a.yml"), "name: A\non: push\njobs:\n  j:\n    runs-on: ubuntu-24.04\n");
    fs.writeFileSync(path.join(root, ".github", "required-checks.json"), JSON.stringify({ checks: [] }));
    fs.writeFileSync(path.join(root, "tools", "verify", "ci-guard.js"), "//\n");
    git(root, "add", "-A"); git(root, "commit", "-q", "-m", "base"); git(root, "checkout", "-q", "-b", "pr");
    fs.writeFileSync(path.join(root, ".github", "workflows", "a.yml"), "name: A\n" + on + "\njobs:\n  j:\n    runs-on: ubuntu-24.04\n");
    git(root, "add", "-A"); git(root, "commit", "-q", "-m", "x");
    const f = guard.run(root, "main", { CI_CHANGE_APPROVED: "1" });
    assert.ok(f.some(x => /pull_request_target/.test(x)), on + "\n" + f.join("\n"));
  }
});
