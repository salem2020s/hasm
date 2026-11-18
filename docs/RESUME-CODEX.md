# HASM restart instruction

Automatic restart/continuation after a usage reset is **NOT CONFIRMED**. No background scheduler, retry loop or billing change was configured. Repository security-policy status is recorded separately below; do not assume a paused client will restart itself.

Installed capability check on2026-11-18: npm-installed **codex-cli0.169.0** supports `resume`, `exec resume` and immediate `queue --resume`; Goals is enabled. Its help exposes no usage-reset scheduling option. Native Goals supports continuation between active turns, but the inspected CLI/help and [official documentation](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex) do not establish automatic restart after a quota reset. This is an uncertainty, not a claim that all clients lack the feature.

The npm shim exists but `codex` is not on this shell's PATH. Verified manual entry point (opens the session picker; choose the existing HASM session):

```powershell
& "$env:APPDATA\npm\codex.cmd" resume -C "C:\Projects\Hasm-Codex"
```

An exact saved session ID can be supplied after the options. Avoid blindly using `--last`: this workspace has parallel HASM sessions. CLI history visibility for the current app conversation has not been confirmed; if it is not listed, use the fallback instruction below. No second development agent was launched by this check.

When usage is available, resume this existing session if the installed client offers Resume. Otherwise open HASM and use this instruction:

> Continue HASM from docs/AUTONOMOUS-PROGRESS.md and docs/AUTONOMOUS-STATE.json; reconcile actual GitHub/all worktrees and preserve newer work. Mainfe443211; PR48 Draft root branch feature/phase-3-8d-impact-preview. Last fully green historicalfb07fdd; cf2254554b06bdab08764016ef7a2f7fa912525f passed PG1126/unit967/API69 but dependency gate FAILED CLOSED: official GHSA-fc5q-39xc-vjpf withdrawn2026-11-18T15:38:56Z, audit0, no patched version claimed. Separate INERT retirement proposal PR51 fe2ee06b1fabd13406428239e5462bb95384d3db (security-picomatch-retirement worktree) awaits NEW explicit owner approval already requested once; no protected gates/waiver/expiry/manifests changed. Do not assume goal continuation is approval or repeat the question. Continue backend work. Shared SQL budget fix now covers authorization/system/scope/resolver/schema/privilege/retry/snapshot application commands and reserves transaction cleanup; typed failures propagate safely, no permission caching, original public callers unchanged. Unit970/PG6 passed; existing broad regression session40955/test-results/read-budget-regression must be polled/read, never duplicated on observation timeout. Registry1076/full1132; latest commit/CI status from Git/PR. Independent full source review confirms original Medium resolved for inspected paths, no CHM, broader evidence pending. Next finish regression/checkpoint/CI then implement complete consistent stored-document revision/validated-line/account-period-basis-entitlement plan and full reversal/negative/conversion/approval/execution/revaluation/shared UI. No production API/DI/caller or conversion activation. Preserve D044/D045, immutable history/journals, Final Closed/audit/SoD/legal and all mandatory review/exact-head gates; no incomplete-phase merge. Check disposable5433 availability BEFORE testing after any reboot; existing test cluster PostgreSQL18 C:/HasmTest/pgdata is authorized, primary5432 untouched. Native goal active; automatic quota-reset restart unconfirmed.

Use local git rev-parse HEAD and the current PR body to obtain the exact latest checkpoint commit; a file cannot contain its own eventual commit SHA. Open PRs and branch heads above are observations, never permission to overwrite a newer remote or another worktree.

The owner resumed the existing native goal and get_goal now reports `active` (2026-11-18). Continue the same objective across active turns; no duplicate goal/session or background restart was created. This observation does not establish automatic restart after a quota reset. Reconcile the latest checkpoint/PR before using the historical fallback head above.
