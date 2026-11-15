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
| 3.2A-2 | Business-type Chart of Accounts templates (mandatory Release 1; see `docs/PHASE-3.2A.md`) | merged (PR #20, merge commit `2d50ada`): Service 54, Retail 66 and Wholesale 64 accounts in `ChartTemplates.All` and the shared template picker; inventory-related accounts stay provisional until the Inventory module; cheque accounts belong to the Cheques module |
| 3.2A-3 | Chart of Accounts Excel import: validation, dry run, reconciliation, required approval (mandatory Release 1; no empty-chart-only restriction, merge and conflict rules defined with the import) | merged with Phase 3.2D (PR #19); fulfilled: real CSV/XLSX Chart import with approval, create/skip conflict rules and end-to-end tests |
| 3.2B | General Ledger and journal posting — see `docs/PHASE-3.2B.md` (slices S1 numbering, S2 exchange rates, S3 cost centers, S4 approval policies, S5 journal and posting, S6 ledger reports, S7 shared UI) | merged (PR #17, merge commit `d2d2a11`, includes the journal-list branch-filter fix `29fadd9`) |
| 3.2C | General Ledger completion — see `docs/PHASE-3.2C.md` (S1 cost-center accounting policies and cost-center-scoped access, S2 risk-based and ordered approval policies, S3 fiscal-year closing and opening balances, and the owner-review corrections: mandatory independent year-end approval, no year-end line limit, exact cost-center carry, history-based balance-sheet reporting) | merged (PR #18, merge commit `b23013d`; all four CI checks passed). Open items stay in the table below: foreign-currency year-end needs the FX policy, inventory close check is a real probe only when inventory exists |
| 3.2D | Generic import framework: staging, validation, dry run, reconciliation, independent approval (mandatory Release 1; also serves opening balances, bank statements and item/party imports) | merged (PR #19, merge commit `c34f99c`); S1-S5 delivered; opening-balance adapter, reusable mapping templates and production key provisioning/recovery remain open Release 1 items |
| 3.3 | Excel/PDF export, the financial statements required by the SRS (Trading/Cost of Sales where applicable, Profit and Loss, Balance Sheet) and completion of the General Ledger and Trial Balance reports (mandatory Release 1; owner-approved requirements in `docs/REPORTING-REQUIREMENTS.md`) | approved and BUILT on branch `feature/phase-3-3-financial-reporting` (S1 reporting foundation, S2 ledger/trial balance completion, S3 statements and classification, S4 XLSX, S5 PDF, S6 report center; see `docs/PHASE-3.3.md`); PR pending merge. Not delivered here: the visual Report Designer, and the reports that need inventory, sales, purchasing, receivables and banking data |

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
