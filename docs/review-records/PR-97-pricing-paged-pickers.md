# Independent review record

* **Pull request:** #97 (Pricing UI: ids lookup and searchable pickers)
* **Head SHA reviewed (full 40 characters):** 61ab34eab259abe000911f3bd2131045b71370b6
* **Reviewer context:** Agent C, a separate session from the author (Agent E); model family Claude Opus (claude-opus-5-5), a DIFFERENT family from the author (claude-sonnet-5-5). Prompt: independent FULL-diff review, read-only, of `useCustomerDirectory` (ids lookups in batches of 100, never empty; 403 sets forbidden and the page warning; other errors ignored for names) and the generic `LookupPicker` for units, item groups, customer groups and customers (bounded search of 50, N of M, kept selection, code-first labels, retry, only a real 403 disables search, active customer groups only). Checks asked for: request volume, debouncing and duplicate fetches, stale responses, ids sent, no reliance on `can` from an ids lookup, membership plain selects, a11y, ar/en/RTL, test strength after the combobox migration, and that the net diff touches no review record.
* **Scope read:** `git diff origin/main...61ab34e` (`Pricing.tsx`, `pricing.test.tsx`) plus the `SearchPicker` debounce and staleness logic.
* **Checks run (commands and results):** `npm ci --ignore-scripts` (0 vulnerabilities); typecheck exit 0; packages/ui 37/37 passed; apps/web 489/489 passed (60 s timeout allowed for machine load, none needed); `npm run build` OK; hosted CI 10/10 green on 61ab34e; the branch merges cleanly with main 78edf4d; the net diff to main touches no review record (the earlier accidental copy was restored by the second commit).
* **Findings:** none above Low. Four Low, recorded as follow-ups and not fixed here:

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| L1 | Low | customer-group `LookupPicker` | Inactive rows are filtered on the client, so "N of M" and the page contents are skewed. | Accepted follow-up (backend: a server-side active filter). |
| L2 | Low | `useCustomerDirectory` | Failed or changed names are not refreshed within a session. | Accepted follow-up. |
| L3 | Low | assignments table | Rows re-order as names arrive. | Accepted follow-up. |
| L4 | Low | pricing page | Up to about 7 initial picker requests. | Accepted follow-up. |

* **Intended behaviour change:** the one dropped assertion (the page warning after a 500 on the former customer preload) no longer applies; a 500 of the names lookup is ignored for names, and only a 403 sets the warning.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** browser RTL and keyboard behaviour; a local Tauri build (hosted `desktop-build` ran green on the reviewed head).
* **Verdict:** approve
