# Independent review record

* **Pull request:** #223 (Phase 4.3 slice 8 S8d implementation plan: retention per destination, `replication_pending`, the tombstone exception, the D-230 protection check, destination retirement; documentation only); full review of origin/main..50bacf5
* **Head SHA reviewed (full 40 characters):** 50bacf5e31ab66059ffa067491e7c665928c811e
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt (summary): "adversarial review of the S8d plan against the owner's D-230 answers Q1-Q3 and safeguards (verbatim) and the lead defaults Q4 (35-day recovery-test window, configurable downwards), Q5 (existing notification means while the gate is closed; the T2 D2 external channel is an activation condition), Q6 (only Rule A counts; Rule B disabled behind a pinned constant). Focus: nothing deletable while 4.3-ACT is closed (every path, structural test T1); Rule A (local disk, bind mount, loopback, overlay, FUSE, an NFS export of this machine; robustness of the not-this-machine check; fail-closed on an unreadable mount table); key evidence (proof plus audit 2011 within 35 days: can a runtime-credential attacker forge them); the remaining-copies check; the retirement workflow; the reordering; tests T1-T20; any remaining owner question".
* **Scope read:** the whole plan (239 lines, the only file of the diff); the owner's D-230 S8d answers (verbatim); for the evidence, `SystemRuntimePrivileges` (the system runtime role's grant on `AuditEvent`), `Audit/AuditVerifier.cs`, `BackupAuditEvents` (2011), and every call site of file deletion and move APIs in `Hasm.Infrastructure/Backup` and `Hasm.Admin`.
* **Checks run (commands and results):**
  * **Nothing deletable while the gate is closed (item 1).**
    * S8d adds no new deletion routine and no new switch.
    * `backup-retention-apply --destination` is the existing apply, which refuses with 2017 `retention_deletion_inactive` before reading any input.
    * The D-230 check is a fourth layer, independent of D-137 in both directions.
    * T2 and T3 compare full file trees (every path and content hash) with real encrypted runs.
    * The design is sound. The structural test T1 is not, as specified (M3).
  * **T1 checked against today's code.** The named APIs (`Directory`/`File` `.Delete`/`.Move`) already appear in `BackupRun`, `BackupSet`, `CompanyBackupService`, `EncryptedSets`, `KeyExport`, `PgBackupTool`, `RecoveryTest`, `RetentionService`, `ReplicaCopier` and `Hasm.Admin/Program.cs`. T1's allow-list names only `RetentionExecutor.DeleteRunFolder`, its tombstone writer and two `ReplicaCopier` sites. Other destructive forms (`FileInfo`/`DirectoryInfo` `.Delete`, `.MoveTo`, `File.WriteAll*`, `FileMode.Create`/`Truncate`) also appear in those files and are not named by T1.
  * **Rule A (item 2), analysed against the mount-table design.** It is sound for its kernel cases:
    * the longest containing mount in `/proc/self/mountinfo`;
    * a closed type list fixed in code;
    * a local disk, partition, LVM volume, bind mount or overlay is NOT qualifying, because each has a local filesystem type at the longest prefix;
    * an unreadable mount table fails closed (T11).
    
    It is weak where the evidence is text that the mounter chose (FUSE) or a DNS name resolved later, and where "this machine" is a container or virtual machine (M2).
  * **Key evidence (item 3).**
    * The proof file cannot be forged without the offline recovery key (the preimage must hash to the signed canary). A proof copied from another copy of the same run remains valid evidence, because S8c's re-hash proves the copies are byte-identical. That is acceptable.
    * The RECENCY evidence, audit 2011, is read from the system audit stream. The system runtime role holds INSERT on `AuditEvent` (`SystemRuntimePrivileges`: `AuditEvent`, Append), and the plan reads events raw (as S8c's reader does; the chain is not verified there). So a holder of the runtime credentials can insert a 2011 that makes a stale test look recent. The same applies to the other audit facts a deletion decision relies on (M1).
    * An `AuditVerifier` with the key ring and checkpoints already exists and is available to `Hasm.Admin`.
    * The identity-integrity work (PR 220) does not cover audit rows; their integrity is the HMAC chain.
  * **Remaining copies (item 4):** sound.
    * The retention classification is recomputed on the union of the primary and the qualifying locations after the candidate is removed.
    * The newest verified recoverable backup and the last recoverable copy per company are never removed; the recovery chain (D-133) is required; holds and pins are honoured.
    * Deleting from the LAST qualifying location a run that retention keeps is refused.
    * Concurrent applies at two locations cannot race each other into removing both copies: apply holds the exclusive run lock (section 5.5 step 2), so applies are serialised.
    * The evaluator runs before any input, in `DeleteRunFolder` and per run.
  * **Retirement workflow (item 5):** sound.
    * One privileged operation of the S4 approval framework: a different named administrator, fresh MFA, bound to the payload hash, single use, with an expiry. No preset grants it.
    * It writes audit only and the workflow code contains no file operation.
    * Retiring the last qualifying location is refused at request and at execution.
    * The unaccounted-removal rule makes a configuration edit without the workflow fail closed.
    * The audit fields are complete (operator or requester, approver, destination, reason, outcome; the event time is the timestamp).
    * It refuses with `destination_retirement_unavailable` until D-099 and S4 are on main.
    * The id-reuse gap is L1.
  * **Reordering (item 6):** sound as argued.
    * The resume runs only after the gate, the D-230 prerequisite and the locks; it finishes only folders already renamed out of their run id, and selects nothing new.
    * T5 pins the new order.
    * At the primary, the resume keeps slice 6's file-marker rule. Because the new order now also resumes while the status is not ok, the primary should require the same authenticated 2015 as a destination (part of M1).
  * **Tests (item 7):** T1-T20 map to every owner safeguard, as listed in section 9 (Q1 T10, Q2 T11-T13, Q3 T19, holds T14, last recoverable and chain T13, periods T15, fail-closed T16, notification T17, evidence T18, the gate T1-T3 and T20). Additions are required by M1-M3 and L1.
  * Hosted CI on 50bacf5, read by REST: ci-guard, secret-scan, dependency-scan, docs-traceability and guard-selftest completed success; backend jobs are path-filtered.
* **Findings:** 0 Critical, 0 High, 3 Medium (M1, M2, M3), 1 Low (L1). Each is listed with its resolution.
  * **Medium M1 (audit facts that authorise a deletion are read unauthenticated; the runtime credentials can insert them).** The plan's rationale for preferring audit evidence ("the database, which the share's writers cannot forge") considers only writers of the share. The system runtime role holds INSERT on the system audit stream, and the plan reads raw rows (exact tokens, no MAC or chain check). With the runtime credentials an attacker can insert, among others:
    * a 2011 that makes a stale recovery test recent, defeating the owner's "required decryption keys available" safeguard (Q4's window);
    * a 2015/2016 pair that, with a tombstone copied from the run's own signed status, makes a run "not pending" at a destination, bypassing `replication_pending` at the primary;
    * a 2015 that authorises a resume at a destination;
    * 2040, 2042 and 2045 facts that feed the last word, the unaccounted set and retirement.
    
    None of these takes effect while the gate is closed. But S8d-2 builds the evaluator now, and every one of them must hold at activation.
    * Resolution: every audit event a deletion decision relies on must be AUTHENTICATED. Run `AuditVerifier` over the system stream (with its checkpoints) and use only events inside the verified range, or verify each relied-on event's MAC and chain link. Anything unverifiable fails closed (`audit_unverified`).
    * At the primary, require the authenticated 2015 for a resume as well, not only the slice 6 file marker.
    * Add tests: a forged 2011 (no valid MAC), a forged 2015, a forged 2015/2016 pair and a forged 2045, each refused or ignored and alerted.
  * **Medium M2 (Rule A trusts mounter-chosen text and DNS, and cannot see a container or VM host).**
    * (a) The closed list includes FUSE types (`fuse.sshfs`; the native GlusterFS client is FUSE too). For FUSE, the mount SOURCE and the type's subtype are free text chosen by whoever mounts (`-o fsname=…,subtype=…`, available to unprivileged users through `fusermount`). A local passthrough filesystem can therefore present as `fuse.sshfs` from `otherhost:/x`.
    * (b) The host is taken from the source string and resolved "now". DNS can be changed, and the source can name a host other than the one the kernel connected to. For the kernel network filesystems the authoritative evidence is the `addr=` super option in mountinfo (nfs, nfs4, cifs, smb3, ceph). It must be compared, normalised, against every local interface address: all of 127.0.0.0/8, `::1`, IPv4-mapped `::ffff:` forms, IPv6 zone ids, `0.0.0.0`. A missing `addr=` must fail closed.
    * (c) In a container or virtual machine, "this machine's own interface addresses" are those of the namespace or the guest. An export served by the container host (for example the bridge gateway) or by the hypervisor is the same physical machine and passes. That is the owner's "second disk inside the same physical machine" in another form.
    * On Windows, `\\localhost`, `\\127.x`, the machine's own name, `\\?\UNC\` forms and DFS referrals to itself need the same normalisation.
    * Resolution: remove FUSE types from the qualifying list; take the server address from `addr=` and normalise as above; specify the Windows normalisation; state the container/VM limit plainly and put it to the owner (question below) and on the 4.3-ACT checklist. Add T11 cases: a FUSE mount with a forged `fsname`/`subtype`; a source name resolving elsewhere while `addr=` is local; IPv6 loopback, mapped and zoned forms; the machine's own non-loopback address.
  * **Medium M3 (the structural test T1 cannot hold as specified, and is weak).**
    * (a) Its allow-list omits the existing deletion and move sites in eight other files (listed above), so it fails on day one or gets widened ad hoc during implementation.
    * (b) "On a path that can be a run folder or a tombstone" cannot be decided by a source scan.
    * (c) It names only four static APIs. `FileInfo`/`DirectoryInfo` `.Delete`, `.MoveTo`, `File.Replace`, truncating or overwriting writes (`File.WriteAll*`, `FileMode.Create`/`Truncate` on an existing run file), P/Invoke `unlink`/`rename`/`rmdir` and `Process.Start` are not covered.
    * (d) Its scope (`Backup` and `Hasm.Admin`) excludes the rest of `Hasm.Infrastructure` and `Hasm.Api`.
    * Resolution: make T1 a METHOD-QUALIFIED allow-list of every existing site, each with a one-line justification, over all of `Hasm.Infrastructure`, `Hasm.Admin` and `Hasm.Api`, covering the full API list. A new site fails the test until it is added with a reviewed justification. Keep the runtime full-tree tests (T2, T3, T18) as the actual proof.
  * **Low L1 (destination identity after retirement).** Retirement and the qualifying and pending sets are keyed by the destination id. If an id is reused after a retirement, the new destination is treated as retired. That is fail closed (exit 8 blocks deletion), but the id becomes unusable and the intent is unspecified. Resolution: bind a retirement to the destination as it was (id, resolved path, device identity) and treat a re-added id as a new destination, or refuse reuse with a clear message; add a test.
