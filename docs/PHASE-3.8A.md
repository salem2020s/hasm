# HASM — Phase 3.8A: Stock balances, item ledger and the reconciliation with the General Ledger (as built)

First slice of Phase 3.8. Read-only: it adds no table and changes no stock or accounting fact. Sources: SRS 5.5, 9.5, 19.2; ARCHITECTURE 5, 7, 8; docs/ROADMAP.md 3.8A.

* **Service** (`Inventory/StockBalance.cs`, `StockBalanceService`) and management facade: balances per item and warehouse (quantity in the base unit, value in the company base currency, average unit cost, costing method), the item ledger (opening balance, ordered movements with the running quantity and value, closing balance, source document number) and the reconciliation of the costing value with the balance of the inventory accounts of the General Ledger up to a date. Every number comes from the append-only costing ledger, never from a cache; an as-of date shows the history to that date.
* **Permission** `company.stockbalance.view` (warehouse-scoped), system migration 0020. The scope is applied INSIDE the queries: a warehouse the user may not see contributes to no row, count, total or reconciliation figure.
* **API** `GET /api/v1/company/stock-balances` (filters: item, warehouse, search, as-of, include zero, paging), `/warehouses`, `/items`, `/ledger`, `/reconciliation`.
* **UI** (one shared screen for Web and Desktop, `StockBalances.tsx`): tabs Balances, Item ledger, Ledger reconciliation; filters survive tab switches; Arabic RTL and English LTR; a difference between the costing ledger and the General Ledger is shown as an error.
* **Tests:** PostgreSQL `StockBalanceTests` (4), `StockBalanceApiTests` (1) on the disposable cluster (5 of 5); frontend `stockBalances.test.tsx` (3). The expected-count registry in `.github/workflows/backend.yml` is raised by 5 (901 to 906).
* **Not in this slice:** lots, serial numbers, reservations (3.8C), back-dated recalculation and revaluation (3.8D), reports and the cost-of-sales statement (3.8E).
