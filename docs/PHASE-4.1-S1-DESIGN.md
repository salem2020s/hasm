# Phase 4.1 slice S1 - schema design (WORK IN PROGRESS, no migration number taken)

Status: working note on the `feature/phase-4-1-s0` branch; not a PR and not reviewed. It implements section 4 of `docs/PHASE-4.1.md` (PR #113) and changes nothing until the slice migration (**M-A**) receives its system number at merge time. Physical identifiers (`t_xxxxxxxx`, `c_xxxxxxxx`, `k_...`, `f_...`, `r_...`) are DERIVED from a seed that includes the migration number (as in the party-registry derivation tests), so they cannot be generated, and `SystemSchema` constants and the derivation test cannot be written, until the number is assigned. The logical names below are the keys that will go into `SystemSchema.Map`.

## 1. `Users` additions (backed up: the user row is in the system dump)
| Logical column | Type | Meaning |
|---|---|---|
| `User.MfaState` | smallint NOT NULL DEFAULT 0 | 0 `None`, 1 `EnrolmentRequired`, 2 `Enrolled`, 3 `RecoveryRequired` |
| `User.MfaStateChangedAtUtc` | timestamptz NULL | last transition (audit cross-check) |
| `User.MfaEnrolmentCodeHash` | bytea NULL | SHA-256 of the one-time enrolment code of a `bootstrap-admin` administrator (all other codes live in the approval request under its MAC) |
| `User.MfaEnrolmentCodeExpiresAtUtc` | timestamptz NULL | at most 24 hours after issue |
| `User.MfaEnrolmentCodeConsumedAtUtc` | timestamptz NULL | single use |

Constraints: `CheckMfaState` (`MfaState BETWEEN 0 AND 3`); `CheckMfaEnrolmentCode` (hash NULL or 32 bytes; expiry NOT NULL when a hash is present; consumed only with a hash). A NEW trigger on the user row (generated into the S1 migration, no DROP) enforces the graph in the database (review M1): `None -> EnrolmentRequired | Enrolled`, `EnrolmentRequired -> Enrolled`, `Enrolled -> EnrolmentRequired | RecoveryRequired`, `RecoveryRequired -> EnrolmentRequired`; nothing returns to `None`. `MfaStateGraph` is its C# twin and a PostgreSQL test pins them equal over all 16 combinations. (The earlier plan to extend the existing guard is superseded.) The existing user guard trigger is extended so that `MfaState`, the code columns and the transition time change only through the service (same guard family as the state column); the service never moves a user from `Enrolled` or `RecoveryRequired` to `None` (a transition test pins the allowed graph: `None -> EnrolmentRequired -> Enrolled`, `Enrolled -> EnrolmentRequired` only by recovery or DR, `Enrolled -> RecoveryRequired` only by the restore procedure, `RecoveryRequired -> EnrolmentRequired` only by recovery or DR).
Existing rows are `None`: the requirement (privileged, company policy) is computed (slice S3), and S3 moves a user who becomes subject to it to `EnrolmentRequired`. S1 itself changes no behaviour.

## 2. `user_totp` (logical `UserTotp`): secret store, per-table backup rule "included only in encrypted runs"
| Logical column | Type | Notes |
|---|---|---|
| `UserTotp.SecretId` | uuid PK | AAD component |
| `UserTotp.UserId` | uuid NOT NULL, FK Users | AAD component |
| `UserTotp.Cipher` | bytea NOT NULL | length = 20 (the secret size) |
| `UserTotp.Nonce` | bytea NOT NULL | 12 bytes |
| `UserTotp.Tag` | bytea NOT NULL | 16 bytes |
| `UserTotp.KekId` | text NOT NULL | pattern of `KekRing.IsValidKeyId`; AAD component |
| `UserTotp.State` | smallint NOT NULL | 0 `Pending`, 1 `Active`, 2 `Revoked` |
| `UserTotp.CreatedAtUtc` | timestamptz NOT NULL | |
| `UserTotp.ConfirmedAtUtc` | timestamptz NULL | NOT NULL iff the row was ever `Active` |
| `UserTotp.LastStep` | bigint NULL | replay CAS; only increases |
| `UserTotp.FailedConfirmations` | integer NOT NULL DEFAULT 0 | enrolment confirmation attempts |

Constraints and indexes: `PkUserTotp` (SecretId); `UqUserTotpActive` unique on (UserId) WHERE State = 1; `UqUserTotpPending` unique on (UserId) WHERE State = 0 (review M2); `CkUserTotpState` (0..2); `CkUserTotpLengths` (cipher 20, nonce 12, tag 16); `CkUserTotpConfirmed` (State = 1 or 2 with `ConfirmedAtUtc` NOT NULL for Active). A guard trigger forbids changing `UserId`, `Cipher`, `Nonce`, `Tag`, `KekId` and `SecretId` except by the rewrap procedure (a transaction-local setting the rewrap path sets, like the other guarded tables) and forbids decreasing `LastStep`; rows are never deleted by the runtime role (revocation is `State = 2`).
The replay compare-and-set used by `MfaVerifier` is exactly `UPDATE UserTotp SET LastStep = @s WHERE SecretId = @id AND State = 1 AND (LastStep IS NULL OR LastStep < @s)` with one affected row required.
**Privileges:** the runtime role gets SELECT, INSERT and UPDATE (no DELETE); reporting roles get nothing on this table.
**Backup rule (LEAD D-120, supersedes the earlier sequencing question):** S1 ships with `UserTotp.Table` in `SystemSchema.SecretStoreTables` AND in `BackupRunService.SystemDumpExclusions` (label `user_totp`), i.e. ALWAYS excluded; it does not wait for #116. The table becomes "included only in encrypted runs" (a per-table rule: `Session.Table` and `LoginThrottle.Table` stay always excluded) in the Phase 4.3 slice 5 implementation PR, the code that actually encrypts runs. Until then every system restore leaves `Enrolled` users `RecoveryRequired`, which the H1 safeguard already handles.

## 3. Throttle counters (LEAD D-121)
No schema change: the second-factor counters reuse the login throttle table under scope `Login` with the domain-separated keys `mfa-pair:` and `mfa-user:` (`ThrottleKeyDomains`, prefix-free, pinned by a unit test). Migrations may not DROP, so the scope check is not widened; relaxing that loader rule belongs to the owner. MFA and password counters never count toward or reset each other, and the MFA bounds (5, 100, 15 minutes) are independent of `AuthOptions`.

## 4. Store seams (code, no schema)
* `IMfaSecretStore` (implemented over `UserTotp` and `TotpSecretProtector`): `FindActiveSecretAsync` reads the Active row, decrypts, returns `MfaSecret`; a missing key or authentication failure surfaces as `MfaUnavailableException` (no downgrade). `TryAdvanceLastStepAsync` is the compare-and-set above.
* `IMfaAttemptLimiter` (implemented over `LoginThrottle` with scope `Login` and the `mfa-` key domains, bounds `MfaThrottleOptions`: 5 per user and source, 100 per user, 15 minutes window and block); the per-user bound crossing is reported for the alert.
* `totp-rewrap` / `--check` (admin tool, authorization per the table in plan section 5): batches of rows inside transactions, resumable, idempotent; `--check` lists the key ids still referenced.
* Restore procedure hooks (plan section 4, D-106/D-107): after a system restore, every `Enrolled` user without an Active `UserTotp` row (or whose secret cannot be read) becomes `RecoveryRequired`, unconsumed enrolment codes are invalidated, and logins stay blocked until the audited completion step.

## 5. Tests planned for S1
Schema derivation and uniqueness tests (once names exist); constraint tests for each CHECK and unique index (second Active and second Pending row refused; wrong lengths refused); guard-trigger tests (immutable columns, `LastStep` cannot decrease, no DELETE for the runtime role); the replay CAS under concurrency against PostgreSQL (exactly one of N wins); the user-row MFA state graph; the backup-rule pin; `totp-rewrap` idempotence and resumption; restore tests that never decrypt `UserTotp` rows (state, counts and the `RecoveryRequired` transitions only; D-107).

## 6. Open points (for the lead, none blocks WIP)
* The migration number (assigned at merge) and therefore the derived names.
* (Resolved by D-120: S1 does not wait for #116.)
