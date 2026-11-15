# PROPOSAL — Inventory Foundation (item master data)

**Status: PROPOSAL for owner confirmation. Nothing is implemented.** Sources: SRS §9 (items, units, barcodes, valuation policy), ARCHITECTURE §8 (costing engine), DECISIONS D-017/D-018/D-019, `docs/REPORTING-REQUIREMENTS.md` (item and item-group attributes needed by later reports), `docs/ROADMAP.md`, completed phases 3.1C (warehouses), 3.2A (chart), 3.2D (import framework).

## 0. Phase identity (needs the owner)
`docs/ROADMAP.md` lists "inventory and costing" only as *not started* and assigns it **no phase number**. This document does not invent one: the working label is **Inventory Foundation (INV-1)** until the owner assigns the number. The roadmap names no prerequisite for it: warehouses (3.1C), the chart of accounts (3.2A/3.2A-2), the import framework (3.2D) and the reporting foundation (3.3) are all merged. Reusable mapping templates and the opening-balance adapter (open 3.2D items) are not prerequisites; the item import in S5 reuses the delivered 3.2D framework.

## 1. Scope: master data only
In: item groups, units and conversions, items (goods and services), barcodes, valuation *policy settings*, permissions, audit, item import, shared Web/Desktop UI.

**Out (later inventory phases; no fake movements or entries):** stock movements, opening quantities, FIFO layers, moving-average ledger, costing engine, back-dated recalculation and Impact Preview, transfers, stocktaking, adjustments, reservations, lot/serial/expiry *behaviour*, negative-stock exception behaviour, sales and purchasing documents, any journal entry. This phase creates **no stock movement and no accounting entry**; stock quantity and value are shown as "not available until the stock-movement phase", never as zero or an estimate.

## 2. Existing versus missing
| Area | Exists | Missing |
|---|---|---|
| Warehouses | Branch-scoped warehouses with RBAC (3.1C) | Items to stock in them |
| Chart, currencies, numbering, approvals | Merged | Item default accounts (deferred, decision 6) |
| Import framework | Staging, dry run, conflict rules, independent approval, Chart adapter | Item adapter |
| Items, groups, units, barcodes | Nothing | Everything in section 3 |

## 3. Proposed slices
| Slice | Content | Key automated tests |
|---|---|---|
| **S1 Units** | Unit catalogue (code, names ar/en, decimals allowed, precision), company level; exact-decimal conversion factors per item base unit, no cycles; deactivate only once used | uniqueness, precision, conversion exactness and round trip, cycle refusal, in-use protection |
| **S2 Item groups** | Hierarchical groups (depth limit, no cycles, activate/deactivate); group default valuation method (FIFO / moving average, D-017) and tracking default | tree integrity, cycle refusal, default inheritance |
| **S3 Items** | Code (numbering service), names ar/en, type (goods / service; a service is never stockable or valued), group, base unit, alternate units, default purchase/sales units, valuation method (inherited from group/company), tracking flags (none / lot / serial / expiry) stored as attributes only, active state, notes, optimistic concurrency, audit. A guard refuses a valuation-method change once movements exist (the costing phase activates it) | code uniqueness, type rules, unit rules, inheritance, concurrency, audit, authorization |
| **S4 Barcodes** | Several barcodes per item and unit; company-wide uniqueness (including inactive); check digits for declared EAN-13/EAN-8/UPC-A, internal free text otherwise; exact lookup endpoint | concurrent-insert uniqueness race, check digits, lookup, deactivation |
| **S5 Import and permissions** | Item and barcode import through the 3.2D framework (validation, dry run, conflicts, independent approval); new system migration adding `company.items.view/manage`, `company.itemgroups.view/manage`, `company.units.view/manage`, `company.barcodes.manage`, `company.items.import` to the Company Administrator preset (Company scope; item data is not branch-owned) | permission fixtures, dry-run versus commit parity |
| **S6 Shared UI** | Units, group tree, item list/search/filter/barcode lookup, item form, barcodes; Arabic RTL and English on UI-1; one React implementation for Web and Tauri | component/interaction tests, typecheck, production build, Tauri build |

Every slice: a new company migration (`0012_…`) with least-privilege runtime grants (masters use update with `row_version`; no destructive rights on history), business audit events, server-side validation, no frontend-only security, PostgreSQL tests on the 5433 cluster only, CI expected-count updates, and `docs/PHASE-INVENTORY-1.md`.

## 4. Decisions needed from the owner (defaults proposed; none assumed approved)
1. **Phase number and name** (section 0).
2. **Item codes:** automatic numbering with optional manual entry, or manual only? *Proposed: automatic with optional manual, unique per company.*
3. **Valuation policy level:** SRS says company *or* item group. *Proposed: company default, overridable per item group, items inherit; no per-item override.*
4. **Barcode uniqueness:** company-wide across items and units. *Proposed: yes.*
5. **Lot/serial/expiry:** store the flags now (behaviour later) or omit until the costing phase? *Proposed: store flags only.*
6. **Default accounts** (inventory, COGS, sales, purchase returns) on items/groups: the roadmap says inventory account mappings are decided by the Inventory module. *Proposed: no account links in this phase, to avoid guessing accounting rules.*
7. **Services:** same item table with type = service. *Proposed: yes.*
8. **Unit conversions:** per item only, or also a company-wide default table? *Proposed: per item only.*

## 5. Invariants preserved or deferred
Ledger and posted history are untouched. Company, branch and warehouse authorization is unchanged; item data is company-level. The costing rules (per item × warehouse, FIFO / moving average, negative-stock restrictions, back-dated Impact Preview, immutable posted history, concurrency control) are recorded as schema constraints (method stored per group with a change guard) but implemented in the later stock-movement and costing phase. The close check `inventory_valuation_differences` stays "not applicable" until that phase supplies a real probe.

## 6. Risks
Early mistakes in units, conversions or valuation scope are costly once movements exist; hence decisions 3, 5, 6 and 8 before coding.
