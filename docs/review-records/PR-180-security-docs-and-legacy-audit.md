# Independent review record

* **Pull request:** #180
* **Head SHA reviewed (full 40 characters):** 15f6dcc564024bb0dc7ea371ebed24ff3065b993
* **Reviewer context:** Agent R (independent docs reviewer, separate agent; not the author, who is Agent F2); model family Claude Opus. Prompt summary: "adversarial independent review of Draft PR #180 (docs only; head 15f6dcc, base 9ffaa6c): `docs/SECURITY-PICOMATCH.md` dated status (exception retired, advisory withdrawn, strict mode) with the investigation kept as history; `docs/SECURITY-DEPENDENCY-STATE.json` auditExceptionActive false with retirement and history; the legacy audit of #48-#85 in INTEGRATION-STATUS. Verify the retirement claims against tools/verify/picomatch-exception.json, dependency-audit-policy.cjs, D-047, #61 (215da56) and the dependency-scan job on main; owner decisions S and C stay OPEN; no protected file is touched; nothing reads the two files (grep the repo); the audit counts (33 merged, #48 active, #51/#55/#85 superseded, #62 inert) by REST; nothing overstates the security posture; severity scale Critical, High, Medium, Low". Own mktemp worktree on the detached head; REST only.
* **Scope read:** the full `git diff origin/main...15f6dcc` (one commit; `docs/SECURITY-PICOMATCH.md`, `docs/SECURITY-DEPENDENCY-STATE.json`, `docs/INTEGRATION-STATUS.md`; merge base = main `9ffaa6c`). Cross-read: `tools/verify/picomatch-exception.json`, `tools/verify/dependency-audit-policy.cjs`, `tools/verify/check-docs.js`, `.github/workflows/verification.yml`, D-046/D-047 in `docs/DECISIONS.md`, `docs/review-records/PR-security-picomatch-apply.md`, `src/frontend/package-lock.json`.
* **Checks run (commands and results):**
  * **Retirement claims:**
    * `picomatch-exception.json` has status `RETIRED-ADVISORY-WITHDRAWN`.
    * Running `node -e` with `assertRetirement(record)` passes, and `retiredHash(record) === RETIRED_SCOPE` is true, so the record is bound to the owner-approved retirement hash.
    * In that state `evaluate` returns `ok: blocking.length === 0` with no exception path ("Permanent strict mode: retirement authorizes ZERO exceptions").
    * D-047 reads "Owner approval A, 2026-11-20: GitHub withdrew GHSA-fc5q-39xc-vjpf on 2026-11-18 ... ZERO exceptions, every High/Critical blocks" and supersedes D-046, as stated.
    * #61 is merged as `215da56` at 2026-11-20T12:38:04Z (REST and git).
    * `verification.yml` has the step "Full npm audit (strict; temporary exception retired)", and REST shows `dependency-scan` completed/success on `9ffaa6c` (job 136980351835).
  * **Owner decisions S and C:** both rows are unchanged and OPEN in the register at the head; the JSON lists both as OPEN.
  * **Protected files:** `git diff --name-only` shows only the three docs files, so no protected file is touched.
  * **Readers of the two files:**
    * `git grep` for `SECURITY-PICOMATCH` and `SECURITY-DEPENDENCY-STATE` outside review records finds only the new INTEGRATION-STATUS sentence.
    * The picomatch tooling (`picomatch-validation.yml`, `validate-picomatch-policy.cjs`, `inspect-production-artifacts.cjs`, `required-checks.json`) does not reference them, and `check-docs.js` does not list them among its required files.
    * `check-docs.js` does read every `docs/*.md` for its generic relative-link check (see L1).
  * **Legacy audit:** REST `pulls/<n>` for every n in 48..85 gives 33 closed and merged, plus 5 open Drafts (#48, #51, #55, #62, #85) and nothing closed unmerged, so 33 of 38 is exact. #89 is merged as `f28c2fa` and #61 as `215da56`.
  * **Posture wording:** nothing says the dependency was fixed or upgraded, and S and C stay OPEN. The lockfile still pins `node_modules/picomatch` 4.0.7 (see L2).
  * **Other checks:** `node tools/verify/check-docs.js` exits 0, `generate.js --check` is OK, the JSON parses, and the non-skipped check-runs on the head (guard-selftest, dependency-scan, secret-scan, docs-traceability, ci-guard) are completed/success. main's `check-review-record.js` on this record: OK.
* **Findings:** Low L1 (INTEGRATION-STATUS, "No tool or workflow reads either file"): strictly, `tools/verify/check-docs.js` reads every `docs/*.md`, including `SECURITY-PICOMATCH.md`, for its relative-link check. No tool reads either file for content, state or policy, which is the substance of the claim. Reword it, for example "no tool or workflow takes policy or state from either file". Low L2 (`SECURITY-PICOMATCH.md` status line and the JSON): the retirement rests on the advisory's withdrawal, not on a change of dependency. picomatch 4.0.7 is still the installed version (`src/frontend/package-lock.json`). One clause such as "the installed picomatch 4.0.7 is unchanged; the strict audit passes because the advisory was withdrawn" would stop a reader taking RETIRED to mean a fixed dependency. No Critical, High or Medium finding.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** GitHub's advisory page itself (the withdrawal date is taken from D-047 and the merged review record, not fetched from github.com/advisories); the dependency-scan logs (only the job conclusion was read); hosted CI beyond the verification check-runs on the head.
* **Verdict:** approve
* **Resolutions (author, in the commit after `44b34cf`; for R's delta confirmation):**
  * **L1 fixed:** `docs/INTEGRATION-STATUS.md` now says "No tool takes policy or state from either file (`check-docs.js` reads the `.md` only for its generic link check)".
  * **L2 fixed:** the dated status line of `docs/SECURITY-PICOMATCH.md` and `docs/SECURITY-DEPENDENCY-STATE.json` (`installedVersion`, `whyAuditPasses`) state plainly that the installed picomatch is unchanged at 4.0.7 (verified in `src/frontend/package-lock.json` on `main`, `node_modules/picomatch`; the desktop lockfile has no picomatch) and that the strict audit passes because the advisory was withdrawn, not because the dependency was upgraded.
