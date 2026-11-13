# Phase 3.1B — Branch Management

**Status:** scope APPROVED (2026-11-13). Schema/migration/backfill design APPROVED and implemented on the disposable test cluster (Checkpoint B); not applied to any permanent database. Branch: `feature/phase-3-1b-branches`.

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

Push and merge require explicit owner authorization (push of this branch authorized 2026-11-13; merge is not).

## Implemented (Checkpoints A–D)
- Company migration 0004 (`Branch`, append-only `BranchRevision`, DB-enforced invariants, idempotent main-branch seed), system migration 0007 (`scope_epoch`, five permission codes), branch-scoped RBAC with activation epochs, Trial limit (1, counting all branches ever created), audited domain service with optimistic concurrency, `/api/v1/company/branches` API, shared Web/Desktop Branch Management screen, isolated PostgreSQL test cluster (5433) with cluster-identity safety gate.
- Details: `docs/PHASE-3.1B-PROPOSAL.md`. No migration has been applied to any permanent development database.

## Known limitations
1. `BranchRevision` history is audited but is not yet displayed in the unified Configuration History screen (`/history` reads only `ConfigRevision`).
2. Stale branch-role assignments (after deactivation/reactivation) may remain visible as records, but never grant access: authorization requires the branch to be active and the assignment's activation epoch to match, failing closed otherwise.
3. Manual Web/Desktop visual validation is deferred to the final validation milestone.
