# Company creation: requirements for the future implementation

Status: **not implemented.** This records constraints the owner stated while the UI-1 "Add New Company" control was added. It is an
open requirement, not an approved design. The approved SRS remains the authority for functional behavior.

## Current state (Phase UI-1)

- The Company Selection screen shows an **"Add New Company" / "إضافة شركة جديدة"** control. It is **not operational**: it is `aria-disabled`,
  does nothing, calls no API, and is labelled "Not available yet".
- The screen explains that the license determines how many companies are allowed and that the **Trial edition includes one company
  (no time expiration); additional companies require a paid license.** No purchase or upgrade workflow exists, and none is suggested.
- Entitlement data is currently company-scoped (`GET /api/v1/company/entitlements`, requires a selected company) and only models the
  Trial restrictions (single enabled currency). There is no company-count limit and no account-level license endpoint. The selection
  screen therefore reads no license data and assumes no edition; it states the documented Trial rule as text.
- No backend endpoint, provisioning code or database migration was added.

## What real company creation must enforce (backend)

1. **License company limits** enforced on the backend, per license/edition (Trial: 1 company; paid licenses: up to their permitted limit).
   The edition must not be hardcoded as Trial-only in the creation path.
2. **Fine-grained authorization**: only principals holding an explicit "create company" permission may create; the check happens on the server.
3. **Safe provisioning and migration**: creating the company database and applying migrations must be atomic from the user's point of view,
   using the existing provisioning foundation and its safety gates.
4. **Existing audit and database-isolation requirements** apply unchanged (audit trail of the creation, per-company database and roles,
   no cross-company access).
5. **Failed or incomplete provisioning** must be handled safely: no half-created company is selectable, partial artifacts are detected and
   cleaned up or marked for administrator action, the operation is idempotent or safely retryable, and the user gets a truthful error.

**UI restrictions are never sufficient enforcement.** Disabling or hiding the control only communicates the rule; every limit and permission must
be re-checked by the backend on each request.

## UI expectations when it is implemented

- The control becomes operational only when the backend reports the user may create (not by client-side assumptions about the edition).
- When the limit is reached, the control stays visible, is explained (limit reached), and remains keyboard and screen-reader accessible.
- Statements about licensing must match what actually exists (do not mention a payment or upgrade flow before it is built).
