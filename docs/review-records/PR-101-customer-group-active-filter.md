# Independent review record - PR #101 (customer-group active filter)

* **Pull request:** #101
* **Head SHA reviewed (full 40 characters):** 138a20f063989f90335903f96ba2e415b9bc53a7
* **Reviewer context:** Agent C, a separate session from the author (Agent D); model family Claude Opus (claude-opus-5-5). Prompt: independent FULL-diff review, read-only, of the optional active=true|false filter on the customer-group list (shared WHERE for page, window total and past-the-end count; active alone selects the paged form; malformed values give the standard invalid_request fields body; permissions unchanged; api-client parameter; tests; registry). Checked: total correctness with the filter, backward compatibility, tests.
* **Scope read:** git diff origin/main...138a20f (customer-group list endpoint and service, ListParameters use, api-client, unit/API/PostgreSQL tests, backend.yml).
* **Checks run (commands and results):** Release build 0 errors; Hasm.Infrastructure.Tests 1146/1146; Hasm.Api.Tests 100/100; frontend typecheck 0; api-client 42/42; PostgreSQL through the Linux harness lock on a fresh 5433 cluster, CustomerGroup* 10/10, 0 skipped, cluster destroyed. Registry 1070 = 1068 + 2; merges cleanly with main 4cf64b0.
* **Findings:** none.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** hosted CI was partly still running at review time (confirmed by the lead's exact-head verification before merge).
* **Verdict:** approve
