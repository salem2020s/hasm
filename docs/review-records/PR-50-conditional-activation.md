# PR50 conditional security-policy implementation

## Owner decision and limits

On 2026-11-18 salem2020s explicitly approved implementing the previously reviewed six-record development-only exception, conditional on successful Node24/Linux actual GitHub Actions execution, production Web/Tauri artifact inspection, independent review and exact-head mandatory CI. This is the same-conversation authorization required by CLAUDE.md for protected verification changes. Activation is forbidden until those conditions pass. Hard expiry: **2026-11-24T00:00:00Z**, no automatic renewal. See D-046.

The actual High vulnerability is unresolved. No dependency replacement, advisory suppression, threshold reduction, production exemption, self-hosted exemption or release authorization is approved. Original manifests/lockfiles and all accounting/security requirements are preserved.

## Two-stage implementation

Stage1 leaves `verification.yml` unchanged and the live policy `CONDITIONALLY-APPROVED-INACTIVE`. Separate mandatory additional jobs execute the real Node24/Linux CLI (which must still reject the inactive policy), full audit disclosure, unchanged production audit, frontend regressions and actual release NSIS payload inspection. They upload evidence; no installer is executed or deployed.

Stage2 is permitted only after both validation jobs pass on one recorded head and review findings are resolved. The policy must reference that successful workflow run/head. The checker verifies GitHub run/job identity and compares frozen application trees and all relevant validation implementation blobs with the proof commit. Only then may the reviewed hosted-audit integration be applied. Every final-head check must pass again before SHA-pinned merge. The self-hosted scan remains strict and unchanged.

## Independent review

Separate GPT-6/Codex agent `security_report_review`, read-only FULL diff against origin/main `814bd7c43cd7ad4cd78aec0a5c0eca707a26a1eb`, including all untracked implementation/workflow files. Prompt: inspect exact advisory/path/version/dev scope; other High/Critical blocking; hard expiry; activation evidence; artifact inventory/extraction; fail-closed behavior; workflows. No operational vulnerability probes or repeated remediation experiments.

First implementation pass: no Critical/High. Medium: proof run was not bound to the current inspected inputs/implementation. Fixed by comparing proof commit trees/blobs and prior inactive policy; added deterministic regression coverage. Reviewer confirmed the fix in a second FULL-diff pass and independently ran21/21 fixtures, zero skips; no remaining Critical/High/Medium for the inactive checkpoint. Final activation-head review is PENDING. Reviewer independently ran initial20 fixture tests successfully, zero skips. This record is not merge approval.

## Author checks and unresolved verification

21 policy/artifact fixtures pass locally, zero skips. Additional immutable-scope and exact-expiry cases demonstrate every other High/Critical remains blocking; changed versions/paths/dev classification, trees, unknown advisory chains, malformed audit/API data and missing/skipped/substituted proof fail closed. These are benign synthetic fixtures, not proof of Linux/packaging integration.

Initial existing verification self-tests:33 passed/1 failed because new hosted jobs lacked established runner-mode conditions; added the required mode guards without changing the test. Rerun:34/34 passed, zero skips. Documentation/traceability passes. Actual Node24/Linux network/CLI, actual release installer inspection and exact-head CI are still **NOT VERIFIED** at this stage; exception remains inactive, PR48/49 may not merge through failing security gates.

Node24/Linux actual GitHub job passed on head4d9356856e91acb6773821368e65be78ddd268ba in run44264261626, including frontend tests/typecheck/build, production audit, real inactive audit CLI and Web production module/hash inventory. Windows release installer build/inspection remains PENDING; no activation. Independent verdict and exact inactive head are also recorded in PR50's description.
