# HASM restart instruction

Automatic restart/continuation after a usage reset is **NOT CONFIRMED**. No background scheduler, retry loop or billing change was configured. Repository security-policy status is recorded separately below; do not assume a paused client will restart itself.

Installed capability check on2026-11-18: npm-installed **codex-cli0.169.0** supports `resume`, `exec resume` and immediate `queue --resume`; Goals is enabled. Its help exposes no usage-reset scheduling option. Native Goals supports continuation between active turns, but the inspected CLI/help and [official documentation](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex) do not establish automatic restart after a quota reset. This is an uncertainty, not a claim that all clients lack the feature.

The npm shim exists but `codex` is not on this shell's PATH. Verified manual entry point (opens the session picker; choose the existing HASM session):

```powershell
& "$env:APPDATA\npm\codex.cmd" resume -C "C:\Projects\Hasm-Codex"
```

An exact saved session ID can be supplied after the options. Avoid blindly using `--last`: this workspace has parallel HASM sessions. CLI history visibility for the current app conversation has not been confirmed; if it is not listed, use the fallback instruction below. No second development agent was launched by this check.

When usage is available, resume this existing session if the installed client offers Resume. Otherwise open HASM and use this instruction:

> Continue HASM from docs/AUTONOMOUS-PROGRESS.md and docs/AUTONOMOUS-STATE.json. Reconcile actual GitHub/all worktrees and preserve newer work. Mainfe443211 is verified; PR50 mergedaba3e916 and PR49 mergedfe443211. D046 conditional approval resolved: exact six development records only; hard expiry2026-11-24T00:00:00Z without renewal; every other High/Critical and production/desktop/NuGet remains blocking. PR48 stays Draft on feature/phase-3-8d-impact-preview; latest verified head8062d04421bbb776e79a3cc31dc91d92546ca9c2 passed all7 exact-head gates and PostgreSQL1087/1087, zero skips (registry1031). New multi-root read-set and ordinary comparison slice has finalunit967/967 and consolidatedPG95/95 passed zero failures/skips; new-head CI pending; registry1057/full1113. Read latest checkpoint/PR body for newer committed head and actual CI status. Independent full cumulative design/source reviews noCHM; limited internal checkpoint only. No API/DI/production caller or execution activation. After final tests/CI, integrate the authorized read-set/comparison under one bounded application contract with final live authorization across load plus calculation; then complete reversal/negative/conversion entitlements, protected persistence/approvals/execution, prospective conversion/revaluation and shared UI. Preserve D044/D045, immutable history/journals, Final Closed, audit/SoD/legal rules and every independent-review/exact-head gate. Never merge incomplete phase or repeat rejected dependency attempts. Native goal remains active; automatic quota-reset restart unconfirmed.

Use local git rev-parse HEAD and the current PR body to obtain the exact latest checkpoint commit; a file cannot contain its own eventual commit SHA. Open PRs and branch heads above are observations, never permission to overwrite a newer remote or another worktree.

The owner resumed the existing native goal and get_goal now reports `active` (2026-11-18). Continue the same objective across active turns; no duplicate goal/session or background restart was created. This observation does not establish automatic restart after a quota reset. Reconcile the latest checkpoint/PR before using the historical fallback head above.
