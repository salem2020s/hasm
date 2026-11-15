# HASM — Release 1 final acceptance criteria

Release 1 may be declared complete only when **every** criterion below is met or explicitly recorded as NOT VERIFIED with the missing means named (never estimated). Derived from SRS sections 15, 18, 19 and 21, `docs/DECISIONS.md` (D-038, D-039), `docs/ARCHITECTURE.md` and `docs/REPORTING-REQUIREMENTS.md`. Nothing here adds scope.

## A. Traceability closure
1. Every row of `docs/RELEASE1-TRACEABILITY.md` is MERGED, CONSTRAINT-verified, or NOT MANDATORY (R1); none NOT STARTED, PARTIAL or IN REVIEW.
2. Every phase of `docs/ROADMAP.md` is merged; no open pull request carries Release 1 scope.
3. No SaaS multi-tenant, payroll/HR, manufacturing, mandatory direct bank integration, country-specific e-invoicing or mandatory PITR feature was added (SRS section 1).

## B. Mandatory scenarios (SRS section 21), each with an automated test or a recorded drill
1. Scenario 21.1 — direct sale posts the stock movement once; separate delivery links the movement to the delivery document and the later invoice duplicates nothing
2. Scenario 21.2 — a posted entry cannot be edited, even by a high-privilege user; correction by reversal with approval and references
3. Scenario 21.3 — user of company A cannot query company B balances even with a shared GlobalPartyId
4. Scenario 21.4 — pending accounting state is explicit and blocks period close while material differences remain
5. Scenario 21.5 — back-dated inventory cost recalculated within allowed periods with preview and separate adjustments
6. Scenario 21.6 — voucher allocation, bank match or retry after a disconnection never creates a duplicate receipt or entry
7. Scenario 21.7 — a scheduled run that breaks approval/separation-of-duties rules or targets a closed period fails and the reason is kept
8. Scenario 21.8 — single-company restore without rolling back other companies or the system database; links and attachments verified
9. Scenario 21.9 — cascading menus open on hover and nothing executes before click or keyboard selection
10. Scenario 21.10 — non-descriptive physical names tested on new database, schema upgrade and backup restore

## C. Invariants (SRS section 19) proven by automated tests, including under concurrency
Company database is the source of truth; at most one financial/stock effect per business event; posted documents never changed or deleted; no cross-company visibility; no posting into Final Closed periods and no approval bypass by automatic tasks; no loss of drafts on licence expiry or disconnection; state tracks separated for sales/purchasing documents; full traceability of price, tax, exchange rate, cost and approval source.

## D. Test categories (SRS 18.4) all present and green on the release commit
Unit; integration (PostgreSQL, exact expected counts); end-to-end (Web and Desktop); general ledger; costing; recalculation; import; security (authorization, isolation, injection, uploads, designers); concurrency; backup and restore; installation; upgrade; load.

## E. Performance (SRS 18.1-18.3)
On the reference environment (8 vCPU, 32 GB RAM, 500 GB SSD; Backend and PostgreSQL together; 5 companies; 50 concurrent active sessions) with reference data (20,000 items, 10,000 customers/suppliers, 100,000 invoices, 500,000 entries and stock movements) and a sustained 60-minute load: P95 item/customer search < 1 s; ordinary screen < 2 s; save/post of an ordinary invoice < 3 s; ordinary report < 5 s. If the environment is unavailable the result is **NOT VERIFIED**; the figures are never estimated and the reference hardware is not published as a minimum production requirement.

## F. Licensing and Trial (SRS 15)
Named and Concurrent seats (each active session counts); annual and perpetual behaviour; read/export-only after expiry with drafts kept; Trial limits of one company, two users, one branch, one warehouse and 100 posted business transactions enforced server-side from both clients; offline activation; support policy of 36 months recorded.

## G. Backup, restore, update, installation (SRS 2, 16)
Scheduled and manual backups with retention and encryption; full and single-company restore with attachments without touching other companies or the system database; measured RPO/RTO recorded; signed manual updates with rollback; clean install and upgrade on the three deployment models; TLS to API and database.

## H. Security (SRS 17, 4)
No open Critical or High finding; audit chain verifies after export; secrets never in repository, logs or support bundles; opaque physical names preserved by every migration; MFA and emergency account as specified.

## I. Quality gates
All mandatory tests pass on the final committed tree; all required GitHub Actions checks green on the exact release head; no weakened or disabled test, no reduced expected count; frontend typecheck, production build and Tauri build pass; manual visual review of Arabic RTL and English LTR on Web and Desktop recorded with owner sign-off.

## J. Documentation
SRS-aligned user and administrator documentation for installation, backup/restore, licensing, updates; this acceptance record completed with evidence (commit, run identifiers, drill logs).
