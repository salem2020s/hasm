# Independent review record

* **Pull request:** #218 (Phase 3.8D threat T2, authorization of replay operations, design revision 2, documentation only); delta confirmation a5010a5..d62bc29 (revision 1 reviewed in `PR-218-t2-design-review.md`, record 182b1da)
* **Head SHA reviewed (full 40 characters):** d62bc29239f17989b60e8f797dfc805f5201f44a
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt (summary): "confirm the revision-2 delta adversarially: C1 with both resolutions, (i) the offline second-person signer and (ii) owner-registered signers with a fresh TOTP verified through the key ring plus two distinct signers. For each: is the threat table honest when system and company runtime credentials are stolen together and while session and identity minting remain unfixed? In (ii), can a system-runtime attacker forge the TOTP verification, the registry, the attempt log or the re-enrolment voiding? Is the out-of-band confirmation of (i) specified well enough to be enforced? Also M1-M4 and L1-L3 as mapped, with at least one known-answer vector rebuilt from the byte specification; D1-D6 precise and complete; probes only if a claim needs one".
* **Scope read:** the full delta of `docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md` (sections 0 to 15, 568 lines). The other file of the delta is my own record of revision 1. For the claims checked: migration 0031 (the TOTP guard `f_55ca2d2a`, the user MFA-state graph), 0036 (the payload column is `text`, the eight `char(64)` hash columns, the plan line types `numeric(28,6)`), and `SystemRuntimePrivileges` (the UserTotp columns the runtime role may update).
* **Checks run (commands and results):**
  * **Known-answer vectors, rebuilt independently from section 4.4 alone (Python, hashlib).** Every vector matches:
    * payload hash `e5f0ae53...b18f`;
    * lines hash `b5fd4e1d...da49`;
    * grant message 1260 bytes, SHA-256 `2bfb65d8...b195`;
    * approval message 1151 bytes, SHA-256 `1d768356...a60c`.
    
    M2 is resolved. The specified forms do not depend on session settings: uuid output, `encode(...,'hex')`, `to_char(d,'YYYY-MM-DD')`, `numeric(28,6)` output, bigint seconds, the `text` payload through `convert_to`. Every field value is constrained, so none can contain an LF.
  * **(ii), what a system-runtime attacker can and cannot forge** (code reading of 0031 and the grants):
    * The registry is owner-written; the runtime role has SELECT only.
    * The pinned secret's identity and material are owner-only. `f_55ca2d2a` refuses changes to identity and material, refuses lowering or clearing the last step, and allows state changes only pending -> active or revoked and active -> revoked. The runtime role has no DELETE.
    * A re-enrolment creates a new secret id, which voids the registration.
    * Attempt rows cannot be removed.
    * So the attacker cannot forge a verification, the registry or the voiding. It can only deny service: revoke the pinned secret, raise the last step, insert failure rows.
    * It CAN guess codes through the API (section 6, steps 6-7, see N1). The DB side of (ii) cannot check signer registration (the registry is in the system database); the installation key's signature stands in for it, which is correct only as long as the API process is intact. The table says this.
  * **Revision 1 findings as mapped:**
    * M1 (4.5): schema-qualified relations; the no-TEMP and no-CREATE invariant tested on provisioned and restored databases; a byte-identical message across changed session settings. Resolved.
    * M3 (4.6): the database-assigned `accepted_at` against `revoke_from`; the pinned list wins for reading. Resolved.
    * M4 (9): .NET verification against pinned keys, independent of database functions. Resolved.
    * L1 (4.2, 4.4): exact length 384, fixed e, constant DigestInfo, Wycheproof-style vectors. Resolved.
    * L2 (4.3): re-verification at commit over the rebuilt message, `clock_timestamp()` at insert and at commit. Resolved.
    * L3: partly. The signature binds content, but the replayed amounts are not bound in the database (N3).
  * **C1, threat tables (section 7) with system and company runtime credentials together:**
    * (i): the decision to sign belongs to a person outside the application, using a participants list held outside the database. Runtime credentials, minted sessions and a fully compromised API cannot produce a grant. Honest, subject to N2 and N3.
    * (ii): runtime credentials and minted sessions are not inputs. Honest about full API compromise (not prevented), but it omits two residuals: guessing (N1), and plans of forged provenance presented to real signers (N2 applies to (ii) as well).
  * No probe was needed: every claim was settled by the rebuild or by reading code. Hosted CI on d62bc29, read by REST: ci-guard, secret-scan, dependency-scan, docs-traceability and guard-selftest completed success; the backend jobs are path-filtered and not triggered.
