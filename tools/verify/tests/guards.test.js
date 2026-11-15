"use strict";
// Self-tests of the Phase 4.0 verification controls: every guard must FAIL on a deliberately broken sample and PASS on a clean one.
const test = require("node:test");
const assert = require("node:assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { execFileSync } = require("child_process");
const guard = require("../ci-guard.js");
const head = require("../verify-head.js");
const scan = require("../scan-secrets.js");
const review = require("../check-review-record.js");
const docs = require("../check-docs.js");

const git = (cwd, ...a) => execFileSync("git", ["-C", cwd, "-c", "user.email=t@t", "-c", "user.name=t", ...a], { encoding: "utf8" });
const write = (root, f, t) => { fs.mkdirSync(path.dirname(path.join(root, f)), { recursive: true }); fs.writeFileSync(path.join(root, f), t); };

const BACKEND = `name: Backend
jobs:
  backend-build-test:
    runs-on: ubuntu-latest
    steps:
      - run: dotnet test
  postgres-integration:
    runs-on: ubuntu-latest
    steps:
      - run: |
          python3 - <<'PY'
          EXPECTED = {
              "AlphaTests": 10, "BetaTests": 5,
              # a comment "GammaTests": 99,
          }
          TOTAL = sum(EXPECTED.values())
          PY
`;
function repo(files, extra = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-guard-"));
  git(root, "init", "-q", "-b", "main");
  write(root, ".github/workflows/backend.yml", BACKEND);
  write(root, ".github/required-checks.json", JSON.stringify({ checks: [{ name: "backend-build-test", paths: ["**"] }, { name: "postgres-integration", paths: ["**"] }] }));
  write(root, "tests/AlphaTests.cs", "public class AlphaTests { [Fact] public void A() {} }\n");
  write(root, "tools/verify/ci-guard.js", "// base copy\n");
  for (const [f, t] of Object.entries(extra)) write(root, f, t);
  git(root, "add", "-A"); git(root, "commit", "-q", "-m", "base");
  git(root, "checkout", "-q", "-b", "pr");
  for (const [f, t] of Object.entries(files)) { if (t === null) fs.rmSync(path.join(root, f)); else write(root, f, t); }
  git(root, "add", "-A"); git(root, "commit", "-q", "-m", "change", "--allow-empty");
  return root;
}

test("guard: a clean change passes", () => {
  const r = repo({ "docs/x.md": "hello\n" });
  assert.deepStrictEqual(guard.run(r, "main", {}), []);
});
test("guard: a raised count passes, a lowered count is refused", () => {
  assert.deepStrictEqual(guard.run(repo({ ".github/workflows/backend.yml": BACKEND.replace('"AlphaTests": 10', '"AlphaTests": 11') }), "main", { CI_CHANGE_APPROVED: "1" }), []);
  const f = guard.run(repo({ ".github/workflows/backend.yml": BACKEND.replace('"AlphaTests": 10', '"AlphaTests": 9') }), "main", {});
  assert.ok(f.some(x => /lowered from 10 to 9/.test(x)), f.join("\n"));
  assert.ok(f.some(x => /total expected PostgreSQL tests decreased/.test(x)));
});
test("guard: removing a test class from the registry is refused", () => {
  const f = guard.run(repo({ ".github/workflows/backend.yml": BACKEND.replace(' "BetaTests": 5,', "") }), "main", {});
  assert.ok(f.some(x => /BetaTests.*removed/.test(x)), f.join("\n"));
});
test("guard: commented registry lines are not counted", () => {
  assert.deepStrictEqual(guard.parseRegistry(BACKEND), { AlphaTests: 10, BetaTests: 5 });
});
test("guard: continue-on-error, if: false, || true and --filter are refused", () => {
  for (const [needle, re] of [["    steps:\n      - run: dotnet test", /continue-on-error/], ["      - run: dotnet test", /if: false/], ["      - run: dotnet test", /\|\| true/], ["      - run: dotnet test", /--filter/]]) {
    const patched = re.source.startsWith("continue") ? BACKEND.replace("    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test", "    runs-on: ubuntu-latest\n    continue-on-error: true\n    steps:\n      - run: dotnet test")
      : re.source === "if: false" ? BACKEND.replace("      - run: dotnet test", "      - if: false\n        run: dotnet test")
      : re.source.includes("true") ? BACKEND.replace("      - run: dotnet test", "      - run: dotnet test || true")
      : BACKEND.replace("      - run: dotnet test", "      - run: dotnet test --filter Category=Fast");
    const f = guard.run(repo({ ".github/workflows/backend.yml": patched }), "main", { CI_CHANGE_APPROVED: "1" });
    assert.ok(f.length >= 1 && f.some(x => /added/.test(x)), `${needle}: ${f.join(" | ")}`);
  }
});
test("guard: removing a required job or deleting a workflow is refused", () => {
  const noPg = BACKEND.split("  postgres-integration:")[0];
  const f = guard.run(repo({ ".github/workflows/backend.yml": noPg }), "main", { CI_CHANGE_APPROVED: "1" });
  assert.ok(f.some(x => /required job postgres-integration was removed/.test(x)), f.join("\n"));
  const g = guard.run(repo({ ".github/workflows/backend.yml": null }), "main", { CI_CHANGE_APPROVED: "1" });
  assert.ok(g.some(x => /workflow file deleted/.test(x)), g.join("\n"));
});
test("guard: added skipped tests are refused", () => {
  const f = guard.run(repo({ "tests/AlphaTests.cs": "public class AlphaTests { [Fact(Skip = \"later\")] public void A() {} }\n" }), "main", {});
  assert.ok(f.some(x => /skipped or focused tests/.test(x)), f.join("\n"));
  const g = guard.run(repo({ "src/a.test.tsx": "it.skip('x', () => {});\n" }), "main", {});
  assert.ok(g.some(x => /skipped or focused tests/.test(x)), g.join("\n"));
});
test("guard: protected verification files need the approval flag once the guard exists on the base", () => {
  const r = repo({ ".github/required-checks.json": JSON.stringify({ checks: [] }) });
  assert.ok(guard.run(r, "main", {}).some(x => /owner approval flag/.test(x)));
  assert.deepStrictEqual(guard.run(r, "main", { CI_CHANGE_APPROVED: "1" }), []);
});
test("guard: the change that introduces the guard is not judged by the protected-file rule", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-guard-boot-"));
  git(root, "init", "-q", "-b", "main"); write(root, "README.md", "x\n"); git(root, "add", "-A"); git(root, "commit", "-q", "-m", "base");
  git(root, "checkout", "-q", "-b", "pr"); write(root, "tools/verify/ci-guard.js", "//\n"); write(root, ".github/required-checks.json", "{\"checks\":[]}"); git(root, "add", "-A"); git(root, "commit", "-q", "-m", "intro");
  assert.deepStrictEqual(guard.run(root, "main", {}), []);
});

