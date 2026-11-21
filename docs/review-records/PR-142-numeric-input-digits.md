# Independent review record

* **Pull request:** #142 (feat(ui): shared whole-number parsing with Arabic-Indic/Persian/mixed digits; retention months field and digit tests; frontend only)
* **Head SHA reviewed (full 40 characters):** 0900517d58f899e306ad984202bfa470e789697f
* **Reviewer context:** Reviewer: Agent H (independent review, separate agent; not the author); model family Claude Sonnet; prompt: lead's independent full-diff review of draft PR #142 (frontend only: `parseIntegerInRange`, retention months field as a text input with numeric input mode parsed to 1..1200, tests for fiscal year, units and retention with Arabic-Indic, Persian and mixed digits): parser correctness for every digit set and the separators, refusing a blank matching the server, dir=ltr and RTL layout, ar/en messages, nothing weakened; Head SHA reviewed 0900517d58f899e306ad984202bfa470e789697f; executed: git diff origin/main...head (6 files, +95/-4), reads of `parseDecimalInput`, `parseWholeNumber`, `parseIntegerInRange` and the retention screen, export of the head with `npm ci`, typecheck, and the retention, numberInput, arabicDigits and inventory tests; Tauri compile and hosted CI not verified; verdict approve; findings 0 Critical/High, 1 Medium, 3 Low.
* **Scope read:** the 6 changed files of the PR (`numberInput.ts`, the retention page, and the numberInput, arabicDigits, inventory and retention tests).
* **Checks run (commands and results):** by the reviewer: as listed above. By the author after the fixes: `npm run typecheck` clean; web suite 45 files, 938 tests passed; `npm run build` OK.
* **Findings:** verdict approve; 0 Critical/High, 1 Medium, 3 Low.

| ID | Severity | Finding | Resolution |
|----|----------|---------|------------|
| M | Medium | the retention test "shows field errors from the server" typed 5000, which the client refuses, so the server-error path was never exercised | Fixed in a2b189b: the test enters a client-valid 60 and the mocked server answers a field error for `rules[0].months` (`duplicate_rule`); it asserts the call happened and that the scoped message is rendered inline (a `field-error` element) and in the banner list. |
| L1 | Low | `monthErr[i] ?? f[...]` let an emptied client error hide the server's inline error, and the client errors were keyed by row index | Fixed in a2b189b: `\|\|` instead of `??`; client errors are keyed by a stable per-row key (rule rows are also keyed by it, so state follows the row). Tests: the server's inline error shows after a client error was emptied; a months error stays on its own rule when another rule is removed. |
| L2 | Low | `parseIntegerInRange` accepted "60.0", "6,0" and "٦٫٠" through the decimal parser | Fixed in a2b189b: it accepts pure digits only (Latin, Arabic-Indic, Persian, mixed); the decimal parser stays for the fields that intentionally use it (fiscal year and units use `parseWholeNumber`, unchanged). Tests in `numberInput.test.ts` and in the retention screen. |
| L3 | Low | convoluted `.replace` in the mixed-digits parser test | Fixed in a2b189b: a plain literal. While doing so the literal was found to be mis-written (it produced five characters and the case still passed through another branch); the corrected literal is `"٢0٢۷"` and the assertion now really covers the mixed form. |

* **Blocker or High findings open:** none; the Medium and the Lows are fixed in a2b189b, pending H's confirmation.
* **NOT VERIFIED:** the Tauri compile (the shell loads the same built frontend; hosted `desktop-build` is the arbiter) and hosted CI on the new head.
* **Verdict:** approve