* **Findings:** 0 Critical, 0 High, 3 Medium (N1, N2, N3), 2 Low (N4, N5). Revision 1's C1, M1-M4, L1 and L2 are resolved; L3 is partly resolved (N3). Each finding is listed below with its resolution.
  * **Medium N1, (ii): the TOTP attempt limit can be raced, and the guessing budget has no cap.** Section 6, steps 6-7, counts failures in the log, verifies the code, and then writes the attempt row. Concurrent requests all read fewer than 5 and each verifies, so the limit does not hold. Even when serialised, 5 per 15 minutes is 480 guesses a day per signer. With a validity window of ±1 step (3 of 10^6 codes), that is roughly 0.14 % a day, about 40 % a year per signer, and parallel across every registered signer. Owner-run guessing through minted sessions is therefore a slow but real path, and only detection limits it.
    * Resolution: serialise per signer (a transaction advisory lock); insert the failure row BEFORE verifying and turn it into a success only on a match; a cumulative cap (for example 10 failures) suspends the registration until the owner re-registers; alert on any failure; state that a success never resets the count and that attempt times come from the database clock.
    * Tests: a concurrent burst; the cumulative cap.
  * **Medium N2, (i) and (ii): the out-of-band confirmation cannot be enforced, and the display is not trustworthy.** Section 5, step 4 says "confirms the request with the approver... for example in person or by phone". Nothing records it, nothing defines what is confirmed, and the creator is not asked, yet creator and approver ids are both writable with the runtime credentials. The export takes item, warehouse and account names from rows the runtime role can rename. Nothing requires the tool to recompute the closure hash from the listed positions, so a displayed closure need not be the signed one.
    * Resolution:
      * the tool recomputes the closure hash and the lines hash from the ids it displays, and refuses on a mismatch;
      * it shows ids and codes next to names, marked as database data;
      * the confirmation covers the plan id, the document, the debit and credit totals and a short digest of the grant message, and is made with the approver AND the creator, who answer from their own knowledge, not from the application;
      * the tool requires an attestation (who was asked, how, when), which goes into the grant file, and the upload writes it to the audit;
      * (ii) states the forged-provenance residual and requires the same confirmation before the second signature.
  * **Medium N3, the replayed amounts are not bound by the database (the rest of L3).** Section 4.7 states this itself. With the runtime credentials plus a grant intercepted within its window (D4 recommends 24 hours for (i), and the grant file travels by hand), an epoch can be committed with amounts that differ from the plan.
    * The read-side check compares only with the signed LINES (per posting, period, branch and account). Cost moved between items or positions inside one posting and account keeps the lines equal, so it passes.
    * The check is also after the fact: later ordinary postings take their cost from the positions the epoch moved, so the effect spreads before any reader refuses it.
    * Resolution: make database-side binding a slice A requirement, not "if E3's shape allows". Either the planner computes a per-entry digest (position, sequence, amounts) that is part of the signed message and the commit trigger compares it with the epoch's entries, or E3 is shaped so that it can be. Until then, the read-side check compares entry by entry against a deterministic recomputation from the fingerprinted pre-state, not only against the lines. Shorten D4 for (i).
  * **Low N4 (owner decisions).** D1 is accurate, but it should name the two residuals of (i) (an intercepted grant within the window; dependence on the out-of-band procedure) and of (ii) (guessing, forged provenance). Missing:
    * an explicit activation condition for N3 (database-side amount binding);
    * the D-level ownership of the participants list (who maintains it, how changes reach every signer) within D2;
    * D4 should justify 24 hours against N3, or reduce it.
    
    D3, D5 and D6 are precise.
  * **Low N5 (threat table 7.2).** Add the residuals of N1 and N2 to the rows "runtime credentials" and "+ minted sessions"; at the moment they read "Prevented" without qualification.
* **Blocker or High findings open:** none (N1, N2, N3 open as Medium; they must be fixed in the design before approval)
* **NOT VERIFIED:** the following two items.
  * No implementation exists: the RSA and SQL claims rest on the revision 1 feasibility probe and my rebuild of it.
  * Whether E3's posting shape allows database-side amount binding.
* **Verdict:** request changes

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: delta confirmation of PR 218 revision 2 (C1 resolutions (i) and (ii), M1-M4, L1-L3, D1-D6); Head SHA reviewed d62bc29239f17989b60e8f797dfc805f5201f44a; executed: full delta read, all four known-answer vectors rebuilt from the byte specification (all match), code reading of 0031, 0036 and the runtime grants for the (ii) forgery questions; verdict request changes; findings N1 (TOTP attempt race and no cumulative cap), N2 (out-of-band confirmation not enforceable, untrusted display), N3 (amounts not bound in the database; read-side check incomplete and after the fact), N4 and N5 (Low).
