# Independent review record - PR #99, Phase 3.13 slice S1b (approval, numbering, server-produced snapshots)

* **Pull request:** #99
* **Head SHA reviewed (full 40 characters):** d1f2d115d5fce1fa3f85390848268b98194e28d6 (round 3; round 2 reviewed 5ddb2a5e05161ec008cde6fdfa4a170c7b203556, round 1 reviewed 3788c459096388c73b54a3caa76f4e3f8566a414)
* **Reviewer context:** Agent C, a separate session from the author (Agent A), model family Claude Opus (claude-opus-5-5, a different family from the author's claude-sonnet-5-5), fresh context, read-only for code. Prompts are noted per round below.
* **Scope read:** round 1: the full diff of the S1b branch against `origin/main`; round 2: the delta 3788c45..5ddb2a5; round 3: the delta 5ddb2a5..d1f2d11 excluding review records (separating the main merges of #100, #101 and #102 from the author's own changes).
* **Checks run (commands and results):**
  * Round 1: Release build; generators 0028 and 0029 `--check`; unit 1136; API 69; PostgreSQL 135/135 through the harness on the disposable cluster.
  * Round 2: generators `--check` (8/8); Release build; unit 1149/1149; API 91/91; PostgreSQL Commercial and Numbering classes 34/34 on the disposable 5433 cluster (created and destroyed); hosted CI on 5ddb2a5 all green.
  * Round 3: Release build; PostgreSQL Commercial and Numbering classes 34/34 on the disposable 5433 cluster (created and destroyed); hosted CI on d1f2d11 all 7 checks green; mergeable with main d3bee74.
* **Findings:** three rounds, listed per round below (round 1 request changes; rounds 2 and 3 approve).

## Round 1 (head 3788c45): request changes

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | The date of a numbered document can leave the fiscal year of its number. | Fixed in 5ddb2a5: D-068 (refuse); service `date_outside_number_year` on edit and approval; the 0029 guard enforces it for direct SQL (generator edit, no new migration); tests at service and by direct SQL. |
| L1 | Low | Sealed content (tax snapshot and hash, totals, exchange rate, fingerprint) is not frozen out of the submitted status when it is decided. | Fixed in 5ddb2a5: the 0029 guard freezes the quote on submitted to approved, rejected or cancelled; direct-SQL tests. |
| L2 | Low | Tests are below the plan. | Fixed in 5ddb2a5: 20 concurrent approvals; rollback after the number was issued leaves no gap or reuse; per-fiscal-year sequences; a direct-SQL duplicate number is refused by the unique index (`CommercialHardeningTests`, `CommercialNumberingTests`). |
| L3 | Low | The PermissionCatalog comment is stale. | Fixed in 5ddb2a5. |
| L4 | Low | The public `typeRegisteredByCaller` parameter is a bypass of the registry check. | Fixed in 5ddb2a5: the public `IssueAsync` keeps its registry check; an internal `IssueForRegisteredTypeAsync` serves the commercial registry. |
| L5 | Info | Recorded identities. | Informational, no change. |

## Round 2 (head 5ddb2a5): approve

Round 2: Reviewer Agent C (independent QA/security, separate session); model family Claude Opus (claude-opus-5-5); prompt: lead's re-review of A's delta 3788c45..5ddb2a5 (M1/D-068, L1-L4, '999' flake fix, D-068 row, registry); Head SHA reviewed 5ddb2a5e05161ec008cde6fdfa4a170c7b203556; executed: generators --check (8/8), Release build, unit 1149/1149, API 91/91, PostgreSQL Commercial+Numbering 34/34 on the disposable 5433 cluster (created and destroyed); CI on 5ddb2a5 all green; verdict approve (M1, L1-L4 resolved); new Lows L5 (date-boundary coverage) and L6 (DECISIONS dates/attribution).

(The round 1 identifiers L1 to L5 and the round 2 identifiers L5 and L6 are the reviewer's own; the round 2 L5 is the date-boundary coverage below, not the round 1 informational note.)

| # | Sev | Finding (round 2) | Disposition |
|---|-----|---------|-------------|
| L5 | Low | Date-boundary coverage of the D-068 rule. | Fixed in the author's follow-up commits, confirmed in round 3: `CommercialHardeningTests` now covers start-1, the start date itself (inclusive), the end date itself (inclusive) and end+1, both at the service and by direct SQL. |
| L6 | Low | DECISIONS dates and attribution. | Fixed, confirmed in round 3: D-067 and D-068 are dated 2026-11-21 (D-064 to D-066 corrected the same way) and D-068 is labelled a LEAD decision under D-048, not an agent decision. |

## Round 3 (head d1f2d11): approve

Round 3: Reviewer Agent C (independent QA/security, separate session); model family Claude Opus (claude-opus-5-5); prompt: lead's round-3 confirmation of 5ddb2a5..d1f2d11 excluding review records (L5 date-boundary tests, L6 decision dates/attribution, A's record, the reordering of CommercialHardeningTests); Head SHA reviewed d1f2d115d5fce1fa3f85390848268b98194e28d6; executed: Release build, PostgreSQL Commercial+Numbering 34/34 on the disposable 5433 cluster (created and destroyed); CI on d1f2d11 all green; mergeable with main d3bee74; verdict approve; L5 and L6 resolved; no assertion dropped or weakened by the reordering; no new findings.

The reordering of `CommercialHardeningTests` (service edits first, committed direct-SQL statements last, so no later service call meets a revision bumped without an event) keeps every earlier assertion and only adds the boundary assertions.

* **Blocker or High findings open:** none
* **Medium findings open:** none
* **NOT VERIFIED:** none for the reviewed head. The registry total (1083 = 1072 + 11) is reconciled by whichever of #99, #104 and #107 merges later.
* **Verdict:** approve
