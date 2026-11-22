# Independent review record

* **Pull request:** #220 (identity integrity plan for register item SEC-RT-SESSION, revision 2, documentation only); delta confirmation b99eba4..31e3aab (revision 1 reviewed in `PR-220-identity-integrity-plan-review.md`, record be9bb4c)
* **Head SHA reviewed (full 40 characters):** 31e3aabbb1c71987000efb0f8185d17cc5214ccc
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt (summary): "confirm the revision-2 delta: re-run the H1 enrolment attack against the plan's design on paper; check the MfaChallenge binding against forged and replayed challenges; verify in code that all session creation goes through `IdentityStore.InsertSessionAsync`; check the valid-anchor rule and the net-state reconciliation; give an owner-facing summary with the final OD-1..OD-8".
* **Scope read:** the full delta of `docs/IDENTITY-INTEGRITY-PLAN.md` (sections 3, 4.2, 5, 5.1, 7, 10, 11, 12, 13, 15; 172 lines). For the claims checked: every writer of the system Session table (`IdentityStore`, `MfaStore`, `AccessService`, `MfaPolicyService`), `AuthService.ActivateAndIssueAsync`, `MfaStore.IssueEnrolmentCodeAsync`, `MfaAttemptLimiter` and its `IThrottleStore`, and the runtime grants in `SystemRuntimePrivileges`.
* **Checks run (commands and results):**
  * **H1 re-run on paper against the revised design.** The demonstrated attack needed two runtime writes: the enrolment-code columns, and a forged `enrol` challenge.
    * The code columns (`EnrolmentCodeHash`, `EnrolmentCodeExpiresAtUtc`, `EnrolmentCodeConsumedAtUtc`, null encoded) are now in the user MAC, and `BeginEnrolmentWithChallengeAsync` verifies the user tag BEFORE comparing the code, as does the confirmation before activation. A runtime-written code fails the user tag: refused.
    * The forged challenge needs a keyed `identity-token` hash and a `hasm.challenge.v1` tag: refused as an unknown token.
    * Un-spending a used code changes the covered consumed time: refused (test 3).
    * Rewriting a previously valid user row that still held an unconsumed code is closed by the anchor (or is the OD-4 residual). Even then the attacker needs the plaintext code (80 bits from the CSPRNG, `IssueEnrolmentCodeAsync`; not guessable from the hash) and a genuine `enrol` challenge, which only the password step issues.
    * **H1 is resolved.**
  * **Challenge binding (5.1) against forged and replayed challenges.**
    * Forged: no key, so neither the keyed token hash for a chosen token nor the tag can be produced; refused.
    * A copied row with another `Id` or `UserId`: both are covered; refused.
    * A captured valid row: the attacker has only the keyed hash, never the token, so it is unusable.
    * `ExpiresAtUtc` and the source hash are covered, so a challenge cannot be extended or re-pointed.
    * `RecoveryRequest` has no caller (confirmed: only `Login` and `Enrol` reach `TryBeginAttemptAsync`), and a source-scan test keeps it so.
    * **M2 is resolved,** except that the uncovered attempt counter and consumed time are part of Q1.
  * **Single session-creation primitive (L2), verified in code.** The only `INSERT` into the system Session table is `IdentityStore.InsertSessionAsync` (`IdentityStore.cs` line 267). It is reached from `CreateSessionAsync` (line 255) and from `MfaStore` activation through its `reissue` parameter (line 209). `AuthService.ActivateAndIssueAsync` builds the `SessionIssue` (line 331) that takes that path. Moving the keyed hash and the tag into the primitive makes the claim true. **L2 is resolved.**
  * **Session UPDATE sites, against the CAS list of 4.2.**
    * Step-up: `MfaStore` line 231.
    * Touch: `IdentityStore` line 358, excluded by design.
    * Revoke one: line 370.
    * Revoke all of a user: line 386, a multi-row statement.
    * Select company: `AccessService` line 132 and `MfaPolicyService` line 108.
    * Clear the selected company on membership revocation: `AccessService` line 586, a multi-row statement.
    * The purge DELETE: line 456.
    
    The list in 4.2 is complete, but two entries are bulk statements (R3).
  * **M1:** resolved. The bound is the maximum over VALID-tagged anchors under a known key; invalid, unknown-key and malformed anchors are ignored and alerted, never a lockout; test 3c covers it.
  * **M3:** resolved. The report replays the ordered grant and revocation events per subject and flags a live row the net state does not support: no event, a grant later ended by a revocation (resurrected), or columns differing from the net state. Only supported rows are tagged; test 4 covers it.
  * **L1:** resolved (four subkeys).
  * **Second-factor rate-limit state (Q1).** `MfaAttemptLimiter` keeps its counters in `LoginThrottle` through `IThrottleStore`, on which the runtime role holds SELECT, INSERT, UPDATE and DELETE. The per-challenge `Attempts` counter and `ConsumedAtUtc` are UPDATE-granted and, by 5.1's choice, outside the tag.
  * Hosted CI on 31e3aab, read by REST: the docs jobs completed success; backend jobs are path-filtered.
