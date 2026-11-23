# Independent review record

* **Pull request:** #227 (identity integrity slice S-1); delta confirmation 97943ae..ef1d5a7 (the review at 97943ae is `PR-227-identity-s1-review.md`, record f06c939)
* **Head SHA reviewed (full 40 characters):** ef1d5a72d2e9bb0c7f7d40a3aaee9cf00b7239ea
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt (summary): "confirm the fix delta: H1 (the guard allows only unchanged or exactly +1, new PostgreSQL test, mutation M9), L1 (the revision check moved into `SessionRow.UpdateAsync` as a plain read after a FOR SHARE version deadlocked against revoke-all: is it race-safe?), L2 (docs), registry 1738, a merge of main. Re-run the overflow probe and the step-up-versus-revoke-all race against the new head; check the merge by remerge-diff".
* **Scope read:** the fix commit bca112e (migration 0036 and its generator, `SessionIntegrity.cs`, the two new tests, the plan's section 4.2, the registry) and the merges a3edfb6 and ef1d5a7 (main 435c44d, including PR 224's S8c follow-ups, which were reviewed there).
* **Checks run (commands and results):**
  * **H1, UPDATE path:** fixed. The guard now refuses any change other than "unchanged" or "+1" (`the security revision of a user only stays or grows by exactly one`). My probe's `UPDATE … SET SecurityRevision = 9223372036854775807` as the runtime role is refused. The new test exercises a jump, a decrease, and deactivation, password change and revoke-all after allowed sequences.
  * **H1, INSERT path (re-run of my overflow probe, extended):** NOT fixed. The guard is a BEFORE UPDATE trigger, and the only bound on INSERT is the column check `>= 0`. The runtime role holds INSERT on the user table. As the real restricted role on the isolated cluster:
    * `INSERT` of a user row with `SecurityRevision = 9223372036854775807` succeeded;
    * that user signed in, and its session was valid;
    * `SetStateAsync(…, Disabled)` threw `bigint out of range`;
    * **the user stayed Active and its session stayed valid.**
    
    Minting a user row is vector B, which S-2 closes. But before S-1 an administrator could deactivate a minted user; S-1's revision bump inside deactivation makes such a user impossible to deactivate. See H1'.
  * **L1, race-safety of the plain read:** safe.
    * `SessionRow.UpdateAsync` now reads the user's revision without a lock and refuses when it differs from the row's revision at issue. It runs while the step-up or selection already holds the session row (`FOR UPDATE`).
    * Revoke-all locks the user row (the bump) and then the session rows, so a lock on the user row here, in the opposite order, would deadlock. The plain read avoids that.
    * If revoke-all bumps after the read: either it waits for the session row lock held by the step-up and then revokes the row the step-up re-tagged (the revocation's `WHERE RevokedAt IS NULL` is re-evaluated after the wait), or the re-tagged row carries the old revision, which `FindActiveSessionAsync` refuses.
    * The plain read is therefore defence in depth with no race that admits a session. The new test covers a stale row whose revocation was cleared.
  * **The race, re-run:** the class `IdentitySessionIntegrityTests` (10 tests, including `A_concurrent_step_up_and_revoke_all_never_leave_a_live_session`) plus my probe ran 11/11 through the CPU gate on the isolated cluster against ef1d5a7.
  * **L2:** the plan's section 4.2 now states that `RevokedAtUtc` and `RevokedReason` are outside the tag (OD-5), and documents the revision guard. Resolved.
  * **Merges by remerge-diff:**
    * a3edfb6 (main) merged without conflict.
    * ef1d5a7 has one conflict, the registry title, resolved to 1738.
    * The registry is 1738 over 258 classes = main's 1728 + `IdentitySessionIntegrityTests` 10 (the class has 10 role-gated tests).
    * No protected file is in the diff; `ci-guard --base origin/main` (main's copy) is clean.
  * Hosted CI on ef1d5a7, read by REST: backend-build-test, postgres-integration, ci-guard, secret-scan, dependency-scan, docs-traceability and guard-selftest all completed success.
* **Findings:** 0 Critical, 1 High (H1', the remainder of H1), 0 Medium, 0 Low open. L1 and L2 are resolved; H1 is resolved for UPDATE only.
  * **High H1' (the overflow remains reachable through INSERT).** A runtime-credential holder can insert a user row with the maximum revision and sign in as it (vector B, open until S-2). The administrator then cannot deactivate it, nor revoke its sessions or change its password, because the bump overflows and rolls the operation back.
    * Resolution: bound the column with a CHECK that applies to INSERT and UPDATE alike, far below the bigint maximum, for example `CHECK (SecurityRevision >= 0 AND SecurityRevision < 4611686018427387904)`. With the +1 step rule a genuine value can never come near it, the bump can never overflow, and a restore of genuine rows is unaffected.
    * Alternatively, a BEFORE INSERT guard that requires 0, but a CHECK is simpler and also covers a data-only restore.
    * Add tests: as the runtime role, an INSERT with a revision at or above the bound is refused; a mutation that removes the bound fails a named test.
    * Because 0036 is not merged (and will be renumbered 0037 after PR 229), adjust it in this PR.
* **Blocker or High findings open:** H1' (High) open; it must be fixed before merge.
* **NOT VERIFIED:** the following two items.
  * The full local suite: hosted postgres-integration on the exact head is the evidence.
  * The renumbering of 0036 to 0037 after PR 229, which needs its own short confirmation.
* **Verdict:** request changes

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: delta confirmation of PR 227 (H1 guard, L1 plain-read race safety, L2, merges, registry 1738); Head SHA reviewed ef1d5a72d2e9bb0c7f7d40a3aaee9cf00b7239ea; executed: delta read, the overflow probe re-run on UPDATE (now refused) and on INSERT (still reachable: the minted user cannot be deactivated), the identity class including the race 11/11, remerge-diff of both merges, registry 1738 equal to the sum, no protected file, ci-guard clean, hosted CI success; verdict request changes; finding H1' (High: overflow through INSERT; add a CHECK bound).
