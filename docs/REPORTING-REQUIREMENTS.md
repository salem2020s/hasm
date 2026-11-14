# HASM — Owner-approved Release 1 reporting and Excel requirements

**Status:** owner-approved mandatory Release 1 requirements (received 2026-11-14 from the owner after feedback from a prospective daily user). Planning record only: nothing here is implemented by this document, no phase scope is changed by it, and the approved SRS (§13 reports, §9 inventory, §7 sales/purchases, §10 receivables/payables) is not rewritten; this file refines and schedules it. Phase 3.2D is not expanded by it.

Principles that apply to every report below (R-STD): Arabic RTL and English LTR; one shared React frontend for Web and Tauri Desktop (no platform-specific report); fine-grained RBAC; company isolation; branch, warehouse and cost-center authorization **applied inside the query before pagination, totals and export**; deterministic ordering and results; date filtering; grouping and subtotals; consistent currency/number formatting; drill-down to source records where the source exists; Excel/PDF per the export section; automated correctness and security tests; posted history is never silently re-computed with today's data.

## 1. Schedule (no new phase numbers)

Existing phase numbers are reused. Modules whose phase numbers the roadmap has not assigned yet (sales, purchases, inventory and costing, receivables/payables, cash) are referred to by their SRS section; **a report is part of the acceptance of the phase that delivers its source module** and cannot be reported complete earlier.

| # | Requirement | Delivered in | Depends on |
|---|---|---|---|
| 8 | General Ledger account report: review and complete | **Phase 3.3** (review of the 3.2B S6 report; extended in place, no second engine) | 3.2B/3.2C ledger |
| 9 | Trial balance: review and complete | **Phase 3.3** (same engine) | 3.2B/3.2C ledger |
| 10 | Trading/Cost of Sales, Profit and Loss, Balance Sheet | **Phase 3.3** (Cost of Sales statement is produced only where inventory/costing data exists, i.e. fully after the inventory phase; until then reported "not applicable", never zero) | Chart classification, ledger, 3.2C closing, report-designer integration |
| 11 | Excel (.xlsx) export engine and PDF export | **Phase 3.3** (one shared export service used by every report; PDF stays a separate mandatory capability) | 3.3 reporting layer |
| 11 | Excel import with reusable mapping templates | Framework: **Phase 3.2D** (built: mapping, dry run, conflicts, independent approval, audit). **Reusable mapping templates are NOT built**; they are delivered with the first operational import adapter (items/parties), as an extension of the 3.2D framework | 3.2D |
| 4 | Cash account movement report | Phase delivering **receipt/payment vouchers and cash accounts (SRS §10)**; the journal-entry-only view can reuse the 3.3 ledger engine earlier | SRS §10 vouchers, GL |
| 3 | Customer account statement | Phase delivering **receivables (SRS §10)** with sales invoices/returns (SRS §7) | Sales, receivables |
| 5 | Customer/supplier invoice movement | Phases delivering **sales and purchases (SRS §7)** (a single report engine for both, document family kept distinct) | Sales, purchases, items, warehouses |
| 6 | Daily transaction report | Same as 5 (shares its document query) | Sales, purchases |
| 1 | Inventory movement report | Phase delivering **inventory and costing (SRS §9)** | Item ledger of stock movements |
| 2 | Stocktaking and valuation as of a date | Same inventory phase | Stock movement history, costing method, price history |
| 7 | Invoice / item / item-group profitability | After sales **and** inventory costing both exist (the later of the two phases) | Posted COGS per invoice line, discounts, returns, cost corrections |
| — | Visual report designer (SRS §13) | Separate mandatory Release 1 phase (not yet numbered); fixed reports above must expose their definitions so the designer can reuse, not duplicate, them | 3.3 |

## 2. What exists today versus what is missing

Verified against the phase documents (`PHASE-3.2B.md` S6, `PHASE-3.2C.md`); each item is re-verified against the code when its phase starts. A basic version never marks a report complete.

| Report | Exists | Missing / to verify |
|---|---|---|
| General Ledger (8) | Account selection (a group includes its descendants), date range, opening balance, running balance, branch and cost-center filters (cost-center-scoped authorization inside the query), original currency, rate and base amounts, paging, CSV | Closing balance presentation and per-page/total reconciliation to verify; source reference beyond the journal number (document link) and drill-down to the journal to verify; fiscal-year/period selector; Excel/PDF |
| Trial Balance (9) | Opening, period debit/credit, closing, hierarchy roll-up with leaf-only totals (no parent/child double counting), zero-row option, branch and cost-center filters, closing-entry exclusion option, CSV | Fiscal-year/period selector to verify; comparative columns; reconciliation proof against the GL as a stated check; Excel/PDF |
| Financial statements (10) | Nothing | All three statements, comparisons, branch-level, reconciliation to the trial balance, designer integration |
| Excel export (11) | CSV only (UTF-8, exact decimals, formula neutralisation) | Real .xlsx, column selection, filters/sorting preserved, grouping/subtotals, detailed/summary, typed numbers/dates, no truncation by paging, same authorization as the source report; PDF |
| Excel import (11) | Framework and Chart of Accounts adapter (Phase 3.2D) | Reusable mapping templates; operational adapters (items, parties, opening balances through normal journal approval) |
| Reports 1-7 | Nothing (their source modules do not exist yet) | Everything |

## 3. Acceptance criteria

Common to all reports: results identical for the same filters on repeated runs (explicit total ordering); totals equal the sum of the visible and hidden-by-paging rows; a user never sees or exports a row outside their company, branch, warehouse or cost-center authorization, and totals/subtotals exclude such rows; every number is reproducible from posted data; Arabic and English both tested; Web and Desktop run the same screen; Excel/PDF content equals the screen content for the same filters.

