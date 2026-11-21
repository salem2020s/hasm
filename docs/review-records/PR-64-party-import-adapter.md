# Independent review - PR #64, Phase 3.9C slice 2 (party import adapter)

* Reviewed: the full diff against `origin/main` at head `7b92beb` (adapter, `PartyService.CreateInWorkAsync` refactor, registration, tests, texts, registry, docs). Reviewer: a separate read-only subagent (general-purpose, Sonnet family, fresh context; static, it did not run the PostgreSQL suite). **No Critical, no High; two Medium, four Low.**
* Verified: the `CreateAsync` refactor preserves behavior (same writes, audit events, order, validation and error mapping; delivery still after commit); validation parity with a manual create (`PartyValidation.TryLocal/TryIdentity`, the same identifier normalization) and re-validation at apply; correct exact/conflict/invalid classification, framework duplicate detection and baseline hash; plan hash recomputed under the apply transaction; atomicity; authorization identical to the manual path (`company.parties.create` upload, `company.parties.view` view, independent approver re-checked at apply); the adapter only inserts a pending local party and queues a NewIdentity request (never links, overwrites or delivers); no leak (issues carry field names and codes only); registry raise-only (1011 -> 1015 = 4 tests).

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | No texts for `imp.recon.parties_to_create` and the apply failure codes `code_taken` / `party_invalid`. | Fixed (Arabic and English). |
| M2 | Medium | Docs claimed delivery "through the ordinary synchronization"; the only trigger is the manual Sync action. | Fixed: docs state that proposals stay pending until Sync runs (no automatic trigger). Optional best-effort post-commit sync recorded as a follow-up. |
| L1 | Low | The unique-violation catch matched any unique violation. | Fixed: restricted to `LocalParty.UniqueCode`. |
| L2 | Low | The rollback test asserted only "not applied". | Fixed: asserts `simulated_failure` and the batch status `Failed`. |
| L3 | Low | Not covered: inactive existing party, in-file duplicate, missing `parties.create`, approver without `parties.view`, import audit events. | Recorded follow-up (the framework tests cover duplicates and permissions generically). |
| L4 | Low | A malformed plan JSON field raises and surfaces as `apply_failed`. | Accepted. |
