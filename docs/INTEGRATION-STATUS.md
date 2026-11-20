# HASM - Integration status and handoff

Maintained by the lead agent; the authoritative record of what is open, how it depends on what, and what is waiting for the owner. Recover from this file after a context reset (then `git fetch`, `gh pr list`, `gh pr checks`). Last verified against GitHub: **2026-11-20**, `main` = `84614cf`. Routine PRs are merged autonomously once every gate of the Autonomous merge policy holds (owner directive 2026-11-20, recorded in `CLAUDE.md` by PR #60).

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

Migrations on `main`: **company 0001-0025, system 0001-0026** (contiguous). PostgreSQL registry: 1011 tests (`.github/workflows/backend.yml`, raise-only). Next free numbers: company 0026, system 0027. PR #48's branch predates these merges (its tree ends at company 0021 and system 0023); when it is rebased onto `main`, any migration it adds must be numbered from company 0026 / system 0027 and its pinned counts re-reconciled (checked against its head `1e8f477` on 2026-11-20).

## Open pull requests

| PR | Branch | Status |
|----|--------|--------|
| #48 | `feature/phase-3-8d-impact-preview` | BLOCKED on owner decisions X1-X8 (X1 would relax a database safeguard); internal replay checkpoint, execution disabled; number any migration from company 0026 / system 0027 on rebase |
| #51, #55 | security proposals | SUPERSEDED and inert (applied by #61); not to be merged; may be closed by the owner |
| #62 | `integration/chain-with-security` | VALIDATION ONLY; never merge |
| #59 | `docs/integration-status` | this file (docs only) |

## Owner-decision register (each recorded once; fail-closed while open)

| ID | Decision | Effect while open |
|----|----------|-------------------|
| X1-X8 | Phase 3.8D decisions | #48 stays a checkpoint; no financial execution; no safeguard changed |
| OD-11 | Phase 3.8C lots/serials/expiry/reservations | 3.8C not started |
| P | Pricing selection defaults (scope before priority, one discount no stacking, item before group, quantity tier as floor; `docs/PHASE-3.11.md`) | pricing persistence (3.11 slice 2) does not start |
| R | Retention/purge of removed attachments | bytes of removed attachments are kept |
| Q | Per-company attachment quota and malware scanning | not built |
| K | Key retirement for removed sensitive attachments (review of #57 M1) | removed sensitive files stay under the old key, which must stay available; fail-closed `KeyUnavailable` |
| S | Production-readiness: bundler/Tauri inspection hardening (aliases outside node_modules, externals, workers, platform Tauri configs and `TAURI_CONFIG`, build hooks, installer/executable embedding; `docs/review-records/PR-security-picomatch-apply.md` M1-M5) | recorded residual risk; existing gates unchanged |
| C | Production-readiness: mandatory Rust `cargo audit` job (M6) | no Rust audit exists (pre-existing gap); adding a mandatory job changes protected gates, so it needs the owner |

## Medium follow-ups recorded in review records (no approval needed; schedule with the owning slice)

* #56: validate a sales representative's `UserId` against system membership before any feature uses it; test that a role whose account/representative was later deactivated stays editable.
* #57: spool of plaintext multipart parts to the OS temp directory (deployment requirement: encrypted temp disk, or stream); lower hard cap/concurrency on upload memory; 403-vs-404 oracle; sweep stale `*.att.tmp-*`.
* #63: retire UI for import templates; route-encoding tests for odd names.

## Next development tasks

1. Verify `main` CI on `84614cf` and record it.
2. 3.9C slice 2: the party import adapter (roles and attachments are now on `main`).
3. Backup/restore coordination for attachment folders and key directory (SRS 14.4, 16).
4. 3.13 commercial document framework prerequisites: 3.7C, 3.9B (done), 3.10 (done), 3.11 (needs owner decision P for slice 2).
5. Do not open new feature PRs until open work is reviewed; one slice at a time, each with an independent full-diff review.
