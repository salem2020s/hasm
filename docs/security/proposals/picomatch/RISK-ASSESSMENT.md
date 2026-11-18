# Proposed temporary development-tool exception

**INERT / NOT APPROVED.** No workflow, verifier, manifest or lockfile has changed. This is an owner-review artifact, not a remediation or permission to merge/release. Existing CI remains blocked.

## Exact scope and duration

Propose accepting **only GHSA-fc5q-39xc-vjpf** in the exact development-only frontend graph recorded in `picomatch-exception.json`: picomatch4.0.7, fdir6.5.0, tinyglobby0.2.17, Vite8.3.6, Vitest5.1.0 and @vitejs/plugin-react6.2.1, at their six exact `node_modules` paths. These are six audit package records inherited from one advisory, not six independent advisory exemptions. Any other advisory, Critical severity, version, path or production classification is denied.

Owner: **salem2020s**. Approval fields remain null. Proposed hard expiry: **2026-11-24 00:00 UTC**, never more than seven days after explicit approval. No automatic extension. An announced official patch, changed advisory scope/severity, missing fresh metadata or changed inputs ends eligibility immediately. No new remediation attempts were repeated.

Restrict eligibility to hosted Linux audit jobs in `salem2020s/hasm`, same-repository PR heads or pushes. Forks and self-hosted runners get no exception. Freeze the complete frontend, desktop and branding source trees to their reviewed Git tree hashes and require clean working inputs. Thus a frontend/UI, packaging, lockfile or related source change requires reassessment, even if the dependency versions stay unchanged.

## Evidence and residual risk

- All six locked packages have `dev:true`. A separate production npm audit passed with **zero vulnerabilities**. Classification alone is not proof of artifact absence.
- A fresh local production build inspected95 module IDs: none belong to the six affected tool packages. All six emitted files matched the final written artifact hashes; evidence is in `artifact-evidence.json`. The inspected frontend tree matches main and both PRs.
- Tauri's configuration embeds this same frontend `dist` directory. **A Windows installer was not rebuilt or scanned.** Local inventory used Node26 on Windows, not Node24/Linux CI. Full release artifact verification remains mandatory; this proposal does not approve deployment or release.
- No application import or user-facing glob API was found for these packages. Build/test configuration and dependencies do execute the matcher. PR-controlled code/configuration can supply patterns; **untrusted reachability cannot be ruled out**. This is acceptance of a bounded development/CI denial-of-service risk, not a claim the High vulnerability is fixed or unreachable.
- Existing hosted jobs use read-only repository permissions, ephemeral runners and timeouts; E2E preview binds loopback. No production secrets should be supplied to these jobs. These controls limit impact but do not repair the matcher. Self-hosted execution is excluded because persistence and host credentials change the risk.

## Proposed blocking behavior

| Check | Proposed result |
|---|---|
| Frontend `npm audit --omit=dev --audit-level=high` | Always mandatory; every production High/Critical blocks. |
| Complete frontend npm audit | Still runs and discloses unfiltered JSON. Only the exact advisory/graph above may be accepted under all policy conditions; all other High/Critical findings block. |
| Desktop full audit; NuGet vulnerable-package scan | Remain blocking without exceptions. |
| Invalid schema/counts, command/network error, missing finding, cycle, expired/unapproved policy, changed source graph | Fail closed; no exception. |
| All other tests, independent review, branch protection and exact-head checks | Unchanged and mandatory. |

Full audit/advisory/policy-result artifacts are retained30 days. Eligible runs emit an explicit **UNRESOLVED HIGH** warning with owner/expiry; severity is never relabeled. Production audit failure still fails the job even if the full-audit exception is eligible. Self-hosted verification is unchanged and continues blocking this finding.

## Concrete approval and verification boundary

`verification.patch` is a checked, unapplied patch. The proposed checker, fixture tests and policy would be copied to protected `tools/verify/` paths only after final owner approval. The custom evaluator avoids adding another dependency; an advisory-wide audit-ci allowlist alone would not enforce this exact graph, expiration and context.

Independent GPT-6/Codex review found no Critical/High issue in the inert proposal, but identified one Medium missing-finding check. Set equality and a regression fixture were added; final confirmation is recorded separately. Fixture tests use synthetic approvals, never real authorization. Linux CLI/network integration, final copied paths, actual CI and installer contents remain unverified.

**Owner approval must explicitly authorize:** the temporary acceptance of this one High development advisory and its six inherited records for the specified merge/security gate; the named owner/expiry/frozen inputs; activation of the proposed protected checker/policy/tests and hosted workflow patch, including the required `ci-change-approved` review. This is not blanket approval for other High/Critical findings, a failed required check, incomplete Phase3.8D scope, production deployment, or merging PR48/49 before all applicable gates pass. No activation or merge is authorized by this proposal.