1. **Inventory movement:** per item and warehouse, opening + incoming − outgoing = closing; each movement shows type and source document with drill-down; summary view equals the sum of the detailed view; multi-warehouse and group subtotals; date boundaries inclusive and tested; an unauthorized warehouse contributes nothing to any total.
2. **Stocktaking and valuation:** quantities as of the selected date equal the movement history to that date (never today's stock); selling price, moving weighted-average cost and last purchase price displayed as **informational** columns, each labelled with the price date/basis it used; the **official book value** (company's approved valuation method, FIFO supported when configured) is shown separately and labelled; choosing a display method never changes the official method or any posted figure; a test proves a report run changes no stored cost.
3. **Customer statement:** opening balance + debits − credits = closing, running balance per line, sales invoices, returns, receipts/payments and adjustments each identifiable with their source document; detailed and summary views; branch filter limited to authorized branches; closing balance equals the customer's receivable ledger balance at that date.
4. **Cash account movement:** opening, debit, credit, running and closing balance per cashbox/cash account; filters by account, branch, date and transaction type (receipt voucher, payment voucher, journal entry, all); each line navigates to its voucher/journal; closing balance equals the ledger balance of that account.
5. **Invoice movement:** sales/purchase invoices and returns reported separately and never mixed on one customer/supplier statement view; quantities and values with subtotals by customer/supplier, item, group, warehouse and branch; drill-down to the document.
6. **Daily transaction report:** chronological (date, then document number), filters as listed incl. sales representative and status; document totals and period summary equal the sum of the listed documents; cancelled/draft documents only when the status filter asks for them and clearly marked.
7. **Profitability:** net sales **excluding applicable tax components** − actual COGS (posted, per the approved valuation method, including cost corrections) = **gross profit**; margin % = gross profit / net sales (defined result when sales are zero); returns and discounts applied; labelled **gross** profit everywhere, never "net profit"; invoice, item and item-group views reconcile to each other and to the posted sales/COGS ledger; historical results do not change when later prices or costs change. A "net profit" view is not offered unless an expense-allocation methodology is first approved by the owner.
8. **General Ledger report:** each listed option implemented and tested or explicitly marked unfinished; closing = opening + debits − credits per account; no parallel ledger engine.
9. **Trial balance:** total debits = total credits; closing balances reconcile to the General Ledger for the same filters; parents roll up their children exactly once; period and fiscal-year selection.
10. **Financial statements:** built only from the Chart classification and posted ledger data; subtotals and totals tested; Balance Sheet balances (assets = liabilities + equity including current-period result before closing, and after closing entries); Profit and Loss equals the net movement of revenue and expense accounts; each statement reconciles to the trial balance; comparisons by fiscal year/period; branch-level statements limited to authorized branches; closing entries handled as in 3.2C (`excludeClosing`).
11. **Excel:** real .xlsx that opens in Excel; numbers and dates are typed cells with correct formats, base/original currencies shown with their codes; any text beginning with `=`, `+`, `-`, `@`, tab or carriage return (including after leading spaces) is neutralised and tests prove no formula is ever produced from data; the full filtered result is exported regardless of page size, within documented limits that fail with a clear message instead of truncating silently; selected columns, sorting, grouping and subtotals preserved; same authorization as the report; export is audited. Import acceptance is the 3.2D acceptance plus mapping templates (saved per company and adapter, versioned, never bypassing validation, dry run or approval).
12. **Standards:** the common criteria above for every report, each with automated correctness and security tests (authorization before pagination/export, cross-company and cross-branch denial).

## 4. Dependencies and duplication rules

* One reporting layer: ledger-based reports (GL, trial balance, statements, cash-by-journal) share the existing ledger query engine; operational reports (inventory, sales, purchases, statements of accounts) share document/stock-movement queries; **profitability reads posted COGS from the inventory ledger, it never recomputes cost**. No report re-implements another's query.
* One export service (3.3) used by all reports; the per-report CSV code is migrated to it, not duplicated.
* Authorization filter helpers (branch, warehouse, cost-center) are shared and applied in the SQL of every report.
* Sales representative, document status, selling price, last purchase price, item groups and cost method are required attributes of modules not yet built; their schemas must carry them (inventory/sales phase requirement).
* Historical valuation needs dated cost and price history (moving average snapshots, price effective dates); the inventory phase must store enough to reproduce any past date.
* The year-end/Inventory close checks recorded in 3.2C stay unchanged: `inventory_valuation_differences` becomes a real probe in the inventory phase.

## 5. Unresolved details for implementation-time review (not decided here)

1. Exact definition of "consumer selling price" for historical dates (price list effective at the date vs document price) and of "last purchase price" (last posted purchase on or before the date, per warehouse or company-wide, in which currency).
2. Whether the Trading/Cost of Sales statement is separate from the Profit and Loss in layout for service companies (hidden when no inventory) and the account classification that feeds it.
3. Comparison basis (previous year, previous period, budget) and the rounding/presentation rules of statements.
4. Export size limits and whether very large exports run as background jobs with a download that respects retention and encryption rules.
5. Sales representative model (user, employee or party), required before reports 6 and 7.
6. Treatment of tax-inclusive prices and discounts in "net sales excluding tax components" per tax setup (SRS §6, §8).
7. Whether a net-profit report is wanted; if so the owner must approve the expense-allocation methodology first.
8. Storage and sharing of reusable import mapping templates (per company vs per user) and their audit.
