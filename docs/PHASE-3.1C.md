# Phase 3.1C — Warehouse Management

**Status:** in progress on `feature/phase-3-1c-warehouses` (from main after PR #13). Nothing is applied to any permanent database.

## Approved basis (SRS / decisions)
SRS §3: "per branch: cash boxes and **warehouses**"; RBAC scopes company/branch/**warehouse** (D-006); Trial = **one warehouse** (D-036); costing is per Item × Warehouse (D-018, later phase). Phase 3.1B conventions apply unchanged (opaque schema, append-only revisions, audited transactional changes, optimistic concurrency, activation epochs, fail-closed RBAC, no deletion).

## In scope
Warehouse master data (stable id, immutable upper-case code unique per company, Arabic/English names with at least one, optional address/contact, active flag, revision, activation epoch), branch association, lifecycle (create / update / deactivate / reactivate), warehouse-scoped RBAC, Trial limit, audit, shared Web/Desktop screen.

## Out of scope (later modules)
Stock quantities and movements, transfers, valuation/costing, purchasing, sales, accounting entries, item master, bin/location structures, fiscal periods (3.1D).

## Design defaults (reasonable, configurable later; flagged for owner review)
1. **Every warehouse belongs to exactly one branch**; the association is **immutable** after creation (moving stock-holding locations between branches needs inventory/accounting rules that do not exist yet).
2. **No "main warehouse" and no seeded warehouse.** Migration 0005 only creates tables: existing company data is neither read nor modified. Trial allows creating one.
3. **Trial limit 1, counting every warehouse ever created** (inactive ones count; never deleted) — same rule as branches, enforced in the backend.
4. **Branch dependency (PostgreSQL-enforced):** a warehouse can be created or reactivated only in an *active* branch; a branch cannot be deactivated while it has active warehouses (409 `branch_has_active_warehouses`). Deferred constraint triggers on both tables.
5. **RBAC:** `company.warehouses.view/update` (warehouse granularity, warehouse-scoped grants allowed), `company.warehousestructure.create/activate` (company-wide). No inheritance: branch grants never reach warehouses and vice versa. Warehouse-scoped assignments record the warehouse activation epoch and are effective only while the warehouse is active (and epoch matches); unverifiable → grant dropped, never widened.
6. **Schema adjustment:** system migration 0007's `scope_epoch` CHECK forces NULL for warehouse scope and migrations may not `DROP` constraints, so 0008 adds a separate `warehouse_scope_epoch` column with its own CHECK/immutability trigger.
7. Preset Company Administrator receives the four new permissions; other presets none.

## Delivery
Commits: (1) docs/plan, (2) company migration 0005 + schema/service/audit, (3) system migration 0008 + RBAC + facade + API, (4) shared UI, (5) verification/docs. Integration tests only on the disposable cluster (5433); CI count table updated.