const manifest = { checks: [{ name: "backend-build-test", count: 1, paths: ["src/backend/**", "tests/**"] }, { name: "ci-guard", count: 1, paths: ["**"] }, { name: "frontend-build-test", count: 1, paths: ["src/frontend/**"] }] };
const run = (name, headSha = "a".repeat(40), status = "completed", conclusion = "success") => ({ name, status, conclusion, headSha });
test("head verification: all required checks green on the head passes; unrelated checks are not required", () => {
  assert.deepStrictEqual(head.evaluate({ headSha: "a".repeat(40), files: ["src/backend/x.cs"], manifest, checkRuns: [run("backend-build-test"), run("ci-guard")] }), []);
});
test("head verification: missing, pending, failed, skipped and older-SHA checks are refused", () => {
  const base = { headSha: "a".repeat(40), files: ["src/backend/x.cs"], manifest };
  assert.ok(head.evaluate({ ...base, checkRuns: [run("ci-guard")] }).some(x => /backend-build-test: expected 1 run/.test(x)));
  assert.ok(head.evaluate({ ...base, checkRuns: [run("backend-build-test", undefined, "in_progress", null), run("ci-guard")] }).some(x => /still in_progress/.test(x)));
  assert.ok(head.evaluate({ ...base, checkRuns: [run("backend-build-test", undefined, "completed", "failure"), run("ci-guard")] }).some(x => /conclusion failure/.test(x)));
  assert.ok(head.evaluate({ ...base, checkRuns: [run("backend-build-test", undefined, "completed", "skipped"), run("ci-guard")] }).some(x => /conclusion skipped/.test(x)));
  const old = head.evaluate({ ...base, checkRuns: [run("backend-build-test", "b".repeat(40)), run("ci-guard")] });
  assert.ok(old.some(x => /older SHA/.test(x)), old.join("\n"));
});
test("head verification: a different head than the expected SHA is refused", () => {
  assert.ok(head.evaluate({ headSha: "a".repeat(40), expectSha: "c".repeat(7), files: ["x"], manifest, checkRuns: [run("ci-guard")] }).some(x => /differs from the expected/.test(x)));
});

