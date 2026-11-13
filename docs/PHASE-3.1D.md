# Phase 3.1D — Fiscal Years and Accounting Periods

**Status:** in progress on `feature/phase-3-1d-fiscal-periods` (from main after PR #14). Nothing is applied to any permanent database.

## Approved basis (SRS §5, §9, §16; ARCHITECTURE)
Multiple fiscal years open at once; monthly or quarterly periods; states Open / Soft Closed / Final Closed; closing needs pending material differences and adjustments resolved; exceptional reopening needs approval, permissions and audit; no posting into a Final Closed period, and no bypass by automatic jobs; year balances carried forward without duplication or loss; posted entries are never edited. The company business time zone (3.1A-2) is never defaulted.

## Implemented now (calendar structure and state machine)
Fiscal years (exactly 12 months from the 1st of a month, any month: company-specific calendars such as July 1), generated periods, the three period states, controlled closing/reopening, year closing, RBAC with separate permissions, audit, optimistic concurrency, DB-level invariants, and the backend posting gate for the future ledger.

## Requires the future General Ledger / posting engine (NOT built, nothing fabricated)
Journal entries, closing/year-end adjustment entries, balance carry-forward, opening balances, the content of "pending material differences" checks (inventory valuation, pending accounting postings, pending adjustments), and any posting. Today these are placeholders: `PeriodCloseGuard` probes (unimplemented, reported as such, never block) and `FiscalPeriodGate` / `f_…postable` (ready for the posting engine to call inside its own transaction).

## Design decisions made (reasonable defaults; flagged for owner review)
1. A year is exactly 12 months starting on a first-of-month day; periods are 12 monthly or 4 quarterly, fixed per year, generated with the year and never edited. (Short/long first years and 13th adjustment periods need ledger rules and are deferred.)
2. Years never overlap and form ONE contiguous calendar: the first year starts anywhere; every later year must start the day after the latest year ends (or end the day before the earliest starts). No gaps, so every date belongs to at most one period.
3. Transitions: Open ⇄ Soft Closed ⇄ Final Closed only; Open can never jump to Final Closed and Final Closed can never become Open directly. Exceptional reopening of a Final Closed period returns it to **Soft Closed**, needs a recorded reason (≥ 10 chars) and a **different user** than the one who final-closed it (segregation of duties).
4. Year closing is state only: allowed when every period is Final Closed; reopening a closed year needs a reason and leaves its periods Final Closed. A closed year freezes its periods.
5. Permissions (company-wide): `company.fiscalyears.view/create/close/reopen`, `company.fiscalperiods.close/finalclose/reopen/reopenfinal`. The two exceptional ones (`fiscalyears.reopen`, `fiscalperiods.reopenfinal`) are in the catalog but in **no preset role**; a custom role must hold them.
6. The business time zone must be configured before a year is created and is recorded on the year (informational snapshot). No Trial limit applies: none is approved (D-036 lists none for fiscal years).
7. **Recommendation for owner decision (not implemented):** SRS says reopening needs "approval". This phase implements a one-step exceptional reopening (dedicated permission + reason + segregation of duties). A true two-person approval workflow should be built with the approval engine, together with the ledger.

## Delivery
Company migration 0006, system migration 0009; domain service, authorized facade, API; shared UI; tests on the disposable cluster only; CI count table updated.
