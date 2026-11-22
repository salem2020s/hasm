# Phase 3.8D threat T2: authorization of replay operations (design proposal)

Status: **DESIGN PROPOSAL, revision 3, documentation only. Nothing here is implemented.** Author: Agent ODC (design author). The independent adversarial review is Agent opusSEC's. Base: `main` `22c9d23`.

Review history (record: `review-records/PR-218-t2-design-review.md`):
* Revision 1 (`a5010a5`): 1 Critical, 4 Medium, 3 Low.
* Revision 2 (`d62bc29`): C1, M1 to M4, L1 and L2 resolved; L3 partly resolved. New: 3 Medium (N1 to N3) and 2 Low (N4, N5).

Section 0 maps every finding of both rounds to its resolution.

Until this design is independently approved and the owner decisions of section 14 are taken, **replay executor production activation and the runtime INSERT grant on the four execution tables stay disabled** (owner direction of 2026-11-22).

Sources:
* the owner's T2 direction of 2026-11-22 (section 1);
* `docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` revision 3 and `docs/PHASE-3.8D-EXECUTOR-PLAN.md`;
* company migrations 0014 (costing ledger), 0034 and 0036;
* system migrations 0031 (TOTP store) and 0035 (owner-only TOTP material, D-235) and `SystemRuntimePrivileges`;
* the migration guard of PR 188;
* the feasibility probe of section 4.2.

---

## 0. Findings and resolutions

### 0.1 Revision 3 (findings at `d62bc29`)

| Finding | Resolution in revision 3 |
|---|---|
| **N1 (Medium, (ii)).** The TOTP attempt limit can be raced, and the guessing budget has no cap. | Section 6.1. Each signer's attempts are serialised by an advisory lock. The attempt row is written and committed BEFORE the code is verified. Only an authenticated success record turns an attempt into a success; its MAC needs the key ring, which the runtime credentials do not have. A success never resets the count. A cumulative cap of 10 failed attempts suspends the registration until the owner re-registers. Every failure raises an alert. Attempt times come from the database clock. Tests cover a concurrent burst and the cap (section 12, test 2). |
| **N2 (Medium, (i) and (ii)).** The out-of-band confirmation is neither enforced nor recorded, and the display is not trustworthy. | Sections 4.4 (attestation) and 5. The offline tool recomputes the closure, lines and epoch hashes from exactly what it displays, and refuses on a mismatch. It shows ids and codes beside names, marked as database data. The signer confirms with BOTH the creator and the approver, who answer from their own knowledge: the plan id, the document, the debit and credit totals, and a 16-hex request digest. A structured attestation is signed with the grant. The database checks it against the plan's creator and approver and the request digest, and it is written to the audit. Resolution (ii) requires the same before the second signature (section 6). |
| **N3 (Medium).** The replayed amounts are not bound by the database. | Sections 4.3, 4.4 (`epoch=`) and 4.7. Binding is now a **slice A requirement and an activation condition**. The grant signs a digest of every epoch cost entry, layer and consumption, keyed by position and sequence. The commit trigger rebuilds that text from the rows and compares. Section 13 checks this against the merged executor plan and names the two changes it needs: E1 emits the full epoch text, and CI binds the compensation journals to the signed lines. D4 is shortened to 2 hours for (i). |
| **N4 (Low).** The owner decisions are incomplete. | Section 14. D1 names each resolution's residuals and the N3 activation condition. D2 adds the upkeep of the participants list. D4 is justified against N3. |
| **N5 (Low).** Table 7.2 overstates. | Section 7.2 states the N1 and N2 residuals. |

### 0.2 Revision 2 (findings at `a5010a5`)

| Finding | Resolution |
|---|---|
| **C1 (Critical).** The revision 1 in-process signer took its authority from system-database state that the runtime credentials can write: who the user is, permissions, a fresh second factor, two different people. A minted MFA-fresh session was accepted by the real API. Option (a) fails the same way. | Every signature's authority now comes from state the runtime credentials cannot create, in one of two ways. **(i)** An offline second person holds a private key on another machine (recommended, section 5). **(ii)** Owner-registered signers, whose pinned TOTP credential is verified for each signature (section 6). Both are specified in full, each with its own threat table (section 7). Every boundary is attacked with the SYSTEM runtime account as well as the company role (section 12). The general session-minting issue is referenced here, not solved (section 15). |
| **M1.** Name resolution of invoker-rights code. | Section 4.5. Relations are schema-qualified. The no-TEMP/no-CREATE invariant is stated and tested, including on provisioned and restored databases. The message does not depend on session settings, and this is tested. |
| **M2.** The message is underspecified. | Section 4.4. Byte-exact specification that can be rebuilt from this document alone, with known-answer vectors. |
| **M3.** Revocation contradicts itself. | Section 4.6. Each signature gets an acceptance time assigned by the database, and each key a revoke-from time. History accepted before revoke-from stays valid and new signatures are refused. The window of a suspected compromise is an owner decision. |
| **M4.** Read-side verification goes through a replaceable database function. | Section 9. Readers, the close probe and the restore check verify in .NET against keys pinned in the application. The threat tables are corrected. |
| **L1.** Signature length and constants. | Sections 4.2 and 4.4. Exact length 384, e = 65537, a constant DigestInfo, built-in `sha256`, `mod` only. Known-answer and edge vectors are required. |
| **L2.** Verified at insert only, against transaction time. | Section 4.3. Verified at insert AND re-verified at commit over the message rebuilt from the rows. The clock is `clock_timestamp()`, checked at both points. |
| **L3.** The execution signature does not bind content. | The grant binds the payload, the seven fingerprints, the closure, the document revision and the lines digest (section 4.4). Revision 2 closed the posted amounts only on the read side; revision 3 binds them in the database (N3). |
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
* `ReplayExecutionGrant`: grant id, key id, plan id, executor user id, signer user id, not-before and not-after (Unix seconds, bigint), the epoch digest (char(64)), the attestation (text, the exact bytes of 4.4), signature (`bytea`, length 384), and accepted at. Accepted at is set by the trigger to `clock_timestamp()` and is never supplied by the client.
* `ReplayGrantUse`: execution id (unique) and grant id (unique). A grant is used at most once, and an execution has at most one grant.
* `ReplayApprovalSignature` (resolution (ii) only): approval id (unique), key id, signer user id, signed at, signature, accepted at.

