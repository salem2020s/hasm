# Independent review record

* **Pull request:** #109 (Party roles UI: party picker searches the server)
* **Head SHA reviewed (full 40 characters):** 634dba65c787a4758f8ffe47503c2358faffc71c (the delta review; C's full-diff review was at f6917b28e43faba464384e05faec4257847ee793)
* **Reviewer context:** Agent C (independent QA/security, separate session); model family Claude Opus (claude-opus-5-5). Prompt: the lead's independent full-diff review of `origin/main...feature/party-picker-search` (truncation and bounded requests, permission/403 behaviour and server authorization, selection persistence and inactive/used filters, honesty of "N of M", accessibility and RTL/LTR parity on `SearchPicker`, no weakened tests).
* **Scope read:** the full diff of the PR against `origin/main` (`PartyRoles.tsx` and `partyRoles.test.tsx`).
* **Checks run (commands and results):** `npm ci`; typecheck; web vitest 498/498 (testTimeout 30 s); ui 37/37; production build; hosted CI on f6917b2 all green including desktop-build; mergeable with main.
* **Findings:** verdict request changes at the reviewed head: one Medium and two Low.

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| M1 | Medium | `PartyPicker` | A page whose rows are all filtered out on the client (inactive or already holding the role) is reported as "no results" although more matches exist beyond it. | Fixed in e51c77abc4bd7676009a3dcf05cecee25002ce7f on the server: `GET /api/v1/company/parties` takes optional `active` (true/false) and `withoutRole` (customer/supplier; an unknown value gets the standard 400 `invalid_request` naming the parameter). Both are bound SQL parameters applied before paging, with the same permission and company scope as the list; no migration. The picker sends them and no longer filters on the client. Tests: a PostgreSQL API test for both filters including a company whose first 55 parties all hold the role, malformed values, permission and 401; unit tests for the parser; a frontend test for that company; an api-client test. |
| L1 | Low | `PartyPicker` | M was only an upper bound while more matches existed. | Fixed in e51c77a: page and total are exact because the filters are applied by the server. |
| L2 | Low | option labels | Party labels lacked Unicode isolates. | Fixed in e51c77a: code and name are wrapped in Unicode isolates; test added. |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** browser RTL/LTR and keyboard behaviour; a local Tauri build (hosted `desktop-build` ran green on the reviewed head); the fix commit e51c77a: confirmed by the delta review at 634dba6.
* **Round 2 (delta review):** Reviewer: independent reviewer subagent (independent, separate agent); model family Claude Opus; prompt: lead's independent delta review of PR #109 f6917b2..634dba6 with the full diff against origin/main for context (M1/L1/L2 fixes, SQL safety and role-table semantics against the 0023 unique index, RBAC/company scope and 401/403 tests, PostgreSQL first-page test and registry 1086 / PartyRoleApiTests 8, no weakened tests, record validation); Head SHA reviewed 634dba65c787a4758f8ffe47503c2358faffc71c; executed: git fetch, git diff f6917b2 634dba6, reads of PartyService.ListAsync, migration 0023 and the schema/enum, node check-review-record.js (main copy) on the record against f6917b28e43faba464384e05faec4257847ee793 (complete), npm ci, npm run typecheck (pass), web vitest 501/501, api-client 48/48, ui 37/37 (PostgreSQL not run; hosted CI on 634dba6 reported green by the lead); verdict approve; findings none (Critical/High/Medium/Low: 0; informational: a malformed filter answers 400 before the 401 check, consistent with the existing list endpoints).
* **Verdict:** approve
