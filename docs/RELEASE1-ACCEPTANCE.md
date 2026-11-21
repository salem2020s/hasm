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

Recorded evidence against G (dated; evidence is not acceptance):
* 2026-11-21 (owner item 11, D-088 with D-085): backup encryption and authenticated manifests, automated and executed: `BackupAcceptanceEvidenceTests` (5 PostgreSQL tests; a real encrypted backup with no plaintext at rest, a trial restore with the offline recovery key equal to the source, refusal before pg_restore of a wrong recovery key, a flipped ciphertext byte, a truncated set, an edited manifest, a removed signature and an unencrypted version-1 downgrade, an unsigned or edited run status counted as failed, recovery and signing key rotation, and the key-export round trip with rotated rings), on top of the slice 5 suites; drill log [DRILL-BACKUP-ENCRYPTION-ACCEPTANCE](qa/DRILL-BACKUP-ENCRYPTION-ACCEPTANCE.md). **Owner item 11 (D-088) is NOT MET for release acceptance.** ALL FOUR of these remain, and each blocks it: (1) the production recovery test with the real offline kit (D-085), performed and recorded per installation; (2) the recorded limitations D-118 and D-099 below, closed or accepted; (3) real-tool TLS to the database during backup and restore, which no test exercises; (4) full-installation restore (the system set) and the live cutover (slice 7 and later), not built. The evidence above covers the trial-restore path only.
* 2026-11-21 (SRS 21 scenario 21.8, trial path only): single-company restore isolation, automated and executed: `CompanyRestoreIsolationTests` (2 PostgreSQL tests; with two companies holding linked parties and plain and sensitive attachments, a trial restore of one leaves the other company, the restored company's live database and the system database byte-identical by per-database content hashes (every public table's rows, every sequence's last value, and the trigger, function and constraint definitions) and every live attachment file byte-identical, the restored copy equals its source and holds nothing of the other company, and an inconsistent set fails closed with its exact findings and changes nothing; the sets are unencrypted v1 sets (DEV/TEST path), so the restore of an encrypted set is not covered by this entry). The live restore cutover (Phase 4.3 slice 7), real backup media, measured RPO and RTO and the independent-approval limitation below are NOT covered: this does not satisfy group G.

Recorded limitations against G (dated; each must be closed or explicitly accepted before release):
* 2026-11-21 (D-118, Phase 4.3 slice 5): trial restore and the backup recovery test run without independent approval until the Phase 4.1 approval framework exists. They run only through the administrative tool under operating-system access, and each writes a system-stream audit event.
* 2026-11-21 (D-099, D-118, Phase 4.3 slice 5): backup-recovery-keygen, backup-signing-keygen and backup-keys run in an interim mode under operating-system access, without a named administrator with MFA, until the Phase 4.1 administrative-tool login (D-099) exists; backup-signing-keygen and backup-keys write system-stream audit events.
* 2026-11-21 (D-086, D-137, Phase 4.3 slice 6): automatic deletion of backup runs is INACTIVE (a code gate changed only by an owner-instructed activation PR, runtime prerequisites and an exact per-installation opt-in); backups accumulate and the operator manages storage. The retention preview (backup-retention-plan) and backup legal holds work; a backup legal hold cannot be released until the Phase 4.1 approval framework exists.

## H. Security (SRS 17, 4)
No open Critical or High finding; audit chain verifies after export; secrets never in repository, logs or support bundles; opaque physical names preserved by every migration; MFA and emergency account as specified.

Recorded limitations against H (dated; each must be closed or explicitly accepted before release):
* 2026-11-21 (Phase 4.1 S1, review M2 of PR #133, lead decision D-048): the system database runtime connection is not least-privilege for user_totp; the hasm.totp_rewrap flag is code discipline only, settable by any session. A system-database runtime-role grant model is an architecture change outside slice S1; it is a follow-up of the S4 authorization or the database-role work, is on the owner/DBA register, and is a blocker for production readiness until closed or explicitly accepted.

## I. Quality gates
All mandatory tests pass on the final committed tree; all required GitHub Actions checks green on the exact release head; no weakened or disabled test, no reduced expected count; frontend typecheck, production build and Tauri build pass; manual visual review of Arabic RTL and English LTR on Web and Desktop recorded with owner sign-off.

## J. Documentation
SRS-aligned user and administrator documentation for installation, backup/restore, licensing, updates; this acceptance record completed with evidence (commit, run identifiers, drill logs).
