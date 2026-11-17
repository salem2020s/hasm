# Independent review record

* **Pull request:** #46 (Phase 3.9A, party registry: shared identity and company-local record)
* **Head SHA reviewed (full 40 characters):** c4aca54726fbe927144eff026e70b2a9df6faa0a
* **Reviewer context:** separate agent (Claude Opus 5.5), NOT the author's context; prompt: adversarial code review of the full diff; the fixes below are by the author.
* **Scope read:** the full diff `origin/main...c4aca54` (system migration 0023, company migration 0021, `Hasm.Infrastructure/Parties`, permissions, runtime privileges, API, shared UI, tests, documents).
* **Checks run (commands and results):** by the reviewer: none. By the author after the fixes: see `docs/PHASE-3.9A.md` (Review of PR #46) and the commit that records this file.
* **Findings:** no Critical, no High. Company isolation, the outbox protocol, authorization, the database invariants and the audit were judged correct.
  * **M1 (Medium, fixed)** — lost update on a shared identity: two change proposals based on the same identity revision could both be approved, the second silently overwriting the first.
    Fixed: the request (company) and the proposal (system) record the identity revision the change was based on (`BasedOnRevision`, CHECKed: required for a change, absent for a new identity, immutable); approving a
    change locks the identity row and refuses a stale base with `identity_changed`; the approver sees the current identity and the stale base, and rejects so the company proposes again on the current revision.
    Test `PartyReviewTests.Two_companies_changing_the_same_identity_cannot_lose_an_update_and_the_rejected_company_sees_why`.
  * **M2 (Medium, fixed)** — an unreachable registry or missing DBA grants on the system tables broke company reads and could abort a synchronization midway; the central endpoints answered a generic error.
    Fixed: `GetLinkedIdentitiesAsync` failures degrade list and get (no shared names, `registryUnavailable` flag shown as a banner); the outcome pull of a delivered request leaves it delivered and the sync continues and
    reports `registryUnavailable`; reconciliation reports the single finding `registry_unavailable`; a change proposal is refused with `registry_unavailable` (503); the central endpoints answer a stable
    `503 registry_unavailable` (Arabic and English texts). Tests `PartyReviewTests.An_unreachable_or_ungranted_registry_degrades_…` and `…The_central_endpoints_answer_a_stable_503_…` (a registry whose database does not exist).
  * **M3 (Medium, fixed)** — the proposing company never learnt why its proposal was rejected. Fixed: the central decision note travels with the outcome and is stored on the completed request (`DecisionNote`, only
    for a rejection) and shown in the Requests tab. Covered by the M1 test.
  * **M4 (Medium, fixed)** — manual recovery paths were undocumented; the registry could be enumerated with a one-character search. Fixed: `docs/PHASE-3.9A.md` documents the path after a company restore
    (`proposal_unknown_locally` now carries the proposal id; an identity administrator decides that proposal) and the `already_linked` -> Rejected path; the registry search requires at least 2 characters
    (`term: too_short`, UI button disabled). Tests: the reconciliation test asserts the proposal id; the M1 test asserts the search minimum.
  * **Low (follow-up, not fixed, by design)** — identity administrators see the proposals of every company (company name, proposed identifying data). This is decision D1: the only central role is the identity
    administrator, who already administers every company. Follow-up: a dedicated central master-data approver role with its own scope, if the owner wants one.
  * **Lows (follow-up)** — the remaining Low observations were relayed to the author only in summary form; none concerns isolation, accounting or security invariants. They are carried as follow-ups of 3.9B
    (customer/supplier layer) and are not fixed in this PR.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the reviewer ran no tests. GitHub Actions on the fixed head not yet observed by the author at the time of writing.
* **Verdict:** approve after the Medium fixes (all fixed above).
