# Independent review record

* **Pull request:** #227 (identity integrity slice S-1: keyed session token hash, session tag, one tagged insert primitive, user security revision; system migration 0036); full review of origin/main..97943ae
* **Head SHA reviewed (full 40 characters):** 97943ae76e78b200abb1e8041253a69ecceac9a0
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author; author of the SEC-RT-SESSION finding and reviewer of the plan). Model family Claude Opus. Prompt (summary): "independent adversarial review of S-1 against the merged plan `docs/IDENTITY-INTEGRITY-PLAN.md` and the owner's OD-1..OD-8. Attack it with the real restricted system runtime role and the real API: mint or tamper sessions, restore old rows, raise or lower SecurityRevision, race step-up against revoke-all, rotation edge cases, a missing key failing open anywhere; verify the mutation proofs; check that the OD-5 single-logout residual is documented and pinned by a test, not claimed eliminated. Concerns: (1) the change to `MigrationTriggerSwitchAllowlistTests` (PR 188's security test); (2) audit code 1339; (3) SecurityRevision not tamper-evident until S-2: the runtime role can raise it; (4) unqualified relations that a TEMP table could shadow".
* **Scope read:** the full diff (41 files): `Security/IdentityKeys.cs`, `Identity/SessionIntegrity.cs`, `Identity/IdentityStore.cs` (`InsertSessionAsync`, `FindActiveSessionAsync`, `RejectSessionAsync`, `RevokeUserSessionsAsync`), `MfaStore.cs` (step-up CAS, reissue, code 1339), `MfaPolicyService.cs` and `AccessService.cs` (company selection and membership clear), `AuthService.cs`, `HasmAuthentication.cs` and `AccessEndpoints.cs` (wiring, 503 mapping), `FileEncryption.cs` (`IdentityPreviousKeyIds`), system migration `0036_session_tags_and_security_revision.sql` with its generator and dictionary, the new tests (`IdentitySessionIntegrityTests` 8, `IdentityKeysTests`, `IdentityIntegritySchemaDerivationTests`), and every adapted existing test.
* **Checks run (commands and results):**
  * **Code review against the plan.**
    * The four subkeys use the TOTP-precedent derivation.
    * The token hash is HMAC under `identity-token`.
    * The session tag is HMAC under `identity-session` over id, user, keyed token hash, client, creation, absolute expiry, methods, proof time, emergency, selected company, revision at issue and key id, with a canonical, length-prefixed, typed encoding and times truncated to the microsecond.
    * The ONE insert primitive (`InsertSessionAsync`) reads the revision `FOR SHARE` and writes the tag; there is no other session INSERT.
    * `FindActiveSessionAsync` requires a token hash under an accepted key, a tag that verifies under an ACCEPTED key id, and a revision at issue EQUAL to the user's current revision. Anything else is revoked, answered with the neutral null, and audited (rate-limited).
    * Step-up and company selection are compare-and-swap on the old tag, under the row lock.
    * The membership clear re-tags by CAS, or revokes rows it cannot re-tag (stricter than retrying).
    * Revoke-all, password change and deactivation bump the revision in the same transaction; the kept session is re-tagged by CAS.
    * Migration 0036 is additive (NULLable columns and checks, a revision column `NOT NULL DEFAULT 0` with a check, and a never-decreases trigger).
  * **Missing key, read for fail-open:**
    * no keys or no active key throws `EncryptionUnavailableException`, which the API maps to 503 `encryption_unavailable`;
    * a request without a token stays anonymous (401 on protected routes);
    * paths that need no key (revoke-all without a kept session, the membership clear) revoke instead of keeping;
    * the idle touch runs only after a successful lookup.
    
    No fail-open path found; the suite's missing-key test asserts 503, that no session is minted, and that nothing is revoked.
  * **Rotation:** only the active key and `IdentityPreviousKeyIds` that the ring can still produce are accepted; any other ring key is not. New values are written under the active key, and a re-tag moves the tag to it. Because the token hash stays under the issuing key, a session ends when that key leaves the list, which matches OD-6's grace. Tested.
  * **My probe on the isolated cluster** (through the CPU gate; the real restricted role from `grant-system-runtime`; the real stores and API; a temporary test, removed afterwards). As the runtime role, `UPDATE user SET SecurityRevision = 9223372036854775807` succeeded (the guard only refuses a DECREASE). The account then signed in normally (the new session carries the maximum). Afterwards:
    * `SetStateAsync(…, Disabled)` threw `bigint out of range`, and **the user stayed Active with the session valid**;
    * the password change threw the same error;
    * revoke-all threw the same error;
    * the session was still valid at the end.
    
    See H1.
  * **Mutations (local, reverted; tree clean after each), PostgreSQL class `IdentitySessionIntegrityTests` through the CPU gate:**
    * base: 9/9 with my probe;
    * removing the revision equality in `FindActiveSessionAsync` fails `A_password_change_and_a_revoke_all_cannot_be_undone_…` (7 of 8 pass);
    * removing the revision bump of revoke-all fails the same test;
    * making the tag check always true fails `Every_tampered_security_field_…`.
    
    The author's eight proofs listed in the PR are consistent with these three.
  * **The adversarial suite (read; it runs with the real restricted role and the real API):**
    * minted rows (plain SHA-256, forged tag, copied row, own user and administrator) are refused;
    * eleven tampered fields each give revoked, neutral, audited;
    * restoring rows after a password change and after a revoke-all is refused, and the database refuses a lower revision;
    * a concurrent step-up and revoke-all never leave a live session;
    * CAS refuses a stale writer;
    * the legitimate flows work under the restricted role.
  * **OD-5:** `KNOWN_RESIDUAL_OD5_a_single_logout_can_be_undone_by_the_runtime_role_until_the_logout_record_slice_exists` pins the residual. It is documented in `SessionIntegrity.cs` and in the test, bounded by the absolute expiry, and NOT claimed as eliminated. Correct.
  * **Concern 1 (`MigrationTriggerSwitchAllowlistTests`):** the test builds a synthetic next migration with a DISABLE/ENABLE pair at `System_.Count + 1` and asserts the refusal names it. The old literal `0036` equalled that number only while there were 35 system migrations, and S-1 adds 0036 itself. The new assertion derives the number from the same expression the test passes in and still asserts the exact name, number, set, triggers, checksum and Q1 text. **This is not a weakening;** it is the only correct form once migrations are added. The pinned list of 26 allowlisted migrations is unchanged, and 0036 contains no trigger switch.
  * **Concern 2 (audit code 1339 `IdentityRecordRejected`):** used nowhere else, and it follows the MFA family 1330-1338 in the system stream. It fits and can be allocated.
  * **Concern 3 (raising the revision):** raising by any amount signs the user's sessions out, which on its own is OD-5's accepted nuisance (2). Raising it to the maximum, however, disables every later bump (H1). A step limit fixes both.
  * **Concern 4 (TEMP shadowing):** the only database code that runs in an attacker's session is the new trigger `f_4fb9f223`, which references NEW and OLD only (no relation, no declared variable), so a TEMP table or type cannot shadow anything in it. The API's SQL runs in the API's own sessions, which an attacker's temporary objects cannot reach.
  * **Registry and counts:** 1735 over 258 classes = main's 1727 + `IdentitySessionIntegrityTests` 8 (the class has 8 role-gated tests). The pinned migration counts are raised from 35 to 36, as the merge turn requires. No protected file is in the diff; `ci-guard --base origin/main` (main's copy) is clean.
  * Hosted CI on 97943ae, read by REST: backend-build-test, postgres-integration, ci-guard, secret-scan, dependency-scan, docs-traceability and guard-selftest all completed success.
