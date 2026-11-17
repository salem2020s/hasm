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
  * **Lows (follow-up, not fixed)** — an `already_linked` refusal moves the local party to Rejected although a system link exists (permanent `link_missing_locally`; manual path documented, reconciliation is report-only by D6); the registry revision shown to companies reveals that other parties changed the shared data (negligible); administrator search had no minimum term length (fixed: 2 characters).
    (customer/supplier layer) and are not fixed in this PR.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the reviewer ran no tests. GitHub Actions on the fixed head not yet observed by the author at the time of writing.
* **Verdict:** approve after the Medium fixes (all fixed above).

## Codex independent review and adversarial self-review (2026-11-17)

* **Reviewed baseline:** `71e68ec1d347e9567108cae41b833bd2080aeef4`, plus the CI repair in `PartyReviewTests.cs` and `.github/workflows/backend.yml`; full diff against `origin/main` (`4b79030d1aa039a9106c5a06f0b472fe9ec1b781`), not a selected slice. Final committed-head confirmation is still required.
* **Reviewer context:** separate Codex/GPT-6 agent (`pr46_review`), independent of the author context. Prompt: adversarial review of the FULL PR diff, including backend/API, migrations/maps, permissions, cross-company isolation, outbox/reconciliation, shared frontend/client/translations, tests/fixtures, workflow and docs; validate that the CI correction preserves assertions and covers both approval orderings and concurrent approvals.
* **Checks run by reviewer:** none. Author validation is recorded in `docs/PHASE-3.9A.md`; CI results remain pending at this checkpoint.
* **Verdict:** approve the correction, conditional on final committed-head review confirmation and mandatory exact-head CI. No Critical or High findings; no new Medium finding in the correction.
* **Self-review:** the failed assertion confused history presentation order with request identity under equal timestamps. The correction preserves stale-revision, stored-value, rejection-note and retry assertions, checks both original and rejected requests, and adds simultaneous approvals and reconciliation checks. Expected counts increase by two; no test or gate is removed or weakened. Existing migration checksums and Claude's uncommitted work are unchanged.
* **Medium follow-ups accepted for a separate repair before Phase 3.9B:**
  1. `PartyService.ProposeAsync` reads the live shared revision at submission rather than carrying the revision viewed by the user. Carry and validate the viewed revision, with a stale-form regression. Central approval is still required and the current concurrent-approval guard remains enforced.
  2. `Parties.tsx` reloads the entire form after local Save or shared Propose, losing unrelated dirty fields. Preserve those fields and test both directions.
  3. `PartyApprovals.tsx` and `listPartyProposals` omit paging despite the server's default 50-row page. Add controls and verify later historical records are reachable.
  4. `Parties.tsx` omits per-tab dirty indicators. Add them using the existing shared Tabs API and interaction tests.
* **Blocker or High findings open:** none. The four Medium findings above are not claimed fixed.
* **NOT VERIFIED:** complete local PostgreSQL suite, final committed-head review confirmation and exact-head GitHub Actions; no merge is authorized by this checkpoint alone.

Author validation after the review: five complete runs of all five strengthened PostgreSQL cases against 5433 passed (25/25, no skips); all 34 verification self-tests, secret scan, CI guard, docs/traceability and roadmap consistency passed. Full relevant PostgreSQL, frontend, desktop and final-head Actions validation remain NOT VERIFIED. The earlier publishing restriction is resolved by owner-updated permissions and approved Git access. Final committed-head CI and review confirmation remain pending.
