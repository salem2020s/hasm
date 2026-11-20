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

## Cloud team handoff (reconciled 2026-11-21 against GitHub; lead = Claude Cloud; recover from here after a reset or usage limit)

`main` = `83343c8`. Leadership passed from the local CLI to the cloud lead after PR #69 (`3bbf66a`). The cloud sandbox has no .NET SDK, so CI is the compiler. GitHub GraphQL is blocked there, so `gh pr view`-based tooling fails: exact-head verification is done by running the UNMODIFIED `main` copy of `tools/verify/verify-head.js` with `--input` built from REST data (PR files and check-runs of the exact head SHA, GitHub Actions app id), drafts are made ready through the CCR `ready_for_review` route and merges use the REST merge endpoint with the head SHA pinned. Gates per merge (Autonomous merge policy): independent full-diff review recorded for the code head, `verify-head` green on the exact merged head, mergeable, no Critical/High, no protected file touched. Records name the reviewed code head; the final commit of a PR may only change its record (verified by diff).

Merged by the cloud team: #73 `9896373` (frontend follow-ups; Arabic retire label "إيقاف استخدام القالب" applied), #72 `e0d3a76` (4.1 slice 1: TOTP primitive only), #74 `62ade0d` (attachment/representative Medium follow-ups; registry 1025 to 1029), #70 `c0fc2f4` (4.3 slice 1: backup set, verify, isolated attachment restore, key coverage), #71 `83343c8` (Release 1 QA test plan, `docs/qa/RELEASE1-TEST-PLAN.md`).

| PR | Branch (owner agent) | Status | Remaining before merge |
|----|----------------------|--------|------------------------|
| #75 | `feature/phase-3-11-customer-groups` (A) | company migration 0027; CI being fixed | A's fixes and C's schema findings, CI green, independent review of the head |
| #77 | `feature/qa-gap-tests-1` (D) | 2 PostgreSQL tests (cross-company pricing, concurrent edit); registry 1029 to 1031; first CI run failed one test from a test-setup omission, fixed | CI green on the fixed head, independent review (C) |
| #76 | `docs/handoff-cloud-team` (lead) | this file | review, merge |
| (new) | `feature/phase-4-3-backup-restore-2` (B) | follow-up Lows of #70 and pg_dump/pg_restore coordination into an isolated CI target | PR, CI, review |

Agent C reviews each final head read-only. Agent E has no approved, unblocked task (the rest needs a UX decision). Migration reservations: company 0027 = #75 (A); company 0028 unassigned; system 0028 reserved for Phase 4.1 enrolment (blocked by the decisions below). Shared `backend.yml` PostgreSQL registry on main = 1029; each PR raises it by its own new tests (raise-only) and the second PR to merge takes a trivial `main` merge.

Superseded or inert (do not merge, do not reopen): #51 and #55 (security proposals) are SUPERSEDED by the merged, owner-approved #61 (merge `215da56`); #62 is validation only. #48 (`feature/phase-3-8d-impact-preview`, head `1e8f477`) stays BLOCKED on owner decisions X1-X8, implementation preserved, no execution; any migration it adds is numbered from the next free numbers on rebase.

Owner decisions recorded: Arabic retire label (applied, #73). OPEN, Phase 4.1 (fail-closed; only the TOTP primitive exists): (1) MFA enforcement scope and whether it can be disabled; (2) key protecting TOTP secrets; (3) lost-authenticator recovery and its audit; (4) emergency account (creation, use, custody, alerting, relation to MFA); (5) second approval (operations, approver, expiry); (6) optional AD/Kerberos. OPEN, Phase 4.3: audited restore acknowledgement with a checkpoint epoch; retention/purge of removed attachments (decision R).

Behaviour changes now on main (#74, reversible): attachment ceiling 100 MB (was 512 MB); a caller who cannot see an attachment's owning record gets 404 (was 403); uploads beyond the gate get 503 `upload_busy` / 408 `upload_timeout`; Kestrel minimum data rate 16 KB/s on the upload route.

Recorded follow-ups: quote must not trust client `itemGroupId`/`unitCost` when 3.13 seals documents; pricing customer/unit/group pickers lack truncation notices and PR-69 M2 stays open until a screen adopts the shared `SearchPicker`; 4.1 enforcement slice needs CAS replay, throttling/lockout, key-ring secret and no-URI-logging tests; #70 Lows (seam, header-size classification, conservative fake-key-id test, manifest back-compat, RealPath) are scheduled in the B follow-up; import-file keys are not covered by the backup manifest.

Next tasks after merges (approved, unblocked): A pricing follow-ups, then 3.13 once 3.11 is complete; B 4.3 follow-up then 4.4 (needs 4.3); D further scenario gap tests that need no decision. Blocked: 3.8C (OD-11), 3.8D (X1-X8), 4.1 enforcement, Phase 4.0 full-stack E2E (protected CI area; larger design).

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
