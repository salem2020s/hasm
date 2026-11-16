# HASM — CI on the self-hosted Windows runner

Purpose: no billed GitHub-hosted minutes for routine work, without weakening any gate. Owner-requested (2026-11-16). This file is the operating and hardening guide.

## What runs where
| Workflow / job | Runner | When |
|---|---|---|
| `Backend` / `backend-build-test`, `postgres-integration` | **vm-test** (`self-hosted, Windows, X64`) | every pull request touching backend/tests/workflow, and every push to main |
| `Frontend` / `frontend-build-test`, `e2e-smoke` | vm-test | frontend/branding/workflow changes, push to main |
| `Desktop` / `desktop-build` (Tauri 2) | vm-test (was `windows-latest`, billed at twice the Linux rate) | desktop/frontend/branding changes, push to main |
| `Verification` / `docs-traceability`, `ci-guard`, `secret-scan`, `dependency-scan`, `guard-selftest` | vm-test | every pull request (no path filter), push to main |
| `Linux verification` / `linux-build-test` | GitHub-hosted `ubuntu-24.04` (**billed**, ~4 min) | after every merge to main, weekly, on demand |
| `Linux verification` / `linux-postgres-integration`, `linux-e2e-smoke` | GitHub-hosted (**billed**, ~20 + 2 min) | weekly (Sunday 03:17 UTC) and on demand only |

The check names that the manifest `.github/required-checks.json` and `tools/verify/verify-head.js` verify did **not** change; the jobs only changed runner. The Linux jobs are deliberately not pull-request checks: SRS 2.4 (PostgreSQL and the backend on Linux) is verified after merges and weekly, and a red Linux run blocks the next phase until fixed. Trigger it by hand before a release: Actions > Linux verification > Run workflow.

Estimated billed minutes after the migration: about 4 per merge plus about 22 per week (about 100 to 150 per month), against more than 1,800 before. Pull requests consume none, as long as the runner is online. **If vm-test is offline, pull-request checks queue and stay pending; nothing falls back to a billed runner and nothing may be merged until the exact head SHA is verified.**

## Cost controls in the workflows
* `concurrency` with `cancel-in-progress` for pull requests only: a newer push cancels the superseded run; pushes to `main` are never cancelled.
* `paths` filters unchanged; documentation-only pull requests run only the always-on verification jobs.
* `timeout-minutes` on every job; npm and dotnet use the runner's persistent caches (setup-node cache).
* Run focused tests locally while developing; the complete regression runs in the pull request before merge.

## PostgreSQL on the runner (no Docker, no service container)
`postgres-integration` creates its OWN disposable cluster with the PostgreSQL 18 binaries installed on the runner: `initdb` into `%RUNNER_TEMP%\hasm-pg-ci`, loopback only, private port **5439**, per-run random passwords and fingerprint (masked in logs), the restricted roles `hasm_test_admin` and `hasm_test_roleadmin`, the identity marker database and the same inventory assertion as before; it is stopped and deleted at the end of the job (`if: always()`). It refuses to start when the port is in use and never uses 5432 (development) or 5433 (standing local test cluster).
The test safety gate (`PostgresClusterIdentity`) accepts this cluster only when ALL hold: the job is a GitHub Actions job on a `self-hosted` runner that exported `HASM_CI_PG_DIR`; the port is exactly 5439; loopback host and the disposable acknowledgement; the marker database carries the per-run fingerprint; and only built-in databases and `hasm_test_` databases exist. `SafetyGateTests` cover every refusal. The expected-count registry is verified by `tools/verify/pg-trx-check.js` (exact count per class, all Passed) from the single registry in `backend.yml`, which `ci-guard` protects against lowering.

## Security model
* The runner is a **repository-level** runner of a **private** repository; forks of a private repository are not enabled. Every self-hosted job additionally carries `if: github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository`, so code from a fork would never run there (a skipped job leaves its required check unsatisfied, which fails closed). **`pull_request_target` is forbidden** (ci-guard refuses it; a test scans all workflows).
* Workflows run with `permissions: contents: read`, `persist-credentials: false` on every checkout, no repository secrets are used by any job, and nothing is deployed from CI.
* Jobs run as the Windows service account of the runner (verified: `NT AUTHORITY\NETWORK SERVICE`, not the developer account, not an administrator). It cannot read `C:\Users\salem`, `C:\HasmDev` or `C:\HasmTest\secrets` (owner-only ACLs, verified with `icacls`).
* One job at a time (a single runner), work folder `C:\ActionsRunner\actions-runner\_work`, temporary clusters under the job's `RUNNER_TEMP`.
* A change to any workflow, `required-checks.json`, `tools/verify/**` or `CLAUDE.md` is a protected change: it needs the `ci-change-approved` label and an explicit owner instruction (CLAUDE.md, Autonomous merge policy).

## Residual risks and manual hardening (owner actions; the agent does not change machine-wide settings)
1. **`C:\Projects` and `C:\HasmTest` (except `secrets`) are writable by `Authenticated Users`**, which includes `NETWORK SERVICE`. A malicious workflow change that is merged, or one run before it is reviewed, could modify or read the working copies and the standing test cluster's files. Recommended: remove `Authenticated Users` from the ACL of `C:\Projects` and `C:\HasmTest` (keep `salem`, Administrators, SYSTEM), e.g. `icacls C:\Projects /inheritance:r /grant:r "vm-test\salem:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "NT AUTHORITY\SYSTEM:(OI)(CI)F"` (run as administrator; review first).
2. The runner shares the VM with development: a heavy job slows interactive work, and a job could in principle start processes as NETWORK SERVICE. Recommended: keep the runner service on its non-interactive account, do not add it to any group, and leave Remote Desktop and file shares closed to it.
3. Pull requests are trusted only because collaborators are trusted. Keep the repository private, keep fork pull requests disabled, and require review of workflow changes (CODEOWNERS covers `.github/`).
4. Jobs that download tools (setup-node, dtolnay/rust-toolchain, npm, NuGet, rustup) trust those sources; versions of the third-party actions are tags, not commit SHAs (same as before the migration). Pinning them to SHAs is a recommended later hardening.
5. Google Chrome (installed machine-wide) is used by the browser smoke tests; the Playwright-downloaded Chromium is not used on the runner.
6. When the runner is offline, merges wait. When more capacity or stronger isolation is needed, use a dedicated VM (or a second runner account) for CI and move the label set accordingly.

## Verified on this migration
See the pull request description for the exact runs (job names, durations, head SHA) on vm-test.
