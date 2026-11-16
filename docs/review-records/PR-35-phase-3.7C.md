# Independent review record

* **Pull request:** #35 (Phase 3.7C reversal, derived accounting states, valuation close probe)
* **Head SHA reviewed (full 40 characters):** 23659683f430b73f038c0ad0b58e45755153e747
* **Reviewer context:** a separate agent in its own context (read-only: it read the full diff, the costing reversal code and the journal service, ran no tests and touched no database); same model family as the author
* **Scope read:** the full diff against origin/main
* **Checks run (commands and results):** none by the reviewer; the author ran StockReversal* (14 of 14) and StockPosting* (18 of 18) on the disposable cluster after the fixes
* **Findings:** no Critical. High H1 (a posting whose journal was reversed outside the stock reversal could never be repaired and blocked the close forever): fixed (a Reversed journal needs nothing more; scenario test). Medium M1 (a later-dated reversal of an unposted journal left the posting period permanently unclosable): fixed (the reversal must stay in the posting period while the entry is not posted; scenario test both ways). Medium M2 (ledger balances on inventory accounts unknown to costing block the close once a posting exists): accepted and documented as a known limitation resolved by Phase 3.27 opening stock. Medium M3 (test gaps): fixed with 5 scenario tests (transfer, submitted/approved journal, posted then reversal posted, journal reversed outside, period rule) and the stable soft-closed code assertion. Low L1 probe full scans (acceptable now; index check at the performance phase), L2 reverser does not need journal permissions (consistent with posting), L3 cancelled reversal draft leaves the stock reversed (probe blocks the close until a manual reversal), L4 a manual draft reversal of the original makes the stock reversal fail with already_reversed, L5 company-wide advisory lock: accepted
* **Blocker or High findings open:** none
* **NOT VERIFIED:** behaviour with large histories and foreign-currency inventory accounts (Phase 5.1 and 3.18); the fixes after the review have not been re-reviewed by a second reviewer (they are small, tested and listed above)
* **Verdict:** approve
