# Independent review record

* **Pull request:** #239 (Draft; SEC-RT-SESSION identity integrity slice S-2c part 1: the IdentityAnchor, the identity audit report and the challenge delete margin; system migration 0040 provisional)
* **Head SHA reviewed (full 40 characters):** 42da8230773bb9aad8bd14517f800dd854054c84
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt summary: adversarial review of the FULL diff of PR 239 against main 1aab0ac33d2d751f7b23907c80c67704188747bb, against the plan (sections 7 and 16), the reviewer's M1 and M3 of PR 220, and OD-3, OD-4 and OD-5. Attack:
  * whether the runtime role can lock out a user or administrator with forged or flooding anchors;
  * whether junk can push valid anchors out of the 5000 window and re-enable rollback;
  * whether a valid anchor can be replayed from another user or revision;
  * whether the report flags the M3 case for users and binds its hash to the complete state;
  * whether the honest limit (no password-hash events) is acceptable for OD-3;
  * the margin trigger and the purge.

  PostgreSQL only through scripts/test-pg/run-isolated.sh; probes and mutations local and reverted.
* **Scope read:** the full diff 1aab0ac..42da823 (19 files, +752/-27):
  * 0040_append_only_anchor_history.sql (the anchor table, its shape CHECK, the index on kind, subject and revision descending, the append-only guard, and the second challenge delete trigger with a one-hour margin);
  * IdentityAnchors.cs (Tag, WriteAsync, ReadBoundAsync with ScanLimit 5000);
  * IdentityStore.cs (the creation anchor at revision 0, the bound read in FindUserAsync, AlertAnchorsAsync, the `anchor` refusal cause);
  * UserIntegrity.cs (an anchor per bump in ChangeAsync);
  * IdentityAuditReport.cs;
  * the Hasm.Admin identity-audit-report command;
  * SystemRuntimePrivileges.cs (the anchor table: SELECT and INSERT);
  * the generator and the system-mfa-login.cjs change;
  * the plan section 16, backend.yml, IdentityAnchorTests.cs, AuditApiAndToolTests.cs and the derivation and pin tests.
