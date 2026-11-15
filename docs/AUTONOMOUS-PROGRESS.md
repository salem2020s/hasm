# HASM — Autonomous execution progress

Machine-readable twin: `docs/AUTONOMOUS-STATE.json` (non-secret metadata only). At every resumed session: read both, **reconcile with GitHub and the repository** (a checkpoint never proves a merge), then continue from the first incomplete approved dependency.

## Reconciled checkpoint (verified against GitHub, 2026-11-15)
| Phase | PR | State | Evidence |
|---|---|---|---|
| 3.3 Financial reporting | #21, #22 | merged | main |
| 3.4 Inventory master data | #24 | merged | main |
| 3.4B Inventory import | #25 | merged | main |
| 3.5 Stock movement documents (non-posting) | #26 | merged | main |
| 3.6 Inventory costing engine | #27 | **merged** (`2d2f187`; head `e212a49` had CI green: build-test and postgres-integration, 861 PostgreSQL tests) | GitHub |

Repository facts relevant to the process: private repository on a plan **without branch-protection APIs** (the protection endpoint answers 403), so required reviews/rules cannot be read or relied upon; the only automatic gates are the GitHub Actions checks (path-filtered workflows: a docs-only PR triggers none). The merge policy of the master directive (all mandatory gates satisfied, no blocking findings, exact head SHA verified) is applied by this agent itself.

## Next: Phase 3.7 — Atomic inventory posting (dependency analysis)
What exists: documents up to *Approved* (3.5), the costing engine with its accounting contract (3.6), the general ledger with an **idempotent automatic-journal path** (`JournalService.CreateAutomaticAsync` / `CreateInWorkAsync` with source and source key, used by year-end closing) and the ordinary journal life cycle (draft → approval → post, separation of duties, fiscal-period gate).
SRS §7 explicitly allows the accounting effect to be **posted automatically OR to remain in an explicit accounting-pending state with its reason, document links and period-close protection**. Because a journal entry's approval and posting are separate, independently authorized steps with database-enforced separation of duties, the genuinely atomic design is: *one transaction* commits the document status (Posted), the cost operation, the stock effect and a **balanced draft journal linked to the document with an explicit accounting state `pending`**; the journal then follows the ordinary approval/post workflow, and the period close refuses while pending states exist. Nothing is posted quantity-only and no fake entry is made.

Missing prerequisites (each to be built and merged before the slice that needs it):
1. **3.7A — Inventory accounting configuration:** per-company mapping of the inventory asset account per item group, and the counter accounts per document type (receipt: goods-received / stock-adjustment account; issue: cost-of-sales / stock-adjustment account), validated against the chart (active, postable, correct type); permissions; API; shared UI; audit. No account is hard-coded: nothing may post until configured.
2. **3.7B — Posting:** `Posted` state and link tables (posting record, cost operation, journal, accounting state), authorized warehouse-scoped *post* permission and action with revalidation (items, units, active warehouses, open fiscal period, configured accounts), authorized per-line **receipt cost input**, costing through `InventoryCostingService` with key `stock_document:<id>`, balanced draft journal via the automatic path, idempotency, rollback, concurrency, reversal/cancellation of posted documents, period-close protection for pending accounting states, audit, API.
3. **3.7C — Posting UI and capabilities** (shared Web/Tauri, tabs standard), then quantity-on-hand screens and the inventory reports (next phases).

## Log
* 2026-11-15 — Reconciled #27 (merged by the owner before this run), created this tracker; starting 3.7A.
