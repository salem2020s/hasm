# Independent review record

* **Pull request:** #143 (Phase 4.3 slice 7 plan: live company restore cutover, D-081; D-143..D-153 planned)
* **Head SHA reviewed (full 40 characters):** 63299f7b285a4a4ae09fe45e4999051b2e3163e9 (round 1)
* **Reviewer context:** Agent C (independent QA/security reviewer), a separate session from the author (Agent B); model family Claude Opus. The prompt is given below.
* **Scope read:** the full diff `origin/main...63299f7` (`docs/DECISIONS.md` +9, `docs/PHASE-4.3.md` +180/−1).
* **Checks run (commands and results):**
  * cross-checks against:
    * PHASE-4.1.md section 7 and S8;
    * DECISIONS D-080, D-081, D-106, D-107, D-118 and D-119;
    * AccessService and the branch, warehouse and cost-center epoch schema;
    * CheckpointStore, AuditTrail, AuditVerifier, CompanyBackupService, CompanyRuntimePrivileges and numbering;
    * #134's RestoreSafeguards and pin migration;
  * a grep of D-14x and 2020–2029 usage across all remote branches;
  * main's check-docs, scan-secrets, ci-guard (`--base origin/main`) and roadmap `generate.js --check`: all pass;
  * hosted CI on 63299f7: guard, secret, dependency and docs checks green; backend not triggered (docs only).
* **Findings:** round 1 requested changes: 0 Critical, 2 High, 4 Medium, 6 Low. Every finding is fixed in the plan in 00a0fb5.

| Round | ID | Severity | Finding | Resolution |
|---|---|---|---|---|
| 1 | H1 | High | Company-side activation-epoch rollback re-enables revoked branch, warehouse and cost-center grants; D-106 principle at company scope | Fixed in 00a0fb5 (D-152): before the switch every restored epoch is set above both the live and the restored value; post-check; planned PostgreSQL test (grant, deactivate, cut over to a pre-deactivation set, the grant stays ineffective; warehouse and cost-center variants); D-106's principle applied at company scope as a security-preserving default; owner question Q8 with that default. |
| 1 | H2 | High | Document numbers issued after the backup point are issued again; owner question needed | Fixed in 00a0fb5 (D-153): owner question Q7 "Numbers issued after the backup point"; PRESERVING DEFAULT: refuse the cutover whenever any series advanced after the restore point; owner option: advance each counter past the live maximum and record the skipped range as void; pre-check, post-check and planned test. |
| 1 | M1 | Medium | Approval consumption vs the 4.1 single-transaction and retry semantics | Fixed in 00a0fb5: the approval is claimed at execution start (`Switching`, unique per approval, under the framework row lock, with 2021/2022); a claimed cutover finishes or reverses regardless of expiry; a reversal leaves it consumed; the deviation from the PHASE-4.1 retry rule is recorded as part of the S8 contract; the contradiction is removed. |
| 1 | M2 | Medium | The lost-tail evidence lapses when the safety-backup pin expires | Fixed in 00a0fb5: a non-expiring pin kind (kind 3, `lost_tail_evidence`) until the owner decides; the retained database named as a second verification source. |
| 1 | M3 | Medium | Rollback must keep the outgoing database and take a safety backup | Fixed in 00a0fb5: a rollback runs the full procedure (safety backup with evidence pin, epoch advance, numbering check, journaled switch keeping the outgoing database and folder, grants, post-checks, a new epoch). |
| 1 | M4 | Medium | An epoch is not bound to its cutover and approval in the verifier | Fixed in 00a0fb5: every epoch from 1 on must be bound to a Completed or RolledBack `restore_cutover` row and the system 2026 event (cutover id, approval id, heads); otherwise `epoch_unbound` (Failed). |
| 1 | L1 | Low | Resume rules | Fixed in 00a0fb5: decided from the observed state; BACK up to the database renames, FORWARD after them; an existing epoch file for the cutover id is reused. |
| 1 | L2 | Low | Sessions and retained-database access | Fixed in 00a0fb5: `ALLOW_CONNECTIONS false` before termination; runtime grants revoked on the retained database. |
| 1 | L3 | Low | Sparse checkpoints | Fixed in 00a0fb5: refused when there is no parent checkpoint at or below seq_r (`restore_prefix_unprovable`), plus a direct hash comparison with live at seq_r. |
| 1 | L4 | Low | Q4 default and D-144's forward migration | Fixed in 00a0fb5: Q4 default REFUSE; the forward migration marked "(Q5 default)". |
| 1 | L5 | Low | Dependency and handoff records | Fixed in 00a0fb5: dependency on #134 recorded; INTEGRATION-STATUS (D-143..D-153 to B, next free D-154, a CUTOVER-ACT register row); DEVELOPMENT.md:556 corrected. |
| 1 | L6 | Low | System-side scope assignments to entities missing from the copy | Fixed in 00a0fb5: warning check `scope_target_missing`. |

Round 1: Reviewer: Agent C (independent QA/security, separate session); model family Claude Opus; prompt: lead's independent full-diff review of Draft PR #143 (docs only, Phase 4.3 slice 7 restore cutover plan, D-143..D-151 planned) against D-081 (acknowledgement, independent approval, audit, a new checkpoint epoch that preserves the previous chain), D-080(6), D-106, D-107, D-118, D-119 and the slice 4/5/6 code, focusing on owner-reserved policy (Q1–Q6), approval only through the 4.1 framework, the CutoverAvailable gate, the epoch and prefix rule, rollback, crash safety, single-company scope, the audit block 2020–2029 and decision ID uniqueness against D-142; Head SHA reviewed 63299f7b285a4a4ae09fe45e4999051b2e3163e9; executed: full diff origin/main...63299f7 (DECISIONS.md +9, PHASE-4.3.md +180/−1), cross-checks against PHASE-4.1.md section 7 and S8, DECISIONS D-080, D-081, D-106, D-107, D-118 and D-119, AccessService and the branch, warehouse and cost-center epoch schema, CheckpointStore, AuditTrail, AuditVerifier, CompanyBackupService, CompanyRuntimePrivileges, numbering and #134's RestoreSafeguards and pin migration; D-14x and 2020–2029 usage grep across all remote branches; main's check-docs, scan-secrets and ci-guard (`--base origin/main`) and roadmap `generate.js --check` (all pass); hosted CI on 63299f7 (guard, secret, dependency and docs checks green; backend not triggered, docs only); verdict request changes; findings H1 (company-side activation-epoch rollback re-enables revoked branch, warehouse and cost-center grants; D-106 principle at company scope), H2 (document numbers issued after the backup point are issued again; owner question needed), M1 (approval consumption vs the 4.1 single-transaction and retry semantics), M2 (the lost-tail evidence lapses when the safety-backup pin expires), M3 (rollback must keep the outgoing database and take a safety backup), M4 (an epoch is not bound to its cutover and approval in the verifier), L1–L6 (Low).

* **Blocker or High findings open:** none; H1 and H2 are fixed in the plan (00a0fb5) and await C's confirmation.
* **NOT VERIFIED:** C's confirmation of the fixes in 00a0fb5. The plan is implemented and tested only in the slice 7 implementation PRs (7a, and 7b with Phase 4.1 S8).
* **Verdict:** request changes