* **Remaining owner question (stated precisely):** "When HASM runs inside a container or a virtual machine, a network share served by the same physical computer (the container host or the hypervisor) looks to the software like another machine. Should activating automatic deletion additionally require a recorded confirmation, by you or a named administrator, that each destination it relies on is physically independent of the primary machine, or is the automatic network check sufficient?" This is separate from OFFMACHINE-ATTESTATION: that item asks whether a declaration may REPLACE Rule A; this one asks whether a declaration must ACCOMPANY it. The plan's lead defaults Q4, Q5 and Q6 are reasonable and correctly marked as defaults, with Q5's activation condition binding.
* **Blocker or High findings open:** none (M1, M2, M3 open; they must be fixed in the plan before approval)
* **NOT VERIFIED:** the following three items.
  * No implementation exists; the evaluator, Rule A and the tests are judged from the plan.
  * The exact kernel super options on every supported kernel and filesystem: `addr=` is standard for nfs, nfs4 and cifs; ceph is to be confirmed at implementation.
  * Windows remote-drive behaviour.
* **Verdict:** request changes

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: adversarial review of the S8d plan (PR 223) against the owner's D-230 answers and the lead defaults Q4-Q6; Head SHA reviewed 50bacf5e31ab66059ffa067491e7c665928c811e; executed: full plan read, T1 checked against every deletion and move call site in the backup and admin code, the audit-evidence chain checked against the system runtime role's grants and the existing verifier, Rule A analysed against mount-table, FUSE, DNS, IPv6 and container cases; verdict request changes; findings M1 (unauthenticated audit evidence), M2 (Rule A spoofable or blind to a container or VM host), M3 (T1 not implementable as specified), L1 (destination id reuse); one owner question.
