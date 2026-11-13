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

## As built
- **Company migration 0005** (`Warehouse`, append-only `WarehouseRevision`; no seed, no existing data touched). PostgreSQL enforces: branch FK, immutable code (upper case, case-insensitively unique) and branch, no delete/truncate (owner included), revision +1 fence, epoch rule, and — through deferred constraint triggers on **both** tables — that no active warehouse lives in an inactive branch. `CompanyBranchService.DeactivateAsync` refuses (`branch_has_active_warehouses`) before PostgreSQL has to.
- **Concurrency:** warehouse operations take the **same transaction-level advisory lock** as branch-structure changes (then the audit-stream lock), so a warehouse change and a branch deactivation can never interleave; verified by a race test.
- **System migration 0008:** separate `warehouse_scope_epoch` column (own CHECK and immutability trigger; the branch CHECK of 0007 is untouched because migrations may not `DROP`); the four warehouse permission codes are added to the preset Company Administrator. `AccessService` reads one logical epoch (`COALESCE` of both columns), drops warehouse grants unless the warehouse is active with the recorded epoch (fail closed, never widened), records the epoch on assignment and retires stale-epoch assignments (audited).
- **Permissions:** `company.warehouses.view/update` (warehouse granularity), `company.warehousestructure.create/activate` (company-wide). No inheritance in either direction between branch and warehouse access.
- **API:** `/api/v1/company/warehouses` (list with `can`, minimal branch refs, limit; create 201+ETag; get; update; deactivate; activate) with `If-Match`/ETag, 428/412/404/409/403 `entitlement_denied`, anti-CSRF and `no-store` like branches.
- **UI:** one shared screen (`pages/Warehouses.tsx`) for Web and Windows Desktop; menu entry Company → Warehouses (the "Warehouses" inventory module entry stays "coming soon").
- **Tests:** 34 new PostgreSQL integration tests (CI table now 468, all executed), 16 unit tests, 23 UI + 4 api-client tests. Existing expectations updated for company migration 0005, system migration 0008, 27 preset-admin permissions.

## Known limitations
1. `WarehouseRevision` history is audited but not shown in the unified Configuration History screen (same as `BranchRevision`).
2. Stale warehouse-role assignments may remain visible as records but never grant access.
3. A warehouse cannot be moved to another branch (by design until inventory/accounting rules exist); no seeded or "main" warehouse; Access Auditor/Manager presets receive no warehouse permissions.
4. No migration has been applied to any permanent development database; manual Web/Desktop visual validation is deferred to the final validation milestone.
