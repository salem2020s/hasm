# HASM - Integration status and handoff

Maintained by the lead agent; the authoritative record of what is open, how it depends on what, and what is waiting for the owner. Recover from this file after a context reset. Last verified against GitHub: **2026-11-20** (`main` = `fe44321`). Nothing below is merged; `main` only changes with the owner's explicit approval.

## Open pull requests (exact heads and hosted CI on those heads)

| PR | Branch | Head | Content | Based on | Required checks on the exact head | Review |
|----|--------|------|---------|----------|-----------------------------------|--------|
| #51 | `feature/security-picomatch-retirement` | `fe2ee06` | INERT proposal: retire the picomatch exception (strict audit, zero exceptions) | main | `dependency-scan` fails (the gate this proposal fixes) | independent review recorded in the PR |
| #55 | `docs/security-consolidated-proposal` | `23d4850` | INERT proposals: lift the frozen source-tree pins only under the retired status + hardening addendum + combined review | main | `dependency-scan` fails | combined independent review recorded (`docs/security/COMBINED-REVIEW.md`) |
| #52 | `test/costingmath-negative-guards` | `a0c0618` | tests only (costing guards) | main | all pass except `dependency-scan` | not required (tests only) |
| #53 | `fix/phase-3-9a-followups` | `bdea3cb` | 3.9A follow-ups (shared revision, dirty state, pagination) | main | backend, PostgreSQL, frontend, desktop, e2e, ci-guard, docs, secret scan pass; 3 security gates fail | recorded in the PR |
| #54 | `feature/phase-3-10-tax-engine` | `a805caf` | 3.10 tax engine, persistence, API, shared UI, localization point, class tabs | main | all pass except the 3 security gates (PostgreSQL 9m20s) | `docs/review-records/PR-54-tax-slice2.md`, `PR-54-tax-slice3.md` (no Critical/High; Mediums fixed) |
| #56 | `feature/phase-3-9b-customers-suppliers` | `6aee2f7` | 3.9B customers, suppliers, representatives | #53 + #54 (stacked) | all pass except the 3 security gates | `docs/review-records/PR-3-9b-customers-suppliers.md` |
| #57 | `feature/phase-3-12-attachments` | `ecc5c03` | 3.12 attachments and key management | #56 (stacked) | all complete checks pass except the 3 security gates; PostgreSQL pending at the time of writing | `docs/review-records/PR-3-12-attachments.md` |
| #58 | `feature/phase-3-11-pricing-engine` | `9fe12af` | 3.11 slice 1: pure pricing engine and snapshot | main | previous head `3617721`: all pass except `dependency-scan`; CI on `9fe12af` running | `docs/review-records/PR-3-11-pricing-slice1.md` (no Critical/High; 5 Medium fixed or decided) |
| #61 | `security/picomatch-retirement-apply` | `326d1cf` | PROTECTED: the three owner-approved patches applied (approval A, 2026-11-20), D-047, independent review record | main | `dependency-scan`, `picomatch-policy-validation`, secret scan, docs, guard self-test pass (real Node 24/Linux); `ci-guard` FAILS only for the missing owner flag (see below); Windows `picomatch-artifact-inspection` pending at the time of writing | `docs/review-records/PR-security-picomatch-apply.md` (APPROVE; no Critical/High) |
| #59 | `docs/integration-status` | see PR | this handoff (docs only) | main | docs-only checks | n/a |
| #60 | `docs/claude-md-autonomy` | see PR | CLAUDE.md autonomy and integration principles (PROTECTED file: owner merges) | main | docs-only checks | n/a |
| #48 | `feature/phase-3-8d-impact-preview` | `1e8f477` | 3.8D internal replay checkpoint (no financial execution) | main | all pass except `dependency-scan` | decisions X1-X8 pending; execution stays disabled |

The three security gates are `dependency-scan`, `picomatch-policy-validation` and `picomatch-artifact-inspection`. They fail on EVERY branch for the same two protected-policy reasons (below); no other required check fails anywhere.

## Dependency graph and migrations

