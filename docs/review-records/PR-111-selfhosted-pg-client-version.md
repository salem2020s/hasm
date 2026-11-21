# Independent review record

* **Pull request:** #111 (self-hosted PostgreSQL client/server major-version assertion, owner decision D-084)
* **Head SHA reviewed (full 40 characters):** 11c2f8f6891f032c6bce185b5fe47e8fee9f5587
* **Reviewer context:** independent reviewer subagent, a separate agent from the author (the lead session); model family Claude Opus; read-only. Prompt: lead's independent full-diff review of PR #111 (D-084 add-only scope, Windows PowerShell 5 correctness, secret handling, parity with the hosted backend.yml step, approval label).
* **Scope read:** `git diff origin/main...origin/ci/selfhosted-pg-client-version` (backend-selfhosted.yml +21, docs/CI-RUNNER.md one sentence), the whole backend-selfhosted.yml at head, the hosted step in backend.yml, the tool resolution in PgBackupTool.cs.
* **Checks run (commands and results):** git fetch and the full diff; REST read of the PR labels (`ci-change-approved` present), mergeability and head check runs. No PowerShell executed (no Windows PowerShell 5 in the review environment).
* **Findings:** no Critical, High or Medium. One step added, nothing removed or weakened; fails closed on a missing variable, psql failure, empty output, missing tool or version mismatch; PGPASSWORD cleared in `finally`; no secret printed (also masked by the earlier step).

| ID | Severity | Problem | Resolution |
|----|----------|---------|------------|
| L1 | Low | The step runs only in self-hosted mode (`HASM_RUNNER_MODE=self-hosted`); hosted runs skip it, so it is unexecuted until the next self-hosted run | Accepted: exact-head CI proves the YAML and the hosted gates only; the next self-hosted run must be checked for the "pg_dump … major 18, server major 18" line (recorded in the PR description and INTEGRATION-STATUS) |
| L2 | Low | Windows PowerShell 5.1 with `$ErrorActionPreference='Stop'` and `2>&1` turns any psql stderr into a terminating error | Accepted: it fails closed and `SHOW` with `-At` writes no stderr; integer parsing hardening is optional and was not applied (D-084 limits the change to adding the check as reviewed) |
| L3 | Low | CreateProcess search order (used by the tests) versus `Get-Command` PATH order could in theory resolve different binaries | Accepted (informational): unlikely on the runner, and PgBackupTool checks the client major itself before running |

Out of scope (not changed; D-084 authorizes adding the check only): the existing step title "Verify all 901 PostgreSQL tests" in backend-selfhosted.yml is stale (the registry is read from backend.yml); correcting it would be a separate protected-file change.

Record line: Reviewer: independent reviewer subagent (independent, separate agent); model family Claude Opus; prompt: lead's independent full-diff review of PR #111 (D-084 self-hosted PostgreSQL client/server major-version assertion: add-only scope, PowerShell 5 correctness, secret handling, parity with backend.yml, approval label); Head SHA reviewed 11c2f8f6891f032c6bce185b5fe47e8fee9f5587; executed: git fetch, git diff origin/main...origin/ci/selfhosted-pg-client-version, full read of backend-selfhosted.yml at head, comparison with the backend.yml hosted step, reading of PgBackupTool tool resolution, REST read of PR #111 labels and mergeability and of the head check runs (no PowerShell executed: no Windows PowerShell 5 here); verdict approve; findings L1, L2, L3 (Low); no Critical, High or Medium.

* **Blocker or High findings open:** none
* **NOT VERIFIED:** the new step has not executed on a self-hosted runner (L1); it must be confirmed on the next self-hosted run.
* **Verdict:** approve
