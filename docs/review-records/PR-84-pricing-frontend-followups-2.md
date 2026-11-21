# Independent review record

* **Pull request:** #84 (Pricing UI follow-ups 2)
* **Head SHA reviewed (full 40 characters):** f53be5f541e6f63ee403f63bd943ae87b72a3c28
* **Reviewer context:** Agent C, a separate session from the author (Agent E); model family Claude Opus (claude-opus-5-5), a DIFFERENT family from the author (claude-sonnet-5-5). Prompt: independent FULL-diff review, read-only, of the PR #80 Low fixes L1-L7 (fan-out and caching, a11y wiring, isolate characters, exact-text tests, Web/Tauri and ar/en parity, no client-only security).
* **Scope read:** `git diff origin/main...f53be5f` (4 files) plus `SearchPicker.tsx`, the auth `handleError`, `numberInput.ts` and `messages.ts` for context.
* **Checks run (commands and results):** `npm ci --ignore-scripts` (0 vulnerabilities); typecheck exit 0; packages/ui 33/33 passed; apps/web 466/466 passed in 35 files (60 s timeout allowed for machine load, none needed); `npm run build` OK; hosted CI green on the reviewed head, including frontend-build-test, desktop-build and e2e-smoke.
* **Findings:** no Critical, no High, no Medium; six Low, recorded as follow-ups and deliberately NOT fixed so that the approved code stays unchanged.

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| L-a | Low | `useItemNames` | The per-id fan-out is still sent when the item list itself failed (a 403 is then certain); no retry of transient 5xx; batches of 50. | Accepted follow-up (skip the fan-out after a failed list). A batch-by-ids endpoint is a backend follow-up. |
| L-b | Low | `codeName` | Option text starts with U+2068, which probably breaks native-select keyboard type-ahead by code and carries invisible characters on copy/paste (unverified in a browser). | Accepted follow-up; candidate fix: keep the code first and unwrapped, isolate only the name. |
| L-c | Low | uniqueness test | The test is pairwise, not catalog-wide. | Accepted follow-up. |
| L-d | Low | test title | Overclaims: the unset base currency, the 50-id cap and skip-when-unknown are untested. | Accepted follow-up. |
| L-e | Low | currency notice | A 403 shows the generic currency warning. | Accepted follow-up. |
| L-f | Low | `SearchPicker` | No unit test for the new props (`id`, `invalid`, `describedBy`, `required`). | Accepted follow-up. |

* **Outside the scope of this PR (recorded):** `field.account_not_found` and `field.account_not_postable` collide between the ledger and party-role catalogs; the party-role text wins, so the ledger and journal screens can show the control-account wording.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** browser RTL/LTR rendering and native-select type-ahead; a local Tauri build (hosted `desktop-build` ran green on the reviewed head).
* **Verdict:** approve
