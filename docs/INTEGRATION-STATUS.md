# HASM - Integration status and handoff

Maintained by the lead agent; the authoritative record of what is open, how it depends on what, and what is waiting for the owner. Recover from this file after a context reset (then `git fetch`, `gh pr list`, `gh pr checks`). Last verified against GitHub: **2026-11-21**, `main` = `83343c8` (see the cloud-team handoff section for the current state; the older sections below are history unless marked current). Routine PRs are merged autonomously once every gate of the Autonomous merge policy holds (owner directive 2026-11-20, recorded in `CLAUDE.md` by PR #60).

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

Migrations on `main`: **company 0001-0026, system 0001-0027** (contiguous). PostgreSQL registry on `main`: **1029** tests (`.github/workflows/backend.yml`, raise-only). Company 0027 is taken by #75 (customer groups); the next unassigned numbers are company 0028 and system 0028 (system 0028 is reserved for Phase 4.1 enrolment). #48 predates these merges; any migration it adds is numbered from the next free numbers at rebase time and its pinned counts re-reconciled.

## Cloud team handoff - SAFE CHECKPOINT (2026-11-21, owner-ordered pause for the Lead model switch Sonnet 5.5 to Opus 5.5; recover from here)

**STATE: PAUSED. No agent is working; nothing is being merged or assigned. Development resumes only after the owner explicitly authorizes it.** `main` = `47efc9b` (PostgreSQL registry 1037; company migrations 0001-0027, system 0001-0027). Everything below is pushed to GitHub; agent worktrees (`.claude/worktrees/`) hold no unpushed work.

Cloud environment: this session had no .NET SDK (`dotnet` absent) and PostgreSQL 16.15 tooling only. The shared `HASM-Cloud-Team` setup script now installs .NET SDK 10, so NEW sessions can restore/build/run unit tests locally (a separate session verified SDK 10.0.115, 1,056 infrastructure unit tests, 69 API tests, and 1,091 PostgreSQL integration tests on a disposable 16.15 cluster on 127.0.0.1:5433 with HASM_REQUIRE_PG_TESTS=1 and HASM_REQUIRE_PG_ROLE_TESTS=1). Use only a fresh disposable cluster on port 5433 per `docs/DEVELOPMENT.md` and the `PostgresClusterIdentity` safeguards (fresh credentials and fingerprint, zero failed and zero skipped, clean up afterwards); GitHub Actions stays mandatory. GraphQL is blocked in cloud sessions: exact-head verification = the unmodified `main` copy of `tools/verify/verify-head.js` with `--input` built from REST data (PR files and check-runs of the exact head); drafts are made ready through the CCR `ready_for_review` route; merge through the REST merge endpoint with the head SHA pinned.

Merged by the cloud team (all gates: independent full-diff review recorded, verify-head green on the exact head, mergeable, no Critical/High, no protected file): #73 `9896373`, #72 `e0d3a76`, #74 `62ade0d`, #70 `c0fc2f4`, #71 `83343c8`, #76 `b6992d4`, #75 `388ec2a` (3.11 customer groups, company 0027), #77 `99d49c7`, #79 `8866db0` (3.13 plan), #80 `2ba756c`, #81 `47efc9b` (decisions D-048..D-052).

| PR / branch | Head | CI | State and exact next step |
|-------------|------|----|---------------------------|
| #78 `feature/phase-4-3-backup-restore-2` (Agent B) | `cd8fec5` | all required green | 4.3 slice 2: PgBackupTool (pg_dump/pg_restore into a NEW database), drill tests, PG14+ fail-closed handling (decision recorded in docs/PHASE-4.3.md). Review record names `441b9a8` (round 3), verdict "request changes". NEXT: independent re-review of the delta 441b9a8..cd8fec5 (Agent C), then docs-only commit updating the record to the final head and verdict approve; merge main (registry = main's current value + 2); exact-head verify; merge. |
| #82 `policy/standing-autonomy-claude-md` (lead) | `5c8d72a` | all required green | PROTECTED FILE (CLAUDE.md), standing-authorization text (D-048) with precedence fix; review record names the reviewed head. NOT merged: needs the owner's explicit "merge #82" instruction. |
| `feature/phase-3-13-s1a` (Agent A), NO PR | `2abbd59` | none run (wip, unbuilt) | 3.13 S1a schema checkpoint: company 0028 (document/lines/events, guard draft->submitted, submitted->draft, draft/submitted->cancelled only), system 0028 (branch-scoped `company.commercialdocs` view/create/update/submit/cancel, granted to the Company Administrator preset), pins updated. UNFINISHED: service, API (/api/v1/company/commercial-documents), audit events 1900-1904, S1a test classes (state machine, idempotency, branch scope, isolation, API, schema derivation), backend.yml generator step and registry raise, DECISIONS.md entries (incl. the deviation: snapshot columns are NOT in 0028 because the loader forbids DROP; S1b adds them additively), PR. NEXT: merge main, finish, build locally if SDK 10 is available, push, one Draft PR, independent review. |

Inert or blocked (do not merge; do not reopen resolved decisions): #51 and #55 SUPERSEDED by the merged owner-approved #61; #62 validation only; #48 BLOCKED on X1-X8, implementation preserved.

Agents (existing, none running): A pricing/3.13, B backup (4.3), C independent review, D backend/QA reviewer, E frontend. After the switch the owner's allocation for NEWLY spawned agents is: Lead Opus 5.5 Medium; A Sonnet 5.5 Medium; B Opus 5.5 High; C Opus 5.5 High; D Sonnet 5.5 Medium; E Sonnet 5.5 Medium; Fable 5.2 High may be considered for exceptionally complex accounting or architectural work. Existing agents' models have NOT been verified to change; they are not restarted merely to change model.

Migration reservations: company 0028 and system 0028 = Agent A (3.13 S1a; contiguous, only on `feature/phase-3-13-s1a`); system 0028 for Phase 4.1 enrolment is NOT available (4.1 enforcement is blocked; renumber when it unblocks). Registry on main 1037; each PR raises it by its own new PostgreSQL tests (raise-only).

Owner decisions: recorded D-048..D-052 (standing authorization; 3.13 Q1, Q7, Q8, Q15) in `docs/DECISIONS.md`. Under D-048 the lead applies the plan's recommended defaults for Q19, Q9 and the material-field list and records them when S1b starts. STILL OPEN with the owner: Phase 4.1 items 1-6 (MFA scope, TOTP key custody, recovery, emergency account, second approval, AD/Kerberos); Phase 4.3 audited restore acknowledgement with checkpoint epoch; decision R (attachment retention); relaxing the permanent 0026 discount tie index (loader DROP policy); adding the client-version assertion to `backend-selfhosted.yml`; 3.8C (OD-11), 3.8D (X1-X8); Q10, Q11, Q13 and the later 3.13 questions as their slices approach.

Next tasks after the owner authorizes resumption (approved, unblocked): finish and merge #78; 3.13 S1a then S1b (and the separate small reviewed PR adding the in-transaction `PostInWorkAsync` entry to 3.7B before S3); 4.3 orchestration slice; pricing frontend follow-ups recorded in the #80 record (blocked on paged/searchable list APIs); backend follow-up for a searchable party-role API and base-currency exposure; further scenario gap tests only where code exists.

## Open pull requests (current table: see the cloud-team handoff section for #75, #76, #77 and the backup follow-up)

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
| 4.1-1..6 | Phase 4.1 (OPEN): MFA enforcement scope; key protecting TOTP secrets; lost-authenticator recovery and audit; emergency account; second approval; optional AD/Kerberos | only the TOTP primitive (#72) exists; enrolment, enforcement and system migration 0028 are not built |
| 4.3-1 | Phase 4.3 (OPEN): audited restore acknowledgement with a checkpoint epoch | not built; recorded BLOCKED in docs/PHASE-4.3.md |
| AR | RESOLVED by the owner 2026-11-20: Arabic label for retiring an import template is "إيقاف استخدام القالب" | applied in #73 |
| C | Production-readiness: mandatory Rust `cargo audit` job (M6) | no Rust audit exists (pre-existing gap); adding a mandatory job changes protected gates, so it needs the owner |

## Medium follow-ups recorded in review records (no approval needed; schedule with the owning slice)

* DONE: #56 deactivated account/representative stays editable and the representative `UserId` membership check (#73, #74); #57 plaintext spooling, upload memory cap/concurrency, 403-vs-404 oracle and stale `*.att.tmp-*` sweep (#74); #63 retire UI for import templates and route-encoding tests (#73).
* OPEN: #56 free-text user GUID input and the raw representative id shown to viewers without `company.salesreps.view` (need a backend user lookup API); #57 encrypt while receiving (one plaintext copy is still held in memory); PR-69 M2 (pricing pickers' silent truncation, until a screen adopts `SearchPicker`); #70 Lows (scheduled in the backup follow-up).

## UNFINISHED Release 1 requirements (explicitly tracked; never silently omitted)

* **Customer groups (SRS 8.1, pricing by customer group):** in progress in #75 (company 0027); until it merges there is no group registry and pricing has no group scope. Plan in `docs/PHASE-3.11.md`.
* **Import-file rewrap retirability report** (decision K follow-up) and import-file key coverage in the backup manifest.
* **Backup/restore (SRS 14.4, 16):** slice 1 merged (#70: backup set, verify, isolated attachment restore, key coverage). Remaining: pg_dump/pg_restore coordination into an isolated target, scheduling, retention, encryption, RPO/RTO, full restore, and the blocked owner decisions (restore acknowledgement, decision R).
* **Production-readiness hardening:** decision S (bundler/Tauri inspection) and decision C (Rust `cargo audit`) need the owner (protected gates).

## Next development tasks

See "Next tasks after merges" in the cloud-team handoff section above (one slice at a time, each with an independent full-diff review).
