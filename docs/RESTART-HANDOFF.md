# Restart handoff (lead), 2026-11-23

Status document for resuming autonomous work after a session or model change. It records state only; it changes no approved decision. Owner decisions quoted below are verbatim and still have to be entered once in the owner-decision register (docs/INTEGRATION-STATUS.md) by a reviewed documentation PR.

## State of main
- main = 78bb2e8 (PR 239). PostgreSQL registry on main: 1805.
- Migrations on main: system 0001-0040, company 0001-0038.
- Merged on 2026-11-22/23 (newest first): 239 (identity S-2c part 1), 237 (S-2d), 236 (T2 A2), 235 (S-2a), 233 (T2 A1), 231 (T2 v5 amendment), 230 (system DB CONNECT only), 229 (cash presets), 227 (identity S-1), 226, 225 (owner decisions D-240..D-260), 224, 223, 222, 221, 220, 219, 218, 217, 216, 215, 214, 213, 212, 211, 210, 209, 206, 201, 188, 48.

## Open pull requests
| PR | Content | Head | Next step |
|---|---|---|---|
| 238 | T2 slice A3, commit-time epoch comparison (company 0039, provisional) | 2ee2ae0 (merge of main pending CI) | Independent security confirmation of the H1 fix and the merge (reviewer had approved the fix on 58fdc55), then merge gate. |
| 232 | Backup S8d-1, read only | 490d466 | Approved; Low fixes A1-A3 exist as an unpushed local commit by the backup agent; needs a merge-main round, a delta confirmation, then the gate. |
| 234 | Audit-head link guard, option (a) (migration numbers provisional) | c773a7c | Not reviewed. Needs merge of main, renumbering to the next free numbers, independent security review. |

## Next slices (not started; each short, reviewed, inert where stated)
- Identity integrity: S-2c part 2 (identity-tag bound to the reviewed report hash with per-record dispositions; owner-only audited recovery of rows with invalid tags), S-2b (MFA attempt log), S-1b (logout record), S-3 (access-row tags).
- T2 replay authorization: A4 (journal equality), A5 (tooling, read side). Activation stays closed.
- Executor: E2a (requirements T3/T4; correct the opcode name stsflda to ldsflda), E2b, E2c, E3.
- Backup: S8d-2; S8d-3 blocked on S4 and D-099. Audit-head recovery (option b) per the owner decisions below; production activation disabled until named administrator, fresh MFA and second-person approval exist.

## Owner decisions to record in the register (verbatim)
# Owner decisions: audit-head recovery (PR 228) D1–D3 and optional items (2026-11-23) — verbatim

D1 — APPROVED
Allow explicit, authorized recovery from a forged audit tail.
Mandatory safeguards:
* Preserve every original audit record, including forged entries, without modification or deletion.
* Mark quarantined entries as untrusted and never use them as authenticated evidence.
* Require verification of the trusted prefix and all available protected checkpoints.
* Permit recovery only for the explicitly validated contiguous damaged tail. Refuse ambiguous corruption, missing keys, or valid records following an invalid record.
* Append an authenticated recovery event identifying the operator, affected range, reason and timestamp.
* Use a distinct `VerifiedWithBreaks` status, never ordinary `Verified`.
* Ensure all audit consumers, backup retention and recovery checks respect quarantined ranges.
* Require an auditable dry run and independent adversarial security tests.

D2 — APPROVED WITH A STRICT ENVIRONMENT BOUNDARY
During development and testing, allow a documented temporary break-glass recovery by the designated server operator holding the database owner credentials and protected audit key, with explicit authorization and complete audit evidence.
Do not permit this single-person exception for production deployments.
Production recovery requires a named administrator, fresh MFA verification and independent second-person approval.
Design and test the recovery mechanism now. Production activation remains disabled until these controls exist.

D3 — APPROVED
Recovery must always be a deliberate manual operation.
Never automatically skip damaged audit entries or silently repair an audit chain.
Repeated attacks must generate visible security findings.

Optional decisions
1. Off-machine backup protection: keep the strict default. An internal disk or an unverified operator statement alone does not qualify as independent protection.
2. Container/VM confirmation: keep the strict default requiring named administrator confirmation of physically independent backup protection before automatic deletion is activated.
3. Identity tagging: keep the conservative default. Require administrator password resets and MFA re-enrolment where provenance cannot be trusted. Other flagged accounts require individual documented verification and appropriate remediation before tagging. Never blindly trust existing records.

Record these decisions once and continue eligible autonomous development and independent reviews.

Lead defaults applied under existing owner decisions (to record as such): S8d Q4 recovery-test window 35 days, configurable downwards only; Q5 existing notification means suffice while the gate is closed, the external channel of T2 D2 is an activation condition; identity tagging: every password hash is flagged as not supported by events and needs a recorded disposition (administrators: password reset and MFA re-enrolment), applying OD-3.

## Merge gate and working rules
- Merge only through the gate: exact-head green CI read by REST, every review record valid for its bound SHA (main's tools/verify/check-review-record.js), REST mergeable, merge with the head SHA. verify-head.js cannot run here because GitHub GraphQL is blocked; checks are read by REST.
- One PR merges at a time; the PostgreSQL registry title in backend.yml conflicts otherwise. A conflicted merge needs a reviewer's merge-only confirmation.
- Never self-merge protected files (CLAUDE.md, .github/required-checks.json, .github/secret-scan-allow.json, .github/CODEOWNERS, .github/workflows/verification.yml, tools/verify/**).
- Usage conservation: lead plus at most two active agents.
