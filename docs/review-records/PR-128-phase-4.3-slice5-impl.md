# Independent review record

* **Pull request:** #128 (Phase 4.3 slice 5 implementation: encrypted, signed backups, key export and recovery test)
* **Head SHA reviewed (full 40 characters):** f30f6fa5329fb7207dc8bd68ca5121d613314969 (round 1)
* **Reviewer context:** Agent C (independent QA/security reviewer), a separate session from the author (Agent B); model family Claude Opus (claude-opus-5-5). The prompt is given below.
* **Scope read:** the full diff `origin/main...feature/phase-4-3-slice5-impl` at f30f6fa, against PHASE-4.3 slice 5 (D-089..D-095, D-116..D-120; owner D-085, D-087, D-088) and G's #116 record.
* **Checks run (commands and results):**
  * Release build;
  * unit tests 1206/1206; API tests 117/117;
  * PostgreSQL EncryptedBackup / BackupRun / CompanyBackupService / BackupRestoreDrill / CompanyRuntimeIsolation 39/39 (0 skipped), on the disposable 5433 cluster (sole holder, destroyed afterwards);
  * a local probe (never committed) proving H1, and a curve-validation probe;
  * hosted CI on f30f6fa all green, including the first EncryptedBackupTests run;
  * the PR is mergeable.
* **Findings:** round 1 requested changes: 0 Critical, 1 High, 0 Medium, 4 Low.

| Round | ID | Severity | Finding | Resolution |
|---|---|---|---|---|
| 1 | H1 | High | The recovery test restores an unencrypted, unsigned version-1 company set planted in an encrypted run without the acknowledgement or any signature, and reports PASSED (the branch was on `IsEncryptedSet` in `TrialRestoreCoreAsync`; only Hasm.Admin checked the acknowledgement) | Fixed in 0758816. `TrialRestoreAsync` takes an explicit `allowUnencrypted` from its caller and refuses a version-1 set otherwise, and always when a run id is expected. `RecoveryTestService` refuses every company set that is not encrypted (`unencrypted_set_refused`). The PostgreSQL forged-set test now plants exactly this substitution: the result is not passed, A is never restored, and no database is left behind. A new unit test checks the library refusal. Mutation check: removing the library refusal makes the unit test fail. The PostgreSQL mutation check was not run locally (see NOT VERIFIED). |
| 1 | L1 | Low | Company manifests are not bound to the run, and company folders are not checked against the signed status (an older, validly signed set could be substituted) | Fixed in 0758816: `run_mismatch` when a company manifest's run id differs from the signed status; `company_sets_mismatch` when the company folders differ from the status's written sets. Both are covered in the PostgreSQL forged-set test. |
| 1 | L2 | Low | No test of an off-curve ephemeral point or of nothing released when the first chunk fails authentication | Fixed in 0758816: `Hbe1Tests` covers an off-curve point (`hbe_malformed`, refused before key agreement) and a first chunk failing the AEAD (output empty). Under the genuine digest the altered chunk is `hbe_not_authenticated`, also with an empty output. |
| 1 | L3 | Low | Refused trial restores and recovery tests are not audited | Fixed in 0758816: 2010/2011 events with `outcome=refused code=<code>` (completed ones carry `outcome=completed`); asserted in the PostgreSQL tests. |
| 1 | L4 | Low | `backup-keys` does not parse `*.pub.pem` at write time; a misplaced `RunAsync` summary in PgBackupTool | Fixed in 0758816: each signing public key is parsed as one P-256 PUBLIC KEY whose derived id equals the file's id (`key_file_invalid`), with a unit test; the summary was moved. |

Round 1: Reviewer: Agent C (independent QA/security, separate session); model family Claude Opus (claude-opus-5-5); prompt: lead's priority full-diff review of origin/main...feature/phase-4-3-slice5-impl against PHASE-4.3 slice 5 (D-089..D-095, D-116..D-120; owner D-085, D-087, D-088) and G's #116 record (H1 chunk authentication and exact count; signature coverage and refusals; reader-only acknowledgement; canary preimage; public-only key export; passphrases; key status; DEV/TEST guard; audit 2010–2013; D-120; no plaintext at rest; nonce and chunk boundaries; curve validation); Head SHA reviewed f30f6fa5329fb7207dc8bd68ca5121d613314969; executed: Release build, unit 1206/1206, API 117/117, PostgreSQL EncryptedBackup/BackupRun/CompanyBackupService/BackupRestoreDrill/CompanyRuntimeIsolation 39/39 (0 skipped) on the disposable 5433 cluster (sole holder, destroyed), a local probe (never committed) proving H1, and a curve-validation probe; hosted CI on f30f6fa all green including the first EncryptedBackupTests run; mergeable; verdict request changes; H1 (High: the recovery test restores an unencrypted, unsigned version-1 company set planted in an encrypted run without the acknowledgement or any signature, and reports PASSED), L1–L4 (Low).

* **Blocker or High findings open:** H1 is fixed in 0758816 and awaits C's confirmation of the delta.
* **NOT VERIFIED:** the author's local PostgreSQL run of the fixes (the disposable cluster was held by another agent; the new PostgreSQL assertions run in hosted CI on the pushed head).
  * The PostgreSQL-level mutation check (the recovery test's own refusal removed) was therefore not run by the author.
  * The PostgreSQL registry is unchanged (the substitution was added to the existing forged-set test); it is reconciled at merge.
* **Verdict:** request changes
