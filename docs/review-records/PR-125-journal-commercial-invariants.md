# Independent review record

* **Pull request:** #125 (test/journal-commercial-invariants: journal posting guard against direct writers, commercial approval period lock, rate traceability)
* **Head SHA reviewed (full 40 characters):** 34190b997ecbf9bf4ef80c531f3df7958a222020
* **Reviewer context:** Agent H, independent review by a separate agent (not the author, Agent F); model family Claude Sonnet; prompt: lead's independent full-diff review of #125 (assert existing behaviour and exact refusals only, guard-removal sensitivity of the 0009 posting guard by reasoning, no timing flakiness)
* **Scope read:** `git diff origin/main...34190b9` (`JournalTests.cs`, `CommercialHardeningTests.cs`, `CommercialSnapshotTests.cs`, `.github/workflows/backend.yml`)
* **Checks run (commands and results):** as stated in the record line below
* **Findings:** 0 Critical, 0 High, 0 Medium, 3 Low: L1 and L2 fixed in 52580e5677dcbfd2f46b47b4aafa8d4e9821dc07, L3 recorded (inherent).
  * L1 (Low): the second journal test's final count covered every entry in status 2/3/4 except the approved one. Fixed in 52580e5677dcbfd2f46b47b4aafa8d4e9821dc07: the count is scoped to the three refused drafts the test created.
  * L2 (Low): the usdRate lookup selected the USD rate record by currency code only. Fixed in 52580e5677dcbfd2f46b47b4aafa8d4e9821dc07: it selects the rate record effective on the document date (latest valid-from not after the date).
  * L3 (Low): migration 0009 redefines 0008's posting guard function, so the test proves the guard as finally defined. Recorded: inherent to the forward-only migration design; no change.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the reviewer did not run PostgreSQL locally (the cluster was in use by another holder); hosted postgres-integration succeeded on 34190b9. The L1/L2 delta needs hosted CI on the new head; the registry (1096 from main 1093 at push time) is reconciled with main when this PR's turn comes; not a finding.
* **Verdict:** approve

Reviewer: Agent H (independent review, separate agent; not the author); model family Claude Sonnet; prompt: lead's independent full-diff review of #125 (assert existing behaviour and exact refusals only, guard-removal sensitivity of the 0009 posting guard by reasoning, no timing flakiness); Head SHA reviewed 34190b997ecbf9bf4ef80c531f3df7958a222020; executed: git diff origin/main...head, check of every asserted message against the migration SQL, check of the ExchangeRateRef formats in CommercialDocumentService, hosted check-runs (postgres-integration success); PostgreSQL not run locally (cluster in use by another holder); verdict approve; findings 0 Critical/High/Medium, 3 Low.
