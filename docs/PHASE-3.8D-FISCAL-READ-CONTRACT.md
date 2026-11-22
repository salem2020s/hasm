# Phase 3.8D fiscal read contract

Status: implemented internally; focused validation passed, broader regression/new-head CI pending. Not approval or execution authority. SRS9/ARCHITECTURE8 require period-policy barriers and immutable original journals. D-044/D-045 and all normal posting/close locks remain unchanged. Evidence: review-records/PR-48-source-fiscal.md.

## Affected dates, not all history

Require the normal stock-posting Open-period policy for the stored source date and EVERY original entry whose amount, running quantity or running carrying value changes in the complete replay comparison. Derive dates by matching comparison EntryId to the original entries in the same authorized snapshot. A missing or ambiguous match refuses the whole result. Include both transfer legs; deduplicate dates/periods, not financial effects.

Do not apply this barrier to unchanged earlier baseline history. Old stock can originate in a Final Closed period without blocking a new Open-period movement that leaves that history unchanged. Conversely, an unchanged event cost is insufficient to exempt a changed running position. Unknown periods, Soft Closed and Final Closed on affected dates refuse using the existing normal gate's reasons; ordinary stock posting currently passes allowSoftClosed=false, and preview does not invent a permission bypass. No original entry or journal is changed.

The source date's fiscal year must match the stored approved source's FiscalYearId. The evidence additionally binds each used period's identity, year, date range, state and revision, and its year's identity/revision/closed state. Only used periods determine eligibility/fingerprint; intervening unrelated periods may be loaded as bounded metadata but do not independently block. All checks and fingerprint construction occur in the SAME read-only RR transaction/deadline/quota as source and history evidence, after all warehouse quarantine. Final source/cost revocation checks still cover both success and caught fiscal failures.

## Shared policy and bounded reads

Reuse one pure state-to-decision evaluator with FiscalPeriodGate.CheckAsync. The normal posting function keeps its FOR SHARE query and locking semantics byte-for-byte; the read-only adapter does not acquire those row locks or open a write transaction. It is not a replacement for locked revalidation during posting.

Collect at most200,001 affected dates (the existing200k-entry bound plus source). Read fiscal rows only across their min/max range with a remaining-row-budget sentinel, then match dates by sorted non-overlapping intervals; never execute one SQL query per date or grow an unbounded date/period cross product. Missing years, overlapping/invalid intervals and unknown states fail closed. Charge every command and metadata row to the shared budget. Fingerprint only canonical, used evidence, independent of culture or SQL row order.

## Remaining approval/execution scope

This adds source/changed-history fiscal evidence only. Compensation/adjustment dates and contents do not exist yet and therefore are NOT approved or implicitly checked. Their periods, original account/dimension classifications, account-map versions, basis/entitlement/legal policy and exact approval contents must be bound separately before a complete plan can be approved. Later execution must reject changed source/history/period revisions and recheck current permissions under normal posting/close locks. A concurrently closed period can make an earlier read snapshot stale; it cannot authorize execution.

No API/DI/production caller, posting barrier removal, original-journal rewrite or conversion execution is enabled by this helper. Full reversal/negative/conversion coverage, protected proposals/approval/atomic execution/revaluation/shared UI remain mandatory incomplete scope. PR48 remains Draft.

## Mandatory evidence

Open/no-period/SoftClosed/FinalClosed parity with the existing gate; original posting locks retained while read-only lookup takes none; source fiscal-year consistency; unchanged closed earlier history allowed; changed closed history refused even if event cost is unchanged; both transfer legs; final authorization masking fiscal failures; changed period/year revisions affect fingerprints; unaffected intervening periods do not; concurrent close after snapshot versus fresh read; resource/cancellation/read-only footprint; existing fiscal closing/reopening and source regression suites. Independent full-diff design review must precede implementation and review the completed code separately.
