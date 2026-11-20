# Independent review record - PR #77, QA gap tests slice 1 (pricing API isolation and concurrency)

* **Pull request:** #77
* **Head SHA reviewed (full 40 characters):** a5905f2b42fbe8541eac881ebb16f77d91c1bbe7
* **Reviewer context:** Agent C, a separate session (claude-sonnet-5-5, the same model family as the author, fresh context, read-only, reporting to the lead only). Prompt: "independent FULL-diff review, read-only, report to the lead only, of PR #77 (tests only): are the tests weakened or vacuous, registry +2, no protected files, honesty of scenario coverage". Independence is limited to a separate context; the model family is the same as the author's.
* **Scope read:** the full diff against `origin/main` at the reviewed head: two files, `tests/integration/Hasm.Database.IntegrationTests/PricingApiTests.cs` (two new PostgreSQL tests) and `.github/workflows/backend.yml` (registry raise only).
* **Checks run (commands and results):** code reading only by the reviewer; the reviewer executed nothing. Hosted CI on the reviewed head: ci-guard, backend-build-test, guard-selftest, postgres-integration, secret-scan, dependency-scan and docs-traceability all success.
* **Findings:** no Critical, no High, no Medium; five Low, all recorded as follow-ups and deliberately NOT fixed here, so that the approved code stays unchanged.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| L1 | Low | Company B's PUTs of company A's list and entry assert only `NotEqual(OK)`; an exact 404 would be stricter. | Recorded follow-up (tighten to the exact status once confirmed). |
| L2 | Low | The assertion "entries of an unknown list is 200 with an empty array" locks in current behaviour and will fail, rightly, if the endpoint is later corrected to 404. | Recorded follow-up (update the test together with that endpoint change; the no-leak assertions stay). |
| L3 | Low | The race test does not read back the stored price list and does not assert the loser's code (`revision_conflict`). | Recorded follow-up (read back the list and assert the code). |
| L4 | Low | Company B's own state is not read back after its write (its list exists with revision 1). | Recorded follow-up. |
| L5 | Low | The merged customer-group routes (PR #75) are not covered by these isolation tests. | Recorded follow-up for the customer-group slice. |

**Honesty of coverage:** these tests prove API-level company scoping for pricing (the shape of scenario 21.3) and revision-fenced concurrent edits over HTTP. They do NOT prove customer or supplier balance isolation (no subledger exists) and do NOT prove database-role isolation (the test host reaches every company with one principal). Scenario 21.3 therefore stays PARTIAL. The registry rise is exactly +2 (1029 to 1031 at the reviewed head; per-class sum equals the total). No protected file is touched; the only workflow change is the raise-only registry.

* **Blocker or High findings open:** none
* **NOT VERIFIED:** nothing was executed by the reviewer (CI results above are the hosted run on the same head); database-role isolation between companies; balances.
* **Verdict:** approve
