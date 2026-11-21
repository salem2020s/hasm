# Independent review record - PR #99, Phase 3.13 slice S1b (approval, numbering, server-produced snapshots)

* **Pull request:** #99
* **Head SHA reviewed (full 40 characters):** round 2: 5ddb2a5e05161ec008cde6fdfa4a170c7b203556 (round 1 reviewed 3788c459096388c73b54a3caa76f4e3f8566a414)
* **Reviewer context:** Agent C, a separate session from the author (Agent A), model family Claude Opus (claude-opus-5-5, a different family from the author's claude-sonnet-5-5), fresh context, read-only for code. Prompts are noted per round below.
* **Scope read:** round 1: the full diff of the S1b branch against `origin/main`; round 2: the delta 3788c45..5ddb2a5.
* **Checks run (commands and results):**
  * Round 1: Release build; generators 0028 and 0029 `--check`; unit 1136; API 69; PostgreSQL 135/135 through the harness on the disposable cluster.
  * Round 2: generators `--check` (8/8); Release build; unit 1149/1149; API 91/91; PostgreSQL Commercial and Numbering classes 34/34 on the disposable 5433 cluster (created and destroyed); hosted CI on 5ddb2a5 all green.
* **Findings:**

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
| L5 | Low | Date-boundary coverage of the D-068 rule. | Fixed in the author's follow-up commit (the commit that adds this record), confirmation pending: `CommercialHardeningTests` now covers start-1, the start date itself (inclusive), the end date itself (inclusive) and end+1, both at the service and by direct SQL. |
| L6 | Low | DECISIONS dates and attribution. | Fixed in the same commit, confirmation pending: D-067 and D-068 are dated 2026-11-21 (D-064 to D-066 corrected the same way) and D-068 is labelled a LEAD decision under D-048, not an agent decision. |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** the L5 and L6 follow-up delta has not been reviewed by C yet (confirmation pending).
* **Verdict:** approve
