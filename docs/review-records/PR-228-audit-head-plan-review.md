# Independent review record

* **Pull request:** #228 (audit head integrity: availability under a forged head row, `docs/AUDIT-HEAD-INTEGRITY-PLAN.md`, documentation only); full review of origin/main..481bc6a
* **Head SHA reviewed (full 40 characters):** 481bc6a211ba23ee7599d7594fdb389614fa15fd
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author; the finding this plan answers was raised in my review of PR 223). Model family Claude Opus. Prompt (summary): "review the plan, which recommends (a) a BEFORE INSERT trigger for Seq and PrevHash plus (b) an owner-only `audit-recover-head` with a quarantine index and an authenticated chain-break record (forged rows kept), and rejects (c) the role split and (d) an automatic skip. Judge against: closes the attack, keeps chain integrity, needs an owner decision; plus concurrency (two writers racing for head+1), whether the recovery can be abused or triggered into re-blessing a forged tail and whether quarantine or break rows can be forged, verifier semantics across a break, the S8d evidence rule across a break, PR 188 guard compliance, and the precision of D1-D3".
* **Scope read:** the whole plan (101 lines, the only file of the diff). For the evidence: `AuditTrail.cs` (`AppendAsync`, `CommitAsync`, `EnsureHeadIsSoundAndAnchoredAsync`), `AuditVerifier.cs`, `AuditCanonical.cs` (`GenesisHash`, `Serialize`), system migration 0005, `SystemRuntimePrivileges.cs` and `CompanyRuntimePrivileges.cs` (database-level privileges), and the S8d plan's section 4B.
* **Checks run (commands and results):**
  * **Probe (isolated cluster through the CPU gate; the real restricted system runtime role created by the real `grant-system-runtime` routine; a temporary test, removed afterwards):** as that role, on the SYSTEM database:
    * `has_database_privilege(…, 'TEMPORARY')` is **true** (CREATE is false);
    * `CREATE TEMP TABLE t_7e93ef23 (…)`, a table named exactly like the audit table, is **allowed**, and an unqualified `SELECT … FROM t_7e93ef23` then reads the temporary table;
    * `CREATE DOMAIN pg_temp.bytea AS text` is **allowed**.
    
    The company databases are different: `CompanyRuntimePrivileges` revokes `ALL ON DATABASE` from PUBLIC and the role and checks `database_create_or_temp`. The system database has neither the revocation nor the check. See Y1.
  * **Test 1, does it close the attack?** No, and the plan says so honestly.
    * (a) cannot stop a junk row at head+1 with a consistent link, because the attacker can read the head.
    * (b) makes the denial of service recoverable, audited and evidence-preserving, not impossible: the attacker can repeat it after each recovery, and each break is visible.
    * Without moving the MAC key into the database or splitting the transaction, that is the best available. The rejections of (c) are sound: c1 is (a) in another form, and the migration guard forbids `SECURITY DEFINER` anyway; c2 breaks the atomicity of an event with its change; c3 needs role membership, so the attacker can switch roles too. So is the rejection of (d) (silent, repeated, automatic breaks in the hot path).
  * **Test 2, does it keep chain integrity?** Yes, subject to Y1.
    * No forged record ever becomes authenticated or evidence.
    * A break is a MAC-sealed record naming its exact range. A range counts as quarantined only if a valid break names exactly that contiguous range, the record before it verifies, and every sequence in it has a quarantine row.
    * Forged rows stay byte for byte.
    * Checkpoints never cover a forged row: the writer throws on a damaged head before it anchors.
  * **Test 3, does it need an owner decision?** Yes, D1 is a genuine policy change: a stream may contain an authenticated break.
  * **Concurrency of (a), two writers racing for head+1.** The trigger reads the head (`max(Seq)` of the stream) in the inserting statement's snapshot.
    * Two concurrent inserts at head+1 both pass the trigger; the primary key admits one and the other fails (unique violation, after waiting on the first transaction).
    * An insert at head+2 while head+1 is uncommitted in another session sees the committed head and is refused.
    * In REPEATABLE READ an older snapshot still hits the primary key.
    * Several rows in one statement or one transaction chain correctly: a row-level BEFORE trigger sees the rows the same command processed earlier.
    * No gap or fork can result. The honest writer's per-stream advisory lock does not bind the attacker, but the primary key and the trigger do.
    * **Sound, apart from Y1.**
  * **Recovery abuse.**
    * The runtime role cannot run the recovery: it holds no audit key, and on `audit_quarantine` it has SELECT only. It cannot forge a break record (no key), so a forged break is just another invalid row, and it cannot insert quarantine rows.
    * The recovery quarantines only records that FAIL verification, and refuses when a valid record follows an invalid one. A spliced genuine record (MAC-valid, link broken) is therefore never blessed: either it fails verification and is quarantined, or its presence refuses the recovery for investigation.
    * The break is inserted at m+1 inside one transaction after re-checking the preconditions. A concurrent junk insert at m+1 makes the break insert fail on the primary key; nothing is half-done, and the operator re-runs.
    * **The recovery cannot be triggered into re-blessing a forged tail.**
  * **Verifier semantics across a break:** well defined.
    * The break links to the forged record m's stored hash. `VerifiedThroughSeq` continues across the break.
    * Quarantined records are a Note, never counted as authenticated and never evidence.
    * See Y3 for what the overall verdict should say after a break, and for how the S8d evidence rule must handle the quarantined range.
  * **The S8d evidence rule across a break:** the plan says S8d never uses a quarantined fact. But S8d's rule is "at or below `VerifiedThroughSeq`", and after a break the quarantined sequences are numerically below the prefix end. The exclusion must be explicit in S8d (recorded there as X2 at 4d3acb4).
  * **PR 188 guard:** compatible as specified (new function and trigger names, `CREATE TABLE`, `GRANT`/`REVOKE`; no `CREATE OR REPLACE`, `DROP`, `DISABLE TRIGGER`, `search_path`, `SECURITY DEFINER` or dynamic SQL), with two caveats:
    * the guard forbids pinning `search_path` and `pg_catalog.`-qualified names, which is exactly why Y1's schema qualification and TEMP revocation are needed;
    * the genesis hash contains a NUL byte (Y2).
  * Hosted CI on 481bc6a, read by REST: ci-guard, secret-scan, dependency-scan, docs-traceability and guard-selftest completed success; backend jobs are path-filtered.
