# Independent review record - PR #98, list endpoint Lows (explicit role columns, empty id lookup, malformed list parameters)

* **Pull request:** #98
* **Head SHA reviewed (full 40 characters):** 3861cbbebef164de815bd25e3aec7c9cf3e3de55
* **Reviewer context:** Agent C, a separate session from the author (Agent D), model family Claude Opus (claude-opus-5-5, a different family from the author's claude-sonnet-5-5), fresh context, read-only. Prompt: independent FULL-diff review, read-only, of the explicit party-role column list (window total read at `RoleColumnCount`), the api-client short-circuit for an empty `ids` lookup, and the text-bound `ListParameters` validation of the party-role, unit, item-group and customer-group list endpoints.
* **Scope read:** the full diff against `origin/main` at the reviewed head, with attention to: no other endpoint changed; authorization precedes validation where it did before; the column ordinals of the shared reader and of the paged statement; the parsing of `ids`; the synthetic page of the api-client; the tests.
* **Checks run (commands and results):** Release build 0 errors; `Hasm.Infrastructure.Tests` 1133 of 1133; `Hasm.Api.Tests` 91 of 91; frontend typecheck 0 errors; api-client tests 42 of 42; PostgreSQL not run by the reviewer or the author at the reviewed head (the Linux harness lock was held by another agent; the hosted CI run on the head is the first run of the new PostgreSQL test); registry 1057 (main 1056 + 1).
* **Findings:** no Critical, no High, no Medium; two Low, both FIXED by the author after the review in commit `6a9f83120b6bb199c8b24761ad74008ce25b0c40` (confirmation of the delta by the reviewer is pending).

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| L1 | Low | The synthetic page returned for an empty `ids` lookup carried `can.configure: false`, a permission nobody had checked. | Fixed in `6a9f831`: `can` is absent from that page (the type is now `can?`, documented "use `can` only from the unfiltered list"); the only caller that reads it, `PartyRoles.tsx`, fails closed (`resp.can?.configure ?? false`); api-client test asserts `can` is undefined. |
| L2 | Low | The 400 body named the parameter in a single `field` property instead of the standard validation shape the client's field-error handling reads. | Fixed in `6a9f831`: `{ error: "invalid_request", fields: [ { field, code: "invalid_value" } ] }`; the 22 `ListParameters` API tests and the PostgreSQL test assert the same cases with the new shape (and that no stray `field` property remains). |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** the reviewer's confirmation of the fix commit; the hosted PostgreSQL result at the reviewed head (the fix commit was additionally run locally through the harness: `PartyRole*`, `CustomerGroupApiTests`, `InventoryApiTests` 22 of 22, cluster destroyed).
* **Verdict:** approve
