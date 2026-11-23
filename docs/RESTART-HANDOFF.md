# Restart handoff (lead), 2026-11-23

Status document for resuming autonomous work after a session or model change. It records state only; it changes no approved decision. The owner decisions of 2026-11-23 are recorded once, verbatim, in docs/DECISIONS.md (D-261..D-266) and in the owner-decision register of docs/INTEGRATION-STATUS.md; this document only points to them.

**Model change 2026-11-23: critical security reviews (identity, replay authorization, audit recovery) are deferred and their PRs stay Draft until a suitable independent reviewer is available.**

## State of main (read by REST and git on 2026-11-23)
- main = 78bb2e8 (78bb2e849b6e01069f47af9da9590ebc82338617, the merge of PR 239; REST branches/main and `git log origin/main` agree).
- PostgreSQL registry on main: 1805 (title of the step "Verify all 1805 PostgreSQL tests executed and passed (none skipped)" in .github/workflows/backend.yml; equal to the per-class sum of its 268 registered classes, recomputed for this refresh).
- Migrations on main (read from the migration directories): system 0001-0040 (highest `0040_append_only_anchor_history.sql`), company 0001-0038 (highest `0038_company_replay_grant_verification.sql`).
- Merged on 2026-11-22/23 (newest first; PR 239 read by REST as merged with merge commit 78bb2e8 and merged head e0afbe6; the others of this list are as recorded by the lead, NOT VERIFIED one by one in this refresh): 239 (identity S-2c part 1), 237 (S-2d), 236 (T2 A2), 235 (S-2a), 233 (T2 A1), 231 (T2 v5 amendment), 230 (system DB CONNECT only), 229 (cash presets), 227 (identity S-1), 226, 225 (owner decisions D-240..D-260), 224, 223, 222, 221, 220, 219, 218, 217, 216, 215, 214, 213, 212, 211, 210, 209, 206, 201, 188, 48.

## Open pull requests (REST `pulls?state=open`, 2026-11-23: exactly these four, all Draft)
| PR | Content | Head | State and next step |
|---|---|---|---|
| 238 | T2 slice A3, commit-time grant checks and the epoch comparison (company 0039, provisional) | 2ee2ae0 (a merge of main) | Draft; REST mergeable. Hosted CI on this head at this read: 6 checks success, `postgres-integration` still in progress (not yet green). Awaits the finished CI and the independent security confirmation of the H1 fix and of the merges (the reviewer's approval of the fix on 58fdc55 is as reported by the lead, NOT VERIFIED here); then the merge gate. A critical security review: deferred, see the model-change note. |
| 232 | Backup S8d-1, read only | 4814c71 | Draft; REST not mergeable (conflicts with main). Approved by the independent reviewer at b2aac3c (record commit 490d466); the Low fixes A1-A3 are pushed as 4814c71. No hosted check-run is listed on 4814c71 (the conflict prevents the PR workflows). Needs a merge-main round (registry and numbering), a delta confirmation of 4814c71 and of the merge, then the gate. |
| 234 | Audit-head link guard, option (a) (migration numbers provisional) | c773a7c | Draft; REST not mergeable (conflicts with main). Not reviewed. Needs the merge of main, renumbering to the next free migration numbers, and an independent security review (deferred, see the model-change note). |
| 240 | This PR: restart handoff and the record of the owner decisions of 2026-11-23 (documentation only) | see the PR | Draft; needs an independent full-diff review of the decision record before the gate. |

Merged, no longer open: 239 (identity integrity S-2c part 1; merge commit 78bb2e8).

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
- Next free decision ID: D-267.

Lead defaults applied under existing owner decisions (not owner text; unchanged by this record): S8d Q4 recovery-test window 35 days, configurable downwards only; Q5 existing notification means suffice while the gate is closed, the external channel of T2 D2 is an activation condition.

## Merge gate and working rules
- Merge only through the gate: exact-head green CI read by REST, every review record valid for its bound SHA (main's tools/verify/check-review-record.js), REST mergeable, merge with the head SHA. verify-head.js cannot run here because GitHub GraphQL is blocked; checks are read by REST.
- One PR merges at a time; the PostgreSQL registry title in backend.yml conflicts otherwise. A conflicted merge needs a reviewer's merge-only confirmation.
- Never self-merge protected files (CLAUDE.md, .github/required-checks.json, .github/secret-scan-allow.json, .github/CODEOWNERS, .github/workflows/verification.yml, tools/verify/**).
- Usage conservation: lead plus at most two active agents.