* **Findings:** 0 Critical, 0 High, 1 Medium (Y1), 2 Low (Y2, Y3). Each is listed with its resolution.
  * **Medium Y1 (option (a) can be bypassed on the system database by TEMP shadowing).** The trigger function runs with invoker rights in the INSERTING session, which is the attacker's session. On the system database the runtime role holds TEMPORARY (probe).
    * A temporary table named like the audit table shadows an unqualified head lookup. The attacker inserts its own fake "head" into the temporary table and then inserts into `public.t_7e93ef23` at any sequence with any `PrevHash`: the trigger validates against the fake head. That reopens gaps, unlinked inserts and splices, so the plan's statement that (a) closes them "for good" would not hold.
    * `pg_temp` types (for example a `pg_temp.bytea` domain) shadow unqualified type names in the function, and the guard forbids writing `pg_catalog.bytea`.
    * This also undercuts the S8d X1 remedy that relies on (a).
    * Resolution: schema-qualify every relation in the trigger function (`public."t_…"`). Revoke `ALL ON DATABASE` (TEMPORARY and CREATE) on the system database from PUBLIC and the runtime role, granting CONNECT only, exactly as company databases do. Add `database_create_or_temp` to `SystemRuntimePrivileges`' violations, refused at start like the D-235 gate.
    * Tests: as the runtime role, a TEMP table or a `pg_temp` type cannot be created on the system database, and a shadow attempt does not get a gapped or unlinked row past the trigger.
    * The TEMP grant on the system database is itself a pre-existing least-privilege gap worth fixing on its own.
  * **Low Y2 (the genesis expression).** `AuditCanonical.GenesisHash` is `SHA256(UTF8("HASM-AUDIT-GENESIS-v1\0" + stream))`. PostgreSQL text cannot hold a NUL, and the guard forbids backslash literals, so "sha256(convert_to(...)) only" cannot express it. State the exact expression, `sha256(convert_to('HASM-AUDIT-GENESIS-v1','UTF8') || decode('00','hex') || convert_to(stream,'UTF8'))`, and pin it with a test against `AuditCanonical.GenesisHash` for the system, `company:` and `business:` streams.
  * **Low Y3 (what verification reports after a break).**
    * (i) Specify the overall verdict after a recovery: either a distinct value, or `Verified` with a mandatory break count and the ranges in every report and in `audit-verify`'s exit status. A plain "Verified" must not hide that a break exists.
    * (ii) Every consumer of `VerifiedThroughSeq` (S8d's `VerifyAndReadAsync`, and any later one) must exclude quarantined sequences explicitly. `VerifyAsync` should return the quarantined set together with the prefix.
* **Owner decisions D1-D3:** precise. D2 should say explicitly that the interim single-person recovery is a dated exception to D-080(6), which requires independent approval for sensitive operations, until D-099 and S4 exist. My owner-facing wording follows.
  * **D1:** "Today, one forged row inserted with the application's database password stops every audited action (sign-in, administration, backups) for good, because the tamper-proof audit trail can never be continued past a damaged entry. Do you allow an explicit, owner-only recovery that keeps the forged rows as evidence, marks them as untrusted, and continues the trail after an authenticated 'break' entry naming who did it, when and why? Without it the system stays down until the database is rebuilt."
  * **D2:** "Until named administrator logins and two-person approval exist, the recovery may be run by the server operator who holds the database owner account and the audit key, as a dated temporary exception to the two-person rule. Afterwards it needs a named administrator with a fresh second factor and a second person's approval. Do you approve?"
  * **D3:** "Should recovery always be a deliberate manual action (recommended), rather than the system skipping a damaged entry automatically, which would hide repeated attacks?"
* **Blocker or High findings open:** none (Y1 open as Medium; it must be fixed in the plan before approval)
* **NOT VERIFIED:** the following two items.
  * The trigger, the recovery and the verifier changes are not implemented; their behaviour is judged from the plan and the current code.
  * Whether a `pg_temp` type shadow would change a comparison in the final trigger body, or only make it fail (Y1 removes the question by revoking TEMPORARY).
* **Verdict:** request changes

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: review of the audit-head-integrity plan (PR 228) against the three tests, concurrency, recovery abuse, verifier and S8d semantics across a break, the PR 188 guard and D1-D3; Head SHA reviewed 481bc6a211ba23ee7599d7594fdb389614fa15fd; executed: full plan read, a probe proving the system runtime role can create a TEMP table named like the audit table and a `pg_temp` type on the system database, the writer, verifier and canonical form read in code, concurrency analysed against the primary key and trigger visibility; verdict request changes; findings Y1 (Medium: TEMP shadowing defeats the trigger on the system database), Y2 and Y3 (Low); owner-facing D1-D3 wording.
