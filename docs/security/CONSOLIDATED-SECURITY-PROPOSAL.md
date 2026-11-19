# Consolidated security-policy proposal: picomatch retirement and the frozen-tree pins

**Status: PROPOSAL ONLY. Nothing here is applied.** No protected file (`tools/verify/**`, workflows, policy JSON) is changed by this branch; the two patches are inert text. **Owner approval is required** before either is applied (CLAUDE.md: protected verification files and mandatory gates).

## The two failures (observed on PR #53, a frontend-only change)

1. **`dependency-scan`, `picomatch-policy-validation`** - the exact-six-record exception for GHSA-fc5q-39xc-vjpf still expects the advisory, but GitHub withdrew it (2026-11-18) and the full audit now reports zero findings, so the old gate correctly fails closed ("Known affected matcher unexpectedly missing").
2. **`picomatch-artifact-inspection`** - `inspect-production-artifacts.cjs` asserts that `git rev-parse HEAD:<tree>` equals the pinned tree hashes in `picomatch-exception.json` for `src/frontend`, `src/desktop` and `assets/branding` ("Frozen source tree changed"). The pins were an exception-era control: the artifact must come from exactly the tree the waiver was audited against. **Any** change to those paths therefore fails, however harmless; PR #53, PR #54's frontend slices, 3.9B and every later UI phase would be blocked.

## What already exists

PR #51 (inert, untouched by this branch) fixes failure 1: retired mode permits zero exceptions and blocks every High/Critical, keeps all validation, mandatory jobs and raw reports. **PR #51 deliberately leaves the artifact pins unchanged**, so even after PR #51 is applied, failure 2 remains and blocks all frontend work. That gap is what this proposal closes.

## The consolidated change (two patches, applied in this order, no other file touched)

1. `docs/security/PICOMATCH-RETIREMENT.patch` (from PR #51, five files, exactly as reviewed there).
2. `docs/security/ARTIFACT-PIN-RETIREMENT.patch` (this branch, two files: `tools/verify/inspect-production-artifacts.cjs` and its test): the tree-pin equality check is skipped **only** when the policy status is exactly `RETIRED-ADVISORY-WITHDRAWN`. Any other status, a missing status, a different spelling or an absent `trees` map keeps the exact pins (fail closed). The policy file, its `scopeHash` integrity check (which still covers the historical `trees`) and the approved-scope constant are untouched.

Why this is sufficient and safe: once the exception is retired the pins protect nothing the strict audit does not already decide for the tree actually built (zero exceptions; any High/Critical blocks). What stays fully enforced and unchanged: Node 24 requirement; the vulnerable-module blacklist on the real production module graph (`picomatch`, `fdir`, `tinyglobby`, `vite`, `vitest`, `@vitejs/plugin-react` must not enter the bundle); inventory of every written asset with SHA-256 and equality of written assets with the inspected bundle; Tauri embedding the same `dist`; the Windows installer inspection (no Node/npm/dev-dependency payload, x64 PE, byte-exact NSIS payload prediction, compiled-binary hash chain); the production `npm audit --omit=dev`, full frontend/desktop/NuGet audits, secret scan, CI guard and exact-head verification. No job is removed, skipped or made optional.

## Evidence (all local, scratch copy outside the repository, Node 26)

* PR #51's patch applies cleanly to `main`; the second patch applies cleanly on top of it.
* Policy and inspector fixtures: 28/28 pass with both patches (22 policy cases including the retired-mode ones, 4 existing inspector cases and 2 new cases: one proves the pins are lifted only by the exact retirement status and enforced for `APPROVED`, `CONDITIONALLY-APPROVED-INACTIVE`, a lower-case spelling, `RETIRED`, empty, undefined and null; a mismatch of each of the three trees is refused in every enforcing state).
* **UNVERIFIED and mandatory after approval:** the actual Node 24/Linux CLI run, the hosted Web/Tauri inspections on the patched head, and the independent full-diff review of the applied change.

## Residual risk (from the independent review of this proposal; none Critical/High)

* **The retired status is guarded by review, not by a hash.** `scopeHash` deliberately excludes `status`. Editing the status to the exact retired value lifts the pin check, but it also turns the waiver off (zero exceptions, every High/Critical blocks), so it cannot create an exception; any other value restores the pins. The only controls on that edit are review of `tools/verify/**` (ci-guard flags it as protected, CODEOWNERS once a ruleset enforces it, and the rule that such a PR is never self-merged). This is stated here so the owner approves it knowingly.
* **Audit-to-artifact binding becomes implicit.** The inspection job and the strict audit are separate jobs; after the pin lift the binding between them rests on the existing exact-head verification (every required check green on the SAME commit, `verify-head`), not on a cross-check inside the inspection job. Both jobs must therefore stay mandatory (they do; nothing here changes that). A stronger variant (record the three tree hashes and HEAD in the web manifest and have the installer step assert them, and have the audit job write the HEAD it audited) is possible but needs a cross-job artifact and a larger protected change; it is offered as an optional follow-up, not required for this approval.
* Fixed after the review: an explicit "pins are missing" refusal instead of an accidental TypeError, a test that main() still delegates to the helper (no accidental unconditional loop or dropped call), and a code comment that pointed at a document absent from main.

## What I am asking the owner to approve (one decision)

Apply both patches (PR #51 first, then the second as a separate reviewed commit on the same protected PR workflow), merging only after an independent review and every exact-head check passes. It does not approve any new exception, expiry renewal, skipped scan or weakened mandatory job.

## If approval is not given

The security gate stays failing and is not bypassed. Frontend/desktop/branding changes (PR #53 and later UI phases) cannot reach `main` with green required checks; backend-only work is unaffected. Interim alternatives are rejected: pin-hash updates per PR (repeated manual policy edits), disabling the job, or broadening the exception.
