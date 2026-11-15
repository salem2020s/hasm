# HASM — Release 1 requirements traceability matrix

Status: planning record produced by the Release 1 roadmap audit (documentation only; no application code was changed). Authoritative sources: `docs/SRS.md` (approved baseline v1.0), `docs/DECISIONS.md`, `docs/ARCHITECTURE.md`, `docs/REPORTING-REQUIREMENTS.md`. Companion documents: `docs/ROADMAP.md` (phases), `docs/RELEASE1-DEPENDENCIES.md`, `docs/RELEASE1-ACCEPTANCE.md`.

## How requirement identifiers are derived

The SRS has no numbered requirement identifiers. An identifier here is **section.n**, where *section* is the SRS section number and *n* is the 1-based position of the bullet (or numbered item) inside that section, counted in document order. Sections 2 to 19 contain the functional and non-functional requirements; section 21 contains ten acceptance scenarios (21.1 to 21.10); section 1 (scope), section 20 (configurable details) and section 22 (approval) are not requirements and are referenced where relevant. Reporting requirements are `RR-1` to `RR-12`, numbered as the owner numbered them in `docs/REPORTING-REQUIREMENTS.md` (RR-n is item n there). Counts below are computed from those documents by the generator that produced this file, not estimated.

## Status vocabulary (implemented ≠ tested ≠ merged ≠ verified)

* **MERGED** — the capability is on `main`, its automated tests passed in CI on the exact head of the merged pull request, and the roadmap claim was checked against the merge history (see ROADMAP "Delivered").
* **PARTIAL** — part is merged; the remaining work is named in the last column.
* **IN REVIEW** — built on a branch, not merged.
* **NOT STARTED** — nothing in merged code or a branch.
* **CONSTRAINT** — a rule or negative requirement that is enforced by tests of the phases listed (not a feature).
* **NOT MANDATORY (R1)** — the SRS states it is optional, future, or a configurable value; kept visible and never silently dropped.
* **No requirement is "fully verified" yet.** Full verification (end-to-end, installation, restore, performance, manual visual review) is the final milestone 5.3 (see `docs/RELEASE1-ACCEPTANCE.md`). MERGED means merged and CI-tested only.

## Totals (derived)

* Items traced: **127** = 115 SRS items (sections 2 to 19: 105 requirements; section 21: 10 acceptance scenarios) + 12 reporting requirements.
* MERGED: **14** · PARTIAL: **43** · NOT STARTED: **62** · IN REVIEW: **0** · CONSTRAINT: **4** · NOT MANDATORY (R1): **4**.
* Still incomplete (PARTIAL + NOT STARTED + CONSTRAINT awaiting its phase): **109**.
* Phase 3.7A (inventory accounting configuration) is built on a branch and is counted under the items it supports (7.5 and 9.3) as PARTIAL, never as merged.

## Matrix

