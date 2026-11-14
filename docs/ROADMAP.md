# HASM — Implementation Roadmap (company structure phases)

Status of this file: records the owner-confirmed phase sequence (confirmed 2026-11-13; decided in earlier requirements discussions and not previously transferred into the repository). It does not change the approved SRS, ARCHITECTURE or DECISIONS.

## Delivered
| Phase | Content | State |
|---|---|---|
| 2.1-2.3C | System database, provisioning, identity, RBAC, security hardening, cryptographic audit | merged |
| 3.1A-1 | Company runtime foundation (least-privilege company roles, schema gate, business audit) | merged |
| 3.1A-2 | Company profile, settings, currencies, history; first frontend | merged (PR #11) |
| UI-1 | Visual design and UX foundation | merged (PR #12) |

## Approved sequence (functional)
| Phase | Content | State |
|---|---|---|
| 3.1B | Branch Management — see `docs/PHASE-3.1B.md` | merged (PR #13) |
| 3.1C | Warehouse Management — see `docs/PHASE-3.1C.md` | merged (PR #14) |
| 3.1D | Fiscal Years and Periods — see `docs/PHASE-3.1D.md` | merged (PR #15) |
| 3.2A | Chart of Accounts foundation — see `docs/PHASE-3.2A.md` | merged (PR #16) |
| 3.2A-2 | Business-type Chart of Accounts templates (mandatory Release 1; see `docs/PHASE-3.2A.md`) | not started |
| 3.2A-3 | Chart of Accounts Excel import: validation, dry run, reconciliation, required approval (mandatory Release 1; no empty-chart-only restriction, merge and conflict rules defined with the import) | not started |
| 3.2B | General Ledger and journal posting — see `docs/PHASE-3.2B.md` (slices S1 numbering, S2 exchange rates, S3 cost centers, S4 approval policies, S5 journal and posting, S6 ledger reports, S7 shared UI) | merged (PR #17, merge commit `d2d2a11`, includes the journal-list branch-filter fix `29fadd9`) |

## Remaining Release 1 requirements after Phase 3.2B (NOT complete; none is implemented in 3.2B)
| Item | State |
|---|---|
| Cost-center allocation policy refinements (mandatory cost center per line/account, branch matching, per-user restrictions) | open, owner decision required |
| Risk-based and ordered multi-level approval policies | open, owner decision required (3.2B counts distinct approvers only) |
| Year-end closing entries and opening-balance carryforward | not started |
| Realized and unrealized foreign-exchange differences | not started (with receivables, payables and banking) |
| Business-type Chart of Accounts templates (3.2A-2) | not started |
| Excel Chart of Accounts import (3.2A-3) | not started |
| Remaining report export formats required by the approved SRS (Excel, PDF; 3.2B provides CSV only) | not started |

Posting limitation to respect until these exist: manual and automatic journals are safe for same-period, single-entry postings, but there is no year-end close or opening-balance carryforward, no FX revaluation or settlement differences, and approval counts distinct approvers without ordering or risk classes. Do not rely on 3.2B for those workflows.

Rules: one phase at a time; no Warehouse or Fiscal-Period work inside 3.1B; no inter-branch accounting entries or branch financial reports before their accounting modules exist (the branch model must leave room for them).
