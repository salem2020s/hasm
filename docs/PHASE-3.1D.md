# Phase 3.1D — Fiscal Years and Accounting Periods

**Status:** implemented locally on `feature/phase-3-1d-fiscal-periods` (from main after PR #14). Nothing is applied to any permanent database.

## Approved basis (SRS §5, §9, §16; ARCHITECTURE)
Multiple fiscal years open at once; monthly or quarterly periods; states Open / Soft Closed / Final Closed; closing needs pending material differences and adjustments resolved; exceptional reopening needs approval, permissions and audit; no posting into a Final Closed period, and no bypass by automatic jobs; year balances carried forward without duplication or loss; posted entries are never edited. The company business time zone (3.1A-2) is never defaulted.

## Implemented now (calendar structure and state machine)
Fiscal years (exactly 12 months from the 1st of a month, any month: company-specific calendars such as July 1), generated periods, the three period states, controlled closing/reopening, year closing, RBAC with separate permissions, audit, optimistic concurrency, DB-level invariants, and the backend posting gate for the future ledger.

## Requires the future General Ledger / posting engine (NOT built, nothing fabricated)
Journal entries, closing/year-end adjustment entries, balance carry-forward, opening balances, the content of "pending material differences" checks (inventory valuation, pending accounting postings, pending adjustments), and any posting. Today these are placeholders: `PeriodCloseGuard` probes (unimplemented, reported as such, never block) and `FiscalPeriodGate` / `f_…postable` (ready for the posting engine to call inside its own transaction).

## Design decisions made (reasonable defaults; flagged for owner review)
1. A year is exactly 12 months starting on a first-of-month day; periods are 12 monthly or 4 quarterly, fixed per year, generated with the year and never edited. (Short/long first years and 13th adjustment periods need ledger rules and are deferred.)
2. Years never overlap and form ONE contiguous calendar: the first year starts anywhere; every later year must start the day after the latest year ends (or end the day before the earliest starts). No gaps, so every date belongs to at most one period.
3. Transitions: Open ⇄ Soft Closed ⇄ Final Closed only; Open can never jump to Final Closed and Final Closed can never become Open directly. **Exceptional reopening of a Final Closed period or a closed year is an approval workflow, never a one-step operation** (see "Exceptional reopening approval" below): on approval a period returns to **Soft Closed**.
4. Year closing is state only: allowed when every period is Final Closed; reopening a closed year needs a reason and leaves its periods Final Closed. A closed year freezes its periods.
5. Permissions (company-wide): `company.fiscalyears.view/create/close`, `company.fiscalperiods.close/finalclose/reopen` (routine soft reopening), `company.fiscalreopen.view/create/approve` (create = request). **No one-step reopening permission exists.** `approve` is held only by the new preset **Fiscal Approver**, never by the Company Administrator, which may only view and request.
6. The business time zone must be configured before a year is created and is recorded on the year (informational snapshot). No Trial limit applies: none is approved (D-036 lists none for fiscal years).
7. Exceptional reopening requires real two-person approval (implemented; see below). A general approval engine for other operations remains future work.

## Delivery
Company migration 0006, system migration 0009; domain service, authorized facade, API; shared UI; tests on the disposable cluster only; CI count table updated.

## As built
- **Company migration 0006** (`FiscalYear`, `FiscalPeriod`, append-only `FiscalHistory`; nothing seeded). PostgreSQL enforces: 12-month years from the 1st of a month; year and period **exclusion constraints** (no overlap); at-commit consistency (exact monthly/quarterly spans, contiguous calendar without gaps, a closed year has only Final Closed periods); transition guard (only Open⇄Soft⇄Final); Final Closed always records its closer; immutable identity/dates; revision +1; no delete/truncate; a mandatory reason (≥10 chars) in the history row of the two exceptional reopenings. `f_…postable(date, allow_soft)` is the DB-side posting-availability check for the future ledger.
- **System migration 0009:** routine fiscal permissions plus `fiscalreopen.view/create` on the preset Company Administrator; new preset role **Fiscal Approver** (`fiscalreopen.approve`, `fiscalreopen.view`, `fiscalyears.view`).
- **Backend:** `FiscalCalendarService` (one advisory lock for all fiscal changes, audited transactions, close guard, segregation of duties), authorized `FiscalCalendarManagementService` (separate company-wide permission per action), `FiscalPeriodGate` (FOR SHARE lock so a close waits for in-flight postings; Final Closed never accepts postings), API `/api/v1/company/fiscal-years` and `/fiscal-periods` (ETag/If-Match, 428/412/404/409/403, anti-CSRF, no-store).
- **UI:** one shared screen (`pages/FiscalYears.tsx`, Company → Fiscal calendar) with per-period actions driven by server capabilities, reason dialogs, create-year form, and an explicit notice while the close checks are not implemented.
- **Tests:** 34 PostgreSQL integration tests (CI table now 502, all executed), 26 unit tests, 20 UI + 4 api-client tests.

## Known limitations / deferred
1. Everything in "Requires the future General Ledger" above; the close checks never block today and are reported as not implemented.
2. `FiscalHistory` is audited but not shown in the unified Configuration History screen; periods created with a year have no individual revision-1 history row (the year's creation event covers them).
3. Year length is fixed at 12 months (no short/long first year, no 13th adjustment period).
4. No migration has been applied to any permanent development database; manual Web/Desktop visual validation is deferred to the final milestone.

## Exceptional reopening approval (implemented)
Reopening a Final Closed period or a closed year is **request → approve/deny/cancel**; nothing changes in the calendar until a different authorized user approves.
- **Request** (`company.fiscalreopen.create`): names the exact period or year, the revision the requester saw (`If-Match`; a stale one is refused) and a reason (≥ 10 chars). One pending request per target (also under concurrency).
- **Approval** (`company.fiscalreopen.approve`, held only by Fiscal Approver): executes the reopening in the SAME transaction after re-validating, now: the request is still pending at the revision the approver saw; approver ≠ requester; approver ≠ the user who closed the object; the requester STILL holds the request permission and is an active member (permissions are re-read, no cache); the target is still in the state and at the revision the request was made for (otherwise `request_stale`, nothing changes). A period returns to **Soft Closed**; a closed year reopens and its periods stay Final Closed.
- **Deny** (an approver other than the requester, optional note) and **cancel** (only the requester) leave the calendar untouched; a decided request is final and can never be revived.
- **Audit:** events 1024 requested, 1025 approved, 1026 denied, 1027 cancelled, plus the reopening event (1019/1023) naming the approved request, requester and approver in its history row (with the reason text, which never enters the audit detail). Every step is in the chained business audit and `FiscalHistory`.
- **PostgreSQL enforces it:** the request table forbids self-approval/self-denial (CHECK), editing and deleting, and revival; a Final Closed period cannot leave that state, and a closed year cannot reopen, unless the same transaction holds an approved request for exactly that object and resulting revision decided by someone other than the closer (deferred constraint trigger). A concurrent request/decision race resolves to one outcome (tested).
- **Residual window:** the requester's permission lives in the system database and the calendar in the company database, so there is no distributed transaction: it is checked immediately before the executing transaction (microseconds), which is the same trade-off as every cross-database check in HASM.

## Permission administration and recovery
- Company administrators cannot grant `company.fiscalreopen.approve` (the service refuses to grant a permission the actor lacks) and cannot put it in a custom role, assign the Fiscal Approver preset, or revoke one (`target_has_higher_privileges`).
- **Supported delegation (offline, audited, no HTTP route):** `Hasm.Admin grant-fiscal-approver <login> <company-id>` and `revoke-fiscal-approver <login> <company-id>`, with the privileged database account like `grant-company-admin`. The user must already be an ACTIVE member of a Ready company (membership is never created), the role is assigned company-wide, and both actions are audited with no actor user in the access audit. This is also the **recovery procedure** for a company with no configured approver (none can be created from inside the company), and the revocation path. Once an approver also holds `access.assignments.create` through a role the company configures, they can delegate in-app by the ordinary rules, since they then hold what they grant.
- Least privilege and separation of duties: the administrator can run the calendar and request reopenings but never approve; the approver role cannot request or run the calendar; no one can approve their own request or an object they closed. An unconfigured company simply cannot reopen final-closed periods (they stay closed), which is the safe default.

## Close checks (mandatory, never silently bypassed)
- API and UI keep saying the checks are not implemented (`closeChecksImplemented: false`, banner), and **every soft/final/year close records `closeChecksNotImplemented: [domains]` in its own history row** while any check domain is a placeholder.
- **Future ledger cannot slip past:** once any financial module reports history (`IFinancialHistoryProbe`), every close is refused with `period_close_checks_not_implemented` until each close-check domain (`pending_accounting_postings`, `inventory_valuation_differences`, `pending_adjustments`) has a real `IPeriodCloseProbe`; probes run on the closing transaction's own connection. A new company table also fails the schema-contract test until it is deliberately approved. No period is ever described as reconciled.
