# HASM — Release 1 implementation roadmap (authoritative phase sequence)

Status of this file: authoritative, dependency-ordered plan for all remaining approved Release 1 work, rebuilt by the roadmap audit (see `docs/RELEASE1-TRACEABILITY.md`, `docs/RELEASE1-DEPENDENCIES.md`, `docs/RELEASE1-ACCEPTANCE.md`). It adds phases and statuses; it does not change the approved SRS, ARCHITECTURE or DECISIONS, and it introduces no requirement outside them. Phases not listed under "Delivered" are **not implemented**.

Status words: *merged* = on `main` with CI green on the pull request head (checked against GitHub); *built* = on a branch only; *planned* = nothing exists. "Verified" is reserved for Phase 5.3.

## Delivered (merged to main; verified against GitHub on 2026-11-15)
| Phase | Content | PR / merge commit |
|---|---|---|
| 2.1-2.3C | System database, provisioning, identity, RBAC, security hardening, cryptographic audit | #4-#9 |
| 3.1A-1, 3.1A-2 | Company runtime foundation; company profile, settings, currencies; first frontend | #10 (runtime), #11 (`31a2b9c`) |
| UI-1 | Visual design and UX foundation | #12 (`8fbd161`) |
| 3.1B | Branch management | #13 (`20e3e30`) |
| 3.1C | Warehouse management | #14 (`9f3cf69`) |
| 3.1D | Fiscal years and periods | #15 (`83e8113`) |
| 3.2A | Chart of accounts foundation | #16 (`f145f00`) |
| 3.2A-2 | Business-type chart templates (Service 54, Retail 66, Wholesale 64 accounts) | #20 (`2d50ada`) |
| 3.2B | General ledger and journal posting | #17 (`d2d2a11`) |
| 3.2C | General ledger completion (cost-center policy, approval policies, year-end close) | #18 (`b23013d`) |
| 3.2D / 3.2A-3 | Import framework and chart Excel import | #19 (`c34f99c`) |
| 3.3 | Excel/PDF export, financial statements, ledger and trial balance completion | #21 (`85035d1`) |
| 3.4 / 3.4B | Inventory master data and tabs standard; inventory master-data import | #24 (`d4038ed`), #25 (`5f194d6`) |
| 3.5 | Stock movement documents up to approval (non-posting) | #26 (`4ffc4ec`) |
| 3.6 | Inventory costing engine (not wired to any document) | #27 (`2d2f187`, head `e212a49`, 861 PostgreSQL tests) |
| policy docs | GitHub persistence/PR/CI-repair policies; autonomous tracker | #22, #23, #28 |

