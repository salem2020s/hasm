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

Built but **not merged**: **3.7A** (inventory accounting configuration) on branch `feature/phase-3-7a-inventory-accounting`; no pull request yet.

## Remaining phases (summary)
Execution order is in `docs/RELEASE1-DEPENDENCIES.md`. Risk is the accounting and security risk of the phase.

| Phase | Title | Risk | Prerequisites | Migration implications |
|---|---|---|---|---|
| 3.7A | Inventory accounting configuration (first slice of 3.7) | High | 3.6 | company 0015, system 0017 |
| 3.7B | Atomic inventory posting (core) | Critical | 3.7A | company (posting links, state, receipt cost input), system (post permission) |
| 3.7C | Reversal, cancellation and period-close protection | Critical | 3.7B | company (if state transitions need columns) |
| 3.7D | Posting UI and capabilities | Medium | 3.7C | none |
| 3.8A | Stock balances and item ledger | High | 3.7D, 4.0 | none or read models only |
| 3.8B | Stocktaking, adjustments and the negative-stock exception | Critical | 3.8A | company, system |
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
| 4.0 | Verification infrastructure | Medium | 3.7A | none |
| 4.1 | Identity completion | High | 3.7D | system |
| 4.2 | Licensing and Trial | Critical | 3.17 | system |
| 4.3 | Backup and restore | Critical | 3.12 | none |
| 4.4 | Signed manual updates | High | 4.3 | none |
| 4.5 | Installer and deployment models | High | 4.1, 4.4 | none |
| 4.6 | Diagnostics, operations and audit retention | Medium | 4.5 | none |
| 4.7 | Desktop offline drafts | Medium | 3.17 | none |
| 4.8 | Localization and UX completion | Low | 3.26 | none |
| 5.1 | Performance and load validation | Medium | 4.8 | none |
| 5.2 | Security review and hardening | High | 4.6 | none |
| 5.3 | Final validation and Release 1 acceptance | Critical | 5.1, 5.2 | none |

## Definition of Done (applies to every phase)
1. Scope exactly as listed; nothing outside the approved SRS; open owner decisions (OD-n) resolved or explicitly deferred before the slice that needs them.
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
* **Notes:** BUILT on branch feature/phase-3-7a-inventory-accounting; full PostgreSQL regression running; not merged, not in a PR.

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
* **Notes:** Needs a design proposal first: the approved costing scope is item x warehouse (D-018); lot/serial dimensions must not break it (OD-11).

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
* **Notes:** Price definitions need OD-4.

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
* **Notes:** Representative model needs OD-3.

### Phase 3.9C — Party import adapter and reusable mapping templates
* **Exact approved scope:** SRS 14.1; RR-11
* **Prerequisites:** 3.9B
* **Deliverables:** Parties adapter on the 3.2D framework; reusable mapping templates saved per company and adapter, versioned, never bypassing validation, dry run or approval.
* **Acceptance criteria:** Acceptance criterion 11 of docs/REPORTING-REQUIREMENTS.md (import part).
* **Mandatory tests:** PostgreSQL import tests; web tests.
* **Database migration implications:** company
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Template scope needs OD-9.

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
* **Deliverables:** Shared life cycle with three independent tracks (commercial status, physical delivery/receipt, accounting posting), links between documents, partial fulfilment, numbering per type/branch/year, approval and re-approval, snapshot storage, idempotency keys, reuse of the 3.7 accounting-pending mechanism.
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
* **Acceptance criteria:** Scenario 21.1 for purchases; receipts cost from the invoice or receipt input; GRNI clears exactly.
* **Mandatory tests:** PostgreSQL end-to-end scenarios and concurrency; web tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Capitalization of freight-in, rebates and discounts needs OD-2.

### Phase 3.16 — Sales
* **Exact approved scope:** SRS 7.1, 7.2, 8.3, 8.4, 6.3
* **Prerequisites:** 3.13, 3.14, 3.11
* **Deliverables:** Quotations, sales orders, delivery documents, invoices, returns, cash/credit/mixed payment, discounts, taxes, currencies, sales representatives, price snapshots, cost of goods sold through posted costing, credit-limit hook.
* **Acceptance criteria:** Scenario 21.1; no duplicate stock/ledger effect; historic prices never recomputed.
* **Mandatory tests:** PostgreSQL end-to-end scenarios and concurrency; web tests.
* **Database migration implications:** company, system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 3.17 — Receivables and payables
* **Exact approved scope:** SRS 10.1-10.4, 5.5, 7.1; Scenario 21.6
* **Prerequisites:** 3.15, 3.16
* **Deliverables:** Customer and supplier ledgers, allocation of vouchers to invoices (manual and suggested), advances, unallocated amounts, un-allocation with audit, credit/debit notes, ageing, statements, credit-limit enforcement, reconciliation of control accounts to subledgers, close probes.
* **Acceptance criteria:** Linking never creates a second entry; subledger equals control account; ageing correct.
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
* **Notes:** Local policy/legislation needs OD-13.

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
* **Notes:** Needs OD-3, OD-4, OD-8.