```
main (fe44321: company 0021, system 0023)
 |- #53 (no migration) --+
 |- #54 tax (company 0022, system 0024) --+--> #56 3.9B (0023, 0025) --> #57 3.12 (0024, 0026)
 |- #58 pricing slice 1 (none)            #56 already contains #53 and #54 (merge commit c7f810e)
 |- #48 3.8D (none yet)
 |- #52 tests, #51/#55 inert security
```

Migration versions MUST be contiguous (the loader refuses gaps), so the numbers follow the merge order, not the PR number: **company 0022 -> 0023 -> 0024, system 0024 -> 0025 -> 0026**. #48 holds no migration yet; when it adds one it takes the next free numbers (company 0025, system 0027) and its pinned counts move with it. #58's persistence slice also takes the next free numbers after the chain. Names are generated (`tools/schema/company-00NN-*.cjs`, checked by CI) and never reused.

Known textual conflicts when branches meet (all expected and mechanical): `.github/workflows/backend.yml` (the PostgreSQL registry count and class list: sum the added counts) and `docs/AUTONOMOUS-PROGRESS.md` (log entries: keep both). `git merge-tree` shows no other conflict between any pair of open branches (checked 2026-11-20).

## Security PR #61: the one remaining gate

`ci-guard` fails on #61 with exactly one reason: "protected verification files changed without the owner approval flag (CI_CHANGE_APPROVED)". The flag is the repository label `ci-change-approved`, which the guard reads from the PR. The owner granted approval A in conversation; recording it as that label is an action the agent was **not permitted to take** (the auto-mode classifier denied it as undermining oversight) and does not retry or work around. **Owner action: add the label `ci-change-approved` to PR #61** (only after reading the exact diff, which is the three approved patches). Then re-run the failed `ci-guard` job; all other required checks on #61 already pass or are running.

