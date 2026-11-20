# HASM - Integration status and handoff

Maintained by the lead agent; the authoritative record of what is open, how it depends on what, and what is waiting for the owner. Recover from this file after a context reset (then `git fetch`, `gh pr list`, `gh pr checks`). Last verified against GitHub: **2026-11-20**, `main` = `219af25` (PR #68). Hosted CI on `40228b2` verified green; CI on later `main` commits is NOT VERIFIED here unless stated. Routine PRs are merged autonomously once every gate of the Autonomous merge policy holds (owner directive 2026-11-20, recorded in `CLAUDE.md` by PR #60).

## Merged on 2026-11-20 (each on its exact head, verify-head green, independent review recorded)

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

Migrations on `main`: **company 0001-0026, system 0001-0027** (contiguous). PostgreSQL registry: 1022 tests on `main` (1025 on the 3.11 slice 2c branch) (`.github/workflows/backend.yml`, raise-only). Next free numbers: company 0027, system 0028. PR #48's branch predates these merges (its tree ends at company 0021 and system 0023); when it is rebased onto `main`, any migration it adds must be numbered from company 0027 / system 0028 and its pinned counts re-reconciled (checked against its head `1e8f477` on 2026-11-20).

## Open pull requests

| PR | Branch | Status |
|----|--------|--------|
| #48 | `feature/phase-3-8d-impact-preview` | BLOCKED on owner decisions X1-X8 (X1 would relax a database safeguard); internal replay checkpoint, execution disabled; number any migration from company 0027 / system 0028 on rebase |
| #51, #55 | security proposals | SUPERSEDED and inert (applied by #61); not to be merged; may be closed by the owner |
| #62 | `integration/chain-with-security` | VALIDATION ONLY; never merge |
| #67 | `docs/handoff-after-3-9c` | this handoff (docs only; the table excludes the PR that carries it once merged) |

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
| C | Production-readiness: mandatory Rust `cargo audit` job (M6) | no Rust audit exists (pre-existing gap); adding a mandatory job changes protected gates, so it needs the owner |

## Medium follow-ups recorded in review records (no approval needed; schedule with the owning slice)

* #56: validate a sales representative's `UserId` against system membership before any feature uses it; test that a role whose account/representative was later deactivated stays editable.
* #57: spool of plaintext multipart parts to the OS temp directory (deployment requirement: encrypted temp disk, or stream); lower hard cap/concurrency on upload memory; 403-vs-404 oracle; sweep stale `*.att.tmp-*`.
* #63: retire UI for import templates; route-encoding tests for odd names.

## UNFINISHED Release 1 requirements (explicitly tracked; never silently omitted)

* **Customer groups (SRS 8.1, pricing by customer group):** no customer-group registry exists; pricing has no group scope yet. Plan in `docs/PHASE-3.11.md` (master + assignment, group scope on price lists and discounts, quote and screens, tests). Release 1 is not complete without it.
* **Import-file rewrap retirability report** (decision K follow-up) and the other Medium follow-ups recorded in the review records (#56, #57, #63, #66).
* **Backup/restore coordination** for attachment folders and the key directory (SRS 14.4, 16): tested procedure.
* **Production-readiness hardening:** decision S (bundler/Tauri inspection) and decision C (Rust `cargo audit`) need the owner (protected gates).

## Next development tasks

1. Merge 3.11 slice 2c (pricing API and screens; branch `feature/phase-3-11-pricing-ui`) after review and exact-head CI.
2. Customer-group registry and its pricing integration (above).
3. Import-file rewrap retirability report; backup/restore coordination; attachment follow-ups from the #57 review.
4. 3.13 commercial document framework once 3.7C and the pricing slices are in.
5. One slice at a time, each with an independent full-diff review.
