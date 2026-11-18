# HASM restart instruction

Automatic restart/continuation after a usage reset is **NOT CONFIRMED**. No background scheduler, retry loop or billing change was configured. Repository security-policy status is recorded separately below; do not assume a paused client will restart itself.

Installed capability check on2026-11-18: npm-installed **codex-cli0.169.0** supports `resume`, `exec resume` and immediate `queue --resume`; Goals is enabled. Its help exposes no usage-reset scheduling option. Native Goals supports continuation between active turns, but the inspected CLI/help and [official documentation](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex) do not establish automatic restart after a quota reset. This is an uncertainty, not a claim that all clients lack the feature.

The npm shim exists but `codex` is not on this shell's PATH. Verified manual entry point (opens the session picker; choose the existing HASM session):

```powershell
& "$env:APPDATA\npm\codex.cmd" resume -C "C:\Projects\Hasm-Codex"
```

An exact saved session ID can be supplied after the options. Avoid blindly using `--last`: this workspace has parallel HASM sessions. CLI history visibility for the current app conversation has not been confirmed; if it is not listed, use the fallback instruction below. No second development agent was launched by this check.

When usage is available, resume this existing session if the installed client offers Resume. Otherwise open HASM and use this instruction:

> Continue HASM from docs/AUTONOMOUS-PROGRESS.md and docs/AUTONOMOUS-STATE.json. Reconcile GitHub/all worktrees and preserve newer work. Mainfe443211 verified; PR50 mergedaba3e916/PR49 mergedfe443211. D046 approval resolved: exact six dev records only, hard expiry2026-11-24T00:00Z without renewal; all other High/Critical and production gates block. PR48 stays Draft on feature/phase-3-8d-impact-preview; latest exact-head verifiedfb07fddbc99bef7418d08901aa2956280656e34d passed7CI/PG1113/unit967/API69. New integrated internal PreviewAsync preserves one deadline/retained scopes across load+comparison and accepts no client snapshots. Unit967 and preview13 passed; consolidated108/newer-head CI status must be read from current checkpoint/PR. Registry1070/full1126. OPEN Medium resource finding:20k counts snapshot commands only; live RBAC scoped-grant probes and resolver/schema checks escape it. Resolve strict shared optional quota BEFORE public integration, preserving live checks, propagating typed budget exhaustion through catch-all handlers, counting cold/retry paths and reserving cleanup. Unknown unbudgeted validators fail closed. See PR-48-integrated-preview.md for exact plan/evidence. Then bind stored document revision/validated line inputs plus account/period/basis/entitlement policy under one consistent source-bound contract; complete approvals/atomic execution/conversion/revaluation/shared UI. No production API/DI/caller, conversion activation or full-phase merge. Preserve D044/D045, immutable history/journals, Final Closed, audit/SoD/legal and every review/CI gate. Disposable5433 was stopped after restart; verified PostgreSQL18 C:/HasmTest/pgdata was started explicitly5433 and original process passed13, no duplicate or primary5432 change. Native goal active; post-quota restart unconfirmed.

Use local git rev-parse HEAD and the current PR body to obtain the exact latest checkpoint commit; a file cannot contain its own eventual commit SHA. Open PRs and branch heads above are observations, never permission to overwrite a newer remote or another worktree.

The owner resumed the existing native goal and get_goal now reports `active` (2026-11-18). Continue the same objective across active turns; no duplicate goal/session or background restart was created. This observation does not establish automatic restart after a quota reset. Reconcile the latest checkpoint/PR before using the historical fallback head above.
