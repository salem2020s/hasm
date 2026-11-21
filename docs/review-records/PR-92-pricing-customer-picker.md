# Independent review record

* **Pull request:** #92 (Pricing UI: searchable customer picker)
* **Head SHA reviewed (full 40 characters):** 677b60d5c9848afbaf9d406568bd922d721e4dd9
* **Reviewer context:** Agent C, a separate session from the author (Agent E); model family Claude Opus, a DIFFERENT family from the author. Prompt: independent FULL-diff review, read-only, of the server-side customer `SearchPicker` in the price-list, discount and quote forms: request volume and debouncing against the #90 API, the empty search sending the paged form, stale responses, selection identity, the customer id sent by the quote, no-permission degradation, `noneLabel`, the unchanged customer/group exclusion, table names from the preload, whether the three adjusted tests are weakened, a11y, ar/en and RTL, Web/Tauri parity, no client-only security.
* **Scope read:** `git diff origin/main...677b60d` plus the `SearchPicker` debounce and stale-response logic, the api-client and the #90 API.
* **Checks run (commands and results):** `npm ci --ignore-scripts` (0 vulnerabilities); typecheck exit 0; packages/ui 36/36 passed; apps/web 478/478 passed (60 s timeout allowed for machine load, none needed); `npm run build` OK; hosted CI 10/10 green on 677b60d; the branch merges cleanly with main e0999c1.
* **Findings:** none above Low. Four Low, recorded as follow-ups and not fixed here:

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| L1 | Low | `Pricing.tsx` load | The full unparameterized customer preload remains, for names in the tables. | Accepted follow-up (backend: a by-id lookup). |
| L2 | Low | `CustomerSelect` | `selectedLabel` is not passed for a preselected customer. | Accepted follow-up. |
| L3 | Low | tests | No quote test for the picked customer id. | Accepted follow-up. |
| L4 | Low | tests / no-permission case | The search box stays enabled without permission, and the exclusion test asserts only the list. | Accepted follow-up. |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** browser RTL and keyboard behaviour; a local Tauri build (hosted `desktop-build` ran green on the reviewed head).
* **Verdict:** approve
