# Independent review record - PR #75, Phase 3.11 customer groups (company migration 0027)

* **Pull request:** #75
* **Head SHA reviewed (full 40 characters):** dcd7b107025e9dede65d4291548e193c99fb9306
* **Reviewer context:** Agent C, a separate session (the same model family as the author, fresh context, read-only). Prompt: "independent FULL-diff adversarial review, read-only, report to the lead only, of PR #75" (customer groups, company migration 0027). Independent of the author's context, but NOT a different model family.
* **Scope read:** the full 31-file diff against `origin/main` at the reviewed head (generator and migration 0027, schema dictionary, privileges, service, endpoints, shared Web/Tauri screen and messages, api-client, tests, `backend.yml` registry, `docs/PHASE-3.11.md`).
* **Checks run (commands and results):** code reading only by the reviewer; nothing was executed by the reviewer (hosted CI is the executor). Hosted CI on the reviewed head: all 12 checks success, including postgres-integration (1035 tests) and backend-build-test.
* **Findings:** no Critical, no High, two Medium, several Low.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | `GET customer-assignments` returned the code and display name of EVERY customer to a user holding only `company.pricing.view`. | Fixed in the follow-up commit: the endpoint returns party ids and group data only; the screen joins names from the party-role list it already loads and shows a short id when the user cannot read it; the HTTP test asserts a pricing-view-only user receives no `partyCode`/`partyName`. |
| M2 | Medium | The kept 0026 tie index makes equal-priority rules of different groups, or a group rule against a general rule with the same keys, fail as `ambiguous_rule` (fail-closed, permanent without a loader DROP policy change). | Accepted as an owner decision (the loader is not touched): precisely documented in `docs/PHASE-3.11.md`, and the `ambiguous_rule` message now tells the user to use a different priority, start date or minimum quantity per group (ar and en). |
| L1 | Low | The active-group and customer-role checks did not lock the rows they read. | Fixed: both use `FOR SHARE`. |
| L2 | Low | Deactivating a group changing nothing was not stated on the screen. | Fixed: a prominent note on the Customer groups tab (ar and en) and in the docs. |
| L3 | Low | No test for an inactive CURRENT group in the membership select or for its exclusion from new lists. | Fixed: a component test. |
| L4 | Low | The all-customers membership list does not scale. | Recorded as a follow-up (adopt `SearchPicker` with server paging) in `docs/PHASE-3.11.md`. |

* **Re-review:** "Re-review at head dcd7b107025e9dede65d4291548e193c99fb9306 (Agent C, Claude Sonnet same family, full diff read-only, nothing executed by the reviewer): M1, M2 and L1-L4 verified fixed by reading the delta; hosted CI on dcd7b10 all 12 non-skipped checks green; Low observations recorded as follow-ups (not fixed, to avoid code churn after review): duplicate field.ambiguous_rule key in messages.pricing.ts (customerGroups version overrides it; remove the old entry), assignments table shows the customer code only and is ordered by GUID (sort by code in the UI, show the name when the party-role list provides it), single HTTP flow test for 5 routes; Verdict approve; Blocker/High open: none". The first review above was of head 470a40868bee109f531cd1ca48dfd27161aabf87.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** nothing was executed by the reviewer; behaviour under concurrent load beyond the covered two-writer races, and the Tauri desktop rendering of the new tab (covered only by the shared component tests and the desktop build check), were not verified.
* **Verdict:** approve