### Phase 3.24 — Multi-company aggregation reports
* **Exact approved scope:** SRS 3.7, 3.8
* **Prerequisites:** 3.18, 3.23
* **Deliverables:** Backend aggregation across authorized companies with currency conversion; formal consolidation only if the owner decides it is part of Release 1.
* **Acceptance criteria:** No cross-database SQL; each company authorized separately.
* **Mandatory tests:** Integration tests across several company databases.
* **Database migration implications:** none
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** OD-1.

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
* **Exact approved scope:** SRS 18.4, 18.6
* **Prerequisites:** 3.7A
* **Deliverables:** Automated end-to-end browser tests of Web and Desktop smoke, dependency and secret scanning, documentation checks, aggregate required-check list, seeded reference-data generator, test-count registry maintained per phase.
* **Acceptance criteria:** A pull request touching documents only is still checked; E2E runs in CI.
* **Mandatory tests:** The infrastructure tests itself; no mandatory test is weakened.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Must precede 3.8.

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
* **Deliverables:** Signed licences, named/concurrent seats with heartbeat and lease, annual/perpetual entitlement, read-only on expiry, Trial limits, support-window metadata, activation online/offline.
* **Acceptance criteria:** Trial limits cannot be bypassed from either client; drafts are kept after expiry.
* **Mandatory tests:** PostgreSQL concurrency and security tests.
* **Database migration implications:** system
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase
* **Notes:** Transaction definition OD-12.

### Phase 4.3 — Backup and restore
* **Exact approved scope:** SRS 14.4, 16.1, 16.2, 16.4; Scenarios 21.8, 21.10
* **Prerequisites:** 3.12
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
* **Prerequisites:** 4.8
* **Deliverables:** Reference environment, reference data, 60-minute load, P95 measurement.
* **Acceptance criteria:** Measured against SRS targets; anything not run is recorded as NOT VERIFIED.
* **Mandatory tests:** Load scripts and recorded results.
* **Database migration implications:** none
* **Security and accounting risk:** Medium
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 5.2 — Security review and hardening
* **Exact approved scope:** SRS 17, 4.5
* **Prerequisites:** 4.6
* **Deliverables:** Security review of authentication, authorization, isolation, uploads, reports, designers, licence and updates; fixes.
* **Acceptance criteria:** No open Critical/High finding.
* **Mandatory tests:** Security test suite and review record.
* **Database migration implications:** none
* **Security and accounting risk:** High
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

### Phase 5.3 — Final validation and Release 1 acceptance
* **Exact approved scope:** SRS 18.4, 18.5; docs/RELEASE1-ACCEPTANCE.md
* **Prerequisites:** 5.1, 5.2
* **Deliverables:** Full regression, scenario suite 21.1-21.10, traceability closure, visual review, recorded open items.
* **Acceptance criteria:** docs/RELEASE1-ACCEPTANCE.md satisfied; unverified items marked NOT VERIFIED.
* **Mandatory tests:** Everything above.
* **Database migration implications:** none
* **Security and accounting risk:** Critical
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase

## Owner decisions needed before the slice that needs them (not conflicts; ambiguities in the approved documents)
| ID | Question | Needed by |
|---|---|---|
| OD-1 | SRS 3.8 distinguishes simple aggregation from formal consolidation: is formal consolidation (account maps, intercompany, eliminations) part of Release 1 or only the aggregation reports? | 3.24 |
| OD-2 | Capitalization of freight-in, purchase returns, rebates and discounts into inventory cost (recorded as decided by the inventory module in the earlier roadmap). | 3.15 |
| OD-3 | Sales-representative model (user, employee or party). | 3.9B, 3.23 |
| OD-4 | Definitions of "consumer selling price" and "last purchase price" for historical dates. | 3.8E |
| OD-5 | Trading/Cost-of-Sales statement layout for service companies and its account classification. | 3.8E |
| OD-6 | Comparison basis for statements (previous year, period, budget) and rounding/presentation rules. | 3.23 |
| OD-7 | Export size limits and background export jobs. | 3.23 |
| OD-8 | Treatment of tax-inclusive prices in "net sales excluding tax". | 3.23 |
| OD-9 | Storage and sharing of reusable import mapping templates (company or user). | 3.9C |
| OD-10 | Trial attachment quota default (the SRS leaves the number to the implementation). | 4.2 |
| OD-11 | Lot/serial tracking versus the approved costing scope of item x warehouse (design proposal). | 3.8C |
| OD-12 | Exact definition of a "posted business transaction" for the Trial limit (automatic entries excluded by the SRS). | 4.2 |
| OD-13 | Company policy and legislation for cheque intermediate entries and returns. | 3.19 |
| OD-14 | Risk classes for approval policies beyond those built in 3.2C. | 3.13 |

Where an OD is not answered in time the owner-approved default is: do not implement the affected slice; implement unaffected slices; report the blocker.

## Tensions to resolve in the phase that meets them (documents agree in substance but their wording can mislead)
* **T-1** ARCHITECTURE section 7 forbids writing "Posted" on an entry that is not actually posted, while Phase 3.7B plans a stock event completed with a balanced **draft** journal in an explicit accounting-pending state (SRS 7.4 allows this). The document state names in 3.7B must say what happened (stock effect done, accounting pending) and never use a state that claims the ledger is posted.
* **T-2** SRS 15.6 counts "posted business transactions" for the Trial while the same document separates stock events from accounting posting; the counted unit is fixed in OD-12 before 4.2.
* **T-3** The earlier roadmap listed production key provisioning for import files as open; it is scheduled in 3.12 with attachments (shared key management), before any production use of imports.
* **T-4** The cost-of-sales statement is "not applicable" until inventory data exists (REPORTING-REQUIREMENTS); it activates in 3.8E.

No two approved requirements were found that contradict each other.

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

