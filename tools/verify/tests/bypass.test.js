"use strict";
// Regression tests for the bypasses found by the independent review of the verification controls (Phase 4.0).
const test = require("node:test");
const assert = require("node:assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { execFileSync } = require("child_process");
const guard = require("../ci-guard.js");
const head = require("../verify-head.js");
const docs = require("../check-docs.js");

const git = (cwd, ...a) => execFileSync("git", ["-C", cwd, "-c", "user.email=t@t", "-c", "user.name=t", ...a], { encoding: "utf8" });
const write = (root, f, t) => { fs.mkdirSync(path.dirname(path.join(root, f)), { recursive: true }); fs.writeFileSync(path.join(root, f), t); };

const BACKEND = `name: Backend
on:
  pull_request:
    paths: ["src/backend/**", "tests/**"]
jobs:
  backend-build-test:
    runs-on: ubuntu-latest
    steps:
      - run: dotnet test
  postgres-integration:
    runs-on: ubuntu-latest
    steps:
      - run: >
          dotnet test tests/integration
          --no-build
      - run: |
          python3 - <<'PY'
          EXPECTED = {
              "AlphaTests": 10, "BetaTests": 5,
          }
          PY
`;
function repo(files) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-bypass-"));
  git(root, "init", "-q", "-b", "main");
  write(root, ".github/workflows/backend.yml", BACKEND);
  write(root, ".github/workflows/desktop.yml", "name: Desktop\njobs:\n  desktop-build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build\n");
  write(root, ".github/required-checks.json", JSON.stringify({ checks: [{ name: "backend-build-test", paths: ["**"] }, { name: "postgres-integration", paths: ["**"] }, { name: "desktop-build", paths: ["**"] }] }));
  write(root, "tests/AlphaTests.cs", "public class AlphaTests {\n [Fact] public void A() {}\n [Fact] public void B() {}\n}\n");
  write(root, "src/frontend/a.test.ts", "it('one', () => {});\nit('two', () => {});\n");
  write(root, "tools/verify/ci-guard.js", "// base copy\n");
  git(root, "add", "-A"); git(root, "commit", "-q", "-m", "base");
  git(root, "checkout", "-q", "-b", "pr");
  for (const [f, t] of Object.entries(files)) { if (t === null) fs.rmSync(path.join(root, f)); else write(root, f, t); }
  git(root, "add", "-A"); git(root, "commit", "-q", "-m", "change", "--allow-empty");
  return root;
}
const withBackend = t => ({ ".github/workflows/backend.yml": t });

