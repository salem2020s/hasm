# Independent review record

* **Pull request:** #116 (Phase 4.3 slice 5 plan: backup set encryption, signed manifests, key export, recovery test, DEV/TEST-only unencrypted sets)
* **Head SHA reviewed (full 40 characters):** 609503cd8afefde744cba66c8f8ba79b69717070 (round 2; round 1 reviewed bd93581274b008715fb5e1d73f9784de53978aad)
* **Reviewer context:** Agent G, independent security/audit reviewer, a separate agent from the author (Agent B); model family Claude Opus; read-only. Prompts are given per round below.
* **Scope read:** round 1: `git diff origin/main...feature/phase-4-3-slice5-plan` at bd93581; round 2: the delta 3ab6e39..609503c with the merge content of main separated from the author's commits.
* **Checks run (commands and results):** documentation and generator-template only; no build or tests apply. Round 2 also checked that `tools/roadmap/generate.js` gains exactly the three lines of the D-118 dated limitation and that the regenerated RELEASE1-ACCEPTANCE.md matches, and that tools/roadmap is not on the CLAUDE.md never-self-merge list.
* **Findings:** round 1 request changes (1 High, 7 Medium, 5 Low); the intermediate delta at 3ab6e39 request changes (2 Medium, 4 Low); round 2 approve (1 Low). Every finding and its fix:

| Round | ID | Severity | Finding | Resolution |
|---|---|---|---|---|
| 1 | H1 | High | Unauthenticated decrypted data streamed into pg_restore before manifest hash verification | Fixed in cc78e8e (D-116): signed manifest carries per-chunk SHA-256 and the header hash; signature first; each chunk checked before release; exact chunk count |
| 1 | M1 | Medium | Signature coverage and downgrade | Fixed in cc78e8e |
| 1 | M2 | Medium | Recovery-test proof forgeable with the server signing key | Fixed in cc78e8e (canary, D-117), completed in 68c29d1 (N1) |
| 1 | M3 | Medium | Passphrase strength and environment variable | Fixed in cc78e8e |
| 1 | M4 | Medium | backup-keys scope and overlap checks | Fixed in cc78e8e, corrected in 68c29d1 (N2) |
| 1 | M5 | Medium | Compromised signing keys | Fixed in cc78e8e |
| 1 | M6 | Medium | D-091 TOTP inclusion risks | Fixed in cc78e8e (D-119, referring to D-106/D-107 after 68c29d1) |
| 1 | M7 | Medium | Restore classification under D-080(6) and audit | Fixed in cc78e8e (lead decision D-118, dated limitation via the roadmap generator in 609503c) |
| 1 | L1-L5 | Low | Counter overflow and curve validation; DEGRADED status; runbook; PBES2 checksum; interim admin-tool limitation | Fixed in cc78e8e / 609503c |
| 2a | N1 | Medium | Canary preimage not checkable by backup-status | Fixed in 68c29d1 (D-117) |
| 2a | N2 | Medium | Signing private keys in the backup-keys export | Fixed in 68c29d1 (D-094: public keys and status list only) |
| 2a | L-a..L-d | Low | Single-buffer chunk hashing; signed bytes of encrypted manifests; D-119 refers to D-106; retired-key date | Fixed in 68c29d1; D-120 citation in 6d294dc |
| 2 | L | Low | Stale coordination bullet naming only the slice 5 PR as the one that switches user_totp | Fixed in 0bae2ed with the reviewer's suggested wording |

Round 1: Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "crypto-rigour review of #116 slice 5 plan: nonce/AAD/chunking, KDF and passphrase protection, signature coverage and downgrade, unencrypted guard, rotation/recovery, D-080(6)/4.1 interaction, D-091 TOTP inclusion"; Head SHA reviewed bd93581274b008715fb5e1d73f9784de53978aad; executed: git diff origin/main...head (PHASE-4.3.md, DECISIONS.md D-089..D-095), cross-read D-085..D-088, #104 BackupRun/SystemBackupSet/CheckRoot, audit and encryption key-directory code on main, #113 D-091 text; no tests (docs only); verdict REQUEST CHANGES; findings 1 High (H1 unauthenticated decrypted data streamed into pg_restore before manifest hash verification), 7 Medium, 5 Low.

Intermediate delta: Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "confirm #116 delta bd93581..3ab6e39 (H1, M1-M7, L1-L4; D-116..D-119; separate merge content from the author's commit; scrutinise the per-chunk hash list and the M2 canary)"; Head SHA reviewed 3ab6e39dca6ea552d8ab58b9c961360e04cbd8ca; executed: git fetch/ls-remote, git log (first-parent and full), `git diff 3ab6e39^2 3ab6e39 --stat` (merge adds only the plan's 3 files, 225 insertions), `git diff bd93581 cc78e8e` (DECISIONS.md, PHASE-4.3.md, RELEASE1-ACCEPTANCE.md) read in full; no tests (docs only; WIP code d44faf6 not reviewed); verdict REQUEST CHANGES; findings 0 Critical/High, 2 Medium (N1 canary preimage not checkable by backup-status, forgeable with the signing key; N2 signing private keys in the backup-keys export), 4 Low; all round-1 findings resolved.

Round 2: Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "confirm #116 round-2 delta 3ab6e39..609503c (N1 canary preimage, N2 public signing keys only, L-a..L-d, D-120 citation, generator CI fix; separate merge content)"; Head SHA reviewed 609503cd8afefde744cba66c8f8ba79b69717070; executed: git fetch/ls-remote, first-parent log and per-commit parents, merge check `git diff 002ffa3 a272a62 --stat` (only the plan's 3 files, +230), `git diff a272a62 609503c` (DECISIONS.md D-092/D-094/D-116/D-117/D-119, PHASE-4.3.md, tools/roadmap/generate.js +3 lines only), `git diff 3ab6e39 884ba00`, CLAUDE.md protected list and .github/CODEOWNERS checked; no tests (docs and generator template only); verdict APPROVE; findings 0 Critical/High/Medium, 1 Low (stale coordination bullet still names the slice 5 PR as the one that switches).

* **Blocker or High findings open:** none
* **NOT VERIFIED:** the one-line fix of the round-2 Low in 0bae2ed (applied by the lead with the reviewer's suggested wording) awaits the reviewer's confirmation; the plan is implemented and tested only in the slice 5 implementation PR.
* **Verdict:** approve
