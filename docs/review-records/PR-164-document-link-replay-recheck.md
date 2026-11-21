# Independent review record

* **Pull request:** #164 (DocumentLinkService.CreateAsync re-checks the idempotency key after the line locks: a concurrent same-payload replay returns the original link instead of `link_exists`; found by C2's constraint-violation matrix)
* **Head SHA reviewed (full 40 characters):** 48bf45c64564e9323291398b75668645c421b575
* **Reviewer context:** Agent G, an independent security/audit reviewer and a separate agent (not the author, Agent A); model family Claude Opus; prompt: "independent full-diff review of Draft PR #164 (head 48bf45c, base main 97953a1): payload equality matches the documented contract, no lock-order or deadlock change, no double audit on a replay, the test is meaningful (re-run the mutation that removes the re-check), registry DocumentLinkTests 7 -> 8"
* **Scope read:** the full diff `git diff origin/main...48bf45c` (merge base 97953a1): `src/backend/Hasm.Infrastructure/Commercial/DocumentLinkService.cs` (one added statement), `tests/integration/Hasm.Database.IntegrationTests/DocumentLinkTests.cs` (one new test), `.github/workflows/backend.yml` (registry); for context the whole of `CreateAsync`, `FindByKeyAsync`, `ResolveRaceAsync` and the `Replay` handling.
* **Checks run (commands and results):**
  * payload equality: the re-check compares the same `Canonical(input, kind, quantity)` hash, scoped to the same actor and key, as the first check and `ResolveRaceAsync`; it matches the documented contract ("the same key with the same payload returns the original link (Replayed)"; another payload keeps `idempotency_key_reused`);
  * locks: the re-check is a plain `SELECT` (no row lock) placed after the existing document share locks and line `FOR NO KEY UPDATE` locks, so the lock order (documents in id order, then lines in id order) is unchanged and no new wait is introduced;
  * audit: on a replay the `Replay` exception is thrown before the insert and before `AppendAuditAsync`, and the transaction rolls back, so no audit event is written for the replay;
  * isolated PostgreSQL run (`scripts/test-pg/run-isolated.sh --filter DocumentLinkTests`): 8/8 passed, cleaned up;
  * mutation (the re-check removed), run three times with the new test: it failed every time ("Assert.All() Failure: 3 out of 7" and "4 out of 7" requests not OK); reverted, tree clean;
  * registry: DocumentLinkTests 7 -> 8 and the total 1185 -> 1186, consistent with one new test.
* **Findings:** 0 Critical, 0 High, 0 Medium, 1 Low.
  * **L1 (Low, accepted, follow-up optional):** the new test proves one link and one created result, but does not assert that exactly one `LinkCreated` business audit event exists for the seven requests; the code path makes a double write impossible today, an assertion would pin it.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** hosted CI on 48bf45c was not checked by the reviewer; the registry total is re-set in the merge queue, so the final count must be verified on the merged head.
* **Verdict:** approve

Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent full-diff review of Draft PR #164 (head 48bf45c, base main 97953a1): payload equality matches the documented contract, no lock-order or deadlock change, no double audit on a replay, the test is meaningful (re-run the mutation that removes the re-check), registry DocumentLinkTests 7 -> 8"; Head SHA reviewed 48bf45c64564e9323291398b75668645c421b575; executed: full diff vs origin/main read, isolated PostgreSQL DocumentLinkTests 8/8 passed, mutation (re-check removed) failed the new test in 3 of 3 runs, reverted; verdict approve; findings L1 (no assertion of exactly one LinkCreated audit event).
