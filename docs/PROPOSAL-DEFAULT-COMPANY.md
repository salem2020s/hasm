# Proposal: per-user Default Company

Status: **PROPOSAL, awaiting owner approval.** Nothing in this document is implemented. No migration has been applied to any database, and no
backend, authentication or session code has been changed.

## 1. Findings (existing implementation)

- The Hasm_System database has users, sessions (with `selected_company_id`), the company registry and company memberships. **There is no
  per-user preference storage and no "default company" concept anywhere** (backend, schema or API).
- `GET /api/v1/companies` returns only companies the user can actually use: active membership, company provisioning state `Ready`, active user.
  It returns `id`, `nameAr`, `nameEn` (logical id; never the physical database name).
- `POST /api/v1/session/company` binds the **session** to a company after a server-side membership check; unknown company, no membership,
  revoked membership and unavailable company are all the same `403 forbidden`. `DELETE /api/v1/session/company` clears the selection.
- Web uses the HttpOnly cookie session, Desktop an in-memory bearer token; both go through the same endpoints and the same session row.
- The frontend kept everything in memory and always showed the Company Selection screen after login.

Conclusion: the saved default **cannot** be fulfilled with the current APIs. It needs a small backend extension (below). It must live on the
server (not localStorage/cookie/React state) so that it survives logout, browser or app restarts, other devices, and Web vs Desktop.

## 2. Proposed storage (two options)

**Option B (recommended): a separate table in Hasm_System.** One row per user:

| logical column | type | notes |
|---|---|---|
| user_id | uuid, PK, FK -> users | exactly one default per user (the PK enforces it) |
| company_id | uuid, NOT NULL, FK -> company registry | the logical company id (never a physical database name) |
| set_at_utc | timestamptz NOT NULL | |

Physical identifiers follow the repository's opaque-naming convention (`SystemSchema` constants), added by a new migration
`0007_user_default_company.sql`. The table holds a *preference only*: it grants nothing. Validity is always re-derived at read time by
joining with the active-membership / `Ready` company / active-user conditions that `/companies` already uses, so a revoked membership, an
inactive company or a deactivated user makes the default silently inapplicable (no stale authorization).

**Option A (not recommended): a boolean column on the membership table** with a partial unique index per user. Smaller, but it mixes a
preference into the access-control table that RBAC and the audit mapping work on, and revocation must remember to clear the flag.

## 3. Proposed API (smallest set)

1. `GET /api/v1/companies` (existing): add `isDefault: boolean` to each item. Exactly zero or one item is `true`, and only if that company is
   currently accessible. No new request is needed to read the default.
2. `PUT /api/v1/me/default-company` with `{ "companyId": "<guid>" }` -> `200 { companyId }`. Requires an authenticated session; verifies
   active membership of a `Ready` company with the same check as company selection (`403 forbidden`, indistinguishable reasons); upserts the
   caller's own row only (the user id comes from the session, never from the request). Same body limit, CSRF and desktop-bearer handling as the
   other mutating endpoints.
3. `DELETE /api/v1/me/default-company` -> `204`: clears the caller's default (optional, not needed by the UI at first).

It does **not** change `session/company`, login, session or CSRF behaviour. Setting the default never switches the active company.

## 4. Frontend behaviour (login flow, using only existing select-company API)

After login (and on session restore with no company selected), with the `/companies` list:

| situation | result |
|---|---|
| exactly one accessible company | enter it (select-company API) -> Dashboard. **Already implemented** (needs no backend change) |
| several, one has `isDefault` | enter the default via select-company -> Dashboard |
| several, no default | Company Selection, with "Set as default" per company |
| saved default no longer accessible | not in the list, so no `isDefault`; one remaining company -> enter it, else selection |
| none accessible | empty state (existing); nothing invented |
| network / 5xx while listing or entering | error with retry; never treated as "no company" |

"Change company" always opens the selection screen and never auto-selects. On the selection screen each tile becomes a container with two
separate controls: **choose** (existing) and **Set as default / تعيين كشركة افتراضية** (this one calls `PUT /me/default-company`, then marks the
tile with the text **Default company / الشركة الافتراضية** plus a star icon; it never switches the active company, and announces success politely).

## 5. Security and isolation analysis

- The preference is bound to the authenticated user taken from the session; nobody can read or write another user's row.
- The default can only be set to a company the caller can currently access; reading applies the same condition again.
- It cannot be used to bypass authorization: the active company is still established only by `POST /session/company`, which re-checks membership.
- Cross-company data is not exposed: the response contains only logical ids and display names the user already receives.
- Failure never widens access: any error simply leaves the existing selection screen in place.

## 6. Open decisions for the owner

1. **Option B (separate table)** or Option A (membership column)?
2. **Audit:** the existing company audit stream is company-scoped and its event set is fixed. A preference change is not an authorization or
   configuration change. Proposal: **no audit entry** for this preference (documented), or alternatively add a system-stream event. Which?
3. Should a **sole company** also be stored as the default automatically? (Not needed for behaviour; the UI already enters a sole company.)
4. Approval to add migration `0007` and apply it with the existing admin tooling, and approval of when/how to apply it to the permanent
   development database (not done; admin credentials are required and are not used without approval).

## 7. Testing plan (after approval)

- Backend: unit and integration tests for set/read/clear, membership revoked, company not `Ready`, user deactivated, another user's company,
  cross-user isolation, idempotency, and CSRF/bearer parity. **Integration tests need the isolated disposable PostgreSQL test cluster
  (separate infrastructure task); they will not be run against the permanent development server.**
- Frontend: the cases in the request (default auto-selection, none/stale default, set vs switch, API failure and retry, cookie vs bearer,
  Arabic and English) using the mocked client, plus the existing RBAC/auth/session tests.