| ID | Capability | Phase(s) | Status | Evidence (code / documentation) | Remaining work |
|---|---|---|---|---|---|
| 2.1 | Shared React + TypeScript frontend for Web and Desktop | 4.8 | PARTIAL | UI-1 (PR #12); apps/web, packages/ui, packages/api-client; frontend.yml | Continuous rule for every phase; final audit of Web/Desktop parity in 4.8 and 5.3 |
| 2.2 | Tauri 2 Windows desktop client | 4.5 | PARTIAL | src/desktop; desktop.yml builds a debug binary on every change | Signed installable package, auto-start, native adapters only where required (4.5) |
| 2.3 | ASP.NET Core backend holding business rules, permissions, posting and costing | 3.7B | PARTIAL | src/backend (Hasm.Api, Hasm.Infrastructure); backend.yml | Continuous; posting and cost rules arrive with 3.7B onward |
| 2.4 | PostgreSQL on Windows or Linux server | 4.5, 5.1 | PARTIAL | CI runs PostgreSQL integration tests (Linux runner); dev on Windows | Windows Server and Linux Server deployment verification (4.5, 5.3) |
| 2.5 | Clients reach data only through the HTTPS API | 4.5, 5.2 | PARTIAL | API-only architecture in every merged phase; no direct DB access from clients | TLS certificate provisioning and enforcement verified in 4.5; hardening review 5.2 |
| 2.6 | Supported deployment models (single machine, central server, separate app and DB servers) | 4.5 | NOT STARTED | - | Installer and configuration per model, tested per model |
| 2.7 | Installer and first-run wizard (PostgreSQL local/external, certificates, secrets, first administrator and company, health checks) | 4.5 | PARTIAL | Hasm.Admin command-line administration and provisioning (PR #5); initial administrator creation (PR #6) | Wizard, certificate and secret setup, environment health checks, installer packaging |
| 2.8 | Daily operation without Internet; offline licence and update | 4.2, 4.4 | PARTIAL | Nothing in merged code needs Internet | Offline licence activation (4.2) and signed offline update package (4.4) |
| 3.1 | Central system database (logical Hasm_System) | 2.x | MERGED | Phases 2.1-2.3C (PR #4-#9); system migrations 0001-0016 | Extended by later system migrations only |
| 3.2 | One independent PostgreSQL database per company | 2.x, 3.1A | MERGED | PR #5 provisioning, PR #10 runtime isolation; CompanyRuntimeIsolationTests | None for the isolation model; every module keeps its data in the company database |
| 3.3 | Shared Party entity: GlobalPartyId in the system database, LocalPartyId in the company | 3.9A | NOT STARTED | - | Registry, identifiers, shared versus company-specific data |
| 3.4 | Shared master data change by proposal, central approval and audit; no visibility of other companies' balances | 3.9A | NOT STARTED | - | Proposal and central approval workflow, outbox and reconciliation across databases |
| 3.5 | Permissions by company, branch and warehouse; per-branch cashboxes, warehouses and cost-centre links | 3.14 | PARTIAL | RBAC with company/branch/warehouse/cost-centre scopes (PR #7, #13, #14, #18, #26); branch and warehouse models | Cashboxes and bank accounts per branch (3.14); permissions of later modules |
| 3.6 | Inter-branch settlements and warehouse-to-warehouse transfer | 3.7B, 3.22 | PARTIAL | Transfer documents up to approval (PR #26) and transfer costing (PR #27) | Posting of transfers (3.7B); inter-branch accounting settlement (3.22) |
| 3.7 | Multi-company reports aggregated in the backend, never by cross-database SQL | 3.24 | NOT STARTED | - | Backend aggregation with currency conversion and authorization per company |
| 3.8 | Simple aggregation kept separate from formal consolidation (account maps, intercompany, eliminations, FX adjustments) | 3.24 | NOT STARTED | - | Aggregation delivered; whether formal consolidation is a Release 1 deliverable needs owner decision OD-1 |
| 4.1 | Local users, optional Active Directory/Kerberos, TOTP MFA, protected emergency account | 4.1 | PARTIAL | Local identity, sessions, throttling, hardening (PR #6, #8) | TOTP MFA, AD/Kerberos option, emergency account (none exists; PHASE 2.3A recorded it as not built) |
| 4.2 | Advanced RBAC with company/branch/warehouse scopes and price/cost/date/edit/approve/backup permissions | 3.11, 3.14, 4.3 | PARTIAL | Permission catalogue and role presets (PR #7 and every phase since); 108 permissions in the Administrator preset on the 3.7A branch | Price, discount, cost-visibility, backup and module permissions are added by the phase owning each capability |
| 4.3 | Configurable approval policies by type, amount, branch and risk; separation of duties | 3.13 | PARTIAL | Journal approval policies and ordered stages (PR #17, #18); import approval (PR #19); stock document approval (PR #26) | Policies for sales, purchasing, vouchers, cheques, bank adjustments and recurring documents; extra risk classes need OD-14 |
| 4.4 | Changing an approved-not-posted document returns it to approval; a posted document is never edited | 3.7B, 3.13 | PARTIAL | Journal posted immutability (PR #17); stock document re-approval rule to be verified in 3.7B | Rule for every document family; verified with tests in each phase |
| 4.5 | No DBA rights for application users; audited administrative and sensitive operations; second approval where required | 4.1, 5.2 | PARTIAL | Least-privilege company roles (PR #10); cryptographic audit (PR #9) | Second approval for sensitive administrative operations; verified in 5.2 |
| 5.1 | Flexible chart of accounts with balance-sheet and P&L types and starter templates | 3.2A | MERGED | PR #16 (foundation), #20 (Service, Retail, Wholesale templates), #19 (Excel import) | None for the chart; capitalization and subledger control-account decisions belong to their modules |
| 5.2 | Manual and automatic balanced journals posted atomically, with branch and cost-centre distribution | 3.2B, 3.2C | MERGED | PR #17, #18; automatic path JournalService.CreateAutomaticAsync | Module-originated automatic journals arrive with 3.7B onward |
| 5.3 | Posted journals are never edited or deleted; correction by reversal or adjustment with reference chain and audit | 3.2B | MERGED | PR #17; database triggers and tests | Document-level reversals arrive with 3.7C and the commercial phases |
| 5.4 | Multiple open fiscal years, monthly or quarterly periods, Open / Soft Closed / Final Closed | 3.1D | MERGED | PR #15; docs/PHASE-3.1D.md | None |
| 5.5 | Period close requires pending material differences to be resolved; exceptional reopen with approval, permission and audit | 3.7C, 3.17, 3.20 | PARTIAL | Close checks and reopen workflow (PR #15, #18); inventory check is a placeholder (docs/ROADMAP.md) | Real probes: inventory pending/valuation (3.7C), unallocated and pending receivable states (3.17), bank reconciliation differences (3.20) |
| 5.6 | Carry-forward of year balances, layers and open documents without duplication or loss | 3.8D, 3.17, 3.18 | PARTIAL | Base-currency year-end close and opening balances (PR #18) | Inventory layers and open documents carried with their module; foreign-currency year-end blocked until 3.18 |
| 5.7 | Multi-currency, base currency, dated exchange rates, authorized rate override, recorded FX differences | 3.18 | PARTIAL | Currencies and rate history (PR #11, #17); override with reason and approval policy | Realized and unrealized difference accounting; year-end for foreign-currency companies |
| 6.1 | Extensible tax engine: dated rates, classifications, several taxes, exemption, zero rate, inclusive/exclusive | 3.10 | NOT STARTED | - | Whole tax engine |
| 6.2 | Calculation base, rounding and priority rules per document type and operation | 3.10 | NOT STARTED | - | Rules and tests |
| 6.3 | Immutable tax snapshot on the final document | 3.10, 3.15, 3.16 | NOT STARTED | - | Snapshot model (3.10) used by purchasing and sales documents |
| 6.4 | National compliance and e-invoicing as future localization modules | 3.10 | NOT MANDATORY (R1) | - | Extension point only; SRS section 1 places country-specific compliance outside Release 1 |
| 7.1 | Quotations, orders, invoices, returns, vouchers; cash/credit/mixed; discounts, taxes, currencies, credit limits, sales representatives | 3.9B, 3.13, 3.14, 3.15, 3.16, 3.17 | NOT STARTED | - | Customers and suppliers 3.9B, framework 3.13, vouchers 3.14, purchasing 3.15, sales 3.16, credit-limit enforcement 3.17 |
| 7.2 | Direct invoice mode versus separate delivery/receipt with partial shipments and links | 3.13, 3.15, 3.16 | NOT STARTED | - | Both modes with partial fulfilment |
| 7.3 | No duplicate stock movement or ledger effect between invoice and delivery/receipt | 3.13, 3.15, 3.16 | NOT STARTED | - | Single-event rule with idempotency; concurrency tests |
| 7.4 | Direct mode: stock event at invoice approval, once; separate mode: at delivery/receipt document; accounting effect posted or in an explicit pending state | 3.7B, 3.7C, 3.13 | PARTIAL | Pending-state design analysed in docs/AUTONOMOUS-PROGRESS.md; stock documents approval only (PR #26) | Posting and pending state (3.7B/3.7C); document-level modes (3.13) |
| 7.5 | Intermediate accounts such as goods received not invoiced (GRNI) | 3.7A, 3.15 | PARTIAL | Receipt counter account configurable (3.7A, in review) | GRNI behaviour with supplier invoices (3.15) |
| 7.6 | Flexible numbering by company, branch, document type and fiscal year; official number at approval; no number reuse | 3.13 | PARTIAL | Numbering engine (PR #17) used by journals and stock documents | Per-document-type, per-branch, per-year rules for every new document family |
| 8.1 | Multiple price lists, deterministic priorities by customer, group, quantity, unit, currency and date | 3.11 | NOT STARTED | - | Whole pricing engine |
| 8.2 | Explicit conflict resolution; chosen rule recorded; authorized manual price/discount override with reason | 3.11 | NOT STARTED | - | Rule trace and override permissions |
| 8.3 | Margin monitoring and minimum-margin approval | 3.11, 3.16 | NOT STARTED | - | Margin policy using actual cost from posted costing |
| 8.4 | Historical price, discount, cost and tax snapshots on issued invoices | 3.11, 3.15, 3.16 | NOT STARTED | - | Snapshot on documents; later list changes never re-price posted documents |
| 9.1 | Items, services, alternate units, conversions, barcodes, lots, serials, expiry, stocktaking, adjustments, transfers, reservations | 3.4, 3.8B, 3.8C | PARTIAL | Items, units, conversions, barcodes (PR #24, #25); stock documents (PR #26) | Stocktaking and adjustments (3.8B); lots, serials, expiry, reservations (3.8C) |
| 9.2 | FIFO and moving weighted average; company/group policy with controls for changing the method and revaluing | 3.8D | PARTIAL | Policy master data (PR #24); costing engine (PR #27) | Method-change and revaluation controls (3.8D) |
| 9.3 | Cost per item and per warehouse; transfers move actual cost, receiving warehouse uses its own method | 3.6, 3.7B, 3.8A | PARTIAL | Costing engine and transfers (PR #27); not wired to documents | Wiring through the posting action (3.7B); on-hand and value screens (3.8A) |
| 9.4 | Negative stock prohibited by default; limited authorized exception for moving average with provisional cost; never FIFO, lot or serial | 3.8B | PARTIAL | Engine refuses insufficient stock (PR #27) | Authorized exception with provisional cost and later settlement (3.8B) |
| 9.5 | Back-dated movements: impact analysis, cost preview, recalculation across chained warehouses, separate adjustment documents | 3.8D | NOT STARTED | Engine forbids back-dating at database level (PR #27) | Impact preview, recalculation and adjustment documents |
| 9.6 | Posted financial entries are not rewritten; no back-posting into Final Closed periods; material pending differences block close | 3.7B, 3.7C | PARTIAL | Ledger rules (PR #17, #15) | Same guarantees for stock-originated entries and the close check |
| 9.7 | Distinguish transaction date from recording/approval date | 3.7B | PARTIAL | Document date and audit timestamps exist; completeness to be verified when posting is built | Explicit fields and tests on posted documents |
| 10.1 | Receivables and payables, ageing, statements, credit limits, cash boxes, banks, transfers, vouchers, credit and debit notes | 3.14, 3.17 | NOT STARTED | - | Cash and banks (3.14); ledgers, ageing, statements, notes (3.17) |
| 10.2 | Allocation of a voucher to one or several invoices; partial and multiple settlement | 3.17 | NOT STARTED | - | Allocation model |
| 10.3 | Manual or suggested allocation by due date with review; advances, over-payments, credit notes, FX differences | 3.17, 3.18 | NOT STARTED | - | Suggestions, advances, unallocated amounts; FX differences in 3.18 |
| 10.4 | Un-allocation and re-allocation with permission and audit; linking never creates a second receipt/payment entry | 3.17 | NOT STARTED | - | No-duplicate-entry tests |
| 11.1 | Cheque life cycle: portfolio, post-dated, deposited, collection, collected, returned, cancelled, endorsed | 3.19 | NOT STARTED | - | State machine, entries, reports |
| 11.2 | Cheques linked to parties, invoices, receivables and entries; due dates, status notices, reports; intermediate entries by company policy and legislation | 3.19 | NOT STARTED | - | Linkage, notices, reports; company/legal policy needs OD-13 |
| 11.3 | Manual and smart bank reconciliation via CSV/Excel import with preview, mapping, duplicate detection, suggestions, partial/multiple matches, approval | 3.20 | NOT STARTED | Import framework (PR #19) reusable | Matching engine and approval flow |
| 11.4 | Matching a bank line to a recorded receipt never creates a duplicate entry; bank fees and FX differences produce a separate adjustment under approval | 3.20 | NOT STARTED | - | Adjustment documents and duplicate-prevention tests |
| 11.5 | Direct bank API integration is not mandatory in v1 | 3.20 | NOT MANDATORY (R1) | - | Not required |
| 12.1 | Templates and schedules for entries, invoices, expenses, accruals, scheduled payments | 3.21 | NOT STARTED | - | Templates and scheduler |
| 12.2 | Draft by default; conditional automatic approval/posting by type, permission, separation of duties and period | 3.21 | NOT STARTED | - | Conditions and gates |
| 12.3 | Idempotent runs, no change to past documents when a template changes, failure and approval alerts, run history, pause | 3.21 | NOT STARTED | - | Run ledger and idempotency tests |
| 12.4 | Scheduling never performs real bank transfers | 3.21 | CONSTRAINT | - | Negative requirement: asserted by test in 3.21 |
| 13.1 | Ready reports: balance sheet, P&L, ledger, balances, receivables, inventory, cost, sales, purchases, tax, banks, cheques, branches, cost centres | 3.3, 3.8E, 3.23, 3.24 | PARTIAL | Ledger, trial balance, statements, Excel/PDF (PR #21, #22) | Inventory (3.8E); operational reports (3.23); multi-company (3.24) |
| 13.2 | Flexible filters, comparisons, drill-down within authorization limits | 3.3, 3.23 | PARTIAL | Report centre and filters (PR #21) | Per new report; comparison basis needs OD-6 |
| 13.3 | Advanced visual report designer with fields, filters, grouping, formulas, charts, PDF/XLSX | 3.25 | NOT STARTED | Stable report definitions (PR #21) | Whole designer |
| 13.4 | Independent document designer: invoices, A4/A5/thermal, barcode, languages, historical template versions | 3.26 | NOT STARTED | - | Whole designer |
| 13.5 | Arabic RTL and English LTR, simple corporate design, keyboard and shortcuts | 4.8 | PARTIAL | UI-1 (PR #12); every phase tests both languages | Continuous; completeness audit 4.8 |
| 13.6 | Desktop-style cascading menus that open on hover and execute only on click or keyboard | 4.8 | MERGED | CascadingMenu component and tests (UI-1) | Re-verified in 4.8 and 5.3; menu entries grow with modules |
| 13.7 | Official brand identity and logo, never regenerated | 4.8 | MERGED | assets/branding used by UI-1 | None; rule applies to installers (4.5) |
| 14.1 | Excel/CSV import with mapping, staging, dry run, duplicate and consistency checks, opening entries/balances/stock | 3.9C, 3.27 | PARTIAL | Framework (PR #19); chart (PR #19) and units, groups, items (PR #25) adapters | Parties (3.9C); opening balances, receivables/payables, opening stock, templates (3.27) |
| 14.2 | Opening balances only or full history; mandatory authorized approval before final import; no duplication | 3.27 | PARTIAL | Approval before import and dedup in the framework (PR #19) | Opening-balance adapter through normal journal approval; full-history migration |
| 14.3 | Attachments as server files (disk/SMB/NFS) with metadata in the company database, hash integrity, backend-only access, application-level encryption for sensitive files | 3.12 | NOT STARTED | - | Whole attachment service and key management |
| 14.4 | Coordinated backup and restore of database and attachment folders; encryption of sensitive files | 4.3 | NOT STARTED | - | Consistent point for database plus attachments |
| 15.1 | Named User or Concurrent licences, annual subscription or perpetual | 4.2 | NOT STARTED | - | Licence model |
| 15.2 | Concurrent licence counts every active session; heartbeat and lease timeout configurable | 4.2 | NOT STARTED | - | Session accounting |
| 15.3 | Expired subscription becomes read/export only keeping drafts; perpetual licence keeps working after maintenance ends | 4.2 | NOT STARTED | - | Entitlement states |
| 15.4 | 36-month security and defect support per major version; notice before end of support | 4.2, 5.3 | NOT STARTED | - | Support-window metadata and notices; process commitment verified in 5.3 |
| 15.5 | Signed licence, online/offline activation; transfer or replacement only through HASM technical support | 4.2 | NOT STARTED | - | Signed licence and activation; no self-service transfer |
| 15.6 | Trial without key or expiry: 1 company, 2 users, 1 branch, 1 warehouse, 100 posted business transactions (automatic entries excluded); basic modules only; read/export afterwards | 4.2 | NOT STARTED | - | Limits enforced server-side; transaction definition needs OD-12 |
| 15.7 | Trial attachment quota is a configurable policy value; no final number approved | 4.2 | NOT MANDATORY (R1) | - | Default chosen and documented by the implementation (SRS section 20) |
| 16.1 | Manual and scheduled backups (daily/weekly/monthly), retention, local/network destinations, encryption, alerts, restore tests | 4.3 | NOT STARTED | - | Backup service and monitoring |
| 16.2 | Restore of a full installation or a single company with attachments without rolling back other companies or the system database; compatibility checks and trial restore | 4.3 | NOT STARTED | - | Single-company restore with party-link verification |
| 16.3 | PostgreSQL point-in-time recovery as a future extension | 4.3 | NOT MANDATORY (R1) | - | Not required |
| 16.4 | RPO/RTO as measured operational targets, never promised without tests | 4.3, 5.1 | NOT STARTED | - | Measurement procedure and recorded results |
| 16.5 | Manual-only updates through signed packages online or offline with signature, compatibility, database version, permission, backup and safe rollback checks | 4.4 | NOT STARTED | - | Update packager and applier |
| 17.1 | TLS for the API and the database; secret protection and key rotation and recovery without Internet | 4.5, 4.6 | PARTIAL | Secrets kept out of the repository; import-file key rewrap procedure exists (PR #19) | TLS provisioning (4.5); secure key provisioning, rotation runbook and recovery (3.12, 4.6) |
| 17.2 | Password hashing; encryption of backups, sensitive attachments and selected fields | 3.12, 4.3 | PARTIAL | Password hashing and hardening (PR #6, #8) | Backup encryption (4.3); sensitive attachments (3.12) |
| 17.3 | Append-only application audit with tamper-evident chain, access, export and retention controls, protected external copies | 4.6 | PARTIAL | Cryptographic audit integrity (PR #9) and company business audit | External protected copy, export and retention controls |
| 17.4 | Within one company database, financial event, entry, audit and de-duplication record commit atomically; cross-database work uses reliable requests and reconciliation | 3.7B, 3.9A | PARTIAL | Pattern proven in ledger posting, import and costing (PR #17, #19, #27) | Applied to stock posting (3.7B) and the system/company outbox (3.9A) |
| 17.5 | Opaque physical names for databases, tables, columns, relations, indexes and constraints with a private logical map | 3.2 | MERGED | Physical-name maps in SystemSchema/CompanySchema; CompanySchemaTests | Continuous: every migration keeps the rule and its tests |
| 17.6 | Redacted health, diagnostics and backup logging; protected support bundle | 4.6 | NOT STARTED | - | Health endpoints, redaction, support bundle |
| 18.1 | Reference test environment 8 vCPU / 32 GB / 500 GB SSD, 5 companies, 50 concurrent sessions | 5.1 | NOT STARTED | - | Environment build; if unavailable the result is recorded as NOT VERIFIED |
| 18.2 | Reference data: 20,000 items, 10,000 customers/suppliers, 100,000 invoices, 500,000 entries and stock movements; 60-minute load | 5.1 | NOT STARTED | - | Data generator and load scripts |
| 18.3 | P95 targets: search < 1 s, ordinary screen < 2 s, save/post invoice < 3 s, ordinary report < 5 s | 5.1 | NOT STARTED | - | Measured results |
| 18.4 | Unit, integration and E2E tests plus ledger, costing, recalculation, import, security, concurrency, backup, restore, install and upgrade tests before a commercial release | 4.0, 5.3 | PARTIAL | Unit, PostgreSQL integration and frontend tests in CI (861 PostgreSQL tests at PR #27) | E2E, install/upgrade, backup/restore, security and load suites (4.0 and the phases that own them) |
| 18.5 | No release with open Critical/High defects or failing mandatory tests; entry atomicity, company isolation and duplicate-movement prevention verified under concurrency | 5.3 | CONSTRAINT | CI gates on every PR | Release gate recorded in 5.3 |
| 18.6 | Internal DEV and TEST environments; customer is not required to keep a staging environment; isolated restore testing | 4.0 | CONSTRAINT | Disposable PostgreSQL cluster on port 5433 for destructive tests | Documented in the acceptance record |
| 19.1 | Company database is the source of truth; no direct UI edits to the database | 3.2 | MERGED | Backend-only access (PR #5, #10); least-privilege runtime role | Continuous |
| 19.2 | One business event produces at most one financial/stock effect (idempotency) | 3.7B, 3.13 | PARTIAL | Idempotent journal automatic path, import execution and costing operations | Posting keys for documents (3.7B) and commercial documents (3.13) |
| 19.3 | Posted accounting documents are never changed or deleted; correction by separate reversal/adjustment | 3.7C, 3.13 | PARTIAL | Journals (PR #17) | Stock and commercial documents |
| 19.4 | No cross-company visibility through shared party identity or a user session | 3.9A | PARTIAL | Company isolation tests for configuration, ledger, inventory | Party-identity case tested in 3.9A |
| 19.5 | No posting into Final Closed periods; automatic tasks never bypass approvals | 3.7B, 3.21 | PARTIAL | Journal period gate (PR #15, #17) | Stock posting gate (3.7B); scheduled tasks (3.21) |
| 19.6 | Drafts and linked documents never lost when the licence expires or the connection drops | 4.2, 4.7 | NOT STARTED | - | Licence read-only mode (4.2); local offline drafts (4.7) |
| 19.7 | Commercial document state separate from physical delivery and from actual accounting posting in separate mode | 3.13 | NOT STARTED | - | Three independent state tracks |
| 19.8 | Full traceability of sale price, tax, exchange rate, cost of goods and approval decision source | 3.10, 3.11, 3.15, 3.16, 3.18 | NOT STARTED | - | Snapshots and approval references on documents |
| 21.1 | Scenario: direct sale posts the stock movement once; separate delivery links the movement to the delivery document and the later invoice duplicates nothing | 3.13, 3.15, 3.16 | NOT STARTED | - | End-to-end scenario test in the sales and purchasing phases |
| 21.2 | Scenario: a posted entry cannot be edited, even by a high-privilege user; correction by reversal with approval and references | 3.2B | MERGED | PR #17 tests | Re-run in final regression |
| 21.3 | Scenario: user of company A cannot query company B balances even with a shared GlobalPartyId | 3.9A | NOT STARTED | Isolation tests exist without parties | Party-based test in 3.9A |
| 21.4 | Scenario: pending accounting state is explicit and blocks period close while material differences remain | 3.7C | NOT STARTED | - | Close probe and tests |
| 21.5 | Scenario: back-dated inventory cost recalculated within allowed periods with preview and separate adjustments | 3.8D | NOT STARTED | - | Scenario test |
| 21.6 | Scenario: voucher allocation, bank match or retry after a disconnection never creates a duplicate receipt or entry | 3.17, 3.20 | NOT STARTED | - | Duplicate-prevention tests |
| 21.7 | Scenario: a scheduled run that breaks approval/separation-of-duties rules or targets a closed period fails and the reason is kept | 3.21 | NOT STARTED | - | Scenario test |
| 21.8 | Scenario: single-company restore without rolling back other companies or the system database; links and attachments verified | 4.3 | NOT STARTED | - | Restore drill |
| 21.9 | Scenario: cascading menus open on hover and nothing executes before click or keyboard selection | 4.8 | MERGED | CascadingMenu tests (UI-1) | Re-verified in 4.8 and 5.3 |
| 21.10 | Scenario: non-descriptive physical names tested on new database, schema upgrade and backup restore | 4.3 | PARTIAL | Fresh creation and upgrade tests in CI | Restore leg (4.3) |
| RR-1 | Inventory movement report | 3.8E | NOT STARTED | docs/REPORTING-REQUIREMENTS.md section 3A item 1 | Whole report |
| RR-2 | Stocktaking and valuation as of a date, informational prices separate from official book value | 3.8E | NOT STARTED | docs/REPORTING-REQUIREMENTS.md item 2 | Whole report; price definitions OD-4 |
| RR-3 | Customer account statement | 3.23 | NOT STARTED | item 3 | After 3.17 |
| RR-4 | Cash account movement | 3.23 | NOT STARTED | item 4 | After 3.14 |
| RR-5 | Customer/supplier invoice movement | 3.23 | NOT STARTED | item 5 | After 3.15 and 3.16 |
| RR-6 | Daily transaction report | 3.23 | NOT STARTED | item 6 | Needs representative model OD-3 |
| RR-7 | Profitability (gross profit only) | 3.23 | NOT STARTED | item 7 | After sales and posted cost of goods sold |
| RR-8 | General Ledger report completion | 3.3 | MERGED | PR #21, #22; docs/PHASE-3.3.md | None |
| RR-9 | Trial balance completion | 3.3 | MERGED | PR #21, #22 | None |
| RR-10 | Financial statements (trading/cost of sales, P&L, balance sheet) | 3.3, 3.8E | PARTIAL | P&L and balance sheet merged (PR #21, #22) | Cost-of-sales statement activates after inventory data (3.8E) |
| RR-11 | Flexible Excel import and export, reusable mapping templates | 3.3, 3.9C | PARTIAL | Excel/PDF export merged (PR #21); import framework (PR #19) | Reusable mapping templates (3.9C) |
| RR-12 | Shared report standards (languages, authorization before pagination, determinism, tests) | 3.23 | CONSTRAINT | docs/REPORTING-REQUIREMENTS.md | Applied to every report |

## Acceptance criteria and test coverage

* Acceptance criteria per requirement are the SRS text itself plus, for RR-1 to RR-12, the acceptance criteria of `docs/REPORTING-REQUIREMENTS.md` section 3; per phase they are stated in `docs/ROADMAP.md` (each phase lists acceptance criteria and mandatory tests). Scenario items 21.1 to 21.10 are acceptance scenarios and each must be present as an automated test (or a recorded manual drill where automation is not possible) before 5.3.
* Current automated coverage on `main` (at `80eea1f`): unit and PostgreSQL integration tests (861 PostgreSQL tests at PR #27 head `e212a49`), frontend component/interaction tests, TypeScript typecheck and production build, Tauri debug build. Per-requirement test names are in the phase documents (`docs/PHASE-*.md`) and are re-verified when each phase starts.

## Findings of the audit

* **Missing requirements:** none found in the SRS that lacked a phase; items that previously had no numbered phase (Party registry, tax, pricing, attachments, sales and purchasing, receivables/payables, cash and banking, cheques, bank reconciliation, recurring documents, designers, licensing, backup, updates, installer, diagnostics, offline drafts, performance) now have one.
* **Deferred or optional by the SRS:** 6.4, 11.5, 15.7, 16.3 (kept as NOT MANDATORY (R1)).
* **Duplicate implementations:** none found; the reporting layer, export engine and import framework are single shared components by design (REPORTING-REQUIREMENTS section 4).
* **Conflicting documentation:** none between SRS, DECISIONS and ARCHITECTURE. Stale statuses found in `docs/ROADMAP.md` (rows for 3.3, 3.4, 3.5, 3.6 still said "pending owner merge" although merged) were corrected. See `docs/ROADMAP.md` "Owner decisions" for ambiguities (OD-1 to OD-14) and "Tensions" for wording to resolve at implementation time.
