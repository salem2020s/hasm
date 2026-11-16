# HASM — CI runner strategy (hosted by default, self-hosted fallback)

Purpose: use the free GitHub-hosted allowance first and never pay: the repository's Actions budget is $0 with "stop usage when the budget is reached", and nothing here changes billing. When the free allowance is not available, the same checks run on the self-hosted Windows runner `vm-test`. Owner-approved hybrid strategy (2026-11-16). This file is the operating and hardening guide.

## Runner mode (how the switch works)
The repository variable **`HASM_RUNNER_MODE`** selects where the mandatory checks run:

| Value | Where the mandatory checks run |
|---|---|
| unset or `hosted` (default) | GitHub-hosted runners: Linux (`ubuntu-latest` / `ubuntu-24.04`) for build, tests, PostgreSQL service container, Playwright/Chromium and all verification jobs; `windows-latest` only for the Tauri desktop build (billed at twice the Linux rate). |
| `self-hosted` | `vm-test` (`self-hosted, Windows, X64`): the same checks, same check names, with a disposable PostgreSQL 18 cluster created by the job. |

Mechanics: every workflow file `X.yml` holds the hosted jobs (`if: vars.HASM_RUNNER_MODE != 'self-hosted'`); `X-selfhosted.yml` holds jobs with the SAME job ids (= required check names, no job-level `name`) gated by `vars.HASM_RUNNER_MODE == 'self-hosted'` and by the same-repository condition. The job of the other mode is skipped. `tools/verify/verify-head.js` ignores a skipped run only when a run of that name really executed; a check that was skipped everywhere is refused, and a failed, cancelled or pending run still blocks. `.github/required-checks.json`, the PostgreSQL expected-count registry (single source in `backend.yml`) and the CI weakening guard are the same in both modes.

Switching is manual and explicit: `gh variable set HASM_RUNNER_MODE --body self-hosted` (or `hosted`), or Settings > Secrets and variables > Actions > Variables. **There is no automatic switch when the free minutes run out**: GitHub raises no such event and none has been implemented or demonstrated. With the $0 budget and "stop usage" enabled, hosted jobs stop running when the allowance is exhausted (they fail or stay queued); the owner or the agent then sets `self-hosted`, and the checks of the exact head SHA are re-run (push or "Re-run all jobs"). Until a check of the exact head SHA has really run and passed, the PR stays unmerged. If vm-test is offline in `self-hosted` mode, checks stay queued, never silently skipped.

## What runs where in each mode
| Check (job id) | `hosted` mode | `self-hosted` mode |
|---|---|---|
| `backend-build-test`, `postgres-integration` | ubuntu (PostgreSQL 16 service container) | vm-test (own PostgreSQL 18 cluster on port 5439) |
| `frontend-build-test`, `e2e-smoke` | ubuntu; Chromium installed by Playwright | vm-test; installed Google Chrome |
| `desktop-build` (Tauri 2) | `windows-latest` (billed 2x) | vm-test |
| `docs-traceability`, `ci-guard`, `secret-scan`, `dependency-scan`, `guard-selftest` | ubuntu | vm-test |
| `Linux verification` (`linux-*`) | skipped (the hosted jobs already are Linux); on demand only | after merges (build, tests), weekly (PostgreSQL, Chromium), on demand |

Cost: the hosted mode bills roughly 50 to 60 minutes per full pull-request run (about 25 of them for the Windows desktop build, which is path filtered). Keep `cancel-in-progress` (a push cancels the superseded run), avoid needless pushes, and run focused tests locally. In `self-hosted` mode pull requests bill nothing; the Linux verification bills about 4 minutes per merge plus about 22 per week.

The Playwright smoke tests run on hosted Linux by default. On vm-test they still hang in pull-request jobs: that issue is **unresolved** and documented below; the tests are never skipped or weakened in either mode.

## Cost controls in the workflows
* `concurrency` with `cancel-in-progress` for pull requests only: a newer push cancels the superseded run; pushes to `main` are never cancelled.
* `paths` filters unchanged; documentation-only pull requests run only the always-on verification jobs.
* `timeout-minutes` on every job; npm and dotnet use the runner's persistent caches (setup-node cache).
* Run focused tests locally while developing; the complete regression runs in the pull request before merge.

## PostgreSQL on the runner (no Docker, no service container)
`postgres-integration` creates its OWN disposable cluster with the PostgreSQL 18 binaries installed on the runner: `initdb` into `%RUNNER_TEMP%\hasm-pg-ci`, loopback only, private port **5439**, per-run random passwords and fingerprint (masked in logs), the restricted roles `hasm_test_admin` and `hasm_test_roleadmin`, the identity marker database and the same inventory assertion as before; it is stopped and deleted at the end of the job (`if: always()`). It refuses to start when the port is in use and never uses 5432 (development) or 5433 (standing local test cluster).
The test safety gate (`PostgresClusterIdentity`) accepts this cluster only when ALL hold: the job is a GitHub Actions job on a `self-hosted` runner that exported `HASM_CI_PG_DIR`; the port is exactly 5439; loopback host and the disposable acknowledgement; the marker database carries the per-run fingerprint; and only built-in databases and `hasm_test_` databases exist. `SafetyGateTests` cover every refusal. The expected-count registry is verified by `tools/verify/pg-trx-check.js` (exact count per class, all Passed) from the single registry in `backend.yml`, which `ci-guard` protects against lowering.

