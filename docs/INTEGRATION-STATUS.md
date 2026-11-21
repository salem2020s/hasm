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

## Cloud team handoff (refreshed 2026-11-21, sprint 1, recovered from git and GitHub REST at `main` = `045dd2a`; lead = Claude Opus; recover from here)

**State of `main`:** `045dd2a` (merge of #116). PostgreSQL registry **1092** (read from the `EXPECTED` registry and the step title of `.github/workflows/backend.yml`; the running total is whatever that file says at merge time, never this sentence). Migrations on `main`: company 0001-0029 (29 files) and system 0001-0029 (29 files); company 0030 is ALLOCATED to 3.13 S2 (#108, A).

**Decisions on `main`:** D-001..D-073 except D-044/D-045 (reserved for #48); D-078..D-107 (contiguous); D-116..D-120. Allocated ranges and where their text lives:

| Range | Owner | State |
|-------|-------|-------|
| D-074..D-077 | A (3.13 S2) | ALLOCATED, text arrives with #108 |
| D-078..D-088 | owner decisions of 2026-11-21 | on `main` (#110) |
| D-089..D-095 and D-116..D-119 | B (4.3) | merged |
| D-096..D-107 and D-120..D-129 | D | D-096..D-107 and D-120 on `main`; D-121..D-129 ALLOCATED, not yet on `main` |
| D-108..D-115 | E and R | ALLOCATED, not yet on `main` (R's plan #114 is merged; its D-109/D-113 await the owner) |

**Next free decision ID: D-142.** Audit-event blocks: **2000-2009** for R (attachment retention; the highest code in use on `main` is 1908); **2010-2013** for 4.3 slice 5 (#128), **2014-2019** for 4.3 slice 6, **2020-2029** for 4.3 slice 7 (lead allocation 2026-11-21). Decision IDs allocated and not yet on main: D-130..D-139 (B, 4.3 slice 6, #130), D-140..D-141 (A, 3.13 S5a). Migration allocation rule: system and company migrations are numbered in MERGE ORDER (the loader refuses gaps and generated names are never reused), allocated here and never reserved ahead in a PR; R1 (attachment retention) is expected to take **system 0030 and company 0031** (after #108's company 0030); 4.1 and 4.3 slice 5 take the next free number at merge.

Cloud environment: .NET SDK 10 (installed in-session by the environment's own apt command); PostgreSQL 16 binaries; the Linux disposable-cluster harness (`scripts/test-pg/*.sh`, single holder lock, port 5433 only, destroy after use) is merged (#89). GraphQL is blocked: exact-head verification = the unmodified `main` copy of `tools/verify/verify-head.js` with `--input` from REST data; merges via REST with the head SHA pinned; for a final head that adds only a review record or a `main` merge, the code delta is proven identical to the reviewed head by `git patch-id`.

Merged by the cloud team since the Opus resumption (each: independent full-diff review recorded, verify-head green on the exact merged head, mergeable clean, no Critical/High, no protected file except #82, merged on the owner's explicit instruction, D-078): #83 handoff, #84/#86/#88/#92/#93/#97/#103 pricing frontend follow-ups (bounded server-side pickers, catalog-wide unique message keys, by-ids customer lookup, active-group filter), #78 4.3 slice 2 (PgBackupTool, locale capture D-053), #91/#94 4.3 slice 3 (registry-bound backup, trial restore with the 21.8 pre-cutover checks, D-058..D-062), #100 4.3 slice 4 plan (D-069..D-073; owner package 4.3-2), #89 Linux test harness, #90/#96/#98/#101 list search/paging/ids/active and standard invalid_request bodies, #95 isolated measurement tests (D-063), #87 3.13 S1a (company/system 0028), #102 shared bounded advisory-lock acquisition, #105 handoff refresh, #99 3.13 S1b (company/system 0029, D-064..D-068), #106 by-ids lookup of units and item groups, #82 CLAUDE.md standing authorization (owner-instructed merge, D-078).

**Merged since the previous refresh, all recovered from GitHub (merge commit on `main`):** #110 `e31c823` owner decisions 1-11 (D-078..D-088); #111 `3e5a3eb` PostgreSQL client/server major-version assertion (D-084); #104 `db2a55e` 4.3 slice 4, backup run of the whole installation; #114 `b676f5f` plan for decision R, attachment retention (D-082); #113 `002ffa3` 4.1 MFA and identity security plan (D-080); #120 `039ad58` D-120, S1 ships with `user_totp` always excluded; #112 `7f65b32` AuditTrail first-touch state scoped to one PostgreSQL transaction; #115 `bdb63f8` endpoint authorization inventory (every route protected or allowlisted, anonymous 401); #118 `b8f551e` frontend i18n parity, permission/session states, Arabic-digit input and the translated error codes; #116 `045dd2a` 4.3 slice 5 plan (encrypted and signed backup sets); #105 `d3bee74` handoff refresh; #99 `98c3a0a` 3.13 S1b; #106 `61ee7be` by-ids lookup of units and item groups; #82 `0c4661d` CLAUDE.md standing authorization.

**Open pull requests (exact heads read from `gh api repos/salem2020s/hasm/pulls?state=open`; every one is a Draft):**

| PR | Branch | Head | Owner agent | State and next step |
|----|--------|------|-------------|---------------------|
| #109 | `feature/party-picker-search` | `d6e9ff4` | E (Sonnet) | party picker searches the server; first in the merge sequence; C requested changes earlier (client-side filtering), server-side filters added; re-verify the exact head before merge |
| #108 | `feature/phase-3-13-s2` | `e0483b5` | A (Sonnet) | 3.13 S2 document links and partial fulfilment (company 0030, D-074..D-077); Q2/Q3 built fail-closed (owner-pending); merges after #109 |
| #107 | `feature/stock-posting-in-work` | `a81d15c` | D (Sonnet) | 3.7B in-transaction `PostInWorkAsync` (prerequisite of 3.13 S3); Critical, so its independent review must be recorded on the exact head; merges after #108 and reconciles the registry |
| #117 | `test/cross-module-scenarios` | `b85e770` | F (Opus) | cross-module accounting scenarios (gap 3); PostgreSQL tests, so the registry is reconciled at merge; follows #107 |
| #121 | `test/concurrency-invariants` | `146a66e` | H (Sonnet) | parallel same-revision writers (6 PostgreSQL classes, registry +6) and the translated conflict-message test; F is reviewing; last in the sequence, the lead gives the registry total at its turn |
| #123 | `feature/frontend-followups-118` | `849e842` | H (Sonnet) | #118 follow-ups (shared decimal parser, list separator, weak_password rules); approved, waiting for hosted CI on this head; next in line after #109; frontend only, no registry change |
| #119 | `test/endpoint-permission-matrix` | `9a01430` | C (Opus) | per-endpoint 403 matrix of every mutating company endpoint (gap 2); under construction; PostgreSQL tests, registry reconciliation at merge |
| #122 | `feature/phase-3-13-s6` | `2d524f0` | A (Sonnet) | 3.13 S6 shared commercial-document screen, first slice (D-079); built from main, NOT stacked on #108 (no migration; the Links tab and line quantities are added after #108 merges); 1 PostgreSQL test (types route), so the registry is reconciled at merge |

Inert pull requests (no action; details in the next section): #48 (`1e8f477`, BLOCKED on X1-X8), #51 (`fe2ee06`) and #55 (`23d4850`) (security proposals superseded by the merged #61), #62 (`8552693`, VALIDATION ONLY), #85 (`8b87ea3`, superseded by the merged #89; the owner may close it).

**Merge sequence:** #109, then #123 (frontend only, no registry change), then #108, then #107, then #117, then #121, then #119 and #122 (#122 is independent of #108's code; its Links-tab follow-up comes after #108; #119 is under construction; both change the PostgreSQL registry, so each reconciles it at merge). At EVERY step: merge `main` into the next branch (never rebase, never force-push), then re-reconcile (1) the PostgreSQL registry in `.github/workflows/backend.yml` (raise it to main's total at that moment plus the PR's own new tests; check with `node tools/verify/ci-guard.js --base origin/main` and the parser), (2) the migration numbers (company 0030 for #108, then the next free numbers in merge order, see the allocation rule above), (3) the decision and audit-event ranges, and repeat the exact-head verification (`verify-head.js` from `main`) because the head SHA changed; `--match-head-commit` on the merge.

**Agent roster:** lead = Claude Opus; A, D, E, H = Sonnet; B, C, F, G = Opus. Roles: A 3.13 (documents, S6 screen #122), B 4.3 (backup), C independent review and the endpoint permission matrix (#119), D backend and stock posting, E frontend and party picker, F accounting scenarios and review, G independent security/audit review, H Release 1 scenario and regression QA. Next 3.13 slices: S3 needs Q6, Q16, Q17 and #107; S4 needs Q4, Q5, Q12; S6 screen approved (D-079). Phases unblocked by the owner decisions of 2026-11-21: 4.1 MFA and identity (D-080), 4.3 encryption, signing, retention, key export and restore cutover (D-081, D-085..D-088), attachment retention R (D-082), the CV workflow step (D-084, merged as #111).

**Still open with the owner:** X1-X8 (#48), OD-11, Q (attachment quota and malware scanning), S and C (production-readiness hardening), the 3.13 questions Q2/Q3 (built fail-closed), Q4, Q5, Q6, Q12, Q16 and Q17, and R's Q1-Q3 (D-109 and D-113 are owner-pending). Owner decisions of 2026-11-21 items 1-11 are RESOLVED (D-078..D-088, register below).


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
| 4.3-ACT | Activation of automatic backup deletion (D-137, 4.3 slice 6): OPEN with the owner; activation PR is never self-merged and needs an explicit owner instruction plus recorded evidence | deletion INACTIVE (three layers); preview only |

## Medium follow-ups recorded in review records (no approval needed; schedule with the owning slice)

* DONE: #56 deactivated account/representative stays editable and the representative `UserId` membership check (#73, #74); #57 plaintext spooling, upload memory cap/concurrency, 403-vs-404 oracle and stale `*.att.tmp-*` sweep (#74); #63 retire UI for import templates and route-encoding tests (#73).
* OPEN: #56 free-text user GUID input and the raw representative id shown to viewers without `company.salesreps.view` (need a backend user lookup API); #57 encrypt while receiving (one plaintext copy is still held in memory); the full pricing preloads used only for table names of units and item groups (PR-69 M2 itself is CLOSED by #92, #97 and #103, which adopted bounded server-side pickers; a by-ids lookup for units and item groups is a backend follow-up); #70 Lows (scheduled in the backup follow-up).

## UNFINISHED Release 1 requirements (explicitly tracked; never silently omitted)

* **Customer groups (SRS 8.1, pricing by customer group):** DONE: merged in #75 (`388ec2a`, company 0027); recorded limitation: the 0026 discount tie index refuses equal-key group rules (the owner decided to keep it for Release 1, D-083). Plan in `docs/PHASE-3.11.md`.
* **Import-file rewrap retirability report** (decision K follow-up) and import-file key coverage in the backup manifest.
* **Backup/restore (SRS 14.4, 16):** merged: slice 1 (#70), slice 2 PgBackupTool (#78, D-053), slice 3 registry-bound backup and trial restore with the 21.8 pre-cutover checks (#91, #94, D-058..D-062), slice 4 plan (#100, D-069..D-073); slice 4 implementation in #104. Remaining: multiple destinations, set encryption/signature, deletion of old sets, key-directory backup (approved with safeguards: D-085..D-088), full-installation restore, and cutover with the audited restore acknowledgement (approved with safeguards: D-081).
* **Production-readiness hardening:** decision S (bundler/Tauri inspection) and decision C (Rust `cargo audit`) need the owner (protected gates).

## Next development tasks

See "Next tasks" in the cloud-team handoff section above (one slice at a time, each with an independent full-diff review).
