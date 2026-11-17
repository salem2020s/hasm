# Independent review record

* **Pull request:** #45 (Phase 3.8B-3, negative-stock exception)
* **Head SHA reviewed (full 40 characters):** b0684a8f294e4f0a6cbb619e022d2b20d452da41
* **Reviewer context:** separate agent (Claude Opus 5.5), NOT the author's context; prompt: adversarial code review of the full diff; the fixes below are by the author.
* **Scope read:** the full diff `origin/main...b0684a8` (migrations 0020 and 0022, engine, posting, reversal, adjustment, probe, accounting configuration, API, shared UI, tests, documents).
* **Checks run (commands and results):** by the reviewer: none. By the author after the fixes: see `docs/PHASE-3.8B-3.md` (Review of PR #45) and the commit that records this file.
* **Findings:** no Critical, no High. DB-1 to DB-4 judged sound.
  * **M1 (Medium, fixed)** — valuation-policy race: group valuation and company-default changes run under the inventory master lock, not the posting lock, and the obligation insert trigger checked only the position's method.
    Fixed: the trigger locks every group of the item's chain and the inventory settings row `FOR SHARE` and also requires the item's live resolved method (`ResolvedMethodFunction`) to be moving average. Test
    `NegativeStockReviewTests.M1_…` (direct insert refused after a policy change; a concurrent policy change and exception posting never leave an open obligation under another method).
  * **M1 CI follow-up (fixed)** — on CI (head 704406d) the policy change won the race and the posting met the new trigger as a raw database error (HTTP 500). The posting now locks the flagged items, their group chains and
    the inventory settings row `FOR SHARE` first and refuses a flagged line that would exceed the stock but no longer resolves to moving average, is tracked or inactive with the business conflict
    `negative_exception_item_ineligible` (409 with the line field), before anything is written; the trigger stays the backstop. The concurrency test asserts the clean answer for BOTH orderings; a sequential test covers the
    policy-changed-first path (and shows tracking switched on after the approval is refused cleanly by the existing line re-validation, 400). The concurrent test passed 5 of 5 local runs. Other new trigger paths were scanned: each has a service pre-check under the same locks or is mapped to a stable code (flag `negative_*`, guards
    `negative_obligation_open`, journal `account_automatic_only`); the deferred and empty-operation triggers are backstops of service checks.
  * **M2 (Medium, fixed)** — approving committed a cost position (the plan created positions with their method fixed). Fixed: approvals and previews plan READ-ONLY (a missing position reads as empty with the
    method its policy resolves to); only posting creates the position. Test `M2_M4_…`.
  * **M3 / M5 (Medium, fixed)** — the live receipt counter G could equal the O or V of an open obligation (settlement journal netting, reconciliation wrong). Fixed: refused at settlement
    (`negative_settlement_account_conflict`) and at configuration save (`receipt_counter_equals_open_obligation_account` on `receiptCounterAccountId`; V already holds K when V was not configured). Test `M3_M5_…`.
  * **M4 (Medium, fixed)** — the exception preview took the company-wide posting lock and could leave position rows. Fixed: no advisory lock, read-only plan, rolled-back transaction. Test `M2_M4_…`.
  * **Low (fixed)** — the manual amount was sent as a JavaScript number: now sent as the typed decimal text; the API accepts a string or a number for it.
  * **Low (fixed)** — the group guard now evaluates the open quantity before walking the group chain.
  * **Low (follow-up, not fixed)** — the approver's permission is not re-checked at posting (the approval records that the approver held it when approving; the stock posting service has no access service). Follow-up:
    re-check at posting when the posting service gains an authorization context (3.8C/3.9).
  * **Note (follow-up)** — data exposure of the close report: it is company-wide (`company.inventoryaccounting.view`) and lists obligations of every warehouse, including warehouses the viewer may not see in the stock
    screens. Accepted for the close (an accounting function); follow-up: filter or redact per warehouse scope if a non-accounting role ever receives that permission.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the reviewer ran no tests. GitHub Actions on the fixed head not yet run (no PR update by the author's instruction).
* **Verdict:** approve after the Medium fixes (all fixed above).
