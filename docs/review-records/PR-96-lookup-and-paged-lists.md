# Independent review record - PR #96, party-role lookup by ids and search/paging of units, item groups and customer groups

* **Pull request:** #96
* **Head SHA reviewed (full 40 characters):** 2562f1c5acf1561d2d6d2220c0fa09f0da6fe905
* **Reviewer context:** Agent C, a separate session from the author (Agent D), model family Claude Opus (claude-opus-5-5, a different family from the author's claude-sonnet-5-5), fresh context, read-only. Prompt: independent FULL-diff review, read-only, of the party-role ids lookup (max 100, the 400 shapes, combination with the other parameters, total, default page 500), the optional search/limit/offset/total for units, item groups and customer groups (backward-compatible shape, ancestor valuation of paged item groups), the shared `ListPaging`/`PagedSql` helpers (database-side case folding, window total, past-the-end count), the changed `PartyRoleSearch` pattern test, the api-client parameters and empty-array binding, unchanged authorization and company scoping, injection, ordering, tests and the registry.
* **Scope read:** the full diff against `origin/main` at the reviewed head (c307755 base), including the endpoints, services, the shared helpers, the api-client and every test file.
* **Checks run (commands and results):** Release build 0 errors; unit project 1130 of 1130; `Hasm.Api.Tests` 69 of 69; frontend typecheck 0 errors; api-client tests 41 of 41; PostgreSQL through the Linux harness with the single-holder lock (PartyRole*, CustomerGroup*, InventoryApiTests, InventoryMasterDataTests) 42 of 42, 0 skipped, cluster destroyed afterwards; registry +6 (`PartyRoleTests` 10, `PartyRoleApiTests` 5, `InventoryMasterDataTests` 15, `InventoryApiTests` 4, `CustomerGroupTests` 6, `CustomerGroupApiTests` 2). The author additionally ran the full suite locally: 1103 of 1103.
* **Findings:** no Critical, no High, no Medium; three Low, recorded as follow-ups and not fixed here so that the reviewed code stays unchanged. The `PartyRoleSearch` LIKE-pattern test now expects the un-folded pattern; this is a correct consequence of moving case folding into PostgreSQL (it fixes #90 L1) and not a weakening.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| L1 | Low | An empty `ids` array means "everything" over HTTP (the framework binds an absent repeated parameter as an empty array; the api-client sends nothing for it) but "nothing" in the service. | Recorded follow-up: consumers must not call the lookup with an empty id list (frontend guard); a later api-client change may short-circuit an empty list. |
| L2 | Low | `RoleSelect.Replace("SELECT r.", ...)` is a fragile way to add the window count to the shared select text. | Recorded follow-up (build the select with the count column explicitly). |
| L3 | Low | A non-numeric `limit` or `offset` and a malformed `ids` value give the framework's 400 (no stable error code). | Recorded follow-up (documented behaviour). |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** the hosted PostgreSQL run on the reviewed head at the time of writing (the registry-only merge of main that follows does not change the reviewed code); behaviour at tens of thousands of rows was reasoned about (ordered LIMIT/OFFSET with a window count over the code ordering), not measured.
* **Verdict:** approve