* **Findings:** 0 Critical, 1 High (H1), 0 Medium, 2 Low (L1, L2). Each is listed with its resolution.
  * **High H1 (a runtime-credential holder can make an account impossible to disable or sign out).** The guard trigger refuses only a decrease of `SecurityRevision`. The runtime role (UPDATE on the user table) can set it to the bigint maximum. Every later `+ 1` bump then overflows, and deactivation, the password change and revoke-all each perform that bump inside their own transaction, so they fail and roll back: the account stays Active and its sessions stay valid (probe above). An attacker who holds the runtime credentials and any account's credentials (a phished user, an insider) can therefore defeat the administrator's incident response (disable the account, revoke its sessions, force a password change).
    * Resolution: change the guard to allow the revision to stay the same or grow by EXACTLY one per update (`NEW.rev = OLD.rev OR NEW.rev = OLD.rev + 1`). Reaching the maximum then needs about 9.2×10^18 updates, and the bump can never overflow. Because 0036 is not merged yet, adjust it in this PR (generator, dictionary and derivation test).
    * Tests: as the runtime role, a jump larger than +1 is refused; deactivation, password change and revoke-all still succeed after the runtime role has raised the revision by +1 any number of times in a loop; a mutation that removes the step limit fails a named test.
  * **Low L1 (CAS writers re-bless a row whose revision is stale).** Step-up (`MfaStore`) and company selection (`SessionRow.SelectCompanyAsync`) check `IsValid` (tag) but not the revision equality. A row whose revision is stale (for example a revoked row whose revocation was cleared) is re-tagged under the active key with its stale revision. This is harmless, because `FindActiveSessionAsync` still refuses it and every such request first passes that lookup, but the writers should refuse it too: pass the user's current revision and require equality. This is defence in depth.
  * **Low L2 (the merged plan and the implementation differ on the revocation columns).** The plan's section 4.2 lists `RevokedAtUtc` and `RevokedReason` as covered by the session tag; the implementation leaves them uncovered and relies on the revision for revoke-all and password change, pinning the single-logout residual (OD-5). The outcome is the same (covering revocation would not stop a pre-logout row being written back) and is within OD-5, but the plan text should be updated in the next plan touch so that plan and code agree.
* **Blocker or High findings open:** H1 (High) open; it must be fixed before merge.
* **NOT VERIFIED:** the following two items.
  * The full local suite (hosted postgres-integration on the exact head is the evidence; the author reports 1791/1791).
  * The remaining five of the author's eight mutation proofs (three re-run here, each caught).
* **Verdict:** request changes

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: adversarial review of identity-integrity S-1 (PR 227) with the real restricted role and API (minting, tampering, replay, revision manipulation, races, rotation, missing key, OD-5 residual, the guard-test change, code 1339, TEMP); Head SHA reviewed 97943ae76e78b200abb1e8041253a69ecceac9a0; executed: full diff read, a probe proving that a revision raised to the maximum blocks deactivation, password change and revoke-all, three mutations each caught by a named test, the guard-test change judged equivalent, registry 1735 equal to the sum, no protected file, ci-guard clean, hosted CI success; verdict request changes; findings H1 (High: revision overflow blocks incident response), L1, L2.
