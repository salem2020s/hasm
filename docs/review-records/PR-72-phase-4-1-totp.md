# Independent review record - PR #72, Phase 4.1 slice 1 (TOTP primitive)

* **Pull request:** #72
* **Head SHA reviewed (full 40 characters):** 6a8df7c1adab1a821897fa2dbf15550068ea99c1
* **Reviewer context:** Agent C, a separate session (claude-sonnet-5-5, the same model family as the authors, fresh context, read-only). Prompt: "independent FULL-diff adversarial review, read-only" of the complete diff against `origin/main`. Independence is limited to a separate context; the model family is the same as the authors'.
* **Review history:** first review of head `d1dda9813c25f89eda6fad980b216a488fff721f` (findings below); re-review of head `6a8df7c1adab1a821897fa2dbf15550068ea99c1` after the fixes (section "Re-review" below). The 40-character SHA above is the re-reviewed head.
* **Scope read:** the full diff against `origin/main` at the reviewed head: `Identity/Totp.cs` and `TotpTests.cs` (two files; no migration, no API, no UI).
* **Checks run (commands and results):** code reading only by the reviewer. The author ran nothing locally (no dotnet in the sandbox); hosted CI on the reviewed head: backend-build-test, postgres-integration, ci-guard, docs-traceability, guard-selftest, secret-scan and dependency-scan all success.
* **Findings:** no Critical, no High, one Medium, several Low.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | `Verify` is pure: replay protection depends on the caller storing the matched step, but the doc comment did not say that the store must be an atomic compare-and-set (a read-then-write lets two concurrent requests with one code both succeed). | Fixed in the follow-up commit: the `Verify` comment states the requirement with the compare-and-set shape and the throttling requirement. The enforcement slice is recorded below with its required tests (no enforcement exists in this slice). |
| L1 | Low | Base32 normalisation used culture-aware upper-casing (U+0131 maps to 'I'; full-width forms). | Fixed: ASCII-only normalisation, any character above 127 is refused; tests. |
| L2 | Low | Padding was accepted anywhere after data and in any count. | Fixed: padding is optional but must complete a block of 8 with a count of 1, 3, 4 or 6; tests (accepted and refused forms). |
| L3 | Low | Missing tests: window 0, negative step, leading zeros, empty account and a ':' in the account, a future `lastAcceptedStep`. | Fixed: tests added for each. |

**Tests REQUIRED by the enforcement slice (recorded, not part of this PR; the slice also needs owner decisions 1-6 of the PR description):**
1. Replay under concurrency: two simultaneous sign-ins with the same code, exactly one succeeds (atomic compare-and-set on the stored step, PostgreSQL test); a code from an earlier step than the stored one is refused.
2. Throttling and lockout: wrong TOTP codes feed the same per-login, per-source and global throttles as passwords; a locked login cannot be used to probe codes; the code attempt is counted before the comparison result is revealed.
3. Secret storage: the TOTP secret is stored only encrypted under the key ring (never plaintext, never in logs, audit or error text); a missing or retired key fails closed; rotation re-wraps it.
4. The provisioning URI and secret are shown once at enrolment and never logged, audited or returned again; enrolment is confirmed with a valid code before MFA becomes active.
5. Enforcement: where MFA is configured as required, a password-only sign-in never yields a usable session (Web cookie and Desktop bearer alike); disabling MFA and recovery follow the approved rule and are audited.

## Re-review (head 6a8df7c1adab1a821897fa2dbf15550068ea99c1)

Reviewer: Agent C, a separate session (claude-sonnet-5-5, the same model family as the authors), full diff against `origin/main`, code reading. It verified that M1 and L1-L3 above are fixed as recorded (compare-and-set requirement on `Verify`, ASCII-only strict Base32, the added tests) and recorded the enforcement-slice tests. **No new findings; no Critical, High or Medium finding is open.**

* **Blocker or High findings open:** none
* **NOT VERIFIED:** nothing beyond the primitive was built, so enrolment, storage, enforcement, recovery, the emergency account, second approval and AD/Kerberos are not implemented and not reviewed; interoperability with real authenticator apps (the RFC 6238 vectors pass, apps were not used).
* **Verdict:** approve
