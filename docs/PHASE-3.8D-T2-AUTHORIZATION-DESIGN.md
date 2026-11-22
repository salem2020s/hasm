# Phase 3.8D threat T2: authorization of replay operations (design proposal)

Status: **DESIGN PROPOSAL, documentation only. Nothing here is implemented.** Author: Agent ODC (design author for this document; the independent adversarial review is Agent opusSEC's). Base: `main` `22c9d23` (PR 216 merged: the executor implementation plan). Until this design is independently reviewed and approved and the owner decisions of section 12 are taken, **replay executor production activation and the runtime INSERT grant on the four execution tables stay disabled** (owner direction of 2026-11-22).

Sources: the owner's T2 direction of 2026-11-22 (quoted in section 1), `docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` revision 3 (sections 3, 5, 7: T2), `docs/PHASE-3.8D-EXECUTOR-PLAN.md` (sections 4, 5, 7), company migration 0036, the migration guard of PR 188 (`docs/MIGRATION-GUARD.md` on that branch), and a feasibility probe recorded in section 4.2.

---

## 1. The owner's requirement (verbatim) and what it rules out

> "possession of the ordinary application runtime database credentials alone must not be sufficient to create or execute an unauthorized backdated replay."

The design must (owner, verbatim bullets): "Preserves least-privilege database access." / "Prevents direct unauthorized replay writes by the ordinary runtime database role." / "Requires independently verifiable authorization for privileged replay operations." / "Preserves the external cryptographic audit evidence, segregation of duties and fiscal-period safeguards." / "Prevents unverified replay effects from being treated as valid financial history." / "Does not rely on an application-controlled flag or a SECURITY DEFINER function callable without an independent authorization check." / "Explicitly distinguishes stolen database credentials from complete application-process compromise." / "Includes adversarial PostgreSQL tests proving the relevant threat boundaries."

Ruled out by the owner: accepting T2 with after-the-fact detection (audit chain, period-close block) as the only control; a SECURITY DEFINER writer that the runtime role can call without an independent authorization check.

## 2. T2 today, precisely

* The per-company runtime role R holds **SELECT only** on the four execution tables (PR 48 review M1); nothing can execute.
* Migration 0036 accepts an execution, and with it the epoch rows and back-dated entries, from any writer that can INSERT into those tables, provided the rows are consistent: an approved plan whose approval names an approver different from the creator (ids only, `f_17b9c93f`), an executor different from the creator, an audit event 1154 row in the same transaction, closure and period rules. The database compares **ids**; it cannot authenticate the application user behind them.
* R holds INSERT on the plan, closure, line, event and approval tables (the storage and approval services write them) and on the business audit table. So, with the execution INSERT grant back, **R alone could write plan, approval, audit row and execution: T2.** The audit row's authentication code (HMAC with the audit key outside the database) would not verify, but PostgreSQL does not check it, and the owner does not accept detection alone.

## 3. Options evaluated

### (a) A separate database principal (executor login role)
A per-company role E with the execution INSERT grants that R never gets; R has no membership in E.
* **Where its credential lives:** the executor runs inside the API process, so E's password must be readable by that process. Not in the runtime configuration, but in a separate owner-only credential file or encrypted under the API's key ring. Any theft vector that reads the API host's secrets yields both; an attacker who has only R's password (leaked connection string, a backup of the runtime credential store, a database-side leak) does not get E.
* **Per-execution unlock:** none that is meaningful without a human secret: the process decrypts E's password whenever it executes. A human unlock (an operator typing a secret per execution) is an operational burden and still trusts the process that receives it.
* **What an attacker with only R's credentials gets:** nothing on the execution tables, but E is a general capability: whoever holds E's password can execute ANY plan with ANY consistent ids (no per-plan binding, no approver binding).
* **Cost:** a second login role per company (cluster role created by `grant-company-runtime`-style tooling with the role administrator), a second credential store, rotation, restore handling.
* **Verdict:** meets the letter of the requirement for the narrow "stolen R password" case, gives no independently verifiable authorization of THIS plan by THIS approver, and adds a second credential with the same storage problem. Not chosen as the primary control (it may be added later as defence in depth, section 11).

### (b) Cryptographically verifiable authorization checked IN the database
Each privileged replay write carries a signature that PostgreSQL verifies before accepting the row.
* **Can PostgreSQL verify it with what is available?** `pgcrypto` offers digests, HMAC and PGP encryption/decryption, **no signature verification**; and the migration guard (PR 188) refuses `CREATE EXTENSION` in migrations anyway. Core PostgreSQL 16 has `sha256()`, `get_byte`, `decode` and arbitrary-precision `numeric`. That is enough for **RSA signature verification in plain PL/pgSQL** (modular exponentiation with the public exponent 65537 and an exact comparison with the PKCS#1 v1.5 encoding). Proven by the probe of section 4.2.
* **HMAC variant (symmetric key):** the database would need the secret. If R can read it, R can forge; if only a verifier role can read it, the trigger must run with that role's rights, i.e. a SECURITY DEFINER function with a pinned `search_path`, which the migration guard refuses (`search_path` is refused in every form) and which puts a forging secret into every company database and every company backup. **Rejected.**
* **Asymmetric variant (public key in the database, private key outside it):** the database holds only the PUBLIC key; nobody who can read the database can forge. The verifying function is **invoker-rights** (no SECURITY DEFINER, no `search_path`), so the owner's objection does not arise: there is no definer function at all, and the check IS the independent authorization check.
* **Verdict: chosen** (section 4).

### (c) Combinations and other architectures
* **Out-of-process executor service** with its own credential: moves the execution code and a credential to another process; against stolen R credentials it is equivalent to (a); against a compromised API it helps only if that service independently authenticates the approver (it would need the MFA secrets or its own login). A large new component for Release 1; not chosen.
* **Two-person, two-channel authorization** (the approver approves in the application, an operator runs an offline `Hasm.Admin` command holding the signing key): strongest against a compromised application, operationally heavy for every execution. Kept as a deployment option of the chosen design (the signer can be the offline tool; section 4.1, Tier 2b).
* **Approver-held hardware keys (WebAuthn assertions over the authorization):** the only option that also prevents a fully compromised application from authorizing on its own (it still cannot stop an approver from signing what a compromised UI shows). Needs WebAuthn, which Release 1 does not have (PHASE-4.1: TOTP only). It is the **upgrade path (Tier 2a)** of the chosen design: the database check stays the same kind (signature verification against registered public keys), only the key holder changes.

## 4. Recommended design: signed replay authorizations verified by PostgreSQL

### 4.1 Keys and signer
* **Replay authorization signing key:** RSA-3072, generated by a new `Hasm.Admin replay-signing-keygen <key-id>` into `Hasm__Replay__SigningKeyDirectory` (owner-only files, never overwritten, exactly the pattern of `backup-signing-keygen`). It is **not** in the database, **not** in the runtime database configuration or the company credential store, and is backed up separately from database backups and from the audit and encryption keys.
* **Public key registration:** a new `Hasm.Admin register-replay-signing-key <key-id>` writes the public key (modulus, byte length, key id, fingerprint) into the new table `ReplayAuthorizationKey` of every company database **as the owner account** (the administrative connection), audited in the system stream. R holds SELECT only; the table is append-only except a one-time revocation stamp.
* **Pinning in the application:** the API configuration carries the fingerprints of the accepted keys. The signer refuses to sign with, and the verifying readers (section 7) refuse to accept, a key whose fingerprint is not pinned: a key row substituted by a database owner is not silently trusted by the application.
* **Signer (Tier 1, Release 1):** an internal component of the API process that signs only after its own checks of the request (section 4.2). **Tier 2a:** approver-held WebAuthn keys (future). **Tier 2b:** an offline signer (`Hasm.Admin authorize-replay`, run by a second person on another machine holding the private key). The database side is identical in all tiers.

### 4.2 Feasibility probe (run on the isolated 5433 cluster, PostgreSQL 16.15, through the CPU gate; local only, not committed)
An invoker-rights PL/pgSQL function `rsa_verify(n numeric, k integer, signature bytea, message bytea)` (convert the signature to `numeric`, refuse `s >= n`, compute `s^65537 mod n` by 16 squarings and one multiplication, build the expected encoding `00 01 FF..FF 00 || DigestInfo(SHA-256) || sha256(message)` and compare the two numbers exactly) against signatures made by .NET `RSA.SignData(..., SHA256, Pkcs1)` with a fresh 3072-bit key:

| Case | Result |
|---|---|
| valid signature | true |
| message changed in one bit | false |
| signature changed in one bit | false |
| signed by another key | false |
| PSS signature by the right key | false (only PKCS#1 v1.5 is accepted) |
| signature value >= modulus | false |
| signature one byte short | false |
| `prosecdef` of the function | false (invoker rights) |
| cost | **4.2 ms per verification** (50 VOLATILE calls on distinct messages: 212 ms) |

`pgcrypto` is available on the cluster but not installed in the databases and is not used. The exact comparison of the whole encoding (no parsing of the decrypted block) avoids the classic PKCS#1 v1.5 parsing forgeries; the key size is fixed at 3072 bits by the registration check.

### 4.3 What is signed: three authorizations, built by PostgreSQL from the rows
The message is **never taken from the client**: PostgreSQL builds the canonical text from the rows it already holds, and only the signature and the key id are supplied. The application builds the same text; a unit test pins byte equality (as for the closure hash). Domain-separated, one line, fields in this order:

* **Creation:** `hasm-replay-v1|creation|company=<company id from the company identity table>|plan=<id>|creator=<id>|document=<id>@<revision>|item=<id>|warehouse=<id>|payload=<sha256 of the payload>|fingerprints=<the seven, in order>|closure=<closure hash recomputed by the database>|lines=<sha256 of the ordered plan lines>`
* **Approval:** `hasm-replay-v1|approval|company=…|plan=<id>|revision=<plan revision approved>|approval=<id>|approver=<id>|creator=<id>|document=<id>@<revision>|fingerprints=…|closure=…|lines=…`
* **Execution:** `hasm-replay-v1|execution|company=…|plan=<id>|approval=<id>|approver=<id>|executor=<id>|execution=<id>|closure=…|not-after=<UTC timestamp, seconds>`

The signer signs only after: the permission of the user on the plan's root warehouse (create, approve or execute); a fresh MFA step-up of that user (the existing `MfaFresh` policy; privileged under D-096); segregation of duties (creation: the creator; approval: approver independent of the plan and of the event document, as `f_17b9c93f`; execution: executor different from the creator); the re-plan with every fingerprint and the closure hash equal (approval and execution); `not-after` at most 10 minutes ahead (execution).

### 4.4 Database enforcement (one new additive company migration, number to be allocated by the lead)
New tables (append-only, refused UPDATE/DELETE/TRUNCATE as in 0036):
* `ReplayAuthorizationKey` (key id, modulus, byte length 384, fingerprint, registered at, revoked at once).
* `ReplayPlanAuthorization` (plan id unique, key id, signature), `ReplayApprovalAuthorization` (approval id unique, key id, signature), `ReplayExecutionAuthorization` (execution id unique, key id, signature, executor id, not-after).

New functions (plain PL/pgSQL, invoker rights): `rsa_verify` (4.2); `replay_message(kind, id)` building the canonical text from the rows; `replay_authorized(kind, id)` = the key exists, is not revoked and has 3072 bits, and `rsa_verify` holds for `replay_message`.

New triggers (additive; no existing trigger, function or constraint is changed or replaced):
* `BEFORE INSERT` on each authorization table: refuses the row unless `replay_authorized` holds for it; on the execution authorization also unless `now() <= not-after <= now() + 10 minutes` and the executor and execution match the execution row.
* `DEFERRABLE INITIALLY DEFERRED` constraint triggers `AFTER INSERT` on the plan, approval and execution tables of 0036: at commit every plan, approval and execution row written in the transaction has its verified authorization row. A plan's transition to approved (`BEFORE UPDATE` new trigger on the plan table) additionally requires the verified approval authorization of the approval it names.
* Nothing else changes: the epoch rows and back-dated entries remain accepted only inside a live execution of an approved plan (0036); since an execution cannot commit without its verified authorization, neither can they.

Grants (catalog `CompanyRuntimePrivileges`): R gets SELECT on `ReplayAuthorizationKey` and SELECT, INSERT on the three authorization tables; the INSERT grant on the four execution tables can return **only** with this migration, the first real caller and the owner decisions of section 12 (executor plan section 7 is replaced by this design).

## 5. Threat boundaries

| Attacker has | Create a plan | Approve | Execute / write epoch rows | What is detected | Out of scope |
|---|---|---|---|---|---|
| **The runtime database credentials only** (R's password or a session as R) | **prevented**: no valid creation signature | **prevented** | **prevented**: no valid execution signature; the deferred checks roll everything back | refused attempts are visible in the database log | availability: R can still write and lock ordinary data, and cancel or reject open plans (state transitions are not signed; section 12 D3) |
| **R plus a stolen approver session** (cookie) without the approver's second factor | prevented (creator session would also be needed, plus step-up) | **prevented**: approval needs a fresh MFA step-up | prevented | the refused step-up | |
| **R plus a stolen approver session AND that approver's TOTP** | needs a second, different user's session and step-up (the creator), because approval and creation are by different people | possible only as that approver, for a plan a different creator made | needs an executor different from the creator | the authenticated audit events name both people | collusion of two privileged users |
| **The signing key file only** | no database access: nothing | | | | |
| **Full compromise of the API process** (it holds the signing key, the audit key, the key ring and R) | **not prevented in Tier 1** | not prevented | not prevented | the signatures and audit events name the key and users; key revocation stops further use | prevention requires Tier 2a/2b (keys outside the API process) |
| **Database owner or superuser** | can replace the public key, switch triggers off, write rows | | | readers refuse keys whose fingerprint is not pinned in the application (section 7); the migration runner refuses a switched-off trigger at its next run (PR 188) | out of scope (design T10; SRS: no DBA rights for users) |

## 6. What is preserved
* **Least privilege:** no new role and no definer function; R's INSERT on the execution tables is conditional on a signature R cannot produce; key registration needs the owner account.
* **External audit HMAC chain:** unchanged; 1154 to 1159 are still required where they are today. The authorization rows add independent, publicly verifiable evidence (the audit detail can carry the signature digest).
* **Segregation of duties:** unchanged in the database (`f_17b9c93f`, executor not the creator) and now also attested by the signed messages, which name creator, approver and executor.
* **Fiscal periods:** unchanged (0036 entry guard: Open period, read `FOR SHARE`; the stock-period guard of 0034).
* **The owner's Q1 safeguards (verbatim) and where this design keeps them:** "preserve all existing CHECKs, triggers and immutability; never relax or replace an existing safeguard" (only additions; N17 extended); "an entry-level guard refusing unauthorized back-dated movements" (0036 unchanged; authorized now also means signed); "the replay exception only for an operation bound, in the same DB transaction, to the single execution of an approved plan" (unchanged; the execution must also carry its signature); "every replayed operation and entry bound to the plan's approved item/warehouse closure" (unchanged; the closure hash is in every signed message); "segregation of duties and Open fiscal periods enforced" (above); "no unauthorized reversals of original, event, storno and rebook operations" (unchanged, 0036 X6); "transactional consistency and an auditable execution record" (deferred checks; signature plus audit event); "never a client-controlled session flag or a general back-dating bypass" (no flag; section 8); "historical accounting records and traceability preserved" (nothing edited; authorization rows are append-only).

## 7. Keeping unverified replay effects out of valid financial history
* **At write:** an execution, and therefore any epoch row, cannot commit without a verified signature (section 4.4).
* **At read:** the snapshot readers and the close probe call `replay_authorized('execution', id)` for every execution they meet and refuse an epoch whose authorization does not verify, or whose key fingerprint is not pinned in the application. This covers rows that bypassed the triggers: a database owner's writes, and a **restore**, because `pg_restore` loads data before it creates triggers.
* **After a restore** (trial restore and the restore drill, N18): a full verification pass over every authorization row is part of the restore check; a failure marks the restored copy unusable.
* **Period close (slice P):** a period cannot be closed while it contains an epoch whose authorization does not verify.

## 8. No application flag; migration-guard compliance
There is no setting, session variable or parameter that opens anything: the only way in is a signature over a message that PostgreSQL builds itself. The new migration uses `CREATE TABLE`, `CREATE FUNCTION` (PL/pgSQL, invoker rights), `CREATE TRIGGER`/`CREATE CONSTRAINT TRIGGER`, `REVOKE ... FROM PUBLIC`, `numeric`, `sha256`, `get_byte`, `decode`, `repeat`, `format`, `now()` and the allowed `pg_current_xact_id()`. It needs **no** `SECURITY DEFINER`, `search_path`, `set_config`, dynamic SQL, extension or other `pg_` name, and no backslash string literals (hex via `decode(..., 'hex')`). **No guard exception is needed.**

## 9. Migration impact and provisioning
* **One additive company migration** (tables, functions, triggers of section 4.4; number to be allocated by the lead; generated names; N17 extended for additions only). No change to 0036 or any earlier migration.
* **No new cluster role.** Two new administrative commands (`replay-signing-keygen`, `register-replay-signing-key`), audited; registration also runs for a newly provisioned company (provisioning calls it when a key is configured) and after a restore (like `grant-company-runtime`). A company database without a registered key cannot authorize anything: it fails closed.
* **Runtime catalog:** the grants of section 4.4.

## 10. Adversarial PostgreSQL tests (role-capable isolated cluster, the real per-company runtime role)
Each with the production catalog; the "INSERT on the execution tables" cases run once with the grant this design allows:
1. R inserts a plan (with closure and lines) without a creation authorization: refused at commit; with a forged signature: refused at insert.
2. R inserts an approval and moves the plan to approved without an approval authorization, with a forged one, and with a genuine approval authorization **of another plan**: refused.
3. R writes a complete, well-formed epoch (plan, approval, audit row 1154, execution, members, entries) with every authorization forged or missing: refused, nothing persists (the PR 48 epoch fixture as the attacker's script).
4. A genuine execution authorization replayed: for another plan; another execution id; another company database (the same key registered in both); another closure (a plan whose closure hash differs); after `not-after`; with `not-after` more than 10 minutes ahead; for a second execution of an executed plan: all refused.
5. A genuine signature with a revoked key; with an unregistered key; with a 2048-bit key; a key row inserted by R: refused (R holds SELECT only on the key table).
6. Approver equal to the creator, to the event document's creator, editor or submitter, and executor equal to the creator, each with a signature made for exactly those ids: refused by the database (`f_17b9c93f`, executor rule), so a signer bug cannot bypass segregation of duties.
7. The verifying functions are invoker-rights (`prosecdef` false), have no `proconfig`, and R cannot create a function or operator that shadows them (no CREATE on the schema; N25-style hijack attempts).
8. Readers and the close probe refuse an epoch inserted with triggers switched off by the owner (the restore case) whose signature is missing or invalid, and one whose key fingerprint is not pinned.
9. The canonical message: a unit test pins the application text against `replay_message` byte for byte for each kind, with edge cases (lowercase uuids, timestamp to seconds, empty lines digest).
10. Mutation proofs: removing the signature check, the expiry check, the company field, the closure field, or the deferred existence check each fails a named test.

## 11. Effects on the merged executor plan
* **New slice A (before E3):** the migration of section 4.4, the key tooling, the canonical message and signer, the adversarial tests of section 10. Inert: no key registered means nothing can be authorized; the runtime catalog keeps SELECT only on the execution tables.
* **Change to merged services (own slice, with the existing-path treatment of the executor plan):** `CompensationPlanService` writes the creation authorization, `CompensationPlanApprovalService` the approval authorization; both behind the MFA step-up.
* **E3:** the executor obtains the execution authorization (signer, after its re-plan) and writes it right after the execution row; the rollback matrix gains "signature refused" and "expired authorization".
* **P:** the close probe's verification of section 7.
* **E5 and the grant step (executor plan section 7) are replaced:** the INSERT grant on the four execution tables returns only with slice A merged, the first real caller, and the owner decisions below. (a) may be added later as defence in depth; it is not required by this design.

## 12. Owner decisions needed before activation
* **D1 (boundary):** "Do you accept enabling replay execution with Tier 1, where the database refuses every replay write that lacks a signature made with a key held outside the database (so stolen database credentials, alone or with a stolen session lacking the second factor, cannot create, approve or execute a replay), while a full compromise of the application process, which holds that key, could still sign one; or do you require Tier 2 before activation (each approval signed by the approver's own hardware-bound key, which needs WebAuthn, not in Release 1; or a second person signing each execution with an offline tool on another machine)?"
* **D2 (key custody):** who generates the replay signing key, where it is stored (a directory separate from the database credentials, the audit key and the encryption keys), how it is backed up, its rotation interval, and who may revoke it.
* **D3 (scope of signing):** whether plan creation is also signed (recommended: yes, so that R cannot bind an approved document to a forged plan, X7), and whether reject, cancel and stale transitions stay unsigned (recommended: yes, they only end a plan; the residual is availability).
* **D4 (validity):** the maximum lifetime of an execution authorization (recommended: 10 minutes).

## 13. Trade-offs and residual risks
* Tier 1 moves the forging capability from "anyone with the database password" to "anyone who controls the API process or its signing key file": a real narrowing, not elimination. Tier 2 closes it at the cost of WebAuthn or a second-person offline step.
* 4 ms per verification at three points of a plan's life is negligible; the verification code (about 25 lines of PL/pgSQL) is security-critical and must be reviewed and mutation-tested as such.
* Key loss blocks new authorizations but not reading (verification needs only the public key); key compromise is handled by revocation and a new key; already verified rows remain verifiable.
* Restores must re-run verification (section 7); a company without a registered key fails closed.
