# Phase 3.8D threat T2: authorization of replay operations (design proposal)

Status: **DESIGN PROPOSAL, revision 2, documentation only. Nothing here is implemented.** Author: Agent ODC (design author). The independent adversarial review is Agent opusSEC's. Base: `main` `22c9d23`.

Revision 1 (`a5010a5`) was reviewed by opusSEC in `review-records/PR-218-t2-design-review.md`: 1 Critical, 4 Medium, 3 Low. Section 0 maps every finding to its resolution.

Until this design is independently approved and the owner decisions of section 14 are taken, **replay executor production activation and the runtime INSERT grant on the four execution tables stay disabled** (owner direction of 2026-11-22).

Sources:
* the owner's T2 direction of 2026-11-22 (section 1);
* `docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` revision 3 and `docs/PHASE-3.8D-EXECUTOR-PLAN.md`;
* company migration 0036;
* system migrations 0031 (TOTP store) and 0035 (owner-only TOTP material, D-235) and `SystemRuntimePrivileges`;
* the migration guard of PR 188;
* the feasibility probe of section 4.2.

---

## 0. Revision 2: findings and resolutions

| Finding | Resolution in this revision |
|---|---|
| **C1 (Critical).** The revision 1 in-process signer took its authority from system-database state that the runtime credentials can write: who the user is, permissions, a fresh second factor, two different people. A minted MFA-fresh session was accepted by the real API. Option (a) fails the same way. | Every signature's authority now comes from state the runtime credentials cannot create, in one of two ways. **(i)** An offline second person holds a private key on another machine (recommended, section 5). **(ii)** Owner-registered signers, whose pinned TOTP credential is verified for each signature (section 6). Both are specified in full, each with its own threat table (section 7). Every boundary is attacked with the SYSTEM runtime account as well as the company role (section 12). The general session-minting issue is referenced here, not solved (section 15). |
| **M1.** Name resolution of invoker-rights code. | Section 4.5. Relations are schema-qualified. The no-TEMP/no-CREATE invariant is stated and tested, including on provisioned and restored databases. The message does not depend on session settings, and this is tested. |
| **M2.** The message is underspecified. | Section 4.4. Byte-exact specification that can be rebuilt from this document alone, with known-answer vectors. |
| **M3.** Revocation contradicts itself. | Section 4.6. Each signature gets an acceptance time assigned by the database, and each key a revoke-from time. History accepted before revoke-from stays valid and new signatures are refused. The window of a suspected compromise is an owner decision. |
| **M4.** Read-side verification goes through a replaceable database function. | Section 9. Readers, the close probe and the restore check verify in .NET against keys pinned in the application. The threat tables are corrected. |
| **L1.** Signature length and constants. | Sections 4.2 and 4.4. Exact length 384, e = 65537, a constant DigestInfo, built-in `sha256`, `mod` only. Known-answer and edge vectors are required. |
| **L2.** Verified at insert only, against transaction time. | Section 4.3. Verified at insert AND re-verified at commit over the message rebuilt from the rows. The clock is `clock_timestamp()`, checked at both points. |
| **L3.** The execution signature does not bind content. | The grant binds the payload, the seven fingerprints, the closure, the document revision and the lines digest (section 4.4). What 0036 does not bind (the posted amounts against the lines) is closed on the read side, and the remaining bearer window is justified (section 4.7). |
| D1's false premise; missing decisions | Section 14 restates every decision: baseline, signer registry and authentication, key custody, validity, revocation, unsigned steps. |

---

## 1. The owner's requirement (verbatim)

> "possession of the ordinary application runtime database credentials alone must not be sufficient to create or execute an unauthorized backdated replay."

The owner's bullets, verbatim:
* "Preserves least-privilege database access."
* "Prevents direct unauthorized replay writes by the ordinary runtime database role."
* "Requires independently verifiable authorization for privileged replay operations."
* "Preserves the external cryptographic audit evidence, segregation of duties and fiscal-period safeguards."
* "Prevents unverified replay effects from being treated as valid financial history."
* "Does not rely on an application-controlled flag or a SECURITY DEFINER function callable without an independent authorization check."
* "Explicitly distinguishes stolen database credentials from complete application-process compromise."
* "Includes adversarial PostgreSQL tests proving the relevant threat boundaries."

Detection alone is not accepted.

A plan, a closure, lines and an approval change no financial history. A backdated replay, meaning its storno, event and rebook entries and the epoch rows, comes into existence only through an execution. Execution is therefore what must be impossible without an authorization the credentials cannot produce. Whether plan creation must also be signed is decision D6.

## 2. T2 precisely, and where authority may come from

**What the runtime credentials are in this deployment.** The API process holds two sets, under the same operating-system account on the same host:
* the SYSTEM runtime account, from its configuration (`Hasm__Database__Username/Password`);
* every COMPANY runtime credential, from `Hasm__Database__CompanyCredentialDirectory` (owner-only files).

**Consequence:** "the ordinary application runtime database credentials" means both together. Every boundary below assumes the attacker holds the system runtime account AND the company runtime role R.