`integration/chain-with-security` is a validation branch (the feature chain #53-#57 head plus the #61 branch merged) used only to prove that the chain passes the retired gates on hosted CI; it has no PR and is not a merge candidate.

## Merge sequence (each step needs the owner's explicit approval and exact-head CI)

0. **Security (approval A):** apply PR #51's patch, then PR #55's patch (see the plan below). Until then every branch has a failing required security gate and none is merge-eligible; do not bypass.
1. #53 (merge `main` into it, re-run checks, merge with the head SHA pinned).
2. #54 (merge `main`: registry conflict with #53; re-run all checks; its migrations 0022/0024 stay).
3. #56 (merge `main`; its stacked commits for #53/#54 are already in main, so the diff is its own four commits).
4. #57 (same).
5. #58 slice 1 any time after step 0 (independent), then its persistence/API/UI slices after step 3.
6. #52 any time after step 0 (tests only).
7. #48 after decisions X1-X8 are made (it stays a checkpoint with execution disabled; it must be re-based after step 4 and its migrations numbered then).

After each merge: verify the merge commit on `main`, update this file and `docs/AUTONOMOUS-PROGRESS.md`, and re-validate every dependent branch (merge `main` into it, PostgreSQL registry and pinned counts, exact-head checks).

## Security resolution plan (one owner decision: "approval A")

* **Issue A - withdrawn advisory.** GitHub withdrew GHSA-fc5q-39xc-vjpf on 2026-11-18. Verified again 2026-11-20: `npm audit` on the frontend lockfile reports 0 vulnerabilities (the lockfile still installs picomatch 4.0.7). The six-record exception therefore fails closed ("Known affected matcher unexpectedly missing"). PR #51 retires it: status `RETIRED-ADVISORY-WITHDRAWN`, **zero exceptions, every High/Critical blocks**, all schema/count/exit validation retained, all mandatory jobs and raw reports retained.
* **Issue B - frozen trees.** `inspect-production-artifacts.cjs` pins `git rev-parse HEAD:<tree>` of `src/frontend`, `src/desktop` and `assets/branding` to the exception-era hashes, so ANY change of those paths fails. PR #55's second patch skips the pin check only when the status is exactly the retired value (any other value, spelling, null or a missing map keeps the pins, fail closed).
* **What stays enforced after both patches** (verified in the code): the same-commit relationship (the capture step records `GITHUB_SHA` in the manifest, the installer step asserts it equals `GITHUB_SHA` and that the rebuilt Tauri web assets are byte-identical to the inspected ones); the vulnerable-module blacklist on the real production module graph (`picomatch`, `fdir`, `tinyglobby`, `vite`, `vitest`, `@vitejs/plugin-react` must not be bundled); the SHA-256 inventory of every written asset; the NSIS payload byte comparison; full frontend, production, desktop and NuGet audits; secret scan; ci-guard and exact-head verification (`verify-head`).
* **Verified by the lead agent on 2026-11-20:** both patches apply cleanly to `main` in order (scratch worktree, nothing pushed); the policy and inspector fixtures pass **28/28**, zero skips (Node 26, Windows). **Not yet verifiable locally:** the actual Node 24/Linux audit CLI and the hosted Web/Tauri inspections (the audit runner supports hosted Linux only); these are the first steps after approval.
* **Independent review of the combined effect (done 2026-11-20, `docs/security/COMBINED-REVIEW.md` on PR #55):** no Critical/High; two Medium (retirement status not covered by `scopeHash`; config-level changes can evade the six-name blacklist once the pins are lifted) and four Low. The first Medium, the stale text and the missing tests are fixed by a third inert patch, `RETIREMENT-HARDENING-ADDENDUM.patch` (hash-bound retirement record asserted by the audit CLI and the inspector, six more tests, corrected comments). Recorded residuals for the owner: no Rust dependency audit exists (propose a mandatory `cargo audit` job), and the audit and inspection artifacts are bound to one commit only through `verify-head` (optional stronger binding). **All three patches apply cleanly in order to `main`; `node --test tools/verify/` passes 68/68, zero skips.**
* **Approval A therefore covers three patches in order:** PICOMATCH-RETIREMENT (#51), ARTIFACT-PIN-RETIREMENT (#55), RETIREMENT-HARDENING-ADDENDUM (#55).
* **Application procedure after approval A** (separate protected PR, never self-merged): apply patch 1 then patch 2 as two reviewed commits; run the fixtures; push; verify hosted Node 24/Linux validation and the Windows artifact inspection on the exact head; independent full-diff review; verify with `verify-head`; merge only with explicit owner approval and the head SHA pinned; then re-validate every dependent branch.

## Consolidated owner-decision register (each recorded once)

| ID | Decision | Effect while open (fail-closed default) |
|----|----------|------------------------------------------|
| A | Approve applying the three picomatch/pin patches (#51, #55 x2) | all branches keep a failing security gate; frontend/desktop work cannot reach `main` |
| M | Explicit approval to merge each PR into `main` (sequence above) | nothing merges |
| X1-X8 | Phase 3.8D decisions (X1 would relax a database safeguard) | #48 stays an internal replay checkpoint; no financial execution; no safeguard changed |
| OD-11 | Phase 3.8C lots/serials/expiry/reservations decision | 3.8C not started |
| R | Retention/purge of removed attachments | default in force: bytes of removed attachments are kept; nothing purged |
| Q | Per-company attachment quota and malware scanning | not built; size limit and content checks only |
| S | Bundler/desktop config hardening of the artifact inspector (aliases outside node_modules, externals/workers, platform Tauri configs and TAURI_CONFIG, build hooks; see `PR-security-picomatch-apply.md` M1-M5) | recorded residual risk; none of these controls is claimed to exist |
| C | Add a mandatory Rust (cargo) dependency audit job | no Rust audit exists (pre-existing gap); not added without approval of the gate change |

## Next development tasks (no approval needed)

1. 3.11 persistence, API and shared UI once #56 is merged (pricing lists, entries, discount rules, policy; permissions; override permission) - meanwhile independent review of slice 1 (#58).
2. 3.9C party import adapter (needs #56 merged for the roles to import; the framework from 3.2D exists).
3. Backup/restore coordination for attachment folders and the key directory (SRS 14.4, 16): runbook and a tested procedure on top of #57.
4. After the document framework prerequisites merge: 3.13 commercial document framework (needs 3.7C, 3.9B, 3.10, 3.11).
