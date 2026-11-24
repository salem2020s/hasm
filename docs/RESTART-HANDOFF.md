# Restart handoff (lead), 2026-11-23

Status document for resuming autonomous work after a session or model change. It records state only; it changes no approved decision. The owner decisions of 2026-11-23 are recorded once, verbatim, in docs/DECISIONS.md (D-261..D-266) and in the owner-decision register of docs/INTEGRATION-STATUS.md; this document only points to them.

**Model change 2026-11-23: critical security reviews (identity, replay authorization, audit recovery) are deferred and their PRs stay Draft until a suitable independent reviewer is available.**

## State of main (read by REST and git on 2026-11-23, second refresh)
- main = cdd6b1a (cdd6b1a7815bad4ca0ab98279480fb0907740e84, the merge of PR 232), read with `git log origin/main` after the merge gate reported the merge.
- PostgreSQL registry on main: 1815 (title of the step "Verify all 1815 PostgreSQL tests executed and passed" in .github/workflows/backend.yml; the per-class sum was recomputed by the PR 232 reviewer on the merged head's content, NOT recomputed again for this refresh).
- Migrations on main: system 0001-0040, company 0001-0038 (unchanged by PR 240 and PR 232, which add no migration).
- Merged since the first refresh: PR 240 (this handoff and the owner decisions of 2026-11-23, D-261 to D-266; merge commit ab91850) and PR 232 (backup S8d-1, read only; merge commit cdd6b1a; records PR-232-s8d1-review, PR-232-s8d1-delta-confirmation, PR-232-s8d1-fix-confirmation).

## Open pull requests (REST, 2026-11-23, second refresh: all Draft)
| PR | Content | Head | State and next step |
|---|---|---|---|
| 238 | T2 slice A3, commit-time grant checks and the epoch comparison (company 0039, provisional) | 2ee2ae0 | Hosted CI on 2ee2ae0: all seven checks success, including postgres-integration. Main has moved since (registry 1815), so a merge-only round is needed (registry title = per-class sum). CRITICAL SECURITY REVIEW DEFERRED: the independent security reviewer approved the H1 fix on the pre-merge head 58fdc55 in notes only (no record written); a confirmation record bound to the final head by a suitable independent security reviewer is required before the gate. Stays Draft until then. |
| 234 | Audit-head link guard, option (a) (migration numbers provisional) | c773a7c | Conflicts with main. Not reviewed. Needs the merge of main, renumbering to the next free numbers (system next free 0041; company next free 0039 unless PR 238 merges first), and an independent security review (deferred). Stays Draft. |
| 241 | Trial-restore database drop: shared helper, FORCE then plain drop on 42501 (autovacuum worker, non-superuser admin account) | dc00754 | Branch fix/trial-restore-drop-flake. Hosted CI on dc00754 at this read: six checks success, postgres-integration in progress (NOT VERIFIED green). Update read by REST in the resumed session of 2026-11-23: postgres-integration on dc00754 completed with conclusion FAILURE (run 45260494831, job 137993635561, step "Run PostgreSQL integration tests", about 22 minutes); root cause under diagnosis, the PR is NOT green. Based on main ab91850 with registry 1806, so it needs a merge-only round against main (registry 1815 + 1 = 1816, to be recomputed) and an independent review (routine backup/operations change; no privilege added, same databases dropped). |
| this PR | Second refresh of this handoff (documentation only) | see the PR | Needs an independent review before the gate. |

Known follow-ups recorded by reviewers (not yet done): PR 232 L3 (make `primaryRoot` required in ReplicationPending before S8d-2 wires deletion), L4 (test counts in docs/PHASE-4.3.md), PR 240 Lows (register consequence cells author-written; ambiguous "section 13" reference; docs/AUTONOMOUS-STATE.json lists and docs/AUTONOMOUS-PROGRESS.md not refreshed), PR 237 L3 done in PR 239, PR 239 follow-up (list only referenced names in the generator scans).

Session note: the lead session is being restarted to reduce context; agents of the previous session are retired. Resume with a maximum of two short-lived agents; start by reading the hosted checks of PR 241 and PR 238.

## Next slices (not started; each short, reviewed, inert where stated)
- Identity integrity: S-2c part 2 (identity-tag bound to the reviewed report hash with per-record dispositions; owner-only audited recovery of rows with invalid tags), S-2b (MFA attempt log), S-1b (logout record), S-3 (access-row tags).
- T2 replay authorization: A4 (journal equality), A5 (tooling, read side). Activation stays closed.
- Executor: E2a (requirements T3/T4; correct the opcode name stsflda to ldsflda), E2b, E2c, E3.
- Backup: S8d-2; S8d-3 blocked on S4 and D-099. Audit-head recovery (option b) per D-261..D-263; production activation disabled until named administrator, fresh MFA and second-person approval exist.

## Owner decisions of 2026-11-23 (recorded once; not repeated here)
- D-261, D-262, D-263: audit-head recovery D1, D2, D3 (register item AUDIT-HEAD-RECOVERY-D1-D3; docs/AUDIT-HEAD-INTEGRITY-PLAN.md). The single-operator break-glass is for development and testing only; production activation stays disabled until named administrator, fresh MFA and second-person approval exist.
- D-264: off-machine backup protection, strict default kept (OFFMACHINE-ATTESTATION; docs/PHASE-4.3-S8D-PLAN.md).
- D-265: container/VM confirmation, strict default kept (CONTAINER-VM-CONFIRMATION; same document).
- D-266: identity tagging, conservative default kept (IDENTITY-TAGGING-DISPOSITION; docs/IDENTITY-INTEGRITY-PLAN.md; applies OD-3, D-242).
- D-267 (TEAM decision under D-048, not owner text; PR 244): implementation readings of D-201 for the short close (register item SHORT-CLOSE-READINGS: OPEN with the owner since 2026-11-24, one consolidated item with Q1 partial close, Q2 segregation of duties and Q3 information; not blocking; no production document type is registered as closable until the owner answers). Migration allocation of PR 244: company 0039 = short close, system 0041 = Commercial Document Closer; PR 238 and PR 234 renumber to the next free numbers when they resume.
- Next free decision ID: D-268.

Lead defaults applied under existing owner decisions (not owner text; unchanged by this record): S8d Q4 recovery-test window 35 days, configurable downwards only; Q5 existing notification means suffice while the gate is closed, the external channel of T2 D2 is an activation condition.

## Merge gate and working rules
- Merge only through the gate: exact-head green CI read by REST, every review record valid for its bound SHA (main's tools/verify/check-review-record.js), REST mergeable, merge with the head SHA. verify-head.js cannot run here because GitHub GraphQL is blocked; checks are read by REST.
- One PR merges at a time; the PostgreSQL registry title in backend.yml conflicts otherwise. A conflicted merge needs a reviewer's merge-only confirmation.
- Never self-merge protected files (CLAUDE.md, .github/required-checks.json, .github/secret-scan-allow.json, .github/CODEOWNERS, .github/workflows/verification.yml, tools/verify/**).
- Usage conservation: lead plus at most two active agents.