## Security model (self-hosted mode)
* The runner is a **repository-level** runner of a **private** repository; forks of a private repository are not enabled. Every self-hosted job additionally carries `if: github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository`, so code from a fork would never run there (a skipped job leaves its required check unsatisfied, which fails closed). **`pull_request_target` is forbidden** (ci-guard refuses it; a test scans all workflows).
* Workflows run with `permissions: contents: read`, `persist-credentials: false` on every checkout, no repository secrets are used by any job, and nothing is deployed from CI.
* Jobs run as the Windows service account of the runner (verified: `NT AUTHORITY\NETWORK SERVICE`, not the developer account, not an administrator). It cannot read `C:\Users\salem`, `C:\HasmDev` or `C:\HasmTest\secrets` (owner-only ACLs, verified with `icacls`).
* One job at a time (a single runner), work folder `C:\ActionsRunner\actions-runner\_work`, temporary clusters under the job's `RUNNER_TEMP`.
* A change to `verification.yml`, any `*-selfhosted.yml`, `required-checks.json`, `secret-scan-allow.json`, `CODEOWNERS`, `tools/verify/**` or `CLAUDE.md` is a protected change: it needs the `ci-change-approved` label and an explicit owner instruction (CLAUDE.md, Autonomous merge policy).

## Residual risks and manual hardening (owner actions; the agent does not change machine-wide settings)
1. **`C:\Projects` and `C:\HasmTest` (except `secrets`) are writable by `Authenticated Users`**, which includes `NETWORK SERVICE`. A malicious workflow change that is merged, or one run before it is reviewed, could modify or read the working copies and the standing test cluster's files. Recommended: remove `Authenticated Users` from the ACL of `C:\Projects` and `C:\HasmTest` (keep `salem`, Administrators, SYSTEM), e.g. `icacls C:\Projects /inheritance:r /grant:r "vm-test\salem:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "NT AUTHORITY\SYSTEM:(OI)(CI)F"` (run as administrator; review first).
2. The runner shares the VM with development: a heavy job slows interactive work, and a job could in principle start processes as NETWORK SERVICE. Recommended: keep the runner service on its non-interactive account, do not add it to any group, and leave Remote Desktop and file shares closed to it.
3. Pull requests are trusted only because collaborators are trusted. Keep the repository private, keep fork pull requests disabled, and require review of workflow changes (CODEOWNERS covers `.github/`).
4. Jobs that download tools (setup-node, dtolnay/rust-toolchain, npm, NuGet, rustup) trust those sources; versions of the third-party actions are tags, not commit SHAs (same as before the migration). Pinning them to SHAs is a recommended later hardening.
5. Google Chrome (installed machine-wide) is used by the browser smoke tests; the Playwright-downloaded Chromium is not used on the runner.
6. When the runner is offline, merges wait. When more capacity or stronger isolation is needed, use a dedicated VM (or a second runner account) for CI and move the label set accordingly.

## Open issue: Playwright smoke tests hang on vm-test (NOT fixed, not silently closed)
Status (2026-11-16): **unresolved on the self-hosted runner**. The smoke tests (`src/frontend/e2e`, 3 tests) run on GitHub-hosted Linux (Chromium) in the default `hosted` mode; they are never skipped or weakened in either mode.

Evidence collected:
* In pull-request jobs on vm-test the step "Browser smoke tests" ran for the full 9-minute guard: one `node.exe ...\@playwright\test\cli.js test --reporter=list` process stayed alive, with no child process (no vite, no Chrome) and no output at all, not even `DEBUG=pw:webserver` lines. This happened with `npx playwright test` (attached and detached) and with the CLI started directly with `node`, on heads `ed154e6` and `956189b`. The same command had also hung in the first runs.
* The identical invocation passes in 6.5 s (3 tests passed, installed Google Chrome) in a push-triggered diagnostic job on the same runner and account (`NT AUTHORITY\NETWORK SERVICE`, branch `ci/diag-e2e`, variants: exact step, without DEBUG, `DEBUG=pw:*`, one test), and from a scheduled task under the same account (a scratch copy of the frontend). `npx playwright --version`, `test --list` and the full test also passed there.
* So the cause is not Chrome, the web server, the assertions or a headless problem: the run stalls before the web server starts, and only in the pull-request jobs. Open hypotheses (untested): the pull-request checkout (merge ref) or its environment differs from the push job; a leftover process or file lock from the job that ran just before; real-time antivirus scanning of freshly installed `node_modules`.
* Kept for the next investigation: the hang detector (process tree printout, `taskkill`), the log upload as artifact `playwright-diagnostics`, `trace: retain-on-failure` and screenshots in `playwright.config.ts`.

Next steps (owner-visible, after Release-1 work is not blocked by it): run the self-hosted mode on a pull request with the diagnostic variants (full environment dump, process list before the step, `node --cpu-prof` / `--report-on-signal` on the hung process), compare the push and pull-request environments, and exclude antivirus scanning of the runner work folder.

## Verified on this migration
See the pull request description and `docs/review-records/PR-36-ci-runner.md` for the exact runs (job names, durations, head SHA).
