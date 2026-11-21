# Independent review record

* **Pull request:** #106 (By-ids lookup for units and item groups)
* **Head SHA reviewed (full 40 characters):** e64d83ea3ce942d4072423bedbe6ce5684cb3e21
* **Reviewer context:** Agent A, a separate session (the author is Agent E); model family Claude Sonnet (claude-sonnet-5-5). Prompt: the lead's independent FULL-diff review of PR #106 (by-ids lookup of units and item groups): RBAC and company scoping, ids validation and injection, an empty ids list never meaning "everything", ancestor valuation, the frontend reload-by-id after a conflict, and that nothing was removed or weakened. The head SHA field names e64d83e; the code was read at 4a25a58, and e64d83e is only the registry raise in `backend.yml`, checked line by line.
* **Scope read:** the full diff of the PR against `origin/main`.
* **Checks run (commands and results):** Release build 0 errors; frontend typecheck; web 492/492; ui 46/46; api-client 37/37. PostgreSQL tests were not run locally (the cluster was held by another agent); the review relies on the hosted `postgres-integration` run.
* **Findings:** no Critical, no High, no Medium. Two Low:

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| L1 | Low | `packages/api-client/src/index.ts` | `lookupByIds` sat between the `createApiClient` doc comment and `createApiClient`, so that comment documented the helper. | Fixed in c8910f5fe6b3802e0184c916fc74b0510bcc9d5b: the helper is above the comment. |
| L2 | Low | `listInventoryUnits` / `listInventoryGroups` | An explicit `ids: []` sent no `ids`, so it returned the unfiltered list. | Fixed in c8910f5fe6b3802e0184c916fc74b0510bcc9d5b: an explicit empty `ids` is answered locally with an empty page (`total` 0) and NO request, for units and for groups; test added (an absent `ids` still sends the ordinary list). |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** local PostgreSQL execution (hosted CI ran all 1074 tests green on 4a25a58); a local Tauri build (hosted `desktop-build` ran green). The fixes in c8910f5 are not part of the reviewed head e64d83e and are covered by their own test, not by a separate independent review.
* **Verdict:** approve
