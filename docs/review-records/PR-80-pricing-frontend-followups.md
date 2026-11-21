# Independent review record

* **Pull request:** #80 (Pricing UI follow-ups)
* **Head SHA reviewed (full 40 characters):** 8ec8f05cdc1a2ae081c1e2a9b68e82e8984324d7
* **Reviewer context:** Agent D, a separate session from the author (Agent E); model claude-sonnet-5-5, the SAME model family as the author. Prompt: "independent FULL-diff review, read-only, report to the lead only, of PR #80 at 8ec8f05, frontend only". Checklist: Web/Desktop parity, server-authoritative security, signed parser edge cases, quote with a searched item, picker race and selection, adjusted tests not weakened, currency-permission behaviour change.
* **Scope read:** `git diff origin/main...8ec8f05` (6 files, +191/-40): `Pricing.tsx`, `messages.pricing.ts`, `pricing.test.tsx`, `numberInput.ts`, `numberInput.test.ts`, `app.css`.
* **Checks run (commands and results):** typecheck exit 0; vitest for pricing and numberInput 23 passed; full web suite 461 passed (other workspaces: 38 and 33 passed); ar/en key parity 133 keys each. Hosted CI on the reviewed head (author-run query): all 10 jobs succeeded.
* **Findings:** no Critical, no High, no Medium; seven Low, recorded as accepted follow-ups and deliberately NOT fixed so that the approved code stays unchanged.

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| L1 | Low | `Pricing.tsx` tables | Item names beyond the preloaded 500 show a raw UUID. | Accepted follow-up: short id fallback, keep picked names, or fetch with `getInventoryItem`. |
| L2 | Low | `ItemPicker` | The label is a `span`, so the field error is not linked through `aria-describedby` / `aria-invalid`. | Accepted follow-up. |
| L3 | Low | `messages.pricing.ts` (ar) | The Arabic `min_margin_hint` "-1000" can render as "1000-". | Accepted follow-up: use U+2212 or a Unicode isolate. |
| L4 | Low | `ItemPicker` options | Option labels lack Unicode isolates. | Accepted follow-up. |
| L5 | Low | `load` in `Pricing.tsx` | A non-401 failure of the currencies call gives an empty currency with no notice. | Accepted follow-up. |
| L6 | Low | `pricing.test.tsx` | The last test reads `messages.pricing.ts` through `node:fs` and `process.cwd()`. | Accepted follow-up: prefer a catalog-level uniqueness check. |
| L7 | Low | assignments table | Rows shown by short id sort among the codes. | Accepted follow-up. |

* **Behaviour change judged acceptable:** without the currencies view permission or a base currency the currency field starts empty (the old hard-coded "JOD" was silently wrong). A follow-up could expose the base currency through a pricing endpoint, which is a backend change and is not part of this PR.
* **Recorded BLOCKED items (unchanged):** the party-role, unit, item-group and customer-group list APIs are unpaged and have no search or total, so the shared `SearchPicker` cannot be adopted for them.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** production build, Tauri build and browser RTL/LTR rendering were not run by the reviewer (hosted CI ran the build and the desktop build on the reviewed head); the reviewer shares the author's model family, so independence is limited to a separate session and context.
* **Verdict:** approve
