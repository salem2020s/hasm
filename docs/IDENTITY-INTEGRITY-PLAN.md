# Identity integrity plan (register item SEC-RT-SESSION): the runtime database credentials must not be able to mint sessions, identities or grants

**Status: PLAN ONLY (a proposal; nothing is built; no decision ID, audit code or migration number is allocated).** It answers the finding recorded by Agent OSEC during the review of PR 218 (T2), 2026-11-22, severity **High**, and it is **blocking** for T2 and for any control that relies on the application authenticating a person against someone who holds the database credentials. Implementation starts only after an independent review of this plan and the owner decisions of section 12. Everything here that is not a quoted owner decision is a proposal.

## 1. The problem, restated

The API authenticates a person from rows of the SYSTEM database, and every row that decides "who is this, is the second factor done, what may they do" is writable by the system runtime role (`grant-system-runtime`, `SystemRuntimePrivileges`). Nothing in those rows is bound to a secret that lives outside the database. Whoever holds the runtime credentials (leaked configuration or connection string, a database-side leak of the role's password) can, without any password, TOTP secret or key of the API host:

* **A. mint a session.** `INSERT INTO session` with `UserId` = a victim (an MFA-enrolled one included), `TokenHash` = the plain SHA-256 of a token the attacker chose (`SessionToken.Hash`), expiries ahead, and `VerifiedAt` = now: `Authorization: Bearer <token>` is accepted as the victim, the MFA-fresh gate passes;
* **B. mint an identity.** `INSERT INTO user` with a PBKDF2 hash of a chosen password, state active and `IsIdentityAdministrator = true`: a normal login succeeds as an identity administrator;
* **C. grant.** Insert or change `RoleAssignment`, `RolePermission`, `UserCompanyAccess`, `Role`: every permission in every company.

Also available through the same grants (not executed by the finding): overwrite a victim's password hash; move a victim's MFA state Enrolled to EnrolmentRequired (the state-graph trigger allows that edge) and re-enrol; take over an existing session row.

**What already holds and must stay:** the TOTP secret material (AES-GCM under the key ring, outside the database, plus the owner-only trigger of D-235 system migration 0035) and the HMAC chain of the audit streams (the actions are recorded, but attributed to the impersonated user).

**Threat model of this plan.** The adversary holds the runtime database credentials (SELECT, INSERT, UPDATE, DELETE on the rows below, as the catalog gives them today) and nothing else: not the key ring, not the API host, not the owner or migration account. A compromised API host (it holds the key ring and the runtime credentials) is OUT of scope: no database privilege helps there (the residual already recorded for D-235). A superuser or database-wide-read role bypasses grants and is reported by the check of D-235, not by this plan.

## 2. The principle

Every fact the API takes from a row to decide identity, second factor or authority is **authenticated with a key the database does not hold** (a MAC verified by the API), is **versioned** (a key id and a per-row revision) and **fails closed** (a missing key, a missing tag, a wrong tag or a stale revision is a refusal, never an acceptance, never a silent re-tag). The database stays the store; the API becomes the verifier. No legacy path: a row without a valid tag is not honoured.

## 3. Key custody

**Proposal:** three purpose-bound subkeys of the EXISTING protected key ring, derived exactly as the TOTP precedent derives its key (`KekHandle.DeriveSubkey(purpose)`, HKDF-SHA-256 over the key's wrapping key with the info label `hasm-purpose/v1/<purpose>/<keyId>`, which is also what `TotpSecretProtector` uses with the purpose `totp`):

| Purpose label | Used for | Mitigation |
|---|---|---|
| `identity-token` | the keyed hash of a session token (HMAC-SHA-256) | 1 |
| `identity-session` | the MAC over a session row's security fields | 2 |
| `identity-user` | the MAC over a user row and the anchors (section 5) | 3 |
| `identity-access` | the MACs over role, assignment, company-access and cost-centre rows | 4 |

(Four labels, so that a leak or a bug in one use never gives the others; the key id is the key ring's own key id.) One key ring, one directory, one backup procedure; no new secret store, no new operator duty beyond what the ring already has.

**Does this fit an existing owner decision?** Partly, and the gap must be closed by the owner:
* D-080 (owner, 2026-11-21), item (2): "TOTP secrets are protected by the existing protected key ring, with secure rotation and access restrictions." It names TOTP secrets. Using the same ring for MACs of sessions, users and access rows is the same custody and the same protection, but it is a NEW USE of the ring that the text does not name.
* D-087 (owner, 2026-11-21): "A `backup-keys` operation exports the protected key directory encrypted to the offline recovery public key, to an independently protected destination separate from the normal data backup root. Key-directory recovery is part of the disaster-recovery tests." This covers the recoverability of every key of the ring, so subkeys derived from it are recoverable with the ring.
* D-093 (lead): key ids, rotation and versioning for the backup keys; "no key is retired or deleted here (retirement needs independent approval, D-080(6))". D-080(6) (owner): "Independent approval is required for sensitive role grants, encryption-key retirement and restore operations."
* **Therefore this plan needs ONE owner decision (OD-1)**: extend the protected key ring to authenticate identity, session and access rows (purpose-bound subkeys), with retirement of such a key under D-080(6). The alternative, a separate owner-only key directory, is listed in section 12 (OD-1b) and is worse: a second custody procedure to back up, rotate and recover.

**Fail closed on a missing key.** If the ring has no key for a row's key id (or no active key), every operation that needs a verification answers 503 `service_unavailable` (the same answer as the other encryption-unavailable conditions), never 401 (which would invite a retry loop) and never acceptance. No key is ever generated implicitly (the existing `FileKekRing` rule).

## 4. Mitigation 1 and 2: the session

### 4.1 Keyed token hash (mitigation 1)
`TokenHash = HMAC-SHA256(K_identity-token(keyId), token)` instead of the plain SHA-256. The token format (43 base64url characters) is unchanged. A session row gains `KeyId`. Lookup computes the HMAC under each key the ring accepts for new-and-grace use (the active key and, during a rotation, the previous one: at most two values, `WHERE token_hash IN (@a, @b)`), so rotation needs no second table. The unique index on `TokenHash` stays. **No legacy plain hash is ever accepted**: a plain SHA-256 value cannot match an HMAC value, so every existing session simply stops matching at rollout (forced re-login, section 9). This alone stops an attacker from minting a session with a token he chose (vector A step 1); it does not stop the takeover of an existing row, which mitigation 2 closes.

### 4.2 MAC over the session's security fields (mitigation 2)
`SessionTag = HMAC-SHA256(K_identity-session(keyId), "hasm.session.v1" | length-prefixed fields)` over exactly:

| Covered | Column (logical) |
|---|---|
| row identity | `Id` |
| who | `UserId` |
| the credential | `TokenHash` (the keyed value) |
| client | `ClientKind` |
| second factor | MFA `VerifiedAtUtc` (null encoded explicitly), `AuthMethods`, `Emergency` |
| company context | `SelectedCompanyId` (null encoded explicitly) |
| lifetime | `AbsoluteExpiresAtUtc` |
| revocation | `RevokedAtUtc` (null encoded explicitly) and `RevokedReason` |
| the user row it was issued under | the user's security revision at issue (section 5) |
| key | `KeyId` |

**Not covered, deliberately:** `LastSeenAtUtc` and `IdleExpiresAtUtc` (they change on every request; the API recomputes the idle expiry itself and never extends beyond the covered absolute expiry, so changing them only stretches an existing, otherwise valid session within its absolute limit: an accepted residual, OD-5), `CreatedAtUtc` (informational). A session row also has `Tag` (new column).

The tag is **verified on every lookup** (`FindActiveSessionAsync`) and **recomputed by the API on every legitimate change**. The change sites in the current code, each of which must recompute: `CreateSessionAsync` (login, issue), the second-factor proof (`MfaStore.RecordStepUpAsync`: VerifiedAt, AuthMethods), the three company-selection writes (`AccessService` x2, `MfaPolicyService.SelectCompanyAsync`: SelectedCompanyId), and revocation (`RevokeSessionAsync`, `RevokeUserSessionsAsync`: RevokedAt, RevokedReason). Each such statement becomes a compare-and-swap on the old tag (`UPDATE ... SET ..., tag = @new WHERE id = @id AND tag = @old`), so a concurrent change or a tampered row cannot be silently re-blessed. Sliding the idle window (`TouchSessionAsync`) does not touch the tag.

Residual that remains by design: a thief can still REVOKE or DELETE a session (denial of service on one session), and can read the table (token hashes are keyed, so reading gives no usable token).

## 5. Mitigation 3: the user row

`UserTag = HMAC-SHA256(K_identity-user(keyId), "hasm.user.v1" | fields)` over exactly: `Id`, `Login`, `PasswordHash`, `State`, `IsIdentityAdministrator`, MFA `State`, and a new per-user `SecurityRevision` (bigint, +1 on every change of a covered field), plus `KeyId`. **Not covered:** `DisplayName` (no authority), `CreatedAtUtc`, `StateChangedAtUtc`, `PasswordChangedAtUtc`, `LastLoginAtUtc`, the enrolment-code columns (hash, expiry, consumed time: they only matter while the covered MFA state is EnrolmentRequired, and the enrolment path additionally needs the user's own password or the emergency procedure). The TOTP rows are already authenticated (AES-GCM; its AAD binds user id, key id and secret id: to be re-verified at implementation, step S-2).

Checked **at login** (before the password is even compared: a user row without a valid tag cannot log in, and the failure is the same neutral answer as a wrong password, with an audit event for the operator) and **on every request** (the session lookup joins the user; the session tag carries the user's revision at issue, and the lookup refuses when the current user row has a LOWER revision than the session was issued under, or an invalid tag). Recomputed at the change sites: user creation, password change, state change (disable, lock), the MFA state edges (`MfaStore`: begin enrolment, activation, revoke, restore handling), `IsIdentityAdministrator` changes (offline commands).

**The replay problem and the anchor (OD-4).** A per-user revision alone does not stop REPLAY: an attacker who captured an old valid row (he can SELECT) can write it back with its old valid tag and revision, e.g. the old MFA state `EnrolmentRequired`, or `IsIdentityAdministrator = true` of a person who was an administrator once. The revision only means something against a record the attacker cannot rewind. **Proposal:** a small append-only table `IdentityAnchor` (new; runtime INSERT and SELECT only, never UPDATE, DELETE or TRUNCATE, enforced by a guard trigger like every append-only table) holding `(Kind, SubjectId, Revision, KeyId, Tag)`, one row per security change, tagged with the same subkey. The verifier requires `row.Revision >= max(anchor.Revision)` for the subject and a valid anchor tag; the attacker can neither delete a newer anchor (no DELETE) nor forge one (no key) nor lower the maximum. Cost: one extra INSERT per security change and one indexed `max` per login (per request it uses the revision carried by the session tag). **If the owner does not want the anchor table, the replay of a previously valid tagged user row stays an accepted, documented residual** (OD-4); every other attack of this plan is closed without it.

## 6. Mitigation 4: the authorization rows

Per-row MACs with a revision on the rows that decide what a person may do. The same anchor table protects against deletion-and-resurrection and rollback; without it the residual is the same as in section 5.

| Table | Covered columns | Excluded | Notes |
|---|---|---|---|
| Role | `Id`, `CompanyId`, `PresetKey`, `Name`, `IsActive`, `Revision`, and a **digest of its permission set** (SHA-256 over the sorted permission codes) | `CreatedAtUtc`, `CreatedBy`, `UpdatedAtUtc` | the digest covers RolePermission, so an inserted, changed or resurrected permission row makes the role fail (a permission added to a role by SQL is not honoured) |
| RolePermission | none per row | | protected through the role digest; no per-row tag, no new column |
| RoleAssignment | `Id`, `UserId`, `CompanyId`, `RoleId`, `ScopeKind`, `ScopeId`, `ScopeEpoch`, `WarehouseScopeEpoch`, `RevokedAtUtc`, `Revision`, `KeyId` | `GrantedAtUtc`, `GrantedBy`, `RevokedBy` (history, attribution) | a revocation is a covered change (an attacker cannot un-revoke) |
| UserCompanyAccess | `UserId`, `CompanyId`, `RevokedAtUtc`, `Revision`, `KeyId` | `GrantedAtUtc`, `GrantedBy`, `RevokedBy` | the membership |
| CostCenterAssignment | its scope columns and `RevokedAtUtc` (exact list at S-3) with `Revision` | history columns | not in the brief; it narrows access, and the same attack applies |

The preset roles and permissions are seeded by migrations (which cannot hold a key): they are tagged by the owner command of section 7, and a preset change in a later migration needs the owner re-tag of that role (documented in the migration guard notes). `PermissionCatalog` evaluation keeps reading the rows on every call; the added work is one HMAC per row read (microseconds) and, with the anchor, one aggregated join. A row that fails verification is **skipped as if absent** for the permission computation AND counted: the request that depended on it answers 403, and an audit event with the table and row id is written once per minute per row (so an operator sees the attack without a flood). A missing KEY is the 503 of section 3, not a skip.

## 7. Existing data: the one-time owner tagging

A migration cannot tag rows (no key, no safe judgement). The system migration only adds NULLable columns and the anchor table; every row without a valid tag is **not honoured** from the moment the code that verifies it is deployed. Tagging is an explicit, owner-account, two-step operator procedure, because blind tagging would bless a row an attacker planted earlier:

1. `Hasm.Admin identity-audit-report` (read-only): lists every user, every administrator, every company membership, every role, assignment and cost-centre assignment, and checks each against the authenticated audit streams (the HMAC-chained system audit stream and the access audit projection: the events `UserCreated`, the access events `RoleAssigned`, `RoleAssignmentRevoked`, role changes, membership grants). It prints: matched rows; **orphans** (a row with no audit event that explains it); and administrators. Codes and counts only, plus the ids an operator needs (no secrets). It verifies the audit chain first (`audit-verify`); a broken chain refuses the report.
2. The operator (a named person, recorded) resolves every orphan: revoke or remove it, or accept it with a recorded reason (an audit event of the owner account). Then `Hasm.Admin identity-tag --reviewed <report-hash>` tags the rows with the active keys, refuses to run when the report hash is not the hash of a report produced from the current table state minus resolved orphans, and writes one audit event per table with counts.

A fresh installation has no existing rows: `bootstrap-admin` and the services create tagged rows from the start.

## 8. Rotation, retirement, restore

* **Rotation with a grace period.** New writes use the active key id. Verification accepts the active key and, for a configured grace period (proposal: the absolute session lifetime for sessions, 12 hours by default, and "until the re-tag command has finished" for user and access rows), the previous key. `Hasm.Admin identity-retag` (owner account) re-tags rows under the active key; it is idempotent and prints counts per key id. A key is retired only when `identity-verify` shows ZERO protected rows under it, with the independent approval of D-080(6) (OD-1).
* **Missing key:** section 3 (503, fail closed).
* **Restored system database.** Sessions: a restore revokes every session (the restore handling already revokes second-factor state; this adds the sessions) and a restored database accepts no old session. Users, access rows and anchors verify with the restored key ring: if the ring is the installation's own, everything verifies and nothing is lost; if it is a DIFFERENT installation's ring, every row fails verification and the owner must run section 7 again. The secret-store dump rules of D-091 are unchanged (the TOTP rows are handled there).
* **Backups of the key ring** are D-087's `backup-keys`; recovery of the ring is part of the disaster-recovery tests the owner already requires, and gets one added case: after a ring recovery a user can sign in.

## 9. Forced re-login (impact)

At the rollout of mitigation 1 every existing session is invalid (no keyed hash matches): **every signed-in user, Web and Desktop, is signed out once** and signs in again (with the second factor where required). The refusal is the ordinary 401 of an unknown session; the screens already handle it (and D-236 keeps unsaved input in memory across an expiry only while the shell is mounted, which a forced sign-out after a deployment is not). There is no dark mode (a mode that accepts untagged rows would be the vulnerability itself). The deployment order is therefore one maintenance window: the migration, the verifying code and the section 7 commands together; then the operator report and the tagging; then reopening. Until a row is tagged it is not honoured. OD-2 asks the owner to accept the maintenance window and the one-time sign-out.

## 10. The migration (number unallocated)

One additive SYSTEM migration (generated like the other system migrations, `tools/schema` generator and dictionary), in the first implementation PR: NULLable columns `KeyId`/`Tag` on `Session`; `SecurityRevision`, `KeyId`, `Tag` on `User`; `Revision`, `KeyId`, `Tag` and the permission digest on `Role`; `Revision`, `KeyId`, `Tag` on `RoleAssignment`, `UserCompanyAccess`, `CostCenterAssignment`; the append-only table `IdentityAnchor` with its guard trigger and index. No CREATE OR REPLACE, no DISABLE TRIGGER (the constructs the guard of PR 188 refuses), nothing seeded. `SystemRuntimePrivileges` gains the anchor table (SELECT, INSERT) and no other privilege changes (the tag columns are written through the existing UPDATE grants; this plan does NOT narrow them, because the point is that a write without the key is not honoured, not that it is impossible). Database-side the migration cannot verify a MAC; it can enforce what needs no key: the anchor append-only rule and that a revision never decreases (a guard trigger on the user and access tables: `NEW.Revision >= OLD.Revision`), which already stops the crudest rollback.

## 11. Slices (each about 1 to 2 days of work, each its own PR with independent review)

| Slice | Content | Closes |
|---|---|---|
| S-1 | The four key purposes and the MAC primitives with golden-value unit tests and key-id handling (`IdentityKeys`); the migration (all columns and the anchor table); keyed token hash; session tag with compare-and-swap at all session change sites; fail-closed 503; adversarial tests 1, 2 and the session part of 5, 6 | vector A completely |
| S-2 | User tag and `SecurityRevision` at all user change sites; verification at login and per request; the anchor for users; `identity-audit-report` and `identity-tag` for users; tests 3 and the user part of 5, 6 | vector B, password overwrite, MFA state rewind (with the anchor) |
| S-3 | Authorization rows: role digest, assignment, company access, cost-centre tags; verification in the permission computation; anchor for them; the report and tagging for access rows; test 4 and the access part of 5, 6 | vector C |
| S-4 | Rotation: `identity-retag`, `identity-verify`, grace period, restore handling (sessions revoked, wrong ring detected), documentation, disaster-recovery case; test 6 completed | rotation and recovery |
| S-5 | Hardening and the full adversarial matrix on the real API and the real restricted role; mutation proofs; review fixes | the proof of 1 to 4 together |

(S-1 and S-2 can be reviewed together if the reviewer prefers; S-1 alone already closes the reproduced session attack.)

## 12. Tests (isolated PostgreSQL, the REAL restricted runtime role created by the real grant command, the REAL API through the web application factory with the gate on)

1. A session inserted with a chosen token (a plain SHA-256 value, a value under a wrong key): 401; the same row with the keyed value computed WITHOUT the key is impossible, so the test uses an attacker's own key: 401.
2. Changing an existing session's `UserId`, MFA `VerifiedAt`, `AuthMethods`, `Emergency`, `SelectedCompanyId`, absolute expiry or revocation: 401 or the selected company ignored; one case per column.
3. A user inserted with a known hash and the administrator flag: login refused (neutral answer); a victim's `PasswordHash` overwritten, or `State`, or `IsIdentityAdministrator`, or MFA state moved Enrolled to EnrolmentRequired: login refused and the second factor still required; the replay of an old tagged row refused WITH the anchor (or recorded as the residual without it).
4. A role assignment, role permission, company access or role inserted, changed, revoked-then-restored or resurrected by SQL: not honoured (403) and reported; a permission added to a role by SQL does not appear in the effective set.
5. Every legitimate flow still works under the restricted role: login, MFA verify, step-up, company selection, password change (other sessions revoked), assignment and revocation through the API, role and permission administration, membership grant, logout, housekeeping purge, emergency access, enrolment and recovery.
6. Key rotation: rows under the old key accepted during the grace period, refused after retirement; `identity-retag` converges and is idempotent; a missing key answers 503 and accepts nothing; a restored system database accepts no old session and verifies users and access rows with the restored ring (and refuses with a different ring).
7. Mutation proofs: removing each tag check (token key, session tag, user tag, role digest, assignment tag, company-access tag, anchor comparison, fail-closed on a missing key) fails a named test of 1 to 6; one proof per check, run through the CPU gate, recorded in the PR.

Also unit: golden values per MAC (so a silent change of the covered field set fails), field-order and length-prefix ambiguity tests, a key-id test, a constant-time comparison test.

## 13. Owner decisions (precisely; none is implied by this plan)

* **OD-1 Key custody.** Extend D-080(2) (the protected key ring protects TOTP secrets) to authenticate session, user and access rows with purpose-bound subkeys of the same ring (`identity-token`, `identity-session`, `identity-user`, `identity-access`), recoverable through D-087, retired only under D-080(6). Alternative OD-1b: a separate owner-only key directory (a second custody procedure; not recommended).
* **OD-2 Rollout.** Accept a maintenance window and a one-time sign-out of every user at the rollout (no dark mode that would accept untagged rows).
* **OD-3 One-time tagging.** Who performs and signs the review of the `identity-audit-report` orphans (a named operator, recorded); that unmatched rows are revoked or accepted only with a recorded reason; that tagging is an owner-account command.
* **OD-4 Replay.** Adopt the `IdentityAnchor` table (closes replay and resurrection) or accept the documented residual that a previously valid tagged row can be written back.
* **OD-5 Residuals.** Accept: idle expiry and last-seen stay outside the session tag (a thief can only stretch an existing valid session within its absolute limit); a thief can revoke or delete sessions (denial of service); a compromised API host (key ring holder) is out of scope; a superuser bypasses everything.
* **OD-6 Grace period.** The rotation grace period (proposal: the absolute session lifetime for sessions, until the re-tag completes for rows).
* **OD-7 Scope.** `CostCenterAssignment` included with the four tables of the brief (proposal: yes); no narrowing of the runtime role's UPDATE grants in this plan (proposal: no, a separate hardening later).
* **OD-8 Failure answer.** A missing key answers 503 and a failed verification a neutral 401/403 with an audit event (proposal), rather than any answer that tells an attacker which check failed.

## 14. Interaction with other work

T2 (PR 218 C1) and every control that depends on "the application authenticates a person" waits for S-1 at least (vector A) and for S-2 (vector B) before it can be called safe against a holder of the runtime credentials. D-235 (TOTP least privilege) is unchanged and is a precondition this plan relies on. The migration guard of PR 188 is respected (no constructs it refuses). The restore flow (D-106, D-107) gains the session revocation of section 8.
