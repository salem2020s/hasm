# Shared picomatch security blocker

Status: **UNRESOLVED — blocks release and merging any PR whose required security scan fails.** Investigation date: 2026-11-17. No dependency, lockfile, severity threshold, scan or application rule was changed.

## Verified scope

Main814bd7c, PR48 head3425594 and PR49 headdfe86dd share the same frontend lockfile blob `b4d1cdb225ac1d7379d9b867c3f438e0dfa0608b`. The single installed matcher is **picomatch4.0.7**, reached through:

- Vite8.3.6 → picomatch4.0.7.
- Vitest5.1.0 → picomatch4.0.7.
- Vite/Vitest → tinyglobby0.2.17 → picomatch4.0.7 and fdir6.5.0 → optional picomatch peer.
- @vitejs/plugin-react6.2.1 requires Vite8, contributing a transitive audit finding.

The frontend audit reports6 High findings (the vulnerable matcher and dependent packages). The separate desktop lockfile has no picomatch; its audit reports zero vulnerabilities. Workspace manifests and both lockfiles were inspected. No commercial/accounting/backend dependency or database was modified.

## Final official check — do not repeat without new evidence

The official [GitHub advisory](https://github.com/advisories/GHSA-fc5q-39xc-vjpf), updated2026-11-17T15:56:44Z, reports High severity, affected range<=4.0.7 and **no patched version**. The final npm release query still returns4.0.7. Vite8.3.8 still depends on picomatch^4.0.7; latest Vitest5.1.0 does too. Upgrading Vite alone does not remove the blocker. [Upstream release request](https://github.com/micromatch/picomatch/issues/239) remains the reference for a legitimate release signal.

## Rejected approaches — do not retry

| Approach | Evidence and decision |
|---|---|
| @ryotsu/picomatch4.0.7-a | Published tarball/source diff and independent review examined. Fixes the advertised single-branch probes, but three related repeated-extglob alternatives still time out in isolated child processes. Independent review rates the remaining bypass High. Also changes bracket-negation, globstar and capture behavior. Publisher provenance does not prove safety. **Rejected; never installed into HASM or adopted through an override.** |
| Older Vite6.2.7 / Vitest3.1.1 / plugin-react4.7.0 graph | Scripts-disabled, lockfile-only experiment reports **2 Critical,1 High,1 Moderate** affected packages (including tinypool/Vitest execution vulnerabilities and Vite file-access flaws). The audit suggestion is not a safe fix. **Rejected; no application lockfile changed.** |
| @flupkejs/picomatch1.3.0 | Published source lacks scan(), which tinyglobby calls, and implements only a small incompatible regex-based subset. Not a validated replacement. |
| zeromatch1.0.0 | Its published API documentation says makeRe returns a source string rather than RegExp and omits callback options. Native dependency and API migration require separate validation; not a demonstrated drop-in fix. |
| Local patch, package rename alone, suppression or process-isolation claims | A renamed package or green audit alone does not prove remediation. Post-install patching leaves the known vulnerable inventory/version unresolved; execution limits reduce exposure but do not satisfy the mandatory security gate. No suppression, forced downgrade or waiver is approved. |

Bounded probe evidence: Node26.13.0/npm11.20.0 on Windows; every child had a1.5-second deadline,64MB JS heap limit and credential-free environment. Upstream timed out on5 adversarial probes; the candidate fork timed out on3. Both passed2 ordinary controls. Independent reviewer separately reproduced the fork bypass with1-second limits. No full upstream/consumer regression acceptance was claimed for a rejected candidate. Temporary tarballs, cloned sources, experimental dependencies and probe artifacts are not committed.

## Resume policy

Preserve PR48/49 and continue independently safe Release1 work. Their required security gates remain mandatory; do not merge either while failed. Revisit this investigation only for a new official patched release or a new independently validated replacement/security patch, not by repeating these rejected attempts. Then use one isolated shared fix from current verified main, verify actual code safety and full consumer/frontend/E2E/desktop regressions, perform independent review and exact-head CI, merge normally, and merge updated main into both feature branches without rewriting history. Until then, main is unchanged and there is no shared fix to propagate.
