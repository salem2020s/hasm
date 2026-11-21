# Independent review record

* **Pull request:** #155 (constraint-violation matrix of the mutating company endpoints, part 1; test-only)
* **Head SHA reviewed (full 40 characters):** f7bcc78a48d39f70beb6939ee493d688d185694b (round 1 reviewed 1b5e89cb0947cdcd18630a406b5855d22dec08d2; the fix delta 1b5e89c..f7bcc78 confirmed)
* **Reviewer context:** Agent G, a separate agent; the author is Agent C. Not the author's own context.
* **Scope read:** the full diff of the PR against `origin/main`: the test class `EndpointConstraintMatrixTests` and the registry entry.
* **Checks run (commands and results):** by the reviewer, see the reviewer lines below. By the author: isolated runner `EndpointConstraintMatrixTests` 4/4 at 1b5e89c, 4/4 in three consecutive runs after the fixes, and 9/9 with `EndpointPermissionMatrixTests` on the merged tree f7bcc78; main's ci-guard, check-docs and scan-secrets pass.
* **Findings:** 0 Critical, 0 High, 1 Medium, 3 Low.

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| M1 | Medium | `Every_mutating_company_endpoint_is_probed_or_listed_as_pending` | The completeness test was a tautology: `Pending` was derived as all routes minus `Probed`, so a new route would become pending silently. | Fixed: `Pending` is an explicit list of the 106 routes, grouped by family. The test asserts that `Probed` and `Pending` do not overlap, that together they equal the permission matrix's routes exactly (an unclassified route fails, and so does a listed route the API no longer maps), and pins the counts (60 probed, 106 pending). |
| L1 | Low | `Race` | Any 4xx with an error code was accepted for the losers. | Fixed: every probe names its expected refusal, and any other answer is a finding. Duplicates expect their own code (for example `409:account_code_taken`, or `400:code_taken` as the field code of `validation_failed`). Stale revisions expect `412:revision_conflict`. Transitions expect their conflict code (for example `409:entry_not_draft`, `409:barcode_already_inactive`). |
| L2 | Low | barcode transitions | A transition without a revision could not detect a double application. | Fixed: each barcode race must have exactly one winner, the other answers must be the expected 409, and exactly ONE business audit event must be written per state change. |
| L3 | Low | registry | The count must be reconciled with #149 and #151 at merge. | Recorded: reconciled at the merge turn in the registry queue (1175 + 4 = 1179 after merging main 0009bd2 in f7bcc78). |

Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent full-diff review of Draft PR #155 constraint-violation matrix part 1: probes trigger the DB constraint or race, concurrency genuine, pending list cannot silently drop routes, no assertion accepts a 5xx, registry +4"; Head SHA reviewed 1b5e89cb0947cdcd18630a406b5855d22dec08d2; executed: full diff vs origin/main read, isolated PostgreSQL EndpointConstraintMatrixTests + EndpointPermissionMatrixTests 9/9 passed, mutation (unique-violation mapping removed for tax and sales-representative codes) failed the matrix with 18 findings of 503 (the losers reach the PostgreSQL constraint), reverted; verdict request changes; findings M1 (completeness test is tautological: Pending is derived, so a new route becomes pending silently; commit an explicit pending list or pin its count), L1 (any 4xx with a code accepted for losers; assert the expected status/code per probe kind), L2 (barcode transitions cannot detect a double application), L3 (registry count to reconcile with #149/#151 at merge).


Confirmation: Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "confirm the #155 fix delta 1b5e89c..f7bcc78: pinned codes are the intended domain codes, the completeness assertion fails on a new route, the audit-event assertion counts the right stream"; Head SHA reviewed f7bcc78a48d39f70beb6939ee493d688d185694b; executed: delta diff read, every pinned code located in its owning service, isolated PostgreSQL EndpointConstraintMatrixTests + EndpointPermissionMatrixTests 9/9 passed, mutation (one pending route removed) failed the completeness test naming the route, reverted; verdict approve; findings M1, L1, L2, L3 fixed; new Lows: the audit count is table-wide (correct table, not filtered by stream or entity), and the fiscal-year race could also legitimately answer fiscal_year_name_taken.

* **Follow-ups (Low, from the f7bcc78 confirmation; not fixed in this PR):**
  * the barcode audit-event count is table-wide: filter it by the barcode's subject (or event type) so an unrelated concurrent event cannot mask a double application;
  * the fiscal-year duplicate race should also accept `409:fiscal_year_name_taken`, which is a legitimate refusal of the same request.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** nothing open for this review; hosted CI on f7bcc78 is the merge-time evidence (checked at the merge turn).
* **Verdict:** approve