**What those credentials can write (the C1 finding).**
* System database:
  * users;
  * sessions: the token hash, which is a plain SHA-256; the MFA-verified time; the selected company;
  * role assignments and permissions;
  * company access;
  * new TOTP secrets (INSERT);
  * the TOTP state, confirmation time, last step and failure counter. UPDATE is allowed on these columns only. Under D-235 the runtime account never has UPDATE of the material or identity, and never DELETE or TRUNCATE.
  * login throttles.
* Company database: plans, closures, lines, plan events, approvals, the business audit table, and every ordinary posting table.

So the credentials alone can fabricate any decision the API makes from those tables: who the user is, what they may do, whether their second factor is fresh.

**Therefore a signature counts as an authorization only if the decision to sign rests on something the runtime credentials can neither write nor use.** Two such sources exist without new cryptographic infrastructure:
* **(i)** a private key held by a second person on another machine;
* **(ii)** a TOTP secret pinned by the owner and verified for each signature. Its plaintext needs the key ring outside the database. The database holds only ciphertext, and the D-235 rules forbid the runtime account to change or delete it.

Session state, roles, permissions and the session's MFA time are never inputs to an authorization decision. They only route the user interface.

**Option (a), a separate executor database principal, fails C1 for the same reason:** the API decides when to use it, and it gives no per-plan binding. It is not part of either resolution.

## 3. Options considered

Revision 1, section 3, has the full comparison.

| Option | Decision |
|---|---|
| (a) Separate executor principal | Rejected: C1, and no per-plan binding. |
| (b) Symmetric MAC verified in the database | Rejected. It needs a secret in the database and a definer function with a pinned `search_path`; the guard refuses that, and the secret would be in every backup. |
| (b) Asymmetric signature verified in the database | **Kept** as the database side of both resolutions (section 4). |
| (c) Where the signing authority lives | **(i)** offline second-person signer (section 5, recommended), or **(ii)** owner-registered in-app signers with per-signature TOTP (section 6). Approver-held WebAuthn keys remain a later upgrade of (ii), not in Release 1. |

## 4. Database side (common to both resolutions)

### 4.1 Keys

* **Algorithm.** RSA with a modulus of exactly 3072 bits (k = 384 bytes) and public exponent exactly 65537, scheme RSASSA-PKCS1-v1_5 with SHA-256. Only public keys are in the database.
* **Table `ReplaySignerKey`** (new, company database). Columns:
  * key id: text matching `^[a-z0-9-]{1,40}$`;
  * modulus: `numeric`, with a CHECK of `>= 2^3071` and `< 2^3072`;
  * signer user id: uuid, the person the key belongs to. In (ii) the installation key has NULL here, and each message names its signer (section 6);
  * registered at;
  * revoke-from: timestamptz, NULL or set once.
* **Who writes it.** Only the database owner account, through `Hasm.Admin`. The table is append-only except for the one-time revoke-from. R holds SELECT only.
* **Pinned copy.** The API configuration pins the same keys: key id, modulus fingerprint, revoke-from. Read-side verification trusts this pinned list, not the table (section 9).

### 4.2 Verification function (feasibility proven)

`rsa_verify(n numeric, signature bytea, message bytea)` returns boolean. It is plain PL/pgSQL with invoker rights and no extension. It:
1. refuses unless `length(signature) = 384` exactly;
2. converts the signature to `numeric`, big-endian, and refuses `s >= n`;
3. computes `s^65537 mod n` with 16 squarings and one multiplication, using `mod()` only and never `/`;
4. compares the result, as a number, with the number of the full encoding `00 01 FF×(384-3-19-32) 00 || 3031300d060960864801650304020105000420 || sha256(message)`. The DigestInfo is constant; hex constants are built with `decode(..., 'hex')`.

Comparing the whole encoding exactly, rather than parsing the decrypted block, avoids the PKCS#1 v1.5 parsing forgeries.

The feasibility probe ran on the isolated 5433 cluster (PostgreSQL 16.15, through the CPU gate; local only):

| Case | Result |
|---|---|
| valid signature (.NET `RSA.SignData` with `Pkcs1`, fresh 3072-bit key) | true |
| message changed | false |
| signature changed | false |
| signed by another key | false |
| PSS padding | false |
| signature value `>= n` | false |
| signature one byte short | false |
| `prosecdef` | false |
| cost | **4.2 ms per verification** (VOLATILE, distinct messages) |

`pgcrypto` has no signature verification, and the guard refuses extensions.

### 4.3 Enforcement

One additive company migration; the lead allocates its number.

**New tables.** All are append-only: UPDATE, DELETE and TRUNCATE are refused, as in 0036.
* `ReplayExecutionGrant`: grant id, key id, plan id, executor user id, signer user id, not-before and not-after (Unix seconds, bigint), signature (`bytea`, length 384), and accepted at. Accepted at is set by the trigger to `clock_timestamp()` and is never supplied by the client.
* `ReplayGrantUse`: execution id (unique) and grant id (unique). A grant is used at most once, and an execution has at most one grant.
* `ReplayApprovalSignature` (resolution (ii) only): approval id (unique), key id, signer user id, signed at, signature, accepted at.