Merged after the roadmap audit: **3.7A** inventory accounting configuration (PR #31, merge commit `1b27ad2`). Merged: **4.0** verification infrastructure (PR #32, merge commit `7e05052`). In review: **3.7B** (PR #33).

## Remaining phases (summary)
Execution order is in `docs/RELEASE1-DEPENDENCIES.md`. Risk is the accounting and security risk of the phase.

| Phase | Title | Risk | Prerequisites | Migration implications |
|---|---|---|---|---|
| 3.7A | Inventory accounting configuration (first slice of 3.7) — **MERGED (PR #31, merge commit 1b27ad2)** | High | 3.6 | company 0015, system 0017 |
| 3.7B | Atomic inventory posting (core) — **MERGED (PR #33, merge commit 5e4d627)** | Critical | 3.7A | company (posting links, state, receipt cost input), system (post permission) |
| 3.7C | Reversal, cancellation and period-close protection — **MERGED (PR #35, merge commit 484b4be)** | Critical | 3.7B | company (if state transitions need columns) |
| 3.7D | Posting UI and capabilities — **MERGED (PR #37, merge commit ab60b55)** | Medium | 3.7C | none |
| 3.8A | Stock balances and item ledger — **MERGED (PR #40, merge commit 9c43fc9)** | High | 3.7D, 4.0 | none or read models only |
| 3.8B | Stocktaking, adjustments and the negative-stock exception — **SLICES 3.8B-1 (PR #42, 84dbb4e) and 3.8B-2 (PR #43, 981e991) MERGED; slice 3.8B-3 (negative-stock exception) DESIGNED (docs/PHASE-3.8B-3-DESIGN.md, revision 3, two independent reviews), NOT implemented** | Critical | 3.8A | company, system |
| 3.8C | Lots, serial numbers, expiry and reservations | High | 3.8A | company |
| 3.8D | Back-dated movements, impact preview, recalculation, method change and revaluation | Critical | 3.7C, 3.8A | company |
| 3.8E | Inventory reports and cost-of-sales statement | Medium | 3.8A, 3.8D | none |
| 3.9A | Party registry (shared identity and company-local record) | Critical | 3.7D | system, company |
| 3.9B | Customers and suppliers (company-local accounts) | Medium | 3.9A | company, system |
| 3.9C | Party import adapter and reusable mapping templates | Medium | 3.9B | company |
| 3.10 | Tax engine | Critical | 3.2B | company, system |
| 3.11 | Pricing and discount engine | High | 3.4, 3.9B | company, system |
| 3.12 | Attachments and key management | High | 3.2A | company |
| 3.13 | Commercial document framework | Critical | 3.7C, 3.9B, 3.10, 3.11 | company |
| 3.14 | Cash and bank accounts, vouchers and transfers | High | 3.13 | company, system |
| 3.15 | Purchasing | Critical | 3.13, 3.14 | company, system |
| 3.16 | Sales | Critical | 3.13, 3.14, 3.11 | company, system |
| 3.17 | Receivables and payables | Critical | 3.15, 3.16 | company |
| 3.18 | Foreign-exchange differences | Critical | 3.17 | company |
| 3.19 | Cheques | Critical | 3.14, 3.17 | company, system |
| 3.20 | Bank reconciliation | High | 3.14, 3.17, 3.19, 3.9C | company |
| 3.21 | Recurring documents and scheduler | High | 3.15, 3.16, 3.17 | company |
| 3.22 | Inter-branch settlements | High | 3.17 | company |
| 3.23 | Operational reports | Medium | 3.17, 3.14, 3.8E | none |
| 3.24 | Multi-company aggregation reports | High | 3.18, 3.23 | none |
| 3.25 | Visual report designer | High | 3.23 | company |
| 3.26 | Document and print designer | Medium | 3.15, 3.16, 3.12 | company |
| 3.27 | Opening balances and legacy migration | Critical | 3.17, 3.8B, 3.9C | company |
| 4.0 | Verification infrastructure — **MERGED (PR #32, merge commit 7e05052; full-stack E2E, Desktop smoke test are NOT built; a GitHub ruleset ProtectMain was added on 2026-11-16 once the repository became public)** | Medium | 3.7A | none |
| 4.1 | Identity completion | High | 3.7D | system |
| 4.2 | Licensing and Trial | Critical | 3.17 | system |
| 4.3 | Backup and restore | Critical | 3.12, 3.9A | none |
| 4.4 | Signed manual updates | High | 4.3 | none |
| 4.5 | Installer and deployment models | High | 4.1, 4.4 | none |
| 4.6 | Diagnostics, operations and audit retention | Medium | 4.5 | none |
| 4.7 | Desktop offline drafts | Medium | 3.17 | none |
| 4.8 | Localization and UX completion | Low | 3.26 | none |
| 5.1 | Performance and load validation | Medium | 4.8, 3.7A, 3.7B, 3.7C, 3.7D, 3.8A, 3.8B, 3.8C, 3.8D, 3.8E, 3.9A, 3.9B, 3.9C, 3.10, 3.11, 3.12, 3.13, 3.14, 3.15, 3.16, 3.17, 3.18, 3.19, 3.20, 3.21, 3.22, 3.23, 3.24, 3.25, 3.26, 3.27, 4.0, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7 | none |
| 5.2 | Security review and hardening | High | 4.6, 3.7A, 3.7B, 3.7C, 3.7D, 3.8A, 3.8B, 3.8C, 3.8D, 3.8E, 3.9A, 3.9B, 3.9C, 3.10, 3.11, 3.12, 3.13, 3.14, 3.15, 3.16, 3.17, 3.18, 3.19, 3.20, 3.21, 3.22, 3.23, 3.24, 3.25, 3.26, 3.27, 4.0, 4.1, 4.2, 4.3, 4.4, 4.5, 4.7, 4.8 | none |
| 5.3 | Final validation and Release 1 acceptance | Critical | 5.1, 5.2, 3.7A, 3.7B, 3.7C, 3.7D, 3.8A, 3.8B, 3.8C, 3.8D, 3.8E, 3.9A, 3.9B, 3.9C, 3.10, 3.11, 3.12, 3.13, 3.14, 3.15, 3.16, 3.17, 3.18, 3.19, 3.20, 3.21, 3.22, 3.23, 3.24, 3.25, 3.26, 3.27, 4.0, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.8 | none |

## Definition of Done (applies to every phase)
1. Scope exactly as listed; nothing outside the approved SRS; OPEN decisions (OD-n) that block it decided or explicitly deferred before the slice that needs them.
2. One shared React frontend delivered to Web and Tauri (no platform gap); Arabic RTL and English LTR; tabs standard where applicable.
3. Server-side authorization, company/branch/warehouse scoping, audit, optimistic concurrency, validation; no frontend-only security.
4. Migrations: fresh creation and upgrade tested; opaque physical names; append-only/immutability where the data is historical; nothing destructive on non-test databases; destructive tests only on the disposable cluster.
5. Automated tests for the acceptance criteria (accounting and inventory invariants, concurrency, authorization, idempotency); CI expected test counts updated, never reduced.
6. Frontend typecheck, production build and Tauri build check pass; documentation (phase document, roadmap, traceability) updated.
7. Pull request with exact-head CI green, adversarial self-review and independent review recorded, merge only under the master directive gates; checkpoint recorded.

## Phase definitions

### Phase 3.7A — Inventory accounting configuration (first slice of 3.7)
* **Exact approved scope:** SRS 7.5, 9.3, 17.4; ARCHITECTURE 7
* **Prerequisites:** 3.6
* **Deliverables:** Versioned immutable mapping of inventory accounts per item group and receipt/issue counter accounts; permissions; API; shared UI. Posts nothing.
* **Acceptance criteria:** Accounts validated against the chart (active, postable, asset, no foreign-currency-only); history immutable; unconfigured company cannot post later; unchanged save creates no version.
* **Mandatory tests:** PostgreSQL (7), web (6), unit fixtures; CI expected count updated.
* **Database migration implications:** company 0015, system 0017
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** BUILT; in review as PR #31 on branch feature/phase-3-7a-inventory-accounting. Evidence: CI on head 1df50aa passed (Backend 868 PostgreSQL tests all passed, Frontend, Desktop); two local full runs on the disposable cluster: the first had 2 failures (a test-order flaw in the new concurrent-save test, fixed, and one failure of JournalAccessTests.Every_journal_operation_is_a_separate_permission_and_posting_and_approval_are_never_implied that was NOT reproduced in 12 isolated runs, nor in the second full run (912 of 912 passed), nor in CI; cause still unknown and the test stays under observation, not declared fixed). Local total 912 = 868 counted by CI + 44 SafetyGateTests excluded by the registry. Independent review (separate agent) found no Blocker/High; Medium/Low findings fixed (group error row index, note-only change recorded, configure implies view); CI count now 869.

### Phase 3.7B — Atomic inventory posting (core)
* **Exact approved scope:** SRS 4.4, 7.4, 9.3, 9.6, 9.7, 17.4, 19.2, 19.5; ARCHITECTURE 5, 7, 8
* **Prerequisites:** 3.7A
* **Deliverables:** Posted state and link tables (posting record, cost operation, journal, explicit accounting state); authorized warehouse-scoped post permission and action; revalidation (items, units, active warehouses, open fiscal period, configured accounts); authorized per-line receipt cost input; costing through the costing service with key stock_document:<id>; balanced draft journal through the automatic journal path; one company transaction; idempotency; audit; API.
* **Acceptance criteria:** Quantity, cost and accounting state commit together or not at all; a document posts at most once under concurrent attempts; no quantity-only posting; no posting into Final Closed periods; journal total equals the cost operation total; nothing is hard-coded.
* **Mandatory tests:** PostgreSQL: atomicity with fault injection, concurrent double post, receipt/issue/transfer, FIFO and average, period gate, unconfigured accounts, permission and warehouse scope, audit, transaction vs recording date, fresh and upgrade migration; unit: allocation arithmetic.
* **Database migration implications:** company (posting links, state, receipt cost input), system (post permission)
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Terminology: the stock event may be 'posted' while the journal is still a pending draft; the document state names must not claim an accounting posting that has not happened (ARCHITECTURE 7).

### Phase 3.7C — Reversal, cancellation and period-close protection
* **Exact approved scope:** SRS 5.5, 9.6, 19.3; Scenario 21.4
* **Prerequisites:** 3.7B
* **Deliverables:** Reversal/cancellation of posted stock documents through reversal cost operations and reversing journals with reference chains; resolution of the accounting-pending state when the journal is approved, posted or rejected; real replacement of the inventory_valuation_differences close probe; period close refuses while pending states exist.
* **Acceptance criteria:** Posted documents are never edited; a reversal posts once; a period with pending inventory accounting cannot close; closing probe is real, not 'not applicable'.
* **Mandatory tests:** PostgreSQL: reversal idempotency and concurrency, pending-state transitions, close refusal, reversal in closed period refused, audit; scenario 21.4.
* **Database migration implications:** company (if state transitions need columns)
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.7D — Posting UI and capabilities
* **Exact approved scope:** SRS 13.5; UI standard
* **Prerequisites:** 3.7C
* **Deliverables:** Shared Web/Tauri screens: post action, receipt cost input, accounting-state display, reversal, links to journal; tabs standard; Arabic/English.
* **Acceptance criteria:** Same behaviour on Web and Tauri; permission-driven controls; errors mapped to messages; keyboard accessible.
* **Mandatory tests:** Shared component tests, typecheck, production build, Tauri build check, backend integration regression.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.8A — Stock balances and item ledger
* **Exact approved scope:** SRS 9.1, 9.3, 13.1
* **Prerequisites:** 3.7D, 4.0
* **Deliverables:** Quantity and value on hand per item x warehouse, item ledger (movement history), authorization applied before pagination and totals, reconciliation to costing positions and to the inventory ledger accounts, screens.
* **Acceptance criteria:** Opening + incoming - outgoing = closing; balances reconcile to the General Ledger inventory accounts; unauthorized warehouses contribute nothing.
* **Mandatory tests:** PostgreSQL reconciliation and authorization tests; web tests.
* **Database migration implications:** none or read models only
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.8B — Stocktaking, adjustments and the negative-stock exception
* **Exact approved scope:** SRS 9.1, 9.4
* **Prerequisites:** 3.8A
* **Deliverables:** Stocktake documents, variance, adjustment documents posted through the 3.7 machinery; limited authorized negative-stock exception for moving-average items with provisional cost and later settlement; never for FIFO, lot or serial items.
* **Acceptance criteria:** Adjustments post once with accounting state; exception refused for ineligible items and without permission; settlement restores exact cost.
* **Mandatory tests:** PostgreSQL: variance posting, exception eligibility, provisional cost settlement, concurrency.
* **Database migration implications:** company, system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.8C — Lots, serial numbers, expiry and reservations
* **Exact approved scope:** SRS 9.1, 9.4
* **Prerequisites:** 3.8A
* **Deliverables:** Tracking by lot, serial and expiry through documents, costing and balances; reservations.
* **Acceptance criteria:** Quantities per lot/serial reconcile to item x warehouse positions; expired stock identifiable; reservations never exceed available stock.
* **Mandatory tests:** PostgreSQL consistency and concurrency tests; web tests.
* **Database migration implications:** company
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Needs a design proposal first: the approved costing scope is item x warehouse (D-018); lot/serial dimensions must not break it (OD-11: costing rule already approved).

### Phase 3.8D — Back-dated movements, impact preview, recalculation, method change and revaluation
* **Exact approved scope:** SRS 9.2, 9.5, 9.7; Scenario 21.5
* **Prerequisites:** 3.7C, 3.8A
* **Deliverables:** Impact analysis and cost preview for back-dated documents, recalculation of affected FIFO layers/averages across chained warehouses, separate adjustment documents, controls for changing the valuation method with revaluation, closed-period barriers.
* **Acceptance criteria:** Original entries untouched; adjustments are separate documents approved before posting; Final Closed periods refused.
* **Mandatory tests:** PostgreSQL: scenario 21.5, chained transfers, FIFO and average, closed-period refusal, reproducibility.
* **Database migration implications:** company
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.8E — Inventory reports and cost-of-sales statement
* **Exact approved scope:** RR-1, RR-2, RR-10, SRS 13.1
* **Prerequisites:** 3.8A, 3.8D
* **Deliverables:** Inventory movement report; stocktaking and valuation as of a date (official value separate from informational prices); inventory and cost reports of SRS 13.1; cost-of-sales statement activated; Excel/PDF through the shared export engine.
* **Acceptance criteria:** Acceptance criteria 1 and 2 of docs/REPORTING-REQUIREMENTS.md; a report run changes no stored cost.
* **Mandatory tests:** PostgreSQL correctness and authorization; export content equals screen; web tests in both languages.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Price definitions: default in OD-4.

### Phase 3.9A — Party registry (shared identity and company-local record)
* **Exact approved scope:** SRS 3.3, 3.4, 17.4, 19.4; Scenario 21.3
* **Prerequisites:** 3.7D
* **Deliverables:** GlobalPartyId in the system database, LocalPartyId in the company database, central proposal and approval of shared master data, reliable request log (outbox) and reconciliation for cross-database operations.
* **Acceptance criteria:** No cross-company visibility; shared identity never exposes balances; retry after a failure creates no duplicate.
* **Mandatory tests:** PostgreSQL: isolation, outbox retry and reconciliation, approval workflow, audit.
* **Database migration implications:** system, company
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.9B — Customers and suppliers (company-local accounts)
* **Exact approved scope:** SRS 7.1, 10.1
* **Prerequisites:** 3.9A
* **Deliverables:** Customer and supplier roles, terms, credit-limit data, control-account mapping, sales-representative model, screens.
* **Acceptance criteria:** Control accounts validated against the chart; permissions; audit.
* **Mandatory tests:** PostgreSQL and web tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Representative model: default in OD-3.

### Phase 3.9C — Party import adapter and reusable mapping templates
* **Exact approved scope:** SRS 14.1; RR-11
* **Prerequisites:** 3.9B
* **Deliverables:** Parties adapter on the 3.2D framework; reusable mapping templates saved per company and adapter, versioned, never bypassing validation, dry run or approval.
* **Acceptance criteria:** Acceptance criterion 11 of docs/REPORTING-REQUIREMENTS.md (import part).
* **Mandatory tests:** PostgreSQL import tests; web tests.
* **Database migration implications:** company
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Template scope: approved in OD-9.

### Phase 3.10 — Tax engine
* **Exact approved scope:** SRS 6.1-6.4, 19.8
* **Prerequisites:** 3.2B
* **Deliverables:** Dated rates, classifications of operations/items/parties, several taxes, exemption and zero rate, inclusive/exclusive, base, rounding and priority rules, immutable snapshot model, extension point for national localization.
* **Acceptance criteria:** Deterministic exact decimal results; snapshot never changes when rules change; no country-specific compliance.
* **Mandatory tests:** Unit (arithmetic matrix) and PostgreSQL tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.11 — Pricing and discount engine
* **Exact approved scope:** SRS 8.1-8.4, 4.2
* **Prerequisites:** 3.4, 3.9B
* **Deliverables:** Price lists, deterministic priority by customer, group, quantity, unit, currency, date; rule trace; authorized overrides with reason; margin policy; snapshot contract.
* **Acceptance criteria:** Same inputs always give the same price and a recorded rule; override permissions enforced.
* **Mandatory tests:** Unit (priority matrix) and PostgreSQL tests; web tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.12 — Attachments and key management
* **Exact approved scope:** SRS 14.3, 14.4, 17.1, 17.2
* **Prerequisites:** 3.2A
* **Deliverables:** Server-side file storage (disk/SMB/NFS), metadata in the company database, hash integrity, backend-only access with authorization, application-level encryption for sensitive files; production key provisioning, rotation and recovery for attachments and import-file encryption (open item of Phase 3.2D).
* **Acceptance criteria:** No path traversal; company isolation; encrypted files unreadable without keys; key rotation and recovery proven.
* **Mandatory tests:** Security tests (traversal, isolation, tampering), PostgreSQL metadata tests, key rotation drill.
* **Database migration implications:** company
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.13 — Commercial document framework
* **Exact approved scope:** SRS 4.3, 4.4, 7.2, 7.3, 7.6, 19.2, 19.3, 19.7, 21.1
* **Prerequisites:** 3.7C, 3.9B, 3.10, 3.11
* **Deliverables:** Shared life cycle with three independent tracks (commercial status, physical delivery/receipt, accounting posting), links between documents, partial fulfilment, numbering per type/branch/year, approval and re-approval, snapshot storage, idempotency keys, reuse of the 3.7 accounting-pending mechanism; a shared posted-business-transaction counting hook (a no-op until Phase 4.2 gives it the Trial definition of OD-12) that every later posting phase calls; document-level foreign-currency rule: documents may be created and posted at their dated document rate, while settling or allocating them across currencies or rates is refused (fail closed) until Phase 3.18.
* **Acceptance criteria:** A business event creates exactly one stock and one ledger effect; separate and direct modes both supported.
* **Mandatory tests:** PostgreSQL: duplicate prevention under concurrency, state-machine, numbering, approval.
* **Database migration implications:** company
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.14 — Cash and bank accounts, vouchers and transfers
* **Exact approved scope:** SRS 3.5, 10.1; RR-4 source
* **Prerequisites:** 3.13
* **Deliverables:** Cashboxes and bank accounts per branch, receipt and payment vouchers (initially unallocated), transfers between cash/bank accounts, ledger integration, approval.
* **Acceptance criteria:** Voucher posts once; balances reconcile to the ledger; branch authorization.
* **Mandatory tests:** PostgreSQL and web tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.15 — Purchasing
* **Exact approved scope:** SRS 7.1, 7.2, 7.5, 6.3, 8.4
* **Prerequisites:** 3.13, 3.14
* **Deliverables:** Purchase orders, goods receipts (posting through 3.7), supplier invoices (direct and separate modes), purchase returns, GRNI, tax and price snapshots.
* **Acceptance criteria:** Scenario 21.1 for purchases; receipts cost from the invoice or receipt input; GRNI clears exactly; foreign-currency settlement refused (fail closed) until 3.18; counting hook called on posting.
* **Mandatory tests:** PostgreSQL end-to-end scenarios and concurrency; web tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Purchase-cost composition is OPEN decision OD-2 (proposal recorded).

### Phase 3.16 — Sales
* **Exact approved scope:** SRS 7.1, 7.2, 8.3, 8.4, 6.3
* **Prerequisites:** 3.13, 3.14, 3.11
* **Deliverables:** Quotations, sales orders, delivery documents, invoices, returns, cash/credit/mixed payment, discounts, taxes, currencies, sales representatives, price snapshots, cost of goods sold through posted costing, credit-limit hook.
* **Acceptance criteria:** Scenario 21.1; no duplicate stock/ledger effect; historic prices never recomputed; foreign-currency settlement refused (fail closed) until 3.18; counting hook called on posting.
* **Mandatory tests:** PostgreSQL end-to-end scenarios and concurrency; web tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.17 — Receivables and payables
* **Exact approved scope:** SRS 10.1-10.4, 5.5, 7.1; Scenario 21.6
* **Prerequisites:** 3.15, 3.16
* **Deliverables:** Customer and supplier ledgers, allocation of vouchers to invoices (manual and suggested), advances, unallocated amounts, un-allocation with audit, credit/debit notes, ageing, statements, credit-limit enforcement, reconciliation of control accounts to subledgers, close probes.
* **Acceptance criteria:** Linking never creates a second entry; subledger equals control account; ageing correct; allocation across different currencies or rates refused (fail closed) until 3.18; counting hook called on posting.
* **Mandatory tests:** PostgreSQL: allocation, duplicate prevention, retry, reconciliation, authorization.
* **Database migration implications:** company
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.18 — Foreign-exchange differences
* **Exact approved scope:** SRS 5.7, 10.3, 5.6, 19.8
* **Prerequisites:** 3.17
* **Deliverables:** Realized and unrealized difference accounting, revaluation, rate source recorded, year-end close and opening balances for companies holding foreign-currency balances.
* **Acceptance criteria:** Foreign-currency companies can close a year; exact base amounts; differences documented.
* **Mandatory tests:** PostgreSQL and unit arithmetic tests.
* **Database migration implications:** company
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.19 — Cheques
* **Exact approved scope:** SRS 11.1, 11.2
* **Prerequisites:** 3.14, 3.17
* **Deliverables:** Incoming and outgoing cheque life cycle, entries, links to parties/invoices/receivables, due dates, notices, reports.
* **Acceptance criteria:** Every state change creates at most one entry; reports reconcile.
* **Mandatory tests:** PostgreSQL state-machine and reconciliation tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Per-company policy settings (OD-13).

### Phase 3.20 — Bank reconciliation
* **Exact approved scope:** SRS 11.3, 11.4; Scenario 21.6
* **Prerequisites:** 3.14, 3.17, 3.19, 3.9C
* **Deliverables:** Manual and smart reconciliation from CSV/Excel through the import framework, matching suggestions, partial/multiple matches, separate adjustment documents for fees and differences, approval.
* **Acceptance criteria:** Matching never duplicates a recorded receipt entry; differences are separate approved documents.
* **Mandatory tests:** PostgreSQL matching and duplicate tests.
* **Database migration implications:** company
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.21 — Recurring documents and scheduler
* **Exact approved scope:** SRS 12.1-12.4, 19.5; Scenario 21.7
* **Prerequisites:** 3.15, 3.16, 3.17
* **Deliverables:** Templates and schedules, drafts by default, conditional auto approval/posting honouring permissions, separation of duties and periods, idempotent runs, history, pause, alerts.
* **Acceptance criteria:** A run never creates the same cycle document twice; failures kept with reason; no bank transfers.
* **Mandatory tests:** PostgreSQL idempotency and gate tests.
* **Database migration implications:** company
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.22 — Inter-branch settlements
* **Exact approved scope:** SRS 3.6
* **Prerequisites:** 3.17
* **Deliverables:** Accounting settlements between branches of one company; branch financial reports.
* **Acceptance criteria:** Entries balance per branch and in total; authorization by branch.
* **Mandatory tests:** PostgreSQL tests.
* **Database migration implications:** company
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.23 — Operational reports
* **Exact approved scope:** RR-3 to RR-7, RR-12, SRS 13.1, 13.2
* **Prerequisites:** 3.17, 3.14, 3.8E
* **Deliverables:** Customer statement, cash movement, invoice movement, daily transactions, profitability (gross), ageing, sales, purchases, tax, bank, cheque, branch and cost-centre reports on the shared export engine.
* **Acceptance criteria:** Acceptance criteria 3 to 7 and 12 of docs/REPORTING-REQUIREMENTS.md.
* **Mandatory tests:** PostgreSQL correctness/authorization; export equals screen; both languages.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Uses the defaults of OD-3, OD-4 and OD-8.

### Phase 3.24 — Multi-company aggregation reports
* **Exact approved scope:** SRS 3.7, 3.8
* **Prerequisites:** 3.18, 3.23
* **Deliverables:** Backend aggregation across authorized companies with currency conversion; formal consolidation only if the owner decides it is part of Release 1.
* **Acceptance criteria:** No cross-database SQL; each company authorized separately.
* **Mandatory tests:** Integration tests across several company databases.
* **Database migration implications:** none
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** OPEN decision OD-1 limits only the consolidation part.

### Phase 3.25 — Visual report designer
* **Exact approved scope:** SRS 13.3
* **Prerequisites:** 3.23
* **Deliverables:** Designer reusing the stable report definitions: fields, filters, grouping, formulas, charts, PDF/XLSX, authorization-safe execution.
* **Acceptance criteria:** Cannot bypass database permissions or company isolation.
* **Mandatory tests:** Security tests, designer tests, both languages.
* **Database migration implications:** company
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.26 — Document and print designer
* **Exact approved scope:** SRS 13.4
* **Prerequisites:** 3.15, 3.16, 3.12
* **Deliverables:** Templates for invoices and documents, A4/A5/thermal, barcode, languages, historical template versions.
* **Acceptance criteria:** A printed historic document reproduces its original template.
* **Mandatory tests:** Rendering tests and visual checks.
* **Database migration implications:** company
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.27 — Opening balances and legacy migration
* **Exact approved scope:** SRS 14.1, 14.2
* **Prerequisites:** 3.17, 3.8B, 3.9C
* **Deliverables:** Opening-balance adapter (draft journals through normal approval), opening receivables/payables, opening stock, optional full-history import, reusable templates, final import approval.
* **Acceptance criteria:** No duplicate documents or balances on re-run; reconciliation to source totals.
* **Mandatory tests:** PostgreSQL import tests with reconciliation.
* **Database migration implications:** company
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 4.0 — Verification infrastructure
* **Exact approved scope:** SRS 18.4, 18.5, 18.6; policy: CLAUDE.md autonomous CI repair
* **Prerequisites:** 3.7A
* **Deliverables:** STATUS (PR #32, in review): BUILT and covered by tools/verify self-tests: (1) always-on documentation/traceability check, (3 partly) Playwright browser smoke tests of the Web bundle, (4) dependency and secret scanning, (5) review-record validator, (6) CI-weakening guard (registry counts, weakening patterns by occurrence, paths filters, renames/deletions, test-inventory decreases, skipped tests, protected files), (7) head-SHA verifier (paginated, GitHub Actions app only). NOT BUILT: Desktop smoke test, full-stack E2E with a real API and PostgreSQL, the seeded reference-data generator skeleton, a GitHub ruleset that enforces these checks (needs a plan with that API), and any binding of the owner approval label to a commit. Original scope follows. (1) Always-on documentation and traceability workflow: regenerates docs from tools/roadmap and fails on drift (node tools/roadmap/generate.js --check), validates links, required files and that every SRS requirement id is present exactly once. (2) Backend and PostgreSQL regression: the existing build-test and postgres-integration jobs stay and are covered by the required-check manifest. (3) Frontend and Desktop verification: existing typecheck, tests, production build and Tauri build stay; Web end-to-end tests (browser) and a Desktop smoke test are added. (4) Dependency vulnerability scanning and secret scanning on every PR. (5) Independent code-review support: a review-record template bound to the PR head SHA and a checklist script; it supports a separate reviewing context but cannot create one. (6) Protection against weakened CI: a guard that fails a PR which lowers the expected PostgreSQL test count, removes or skips tests, deletes or disables a required job, or edits the required-check manifest without an owner-labelled approval. (7) Head-SHA verification: a script that lists the required checks from the manifest and proves each is green on the exact PR head SHA before any merge, refusing when checks are pending, missing or stale. (8) Seeded reference-data generator skeleton and test-count registry.
* **Acceptance criteria:** Each control demonstrably fails on a deliberately broken sample (lowered count, skipped test, drifted doc, planted fake secret, red check on an older SHA) and passes on a clean PR; a docs-only PR is checked; no mandatory check is weakened.
* **Mandatory tests:** Self-tests of every guard with negative cases; workflow dry runs on a throwaway branch; recorded evidence in the PR.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Must precede 3.8; the independent-review control remains a process gate that the tooling can only support (G-4, G-5).

### Phase 4.1 — Identity completion
* **Exact approved scope:** SRS 4.1, 4.5
* **Prerequisites:** 3.7D
* **Deliverables:** TOTP MFA, optional Active Directory/Kerberos, protected emergency account, second approval for sensitive administrative operations.
* **Acceptance criteria:** MFA enforced where configured; emergency account audited.
* **Mandatory tests:** PostgreSQL, security and web tests.
* **Database migration implications:** system
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 4.2 — Licensing and Trial
* **Exact approved scope:** SRS 15.1-15.7, 19.6
* **Prerequisites:** 3.17
* **Deliverables:** Signed licences, named/concurrent seats with heartbeat and lease, annual/perpetual entitlement, read-only on expiry, Trial limits, support-window metadata, activation online/offline; Trial restrictions of SRS 15.6 (basic modules only: no designers, multi-currency, cheques or company aggregation; read and export after the limit; upgrade preserves data); one count per session regardless of browser tabs (SRS 15.2); the Trial transaction counter wired to the hook of 3.13 and the counted-document contract of OD-12 for every later posting phase (3.19, 3.20, 3.21, 3.22, 3.27 integrate with it in their own PRs).
* **Acceptance criteria:** Trial limits and module restrictions cannot be bypassed from either client; drafts are kept after expiry; tabs of one session are not double counted; upgrading a Trial company keeps its data.
* **Mandatory tests:** PostgreSQL concurrency and security tests.
* **Database migration implications:** system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Counted unit is OPEN decision OD-12 (proposal recorded).

### Phase 4.3 — Backup and restore
* **Exact approved scope:** SRS 14.4, 16.1, 16.2, 16.4; Scenarios 21.8, 21.10
* **Prerequisites:** 3.12, 3.9A
* **Deliverables:** Manual and scheduled backups, retention, destinations, encryption, monitoring, single-company and full restore with attachments, trial restore, measured RPO/RTO.
* **Acceptance criteria:** Restore drill passes on a disposable cluster; other companies and the system database are untouched.
* **Mandatory tests:** Restore drills in CI where feasible; recorded manual drills otherwise.
* **Database migration implications:** none
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 4.4 — Signed manual updates
* **Exact approved scope:** SRS 16.5
* **Prerequisites:** 4.3
* **Deliverables:** Signed packages, compatibility and migration preflight, backup requirement, safe rollback, client compatibility.
* **Acceptance criteria:** Tampered package refused; failed upgrade rolls back.
* **Mandatory tests:** Upgrade tests across versions.
* **Database migration implications:** none
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 4.5 — Installer and deployment models
* **Exact approved scope:** SRS 2.2, 2.4-2.7, 17.1
* **Prerequisites:** 4.1, 4.4
* **Deliverables:** Installer and first-run wizard for the three deployment models, PostgreSQL local or external, certificates and secrets, first administrator and company, health checks, signed desktop package.
* **Acceptance criteria:** Clean install and upgrade succeed on each model.
* **Mandatory tests:** Install and upgrade tests on clean machines or virtual machines.
* **Database migration implications:** none
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 4.6 — Diagnostics, operations and audit retention
* **Exact approved scope:** SRS 17.3, 17.6; D-041
* **Prerequisites:** 4.5
* **Deliverables:** Redacted logs, health, alerts, support bundle, audit export/retention and protected external copy, key rotation runbook.
* **Acceptance criteria:** No secret in any log or bundle.
* **Mandatory tests:** Redaction tests, audit-chain verification after export.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 4.7 — Desktop offline drafts
* **Exact approved scope:** SRS 19.6; ARCHITECTURE 11; D-040
* **Prerequisites:** 3.17
* **Deliverables:** Secure local draft storage in the Tauri client, resubmission with server revalidation; no offline posting.
* **Acceptance criteria:** No ledger or stock effect without the server.
* **Mandatory tests:** Desktop adapter tests and shared interaction tests.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 4.8 — Localization and UX completion
* **Exact approved scope:** SRS 13.5-13.7, 2.1; Scenario 21.9
* **Prerequisites:** 3.26
* **Deliverables:** Complete Arabic/English audit, shortcuts, menu completeness, Web/Desktop parity audit.
* **Acceptance criteria:** No untranslated string; identical behaviour on Web and Desktop.
* **Mandatory tests:** Automated translation-key and parity checks.
* **Database migration implications:** none
* **Security and accounting risk:** Low
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 5.1 — Performance and load validation
* **Exact approved scope:** SRS 18.1-18.3, 16.4
* **Prerequisites:** 4.8, 3.7A, 3.7B, 3.7C, 3.7D, 3.8A, 3.8B, 3.8C, 3.8D, 3.8E, 3.9A, 3.9B, 3.9C, 3.10, 3.11, 3.12, 3.13, 3.14, 3.15, 3.16, 3.17, 3.18, 3.19, 3.20, 3.21, 3.22, 3.23, 3.24, 3.25, 3.26, 3.27, 4.0, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7
* **Deliverables:** Reference environment, reference data, 60-minute load, P95 measurement.
* **Acceptance criteria:** Measured against SRS targets; anything not run is recorded as NOT VERIFIED.
* **Mandatory tests:** Load scripts and recorded results.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 5.2 — Security review and hardening
* **Exact approved scope:** SRS 17, 4.5
* **Prerequisites:** 4.6, 3.7A, 3.7B, 3.7C, 3.7D, 3.8A, 3.8B, 3.8C, 3.8D, 3.8E, 3.9A, 3.9B, 3.9C, 3.10, 3.11, 3.12, 3.13, 3.14, 3.15, 3.16, 3.17, 3.18, 3.19, 3.20, 3.21, 3.22, 3.23, 3.24, 3.25, 3.26, 3.27, 4.0, 4.1, 4.2, 4.3, 4.4, 4.5, 4.7, 4.8
* **Deliverables:** Security review of authentication, authorization, isolation, uploads, reports, designers, licence and updates; fixes.
* **Acceptance criteria:** No open Critical/High finding.
* **Mandatory tests:** Security test suite and review record.
* **Database migration implications:** none
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 5.3 — Final validation and Release 1 acceptance
* **Exact approved scope:** SRS 18.4, 18.5; docs/RELEASE1-ACCEPTANCE.md
* **Prerequisites:** 5.1, 5.2, 3.7A, 3.7B, 3.7C, 3.7D, 3.8A, 3.8B, 3.8C, 3.8D, 3.8E, 3.9A, 3.9B, 3.9C, 3.10, 3.11, 3.12, 3.13, 3.14, 3.15, 3.16, 3.17, 3.18, 3.19, 3.20, 3.21, 3.22, 3.23, 3.24, 3.25, 3.26, 3.27, 4.0, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.8
* **Deliverables:** Full regression, scenario suite 21.1-21.10, traceability closure, visual review, recorded open items.
* **Acceptance criteria:** docs/RELEASE1-ACCEPTANCE.md satisfied; unverified items marked NOT VERIFIED.
* **Mandatory tests:** Everything above.
* **Database migration implications:** none
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

## Roadmap ambiguities: resolutions and open decisions
Every ambiguity found by the audit was checked against the approved SRS, DECISIONS, ARCHITECTURE, REPORTING-REQUIREMENTS and the merged phase documents. **APPROVED** = an approved document already answers it (nothing is reopened). **DEFAULT** = minor implementation ambiguity, documented default adopted. **OPEN** = material accounting, inventory, security or licensing question: a proposal and its consequences are recorded, the owner decides, and the blocked phases are named. No requirement is changed by this section.

| ID | Status | Topic | Question | Basis in approved documents | Resolution / proposal and consequence | Blocks |
|---|---|---|---|---|---|---|
| OD-1 | OPEN | Formal consolidation | SRS 3.8 separates simple aggregation of companies from formal consolidation (account maps, intercompany, eliminations, FX adjustments). Is formal consolidation a Release 1 deliverable? | SRS 3.7 (aggregation in the backend) is mandatory. SRS 3.8 describes what formal consolidation would require but no SRS section, decision (D-001..D-043) or acceptance scenario lists it as a deliverable. ARCHITECTURE 5 mentions account maps only 'where needed'. | Proposal: Release 1 delivers aggregation (with an optional account map when companies use different charts) and does NOT schedule eliminations or intercompany processing. Consequence: no consolidated statements with eliminations in Release 1; adding them later does not change posted data because aggregation reads posted ledgers only. | 3.24 (consolidation part only; aggregation reports proceed) |
| OD-2 | OPEN | Composition of purchase cost | Are freight-in, rebates and purchase discounts capitalized into inventory cost? | The SRS never mentions landed cost or cost allocation of charges; SRS 7.1 lists discounts and taxes on documents; D-017/D-018 approve only the two valuation methods at item x warehouse. The engine (3.6) takes an exact receipt amount per line. | Proposal (revised after the owner directive that freight and purchase-cost adjustments must not be excluded merely because they are not yet supported): nothing is excluded. Phase 3.7B takes the EXACT receipt amount per line entered by the authorized poster, so a poster can already include allocated freight, duties and discounts in it; the engine stores that amount unchanged. Phase 3.15 designs freight-in, duties, rebates and purchase discounts as explicit, audited purchase-cost adjustments (allocation rules by value, quantity or weight, applied to the receipt lines of the document or as a later adjustment operation through the costing engine with its own accounting entry), and 3.8D provides revaluation. The accounting treatment (which expense or payable account each charge uses, and what happens to charges after the goods were sold) is a material accounting rule that the owner confirms before 3.15 builds it. | 3.15 (purchase-cost adjustment slice only; 3.7B is not blocked because the receipt amount is explicit per line) |
| OD-3 | DEFAULT | Sales representative model | Is a sales representative a user, an employee or a party? | SRS 7.1 names representatives; payroll/HR are out of scope (SRS 1); nothing defines the entity. | Default: a company-local representative record (code, Arabic/English name, active flag, optional link to a user); no commission calculation. Documents reference it; reports filter by it. | none (3.9B builds it; 3.16 and 3.23 consume it) |
| OD-4 | DEFAULT | Historical selling price and last purchase price | Definitions for 'consumer selling price' and 'last purchase price' as of a past date. | REPORTING-REQUIREMENTS 5.1 leaves them to implementation; both are informational (acceptance criterion 2). | Default: selling price = the default price list price in force on the as-of date for the item base unit in base currency (3.11); last purchase price = unit cost of the latest posted purchase line dated on or before the as-of date, company-wide, in base currency. Every displayed value is labelled with its date and basis; neither changes any stored cost. | none (3.8E) |
| OD-5 | APPROVED | Trading / cost-of-sales statement | Layout and classification of the Trading account for service companies. | Phase 3.3 (merged, PR #21) already built the Trading Account/Profit and Loss with a company-level versioned statement classification (company migration 0011); REPORTING-REQUIREMENTS 1 row 10 says it is 'not applicable' without inventory, never zero. | No new decision: the existing classification decides; 3.8E only activates the cost-of-sales figures from posted inventory data and keeps 'not applicable' for companies without inventory. | none |
| OD-6 | DEFAULT | Comparison basis and presentation | Comparison basis (previous year, period, budget) and rounding of statements. | docs/PHASE-3.3.md: comparatives are the previous fiscal period and previous fiscal year, actual values only, in the company's own calendar. SRS contains no budget requirement. | Default: the built comparatives (previous period, previous year) are kept; no budget comparison is scheduled because the SRS contains no budget requirement (REPORTING-REQUIREMENTS section 5 item 3 leaves the basis open). New operational reports reuse the same period resolution. | none |
| OD-7 | DEFAULT | Export limits and background export | Export size limits and background jobs. | docs/PHASE-3.3.md: XLSX/CSV default maximum 500,000 rows (Hasm:Reports:MaxExportRows), PDF 50,000 (MaxPdfRows), refused before a file is produced; the export is synchronous. | The limits are decided and built (3.3). Default for the rest: exports stay synchronous because 3.3 built them that way and nothing in the SRS asks for background jobs; if a reference-data export exceeds the limit it fails with a clear message. Revisit after the 5.1 measurements. | none |
| OD-8 | DEFAULT | Net sales excluding tax | Treatment of tax-inclusive prices in 'net sales excluding tax components'. | SRS 6.1 (inclusive/exclusive) and 6.3 (immutable snapshot of base, rates and results on the final document). | Default: net sales = the net (pre-tax) amount stored in each document's tax snapshot (SRS 6.3), after discounts and returns; no recomputation from today's rules. REPORTING-REQUIREMENTS section 5 item 6 keeps the tax-setup detail open until 3.10 defines the snapshot. | none (3.10 defines the snapshot, 3.23 reads it) |
| OD-9 | DEFAULT | Scope of import mapping templates | Are reusable mapping templates per company or per user? | REPORTING-REQUIREMENTS 3 item 11 requires reusable mapping templates that never bypass validation, dry run or approval; section 5 item 8 lists company-versus-user storage as undecided. | Default: templates are saved per company and per import adapter, versioned and audited, and applying one never skips validation, dry run or approval; per-user templates are not built. Changeable by a normal decision record. | none |
| OD-10 | APPROVED | Trial attachment quota | Numeric Trial attachment quota. | SRS 15.7 and 20, DECISIONS 'configurable details': a licence-policy value whose default the implementation chooses and publishes; it does not block approval. | Not a decision: 4.2 picks, documents and tests a default; it stays configurable. | none |
| OD-11 | OPEN | Lot/serial tracking versus item x warehouse costing | How lots and serial numbers coexist with the approved costing scope. | D-017 approves only FIFO and weighted moving average; D-018 fixes the scope at item x warehouse; D-019 forbids negative stock for lot/serial items; SRS 9.1 requires lots, serials and expiry. | Proposal (consequential accounting rule, owner confirmation requested before 3.8C design approval): cost keeps being computed per item x warehouse by the company/group method; a lot or serial is a quantity-tracking dimension whose balances must reconcile to that position. Specific-identification costing is NOT approved, so choosing a lot never changes the cost drawn. Consequence: a FIFO item issued from a newer lot still consumes the oldest cost layer. 3.8C still starts with a written design proposal reviewed against these rules. | 3.8C (design record only; 3.8A, 3.8B and 3.8D do not need it) |
| OD-12 | OPEN | Definition of a posted business transaction for the Trial limit | What counts toward the 100 posted business transactions? | SRS 15.6: 100 posted business transactions; automatic entries are not counted as additional transactions. The unit is not defined; importing opening balances and reversals are not mentioned. | Proposal: one count per posted business document (manual journal entry, stock document, sales or purchase document, voucher, cheque event, bank adjustment); automatic journals generated for a counted document, reversals of a counted document, imports of opening data and recurring-run drafts are not counted until they post as their own business document. Consequence: the Trial cannot be exhausted by internal side effects, and the counter lives server-side in the company database. | 4.2 |
| OD-13 | APPROVED | Cheque intermediate entries and legislation | Which intermediate entries and returned-cheque settlements apply? | SRS 11.2: 'according to company policy and applicable legislation'. | The rule is a per-company configurable policy (intermediate accounts, return handling) validated against the chart, with a default set of accounts; no legislation-specific rule is hard-coded. Chart templates already reserve cheque accounts for the Cheques module. | none (3.19 designs the policy settings) |
| OD-14 | DEFAULT | Risk classes for approval policies | Which risk classifications may conditions use? | SRS 4.3 (type, value, branch, risk); the conditions already built in 3.2C (type, amount, branch, manual rate override, soft-closed period, year-end class); risk events named elsewhere in the SRS: price/discount override (8.2), margin below minimum (8.3), negative-stock exception (9.4), back-dated posting (9.5), credit limit exceeded (7.1). | Default (an interpretation of SRS 4.3): each module adds the risk conditions its SRS clauses name, using the existing ordered-stage policy engine; other classes are added only by a decision record. | none |
| T-1 | OPEN | State naming when stock is done but the journal is a pending draft | ARCHITECTURE 7 forbids writing 'Posted' on an entry that is not posted, while SRS 7.4 allows an explicit accounting-pending state. | SRS 7.4, 19.7; ARCHITECTURE 7 (document state, stock effect and accounting effect are separate states). | Proposal for the 3.7B design record: document status Approved -> Executed (stock and cost effect committed), with a separate accounting state pending -> posted -> reversed (or failed); the word 'posted' belongs to the journal and the accounting state only. Consequence: no state ever claims a ledger posting that has not happened; reports and period close read the accounting state. | 3.7B (naming only; the mechanism is unchanged) |
| T-2 | OPEN | Counted unit of the Trial limit | Same question as OD-12 seen from SRS 15.6 versus the separated state tracks. | See OD-12. | Resolved together with OD-12. | 4.2 |
| T-3 | APPROVED | Production key provisioning for import files | Open item recorded after Phase 3.2D. | docs/ROADMAP.md (previous) and docs/PHASE-3.2D.md. | Scheduled in Phase 3.12 (shared key management with attachments) and must be complete before production use of imports. | 3.12 |
| T-4 | APPROVED | Cost-of-sales statement before inventory data | Statement availability. | REPORTING-REQUIREMENTS 1 row 10. | Reported as 'not applicable', never zero, until inventory data exists; activated in 3.8E. | none |

Until an OPEN item is decided, only the blocked slice waits; unaffected work continues and the blocker is reported with the work preserved. No two approved requirements were found that contradict each other.

## Autonomous-development readiness
See `docs/RELEASE1-DEPENDENCIES.md` ("Autonomous workflow readiness") for the gaps found, the added verification milestones (VM-1 to VM-8) and Phase 4.0.

---

# Appendix A — carried-over notes recorded before this audit
The text below is preserved from the previous roadmap so that no earlier decision is lost. Its status columns are superseded by "Delivered" above and by `docs/RELEASE1-TRACEABILITY.md`; the notes themselves still apply.

## Owner-approved reporting and Excel requirements (2026-11-14)
Twelve mandatory Release 1 reporting requirements (inventory movement, stocktaking/valuation as of a date, customer statement, cash movement, invoice movement, daily transactions, profitability, General Ledger and Trial Balance completion, financial statements, flexible Excel import/export, shared standards) are recorded, scheduled by existing phase or source module, and given acceptance criteria in `docs/REPORTING-REQUIREMENTS.md`. Ledger-based reports and the shared export engine belong to Phase 3.3; operational reports are delivered with the phase of their source module (inventory, sales, purchases, receivables/payables, cash); reusable import mapping templates are delivered with the first operational import adapter. None is implemented by this entry; existing basic reports (CSV trial balance and general ledger) are not counted as complete.

## Remaining Release 1 requirements after Phase 3.2C (NOT complete; none is implemented in 3.2B or 3.2C)
| Item | State |
|---|---|
| Cost-center allocation policy: per-account requirement, optional branch eligibility, cost-center-scoped access and the exact carry of cost-center balances into opening balances are DONE in 3.2C; a role-assignment screen for cost-center scope remains open | partly done (3.2C) |
| Risk-based and ordered multi-level approval policies | done in 3.2C S2 (conditions: type, amount, branch, manual rate override, soft-closed period, year-end class; ordered stages; other risk classifications need an owner decision) |
| Year-end closing entries and opening-balance carryforward | done in 3.2C S3 and the final review for base-currency balances (mandatory independent approval, no line limit, exact dimension carry); **NOT complete for companies holding foreign-currency balances**: they stay blocked (fail closed) until the FX revaluation / realized and unrealized difference accounting below exists |
| Realized and unrealized foreign-exchange differences | not started (with receivables, payables and banking) |
| Business-type Chart of Accounts templates (3.2A-2) | DONE (merged, PR #20). Remaining dependencies: inventory account mappings and the capitalization of freight-in, purchase returns, rebates and discounts are decided by the Inventory module; customer and supplier control accounts must reconcile with their subledgers and bank accounts with the banking module; cheque accounts and an optional account-extension workflow for existing charts belong to the Cheques module; realized/unrealized FX accounts belong to the FX phase |
| Excel Chart of Accounts import (3.2A-3) | DONE (merged with 3.2D, PR #19) |
| Excel and PDF export of the ledger reports and statements | DONE in Phase 3.3 (pending merge) |
| Visual Report Designer (SRS section 13) | not started (Phase 3.3 provides stable report definitions only) |
| Operational reports in `docs/REPORTING-REQUIREMENTS.md` (inventory, customer statement, cash, invoices, daily transactions, profitability) | not started (need their source modules) |
| Reusable import mapping templates and the opening-balance import adapter (Draft journals through the normal journal approval; import approval never replaces it) | not started (3.2D delivers the framework and the Chart adapter only) |
| Secure production key provisioning for import-file encryption, key rotation (a rewrap procedure exists; the operational runbook and key-directory provisioning on production do not), and backup/recovery of keys and encrypted originals | not started; must be done before production use of imports |
| Report designer (SRS section 13) and PDF export | not started |
| Taxes, sales and purchases, inventory and costing, receivables and payables, cheques, bank reconciliation, recurring documents, legacy migration, licensing, backup and the remaining operational modules (SRS sections 6 to 16) | not started |

Inventory close check: `inventory_valuation_differences` is "not applicable" only while no inventory posting capability or data exists; the inventory phase MUST replace it with a real probe before inventory history can be closed. Posting limitation to respect (3.2C): year-end closing and opening balances refuse any foreign-currency balance-sheet position or foreign-currency-tied account (no FX revaluation or realized/unrealized difference exists yet); a company holding such balances cannot close the year in the application until the FX policy of the receivables, payables and banking phases exists. Cost centers and other analytic dimensions are not carried into opening balances. Business-type templates, Excel imports and Excel/PDF exports are not available.

Rules: one phase at a time; no Warehouse or Fiscal-Period work inside 3.1B; no inter-branch accounting entries or branch financial reports before their accounting modules exist (the branch model must leave room for them).
