# Independent review record

* **Pull request:** #118
* **Head SHA reviewed (full 40 characters):** 8956418c4201900a9dc1eedad5e890072ed51d8e
* **Reviewer context:** Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent full-diff review of #118 frontend i18n/auth-state tests and Arabic-digit fixes: no weakened tests, allowlists, UI-vs-server authorization, digit correctness, translation accuracy, RTL/LTR parity"
* **Scope read:** `git diff origin/main...head`; the new tests, `screenHarness`, `numberInput`/`toLatinDigits`, validation in the 5 pages, App.tsx DEV gating, backend sources of the 11 new codes
* **Checks run (commands and results):** `npm ci`; `npm run typecheck` (exit 0); `npm test` (web 41 files / 800 tests, ui 47, third workspace 37; all passed)
* **Findings:** 0 Critical, 0 High, 0 Medium, 6 Low:
  * L1: a blank unit-decimals value was saved as 0 (Number("") is 0). Fixed in fd918e63b5061d9dd7304f59737f877e47581d4d (`/^[0-6]$/` required before save; test added in inventory.test.tsx).
  * L5: the server-403 test waited a fixed 30 ms and asserted only absence. Fixed in fd918e63b5061d9dd7304f59737f877e47581d4d (waits for a stable loaded state, asserts the translated refusal positively; every failing call except the sign-in and access bootstrap is rejected, so each screen meets the refusal on its own load).
  * L2: follow-up, not fixed: use one shared decimal parser (toLatinDigits, parseDecimalInput and per-page patterns coexist).
  * L3: follow-up, not fixed: StockAdjustmentPanel shows raw server codes.
  * L4: follow-up, not fixed: errorCodes.test cannot see namespace-qualified or runtime-passed codes (documented limitation).
  * L6: follow-up, not fixed: weak_password is translated generically and ignores the `problems` list the server returns.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** G's confirmation of the L1/L5 delta in fd918e63b5061d9dd7304f59737f877e47581d4d; hosted CI on the head
* **Verdict:** APPROVE (L1 and L5 fixed in fd918e63b5061d9dd7304f59737f877e47581d4d; L2, L3, L4, L6 recorded as follow-ups)
