# Phase 3.1B — Branch Management

**Status:** scope APPROVED by the project owner (2026-11-13). Schema, migration and backfill are **not yet designed or approved** (Checkpoint B). Branch: `feature/phase-3-1b-branches`.

## Approved requirements
A proper branch entity with: stable identifier; code unique within the company; Arabic and English names; active/inactive status; main-branch designation; optional address and contact information; optimistic concurrency and revision tracking; transactional audit events; explicit fine-grained RBAC. Existing security, database-naming, migration and audit conventions apply.

**Main branch.** Every company has exactly one main branch. It is created automatically when a company is provisioned; existing companies receive one through a safe, idempotent migration or initialization procedure that never resets, recreates or overwrites existing data (procedure to be presented for review before it touches any permanent development database). The main branch cannot be deactivated unless another active branch is designated main in the same transaction. Uniqueness is protected against concurrency and holds throughout initialization and updates.

**Trial licensing.** Trial permits one branch. Every branch ever created counts toward the limit, inactive ones included; deactivating a branch never frees a slot, and records are never deleted to bypass the limit. Enforced in the backend, not only in the UI.

**RBAC and reactivation (previously approved invariants).** Explicit permissions with no automatic inheritance of branch access; branch-scoped authorization; an ActivationEpoch (generation) mechanism so deactivating and reactivating a branch never silently restores stale access assignments; reassignment after reactivation needs explicit authorized actions. The existing company authorization and session security model is not weakened.

## Out of scope
Warehouses (3.1C), fiscal years and periods (3.1D), inter-branch accounting entries, branch financial reporting, any change to UI-1 branding or layouts beyond adding the Branch screens.

## Checkpoints
| | Content | Gate |
|---|---|---|
| A | Scope documented, branch created, isolated test cluster and strengthened safety gate verified | report |
| B | Exact branch schema, migration, existing-company backfill, runtime grants, activation epochs, licensing and concurrency invariants | **owner approval before any migration touches a permanent development database** |
| C | Backend domain logic, audit, RBAC, entitlements, API; verified on the isolated cluster | report |
| D | Branch Management UI (UI-1 design); Arabic RTL, English LTR, Web and Windows Desktop | report |
| E | Relevant tests and phase review | before any GitHub delivery |

No commit, push or merge happens without explicit approval at the relevant checkpoint.