test("secret scan: planted fake secrets are found without printing values, allowances need a reason, stale allowances are reported", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-scan-"));
  const fakeKey = "-----BEGIN " + "RSA PRIVATE KEY-----";
  const fakeToken = "gh" + "p_" + "A".repeat(36);
  write(root, "a.txt", fakeKey + "\n"); write(root, "b.md", "token " + fakeToken + "\n"); write(root, "c.yml", "x: Host=h;Password=" + "hunter2hunter2" + ";\n"); write(root, "d.yml", "x: Password=${SECRET};\n"); write(root, "e.cs", "var c = \"Password=" + "hunter2hunter2" + "\";\n");
  const hits = scan.scan(root, ["a.txt", "b.md", "c.yml", "d.yml", "e.cs"]);
  assert.deepStrictEqual(hits.map(h => h.file + ":" + h.rule).sort(), ["a.txt:private-key", "b.md:github-token", "c.yml:connection-password"]);
  const { left, stale } = scan.filterAllowed(hits, [{ file: "c.yml", rule: "connection-password", reason: "throwaway" }, { file: "gone.yml", rule: "private-key", reason: "old" }, { file: "a.txt", rule: "private-key" }]);
  assert.deepStrictEqual(left.map(h => h.file).sort(), ["a.txt", "b.md"]);                      // an allowance without a reason does not count
  assert.deepStrictEqual(stale.map(a => a.file).sort(), ["a.txt", "gone.yml"]);
});

test("review record: complete record bound to the head passes; wrong SHA, placeholders and approval with open findings fail", () => {
  const sha = "1".repeat(40);
  const ok = `* **Pull request:** #1\n* **Head SHA reviewed (full 40 characters):** ${sha}\n* **Reviewer context:** separate agent\n* **Findings:** none\n* **Blocker or High findings open:** none\n* **NOT VERIFIED:** load tests\n* **Verdict:** approve\n`;
  assert.deepStrictEqual(review.validate(ok, sha), []);
  assert.ok(review.validate(ok, "2".repeat(40)).some(x => /not the PR head/.test(x)));
  assert.ok(review.validate(ok.replace("separate agent", ""), sha).some(x => /Reviewer context/.test(x)));
  assert.ok(review.validate(ok.replace("Blocker or High findings open:** none", "Blocker or High findings open:** F-1"), sha).some(x => /open Blocker\/High/.test(x)));
  assert.ok(review.validate(fs.readFileSync(path.join(__dirname, "../../../docs/review-records/TEMPLATE.md"), "utf8"), sha).length > 0);
});

test("documentation check: the repository passes and a tree missing required files fails", () => {
  const repoRoot = path.resolve(__dirname, "../../..");
  assert.deepStrictEqual(docs.check(repoRoot), []);
  const empty = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-docs-"));
  assert.ok(docs.check(empty).some(x => /missing required file/.test(x)));
});
