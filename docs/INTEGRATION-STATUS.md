# HASM - Integration status and handoff

Maintained by the lead agent; the authoritative record of what is open, how it depends on what, and what is waiting for the owner. Recover from this file after a context reset (then `git fetch`, `gh pr list`, `gh pr checks`). Last verified against GitHub: **2026-11-21**, `main` = `47efc9b` (see the cloud-team handoff section for the current state; the older sections below are history unless marked current). Routine PRs are merged autonomously once every gate of the Autonomous merge policy holds (owner directive 2026-11-20, recorded in `CLAUDE.md` by PR #60).

## Merged on 2026-11-20 up to #69 (each on its exact head, verify-head green, independent review recorded; later merges are listed in the cloud-team handoff section)

| PR | Merge commit | Content |
|----|--------------|---------|
| #61 | `215da56` | owner-approved picomatch/pin retirement (approval A, D-047) |
| #53 | `3c42e77` | 3.9A follow-ups |
| #54 | `f3b7831` | 3.10 tax engine (company 0022, system 0024) |
| #60 | `af3cb45` | CLAUDE.md: autonomous routine merging (owner-authorized) |
| #56 | `a7207d4` | 3.9B customers, suppliers, representatives (company 0023, system 0025) |
| #57 | `5731b41` | 3.12 attachments and key management (company 0024, system 0026) |
| #63 | `8f2ccaa` | 3.9C slice 1 import mapping templates (company 0025) |
| #52 | `371adb3` | costing guard tests |
| #58 | `84614cf` | 3.11 slice 1 pure pricing engine |
| #59 | `eb9dfea` | integration status and handoff (earlier revision) |
| #64 | `acf66bb` | 3.9C slice 2 party import adapter (3.9C complete) |
| #65 | `3d13007` | 3.11 slice 2a configurable price-list and discount precedence (decision P resolved) |
| #66 | `23963e6` | attachment key-retirement safety (decision K resolved) |
| #67 | `40228b2` | handoff update |
| #68 | `219af25` | 3.11 slice 2b pricing persistence (company 0026, system 0027) |
| #69 | `3bbf66a` | 3.11 slice 2c pricing API and shared screens (no migration) |

Migrations on `main` (current, see the cloud-team handoff section): **company 0001-0027, system 0001-0027**; PostgreSQL registry **1037**. Company 0028 and system 0028 are reserved for Phase 3.13 S1a (Agent A). The Phase 4.1 enrolment migration takes the next free system number when 4.1 is unblocked. #48 predates these merges; any migration it adds is numbered from the next free numbers at rebase time and its pinned counts re-reconciled.

## Cloud team handoff (refreshed 2026-11-21 against GitHub; lead = Claude Opus 5.5; recover from here)

`main` = `1e0b30b`; PostgreSQL registry **1072**; company migrations 0001-0028, system 0001-0028 (0029 of both reserved for #99). Decisions on `main`: D-001..D-063 and D-069..D-073 (D-064..D-068 are carried by #99). Cloud environment: .NET SDK 10 (installed in-session by the environment's own apt command); PostgreSQL 16 binaries; the Linux disposable-cluster harness (`scripts/test-pg/*.sh`, single holder lock, port 5433 only, destroy after use) is merged (#89). GraphQL is blocked: exact-head verification = the unmodified `main` copy of `tools/verify/verify-head.js` with `--input` from REST data; merges via REST with the head SHA pinned; for a final head that adds only a review record or a `main` merge, the code delta is proven identical to the reviewed head by `git patch-id`.

Merged by the cloud team since the Opus resumption (each: independent full-diff review recorded, verify-head green on the exact merged head, mergeable clean, no Critical/High, no protected file): #83 handoff, #84/#86/#88/#92/#93/#97/#103 pricing frontend follow-ups (bounded server-side pickers, catalog-wide unique message keys, by-ids customer lookup, active-group filter), #78 4.3 slice 2 (PgBackupTool, locale capture D-053), #91/#94 4.3 slice 3 (registry-bound backup, trial restore with the 21.8 pre-cutover checks, D-058..D-062), #100 4.3 slice 4 plan (D-069..D-073; owner package 4.3-2), #89 Linux test harness, #90/#96/#98/#101 list search/paging/ids/active and standard invalid_request bodies, #95 isolated measurement tests (D-063), #87 3.13 S1a (company/system 0028), #102 shared bounded advisory-lock acquisition.

| Open work | Owner agent | Head | State and next step |
|-----------|-------------|------|---------------------|
| #99 3.13 S1b (company/system 0029, D-064..D-068) | A (Sonnet) | after `5ddb2a5` | approved in round 2 by Agent C (Opus); A merges main (DECISIONS conflict), adds date-boundary tests (L5) and date/attribution fixes (L6) and the review record; C confirms the delta; then merge |
| 4.3 slice 4 implementation (scheduled whole-installation backup) | B (Opus) | branch `feature/phase-4-3-slice4-impl` | in progress per the merged plan; nothing reserved by owner package 4.3-2 (no encryption, signature, deletion or key export) |
| 3.7B in-transaction PostInWorkAsync (prerequisite of 3.13 S3) | D (Sonnet) | branch `feature/stock-posting-in-work` | in progress; Critical (merged posting code); separate independent review |
| #82 CLAUDE.md standing authorization (protected) | lead | `e9c5f68` | approved after four review rounds, all checks green; waits ONLY for the owner's explicit "merge #82" |

Agents: A Sonnet (3.13), B Opus High (4.3), C Opus High (independent review), D Sonnet (backend/QA), E Sonnet (frontend; idle: the next 3.13 screen needs owner Q10). Next 3.13 slices: S2 (links, partial fulfilment) after #99, with the plan's fail-closed defaults for Q2/Q3 unless the owner decides otherwise; S3 needs Q6, Q16, Q17 and the PostInWorkAsync change; S4 needs Q4, Q5, Q12.

Owner decisions waiting (one consolidated package, recommendations given in the conversation of 2026-11-21): merge #82; 3.13 Q10 (screen in Release 1); 4.1 items 1-6; 4.3-1 restore acknowledgement and cutover; R attachment retention; relaxing the 0026 discount tie index; the self-hosted client-version CI step; 4.3-2 items 1-4 (backup key custody, deletion of old backups with month-end/year-end definitions, key-directory backup, behaviour until encryption).


## Inert or blocked pull requests (open work: see the cloud-team handoff section)

| PR | Branch | Status |
|----|--------|--------|
| #48 | `feature/phase-3-8d-impact-preview` | BLOCKED on owner decisions X1-X8 (X1 would relax a database safeguard); internal replay checkpoint, execution disabled, implementation preserved; any migration it adds is numbered from the next free numbers at rebase time |
| #51, #55 | security proposals | SUPERSEDED by the merged owner-approved #61 (`215da56`) and inert; not to be merged; may be closed by the owner; never reopen the resolved decision |
| #62 | `integration/chain-with-security` | VALIDATION ONLY; never merge |

## Owner-decision register (each recorded once; fail-closed while open)

| ID | Decision | Effect while open |
|----|----------|-------------------|
| X1-X8 | Phase 3.8D decisions | #48 stays a checkpoint; no financial execution; no safeguard changed |
| OD-11 | Phase 3.8C lots/serials/expiry/reservations | 3.8C not started |
| P | RESOLVED by the owner directive 2026-11-20: precedence is company-configurable, defaults = slice 1; no stacking offered | slice 2a (engine policy) implemented; persistence is slice 2b |
| R | Retention/purge of removed attachments | bytes of removed attachments are kept |
| Q | Per-company attachment quota and malware scanning | not built |
| K | RESOLVED by the owner rule of 2026-11-20 (never retire a key retained attachments still need): rewrap now reports `retainedUnderOtherKeys` / `oldKeysMayBeRetired`; retirement requires true for every company plus a clean verify (`docs/PHASE-3.12.md`) | removed sensitive files keep their key; the import-file rewrap needs the same report (follow-up) |
| S | Production-readiness: bundler/Tauri inspection hardening (aliases outside node_modules, externals, workers, platform Tauri configs and `TAURI_CONFIG`, build hooks, installer/executable embedding; `docs/review-records/PR-security-picomatch-apply.md` M1-M5) | recorded residual risk; existing gates unchanged |
| 4.1-1..6 | Phase 4.1 (OPEN): MFA enforcement scope; key protecting TOTP secrets; lost-authenticator recovery and audit; emergency account; second approval; optional AD/Kerberos | only the TOTP primitive (#72) exists; enrolment and enforcement are not built; their system migration takes the next free number when unblocked (system 0028 is now Phase 3.13 S1a) |
| 4.3-1 | Phase 4.3 (OPEN): audited restore acknowledgement with a checkpoint epoch | not built; recorded BLOCKED in docs/PHASE-4.3.md |
| 4.3-2 | Phase 4.3 (OPEN), ONE package (recommended: 1d, 2b, 3b, 4a): (1) backup encryption key custody (offline recovery key pair recommended), (2) whether and how HASM may delete backups containing financial data (daily/weekly only; month-end and year-end kept until an owner-configured legal period, default never), (3) export of the key directory, (4) interim unencrypted sets on an encrypted volume; details in `docs/PHASE-4.3.md` slice 4 | no set encryption or signature, nothing deleted (retention preview only), no key export; RELEASE1-ACCEPTANCE G not met |
| AR | RESOLVED by the owner 2026-11-20: Arabic label for retiring an import template is "إيقاف استخدام القالب" | applied in #73 |
| C | Production-readiness: mandatory Rust `cargo audit` job (M6) | no Rust audit exists (pre-existing gap); adding a mandatory job changes protected gates, so it needs the owner |

## Medium follow-ups recorded in review records (no approval needed; schedule with the owning slice)

* DONE: #56 deactivated account/representative stays editable and the representative `UserId` membership check (#73, #74); #57 plaintext spooling, upload memory cap/concurrency, 403-vs-404 oracle and stale `*.att.tmp-*` sweep (#74); #63 retire UI for import templates and route-encoding tests (#73).
* OPEN: #56 free-text user GUID input and the raw representative id shown to viewers without `company.salesreps.view` (need a backend user lookup API); #57 encrypt while receiving (one plaintext copy is still held in memory); PR-69 M2 (pricing pickers' silent truncation, until a screen adopts `SearchPicker`); #70 Lows (scheduled in the backup follow-up).

## UNFINISHED Release 1 requirements (explicitly tracked; never silently omitted)

* **Customer groups (SRS 8.1, pricing by customer group):** DONE: merged in #75 (`388ec2a`, company 0027); recorded limitation: the 0026 discount tie index refuses equal-key group rules (relaxing it is an owner decision on the loader DROP policy). Plan in `docs/PHASE-3.11.md`.
* **Import-file rewrap retirability report** (decision K follow-up) and import-file key coverage in the backup manifest.
* **Backup/restore (SRS 14.4, 16):** slice 1 merged (#70: backup set, verify, isolated attachment restore, key coverage). Remaining: pg_dump/pg_restore coordination into an isolated target, scheduling, retention, encryption, RPO/RTO, full restore, and the blocked owner decisions (restore acknowledgement, decision R).
* **Production-readiness hardening:** decision S (bundler/Tauri inspection) and decision C (Rust `cargo audit`) need the owner (protected gates).

## Next development tasks

See "Next tasks" in the cloud-team handoff section above (one slice at a time, each with an independent full-diff review).
