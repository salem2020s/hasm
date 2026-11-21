# Independent review record

* **Pull request:** #123
* **Head SHA reviewed (full 40 characters):** b98853fceb17723b5d038133f80f5dea637b1a07 (delta confirmation; the review of the PR commit was at 8cf855ea725b2a7dd73f9a502555dcefa85dd1c1)
* **Reviewer context:** Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent review of #123 (H's #118 follow-ups L2/L3/L6, commit 8cf855e only): comma-as-decimal risk, parseDecimalInput grouping, money-field parity, exact bounds, error.password.* accuracy"
* **Scope read:** `git log`/parents (fcd7aa7 is the merge of main, 0282f92 the #118 base); `git show 8cf855e` in full; numberInput.ts; remaining Number() uses in the 5 pages; parser use in Pricing/Taxes/PartyRoles; PasswordPolicy.cs
* **Checks run (commands and results):** own worktree `npm ci`; `npm run typecheck` (exit 0); `npm test` (web 41 files / 805 tests, ui 48, third workspace 37; all passed)
* **Findings:** 0 Critical, 0 High, 0 Medium, 4 Low:
  * L1 (regression): the StockDocuments base-quantity preview used Number(l.quantity), so "٢٫٥" or "2,5" showed "—". Fixed in d138ddac0265fd0c44020a5374755fdbf8da6b7e (parsePositiveDecimal; preview assertions for Arabic digits, comma and Latin added).
  * L2: the shared parser accepted "1234,567". Fixed in d138ddac0265fd0c44020a5374755fdbf8da6b7e (any comma followed by exactly three digits is refused, whatever the integer length; tests added; this also tightens the money fields, as intended).
  * L3: Arabic too_repetitive wording. Fixed in d138ddac0265fd0c44020a5374755fdbf8da6b7e (now "عدد الأحرف المختلفة قليل جداً (أقل من 4)").
  * L4: hard-coded 12/128 limits. Fixed in d138ddac0265fd0c44020a5374755fdbf8da6b7e (comments state they must follow the PasswordPolicy constants).
* **Blocker or High findings open:** none
* **NOT VERIFIED:** hosted CI on the final head (checked by the lead before merge).
* **Verdict:** approve


Confirmation: Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "confirm #123 delta 8cf855e..b98853f (L1-L4 fixed in d138dda; record; merge of main)"; Head SHA reviewed b98853fceb17723b5d038133f80f5dea637b1a07; executed: git log/parents (d138dda fix, 0055aa7 record, b98853f merge of main b8f551e), `git show d138dda` read in full, merge check `git diff b8f551e b98853f --stat` (only #123's 15 files), own worktree `npm ci`, `npm run typecheck` (exit 0), `npm test` (web 41 files/810 tests, ui 48, third workspace 37; all passed); verdict APPROVE; findings 0 open (L1, L2, L3, L4 fixed in d138ddac0265fd0c44020a5374755fdbf8da6b7e).