**At insert.** A `BEFORE INSERT` trigger with invoker rights requires:
* the key exists, its modulus has 3072 bits, and `accepted_at < coalesce(revoke_from, 'infinity')`;
* `rsa_verify` holds over the message of section 4.4, built from the rows;
* `not_before <= clock_timestamp() <= not_after`, in epoch seconds;
* signer independence: the signer is not the plan's creator, not the approval's approver (except in (ii)'s approval signature, where the signer must BE the approver), not the executor, and not the event document's creator, editor or submitter. This is the rule of `f_17b9c93f`, applied to the signer id;
* for a key with a non-NULL signer user id, the message's signer equals it.

**At commit.** New `DEFERRABLE INITIALLY DEFERRED` constraint triggers on the 0036 execution table and on `ReplayGrantUse` require:
* every execution of the transaction has exactly one grant use;
* its grant is **re-verified**: the signature is checked over the message rebuilt NOW from the rows. A closure or line row added after the grant was inserted therefore fails;
* `clock_timestamp() <= not_after` still holds;
* the grant's plan and executor equal the execution's plan and `executed_by`;
* in (ii), the approval named by the plan has its verified approval signature, and the grant's signer differs from that signature's signer.

Nothing in 0036 is changed or replaced; these are new triggers on new and existing tables.

**Clock.** The database server's `clock_timestamp()`, synchronised by NTP. The signer sets not-before to its own time minus 5 minutes. `now()`, the transaction start, is never used for the window.

### 4.4 The signed messages, byte-exact

**Encoding.**
* The UTF-8 bytes of ASCII text.
* One field per line, separated by a single LF (0x0A), with no trailing LF.
* No spaces. Keys are lowercase. Fields appear in exactly the order below.

**Grant message** (resolutions (i) and (ii)):

```
hasm-replay-v2
grant
company=<company id>
plan=<plan id>
revision=<plan revision>
approval=<approval id>
approver=<approver user id>
creator=<plan creator user id>
document=<event document id>@<document revision>
item=<root item id>
warehouse=<root warehouse id>
payload=<hex sha256 of the plan payload>
fingerprints=<the seven fingerprints>
closure=<closure hash>
lines=<hex sha256 of the lines text>
executor=<executor user id>
grant=<grant id>
signer=<signer user id>
key=<key id>
not-before=<integer>
not-after=<integer>
```

**Approval message** (resolution (ii) only): the first 15 lines of the grant message (through `lines=`), with line 2 equal to `approve` instead of `grant`, followed by three lines: `signer=<signer user id>`, `key=<key id>` and `signed-at=<integer>`.

**Value rules.**
* **uuid:** lowercase, hyphenated, 36 characters. This is PostgreSQL `uuid` output and never upper case.
* **integer:** decimal, with no sign and no leading zeros.
* **hash:** 64 lowercase hex characters. In SQL, `encode(sha256(x), 'hex')`, never `bytea::text`.
* **time:** whole Unix seconds. Stored as bigint, so TimeZone and DateStyle never enter.

**Field sources.**
* `company`: the company identity table.
* `revision`: the plan revision named by the approval.
* `approval`, `approver`, `creator`, `document`, `item`, `warehouse`: the plan and approval rows of 0036.
* `payload`: the hash of `convert_to(payload, 'UTF8')`; the server encoding is UTF-8.
* `fingerprints`: the plan's seven `char(64)` fingerprint columns in their 0036 declaration order (plan, comparison, evidence, periods, position, policy, accounting), joined by `,` (0x2C).
* `closure`: the plan's stored closure hash, after the database has recomputed it with `f_3cdca6ba` and compared.

**Lines text.**
* One line per plan line, ordered by line number (integer), joined by LF, with no trailing LF.
* Each line is `<line number>|<posting id, or - when NULL>|<entry date>|<period id>|<branch id>|<account id>|<debit>|<credit>`.
* Entry date: `to_char(d, 'YYYY-MM-DD')`, which has no locale-dependent pattern.
* Debit and credit: the `numeric(28,6)` values with exactly six decimals and no thousands separator. This is PostgreSQL `numeric` output for the column, for example `12.500000`. In .NET it is `ToString("F6", CultureInfo.InvariantCulture)`.
* A plan with no lines: the hash of the empty string, `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.

**Known-answer vectors.** These are pinned in the unit tests for the SQL builder and the .NET builder alike.

* **Ids.** Each id named by a digit or letter `d` has the form `dddddddd-dddd-4ddd-8ddd-dddddddddddd`, for example `44444444-4444-4444-8444-444444444444`.

  | Field | Id |
  |---|---|
  | company | 1 |
  | plan | 2 |
  | approval | 3 |
  | approver | 4 |
  | creator | 5 |
  | document | 6 |
  | item | 7 |
  | warehouse | 8 |
  | executor | 9 |
  | grant | a |
  | signer | b |

* **Other fields.**

  | Field | Value |
  |---|---|
  | revision | 2 |
  | document revision | 3 |
  | fingerprints | `1`×64 through `7`×64 |
  | closure | `c`×64 |
  | key | `k1` |
  | not-before | `1795651200` |
  | not-after | `1795737600` |

* **Payload.** `{"Contract":"stock-compensation-plan-payload-v1"}`, whose hash is `e5f0ae53bfb61148c98966f48120e58e83c035a397ad09ca30e879cd3417b18f`.
* **Lines.**
  ```
  1|9f6c1d2e-0000-4000-8000-000000000001|2026-03-11|1a2b3c4d-0000-4000-8000-0000000000aa|2b3c4d5e-0000-4000-8000-0000000000bb|3c4d5e6f-0000-4000-8000-0000000000cc|12.500000|0.000000
  2|-|2026-03-12|1a2b3c4d-0000-4000-8000-0000000000aa|2b3c4d5e-0000-4000-8000-0000000000bb|4d5e6f70-0000-4000-8000-0000000000dd|0.000000|12.500000
  ```
  The lines hash is `b5fd4e1d6a1b3265a0c351234a1ffc54b0fe850693aecdb67f1163c38facda49`.

* **Results.**

  | Message | Length | SHA-256 |
  |---|---|---|
  | Grant | 1260 bytes | `2bfb65d86a007c70bdb959a12c2ee6f26027c111665fea2d077ae12c1cc5b195` |
  | Approval (signer `b`, key `k1`, `signed-at=1795651200`) | 1151 bytes | `1d7683564e8de382ff2b865d088f54251aacf0063112c89b9cdcac1bad9aa60c` |

* **RSA vectors.** Slice A's tests also use a fixed 3072-bit test key with a valid signature over the grant vector, and Wycheproof-style invalid cases. Each case must give the same result in SQL and in .NET:
  * wrong length, 383 and 385 bytes;
  * the leading zero byte dropped;
  * s = 0, s = 1, s = n − 1, s = n, s > n;
  * altered padding;
  * altered DigestInfo;
  * a SHA-1 DigestInfo;
  * a PSS signature;
  * a 2048-bit key;
  * exponent 3.

### 4.5 Name resolution and session independence (M1)

* **Schema qualification.** Every relation inside the new functions is written `public."t_…"`. Built-in functions are unqualified, because the guard refuses `pg_catalog.`.
* **Invariant (tested):** R has no TEMPORARY privilege on its database and no CREATE on any schema. These are the existing `database_create_or_temp` and `schema_create` violations of `CompanyRuntimePrivileges`, which the API refuses before use. So no temporary relation or type, and no user-defined function or operator, can shadow what the verifier names.
  * The test runs on a freshly provisioned company after `grant-company-runtime`, and on a restored database after its grant step.
  * The verifying functions run only inside triggers on R's own writes; no other role's session ever calls them.
* **Session settings.** The message uses only the formats of 4.4. TimeZone, DateStyle, IntervalStyle, `bytea_output`, `extra_float_digits` and `lc_numeric` therefore cannot change it. There are no floats and no `to_char` with D or G. Every ORDER BY is on uuid or integer columns, never on text.
* **Test.** The message is built in sessions with each of those settings changed, and must be byte-identical every time.

### 4.6 Revocation (M3)

**Rule.** The owner sets a key's `revoke_from` once. A signature is valid exactly when two conditions hold:
* the key verifies it;
* its database-assigned `accepted_at` is before `revoke_from`, or `revoke_from` is NULL.

**Consequences.**
* History accepted before `revoke_from` stays verifiable, by the database and by the readers.
* Every new signature by the key is refused from `revoke_from` on.

**Suspected compromise.** The owner may set `revoke_from` to the suspected time, including a time in the past. Signatures accepted after it become invalid. The readers refuse their epochs (section 9) until the owner decides their remediation (decision D5).

**Precedence for reading.** The pinned application list carries the same `revoke_from`, and the pinned list wins.

`accepted_at` is assigned by the trigger, so a writer cannot backdate it. A database owner can, but the owner's writes are covered by section 9's assumptions, not by this rule.

### 4.7 Content binding and the bearer window (L3)

**What the grant binds.** It is valid for exactly one plan, one plan revision, one approval and approver, one creator and one event document revision. It also binds the closure, the payload, all seven fingerprints, the lines, one executor id, one use and one time window.

**What 0036 binds of the replayed effects.**
* The epoch rows are confined to the plan's closure and to the single execution of this approved plan, within this transaction.
* Lineage is enforced: original, then storno, then rebook.
* Periods must be Open.

0036 does **not** require the posted amounts to equal the plan lines.

**Remaining gap.** A party that holds a valid grant within its window and can write to the database could execute that plan with epoch amounts that differ from the signed lines but stay within the closure. Two measures close the gap:
* **Read side.** The .NET verifier of section 9 recomputes the executed epoch's ledger effect per period, branch and account, and requires it to equal the signed lines. Otherwise the epoch is not treated as valid history, and its period cannot close.
* **Exposure.** The grant reaches the database only inside the execution transaction. The executor uploads the grant file when executing, and the application inserts the grant, the execution and the grant use together. A grant is therefore never readable from the database before it is consumed, and it is single-use.

A database-side equality check of posted amounts against lines is part of slice A if E3's posting shape allows it to be stated in SQL. That is to be confirmed when E3 is planned. Until then the residual is the read-side refusal described above.

## 5. Resolution (i), RECOMMENDED: offline second-person execution grant

**Who.** A named person whom the owner registers as a replay signer (decision D2). The signer is a different person from the plan's creator, the approver and the executor, and from the event document's creator, editor and submitter. The database enforces this (4.3).

**Key.**
* The signer generates their own RSA-3072 key pair on their own offline machine with a small offline tool, `Hasm.ReplaySigner keygen`, which uses no network. The private key never leaves that machine.
* The public key file goes to the owner. The owner registers it in every company database with `Hasm.Admin register-replay-signer <public key file> <signer user id>`, as the owner account, and a system audit event is written. The owner adds the key to the API's pinned list.

**Participants list.** The owner also gives each signer a list of the user ids and names of the people allowed to create, approve and execute replays. The list is pinned in the offline tool's own configuration, outside the database. The tool refuses to sign a plan whose creator, approver or executor id is not on the list. It shows names from this list, never from the export, because names in the database can be forged.

**Flow per execution.**
1. The plan is created and approved in the application, as today. The approval is recorded but grants nothing by itself.
2. An operator runs `Hasm.Admin export-replay-plan <company> <plan> <executor>` with the owner account. It produces a file with every field of 4.4 except grant, signer, key and the times, plus display data: the closure positions with codes and names, and the lines with accounts, periods, branches and amounts.
3. The file is carried to the signer's machine. The offline tool:
   * rebuilds the lines digest and the message from the file's fields;
   * shows the content in full;
   * checks the participants list;
   * on the signer's explicit confirmation, signs with not-before = now − 5 minutes and not-after = now + the validity of decision D4, and writes a grant file.
4. The signer confirms the request with the approver through a channel outside the application (decision D2), for example in person or by phone. This step remains necessary while runtime credentials can mint sessions of real users (section 15): a thief could otherwise make a forged plan appear to come from real colleagues.
5. The executor uploads the grant file in the application when executing. The grant, the execution and the grant use are written in one transaction, and the checks of 4.3 bind them at insert and at commit.

**Why a forged export does not help.** The database verifies the signature against the message it rebuilds from its own rows. A file that misstates the rows yields a signature that does not verify. A plan forged INTO the database by the runtime credentials is shown to the signer as it actually is; signing it is a human decision about visible content, confirmed out of band.

**Cost.** Each execution needs one offline step by a second person. Back-dated replays are rare corrections. A second machine and per-person keys are required.

## 6. Resolution (ii): owner-registered in-app signers with per-signature TOTP

**Registry: one new system migration** (the lead allocates its number).
* **`ReplaySignerRegistration`:** user id, pinned TOTP secret id, registered at, revoked at (set once).
  * Only the system database owner writes it, through `Hasm.Admin register-replay-signer-totp <login>`, with a system audit event.
  * The system runtime account holds SELECT only. This is a new `SystemRuntimePrivileges` entry, refused at start of use like the D-235 TOTP gate.
* **`ReplaySignerAttempt`:** signer, time, outcome. It is append-only: the runtime account holds INSERT and SELECT, and UPDATE, DELETE and TRUNCATE are refused by grants and by an owner-only trigger.
* **Configuration.** The explicit non-least-privilege opt-in of `HasmDatabaseOptions` (D-235) is refused while replay signing is enabled.

**Signing key.** One installation RSA key lives in `Hasm__Replay__SigningKeyDirectory`. The files are owner-only, like the backup signing key, and kept separate from the database credentials, the audit key and the key ring. Its public key is registered as in 4.1, with signer user id NULL. Each message names its registered signer.

**Two signatures, by two different registered signers.**
* The **approval signature** is by the approver, who must be a registered signer A, at approval time.
* The **execution grant** is by registered signer B, with B ≠ A, B not the creator, and B not the executor.

**Each signature requires the signer to type a fresh TOTP code for this operation.** The signer component:
1. reads the registration, which the runtime account can read but only the owner can write;
2. loads the pinned TOTP secret by its id. The material and identity are owner-only (D-235), the runtime account has no DELETE, and a newly inserted secret has a different id;
3. requires the secret's state to be Active;
4. decrypts the secret with the key ring held outside the database;
5. verifies the code at the current step under the existing replay rule: the last accepted step strictly increases, and the runtime credentials can only raise it, never lower it;
6. refuses after 5 failures in 15 minutes, counted from the append-only attempt log. The runtime-writable login throttle is not used;
7. writes the attempt row, then signs.

Session, roles, permissions and the session's MFA time are not inputs.

**Re-enrolment voids the registration.** A re-enrolled signer has a new secret id. The registration still pins the old id and stops working until the owner registers the signer again.

**Cost.**
* a system migration;
* an owner registration step per signer;
* an installation key on the API host.

It does not stop a fully compromised API process, which holds both the key ring and the signing key.

## 7. Threat boundaries

In both tables, "runtime credentials" means the system runtime account AND the company role R together (section 2).

### 7.1 Resolution (i): offline second-person grant

| Attacker holds | Plan / approval rows | Execute, or write epoch rows | Detected | Residual / out of scope |
|---|---|---|---|---|
| **Runtime credentials** | Can be written (unsigned); they grant nothing. | **Prevented.** No grant verifies without a signer's private key; the deferred checks roll back the whole epoch. | Refused writes in the database log. | Availability: forged proposed plans can bind documents (X7) until cancelled; cancel, reject and stale are unsigned (D6). |
| Runtime credentials **+ sessions, users, roles and MFA times they mint** | As above. | **Prevented.** The signer is a person outside the application, using a participants list pinned outside the database. | As above. | A forged plan that appears to come from real colleagues is stopped by the signer's out-of-band confirmation (5, step 4). |
| Runtime credentials **+ a legitimately issued grant file, intercepted within its window** | | Only that plan, that executor id and that content, once. | Different amounts are refused as history by the read-side check (4.7). | The one approved replay may be executed by the thief within the window, consuming the grant. |
| **Full compromise of the API process** | Yes. | **Prevented** for any plan without a grant: there is no signer key on the API host. For a grant delivered to the compromised process: as in the row above. | As above. | |
| A signer's offline machine and private key | | Without database access: nothing. With runtime credentials as well: equivalent to a malicious signer, one plan per grant, attributable. | Key revocation (4.6). | Collusion or coercion of a signer. |
| **Database owner or superuser** | Can write any rows and switch triggers off. | Can write an execution without a valid grant. | **Readers, the close probe and the restore check refuse it** (.NET verification against pinned keys, section 9). | Preventing the write (design T10). |

### 7.2 Resolution (ii): registered in-app signers with per-signature TOTP

| Attacker holds | Approve with signature / execute | Detected | Residual / out of scope |
|---|---|---|---|
| **Runtime credentials** | **Prevented.** The registry is owner-written. The TOTP secret is pinned by id, and its plaintext needs the key ring outside the database. Guessing is bounded by the append-only attempt log: 5 per 15 minutes per signer. | The attempt log. | Availability: signers locked out by failed attempts, by a raised last step, or by a revoked TOTP state; unsigned creation, cancel, reject and stale (D6). |
| Runtime credentials **+ minted sessions, users, roles and MFA times** | **Prevented.** None of these is an input. | | |
| Runtime credentials **+ one registered signer's TOTP device or a phished code** | **Prevented.** A second, different registered signer is required. | The attempt log and the audit events. | Two registered signers colluding or compromised. |
| **Full compromise of the API process** | **NOT prevented.** The process holds the key ring and the signing key. | The signatures and audit events name the signers. | Prevention needs resolution (i). |
| Database owner or superuser | Can write unsigned rows. | Readers refuse them (section 9). | Preventing the write. |

## 8. What is preserved

* **Least privilege.** No new database role and no definer function. R's INSERT on the execution tables is conditional on a grant that R cannot produce. Keys and registrations are written by the owner.
* **External audit HMAC chain.** Unchanged; business audit events 1154 to 1159 are still required as today. Grants and signatures add publicly verifiable evidence. The audit detail can carry the grant id, and the signature is unique because its length is exact.
* **Segregation of duties.** Unchanged in the database (`f_17b9c93f`; the executor is not the creator) and extended to the signer (4.3). Resolution (ii) requires two different registered signers.
* **Fiscal periods.** Unchanged: the 0036 entry guard with `FOR SHARE` on the period, and the stock-period guard of 0034.
* **The owner's Q1 safeguards** (quoted verbatim, each followed by where this design keeps it):

  | Q1 safeguard (verbatim) | Where it is kept |
  |---|---|
  | "preserve all existing CHECKs, triggers and immutability; never relax or replace an existing safeguard" | Additions only; N17 is extended. |
  | "an entry-level guard refusing unauthorized back-dated movements" | 0036 is unchanged, and an execution now also needs its verified grant. |
  | "the replay exception only for an operation bound, in the same DB transaction, to the single execution of an approved plan" | Unchanged, plus the single grant use. |
  | "every replayed operation and entry bound to the plan's approved item/warehouse closure" | Unchanged, and the closure and lines are signed. |
  | "segregation of duties and Open fiscal periods enforced" | See the bullets above. |
  | "no unauthorized reversals of original, event, storno and rebook operations" | Unchanged (0036 X6). |
  | "transactional consistency and an auditable execution record" | Deferred checks, the grant and the audit event. |
  | "never a client-controlled session flag or a general back-dating bypass" | There is no flag (section 10). |
  | "historical accounting records and traceability preserved" | Nothing is edited; every new table is append-only. |

## 9. Keeping unverified replay effects out of valid financial history (M4)

* **At write.** No execution commits without a grant that was verified at insert and re-verified at commit (4.3). Without an execution, no epoch row can commit.
* **At read, independent of the database's functions.** The snapshot readers, the close probe (slice P) and the restore check do their own verification:
  * they rebuild the message of 4.4 in .NET from the rows;
  * they verify the RSA signature in .NET against the keys **pinned in the application configuration**, with their revoke-from;
  * they compare the epoch's ledger effect with the signed lines (4.7).

  An epoch is refused, and not treated as valid history, if any of these holds:
  * its grant does not verify;
  * its key is not pinned;
  * its acceptance time is not before the pinned revoke-from;
  * its ledger effect differs from the signed lines.

  None of the following changes the outcome: a replaced database function, a replaced key row, rows written with triggers switched off, or a restore that loads data before it creates triggers.
* **After a restore.** The restore check runs this verification over every grant. A failure marks the restored copy unusable.
* **Period close.** A period that contains a refused epoch cannot be closed.

## 10. No application flag; migration-guard compliance

No setting, session variable or parameter opens anything.

**What the migrations use.** The company migration, and for (ii) the system migration, use only:
* `CREATE TABLE`;
* PL/pgSQL `CREATE FUNCTION` with invoker rights;
* `CREATE TRIGGER` and `CREATE CONSTRAINT TRIGGER`;
* `REVOKE ... FROM PUBLIC`;
* the built-ins `numeric`, `sha256`, `get_byte`, `length`, `mod`, `decode`, `encode`, `convert_to`, `to_char`, `string_agg`, `clock_timestamp` and `extract`, and the allowed `pg_current_xact_id()`.

**What they avoid:** `SECURITY DEFINER`, `search_path`, `set_config`, dynamic SQL, extensions, other `pg_` names and backslash literals.

**No guard exception is needed.**

## 11. Migrations and provisioning

**Resolution (i).**
* One additive COMPANY migration: the tables `ReplaySignerKey`, `ReplayExecutionGrant` and `ReplayGrantUse`, the functions and the new triggers. No system migration and no cluster role.
* Tools, run with the owner account and audited:
  * the offline `Hasm.ReplaySigner` (`keygen`, `show`, `sign`);
  * `Hasm.Admin register-replay-signer`, `revoke-replay-signer` and `export-replay-plan`.
* Registration runs for every company database. Provisioning runs it for a new company when keys are configured, and it runs again after a restore. A company without a registered key cannot execute: it fails closed.

**Resolution (ii).** The same company migration plus `ReplayApprovalSignature`, and one additive SYSTEM migration: `ReplaySignerRegistration`, `ReplaySignerAttempt` and the owner-only triggers. The `SystemRuntimePrivileges` entries are SELECT on the registry, and INSERT and SELECT on the attempt log.

**Runtime grants (company).**
* SELECT on `ReplaySignerKey`.
* SELECT and INSERT on the grant tables.
* The INSERT grant on the four execution tables returns only with these migrations, the first real caller and the owner decisions of section 14.

## 12. Adversarial PostgreSQL tests

The tests run on the role-capable isolated cluster. The attackers are the SYSTEM runtime account and the company runtime role R, separately and together.

1. **The C1 replay, in both resolutions.**
   * With the system runtime account: mint a session with a fresh MFA time for an enrolled user, create a user, and assign roles and company access.
   * With R: write the plan, closure, lines, approval, audit row 1154, execution, members and entries.
   * Expected: the execution never commits without a verified grant. In (ii), driving the honest API with the minted sessions produces no signature, because the TOTP of the pinned secret is required.
2. **(ii): the registry and TOTP.** The system runtime account attempts each of the following:

   | Attempt | Expected |
   |---|---|
   | INSERT or UPDATE on `ReplaySignerRegistration` | 42501 |
   | INSERT a new TOTP secret for a registered signer | No signature: the registration still pins the old id. |
   | UPDATE the state or last step of the pinned secret | Denial only; no signature results. |
   | UPDATE the material or DELETE the pinned secret | Refused. |
   | DELETE or UPDATE attempt rows | Refused. |
   | Guess codes repeatedly | Stopped by the attempt limit. |
   | Re-enrol the signer | The registration is void. |

3. **Forged, missing and replayed grants.** Each of the following is refused:
   * a grant for another plan, another executor, another company (with the same key registered in both), another revision, another closure, other lines, another payload, other fingerprints or another document revision;
   * a second use of a grant;
   * a grant before not-before;
   * a grant after not-after, both at insert and at commit (a transaction kept open past not-after);
   * a closure or line row added after the grant within the same transaction.
4. **Keys.**
   * An unregistered key, a 2048-bit key, and the wrong exponent are refused.
   * A revoked key: a new signature is refused; a grant accepted before revoke-from still verifies; a grant accepted after a backdated revoke-from is refused by the readers.
   * A key row written by R or by the system account: 42501.
5. **Segregation of duties.** A grant is refused, even with a valid signature, if its signer is the plan's creator, the approver, the executor, or the event document's creator, editor or submitter. In (ii), the grant is refused if its signer equals the approval signer.
6. **Name resolution and settings (M1).**
   * R has no TEMPORARY and no CREATE on fresh, provisioned and restored databases.
   * Attempts to create a temporary table, type or function, or a public function or operator shadowing a built-in, fail.
   * The message is byte-identical in sessions with TimeZone, DateStyle, IntervalStyle, `bytea_output`, `extra_float_digits` and `lc_numeric` changed.
7. **Signature primitives.** The known-answer and Wycheproof-style vectors of 4.4 give the same results in `rsa_verify` and in the .NET verifier.
8. **Read side (M4).** Readers, the close probe and the restore check each refuse:
   * an execution inserted by the owner with triggers switched off and an invalid or missing grant;
   * an execution accepted after the database function was replaced by one that returns true;
   * a key row replaced in the database;
   * an epoch whose ledger effect differs from the signed lines.
9. **Mutation proofs.** Removing any one of the following makes a named test fail:
   * the length check;
   * the expiry check at commit;
   * the content re-verification at commit;
   * the signer independence check;
   * the single-use constraint;
   * the read-side lines comparison.

## 13. Effects on the merged executor plan (`docs/PHASE-3.8D-EXECUTOR-PLAN.md`)

* **New slice A, before E3.** It contains:
  * the migrations of section 11;
  * `rsa_verify` and the message builders, in SQL and .NET;
  * the read-side verifier;
  * the tooling of the chosen resolution;
  * the tests of section 12.

  It stays inert while no key is registered. For (ii) it also contains the system migration and the signer component.
* **E3.**
  * The executor accepts the grant file, writes the grant, the execution and `ReplayGrantUse` in one transaction, and fails closed without a grant.
  * The rollback matrix gains "grant refused" and "grant expired at commit".
  * E3's planning confirms whether a database-side check of posted amounts against lines can be added (4.7).
* **P.** The close probe and the readers use the .NET verifier (section 9).
* **E5 and executor-plan section 7 are replaced by this design.** The runtime INSERT grant on the four execution tables returns only when all of these hold:
  * slice A is merged;
  * the first real caller exists;
  * a revision of this design is approved;
  * the owner has taken the decisions below.

## 14. Owner decisions needed before activation

* **D1 (activation baseline).** Proposed wording for the owner:

  > "Shall replay execution require, for each execution, a grant signed by a second, owner-registered person with a private key kept on a separate offline machine? This is resolution (i), recommended: stolen runtime database credentials, sessions minted with them, and a fully compromised application server cannot execute a replay without such a grant. Or do you accept owner-registered in-application signers, two different people, who each confirm with a fresh code from their own registered authenticator? This is resolution (ii): stolen runtime database credentials and minted sessions cannot execute a replay, but a fully compromised application server can."
* **D2 (signer registry and authentication).**
  * Who may be registered as a replay signer, and by whom. Recommended: the owner account through `Hasm.Admin`, audited.
  * The rule that a signer is never the creator, approver or executor of a plan they sign, nor the creator, editor or submitter of its document.
  * For (i): the participants list held by each signer, and the out-of-band confirmation with the approver.
  * For (ii): the pinned authenticator, and the rule that re-enrolment voids the registration.
* **D3 (key custody).**
  * For (i): per-signer keys, generated and kept on each signer's offline machine, backed up by that signer under a rule the owner sets.
  * For (ii): the installation signing key in its own owner-only directory, separate from the database credentials, the audit key and the key ring.
  * For both: the rotation interval, and who may revoke a key.
* **D4 (grant validity).** The maximum window between signing and execution. Recommended: 24 hours for (i) and 10 minutes for (ii). A grant is always single-use.
* **D5 (revocation after a suspected compromise).** When the owner backdates a key's revoke-from to the suspected time, replays accepted after it are refused as history. Decide what then happens to them. Recommended: they are treated as unauthorized and corrected by a new, properly granted plan under owner review.
* **D6 (unsigned steps).** Accept that plan creation, approval in (i), cancel, reject and stale stay unsigned. None of them changes financial history; the residual is availability, because forged proposed plans can block documents until cancelled. The alternative is to also require a grant for plan creation.
* **Not a decision of this design, but relevant to activation:** the separate register item on runtime-credential session minting (section 15).

## 15. Residual risks and the separate issue

* **Separate application-wide issue.** This was a side finding of C1. It is a register item handled by the lead and is not solved here. The system runtime credentials can create authenticated, MFA-satisfied sessions for any user: the token hash is a plain SHA-256, and the session's MFA time is writable.
  * This design takes no authorization decision from sessions.
  * For (i), the issue is why the signer's out-of-band confirmation is part of the procedure (5, step 4).
  * The rest of the application depends on sessions.
* **Residuals of (i).**
  * A malicious or coerced signer: one plan per grant, attributable, revocable.
  * An intercepted grant within its window: only that plan and that executor, once, with different amounts refused as history.
* **Residuals of (ii).** It prevents the database-credential attacks, but not a fully compromised application process.
* **Security-critical code.** The verification code, about 25 lines of PL/pgSQL and their .NET twin, together with the message builders, needs the vectors and mutation proofs of section 12.
* **Key loss.** It blocks new grants but never reading, because verification needs only public keys. History accepted before a key's revoke-from remains verifiable.