**At insert.** A `BEFORE INSERT` trigger with invoker rights requires:
* the key exists, its modulus has 3072 bits, and `accepted_at < coalesce(revoke_from, 'infinity')`;
* `rsa_verify` holds over the message of section 4.4, built from the rows. The epoch digest and the attestation are stored columns, so the message covers them;
* `not_before <= clock_timestamp() <= not_after`, in epoch seconds;
* signer independence: the signer is not the plan's creator, not the approval's approver (except in (ii)'s approval signature, where the signer must BE the approver), not the executor, and not the event document's creator, editor or submitter. This is the rule of `f_17b9c93f`, applied to the signer id;
* for a key with a non-NULL signer user id, the message's signer equals it;
* **the attestation is well formed (4.4):**
  * its creator and approver lines name exactly the plan's creator and the approval's approver;
  * each method is one of `in-person`, `phone`, `video`;
  * each confirmation time is `<= not_before + 300` and `>= not_before - 86400`;
  * its request line equals the request digest the trigger computes from the rows.

**At commit.** New `DEFERRABLE INITIALLY DEFERRED` constraint triggers on the 0036 execution table and on `ReplayGrantUse` require:
* every execution of the transaction has exactly one grant use;
* its grant is **re-verified**: the signature is checked over the message rebuilt NOW from the rows. A closure or line row added after the grant was inserted therefore fails;
* `clock_timestamp() <= not_after` still holds;
* the grant's plan and executor equal the execution's plan and `executed_by`;
* **the epoch text of 4.4 rebuilt from the cost entries, layers and consumption rows of this execution's epoch members hashes to the grant's signed epoch digest (N3).** Any difference in any entry's position, sequence, role, origin, kind, direction, date, quantity, amount or running figures, or any layer or consumption that differs, is missing or is extra, refuses the commit;
* in (ii), the approval named by the plan has its verified approval signature, and the grant's signer differs from that signature's signer.

Nothing in 0036 is changed or replaced; these are new triggers on new and existing tables.

**Clock.** The database server's `clock_timestamp()`, synchronised by NTP. The signer sets not-before to its own time minus 5 minutes. `now()`, the transaction start, is never used for the window.

### 4.4 The signed messages, byte-exact

The version line is `hasm-replay-v3`. Revision 3 added `epoch=` and `attestation=`, so revision 2's vectors no longer apply.

**Encoding.**
* The UTF-8 bytes of ASCII text, except that the attestation's note may hold other UTF-8.
* One field per line, separated by a single LF (0x0A), with no trailing LF.
* No spaces except inside the attestation's note. Keys are lowercase. Fields appear in exactly the order below.

**Grant message** (resolutions (i) and (ii)):

```
hasm-replay-v3
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
epoch=<hex sha256 of the epoch text>
executor=<executor user id>
attestation=<hex sha256 of the attestation>
grant=<grant id>
signer=<signer user id>
key=<key id>
not-before=<integer>
not-after=<integer>
```

**Request digest.** The first 16 hex characters of the SHA-256 of the first 17 lines of the grant message, through `executor=` and joined by LF. It names the content the people confirm, before the attestation, grant id, signer, key and times exist. A forged plan always has a different plan id, which is confirmed separately. The 64-bit prefix makes a matching second plan content impractical to find (about 2^64 work).

**Approval message** (resolution (ii) only): the first 15 lines of the grant message (through `lines=`), with line 2 equal to `approve` instead of `grant`, followed by three lines: `signer=<signer user id>`, `key=<key id>` and `signed-at=<integer>`.

**Value rules.**
* **uuid:** lowercase, hyphenated, 36 characters. This is PostgreSQL `uuid` output and never upper case.
* **integer:** decimal, with no leading zeros; a minus sign only for a negative value.
* **hash:** 64 lowercase hex characters. In SQL, `encode(sha256(x), 'hex')`, never `bytea::text`.
* **time:** whole Unix seconds. Stored as bigint, so TimeZone and DateStyle never enter.
* **numeric:** at the column's declared scale, with no exponent and no thousands separator, and a leading `-` only when negative. This is PostgreSQL `numeric` output for the column. In .NET it is `ToString("F<scale>", CultureInfo.InvariantCulture)`. For example, `numeric(28,6)` gives `12.500000` and `numeric(28,10)` gives `5.0000000000`.
* **boolean:** `t` or `f`.
* **date:** `to_char(d, 'YYYY-MM-DD')`.

**Field sources.**
* `company`: the company identity table.
* `revision`: the plan revision named by the approval.
* `approval`, `approver`, `creator`, `document`, `item`, `warehouse`: the plan and approval rows of 0036.
* `payload`: the hash of `convert_to(payload, 'UTF8')`; the server encoding is UTF-8.
* `fingerprints`: the plan's seven `char(64)` fingerprint columns in their 0036 declaration order (plan, comparison, evidence, periods, position, policy, accounting), joined by `,` (0x2C).
* `closure`: the plan's stored closure hash, after the database has recomputed it with `f_3cdca6ba` and compared.
* `epoch`: the grant's stored epoch digest. At commit it must equal the digest of the epoch text rebuilt from the rows (4.3).
* `attestation`: the hash of the grant's stored attestation bytes.

**Lines text.**
* One line per plan line, ordered by line number (integer), joined by LF, with no trailing LF.
* Each line is `<line number>|<posting id, or - when NULL>|<entry date>|<period id>|<branch id>|<account id>|<debit>|<credit>`. Debit and credit are `numeric(28,6)`.
* A plan with no lines: the hash of the empty string, `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.

**Epoch text (N3).** It covers every cost-ledger row (company migration 0014) that belongs to the epoch of this execution. "Epoch entries" are the cost entries whose operation has an `EpochMember` row of this execution. Entry ids (identity) and layer ids (random uuids) are not deterministic, so rows are keyed by position (item, warehouse) and sequence; 0014 makes (position, sequence) unique for entries and for layers.

Three groups, in this order, every line LF-joined, with no trailing LF:
1. **`E` lines**, one per epoch entry, ordered by item, warehouse (uuid order) and sequence:

   `E|<item>|<warehouse>|<seq>|<role>|<origin>|<kind>|<direction>|<date>|<quantity>|<amount>|<quantity after>|<value after>`

   * `role` is the member role: 1 storno, 2 event, 3 rebook.
   * `origin`: for role 1, the entry's reverses-entry id (the original's existing id); for role 3, the original entry id of its lineage row; for role 2, `-`.
   * Quantities are `numeric(28,10)` and amounts `numeric(28,6)`.
2. **`L` lines**, one per cost layer whose entry is an epoch entry, ordered by item, warehouse and layer sequence:

   `L|<item>|<warehouse>|<layer seq>|<entry seq>|<original quantity>|<original amount>|<source>`

   `source` is `-` when the layer has no source layer, otherwise `<item>/<warehouse>/<layer seq>` of the source layer.
3. **`C` lines**, one per consumption row whose entry is an epoch entry, ordered by entry item, entry warehouse, entry sequence, layer item, layer warehouse, layer sequence and restores (`f` before `t`):

   `C|<entry item>|<entry warehouse>|<entry seq>|<layer item>|<layer warehouse>|<layer seq>|<quantity>|<amount>|<restores>`

An epoch with no rows hashes as the empty string. Remaining layer quantities are not listed separately: 0014 derives them from the layers and consumption rows, which are bound. Each position's final quantity and value are bound by its last entry's running figures.

**Attestation (N2).** The exact bytes stored in the grant, LF-joined, with no trailing LF:

```
hasm-replay-attestation-v1
creator=<plan creator user id>|<method>|<unix seconds of the confirmation>
approver=<approver user id>|<method>|<unix seconds of the confirmation>
request=<16 hex request digest>
participants=<version of the signer's participants list>
note=<free text, UTF-8, no LF, at most 500 characters>
```

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
  | not-after | `1795658400` |

* **Payload.** `{"Contract":"stock-compensation-plan-payload-v1"}`, whose hash is `e5f0ae53bfb61148c98966f48120e58e83c035a397ad09ca30e879cd3417b18f`.
* **Lines.**
  ```
  1|9f6c1d2e-0000-4000-8000-000000000001|2026-03-11|1a2b3c4d-0000-4000-8000-0000000000aa|2b3c4d5e-0000-4000-8000-0000000000bb|3c4d5e6f-0000-4000-8000-0000000000cc|12.500000|0.000000
  2|-|2026-03-12|1a2b3c4d-0000-4000-8000-0000000000aa|2b3c4d5e-0000-4000-8000-0000000000bb|4d5e6f70-0000-4000-8000-0000000000dd|0.000000|12.500000
  ```
  The lines hash is `b5fd4e1d6a1b3265a0c351234a1ffc54b0fe850693aecdb67f1163c38facda49`.
* **Epoch text.** Six lines, with I = item 7 and W = warehouse 8 written in full:
  ```
  E|I|W|41|1|17|2|1|2026-03-11|5.0000000000|62.500000|105.0000000000|1312.500000
  E|I|W|42|2|-|1|1|2026-03-11|20.0000000000|240.000000|125.0000000000|1552.500000
  E|I|W|43|3|17|2|2|2026-03-11|5.0000000000|60.000000|120.0000000000|1492.500000
  L|I|W|9|42|20.0000000000|240.000000|-
  C|I|W|41|I|W|3|5.0000000000|62.500000|t
  C|I|W|43|I|W|3|5.0000000000|60.000000|f
  ```
  With I and W in full, the text is 915 bytes and its hash is `459e3dd2ca09cd91cf403a1dd772a8862c6763f42c1ebff28a522064fb09db71`. The kind and direction codes are illustrative; the vector pins only the format.
* **Request digest:** `c46bf7cbc1b93068`.
* **Attestation.**
  ```
  hasm-replay-attestation-v1
  creator=55555555-5555-4555-8555-555555555555|phone|1795650600
  approver=44444444-4444-4444-8444-444444444444|in-person|1795650900
  request=c46bf7cbc1b93068
  participants=3
  note=plan, document and totals confirmed
  ```
  It is 236 bytes, and its hash is `ddeb7f2e567e3ab2b14185617734d45e65f45d86566d80f22e46a74393e8fa15`.
* **Results.**

  | Message | Length | SHA-256 |
  |---|---|---|
  | Grant | 1408 bytes | `76e4fb700472e99a0135321ba28d96bd027ad9c3081844a2eaf5a16925b9464e` |
  | Approval (signer `4`, which is the approver; key `k1`; `signed-at=1795651200`) | 1151 bytes | `99c0c0360e8c3d0b6ca647eae112e52753bc1c583e972da536391a5423e2602e` |

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
* **Session settings.** The messages and texts use only the formats of 4.4. TimeZone, DateStyle, IntervalStyle, `bytea_output`, `extra_float_digits` and `lc_numeric` therefore cannot change them. There are no floats and no `to_char` with D or G. Every ORDER BY is on uuid, integer or boolean columns, never on text.
* **Test.** The messages and the epoch text are built in sessions with each of those settings changed, and must be byte-identical every time.

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

### 4.7 Content binding and the remaining bearer window (L3, N3)

**What the grant binds.**
* One plan, one plan revision, one approval and approver, one creator, and one event document revision.
* The closure, the payload, all seven fingerprints and the lines.
* **The exact epoch**: every cost entry, layer and consumption, with their amounts and running figures (4.4).
* One executor id, the attestation, one use and one time window.

**Where the epoch text comes from.** The epoch is deterministic given the pre-state, and the pre-state is fingerprinted. The epoch text is computed at export time by the executor's pure derivation (E1, section 13) from the planner result, and it is signed. At commit, the database rebuilds the text from the rows the execution actually wrote.

**Consequences.**
* An intercepted grant can, within its window, produce only the exact signed epoch, executed by the signed executor id. That is the authorized outcome, executed by someone else at a time of their choosing within the window.
* Cost cannot be moved between items, positions, entries or layers: every row is bound.
* Any change in the pre-state between export and execution makes the epoch differ, and the commit is refused. This fails closed: a new export and a new grant are needed.

**Compensation journals.** These are the general-ledger side, built in slice CI. Until CI, E3 refuses plans with lines (`compensation_not_integrated`, merged executor plan). CI must add a deferred database check: the multiset of (entry date, period, branch, account, debit, credit) of the journal lines carrying this plan's source key equals that of the signed plan lines. This is an activation condition for plans with lines (section 13).

**Exposure.** The grant reaches the database only inside the execution transaction. The executor uploads the grant file when executing, and the application inserts the grant, the execution and the grant use together. A grant is therefore never readable from the database before it is consumed. D4 limits its life to 2 hours for (i).

**Until the database-side binding exists**, meaning slice A together with E1's epoch text, activation is not allowed (section 14). The read-side check of section 9 nevertheless compares entry by entry against the signed epoch text, not only against the lines.

## 5. Resolution (i), RECOMMENDED: offline second-person execution grant

**Who.** A named person whom the owner registers as a replay signer (decision D2). The signer is a different person from the plan's creator, the approver and the executor, and from the event document's creator, editor and submitter. The database enforces this (4.3).

**Key.**
* The signer generates their own RSA-3072 key pair on their own offline machine with a small offline tool, `Hasm.ReplaySigner keygen`, which uses no network. The private key never leaves that machine.
* The public key file goes to the owner. The owner registers it in every company database with `Hasm.Admin register-replay-signer <public key file> <signer user id>`, as the owner account, and a system audit event is written. The owner adds the key to the API's pinned list.

**Participants list.** The owner gives each signer a versioned list of the user ids, logins and names of the people allowed to create, approve and execute replays, with a contact channel for each person that is not the application.
* The list is pinned in the offline tool's own configuration, outside the database.
* The tool refuses to sign a plan whose creator, approver or executor id is not on the list.
* Names and contacts come from this list, never from the export. The list's version goes into the attestation.
* Upkeep is decision D2.

**Flow per execution.**
1. The plan is created and approved in the application, as today. The approval is recorded but grants nothing by itself.
2. An operator runs `Hasm.Admin export-replay-plan <company> <plan> <executor>` with the owner account. It runs the planner and E1's derivation read-only, and writes a file with:
   * every field of 4.4 through `executor=`;
   * the closure positions;
   * the plan lines;
   * the full epoch text;
   * display data: codes and names of items, warehouses, accounts, periods and branches.
3. The file is carried to the signer's machine. The offline tool:
   * **recomputes from the displayed data alone, and refuses on any mismatch:**
     * the closure hash, from the listed positions, with the 0036 algorithm of `f_3cdca6ba`;
     * the lines hash, from the listed lines;
     * the epoch hash, from the listed epoch rows;
     * the request digest;
   * **displays:**
     * every id and code beside each name;
     * the database's names, marked "database data, not verified";
     * per position, the quantity and value before and after the epoch;
     * the debit and credit totals of the lines;
     * the request digest, grouped as 4×4 hex;
   * refuses if the creator, approver or executor is not on the participants list.
4. **Confirmation, enforced and recorded.** The signer contacts the creator AND the approver through the contact channels on the participants list, never through the application. Each must state, from their own knowledge, the plan id, the event document, the debit and credit totals, and the request digest they see in their own application session. The signer enters into the tool, for each person, the method (`in-person`, `phone` or `video`) and the time, plus a short note. The tool builds the attestation of 4.4.

   This is required because runtime credentials can mint sessions of real users (section 15), so a thief could make a forged plan appear to come from real colleagues. A colleague who never created or approved that plan, or who sees other content, does not confirm it.
5. On the signer's explicit confirmation, the tool signs with not-before = now − 5 minutes and not-after = not-before + the validity of D4 (2 hours). It writes a grant file that holds the grant fields, the attestation and the signature.
6. The executor uploads the grant file in the application when executing. The grant, the execution and the grant use are written in one transaction. The checks of 4.3 apply at insert and at commit, including the attestation and the epoch.
7. The execution's audit event 1154 carries the grant id, the attestation hash and the attestation text.

**Why a forged export does not help.**
* The database verifies the signature against the message it rebuilds from its own rows, and at commit against the epoch actually written. A file that misstates either yields a refusal.
* A plan forged INTO the database by the runtime credentials is shown to the signer as it actually is. The creator and approver named on it must confirm it from their own knowledge.

**Cost.** Each execution needs one offline step by a second person plus two confirmations. Back-dated replays are rare corrections. A second machine and per-person keys are required.

## 6. Resolution (ii): owner-registered in-app signers with per-signature TOTP

**Registry: one new system migration** (the lead allocates its number).
* **`ReplaySignerRegistration`:** registration id, user id, pinned TOTP secret id, registered at, revoked at (set once).
  * Only the system database owner writes it, through `Hasm.Admin register-replay-signer-totp <login>`, with a system audit event.
  * The system runtime account holds SELECT only. This is a new `SystemRuntimePrivileges` entry, refused at start of use like the D-235 TOTP gate.
* **`ReplaySignerAttempt`:** attempt id, registration id, at (set by the trigger to `clock_timestamp()`, never supplied by the client), operation (`approve` or `grant`, with the plan id). Append-only.
* **`ReplaySignerSuccess`:** attempt id (unique), MAC. Append-only.
* **Privileges on both logs:** the runtime account holds INSERT and SELECT. UPDATE, DELETE and TRUNCATE are refused by grants and by an owner-only trigger.
* **Configuration.** The explicit non-least-privilege opt-in of `HasmDatabaseOptions` (D-235) is refused while replay signing is enabled.

**Signing key.** One installation RSA key lives in `Hasm__Replay__SigningKeyDirectory`. The files are owner-only, like the backup signing key, and kept separate from the database credentials, the audit key and the key ring. Its public key is registered as in 4.1, with signer user id NULL. Each message names its registered signer.

**Two signatures, by two different registered signers.**
* The **approval signature** is by the approver, who must be a registered signer A, at approval time.
* The **execution grant** is by registered signer B, with B ≠ A, B not the creator, and B not the executor.

**Before B signs the grant (N2).** B performs the confirmation of section 5, step 4, with the creator AND the approver, through contact channels outside the application. The application shows B the same displays as the offline tool, with database names marked as database data, and builds the same attestation, which the database checks (4.3).

**Forged-provenance residual.** Plan creation is unsigned, so the runtime credentials can attribute a forged plan to a real creator. Approver A's own signature, and B's confirmation with the creator, are what stop it. A careless confirmation remains a residual (7.2).

### 6.1 Per-signature TOTP verification (N1)

Each signature requires the signer to type a fresh TOTP code for this operation. The signer component:
1. **Transaction 1:**
   * takes a transaction advisory lock keyed by the registration id. This serialises every attempt of that signer across all API instances;
   * reads the registration and requires it unrevoked;
   * counts that registration's **failed attempts**: attempt rows without a valid success record (step 4);
   * refuses, and alerts, when there are 10 or more failed attempts since the registration's `registered_at`. This is the cumulative cap: the registration is **suspended until the owner re-registers**, which creates a new registration;
   * refuses when there are 5 or more failed attempts in the last 15 minutes by the database clock;
   * inserts the attempt row and **commits before verifying**. Until a success record exists, the attempt counts as a failure. A cancelled request, a crash or a timeout therefore never removes an attempt.
2. Loads the pinned TOTP secret by its id. The material and identity are owner-only (D-235), the runtime account has no DELETE, and a newly inserted secret has a different id. Requires the secret's state to be Active.
3. Decrypts the secret with the key ring held outside the database, and verifies the code at the current step under the existing replay rule. The last accepted step strictly increases; the runtime credentials can only raise it, never lower it.
4. **On a match (transaction 2):** inserts `ReplaySignerSuccess` with MAC = HMAC-SHA-256 under a key derived from the key ring, over `hasm-replay-signer-success-v1|<attempt id>|<registration id>`. The runtime credentials cannot compute this MAC, so success rows they insert do not count, and failures cannot be erased. Then the component signs. A success never resets or reduces the failure count. Only a new owner registration starts a new count.
5. **On a mismatch:** writes a system audit event (replay signer failure) and raises an alert to the owner through the deployment's alert channel (D2). Reaching the cap raises a suspension alert.

**Guessing budget.** At most 10 failures per registration. With a ±1-step window (3 of 10^6 codes), the chance of guessing is at most 3×10^-5 per registration, and every failure alerts the owner. Concurrent bursts cannot exceed the limit: the lock serialises attempts, and committed attempt rows count before verification.

**Availability cost.** The runtime credentials can insert attempt rows. They can therefore suspend signers, which needs owner re-registration. This is detected and alerted, and is an availability residual only.

**Re-enrolment voids the registration.** A re-enrolled signer has a new secret id. The registration still pins the old id and stops working until the owner registers the signer again.

Session, roles, permissions and the session's MFA time are not inputs.

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
| Runtime credentials **+ sessions, users, roles and MFA times they mint** | As above. | **Prevented** unless a signer signs a forged plan. The participants list is pinned outside the database. The creator and approver must confirm from their own knowledge through channels outside the application. The attestation is signed, checked and audited. | The attestation names who confirmed, how and when. | **Procedural residual:** a signer who records a confirmation that did not happen, or confirmers who are deceived. This is attributable to the signer and the confirmers. |
| Runtime credentials **+ a legitimately issued grant file, intercepted within its window** | | Only the exact signed epoch of that plan, by the signed executor id, once, within 2 hours. Any other epoch is refused at commit. | The audit names the executor. | The authorized replay is executed by someone else, at a time the attacker chooses within the window. Its content is unchanged. |
| **Full compromise of the API process** | Yes. | **Prevented** for any plan without a grant: there is no signer key on the API host. For a grant delivered to the compromised process: as in the row above. | As above. | |
| A signer's offline machine and private key | | Without database access: nothing. With runtime credentials as well: equivalent to a malicious signer, one plan per grant, attributable. | Key revocation (4.6). | Collusion or coercion of a signer. |
| **Database owner or superuser** | Can write any rows and switch triggers off. | Can write an execution without a valid grant. | **Readers, the close probe and the restore check refuse it** (.NET verification against pinned keys, section 9). | Preventing the write (design T10). |

### 7.2 Resolution (ii): registered in-app signers with per-signature TOTP

| Attacker holds | Approve with signature / execute | Detected | Residual / out of scope |
|---|---|---|---|
| **Runtime credentials** | **Prevented, except for guessing (N1).** The registry is owner-written. The TOTP secret is pinned by id, and its plaintext needs the key ring outside the database. Guessing is serialised per signer and capped at 10 failures per registration. A guess succeeds with probability at most 3×10^-5 per registration, and two registered signers are needed. | Every failure is audited and alerted to the owner. | Availability: signers can be suspended by inserted attempt rows, a raised last step or a revoked TOTP state, and need owner re-registration. Unsigned creation, cancel, reject and stale (D6). |
| Runtime credentials **+ minted sessions, users, roles and MFA times** | **Prevented**, except for the guessing residual above. Session data is not an input. **Forged provenance (N2):** a forged plan can name a real creator. Approver A must sign it with their own code, and B must confirm it with the creator and the approver before signing the grant. | The attestation; the audit of both signatures. | **Procedural residual:** a careless approver A, together with a careless or false confirmation by B. |
| Runtime credentials **+ one registered signer's TOTP device or a phished code** | **Prevented.** A second, different registered signer is required. | The attempt log and the audit events. | Two registered signers colluding or compromised. |
| **Full compromise of the API process** | **NOT prevented.** The process holds the key ring and the signing key. | The signatures and audit events name the signers. | Prevention needs resolution (i). |
| Database owner or superuser | Can write unsigned rows. | Readers refuse them (section 9). | Preventing the write. |

## 8. What is preserved

* **Least privilege.** No new database role and no definer function. R's INSERT on the execution tables is conditional on a grant that R cannot produce. Keys and registrations are written by the owner.
* **External audit HMAC chain.** Unchanged; business audit events 1154 to 1159 are still required as today. Grants, attestations and signatures add publicly verifiable evidence. The audit detail carries the grant id and the attestation, and the signature is unique because its length is exact.
* **Segregation of duties.** Unchanged in the database (`f_17b9c93f`; the executor is not the creator) and extended to the signer (4.3). Resolution (ii) requires two different registered signers.
* **Fiscal periods.** Unchanged: the 0036 entry guard with `FOR SHARE` on the period, and the stock-period guard of 0034.
* **The owner's Q1 safeguards** (quoted verbatim, each followed by where this design keeps it):

  | Q1 safeguard (verbatim) | Where it is kept |
  |---|---|
  | "preserve all existing CHECKs, triggers and immutability; never relax or replace an existing safeguard" | Additions only; N17 is extended. |
  | "an entry-level guard refusing unauthorized back-dated movements" | 0036 is unchanged, and an execution now also needs its verified grant. |
  | "the replay exception only for an operation bound, in the same DB transaction, to the single execution of an approved plan" | Unchanged, plus the single grant use. |
  | "every replayed operation and entry bound to the plan's approved item/warehouse closure" | Unchanged, and the closure, the lines and every epoch entry are signed. |
  | "segregation of duties and Open fiscal periods enforced" | See the bullets above. |
  | "no unauthorized reversals of original, event, storno and rebook operations" | Unchanged (0036 X6). |
  | "transactional consistency and an auditable execution record" | Deferred checks, the grant, the attestation and the audit event. |
  | "never a client-controlled session flag or a general back-dating bypass" | There is no flag (section 10). |
  | "historical accounting records and traceability preserved" | Nothing is edited; every new table is append-only. |

## 9. Keeping unverified replay effects out of valid financial history (M4)

* **At write.** No execution commits without a grant that was verified at insert and re-verified at commit, with its epoch text matching the rows (4.3). Without an execution, no epoch row can commit.
* **At read, independent of the database's functions.** The snapshot readers, the close probe (slice P) and the restore check do their own verification:
  * they rebuild the grant message, the epoch text and the attestation of 4.4 in .NET from the rows;
  * they verify the RSA signature in .NET against the keys **pinned in the application configuration**, with their revoke-from;
  * they compare the rebuilt epoch text with the signed digest, which binds every entry, layer and consumption;
  * from CI on, they compare the compensation journal lines with the signed plan lines.

  An epoch is refused, and not treated as valid history, if any of these holds:
  * its grant does not verify;
  * its key is not pinned;
  * its acceptance time is not before the pinned revoke-from;
  * its epoch rows differ from the signed epoch text;
  * its journal lines differ from the signed lines.

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
* the built-ins `numeric`, `sha256`, `get_byte`, `length`, `mod`, `decode`, `encode`, `convert_to`, `to_char`, `string_agg`, `split_part`, `left` and `clock_timestamp`, and the allowed `pg_current_xact_id()`.

**What they avoid:** `SECURITY DEFINER`, `search_path`, `set_config`, dynamic SQL, extensions, other `pg_` names and backslash literals.

The per-signer advisory lock of 6.1 is taken by application code on the system database, not by a migration. The application already uses advisory locks, for example the company posting lock.

**No guard exception is needed.**

## 11. Migrations and provisioning

**Resolution (i).**
* One additive COMPANY migration: the tables `ReplaySignerKey`, `ReplayExecutionGrant` and `ReplayGrantUse`, the functions (including the epoch-text and attestation checks) and the new triggers. No system migration and no cluster role.
* Tools, run with the owner account and audited:
  * the offline `Hasm.ReplaySigner` (`keygen`, `show`, `sign`, and the participants list);
  * `Hasm.Admin register-replay-signer`, `revoke-replay-signer` and `export-replay-plan`.
* Registration runs for every company database. Provisioning runs it for a new company when keys are configured, and it runs again after a restore. A company without a registered key cannot execute: it fails closed.

**Resolution (ii).** The same company migration plus `ReplayApprovalSignature`, and one additive SYSTEM migration: `ReplaySignerRegistration`, `ReplaySignerAttempt`, `ReplaySignerSuccess` and the owner-only triggers. The `SystemRuntimePrivileges` entries are SELECT on the registry, and INSERT and SELECT on the two logs.

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
2. **(ii): the registry, TOTP and the attempt limit (N1).**
   * The system runtime account attempts each of the following:

     | Attempt | Expected |
     |---|---|
     | INSERT or UPDATE on `ReplaySignerRegistration` | 42501 |
     | INSERT a new TOTP secret for a registered signer | No signature: the registration still pins the old id. |
     | UPDATE the state or last step of the pinned secret | Denial only; no signature results. |
     | UPDATE the material or DELETE the pinned secret | Refused. |
     | DELETE or UPDATE attempt or success rows | Refused. |
     | INSERT forged success rows | Ignored: the MAC does not verify, and the failures still count. |
     | Re-enrol the signer | The registration is void. |

   * **Concurrent burst:** 50 parallel wrong codes for one signer allow at most 5 verifications in the window, and at most 10 in total.
   * **Cancellation:** cancelling a request after its attempt row is committed still counts as a failure.
   * **Cap:** the 11th attempt is refused even with the right code, and the registration stays suspended until the owner registers again.
   * **No reset:** a success between failures does not reset the count.
   * **Clock:** a client-supplied attempt time is ignored.
3. **Forged, missing and replayed grants.** Each of the following is refused:
   * a grant for another plan, another executor, another company (with the same key registered in both), another revision, another closure, other lines, another payload, other fingerprints or another document revision;
   * a second use of a grant;
   * a grant before not-before;
   * a grant after not-after, both at insert and at commit (a transaction kept open past not-after);
   * a closure or line row added after the grant within the same transaction.
4. **Epoch binding (N3).** With a valid grant, the commit is refused for each of these epoch deviations:
   * one entry's amount changed by 0.000001;
   * one quantity changed;
   * cost moved between two entries with equal totals;
   * cost moved between two positions with equal lines;
   * an entry with another sequence;
   * an extra entry, layer or consumption row;
   * a missing entry, layer or consumption row;
   * a layer with another source;
   * a consumption marked `restores`;
   * the pre-state changed by an ordinary posting between export and execution.
   * From CI on, journal lines that differ from the signed lines in any field.
5. **Attestation (N2).** Refused at insert when the attestation:
   * names another creator or approver;
   * uses an unknown method;
   * has a confirmation time outside the window;
   * has a wrong request digest;
   * has been altered after signing (the hash no longer matches).
6. **Keys.**
   * An unregistered key, a 2048-bit key, and the wrong exponent are refused.
   * A revoked key: a new signature is refused; a grant accepted before revoke-from still verifies; a grant accepted after a backdated revoke-from is refused by the readers.
   * A key row written by R or by the system account: 42501.
7. **Segregation of duties.** A grant is refused, even with a valid signature, if its signer is the plan's creator, the approver, the executor, or the event document's creator, editor or submitter. In (ii), the grant is refused if its signer equals the approval signer.
8. **Name resolution and settings (M1).**
   * R has no TEMPORARY and no CREATE on fresh, provisioned and restored databases.
   * Attempts to create a temporary table, type or function, or a public function or operator shadowing a built-in, fail.
   * The messages and the epoch text are byte-identical in sessions with TimeZone, DateStyle, IntervalStyle, `bytea_output`, `extra_float_digits` and `lc_numeric` changed.
9. **Signature primitives and vectors.** The known-answer vectors of 4.4 (lines, epoch, attestation, request digest, grant and approval messages) and the Wycheproof-style RSA vectors give the same results in SQL and in .NET.
10. **Read side (M4).** Readers, the close probe and the restore check each refuse:
    * an execution inserted by the owner with triggers switched off and an invalid or missing grant;
    * an execution accepted after the database function was replaced by one that returns true;
    * a key row replaced in the database;
    * an epoch whose rows differ from the signed epoch text.
11. **Mutation proofs.** Removing any one of the following makes a named test fail:
    * the length check;
    * the expiry check at commit;
    * the content re-verification at commit;
    * the epoch comparison at commit;
    * any single field of the `E`, `L` or `C` lines;
    * the attestation checks;
    * the signer independence check;
    * the single-use constraint;
    * the per-signer lock;
    * the pre-verification attempt commit;
    * the success MAC check;
    * the cumulative cap.

## 13. Effects on the merged executor plan (`docs/PHASE-3.8D-EXECUTOR-PLAN.md`, `22c9d23`)

**Does E3's posting shape allow per-entry binding? Yes, with two changes.** Checked against the merged plan:
* **Storno and rebook entries** are written by `EpochWriter` (E2b). Their content is fixed by the schedule: a storno mirrors its original, and a rebook carries the replayed amount. Both are tied to the original entry id by the storno's reverses link and by the lineage row (0036).
* **The event's entries** are written by the normal engine inside `PostInWorkAsync` (E2b, E3 step 6). They are a deterministic function of the pre-state and the payload. E1 must reproduce them, and E1's tests already require the schedule to reproduce `CostReplay`'s final state.
* **Sequences** are deterministic: "an entry takes the next sequence number of its position" (0036), and the position state is fingerprinted and re-checked by the re-plan (E3 step 3).
* **Entry ids and layer ids** are not deterministic (identity, `gen_random_uuid()`). The epoch text therefore never uses them, except the existing original entry ids.

**Change 1, E1.** `EpochSchedule` emits the full epoch text of 4.4: every storno, event and rebook entry with position, sequence, kind, direction, date, amounts and running figures, plus every layer and consumption. Today the merged E1 states only "the expected rebook amount per entry, the expected final position and layer state, and the lineage map". New tests check that E2b's writers and the real `PostInWorkAsync` produce rows whose rebuilt text equals E1's, for FIFO and average, one- and two-warehouse chains, and both directions with equal dates.

**Change 2, CI.** The compensation journal writer gains the deferred database check of 4.7: the journal lines equal the signed plan lines. Plans with lines stay refused (`compensation_not_integrated`) until then.

**New slice A, before E3.** It contains:
* the migrations of section 11;
* `rsa_verify`, the message builders and the epoch-text and attestation checks, in SQL and .NET;
* the read-side verifier;
* the tooling of the chosen resolution;
* the tests of section 12.

It stays inert while no key is registered. For (ii) it also contains the system migration and the signer component of 6.1.

**E3.**
* The executor accepts the grant file, writes the grant, the execution and `ReplayGrantUse` in one transaction, and fails closed without a grant.
* The rollback matrix gains "grant refused", "grant expired at commit", "attestation refused" and "epoch differs from the signed epoch".

**P.** The close probe and the readers use the .NET verifier (section 9).

**E5 and executor-plan section 7 are replaced by this design.** The runtime INSERT grant on the four execution tables returns only when all of these hold:
* slice A is merged, with E1's epoch text;
* for plans with lines, CI's journal binding;
* the first real caller exists;
* a revision of this design is approved;
* the owner has taken the decisions below.

## 14. Owner decisions needed before activation

* **D1 (activation baseline).** Proposed wording for the owner:

  > "Shall replay execution require, for each execution, a grant signed by a second, owner-registered person with a private key kept on a separate offline machine, after that person has confirmed the plan with its creator and its approver by a channel outside the application? This is resolution (i), recommended: stolen runtime database credentials, sessions minted with them, and a fully compromised application server cannot execute a replay without such a grant. Its residuals: someone who intercepts a grant file can run exactly that signed replay, unchanged, within its validity window; and the protection against a forged plan that names real colleagues depends on the confirmation procedure being followed honestly, which is recorded and attributable.
  >
  > Or do you accept owner-registered in-application signers, two different people, who each confirm with a fresh code from their own registered authenticator? This is resolution (ii): stolen runtime database credentials and minted sessions cannot execute a replay, but a fully compromised application server can. Its residuals: a holder of the database credentials can guess codes, bounded to 10 tries per signer before suspension, each try alerting you; forged attribution of a plan to a real creator is stopped only by the approver's signature and the second signer's confirmation.
  >
  > In both cases, replay execution is activated only once the database itself checks that the posted replay entries equal the signed ones."
* **D2 (signer registry and authentication).**
  * Who may be registered as a replay signer, and by whom. Recommended: the owner account through `Hasm.Admin`, audited.
  * The rule that a signer is never the creator, approver or executor of a plan they sign, nor the creator, editor or submitter of its document.
  * For (i): the participants list. The owner maintains it as a single versioned list: user id, login, name and an out-of-application contact for each person allowed to create, approve or execute. The owner delivers each new version to every signer in person or on signed media. Signers install it on their offline machines, and the tool refuses lists older than the newest version it has seen. A person who leaves is removed in a new version on the day they leave.
  * For (i) and (ii): the confirmation procedure of section 5, step 4, and the alert channel for (ii)'s failures and suspensions.
  * For (ii): the pinned authenticator, the rule that re-enrolment voids the registration, and owner re-registration after a suspension.
* **D3 (key custody).**
  * For (i): per-signer keys, generated and kept on each signer's offline machine, backed up by that signer under a rule the owner sets.
  * For (ii): the installation signing key in its own owner-only directory, separate from the database credentials, the audit key and the key ring.
  * For both: the rotation interval, and who may revoke a key.
* **D4 (grant validity).** Recommended: **2 hours for (i)** and 10 minutes for (ii). A grant is always single-use.

  Justification against N3: since the database binds the exact epoch, an intercepted grant can only run the authorized replay unchanged. The window now limits *when* that can happen, and how long a grant file is worth intercepting. Two hours covers carrying the file from the offline machine and executing it. A longer window also raises the chance that the pre-state changes, which fails closed and needs a new grant.
* **D5 (revocation after a suspected compromise).** When the owner backdates a key's revoke-from to the suspected time, replays accepted after it are refused as history. Decide what then happens to them. Recommended: they are treated as unauthorized and corrected by a new, properly granted plan under owner review.
* **D6 (unsigned steps).** Accept that plan creation, approval in (i), cancel, reject and stale stay unsigned. None of them changes financial history; the residual is availability, because forged proposed plans can block documents until cancelled. The alternative is to also require a grant for plan creation.
* **Activation condition (not a decision; stated for the owner).** Activation requires the database-side epoch binding (slice A with E1's epoch text) and, for plans with lines, CI's journal binding (4.7, section 13).
* **Not a decision of this design, but relevant to activation:** the separate register item on runtime-credential session minting (section 15).

## 15. Residual risks and the separate issue

* **Separate application-wide issue.** This was a side finding of C1. It is a register item handled by the lead and is not solved here. The system runtime credentials can create authenticated, MFA-satisfied sessions for any user: the token hash is a plain SHA-256, and the session's MFA time is writable.
  * This design takes no authorization decision from sessions.
  * The issue is why the creator and approver confirm from their own knowledge, by channels outside the application (5, step 4).
  * The rest of the application depends on sessions.
* **Residuals of (i).**
  * A malicious or coerced signer, or a false attestation: one plan per grant, attributable, revocable.
  * An intercepted grant within its window: only the exact signed epoch, once.
* **Residuals of (ii).**
  * Code guessing, bounded to 10 failures per registration, each alerted.
  * Forged creator attribution, stopped by the approver's signature and the second signer's confirmation.
  * Not protected against a fully compromised application process.
* **Security-critical code.** The verification code, about 25 lines of PL/pgSQL and their .NET twin, together with the message, epoch-text and attestation builders, needs the vectors and mutation proofs of section 12.
* **Key loss.** It blocks new grants but never reading, because verification needs only public keys. History accepted before a key's revoke-from remains verifiable.
