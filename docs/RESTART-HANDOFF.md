# Restart handoff (lead), 2026-11-24

Status document for resuming autonomous work after a session or model change. It records state only; it changes no approved decision. The owner decisions of 2026-11-23 are recorded once, verbatim, in docs/DECISIONS.md (D-261..D-266) and in the owner-decision register of docs/INTEGRATION-STATUS.md; this document only points to them.

**Model change 2026-11-23: critical security reviews (identity, replay authorization, audit recovery) are deferred and their PRs stay Draft until a suitable independent reviewer is available.**

## State of main (third refresh, 2026-11-24; sources named per line)
- main = 9f5079d (9f5079d4dd2d3f9419202bec815dfa02500fadbd, the merge of PR 244), read with `git fetch origin` and `git log --first-parent origin/main`; the same head and the merge of each pull request below (merged = true, merge time, head) read from GitHub REST.
- PostgreSQL registry on main: 1832 (title of the step "Verify all 1832 PostgreSQL tests executed and passed" in .github/workflows/backend.yml, equal to the per-class sum recomputed for this refresh: 279 classes, sum 1832). Read at each merge commit: 1815 at e309ca7, 1817 at 4b8fa4a (PR 241: `DatabaseDropAutovacuumTests` 2), 1817 at ad23cf4 and 2dcc8d7 (unchanged), 1832 at 9f5079d (PR 244: +15).
- Migrations on main (read from the two migration directories, contiguous): system 0001-0041 (0041 = the preset Commercial Document Closer), company 0001-0039 (0039 = the short close). Next free: system 0042, company 0040.
- Merged since the second refresh (cdd6b1a), in merge order; each merged head is the reviewed head plus one commit that adds only the review record (read with `git diff --stat`):
  - PR 242, merge e309ca7 (2026-11-23T21:11:07Z, head 0bfbe4f): the second refresh of this handoff, documentation only. Record PR-242-handoff-second-refresh-review (976cb4b, approve, four Lows).
  - PR 241, merge 4b8fa4a (2026-11-23T22:45:31Z, head ad35848): drop of a disposable database although an autovacuum worker is in it (42501 on the forced drop); registry 1815 to 1817. Records PR-241-database-drop-review (025edb8, request changes, M1) and PR-241-database-drop-delta-confirmation (6127c2e, approve).
  - PR 243, merge ad23cf4 (2026-11-23T23:44:47Z, head 4646891): review Lows of PR 241 (L3, L5) and PR 232 (L3, L4); no migration, registry unchanged. Records PR-243-backup-review-lows-review (ad81e5f, request changes, M1) and PR-243-backup-review-lows-delta-confirmation (420fcfd, approve).
  - PR 245, merge 2dcc8d7 (2026-11-24T01:08:33Z, head a1db726): the shared `Tabs` moves focus in the key event, not in a later animation frame. Record PR-245-tabs-focus-race-review (b852d11, approve, four Lows).
  - PR 244, merge 9f5079d (2026-11-24T01:23:30Z, head 7455d5a): Phase 3.13 short close (D-201) with the preset Commercial Document Closer (D-254); company migration 0039, system migration 0041; registry 1817 to 1832. INERT in production: no production document type is registered as closable (docs/PHASE-3.13.md, "Production gate"; pinned by `ShortCloseProductionGateTests`) until the owner answers SHORT-CLOSE-READINGS. Team decision D-267. Records PR-244-short-close-review (f6dfaa4, approve, five Lows), PR-244-short-close-concurrency-and-preset-review (49c3e19, request changes, M1) and PR-244-short-close-delta-confirmation (9416201, approve).
- Hosted Actions, push runs on main for 9f5079d (REST, read 2026-11-24 shortly after the merge): Verification success, Frontend success; Desktop and Picomatch conditional-policy validation in progress; Backend pending (queued behind the Backend run of 2dcc8d7, itself still in progress); the self-hosted and Linux duplicates skipped. Main is therefore NOT VERIFIED green on 9f5079d at this read; re-read before relying on it. The hosted checks of the five merged heads were not re-read for this refresh (NOT VERIFIED here; the merge gate read them).

