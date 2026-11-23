# Independent review record

* **Pull request:** #231 (Phase 3.8D T2 authorization design, revision 5 amendment: the `P` group, `hasm-replay-v5`, slice A cut into pull requests; documentation only); full review of 9b450f2..2e91d8c
* **Head SHA reviewed (full 40 characters):** 2e91d8cb0bf55517ffbd063ff4fab2e88a2c2ec1
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author; reviewer of every earlier revision of this design). Model family Claude Opus. Prompt (summary): "review the T2 v5 amendment: the `P` group added to the epoch text, the version `hasm-replay-v5` with v4 refused, the vectors recomputed (epoch 1888 bytes, 4329df10…) by a script that first reproduced v4; tests 4 and 11 extended; the new section 16 cutting slice A into A1-A5. Rebuild at least the v5 epoch vector".
* **Scope read:** the whole delta of `docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md` (status line, section 0.00, 4.4 version, epoch text group 6, the vectors and the v4 history, test 4 and the mutation list, section 15, the new section 16).
* **Checks run (commands and results):**
  * **Vectors, rebuilt independently from section 4.4 (Python, hashlib).**
    * First, every version 4 value reproduces exactly: epoch 1777 bytes `719a4407…`, request `7af26e059fb7e332`, attestation `66ed1fd6…`, grant `ba4b8e2a…`, approval `7786134c…`.
    * Then, with the version line `hasm-replay-v5` and the `P` line `P|I|W|40|43|120.0000000000|1492.500000|2` appended, every version 5 value reproduces exactly:
      * epoch text **1888 bytes**, `4329df10c3039a9f9e66941607a58ebd1474826d1c4ac2cc7a82a11284b7aa42`;
      * request digest `b0953249b1f326dd`;
      * attestation 236 bytes, `6e403bf4…`;
      * grant 1408 bytes, `ef7615cb…`;
      * approval 1151 bytes, `3352bf3b…`.
    * The lengths of grant, approval and attestation are unchanged, as the plan says, because the version label and the digest keep their lengths.
  * **The `P` group (semantics).** One line per position with at least one epoch entry, in uuid order. It carries:
    * the baseline last sequence: the first epoch entry's sequence minus one, which is exact under 0036's next-sequence rule;
    * the final last sequence, which must equal the position row's at commit;
    * the final quantity and value, which must equal the position row and the last entry's running figures;
    * the open-layer count, which must equal the layers left open by the `R` lines and the unchanged earlier layers.
    
    The vector is internally consistent: entries 41-43 give baseline 40 and final 43; the last entry's running figures are 120 and 1492.50; the two `R` lines leave layers 3 and 9 open. The pre-state is bound by the position fingerprint. This is what my S1 Low on revision 4 asked for (the position row signed). It also binds "a position's quantity or value changed after the last entry in the same transaction" (test 4).
  * **Version refusal:** the slice A verifier accepts `hasm-replay-v5` only, and test 4 adds a v4 message, which is refused. The v4 vectors are kept as history and marked invalid for new grants.
  * **Tests:** test 4 gains the `P` deviations (each field, a missing or extra `P` line, a change after the last entry); the mutation list of test 11 gains the `P` fields; section 15 lists the `P` totals as security-critical.
  * **Section 16 (the slice A cut):**
    * Every PR is inert: no grant issued, no caller, the execution INSERT grant does not return, activation closed.
    * Each is additive, guard-compliant and schema-qualified (with the Y1 reference).
    * The order A1, A2, A3, A4 is sequential, with A5 after A2; A3 needs E1b; the executor slices need A2-A4 before activation.
    * The test sets per PR match sections 4 and 12 (A1: the RSA vectors and the TEMP/CREATE invariant; A2: the v5 message vectors under changed session settings; A3: the 1888-byte epoch vector against the SQL builder and E1b's `EpochText`; A4: test 4b).
    * Company migration 0037 allocated to A1 is the lead's allocation.
    * The cut is coherent.
  * Hosted CI on 2e91d8c, read by REST: ci-guard, secret-scan, dependency-scan, docs-traceability and guard-selftest completed success; backend jobs are path-filtered.
* **Findings:** 0 Critical, 0 High, 0 Medium, 0 Low.
  * Information, not a finding of this design: the `P` group binds the positions that have epoch entries. 0014's position guard does not require an entry for a sequence bump, so the runtime role can rewrite a position row's quantity and value in any transaction, together with a sequence +1, for a position with no epoch entry. That is a capability outside T2, as recorded at revision 4 (S1). It is worth a general costing-integrity item: a commit check tying a position's sequence bump to an entry.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** no implementation exists; the `P` group's SQL builder and its equality with E1b's `EpochText` are A3's test obligations.
* **Verdict:** approve

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: review of the T2 v5 amendment (PR 231): the `P` group, `hasm-replay-v5`, the vectors, tests 4 and 11, section 16; Head SHA reviewed 2e91d8cb0bf55517ffbd063ff4fab2e88a2c2ec1; executed: every v4 vector reproduced and then every v5 vector rebuilt from the specification (all match, epoch 1888 bytes), the `P` semantics checked against the vector and 0014 and 0036, section 16 read, hosted CI read by REST; verdict approve; no findings (one information note).
