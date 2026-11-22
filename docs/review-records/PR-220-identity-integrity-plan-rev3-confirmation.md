# Independent review record

* **Pull request:** #220 (identity integrity plan for register item SEC-RT-SESSION, revision 3, documentation only); delta confirmation 31e3aab..317bdc0 (revision 2 reviewed in `PR-220-identity-integrity-plan-delta-confirmation.md`, record 2fe38ad)
* **Head SHA reviewed (full 40 characters):** 317bdc0de9f9f92f5c3ac98a378130d793d99d66
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt (summary): "confirm revision 3: Q1 fixed by an append-only, MAC'd MfaAttemptLog from which every second-factor limit is derived, written before verification under a per-user lock; sessions bound to the anchored SecurityRevision through UserRevisionAtIssue; R2 and R3; the new slice S-2b; and judge whether the narrow OD-5 residual (a single logout is not a revision bump) is acceptable; finalise OD-1..OD-8 if approved".
* **Scope read:** the full delta of `docs/IDENTITY-INTEGRITY-PLAN.md` (sections 4.2, 5.1, 5.2, 10, 11, 12 tests 3d, 3e and 7, 13, 15).
* **Checks run (commands and results):**
  * **Q1, the second-factor limits (5.2, S-2b, test 3d).** The limiter no longer trusts any mutable counter.
    * `MfaAttemptLog` is append-only for the runtime role (INSERT and SELECT, guard trigger). Every record carries an `identity-session` MAC over all its columns, and invalid or unknown-key records are ignored and alerted. A forged success therefore cannot reset a limit, and a forged failure cannot lock anyone out.
    * The per-user, per-(user, source) and per-challenge limits are all derived from valid records. `LoginThrottle` keeps only the password throttle, which the plan correctly argues is moot against a holder of the database credentials (the PBKDF2 hash can be cracked offline).
    * The attempt record is committed before verification under a per-user transaction advisory lock, so a crash, a timeout or a burst cannot exceed the limit.
    * Deleting throttle rows, resetting the challenge `Attempts` or re-arming a challenge changes nothing.
    * A success resetting the count is acceptable here: it is ordinary login-throttle semantics, and only a genuine correct code produces a valid success record.
    * The remaining online-guessing rate for someone holding a stolen password is the same as without the database credentials. That baseline is the existing throttle policy, not something this plan must change. **Q1 is resolved.**
  * **Session replay and revocation (4.2, test 3e).**
    * A session is valid only when its `UserRevisionAtIssue` EQUALS the user's current, anchor-protected `SecurityRevision`.
    * Password change, revoke-all and administrative revocation bump it, and the surviving session is re-tagged in the same transaction.
    * A pre-revocation session row written back carries a valid old tag but a stale revision, and a row with the new revision cannot be tagged without the key, so revocations can no longer be undone. **Resolved.**
  * **R2:** `UserRevisionAtIssue` is in section 4.2 and in the migration of section 10. **Resolved.**
  * **R3:** revoke-all and the membership-revocation clear of the selected company become per-row compare-and-swap that retries until applied; a row with an invalid tag is revoked unconditionally; a concurrency test is added. **Resolved.**
  * **The narrow residual (judged as asked):** a single logout writes `RevokedAt` on one row and is not a revision bump. A thief who holds BOTH the database credentials AND that session's stolen token can write the pre-logout row back. **Acceptable**, for four reasons:
    * it needs two independent compromises;
    * it defeats only a single-device logout;
    * the user's and administrator's stronger remedies (password change, revoke-all, administrative revocation) are revision bumps and stay effective;
    * bumping on every logout would sign the user out of every device each time.
    
    A cheaper full closure exists if the owner wants it: a tagged per-session revocation record in `IdentityAnchor` (kind session, subject session id), checked by the lookup with one indexed probe. It is offered in T3.
  * **Migration guard (PR 188):** the delta adds only a new append-only table, NULLable columns and indexes; nothing refused by the guard.
  * Hosted CI on 317bdc0, read by REST: the docs jobs completed success; backend jobs are path-filtered.
* **Findings:** 0 Critical, 0 High, 0 Medium, 4 Low (T1 to T4), each recorded with a follow-up for the implementing slice. Q1, R2 and R3 of revision 2 are resolved.
  * **Low T1 (the per-user advisory lock can be held by the runtime role).** Advisory locks are not privilege-controlled: a holder of the runtime credentials can take the same `pg_advisory_xact_lock` key and keep a transaction open, so every second-factor verification of that user waits. Follow-up (S-2b): take the lock with a bounded wait (`pg_try_advisory_xact_lock` in a short retry loop, or a `lock_timeout`) and answer the neutral refusal on timeout, so an API thread never hangs. Classify it with OD-5's nuisance item (2).
  * **Low T2 (junk records cost verification time).** The runtime role may insert unlimited invalid-tag records for a user inside the window. Every verification MACs every record it reads, so junk is a CPU-amplification nuisance, not a bypass. Follow-up (S-2b): bound the work. Records are immutable, so the API can cache the validity result by record id, and junk is then MAC'd once. Alert at a threshold.
  * **Low T3 (the owner-facing OD-5 omits the remaining residual).** The single-logout remainder appears only in a bracketed technical note after the quoted OD-5 text, so the owner would not see it in the question. Follow-up: add it to the quoted text as item (5), with the two options (bump on every logout, which signs out every device; or a tagged per-session revocation record, one probe per request, which closes it without that cost). The wording is in the scratchpad recommendation.
  * **Low T4 (which changes bump the revision).** Session validity now depends on equality with the user's revision, which increases on EVERY change of a covered user field. That includes the MFA state edges, the issue and spending of an enrolment code, the administrator flag and disabling, not only the three revocations named. Follow-up (S-2): state the full list and its user-visible effect (all the user's sessions end at once). State that a session created in the same transaction as a bump (the enrolment confirmation's reissue, the surviving session) is issued or re-tagged under the new revision, and add a test for each such edge.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** no implementation exists; the attempt log, the revision binding and the per-row retry are judged from the plan, and are S-2 and S-2b test obligations.
* **Verdict:** approve

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: delta confirmation of PR 220 revision 3 (Q1 attempt log, session revision binding, R2, R3, S-2b, the single-logout residual, final OD-1..OD-8); Head SHA reviewed 317bdc0de9f9f92f5c3ac98a378130d793d99d66; executed: full delta read, the attempt-log design checked against forged, deleted and concurrent attempts, the revision binding checked against replayed pre-revocation rows, the single-logout residual judged acceptable, hosted CI read by REST; verdict approve; findings T1 to T4 (Low, recorded with follow-ups in S-2 and S-2b).
