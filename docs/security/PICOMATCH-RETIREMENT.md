# Picomatch withdrawal: inert strict-audit retirement proposal

**Not activated. Owner approval required before applying the protected five-file patch.** This branch contains documentation/patch evidence only; active workflows, policy implementation, manifests, lockfiles and original expiry remain unchanged.

## Verified external change

GitHub officially withdrew [GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) at2026-11-18T15:38:56Z (updated15:38:58Z). The official API summary is now “Withdrawn Advisory: picomatch vulnerable to ReDoS through single-branch repeated extglobs”. npm's latest picomatch remains4.0.7; no patched release or replacement is claimed.

On PR48 headcf2254554b06bdab08764016ef7a2f7fa912525f, Actions run44373931915 produced a complete full audit with zero findings. The existing exact exception correctly FAILED CLOSED: “Known affected matcher unexpectedly missing from audit findings”. This is a changed official advisory state, not a new passing CI result or a reason to broaden the waiver. PR48 remains Draft; its implementation/worktree is untouched by this proposal.

## Exact proposed change

After explicit owner approval, apply [PICOMATCH-RETIREMENT.patch](PICOMATCH-RETIREMENT.patch). It modifies only:

- policy JSON status to RETIRED-ADVISORY-WITHDRAWN, preserving the historical approval/six records/original November24 expiry;
- the audit evaluator's retired mode, which permits ZERO exceptions and blocks every High/Critical finding after existing schema/count/exit validation;
- four regression cases for retired clean/High/Critical/error/lower-severity behavior, retaining all historical tests;
- the additional Node24/Linux validator to verify the actual retired CLI succeeds with zero exceptions, retaining both mandatory proof/artifact jobs;
- the mandatory full-audit step's description to identify strict retirement mode.

No required job is removed, skipped or made optional. Production `npm audit --omit=dev --audit-level=high`, full frontend audit, desktop audit, NuGet audit, secret scan, CI guard, artifact inspection and exact-head verification remain mandatory. Raw audit/advisory/policy reports continue uploading. Every future High/Critical finding, including the former picomatch advisory if reintroduced, blocks; there is no replacement whitelist, expiry renewal or broad development-dependency exemption.

The patch is INERT until approved/applied. Existing protected source files are byte-for-byte unchanged. `git apply --check` passed; scratch copies under ignored test-results were used to run22/22 policy fixtures, zero skips, on local Node26.13.0. Combined policy/artifact fixtures passed26/26 with zero skips. The proposed Linux validator passed syntax checking. Actual Node24/Linux CLI, hosted Actions and production Web/Tauri inspections for the applied patch remain UNVERIFIED and mandatory before merge. No temporary dependencies/forks/caches are committed.

A separate fresh `npm audit --json --audit-level=high --registry=https://registry.npmjs.org` against the unchanged frontend lockfile also returned exit0/zero findings (153 dependencies), corroborating the official withdrawal and hosted report. This does not prove the proposed retired CLI on Node24/Linux. The existing artifact inspectors' frozen source pins remain unchanged; this minimal proposal does not authorize removing those pins or changing inspected frontend/desktop/branding inputs. Any later replacement of that exception-era constraint needs its own reviewed, current-head artifact-integrity contract.

## Approval and remaining validation

This is a material change from the conditionally approved six-finding exception: its advisory was withdrawn and no findings remain. The owner previously prohibited mandatory-gate changes without explicit final approval; CLAUDE.md also requires an explicit owner instruction for protected verification/tools changes. Approval sought is ONLY to apply this exact retirement patch and complete its protected PR workflow, merging only after independent review and every exact-head check passes. It does not approve any other exception or weaken a scan.

After approval: apply patch; update the decision/review checkpoint with approval; run local fixtures/guards; push; verify actual Node24/Linux validation and retained release artifact inspection; resolve Critical/High findings; verify the exact final head using main's verifier; merge with SHA pin; reconcile PR48 with main and rerun required checks. Until then, the security gate remains failing and is not bypassed. Continue independent backend work, including PR48's separate Medium whole-operation query-budget fix.
