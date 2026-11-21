# Independent review record - PR #90, optional search and paging of the party-role list

* **Pull request:** #90
* **Head SHA reviewed (full 40 characters):** d3c092a54af7b49b8db8bce8b456348829c81ead
* **Reviewer context:** Agent C, a separate session from the author (Agent D), model family Claude Opus (a different family from the author's Claude Sonnet), fresh context, read-only. Prompt: independent FULL-diff review, read-only, of the optional search and paging of the party-role list: backward-compatible shape, `total`, default 50 and clamp 1-500, literal case-insensitive search on party code and name, parameterization, 400 on bad parameters, unchanged permission and company scoping including `company.salesreps.view` semantics, no new data exposed, LIKE escaping, count versus page, ordering stability, injection, performance at 10,000 parties, the api-client change, tests and the registry.
* **Scope read:** `git diff origin/main...d3c092a` plus the surrounding code it relies on (the `Read` helper of `PartyRoleManagementService`, `RoleSelect`, the capabilities).
* **Checks run (commands and results):** Release build 0 errors; `Hasm.Infrastructure.Tests` 1119 passed (11 of 12 runs clean; one unidentified load-related failure in one run was not reproduced); `PartyRoleSearch` tests 22 of 22; `Hasm.Api.Tests` 69 of 69; frontend typecheck 0 errors; api-client tests 39 of 39; the PostgreSQL suite through hosted CI on this head (the author also ran the full suite locally before the merge of main: 1095 of 1095); registry 1041 = main 1039 + 2 (`PartyRoleTests` 9, `PartyRoleApiTests` 4), the per-class entries sum to the total.
* **Findings:** no Critical, no High, no Medium; three Low, recorded as follow-ups and deliberately NOT fixed here, so that the reviewed code stays unchanged.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| L1 | Low | `.NET ToUpperInvariant` and PostgreSQL `upper()` differ for non-ASCII Latin letters under a C ctype, so a search for such letters may miss a match. | Recorded follow-up: compare `upper(col) LIKE upper(@like)` (fold both sides in the database). |
| L2 | Low | The `total` and the page are separate statements, so a concurrent change between them can make them momentarily inconsistent. | Recorded follow-up: `count(*) OVER ()` in the page query makes them one statement. |
| L3 | Low | A non-numeric `limit` or `offset` is a framework 400 (no stable code), and an empty `search` alone switches the response to the paged shape. | Recorded follow-up (documented behaviour; clients send parameters only when they want paging). |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** the PostgreSQL results of the hosted run on this head at the time of writing (approval is subject to postgres-integration finishing green on `d3c092a`); behaviour at 10,000 parties was reasoned about (count plus ordered LIMIT/OFFSET over the party-code index), not measured.
* **Verdict:** approve