test("registry: a forged trailing comment cannot lower a count", () => {
  const forged = BACKEND.replace('"AlphaTests": 10, "BetaTests": 5,', '"AlphaTests": 1, "BetaTests": 5, # "AlphaTests": 10');
  const f = guard.run(repo(withBackend(forged)), "main", { CI_CHANGE_APPROVED: "1" });
  assert.ok(f.some(x => /lowered from 10 to 1/.test(x)), f.join("\n"));
});
test("registry: duplicate keys and unparseable entries fail closed", () => {
  const dup = BACKEND.replace('"BetaTests": 5,', '"BetaTests": 5, "AlphaTests": 99,');
  assert.ok(guard.run(repo(withBackend(dup)), "main", {}).some(x => /duplicate registry key AlphaTests/.test(x)));
  const bad = BACKEND.replace('"BetaTests": 5,', '"BetaTests": five,');
  assert.ok(guard.run(repo(withBackend(bad)), "main", {}).some(x => /unparseable registry entry/.test(x)));
});
test("weakeners: quoted/boolean variants, comments after a constant condition, ignored failures and folded --filter are caught by occurrence", () => {
  const cases = {
    quotedTrue: BACKEND.replace("    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test\n", '    runs-on: ubuntu-latest\n    continue-on-error: "true"\n    steps:\n      - run: dotnet test\n'),
    expression: BACKEND.replace("    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test\n", "    runs-on: ubuntu-latest\n    continue-on-error: ${{ true }}\n    steps:\n      - run: dotnet test\n"),
    ifComment: BACKEND.replace("      - run: dotnet test\n", "      - if: false # temporarily\n        run: dotnet test\n"),
    exit0: BACKEND.replace("      - run: dotnet test\n", "      - run: dotnet test || exit 0\n"),
    folded: BACKEND.replace("          --no-build\n", "          --no-build\n          --filter Category=Fast\n"),
  };
  for (const [name, text] of Object.entries(cases)) {
    const f = guard.run(repo(withBackend(text)), "main", { CI_CHANGE_APPROVED: "1" });
    assert.ok(f.some(x => /added/.test(x)), `${name}: ${f.join(" | ")}`);
  }
});
test("weakeners: a second occurrence is caught even when the base already contained one", () => {
  const base = BACKEND.replace("      - run: dotnet test\n", "      - run: dotnet test || true\n");
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-bypass2-"));
  git(root, "init", "-q", "-b", "main");
  write(root, ".github/workflows/backend.yml", base); write(root, ".github/required-checks.json", JSON.stringify({ checks: [] })); write(root, "tools/verify/ci-guard.js", "//\n");
  git(root, "add", "-A"); git(root, "commit", "-q", "-m", "base"); git(root, "checkout", "-q", "-b", "pr");
  write(root, ".github/workflows/backend.yml", base.replace("          --no-build\n", "          --no-build || true\n")); git(root, "add", "-A"); git(root, "commit", "-q", "-m", "x");
  assert.ok(guard.run(root, "main", { CI_CHANGE_APPROVED: "1" }).some(x => /ignores failure/.test(x)));
});
test("workflows: a rename away, a narrowed paths filter and a new job-level name are refused", () => {
  const r = repo({ ".github/workflows/desktop.yml": null, ".github/workflows/desktop.yml.off": "name: Desktop\n" });
  assert.ok(guard.run(r, "main", { CI_CHANGE_APPROVED: "1" }).some(x => /desktop.yml: workflow file deleted or renamed away/.test(x)));
  const narrowed = BACKEND.replace('paths: ["src/backend/**", "tests/**"]', 'paths: ["src/backend/**"]');
  assert.ok(guard.run(repo(withBackend(narrowed)), "main", { CI_CHANGE_APPROVED: "1" }).some(x => /paths filter entry tests\/\*\* was removed/.test(x)));
  const named = BACKEND.replace("  backend-build-test:\n    runs-on: ubuntu-latest", "  backend-build-test:\n    name: Quick\n    runs-on: ubuntu-latest");
  assert.ok(guard.run(repo(withBackend(named)), "main", { CI_CHANGE_APPROVED: "1" }).some(x => /job-level name was added/.test(x)));
});
test("test inventory: removing tests is refused without approval, adding tests passes", () => {
  const fewer = guard.run(repo({ "tests/AlphaTests.cs": "public class AlphaTests {\n [Fact] public void A() {}\n}\n" }), "main", {});
  assert.ok(fewer.some(x => /PostgreSQL\/xUnit test attributes decreased from 2 to 1/.test(x)), fewer.join("\n"));
  const fe = guard.run(repo({ "src/frontend/a.test.ts": "it('one', () => {});\n" }), "main", {});
  assert.ok(fe.some(x => /frontend test cases decreased from 2 to 1/.test(x)), fe.join("\n"));
  const deleted = guard.run(repo({ "src/frontend/a.test.ts": null }), "main", {});
  assert.ok(deleted.some(x => /frontend test cases decreased/.test(x)));
  assert.deepStrictEqual(guard.run(repo({ "tests/AlphaTests.cs": "public class AlphaTests {\n [Fact] public void A() {}\n [Fact] public void B() {}\n [Fact] public void C() {}\n}\n" }), "main", {}), []);
  assert.deepStrictEqual(guard.run(repo({ "tests/AlphaTests.cs": "public class AlphaTests {\n [Fact] public void A() {}\n}\n" }), "main", { CI_CHANGE_APPROVED: "1" }), []);
});
test("protected files: the verification workflow, the allow list and CODEOWNERS need the approval flag", () => {
  for (const f of [".github/workflows/verification.yml", ".github/secret-scan-allow.json", ".github/CODEOWNERS"]) {
    const r = repo({ [f]: "changed\n" });
    assert.ok(guard.run(r, "main", {}).some(x => /owner approval flag/.test(x) && x.includes(f)), f);
  }
});
test("ignored and explicit tests count as skips", () => {
  assert.ok(guard.run(repo({ "tests/AlphaTests.cs": "public class AlphaTests {\n [Fact] public void A() {}\n [Fact] public void B() {}\n [Ignore] \n}\n" }), "main", {}).some(x => /skipped, ignored or focused/.test(x)));
  assert.ok(guard.run(repo({ "src/frontend/a.test.ts": "it('one', () => {});\nit('two', () => {});\nit.todo('x');\n" }), "main", {}).some(x => /skipped, ignored or focused/.test(x)));
});

test("head verification: a run created by another app does not satisfy a requirement", () => {
  const manifest = { checks: [{ name: "ci-guard", count: 1, paths: ["**"] }] };
  const forged = { name: "ci-guard", status: "completed", conclusion: "success", headSha: "a".repeat(40), appId: 99999 };
  assert.ok(head.evaluate({ headSha: "a".repeat(40), files: ["x"], manifest, checkRuns: [forged] }).some(x => /expected 1 run/.test(x)));
  assert.deepStrictEqual(head.evaluate({ headSha: "a".repeat(40), files: ["x"], manifest, checkRuns: [{ ...forged, appId: head.GITHUB_ACTIONS_APP_ID }] }), []);
});

test("documentation check: a manifest that drifts from the workflow path filters is refused", () => {
  const repoRoot = path.resolve(__dirname, "../../..");
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "hasm-docs2-"));
  fs.cpSync(repoRoot, tmp, { recursive: true, filter: s => !/node_modules|[\\/]\.git([\\/]|$)|test-results/.test(s) });
  const mf = path.join(tmp, ".github/required-checks.json");
  const m = JSON.parse(fs.readFileSync(mf, "utf8"));
  m.checks.find(c => c.name === "backend-build-test").paths = ["src/backend/**"];
  fs.writeFileSync(mf, JSON.stringify(m));
  assert.ok(docs.check(tmp).some(x => /paths of backend-build-test differ/.test(x)), docs.check(tmp).join("\n"));
});
