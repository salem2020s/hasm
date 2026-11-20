# Independent review - PR #61, applied picomatch retirement (owner approval A, 2026-11-20)

* Reviewed: the full diff `main...security/picomatch-retirement-apply` (head at review time `d159a94`). Reviewer: a separate subagent session (general-purpose, fresh context, read-only; it ran the two policy test files, 34/34; it did not run the live audit or read CI). Verdict: **APPROVE**; no Critical, no High.
* **Scope fidelity (verified):** the three approved patches applied to a temporary index of `main` produce a tree that differs from the PR head only in `docs/DECISIONS.md` (D-047) and `docs/AUTONOMOUS-PROGRESS.md` (log line). Everything under `tools/verify/**`, `.github/**` and the policy JSON is byte-identical to the patch result. The only workflow change is a step display name (`verification.yml:93`). `required-checks.json`, CODEOWNERS, the secret-scan allow-list, `ci-guard`, other workflows, lockfiles, `package.json` and all frontend/desktop sources are untouched.
* **Retired-mode semantics (verified):** audit-error, schema and count/exit-disagreement checks return before the retired branch; the retired branch permits no exception and blocks every High/Critical; the status match is exact (any other spelling falls back to the stricter exception path / keeps the pins; a missing pin map asserts); `assertRetirement` runs in both the audit CLI and the inspector after the scope-hash check. Residual (stated in the code): the hash does not prevent editing the tools themselves; review of `tools/verify/**`, the ci-guard owner flag and the never-self-merge rule are the controls.
* **Mandatory jobs (verified):** none removed, no `continue-on-error`, no weakened `if`; every name in `required-checks.json` matches a real job in the hosted and self-hosted workflows (no silent rename).

## Bundler and desktop configuration inspection (owner-required) - remaining source-to-artifact risks

With the exception-era pins lifted, what the inspection **catches**: a changed Tauri `frontendDist`; forbidden `node_modules` module ids in the main bundle graph; any `dist` file that differs from the written-asset inventory (extra public files, another outDir, plugin writes); a change to `dist` between the capture and installer steps; an installer executable that differs from the compiled executable apart from the NSIS marker.

What it does **not** catch (all Medium; none is new in kind - the pins only made them harder to exploit unnoticed - and none is fixed here because the owner approved exactly three patches):

| # | Gap | Proposed minimal mitigation (needs a separate approval: it changes protected tools) |
|---|-----|--------------------------------------------------------------------------------------|
| M1 | An alias or vendored copy outside `node_modules` passes the module blacklist. | Match forbidden modules by the package name found in `package.json`; reject module ids outside the workspace and `node_modules`. |
| M2 | An externalized or CDN-loaded module is not in the graph. | Inspect the resolved Vite config in a `configResolved` hook and require empty alias/external settings. |
| M3 | Worker bundles have their own rollup graph that is not checked. | Require empty worker settings in the same hook (or inspect worker graphs). |
| M4 | Tauri 2 merges platform config files (`tauri.windows.conf.json`) and `TAURI_CONFIG`; the inspector reads only `tauri.conf.json`; extra bundle resources, `externalBin` and NSIS hooks are not asserted. | Fail if any `src-tauri/tauri.*.conf.json` exists or `TAURI_CONFIG` is set; assert no bundle resources or `externalBin`; allow-list the installer archive entries. |
| M5 | A `beforeBuildCommand` or `build.rs` that substitutes content before embedding and restores `dist` afterwards would pass: nothing proves the executable embeds the inspected `dist`. | Pin or assert `vite.config.ts`, `tauri.conf.json` and `build.rs`; verify the `dist` digests inside the executable; put these files under CODEOWNERS and the owner label. |
| M6 | No job audits the Rust crates; the self-hosted path runs a plain `npm audit` only. | Owner decision C: a mandatory `cargo audit` job. |

Lows: the blacklist is a fixed seven-name list (new dependencies are judged only by known advisories); `picomatch-validation.yml` header lines still say "conditional-policy validation / mandatory until activation" (a comment in a protected file; a job NAME is a required-check name and is not renamed); the D-046 row is superseded by D-047 rather than rewritten; `expiresAt` stays in the retired JSON (inert, bound by the scope hash).

These residuals are recorded in `docs/INTEGRATION-STATUS.md` as decision S (bundler/desktop config hardening) next to decision C; neither is claimed to exist.

## Final approval and verification evidence (2026-11-20)

* Head 326d1cf: label `ci-change-approved` present on PR #61 (owner approval A covers the three patches exactly; not re-requested). The independent review above stands (APPROVE, no Critical/High).
* All hosted jobs for the labelled run were green on 326d1cf; the one red `ci-guard` entry on that SHA is a historical run that kept the pre-label event. This documentation-only commit creates a fresh head so every mandatory check, including ci-guard with the label, is evaluated on a single new SHA. No security patch or protected tool changed.
