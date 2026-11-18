# HASM restart instruction

Automatic restart/continuation after a usage reset is **NOT CONFIRMED**. No background scheduler, retry loop or billing change was configured. Repository security-policy status is recorded separately below; do not assume a paused client will restart itself.

Installed capability check on2026-11-18: npm-installed **codex-cli0.169.0** supports `resume`, `exec resume` and immediate `queue --resume`; Goals is enabled. Its help exposes no usage-reset scheduling option. Native Goals supports continuation between active turns, but the inspected CLI/help and [official documentation](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex) do not establish automatic restart after a quota reset. This is an uncertainty, not a claim that all clients lack the feature.

The npm shim exists but `codex` is not on this shell's PATH. Verified manual entry point (opens the session picker; choose the existing HASM session):

```powershell
& "$env:APPDATA\npm\codex.cmd" resume -C "C:\Projects\Hasm-Codex"
```

An exact saved session ID can be supplied after the options. Avoid blindly using `--last`: this workspace has parallel HASM sessions. CLI history visibility for the current app conversation has not been confirmed; if it is not listed, use the fallback instruction below. No second development agent was launched by this check.

When usage is available, resume this existing session if the installed client offers Resume. Otherwise open HASM and use this instruction:

> Continue HASM from docs/AUTONOMOUS-PROGRESS.md and docs/AUTONOMOUS-STATE.json. Reconcile GitHub and every worktree before changing anything. PR50 merged asaba3e916524a22a0d422a2f44a54557ad5f584db after the owner's explicit conditional D046 approval and all prerequisite/exact-head gates. PR49 merged asfe4432113c1c13a62c70ee98c218586d7b424b32. Their merge trees/ancestry and all main post-merge hosted workflows passed; merged remote branches were automatically removed, local evidence/worktrees preserved. Do not request the same security approval again: only the exact six reviewed development records for GHSA-fc5q-39xc-vjpf are accepted, hard expiry2026-11-24T00:00:00Z without renewal; every other High/Critical and production/desktop/NuGet remains blocking. PR48 remains Draft on feature/phase-3-8d-impact-preview; latest fully verified implementation is6dd5f2f18e728043b09af1f116bf0539a955f48c, with a later documentation checkpoint whose exact head must be read from Git/PR. Focused replay42/full unit942/PG snapshot17 and CI PostgreSQL1035/979registry passed, zero skips; all seven applicable exact-head gates passed. Preserve newer work and do not repeat completed tests without a relevant change or required new-head gate. Continue the reviewed internal adapter in PHASE-3.8D-TRANSFER-SNAPSHOT.md: full authorized closure, malformed/foreign-layer endpoint quarantine, actual/simulated layer reconciliation, bounded discovery, explicit multiline refusal until its child-entry model exists. No adapter or public/approval/execution/conversion activation exists yet. Keep all D044/D045 accounting/immutability/Final Closed/security safeguards; never merge incomplete scope or failed gates. Record new checkpoints through the established PR workflow. Do not repeat rejected dependency investigations.

Use local git rev-parse HEAD and the current PR body to obtain the exact latest checkpoint commit; a file cannot contain its own eventual commit SHA. Open PRs and branch heads above are observations, never permission to overwrite a newer remote or another worktree.

The owner resumed the existing native goal and get_goal now reports `active` (2026-11-18). Continue the same objective across active turns; no duplicate goal/session or background restart was created. This observation does not establish automatic restart after a quota reset. Reconcile the latest checkpoint/PR before using the historical fallback head above.