## Open pull requests (REST list of open pull requests, 2026-11-24: 238 and 234 only, both Draft)
| PR | Content | Head | State and next step |
|---|---|---|---|
| 238 | T2 slice A3, commit-time grant checks and the epoch comparison (its company number 0039 was provisional and is now TAKEN by PR 244) | 2ee2ae0 | REST mergeable state: unknown at this read (the PR 242 review read it as dirty; the registry lines of backend.yml will conflict). Base is still 78bb2e8, so it needs a merge round against main: renumber to company 0040 (or the next free number if another PR merges first) by regeneration, move its extra test migration and pinned counts accordingly, and set the registry title to the per-class sum (main 1832 plus its own classes). Hosted CI on 2ee2ae0 was seven checks success, on the old base only. CRITICAL SECURITY REVIEW DEFERRED: the independent security reviewer approved the H1 fix on the pre-merge head 58fdc55 in notes only (no record written; NOT VERIFIED); a confirmation record bound to the final head by a suitable independent security reviewer is required before the gate. Stays Draft until then. |
| 234 | Audit-head link guard, option (a) (migration numbers provisional) | c773a7c | REST mergeable state: unknown at this read (conflicts with main at the earlier reads; base ea07aca). Not reviewed. Needs the merge of main, renumbering to the next free numbers (system 0042; company 0040 unless another PR merges first), and an independent security review (deferred). Stays Draft. |

This refresh is carried by the branch docs/state-after-244 (documentation only; no pull request opened at the time of writing; it needs an independent review before the gate).

Known follow-ups recorded by reviewers (not yet done):
- PR 241: L1 (`DatabaseDropAutovacuumTests` still depends on when the autovacuum launcher visits the database, 4-minute bound; a short `autovacuum_naptime` in the hosted service container would make it deterministic). L4 is accepted as recorded; L2 was fixed in the PR; L3 and L5 were done in PR 243.
- PR 243: L3 (for a primary root that is a file-system root, `RetentionInventory.Scan`, `ReplicaTombstones.PrimaryHashes` and `ReadInputsAsync` still receive the bare `RealPath` form; pre-existing, same reads as before).
- PR 244 (also recorded in docs/PHASE-3.13.md): second review L2 (a deadlock victim gets 409 `concurrent_change`, no retry in the service; no change requested); first review L2 (one idempotency key per creator is unique per line only), L4 (`document_short_closed` unreachable while D-075 stands: a test when D-075 changes) and L5 (three sums per line under the line locks: one grouped query).
- PR 245: L1 (focus happens before the selection is committed, so `aria-selected` is corrected by the render of the same key event; effect with a real screen reader not measured), L2 (controlled use with a vetoing owner: focus rests on an unselected tab), L3 (the hand-written tab strip of CompanyDetails.tsx still focuses inside `requestAnimationFrame`: see Next slices), L4 (two of the new tests are coupled to "Tabs schedules no frame"; informational).
- PR 242: its four Lows are addressed by this refresh (Low 1: the conflict state of the open rows is stated; Low 2: the two-state row of PR 241 is gone with the merge; Low 3: the class count behind the registry is given; Low 4: each state line names its source).
- Older: PR 232 L3 and L4 done in PR 243. PR 240 Lows: register consequence cells author-written and the ambiguous "section 13" reference stay open; docs/AUTONOMOUS-STATE.json and docs/AUTONOMOUS-PROGRESS.md are refreshed here for PRs 241 to 245 only (the pull requests merged between #48 and PR 240 are still not itemised there). PR 239 follow-up (list only referenced names in the generator scans). PR 237 L3 done in PR 239.

Session note: resume with the lead plus at most two short-lived agents; start by re-reading the push runs of main on 9f5079d and the open list (238, 234).

## Next slices (each short, reviewed, inert where stated)
- DONE: Phase 3.13 short close (PR 244, merged; inert in production until the owner answers SHORT-CLOSE-READINGS).
- STARTED (per the lead; NOT VERIFIED: the branch was not on origin at this read): Phase 3.14 S2 vouchers backend, first cut without posting (the posting gate D-214 stays closed), on branch feature/3-14-s2-vouchers; Draft PR to follow.
- Small follow-up: the CompanyDetails tab strip focus race (PR 245 L3): focus synchronously, or migrate the screen to the shared `Tabs`.
- Identity integrity: S-2c part 2 (identity-tag bound to the reviewed report hash with per-record dispositions; owner-only audited recovery of rows with invalid tags), S-2b (MFA attempt log), S-1b (logout record), S-3 (access-row tags).
- T2 replay authorization: A3 (PR 238, open, see above), A4 (journal equality), A5 (tooling, read side). Activation stays closed.
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
