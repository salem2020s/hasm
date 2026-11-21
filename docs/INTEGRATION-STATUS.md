# HASM - Integration status and handoff

Maintained by the lead agent; the authoritative record of what is open, how it depends on what, and what is waiting for the owner. Recover from this file after a context reset (then `git fetch`, `gh pr list`, `gh pr checks`). Last verified against GitHub: **2026-11-21**, `main` = `045dd2a` (see the cloud-team handoff section for the current state; the older sections below are history unless marked current). Routine PRs are merged autonomously once every gate of the Autonomous merge policy holds (owner directive 2026-11-20, recorded in `CLAUDE.md` by PR #60).

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

## Cloud team handoff (refreshed 2026-11-21, sprint 2, recovered from git and GitHub REST at `main` = `0aeeab7`; lead = Claude Opus; recover from here)

**State of `main`:** `0aeeab77b68f7151cdc7df059c1b6cacd85ef614` (merge of #128). PostgreSQL registry **1149** (parsed from the `EXPECTED` registry of `.github/workflows/backend.yml`, which agrees with the step title; the running total is whatever that file says at merge time, never this sentence). Migrations on `main`: company 0001-0031 (31 files; 0030 document links, 0031 attachment retention) and system 0001-0030 (30 files; 0030 attachment-retention access).

**Migration allocation (numbers follow MERGE ORDER; allocated here, never reserved ahead in a PR):** system 0030 R1 (on `main`); system 0031 #133 (MFA S1); system 0032 #134 (4.3 slice 6); system 0033 D's S2 (taken at rebase); company 0031 R1 (on `main`); company 0032 S5a (#136). Each branch is renumbered at merge by merging `main` into it; generated names are never reused.

**Decisions on `main`:** D-001..D-043, D-046..D-107, D-116..D-120 and D-130..D-139 (D-044/D-045 stay reserved for #48). Allocated ranges not yet on `main`:

| Range | Owner | State |
|-------|-------|-------|
| D-108..D-115 | E and R | allocated; R's plan text is in `docs/PHASE-R-attachment-retention.md`; D-109 and D-113 are owner-pending |
| D-121..D-129 | D | allocated, not yet on `main` |
| D-140..D-141 | A (3.13 S5a, #136) | on the #136 branch, not on `main` |
| D-142 | D (4.1, enrolment-code rules) | on D's S2 branch, not on `main` |
| D-143..D-153 | B (4.3 slice 7 plan, #143; D-152 and D-153 confirmed by the lead after review round 1) | on the #143 branch (docs only, under C's review), not on `main` |
| D-154..D-159 | H (3.14 plan, #148) | on the #148 branch (docs only), not on `main`; D-160..D-169 stay free; D-170..D-178 are B's (4.4, #152) |
| D-170..D-178 | B (Phase 4.4 plan, signed manual updates, #152; lead allocation confirmed, starting at D-170 to avoid H's 3.14 range from D-154; audit block 2030-2039) | on the #152 branch (docs only, under C's review), not on `main` |

**Next free decision ID: D-160** (D-160..D-169 stay free; D-170..D-178 are taken by Phase 4.4; the next free ID after them is D-179). Audit-event blocks (lead allocation): **2000-2009** R (attachment retention), **2010-2013** 4.3 slice 5, **2014-2019** 4.3 slice 6, **2020-2029** 4.3 slice 7, **2030-2039** Phase 4.4 (signed manual updates, #152), **2100-2199** Phase 3.14 (cash and bank, #148; no 21xx code exists on `main`); the highest code in use on `main` outside these is 1908.

Cloud environment: .NET SDK 10 (installed in-session by the environment's own apt command); PostgreSQL 16 binaries; the Linux disposable-cluster harness (`scripts/test-pg/*.sh`, single holder lock, port 5433 only, destroy after use) is merged (#89). Since #135 (`38096b5`) isolated workers are the PREFERRED local runner for PostgreSQL classes: `scripts/test-pg/run-isolated.sh "$PWD" --build --filter "FullyQualifiedName~X"` from your own worktree with `main` merged in; capacity is 2 runs machine-wide, one per agent; never touch another run's directory or lock. GraphQL is blocked: exact-head verification = the unmodified `main` copy of `tools/verify/verify-head.js` with `--input` from REST data; merges via REST with the head SHA pinned; for a final head that adds only a review record or a `main` merge, the code delta is proven identical to the reviewed head by `git patch-id`.

Merged by the cloud team since the Opus resumption (each: independent full-diff review recorded, verify-head green on the exact merged head, mergeable clean, no Critical/High, no protected file except #82, merged on the owner's explicit instruction, D-078): #83 handoff, #84/#86/#88/#92/#93/#97/#103 pricing frontend follow-ups (bounded server-side pickers, catalog-wide unique message keys, by-ids customer lookup, active-group filter), #78 4.3 slice 2 (PgBackupTool, locale capture D-053), #91/#94 4.3 slice 3 (registry-bound backup, trial restore with the 21.8 pre-cutover checks, D-058..D-062), #100 4.3 slice 4 plan (D-069..D-073; owner package 4.3-2), #89 Linux test harness, #90/#96/#98/#101 list search/paging/ids/active and standard invalid_request bodies, #95 isolated measurement tests (D-063), #87 3.13 S1a (company/system 0028), #102 shared bounded advisory-lock acquisition, #105 handoff refresh, #99 3.13 S1b (company/system 0029, D-064..D-068), #106 by-ids lookup of units and item groups, #82 CLAUDE.md standing authorization (owner-instructed merge, D-078).

**Merged in sprint 1 (refresh of 2026-11-21, recovered from GitHub):** #110 `e31c823` owner decisions 1-11 (D-078..D-088); #111 `3e5a3eb` PostgreSQL client/server major-version assertion (D-084); #104 `db2a55e` 4.3 slice 4, backup run of the whole installation; #114 `b676f5f` plan for decision R, attachment retention (D-082); #113 `002ffa3` 4.1 MFA and identity security plan (D-080); #120 `039ad58` D-120, S1 ships with `user_totp` always excluded; #112 `7f65b32` AuditTrail first-touch state scoped to one PostgreSQL transaction; #115 `bdb63f8` endpoint authorization inventory (every route protected or allowlisted, anonymous 401); #118 `b8f551e` frontend i18n parity, permission/session states, Arabic-digit input and the translated error codes; #116 `045dd2a` 4.3 slice 5 plan (encrypted and signed backup sets); #105 `d3bee74` handoff refresh; #99 `98c3a0a` 3.13 S1b; #106 `61ee7be` by-ids lookup of units and item groups; #82 `0c4661d` CLAUDE.md standing authorization.

**Merged since the sprint-1 refresh (#124), all recovered from GitHub; merge commit on `main`:** #108 `cb7bd12` 3.13 S2 document links and partial fulfilment (company 0030); #117 `b4bb408` cross-module accounting scenarios; #121 `453fa5b` concurrency invariants; #122 `7c060ee` 3.13 S6 commercial-document screen; #130 `c0edc8b` 4.3 slice 6 plan; #126 `631361a` S6 Links tab; #107 `9d54229` `PostInWorkAsync` in a caller-supplied transaction; #131 `89f8ffe` Phase R slice R1 (retention policy, legal holds, eligibility preview; company 0031, system 0030); #132 `b65b93b` runtime-role access to the document-link table with a completeness guard; #119 `364a519` endpoint permission matrix; #138 `ae65f9b` model identifiers and version-qualified model names removed from docs; #135 `38096b5` isolated parallel PostgreSQL test workers; #140 `649fbc9` hardened migration CREATE TABLE scan; #129 `2c86052` `number_already_issued` refusal; #139 `67450de` Phase R slice R3 retention screen; #141 `5b9fe04` pricing UI test split; #128 `0aeeab7` 4.3 slice 5 (encrypted backup sets, authenticated manifests, key export, recovery).

**Open pull requests (exact heads read from `gh api repos/salem2020s/hasm/pulls?state=open`; every one is a Draft):**

| PR | Branch | Head | Owner agent | State and next step |
|----|--------|------|-------------|---------------------|
| #125 | `test/journal-commercial-invariants` | `2f9ee37` | F (Opus) | journal and commercial invariants; first in the queue; registry reconciled at 1152 on its head, re-reconciled at merge |
| #133 | `feature/phase-4-1-s0` | `3a7a517` | D (Sonnet) | 4.1 S1 MFA state and the TOTP secret store (system 0031); approved; second in the queue |
| #127 | `test/inventory-period-invariants` | `113b285` | F (Opus) | inventory period invariants (item 6 withdrawn and tracked on #129's line); third in the queue |
| #136 | `feature/phase-3-13-s5a` | `3bb88df` | A (Sonnet) | 3.13 S5a approval policies for commercial documents (company 0032, D-140/D-141); fourth |
| #137 | `feature/phase-3-13-s5a-ui` | `218046d` | A (Sonnet) | S5a approval-progress panel; STACKED on #136, merges right after it |
| #134 | `feature/phase-4-3-slice6-impl` | `016d0f8` | B (Opus) | 4.3 slice 6 retention of backup runs (system 0032); approved; red until system 0031 (#133) is on `main`; last in the queue |
| #143 | `docs/phase-4-3-slice7-plan` | `033e831` confirmed by C; the D-152 redesign for M7 is under C's confirmation | B (Opus) | 4.3 slice 7 plan, live company restore cutover (D-081; docs only, D-143..D-153, audit 2020-2029); under C's review; OUTSIDE the queue |
| #142 | `feat/numeric-input-digits` | `9736fa7` | E (Sonnet) | shared whole-number parsing for Arabic-Indic, Persian and mixed digits (frontend only, no registry change); approved, record pending; OUTSIDE the queue |

WIP with no pull request yet: D's S2 (system 0033 at rebase, D-142). B's slice 7 plan is #143 (docs only, D-143..D-153, audit 2020-2029) and is outside the merge queue.

Inert pull requests (no action; details in the next section): #48 (`1e8f477`, BLOCKED on X1-X8), #51 (`fe2ee06`) and #55 (`23d4850`) (security proposals superseded by the merged #61), #62 (`8552693`, VALIDATION ONLY), #85 (`8b87ea3`, superseded by the merged #89; the owner may close it).

**Merge queue (PostgreSQL registry serial):** #125, then #133, then #127, then #136, then #137, then #134; #142 is outside the queue. At EVERY step: merge `main` into the next branch (never rebase, never force-push), then re-reconcile (1) the registry in `.github/workflows/backend.yml` (raise it to `main`'s total at that moment plus the PR's own new tests, checked with `node tools/verify/ci-guard.js --base origin/main` and the parser), (2) the migration numbers (see the allocation above), (3) the decision and audit-event ranges, and repeat the exact-head verification (`verify-head.js` from `main`) because the head SHA changed; `--match-head-commit` on the merge. #134 stays red until #133 lands because its system migration 0032 needs 0031 on `main`.

**Local runner:** since #135 (`38096b5`) isolated workers are the PREFERRED local runner for PostgreSQL classes: `scripts/test-pg/run-isolated.sh "$PWD" --build --filter "FullyQualifiedName~X"` from your own worktree with `main` merged in; capacity is 2 runs machine-wide, one per agent; never touch another run's directory or lock.

**Agent roster:** lead = Claude Opus; A, D, E, H = Sonnet; B, C, F, G = Opus. Roles: A 3.13 (documents, S5a, S6), B 4.3 (backup), C independent review and the endpoint permission matrix, D backend, stock posting and 4.1 identity, E frontend and Phase R screens, F accounting scenarios and review, G independent security/audit review, H Release 1 scenario and regression QA, handoffs and documentation hygiene. Next 3.13 slices: S5b is blocked (see the register); S6 screen approved (D-079).

**Still open with the owner:** X1-X8 (#48), OD-11, Q (attachment quota and malware scanning), S and C (production-readiness hardening), the 3.13 questions Q2/Q3 (built fail-closed), Q4, Q5, Q6, Q12, Q16, Q17 and Q18, R's Q1-Q3 (D-109 and D-113 owner-pending), 4.3-ACT, the slice 6 questions Q1-Q7, the slice 7 questions Q1-Q6, item 2 (the stock-period database guard), the S5a digit-type limit, S5a Reject-and-stage-role, and the system-database least privilege of `user_totp` (M2 of #133). Details in the register below.

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
| 4.3-2 | RESOLVED by the owner 2026-11-21 (D-085..D-088): envelope encryption with an offline recovery key pair and a separate manifest signing key; retention at least 7 daily / 4 weekly (minimums; never the last recoverable, legal-hold or active-chain backup), month-end and year-end by legal policy (default never), automatic deletion inactive until safeguards are tested; `backup-keys` to a separate destination; unencrypted sets DEV/TEST only | UNBLOCKED; no set encryption, signature, deletion or key export yet; RELEASE1-ACCEPTANCE G not met (D-088 keeps release acceptance blocked until encryption and authenticated manifests are tested) |
| 3.13-Q10 | RESOLVED by the owner 2026-11-21 (D-079): a minimal shared Web/Tauri commercial-document screen in S6; test-only types never shown in production | UNBLOCKED for S6 |
| TIE | RESOLVED by the owner 2026-11-21 (D-083): keep the 0026 tie index; no DROP exception | equal-key group rules stay refused with ambiguous_rule |
| CV | RESOLVED by the owner 2026-11-21 (D-084): add the client-version assertion to backend-selfhosted.yml (adding only; protected-file process) | added and merged in #111 (`3e5a3eb`) |
| AR | RESOLVED by the owner 2026-11-20: Arabic label for retiring an import template is "إيقاف استخدام القالب" | applied in #73 |
| 3.13-Q2, Q3 | OPEN with the owner (3.13 S2 questions) | #108 is built with fail-closed defaults; the behavior stays disabled until decided |
| 3.13-Q4, Q5, Q6, Q12, Q16, Q17 | OPEN with the owner | S3 needs Q6, Q16, Q17 (and #107); S4 needs Q4, Q5, Q12; not started |
| R-Q1..Q3 | OPEN with the owner: questions of the retention plan (`docs/PHASE-R-attachment-retention.md`); D-109 and D-113 are owner-pending | the plan (#114) is merged; retention policy and purge are not built; nothing deleted |
| C | Production-readiness: mandatory Rust `cargo audit` job (M6) | no Rust audit exists (pre-existing gap); adding a mandatory job changes protected gates, so it needs the owner |
| 4.3-ACT | Activation of automatic backup deletion (D-137, 4.3 slice 6): OPEN with the owner; the activation PR is never self-merged and needs an explicit owner instruction plus recorded evidence. Prerequisites from the review of #134: L1 (the delete-walk time-of-check/time-of-use race) and L2 (resumable audit event 2016) must be closed first; L5 is fixed | deletion INACTIVE (three layers); preview only |
| CUTOVER-ACT | Activation of the live company restore cutover (D-081; 4.3 slice 7, D-144): OPEN with the owner (Q3); `RestoreSafeguards.CutoverAvailable` stays false until the slice 7 engine, the Phase 4.1 approval framework (S4/S8, `restore.execute`) and an owner-instructed activation PR with a recorded drill exist | cutovers UNAVAILABLE; manual supervised restore procedure only |
| 4.3-S6-Q1..Q7 | OPEN with the owner (backup retention questions of slice 6, `docs/PHASE-4.3.md`): Q1 legal policy for month-end and fiscal-year-end runs (period, approver, whether automatic deletion may ever apply); Q2 which run is "the" month-end backup; Q3 calendar month-ends in addition to fiscal period ends; Q4 whether attachment legal holds extend to backups; Q5 runs of companies no longer Ready; Q6 an authorised manual deletion path for protected runs; Q7 how long tombstones and the deletion audit are kept | every default is the preserving one: nothing protected is deleted automatically, no policy model is built |
| S5a-Reject | OPEN: should Reject of a role-bound approval stage require the stage role? Today the server's reject needs only approve permission and independence (found in the review of #137); the screen hides Reject like Approve but that is not a control | decision for the owner (approval policy); until then the server permits and the UI only mirrors |
| S5a-DIGIT | OPEN limitation (not relaxed): the approval-policy table's document-type shape has no digit, while the commercial type pattern allows digits, so a registered commercial type whose name contains a digit cannot be given a policy. The owner decides whether to widen the shape (a migration replacing a reviewed constraint) or to keep type names digit-free | #136 keeps the constraint and refuses the policy |
| S5b | BLOCKED: stock-affecting approval needs S3 and the open questions Q6, Q16, Q17 and Q18 (3.13) | not started |
| 4.3-S7-Q1..Q9 | OPEN with the owner (cutover questions of the slice 7 plan in #143), each with its preserving default: Q1 the live database is lost or unreadable so no pre-cutover safety backup can be taken (default: the cutover is refused); Q2 how long the rollback window lasts and what happens to the replaced database and folder (default: a 7-day window, the replaced database and folder are never removed automatically); Q3 who may turn cutovers on (default: only an activation PR merged on the owner's explicit instruction after a recorded drill, as for 4.3-ACT); Q4 cutover when the live audit chain does not verify (default: REFUSED, review L4 of #143; option: allowed only when the approver re-types every failure code, reported forever); Q5 a set from an older migration level (default: migrated forward on the prepared copy before the switch with the checks run after migration, a newer set is refused); Q6 who is notified and how (default: per D-102, audit and in-app to every company administrator and to the identity administrators); Q7 what may a restore lose (default: nothing of accounting: REFUSE when the lost tail holds any accounting event or any number, issued or voided, differs; option for numbering: advance and void under Agent F's six conditions; D-153); Q9 fiscal state after the backup point (default: REFUSE on any difference; option: re-apply live closures, never reopen; D-153); Q8 company-scope revocations after the backup point (default: scoped assignments already inert in live are soft-revoked on the system side before the switch, and a widened cost-center coverage refuses; security-preserving, no alternative needed; D-152) | no cutover exists until the plan is merged and built; every default is the preserving one |
| 4.4-Q1..Q4 | OPEN with the owner (update questions of the Phase 4.4 plan), each with its preserving default: Q1 custody of the publisher's update signing key, separate from backup manifest signing, licence signing and desktop code signing (default: a dedicated key used for nothing else, kept offline, never on a build or update server); Q2 independent approval for applying an update, which D-080(6) does not list (default: required through the 4.1 framework once it exists, a named administrator under the interim mode until then); Q3 rollback after the installation has reopened (default: none; recovery through the restore procedures, copies kept until released); Q4 licence entitlement for updates (default: no entitlement check in 4.4; 4.2 decides) | no update command exists until the plan is merged and built; every default is the preserving one |
| STOCK-PERIOD-DB | OPEN, gap item 2 (F's proposal; nothing built): the stock and cost effects are guarded against closed periods in the service layer only, while journals have a database guard (migration 0009). Option (a) keep the service-only gate and document the gap; (b) RECOMMENDED: one additive company migration of insert-only BEFORE INSERT triggers on the header tables of cost operations, stock postings and reversals, adjustments and negative-stock settlements, refusing soft-closed, Final Closed or missing periods (existing rows untouched; about +2 PostgreSQL tests; one migration number; a future replay or backfill such as 3.8D could not insert into closed periods); (c) a detector probe only | the gap is open until the owner chooses; services already refuse, so no behaviour changes today |
| M2-TOTP | OPEN, a production-readiness BLOCKER (M2 of #133): least privilege of the system database for the `user_totp` secret store | #133 may merge only with this recorded; production readiness stays blocked |
| REL-ACC | Release acceptance stays BLOCKED per owner item 11 (D-088) until encryption and authenticated manifests are tested: #128 merged the code, but the tests that release acceptance needs are still to be recorded | `docs/RELEASE1-ACCEPTANCE.md` G not met; never hand-edited, generated only by `tools/roadmap/generate.js` |

## Medium follow-ups recorded in review records (no approval needed; schedule with the owning slice)

* DONE: #56 deactivated account/representative stays editable and the representative `UserId` membership check (#73, #74); #57 plaintext spooling, upload memory cap/concurrency, 403-vs-404 oracle and stale `*.att.tmp-*` sweep (#74); #63 retire UI for import templates and route-encoding tests (#73).
* OPEN: #56 free-text user GUID input and the raw representative id shown to viewers without `company.salesreps.view` (need a backend user lookup API); #57 encrypt while receiving (one plaintext copy is still held in memory); the full pricing preloads used only for table names of units and item groups (PR-69 M2 itself is CLOSED by #92, #97 and #103, which adopted bounded server-side pickers; a by-ids lookup for units and item groups is a backend follow-up); #70 Lows (scheduled in the backup follow-up).

* LOW follow-ups recorded in review records (sprint 2): #107 G round 6 L1, widen `CompanyTransactionDisciplineTests` to any receiver, aliases and Rollback, or replace the exposed `NpgsqlTransaction` with a savepoint-only wrapper (OPEN); #132 the completeness guard's migration scan regex (hardened by #140, merged); #119 warehouse and cost-centre scope controls are done, the Retention Approver permission-binding test (a Company Administrator without approve is refused on approve, revoke and release) is optional (OPEN); #137 role and user NAMES instead of the first 8 characters of ids, and a notice when the approval progress cannot be read (OPEN); #139 R3 hold-target pickers (`SearchPicker` over records and attachments, needs a server search for removed attachments) (OPEN); #134 L1 and L2 (see 4.3-ACT).

## UNFINISHED Release 1 requirements (explicitly tracked; never silently omitted)

* **Customer groups (SRS 8.1, pricing by customer group):** DONE: merged in #75 (`388ec2a`, company 0027); recorded limitation: the 0026 discount tie index refuses equal-key group rules (the owner decided to keep it for Release 1, D-083). Plan in `docs/PHASE-3.11.md`.
* **Import-file rewrap retirability report** (decision K follow-up) and import-file key coverage in the backup manifest.
* **Backup/restore (SRS 14.4, 16):** merged: slice 1 (#70), slice 2 PgBackupTool (#78, D-053), slice 3 registry-bound backup and trial restore with the 21.8 pre-cutover checks (#91, #94, D-058..D-062), slice 4 plan (#100, D-069..D-073); slice 4 implementation in #104. Remaining: multiple destinations, set encryption/signature, deletion of old sets, key-directory backup (approved with safeguards: D-085..D-088), full-installation restore, and cutover with the audited restore acknowledgement (approved with safeguards: D-081).
* **Production-readiness hardening:** decision S (bundler/Tauri inspection) and decision C (Rust `cargo audit`) need the owner (protected gates).

## Next development tasks

See "Next tasks" in the cloud-team handoff section above (one slice at a time, each with an independent full-diff review).
