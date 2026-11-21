# Independent review record - PR #108, Phase 3.13 slice S2 (document links and partial fulfilment)

* **Pull request:** #108
* **Head SHA reviewed (full 40 characters):** 47f2c0b857951e4823f2f18e44b62fa27bbb08b5
* **Reviewer context:** Reviewer: Agent F (independent accounting/integration QA, separate agent; not the author); model family Claude Opus; prompt: "independent full-diff review of PR #108 (3.13 S2 document links): accounting/data integrity, concurrency of over-fulfilment guard, append-only, idempotency, cross-company/branch isolation and RBAC, migration safety and generator --check, tests at service and SQL level with zero skips, PostgreSQL registry raise-only, consistency with PHASE-3.13 S2 and D-074..D-077".
* **Scope read:** the full diff against origin/main.
* **Checks run (commands and results):** executed: full diff vs origin/main read, Release build, dotnet test (unit 1160/1160, API 100/100), 0028/0029/0030 generator --check, registry sum 1085 to 1092 verified, disposable-cluster PostgreSQL run of DocumentLink*/Commercial*/pin-affected classes 76/76 passed 0 skipped (cluster created and destroyed by the reviewer, sole holder), hosted check-runs on head all success, merge-tree against main 3e5a3eb (conflict in docs/DECISIONS.md).
* **Findings:** Critical 0, High 0, Medium 1, Low 4.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | A link racing a material edit deadlocks (the link locked lines then documents; the edit locks the document then lines), and the resulting 40P01 was unmapped (a likely HTTP 500). | Fixed in 7cbb0d211ddde41b060f75859f315f0bfc0daedc: the 0030 guard (through its generator, no new migration) and the service lock the two documents FOR SHARE in id order BEFORE the lines; 40P01 and 40001 map to `concurrent_change` in both Mutate paths as a safety net; new test `A_link_racing_a_material_edit_never_deadlocks_has_exactly_one_winner_and_keeps_every_invariant` (12 rounds, edit of the target and of the source, exactly one winner, invariants checked); the unit derivation test asserts the document lock precedes the line lock. |
| L1 | Low | Cross-branch existence oracle: `line_not_found` for a source document of another branch. | Fixed in 7cbb0d211ddde41b060f75859f315f0bfc0daedc: the documents are locked and the branch is compared BEFORE any line lookup; a missing line of another branch answers `document_not_found` (asserted). |
| L2 | Low | The test name `Linking_is_branch_scoped_..._across_branches_or_companies` overclaimed cross-company. | Fixed in 7cbb0d211ddde41b060f75859f315f0bfc0daedc: a real second company is asserted not to find, list or read company A's documents. |
| L3 | Low | The plain TRUNCATE assertion should be exactly 0A000. | Fixed in 7cbb0d211ddde41b060f75859f315f0bfc0daedc: `Assert.Equal("0A000", ...)` for the plain form (PostgreSQL checks the foreign key before the BEFORE TRUNCATE triggers); the CASCADE form still asserts P0001. |
| L4 | Low | PHASE-3.13 said "Q10 is open: no screen" after D-079. | Fixed in 7cbb0d211ddde41b060f75859f315f0bfc0daedc: "API only in S2; the UI is S6 (D-079)". |

* **Merge conflict (docs/DECISIONS.md with main's D-078..D-088):** resolved by merge c4c7110 (D-074..D-077 kept before D-078..D-088, each id once); registry raised to main 1085 + 8 = 1093 in 7cbb0d2.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the delta 47f2c0b..7cbb0d2 has not been confirmed by the reviewer yet (confirmation pending). The author ran, through the harness (sole holder, status checked, cluster destroyed), the classes DocumentLink*, CommercialStateMachine, CommercialHardening and CommercialIsolation: 17/17; hosted CI on the new head is checked separately.
* **Verdict:** request changes (round 1, head 47f2c0b); the findings above are fixed, confirmation by Agent F pending.