* **Checks run (commands and results):**
  * `scripts/test-pg/run-isolated.sh "$PWD" --build --filter "FullyQualifiedName~IdentityAnchorTests|FullyQualifiedName~AuditAdminToolTests|FullyQualifiedName~ChallengeIntegrityTests|FullyQualifiedName~UserIntegrityTests"` (isolated cluster, through the CPU gate, 103 s) with a temporary reviewer probe: 32 of 32 passed. The probe, through the real API and as the real restricted runtime role:
    * a user signs in (200);
    * a VALID anchor at revision 7, above the row's revision 0 (what a written-back older row looks like to the verifier, as in the PR's own test), makes the login refused (401);
    * the runtime role then inserts 5000 junk anchors (random 32-byte tags, key id k1) at revisions 8 to 5007 in one statement. `ReadBoundAsync` now returns no valid bound ("max valid NONE, ignored 5000, scan limit reached True"), and the same login is ACCEPTED (200);
    * the report: a matched user in the untagged state of the rollout gets report hash `13e0c349f5c419e4…`. The runtime role changes that user's password hash (to a password of its choice) and raises its security revision; the new report is again MATCHED, untagged, with the IDENTICAL hash.

    The probe was reverted.
  * Two local mutations, each run on IdentityAnchorTests and each reverted, each caught (1 failed): the anchor floor check in FindUserAsync disabled, and the one-hour margin condition replaced by `false`.
  * `node tools/schema/system-identity-anchor.cjs --check`, `system-mfa-login.cjs --check`, `system-challenge-tag.cjs --check` and `system-user-tag.cjs --check`: all match. `node ci-guard.js --base 1aab0ac` (main's copy): no weakening found. Registry: base 1794, head 1801, title and per-class sum agree. Hosted CI by REST on 42da823: all seven jobs completed success.
  * The tree was verified clean after each mutation and after the probe; bin/obj were deleted.
* **Findings:** one High (H1), three Medium (M1 to M3), two Low (L1, L2) and rulings on the named concerns.
  * H1 (High), IdentityAnchors.ReadBoundAsync and the anchor table: a junk flood switches the anchor off (proven above). The bound is the first VALID anchor among at most 5000 rows read from the highest revision down. When the 5000 rows are all junk, the result is "no bound", and the record is then judged with no lower limit. The runtime role holds INSERT on the table, and the database accepts any revision and any tag of the right shape, so 5000 junk rows above a subject's real anchors take the valid ones out of the window. A record that a valid anchor refuses is then honoured again, which is exactly the rollback OD-4 requires to be impossible ("Prove that previously valid but revoked identity or access records cannot be restored by database-only manipulation"). Flagging "scan limit reached" in an alert does not change the answer.

    For USERS today an actual write-back is still stopped by the S-1 revision guard (no lower revision, INSERT at 0). But the anchor is the OD-4 mechanism the plan relies on for the record kinds of S-3 (roles, assignments, memberships), which have no such guard, and they would inherit this.

    Required, in this slice:
    * bound the junk in the database instead of in the scan. For example, a BEFORE INSERT trigger on the anchor table requires the anchor's revision to EQUAL the subject's current revision (for KindUser: the user row's SecurityRevision, which the bump of the same transaction has already raised), plus UNIQUE (kind, subject, revision);
    * then no anchor can exist above a record's revision. A junk row needs a +1 bump of the record, which makes the record itself unverifiable, and a junk row can never occupy a revision before its legitimate anchor (the anchor is written in the bump's own transaction, under the row lock);
    * whatever the design, "scan limit reached without a valid anchor" must never be read as "no bound";
    * add the flood as a test.
  * M1 (Medium), IdentityAuditReport, the report hash: the hash is not bound to the state that tagging will bless (proven above). The canonical text holds, per user, the id, login, disposition, tag status, administrator flag, state and MFA state, plus the chain counts. It does not hold the password hash, the security revision, the three enrolment-code columns or the key id. Part 2 is to bind `identity-tag` to this hash, so a password hash, an enrolment code or a revision changed after the operator's review leaves the hash unchanged, and tagging would bless a state nobody reviewed. That conflicts with OD-3's "may tag only verified and approved records". Required: add, per user, a digest of exactly the fields the user tag covers (the UserRow canonical input, without the key) to the hashed text. It need not be printed, so no secret appears.
  * M2 (Medium), IdentityAuditReport.BuildAsync, the evidence boundary: verification and replay are not bound together. `AuditVerifier.VerifyAsync` verifies the stream first, and the events are then read in a NEW repeatable-read snapshot with no upper sequence bound. The runtime role holds INSERT on the audit tables, so an event appended between the two is replayed without having been verified. For example, a forged `UserCreated` with `administrator: yes` for a minted row would turn its ORPHAN into MATCHED. This is the rule of the S8d plan (PR 223): authenticated evidence only at or below the verified sequence, read in the same REPEATABLE READ, READ ONLY snapshot as the verification. Required: run the verification inside the report's snapshot and replay only events with `seq <= VerifiedThroughSeq`. The race was analysed from the code, not probed.
  * M3 (Medium, needs an owner decision), OD-3 and the honest limit. The report says the password hash cannot be reconciled because no event carries it, and leaves it to a person. Ruling: that is not something this slice can declare acceptable.
    * OD-3 requires "the complete identity and permission reconciliation report against the authenticated audit history" and an "explicit documented disposition" for "suspicious or unsupported records". A field no event can support is unsupported for every user.
    * Together with the enrolment-code columns, which the report also does not reconcile although issue events exist, it leaves this path open: a pre-rollout tamper that sets a known password hash and a known enrolment code on an EnrolmentRequired administrator is reported MATCHED and would be tagged, and the attacker then enrols his own authenticator.

    Required:
    * reconcile the code columns against the code-issue and code-spend events;
    * mark every user's password hash explicitly as "not supported by events, disposition required" in the report;
    * record the residual ONCE in the owner-decision register for the owner to rule on (for example, a mandatory reset before an administrator is tagged), instead of describing it as accepted.
  * L1 (Low), IdentityStore.FindUserAsync: the row and the bound are read in two statements without a common snapshot. A concurrent legitimate bump between them makes the bound (R+1) exceed the row read before (R), so the request is refused and an operator sees "rejected: anchor", which looks like a rollback. Read both in one statement or one repeatable-read transaction.
  * L2 (Low), IdentityAuditReport: the administrator flag of a creation event is detected with `Contains("administrator: yes")`. The detail is a fixed string, so nothing can be spoofed today; exact equality, with anything else treated as unparsed, is stricter.
  * Ruling, lockout by anchors: not possible. An anchor's tag binds kind, subject, revision and key id under the identity-user key, so a forged anchor, an anchor copied to another user, and an anchor moved to another revision are all junk and are ignored. Junk never raises the bound and never refuses anybody; it is reported (rate-limited) only.
  * Ruling, anchor replay: copying a valid anchor of user A to user B, or to another revision, breaks its tag. A duplicate of a valid anchor (same values, new id) carries the same revision and changes nothing.
  * Ruling, the M3 case of PR 220 for users: users cannot be deleted, so "revoked then re-inserted" for a user means deactivated by an event and re-activated by SQL. The replay gives the event's state and the row disagrees (MISMATCH_STATE), and an administrator flag set by SQL gives MISMATCH_ADMINISTRATOR. A row with no creation event is an ORPHAN. These are covered (the PR's test), subject to M2.
  * Ruling, the margin trigger and the purge: the second trigger refuses deletion until a challenge has been expired for an hour (by the database clock). The purge deletes rows expired for more than a day (by the application clock), so it is unaffected, and the clock-skew window of my L3 of PR 237 now needs a skew above one hour. Closed, with a mutation proof. Information: an application clock AHEAD of the database by more than about 23 hours would make the purge statement hit a refused row and fail as a whole; that is only an availability concern.
* **Blocker or High findings open:** H1 (above): 5000 junk anchors take every valid anchor out of the scan window, and a record that a valid anchor refuses is then honoured again (proven).
* **NOT VERIFIED:** `verify-head.js` from main could not be run (GitHub GraphQL blocked in this session, HTTP 403). The full PostgreSQL suite was not run locally (hosted postgres-integration on 42da823 is green). M2 was analysed from the code, not raced.
* **Verdict:** request changes
