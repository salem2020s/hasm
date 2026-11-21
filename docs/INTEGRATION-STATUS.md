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

Migrations on `main`: see the cloud-team handoff section below (current values are kept there only, to avoid contradictions).

## Cloud team handoff (refreshed 2026-11-21 against GitHub; lead = Claude Opus 5.5; recover from here)

`main` = `0c4661d`; PostgreSQL registry **1085**; company migrations 0001-0029, system 0001-0029; company 0030 is ALLOCATED to 3.13 S2 (A). Decisions on `main`: D-001..D-073 except D-044/D-045 (reserved for #48); D-074..D-077 are ALLOCATED to 3.13 S2 (A); D-078..D-088 record the owner decisions of 2026-11-21; the next free decision ID is D-089. Cloud environment: .NET SDK 10 (installed in-session by the environment's own apt command); PostgreSQL 16 binaries; the Linux disposable-cluster harness (`scripts/test-pg/*.sh`, single holder lock, port 5433 only, destroy after use) is merged (#89). GraphQL is blocked: exact-head verification = the unmodified `main` copy of `tools/verify/verify-head.js` with `--input` from REST data; merges via REST with the head SHA pinned; for a final head that adds only a review record or a `main` merge, the code delta is proven identical to the reviewed head by `git patch-id`.

Merged by the cloud team since the Opus resumption (each: independent full-diff review recorded, verify-head green on the exact merged head, mergeable clean, no Critical/High, no protected file): #83 handoff, #84/#86/#88/#92/#93/#97/#103 pricing frontend follow-ups (bounded server-side pickers, catalog-wide unique message keys, by-ids customer lookup, active-group filter), #78 4.3 slice 2 (PgBackupTool, locale capture D-053), #91/#94 4.3 slice 3 (registry-bound backup, trial restore with the 21.8 pre-cutover checks, D-058..D-062), #100 4.3 slice 4 plan (D-069..D-073; owner package 4.3-2), #89 Linux test harness, #90/#96/#98/#101 list search/paging/ids/active and standard invalid_request bodies, #95 isolated measurement tests (D-063), #87 3.13 S1a (company/system 0028), #102 shared bounded advisory-lock acquisition, #105 handoff refresh, #99 3.13 S1b (company/system 0029, D-064..D-068), #106 by-ids lookup of units and item groups, #82 CLAUDE.md standing authorization (owner-instructed merge, D-078).

| Open work | Owner agent | Head | State and next step |
|-----------|-------------|------|---------------------|
| #104 4.3 slice 4 implementation (scheduled whole-installation backup) | B (Opus) | `0e7d184` | approved by Agent C; L1-L7 fixed; C confirms the L6/L7 delta, then exact-head CI and merge (registry reconciled with #107) |
| #107 3.7B in-transaction PostInWorkAsync (prerequisite of 3.13 S3) | D (Sonnet) | `a81d15c` | Critical; round-2 audit finding (touched-stream state after savepoint rollback) fixed; C reviews round 3 |
| #109 party picker search (truncated at 500) | E (Sonnet) | moving | C requested changes (M1: client-side filtering hid matches); server-side `active`/`withoutRole` filters being added |
| 3.13 S2 document links and partial fulfilment | A (Sonnet) | branch `feature/phase-3-13-s2` | company 0030, D-074..D-077; fail-closed defaults for Q2/Q3 (owner-pending) |

Agents: A Sonnet (3.13), B Opus High (4.3), C Opus High (independent review), D Sonnet (backend/QA), E Sonnet (frontend). Next 3.13 slices: S3 needs Q6, Q16, Q17 and #107; S4 needs Q4, Q5, Q12; S6 screen approved (D-079). Phases unblocked by the owner decisions of 2026-11-21: 4.1 MFA and identity (D-080), 4.3 encryption, signing, retention, key export and restore cutover (D-081, D-085..D-088), attachment retention R (D-082), the CV workflow step (D-084).

#85 (`feature/tooling-linux-pg-test-harness`, `8b87ea3`) is an older Linux harness PR SUPERSEDED by the merged #89; not to be merged; the owner may close it.

Owner decisions of 2026-11-21: items 1-11 RESOLVED (D-078..D-088, register below). Still open with the owner: X1-X8 (#48), OD-11, Q (attachment quota and malware scanning), S and C (production-readiness hardening), and the 3.13 questions Q2/Q3 (built fail-closed), Q4, Q5, Q6, Q12, Q16 and Q17.


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
| R | RESOLVED by the owner 2026-11-21 (D-082): retention per a company-approved legal policy, default preservation; audited, authorized purge only after eligibility and legal-hold checks | bytes of removed attachments are kept; the policy and purge are not built yet |
| Q | Per-company attachment quota and malware scanning | not built |
| K | RESOLVED by the owner rule of 2026-11-20 (never retire a key retained attachments still need): rewrap now reports `retainedUnderOtherKeys` / `oldKeysMayBeRetired`; retirement requires true for every company plus a clean verify (`docs/PHASE-3.12.md`) | removed sensitive files keep their key; the import-file rewrap needs the same report (follow-up) |
| S | Production-readiness: bundler/Tauri inspection hardening (aliases outside node_modules, externals, workers, platform Tauri configs and `TAURI_CONFIG`, build hooks, installer/executable embedding; `docs/review-records/PR-security-picomatch-apply.md` M1-M5) | recorded residual risk; existing gates unchanged |
| 4.1-1..6 | RESOLVED by the owner 2026-11-21 (D-080): mandatory MFA for system and privileged administrators, company policy for other roles, key-ring protection of TOTP secrets, controlled audited recovery, one emergency account, independent DR procedure, independent approval for sensitive grants/key retirement/restore; AD/Kerberos deferred | UNBLOCKED; only the TOTP primitive (#72) exists; the system migration takes the next free number |
| 4.3-1 | RESOLVED by the owner 2026-11-21 (D-081): live restore with acknowledgement, independent approval, audit and a new checkpoint epoch that preserves and verifies the previous chain | UNBLOCKED; not built |
| 4.3-2 | RESOLVED by the owner 2026-11-21 (D-085..D-088): envelope encryption with an offline recovery key pair and a separate manifest signing key; retention 7 daily / 4 weekly, month-end and year-end by legal policy (default never), automatic deletion inactive until safeguards are tested; `backup-keys` to a separate destination; unencrypted sets DEV/TEST only | UNBLOCKED; no set encryption, signature, deletion or key export yet; RELEASE1-ACCEPTANCE G not met (D-088 keeps release acceptance blocked until encryption and authenticated manifests are tested) |
| 3.13-Q10 | RESOLVED by the owner 2026-11-21 (D-079): a minimal shared Web/Tauri commercial-document screen in S6; test-only types never shown in production | UNBLOCKED for S6 |
| TIE | RESOLVED by the owner 2026-11-21 (D-083): keep the 0026 tie index; no DROP exception | equal-key group rules stay refused with ambiguous_rule |
| CV | RESOLVED by the owner 2026-11-21 (D-084): add the client-version assertion to backend-selfhosted.yml (adding only; protected-file process) | not yet added |
| AR | RESOLVED by the owner 2026-11-20: Arabic label for retiring an import template is "إيقاف استخدام القالب" | applied in #73 |
| C | Production-readiness: mandatory Rust `cargo audit` job (M6) | no Rust audit exists (pre-existing gap); adding a mandatory job changes protected gates, so it needs the owner |

## Medium follow-ups recorded in review records (no approval needed; schedule with the owning slice)

* DONE: #56 deactivated account/representative stays editable and the representative `UserId` membership check (#73, #74); #57 plaintext spooling, upload memory cap/concurrency, 403-vs-404 oracle and stale `*.att.tmp-*` sweep (#74); #63 retire UI for import templates and route-encoding tests (#73).
* OPEN: #56 free-text user GUID input and the raw representative id shown to viewers without `company.salesreps.view` (need a backend user lookup API); #57 encrypt while receiving (one plaintext copy is still held in memory); the full pricing preloads used only for table names of units and item groups (PR-69 M2 itself is CLOSED by #92, #97 and #103, which adopted bounded server-side pickers; a by-ids lookup for units and item groups is a backend follow-up); #70 Lows (scheduled in the backup follow-up).

## UNFINISHED Release 1 requirements (explicitly tracked; never silently omitted)

* **Customer groups (SRS 8.1, pricing by customer group):** DONE: merged in #75 (`388ec2a`, company 0027); recorded limitation: the 0026 discount tie index refuses equal-key group rules (relaxing it is an owner decision on the loader DROP policy). Plan in `docs/PHASE-3.11.md`.
* **Import-file rewrap retirability report** (decision K follow-up) and import-file key coverage in the backup manifest.
* **Backup/restore (SRS 14.4, 16):** merged: slice 1 (#70), slice 2 PgBackupTool (#78, D-053), slice 3 registry-bound backup and trial restore with the 21.8 pre-cutover checks (#91, #94, D-058..D-062), slice 4 plan (#100, D-069..D-073); slice 4 implementation in #104. Remaining: multiple destinations, set encryption/signature, deletion of old sets, key-directory backup (owner package 4.3-2), full-installation restore, and cutover with the audited restore acknowledgement (owner item 4.3-1).
* **Production-readiness hardening:** decision S (bundler/Tauri inspection) and decision C (Rust `cargo audit`) need the owner (protected gates).

## Next development tasks

See "Next tasks" in the cloud-team handoff section above (one slice at a time, each with an independent full-diff review).
