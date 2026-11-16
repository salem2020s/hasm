# Independent review record

* **Pull request:** #33 (Phase 3.7B atomic inventory posting)
* **Head SHA reviewed (full 40 characters):** a306e2568a333b9b283be88feb43ba6b49af9898
* **Reviews:** (1) a full review of commit 0e38fd16601a2cf35ca723d983d9f753ade52ab2 (the whole change against origin/main); (2) a second, separate review of the fix delta 0e38fd1..a306e25 (the commit named above, the last code commit before this record). Later commits only tighten one test assertion and add this record; no production code changed after the reviewed SHA.
* **Reviewer context:** a separate agent in its own context (read-only: it read the code and SQL, ran no builds, no tests and touched no PostgreSQL)
* **Scope read:** StockPosting.cs, StockDocumentService changes, InventoryAccounting.ValidateForPostingAsync, migration 0016 and 0018, permission catalog, endpoints, StockPostingTests, backend.yml registry
* **Checks run (commands and results):** none by the reviewer; the author ran StockPostingTests/StockPostingApiTests/StockPostingHardeningTests (18 of 18 passed) and the migration-count fixtures (84 passed) on the disposable cluster
* **Findings:** no Blocker, no High. Medium-1 configuration version could mix within one posting: fixed (the configuration is resolved once, one version enforced, the validated accounts are the booked accounts). Medium-2 test gaps: fixed with 6 hardening tests (per-account journal, free lines, retry without body, stable closed-period codes, summed transfer lines, deactivated item). Low-1 engine input errors returned as 409: fixed (validation errors). Low-2 retry without amounts: fixed. Low-3 company-wide advisory lock held during a posting, Low-4 draft journal lines carry no cost-centre allocation (a cost-centre-required counter account is refused at submit and the draft can be edited), Low-5 posting lines readable by anyone who can view the document: accepted and recorded for later slices
* **Blocker or High findings open:** none
* **Re-review of the fix delta:** no Blocker, High or Medium. Low: the new accounting_configuration_changed branch has no deterministic test (needs a concurrent configuration save between two reads; accepted as a documented residual gap), one test asserted only failure (now asserts item_inactive), the amount-precision test assumes 2 money decimals, an unconfigured group with only free lines blocks a receipt with value elsewhere (intentional).
* **NOT VERIFIED:** behaviour under production-size data and load (Phase 5.1); the review was static (no execution by the reviewer)
* **Verdict:** approve