* **Findings:** 0 Critical, 0 High, 1 Medium (Q1), 2 Low (R2, R3). H1, M1, M2, M3, L1 and L2 of revision 1 are resolved. Each finding is listed with its resolution.
  * **Medium Q1 (rate limits and revocations can be erased or replayed by the runtime credentials).** Section 5.1 leaves the challenge attempt counter and consumed time outside the tag, "because an attacker who resets them can only help someone who already holds the token". That someone is exactly the attacker the second factor exists for: one who knows the victim's PASSWORD. The second-factor limiter's counters in `LoginThrottle` are also fully runtime-writable.
    * So with the runtime credentials plus a stolen password, the attacker can do the following without limit, by repeated password steps (each yielding a fresh, genuine challenge) and by deleting the throttle rows:
      * guess TOTP codes at network speed (3 valid codes in 10^6 with a ±1 window), which succeeds within days;
      * or re-arm a consumed challenge.
    * Session rows likewise have no anchor. An attacker who also holds a stolen session token can write back the pre-revocation row (its old tag is valid) and undo a logout, an administrator's revocation or the revoke-all of a password change.
    * None of this is "the runtime credentials alone", but it removes the protection the second factor and revocation give against a stolen password or token, and the plan neither closes it nor records it as a residual.
    * Resolution (recommended): count second-factor failures in an append-only log (runtime INSERT and SELECT only; no UPDATE or DELETE; a success record carries an `identity-session` MAC), and derive both the per-user throttle and the per-challenge budget from it. This is the pattern already accepted for PR 218's signer attempts. Also bind session validity to the user's revision (valid only when the session's issue revision EQUALS the current, anchor-protected revision), and make password change, revoke-all and administrative revocation bump that revision, so a replayed pre-revocation session row fails. A single logout may stay a recorded residual. Add tests: a deleted throttle row does not restore guesses; a reset challenge counter does not; a replayed pre-revocation session row is refused after a password change.
    * Minimum acceptable alternative: state these as explicit residuals in OD-5, so the owner decides knowingly.
  * **Low R2 (missing column).** The session tag covers "the user's security revision at issue", but section 10 adds only `KeyId` and `Tag` to `Session`. The verifier needs that revision stored to recompute the tag. Add a `UserRevisionAtIssue` column (Q1's recommendation relies on it).
  * **Low R3 (bulk statements cannot be one compare-and-swap).** Revoke-all (`IdentityStore` line 386) and the membership-revocation clear of the selected company (`AccessService` line 586) update many rows in one statement. Each needs a new per-row tag computed by the API, so each must become a read followed by a per-row CAS. For revocations the CAS must RETRY until applied: a conflict (for example a concurrent step-up) must never let a session escape a revocation. A row whose tag is already invalid is not honoured anyway and may be revoked unconditionally. State this, and add a concurrent step-up during revoke-all to the tests.
* **Blocker or High findings open:** none (Q1 open as Medium; it must be resolved in the plan, by the fix or by an explicit OD-5 residual, before approval)
* **NOT VERIFIED:** the following two items.
  * No implementation exists; the MAC, anchor and reconciliation behaviour is judged from the plan.
  * The exact entry points that call the limiter beyond the login and enrolment paths read here.
* **Verdict:** request changes

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: delta confirmation of PR 220 revision 2 (H1 on paper, challenge binding against forged and replayed challenges, single session-creation primitive in code, M1, M3, final OD-1..OD-8); Head SHA reviewed 31e3aabbb1c71987000efb0f8185d17cc5214ccc; executed: full delta read, H1 re-run on paper (closed), challenge binding analysis (closed), every session-table writer traced in code (the single INSERT primitive confirmed; two bulk UPDATE sites found), the second-factor limiter's storage and grants traced, hosted CI read by REST; verdict request changes; findings Q1 (Medium: rate limits and revocations erasable or replayable with the runtime credentials plus a stolen password or token), R2 and R3 (Low).
