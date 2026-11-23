# Independent review record

* **Pull request:** #239 (SEC-RT-SESSION identity integrity slice S-2c part 1, system migration 0040 provisional): confirmation of the review fixes
* **Head SHA reviewed (full 40 characters):** e77b297cb604c5a8512b2725adbc7ddb8764d1ee
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt summary: a focused confirmation after the review at 42da8230773bb9aad8bd14517f800dd854054c84 (record 540478a0a3bdd43e5f3f9a5e550b843ca3516217, request changes on H1):
  * H1: a BEFORE INSERT trigger (revision equals the subject's current revision; unknown kinds refused), UNIQUE (kind, subject, revision), and an unknown bound failing closed;
  * M1: a per-user digest of the tag-covered fields in the hash;
  * M2: replay only at or below the verified sequence (argued equivalent to one snapshot by append-only; to be judged);
  * M3: MISMATCH_ENROLMENT_CODE and every password hash marked disposition-required;
  * L1 and L2;
  * two generator scans and IdentityIntegritySchemaDerivationTests excluding 0040 (not a weakening?).

  Re-run the flood probe.
* **Scope read:** 206cf0b and e77b297 (diff 540478a..e77b297, 13 files): the 0040 migration and its generator, IdentityAnchors.cs, IdentityStore.FindUserAsync, IdentityAuditReport.cs, UserRow.ReportDigest, the plan section 16, IdentityAnchorTests.cs, IdentityAnchorSchemaDerivationTests.cs, IdentityIntegritySchemaDerivationTests.cs, tools/schema/system-identity-integrity.cjs and backend.yml.
* **Checks run (commands and results):**
  * `scripts/test-pg/run-isolated.sh "$PWD" --build --filter "FullyQualifiedName~IdentityAnchorTests|FullyQualifiedName~AuditAdminToolTests"` (isolated cluster, through the CPU gate, 87 s) with a temporary reviewer probe: 20 of 20 passed. The probe, as the real restricted runtime role:
    * the flood of 5000 junk anchors at revisions 1 to 5000 above the user's row: refused (P0001 "an identity anchor is written only for the current revision of its subject");
    * a junk anchor at the user's current revision 0: refused (23505, unique (kind, subject, revision); the creation anchor holds it);
    * a junk anchor of an unknown kind: refused (P0001);
    * the user's login is 200 before and after, and the user still has its single genuine anchor.

    The probe was reverted and the tree verified clean.
  * `node tools/schema/system-identity-anchor.cjs --check`, `system-identity-integrity.cjs --check`, `system-mfa-login.cjs --check`, `system-challenge-tag.cjs --check` and `system-user-tag.cjs --check`: all match. `node ci-guard.js` (main's copy) against 42da823 and against origin/main: no weakening found. Registry: main 1794, head 1805, title and per-class sum agree; main is an ancestor of the head. Hosted CI by REST on e77b297: all seven jobs completed success. bin/obj were deleted.
* **Findings:** H1, M1, M2, L1 and L2 are closed; M3 is closed in code, with one register follow-up. No Critical, High or Medium finding is open.
  * H1 (High, from 540478a): fixed. An anchor can only carry its subject's committed current revision, and each (kind, subject, revision) holds one anchor. A junk anchor can therefore never take the slot of a future revision before its legitimate anchor, which is written in the bump's own transaction under the row lock. A junk anchor above a record needs a +1 bump by the attacker, and that makes the user row itself unverifiable. Deactivation of an invalid row bumps without writing an anchor, so junk cannot block it. The scan window can no longer be filled above a valid record, and a window that is all junk is now "unknown" and fails closed (it cannot be reached for a valid user, for the same reason). Proven above, and covered by the PR's flood test.
  * M1 (Medium): fixed. `UserRow.ReportDigest` covers exactly the tag input (id, login, password hash, state, administrator flag, MFA state, revision, the three code columns, key id). It is in the hashed text and is not printed, so any change of a covered field changes the report hash.
  * M2 (Medium): fixed, and the equivalence argument holds. The replay now reads only events with `seq <= VerifiedThroughSeq`. Sequence numbers are unique per stream and the audit rows are append-only, so no event can later appear at or below a verified sequence, and the later snapshot reads exactly the events the verification covered. A test seam appends an event between the verification and the replay and shows it excluded.
  * M3 (Medium): fixed in code. The code columns are reconciled against their issue and activation events (MISMATCH_ENROLMENT_CODE), and every user's password hash carries `NOT_SUPPORTED_BY_EVENTS_DISPOSITION_REQUIRED` in the hashed text and in the rendered report, which part 2 will require a disposition for. Follow-up, not a code change: record the password-hash residual once in the owner-decision register (`docs/INTEGRATION-STATUS.md`), which this PR does not touch, so that the owner rules on the disposition policy (for example, a reset before an administrator is tagged).
  * L1: fixed; the row and its anchors are read in one REPEATABLE READ snapshot.
  * L2: fixed; the creation detail is matched exactly, and anything else is unparsed (a state mismatch).
  * The changed tests are not a weakening. Migration 0040's trigger reads S-1's revision column by its physical name, so S-1's generator scan and IdentityIntegritySchemaDerivationTests, which check that S-1's names appear in no other migration, must skip 0040. 0040's own generator and derivation test still check every name 0040 introduces against all earlier migrations, so a collision is still caught. The exclusion skips the whole 0040 file; listing only the referenced names would be stricter (recorded only).
* **Blocker or High findings open:** none
* **NOT VERIFIED:** `verify-head.js` from main could not be run (GitHub GraphQL blocked in this session, HTTP 403). The full PostgreSQL suite was not run locally (hosted postgres-integration on e77b297 is green). The owner-decision register entry for the password-hash residual is not in this PR.
* **Verdict:** approve
