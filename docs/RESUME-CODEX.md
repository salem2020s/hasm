# HASM restart instruction

Automatic restart/continuation after a usage reset is **NOT CONFIRMED**. No scheduler, retry loop, billing change or security-policy activation was configured. Installed CLI capability verification is recorded below; do not assume a paused session will restart itself.

Installed capability check on2026-11-18: npm-installed **codex-cli0.169.0** supports `resume`, `exec resume` and immediate `queue --resume`; Goals is enabled. Its help exposes no usage-reset scheduling option. Native Goals supports continuation between active turns, but the inspected CLI/help and [official documentation](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex) do not establish automatic restart after a quota reset. This is an uncertainty, not a claim that all clients lack the feature.

The npm shim exists but `codex` is not on this shell's PATH. Verified manual entry point (opens the session picker; choose the existing HASM session):

```powershell
& "$env:APPDATA\npm\codex.cmd" resume -C "C:\Projects\Hasm-Codex"
```

An exact saved session ID can be supplied after the options. Avoid blindly using `--last`: this workspace has parallel HASM sessions. CLI history visibility for the current app conversation has not been confirmed; if it is not listed, use the fallback instruction below. No second development agent was launched by this check.

When usage is available, resume this existing session if the installed client offers Resume. Otherwise open HASM and use this instruction:

> Continue HASM from docs/AUTONOMOUS-PROGRESS.md and docs/AUTONOMOUS-STATE.json. Reconcile GitHub and every worktree before changing anything. PR48 is feature/phase-3-8d-impact-preview at34255945dab04d8a093f7b396c39a124a585b463; PR49 is feature/codex-workflow-optimization atdfe86dd2179f3d63be978d17e23554d3cb9e72bc; PR50 is feature/security-picomatch (read its latest pushed head and proposal review). The security-policy proposal is INERT and requires explicit owner approval; do not activate it or assume approval from a resume instruction. Preserve all branches and unfinished work. Do not repeat rejected dependency investigations or completed tests. Continue the next safe Phase3.8D replay/authorized-history slice; merge nothing with failed required checks or incomplete scope. Record new checkpoints through PRs.

Use local git rev-parse HEAD and the current PR body to obtain the exact latest checkpoint commit; a file cannot contain its own eventual commit SHA. Open PRs and branch heads above are observations, never permission to overwrite a newer remote or another worktree.
