# Independent review record

* **Pull request:** #114 (Plan for Decision R, D-082: retention and purge of removed attachments)
* **Head SHA reviewed (full 40 characters):** be4d2559e1376a169d5054f6f14af3e7a145ea49 (round 2, the delta confirmation; round 1 reviewed 5c1e47c6f2d8085fe417d683cb864b1f3d856a82)
* **Reviewer context:** two rounds, both by independent agents (not the author), both Claude Opus; the lines below are as supplied by the lead.
  * **Round 1:** Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent security/audit review of PR #114 Decision R plan against D-082, backup interaction D-085..D-088, audit, key item K"; Head SHA reviewed 5c1e47c6f2d8085fe417d683cb864b1f3d856a82; executed: git ls-remote/fetch, `git diff origin/main...origin/docs/attachment-retention-plan` and `git diff a7f7ebc..5c1e47c`, read PHASE-R-attachment-retention.md in full, D-080..D-088, PHASE-3.12.md item K / rewrap retirability, PHASE-4.3.md (R dependency), CompanyBackupService lock, AttachmentEndpoints rewrap report; no build or tests (docs-only); verdict REQUEST CHANGES; findings 2 High (H1 R2 depends on owner-pending non-conservative D-109; H2 key-retirability window between R2 and R4), 4 Medium, 4 Low.
  * **Round 2:** Reviewer: independent reviewer subagent (independent, separate agent); model family Claude Opus; prompt: lead's independent delta confirmation of PR #114 (plan R, D-082) 5c1e47c..be4d255 against Agent G's findings H1, H2, M1-M4, L1-L4, and consistency with D-082, D-080 and D-085..D-088; Head SHA reviewed be4d2559e1376a169d5054f6f14af3e7a145ea49; executed: git fetch, git log and git diff 5c1e47c..468d858..be4d255, git diff --stat origin/main...origin/docs/attachment-retention-plan, full read of docs/PHASE-R-attachment-retention.md at the head; verdict request changes; findings M1 (section 14 R2 "key report extension (a) live rows only" contradicts the H2 fix), L1 (section 15 Q1 and the Allocations line still call the removal date the default), L2 (wording of file-missing-before-purge versus a resumed pending item), L3 (meaning of "potentially affected" before R4); all of G's H1, H2, M1-M4 and L1-L4 fixed in the normative sections; no Critical or High.
* **Scope read:** docs-only; the single file `docs/PHASE-R-attachment-retention.md` (the diff against `origin/main` is that one file).
* **Checks run (commands and results):** none beyond reading (docs-only: no build or tests); hosted always-on verification jobs green on the pushed heads.
* **Findings:**

| Round | ID | Severity | Finding | Resolution |
|-------|----|----------|---------|------------|
| 1 | H1 | High | R2 depends on the owner-pending retention basis (D-109), which is not conservative by itself. | Fixed in 468d858: while D-109 is owner-pending no item is eligible (`retention_basis_pending`) and R2 purge execution stays disabled. |
| 1 | H2 | High | Key-retirability window between R2 and R4. | Fixed in 468d858: until R4 a purged row still counts as needing its key, or the report answers false with `backup_inventory_unavailable`; R2 tests assert it; import-file originals keep the existing rule. |
| 1 | M1 | Medium | A file missing before the purge. | Fixed in 468d858: `skipped` with `file_missing_before_purge`, audit 2008 integrity warning, never recorded as deleted. |
| 1 | M2 | Medium | Releaser of a hold and plan approval. | Fixed in 468d858: the releaser of a hold may never approve a plan containing items that hold covered; release by the placer refused. |
| 1 | M3 | Medium | The Retention Approver preset is a sensitive grant. | Fixed in 468d858: restricted approver path, MFA mandatory, fresh MFA for approve, release and execute once 4.1 S3 exists. |
| 1 | M4 | Medium | A hold on an already-purged attachment versus D-086. | Fixed in 468d858: marks the retained backup sets containing it as under legal hold; recorded in D-114. |
| 1 | L1 | Low | Removals before any policy. | Fixed in 468d858: Indefinite. |
| 1 | L2 | Low | The same plan running twice. | Fixed in 468d858: plan row lock plus status compare-and-set. |
| 1 | L3 | Low | D-109 and D-113 must be owner-pending. | Fixed in 468d858: recorded as owner-pending, not decided. |
| 1 | L4 | Low | Audit 2008 detail. | Fixed in 468d858: carries the policy version and applied rule. |
| 2 | M1 | Medium | Section 14 R2 bullet "key report extension (a) live rows only" contradicts the H2 fix. | Fixed in 3f094ff: a purged row still counts as needing its key (or false with `backup_inventory_unavailable`) until R4 delivers condition (b); R2 tests assert purging never makes a key retirable. |
| 2 | L1 | Low | Section 15 Q1 and the Allocations line still call the removal date the default. | Fixed in 3f094ff: "proposed basis"; until answered no item is eligible and purge execution is disabled. |
| 2 | L2 | Low | File-missing-before-purge versus a resumed pending item. | Fixed in 3f094ff: missing before the purge means no pending purge item was committed; a resumed pending item whose file is gone is completed as deleted. |
| 2 | L3 | Low | Meaning of "potentially affected" before R4. | Fixed in 3f094ff: until R4 every retained backup set is treated as under legal hold for D-086 purposes and the warning says the inventory is unavailable. |

* **Blocker or High findings open:** none (round 1's two High findings are fixed; the round 2 fixes in 3f094ff await the same reviewer's confirmation).
* **NOT VERIFIED:** nothing was built or executed (a plan only); the round 2 fixes in 3f094ff are not part of the reviewed head be4d255.
* **Verdict:** request changes
