# HASM restart instruction

Automatic restart/continuation after a usage reset is **NOT CONFIRMED**. No background scheduler, retry loop or billing change was configured. Repository security-policy status is recorded separately below; do not assume a paused client will restart itself.

Installed capability check on2026-11-18: npm-installed **codex-cli0.169.0** supports `resume`, `exec resume` and immediate `queue --resume`; Goals is enabled. Its help exposes no usage-reset scheduling option. Native Goals supports continuation between active turns, but the inspected CLI/help and [official documentation](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex) do not establish automatic restart after a quota reset. This is an uncertainty, not a claim that all clients lack the feature.

The npm shim exists but `codex` is not on this shell's PATH. Verified manual entry point (opens the session picker; choose the existing HASM session):

```powershell
& "$env:APPDATA\npm\codex.cmd" resume -C "C:\Projects\Hasm-Codex"
```

An exact saved session ID can be supplied after the options. Avoid blindly using `--last`: this workspace has parallel HASM sessions. CLI history visibility for the current app conversation has not been confirmed; if it is not listed, use the fallback instruction below. No second development agent was launched by this check.

When usage is available, resume this existing session if the installed client offers Resume. Otherwise open HASM and use this instruction:

> Continue HASM from docs/AUTONOMOUS-PROGRESS.md and docs/AUTONOMOUS-STATE.json. Reconcile GitHub and all worktrees; preserve newer work. PR50 mergedaba3e916 and PR49 mergedfe443211; main fe443211 post-merge gates verified. D046 conditional approval is resolved: exact six development records only, hard expiry2026-11-24T00:00:00Z without renewal; other High/Critical and production/desktop/NuGet remain blocking. Never repeat rejected forks/downgrades. PR48 remains Draft, feature/phase-3-8d-impact-preview; latest verified headdec9742990c5a091f1b4ef653dd8c48281872ea5 passed all7 exact-head gates with PostgreSQL1039/1039. New internal CostFlowSnapshotService checkpoint retains every child and reconciles authorized ordinary operation components with actual FIFO provenance. No API/DI/production caller or execution activation. Full unit960 and PG37 passed; expanded51 had50 pass/1 new fixture expectation mismatch; independently reviewed correction passed focused1/1, zero skips. Newer-head full CI status must be read from the latest checkpoint. Latest follow-up adds final success/error and peer-only revocation, cancellation, duplicate-work and malformed-history/classification regressions: focused4/10/6 and consolidated69/69 passed, zero failures/skips. Registry1031, expectedfull1087. Independent full-diff reviewers confirmed the transitive foreign-item layer Medium fixed and no remaining CHM, limited checkpoint only. Complete remaining malformed child/position/foreign-consumption variants and proposal endpoint read-set acceptance before integration; latest review evidence is in PR-48-flow-snapshot.md. Then complete proposal comparison/scheduling, reversal/negative/conversion contracts, guarded persistence/approvals/execution and shared UI. Preserve D044/D045, immutable history/journals and Final Closed protections. No incomplete-phase merge. Do not repeat completed tests without relevant changes or required new-head gates.

Use local git rev-parse HEAD and the current PR body to obtain the exact latest checkpoint commit; a file cannot contain its own eventual commit SHA. Open PRs and branch heads above are observations, never permission to overwrite a newer remote or another worktree.

The owner resumed the existing native goal and get_goal now reports `active` (2026-11-18). Continue the same objective across active turns; no duplicate goal/session or background restart was created. This observation does not establish automatic restart after a quota reset. Reconcile the latest checkpoint/PR before using the historical fallback head above.
