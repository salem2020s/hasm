# HASM — Development Guide

Scope of the current foundation: Backend health endpoint, shared React frontend (Arabic RTL / English LTR), the Tauri 2 Windows shell, (Phase 2.1) the system database foundation with a minimal company registry, (Phase 2.3A) central identity with local authentication, and (Phase 2.3B) company access with role-based access control. No business modules exist yet.

## Prerequisites
- .NET SDK 10 (`dotnet --list-sdks`)
- Node.js 24 + npm 11
- Rust stable (`x86_64-pc-windows-msvc`) + Visual Studio Build Tools with "Desktop development with C++"; WebView2 (included in Windows 11)
- Tauri 2 is a project dependency (`src/desktop/package.json`); nothing is installed globally.
- PostgreSQL 14+ is required only to initialize the system database and to run the PostgreSQL integration tests. Install it yourself; the repository never installs it.

## Structure
```
assets/branding/                Official branding assets, see BRANDING.md (never modify or regenerate)
src/backend/                    Hasm.slnx, Hasm.Api (ASP.NET Core), Hasm.Infrastructure (PostgreSQL, migrations), Hasm.Admin (admin CLI)
src/frontend/                   npm workspace: apps/web, packages/ui, packages/api-client
src/desktop/                    Tauri 2 shell (src-tauri) loading the shared frontend
tests/unit/                     Hasm.Infrastructure.Tests (no database needed)
tests/integration/              Hasm.Api.Tests (WebApplicationFactory), Hasm.Database.IntegrationTests (real PostgreSQL)
```
Clients talk only to the Backend API over HTTPS; neither the web app nor the desktop shell can reach PostgreSQL.

## Run locally
One-time: `dotnet dev-certs https --trust` (the dev certificate must be trusted; certificate validation is never bypassed).

**Backend** (https://localhost:7241):
```
dotnet run --project src/backend/Hasm.Api --launch-profile https
```
Health: `GET https://localhost:7241/api/v1/health`

**Web** (http://localhost:5173):
```
cd src/frontend
npm install
cp apps/web/.env.example apps/web/.env.local   # optional; sets VITE_API_BASE_URL
npm run dev
```

**Desktop** (starts the Vite dev server itself):
```
cd src/desktop
npm install
npm run dev
```

## Configuration
- Frontend: `VITE_API_BASE_URL` (see `apps/web/.env.example`). Public by nature; never put secrets in `VITE_*` variables.
- Backend: `Cors:AllowedOrigins` in configuration. Empty by default (deny all); `appsettings.Development.json` allows the local Vite and Tauri origins. Set production origins through environment variables (`Cors__AllowedOrigins__0=...`) or an untracked config file.
- Desktop CSP (`src/desktop/src-tauri/tauri.conf.json`) `connect-src` lists the API origin; update it for the deployment's API address.

## System database (Phase 2.1)
Logical `Hasm_System` is one central PostgreSQL database; its **physical name is configurable and must be opaque** (lowercase `[a-z][a-z0-9_]*`, max 63 chars, not `pg_`-prefixed, e.g. `d` + random hex). Each company later gets its own database named by a generated opaque name (`d` + 32 hex chars from a CSPRNG), stored in the registry and never regenerated.

**Configuration** (environment variables only; never commit values):
| Variable | Purpose |
|---|---|
| `Hasm__Database__Host`, `Hasm__Database__Port` (5432) | server |
| `Hasm__Database__SystemDatabaseName` | physical system DB name |
| `Hasm__Database__Username` / `Password` | runtime account used by the Backend (least privilege) |
| `Hasm__Database__AdminUsername` / `AdminPassword` | privileged account, used only by `Hasm.Admin` |
| `Hasm__Database__MaintenanceDatabase` (`postgres`) | DB used to create the system DB |
| `Hasm__Database__SslMode` (`VerifyFull`) | Npgsql SslMode; only `VerifyCA`/`VerifyFull` accepted by default |
| `Hasm__Database__SslRootCertificate` | optional CA certificate file (PEM/PFX) when the server uses a private CA |
| `Hasm__Database__AllowInsecureTransport` (`false`) | explicit opt-in to non-validating modes; disposable test environments only |

Missing or invalid settings fail with a message naming the setting, never its value. Connection strings are built in memory only and are never logged or returned by the API.

**Initialize (explicit, administrative; the API never does this at startup):**
```
$env:Hasm__Database__Host="localhost"; $env:Hasm__Database__SystemDatabaseName="d<random>"
$env:Hasm__Database__AdminUsername="..."; $env:Hasm__Database__AdminPassword="..."
dotnet run --project src/backend/Hasm.Admin -- init-system-db
```
Creates the database only if absent (never drops/alters an existing one), then applies pending migrations. Safe to repeat. Exit codes: 0 ok, 1 failure, 2 usage, 3 bad configuration. Company databases are created by `provision-company` (Phase 2.2). The runtime account's privileges on the registry table must be granted by the DBA (not automated yet).

**Migrations:** forward-only versioned SQL scripts `NNNN_name.sql` embedded in `Hasm.Infrastructure/SystemDatabase/Migrations`. Each runs in its own transaction and is recorded in `migration_journal` with a SHA-256 checksum; applied scripts are immutable (edits abort), a database newer than the build is refused, concurrent runners are serialized by an advisory lock, and `DROP`/`TRUNCATE`/`DELETE FROM` are rejected. Chosen over an ORM because the ORM is undecided (ARCHITECTURE.md §15) and explicit SQL keeps opaque physical names visible and reviewable; the only new runtime dependency is Npgsql.

**Physical naming:** C# uses meaningful names; `SystemSchema` is the single logical-to-physical map (fixed, committed, pinned by tests; migrations use the physical names). It is internal and never exposed by API/clients. Obfuscation is hygiene only, not a security control (D-029).

**Central vs. company data:** central (`Hasm_System`): company id, opaque physical database name (immutable, unique), provisioning state, timestamps. Also central since Phase 2.3A: users, sessions, login throttling. Later central phases: licensing, roles/permissions, global parties. Exclusively per-company: chart of accounts, ledger, documents, inventory, local parties, branches, warehouses, settings, attachments metadata. Company display names/tax data are deliberately not in the registry yet (to be decided with company management). The registry has no HTTP endpoints until authentication/authorization exist.

**TLS to PostgreSQL (production requirement):** Npgsql's `SslMode=Require` (and `Prefer`) encrypts the connection but does **not** validate the server certificate, leaving it open to man-in-the-middle attacks. HASM therefore defaults to `VerifyFull` (chain + host name) and rejects `Disable`/`Allow`/`Prefer`/`Require` unless `AllowInsecureTransport=true`, which is logged by `ToString()` as `InsecureTransport=True`. Production needs a PostgreSQL server certificate whose subject/SAN matches `Hasm__Database__Host`, signed by a CA trusted by the Backend host (system store, or `Hasm__Database__SslRootCertificate` for a private CA). `VerifyCA` skips host-name checking; prefer `VerifyFull`. Certificate validation is never bypassed in code.

## Company database provisioning (Phase 2.2)
Each company gets its own PostgreSQL database. Provisioning is an **explicit administrative operation** run with the privileged account through `Hasm.Admin`; the API never provisions, never starts it, and exposes no endpoint for it (and never returns physical names or credentials).
```
dotnet run --project src/backend/Hasm.Admin -- register-company             # registers Pending, prints the company id only
dotnet run --project src/backend/Hasm.Admin -- provision-company <company-id>
```
Exit codes: 0 ok, 1 failure, 2 usage, 3 bad configuration, 4 another process is already provisioning that company.

**Flow** (`CompanyProvisioner`): take a per-company session advisory lock (dedicated connection to the system database, held throughout; requires direct, non-transaction-pooled connections) → Pending/Failed/Provisioning(no lock holder) → `Provisioning` (attempt counted) → create the database named by the registry's opaque name if absent → apply company migrations (`CompanyDatabase/Migrations`, own `migration_journal`, same runner) → bind identity (single immutable row holding the company id) → `REVOKE ALL ON DATABASE … FROM PUBLIC` → verify the mapping both ways → `Ready`.

**Schema:** system migration `0002` adds `ProvisioningAttempts` (int) and `LastErrorCode` (short sanitized code such as `migration`, `consistency`, `pg_<SQLSTATE>`; never a message) to the registry. Company migration `0001` creates the identity table (+ immutability trigger). Physical names are mapped in `CompanySchema` and pinned by tests; no company names appear in any physical name.

**States:** `Pending → Provisioning → Ready | Failed`, `Failed → Provisioning` (retry). `Ready` is terminal; provisioning a Ready company only re-verifies the mapping.

**Recovery:** a process death releases the lock with its connection, so a company in `Provisioning` with no lock holder is interrupted and is resumed by simply running `provision-company` again; cancellation also leaves `Provisioning`. Every step is idempotent. There is no distributed transaction between system and company databases; the two are reconciled by re-running. A failure records `Failed`; the company database is **never dropped automatically**. A pre-existing database with the registered name is accepted only if empty or already HASM-shaped (identity table present); any other database is refused and left untouched (`consistency`). Protected names (system, maintenance, `postgres`, `template0/1`) are never targets.

**Privileges:** provisioning uses `AdminUsername`/`AdminPassword` only (runtime credentials are rejected). Company databases are not connectable by PUBLIC; granting the runtime role `CONNECT`/table rights on a company database is still a DBA step (not automated yet).

**Tests:** `CompanyProvisioningTests` (real PostgreSQL, same safety gate as above). Company databases created by tests are named `hasm_test_<hex>` through an injected name generator; teardown drops only names the test generated itself and re-checks the prefix.

**PostgreSQL integration tests** (`tests/integration/Hasm.Database.IntegrationTests`) use a real server and create/drop their own databases named `hasm_test_<random hex>`; they never drop anything else. Destructive tests run only when **all** hold: `HASM_TEST_PG_ADMIN_CONNECTION` is set, `HASM_TEST_PG_DISPOSABLE=yes-this-server-is-disposable` is set (you assert the server is disposable), and the host is a loopback address. Otherwise they are reported *skipped*; with `HASM_REQUIRE_PG_TESTS=1` (CI) a closed gate *fails* the tests instead. Use a role without superuser rights, only `CREATEDB`. To run on a disposable local server you manage yourself:
```
$env:HASM_TEST_PG_DISPOSABLE="yes-this-server-is-disposable"
$env:HASM_TEST_PG_ADMIN_CONNECTION="Host=localhost;Username=<CREATEDB role>;Password=<pw>;Database=postgres;SSL Mode=Disable"
dotnet test tests/integration/Hasm.Database.IntegrationTests
```

**CI** (`.github/workflows/backend.yml`, job `postgres-integration`, Ubuntu): starts an ephemeral `postgres:16` service container, creates a restricted `hasm_test_admin` role (`CREATEDB`, no superuser/role/replication) with a random per-run password that is masked in logs and never committed, runs the tests with `HASM_REQUIRE_PG_TESTS=1`, then verifies from the TRX results that exactly 170 database tests ran (per-class counts) and all passed (skipped or missing tests fail the job). Non-TLS (`SSL Mode=Disable`) exists only in that disposable job's loopback connection string; the only literal credential in the workflow is the throwaway bootstrap password of that job-local container. The `build-test` job still runs these tests as skipped.

## Identity and local authentication (Phase 2.3A)
Central identities live in `Hasm_System` (system migration `0003_users_and_sessions.sql`, opaque physical names mapped in `SystemSchema.User/Session/LoginThrottle/UserCompanyAccess`). Scope: login/logout, sessions, throttling, administrative user creation and the initial administrator. **Not** in scope: MFA, AD, licensing seats, a login UI.

**Data model:** `User` (stable `uuid`, normalized unique login, display name, password hash, state `Active|Disabled|Locked`, `IsIdentityAdministrator`, timestamps; rows are never deleted and id/login are immutable via trigger), `Session` (server-side; SHA-256 of a 256-bit random token, client kind, last-seen, idle and absolute expiry, revocation time/reason), `LoginThrottle` (SHA-256 key digests, failure counters, block-until), `UserCompanyAccess` (explicit user-to-company link with FK to the company registry; created and revoked only through the Phase 2.3B access service, see below). Authentication never reads or grants company access; the status response contains no company information.

**Login identifiers:** trim, Unicode NFKC, invariant lower-casing; must match `[a-z0-9][a-z0-9._@-]{2,63}` (ASCII only, which rules out homoglyph look-alikes; fullwidth forms fold to ASCII). Uniqueness is on the normalized value, enforced by the application and by a database UNIQUE + CHECK constraint.

**Passwords:** `Microsoft.Extensions.Identity.Core` `PasswordHasher` (Identity v3: PBKDF2-HMAC-SHA512, random 128-bit salt per hash, iteration count stored in the hash, constant-time compare), default 210,000 iterations (`Hasm__Auth__Pbkdf2Iterations`, floor 100,000). Hashes with a lower count are upgraded at the next successful login. No custom cryptography; no plaintext or reversible storage. Policy: 12-128 characters, not trivially repetitive, not equal to the login (length based, no composition rules; there is no breached-password list yet).

**Session transport and storage (rationale):** one opaque, random, server-side session; two transports chosen by the client at login (`"client":"web"|"desktop"`):
- **Web (React):** `__Host-hasm_session` cookie, `HttpOnly; Secure; SameSite=Strict; Path=/` (no Domain). JavaScript never sees the token, so XSS cannot steal it, and it is never placed in `localStorage`/`sessionStorage`. The token is not in the response body. Requires the web UI and API to be same-site (e.g. `app.example.com` + `api.example.com`, or `localhost` ports in development).
- **Desktop (Tauri):** the WebView origin is cross-site to the API, so SameSite cookies are unsuitable. The token is returned once in the login response body and sent as `Authorization: Bearer`. The client must keep it in the OS credential store (Windows Credential Manager, from the Rust side), never in WebView storage; **this client side is not implemented in this phase**.
- Opaque server-side sessions (rather than JWTs) give instant revocation, server-side idle/absolute expiry and the per-session accounting the licensing phase will need. Only the token's SHA-256 is stored, so a database leak yields no usable tokens.
- **CSRF:** SameSite=Strict plus a required `X-Hasm-Request` header on every state-changing `/api` request (cross-site pages cannot send it without a CORS preflight, which only configured origins pass). CORS allows credentials only for the explicit `Cors:AllowedOrigins`.

**Sessions:** idle timeout 30 min (sliding, written at most once a minute), absolute limit 12 h (`Hasm__Auth__SessionIdleMinutes`, `SessionAbsoluteHours`). Each login issues a new token (no fixation). Logout revokes server-side. Disabling/locking a user, and changing a password (all other sessions), revoke sessions in the same transaction. The user's state and administrator flag are re-read on every request. An hourly housekeeping service removes long-expired/revoked sessions and stale throttle rows.

**Brute force / credential stuffing / enumeration:** failures are counted per login string (existing or not) and per source address (`MaxFailuresPerLogin`=5, `MaxFailuresPerSource`=20 per 15 min, then blocked 15 min; HTTP 429 + `Retry-After`). Blocks are temporary and do **not** change user state or end existing sessions, so an attacker cannot lock out or log out a real user permanently. A success resets the login counter but not the source counter. Every credential failure (unknown/malformed login, wrong password, Disabled, Locked) returns the same `401 {"error":"invalid_credentials"}`, and unknown logins spend the same hashing time. Source address is the connection's remote address; behind a reverse proxy set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` and restrict trusted proxies, otherwise all clients share one source counter.

**Endpoints** (`/api/v1`): `POST auth/login`, `POST auth/logout`, `GET auth/status` (anonymous allowed; returns `authenticated` and, when signed in, user id/login/display name/administrator flag and expiry times), `POST auth/change-password` (current password required; shares the throttle), `POST admin/users`, `POST admin/users/{id}/state` (`Active|Disabled|Locked`; an administrator cannot change their own state). Admin endpoints require an authenticated identity administrator (401/403 otherwise). There is **no public registration**. Auth responses are `no-store`; errors are generic JSON (no exception text; database failures return 503).

**Logging:** auth events log outcome codes, user ids and session ids only. Passwords, tokens, hashes, connection strings and attacker-supplied login strings are never logged (pinned by tests that capture all log output).

**Initial administrator (explicit, offline):**
```
$env:Hasm__Database__AdminUsername="..."; $env:Hasm__Database__AdminPassword="..."   # plus Host / SystemDatabaseName
$env:HASM_BOOTSTRAP_ADMIN_PASSWORD="..."      # optional; otherwise prompted without echo. Never a command-line argument.
dotnet run --project src/backend/Hasm.Admin -- bootstrap-admin <login> ["Display name"]
```
Creates the first identity administrator and refuses (exit 5) when one already exists (serialized by an advisory lock, so concurrent runs cannot create two). Exit codes otherwise as for the other commands.

**Runtime identity and privileges:** the API connects with `Hasm__Database__Username/Password` (never the provisioning admin account; if the runtime account is unusable the API returns 503 rather than falling back). The DBA must grant it, on the system database: `SELECT, INSERT, UPDATE` on the `User` table; `SELECT, INSERT, UPDATE, DELETE` on the `Session` and `LoginThrottle` tables; `SELECT` on `UserCompanyAccess` (extended by Phase 2.3B, see below; physical names are in `SystemSchema`; grants are not automated). The runtime role needs no DDL rights. API connections to PostgreSQL are pooled; all other TLS rules above still apply.

**Tests:** unit tests (login normalization, password policy/hashing, tokens, options) run everywhere. PostgreSQL tests (`IdentityMigrationTests`, `UserCreationTests`, `LoginTests`, `SessionTests`, `BruteForceProtectionTests`, `AuthApiTests`, same safety gate as above) use an injectable clock instead of sleeping and drive the real API in-process via `WebApplicationFactory`. `AuthWithoutDatabaseTests` (no database) covers anonymous access, CSRF header and generic errors.

## Company access and RBAC (Phase 2.3B)
Authorization metadata is central and lives in `Hasm_System` (system migration `0004_company_access_rbac.sql`; opaque physical names mapped in `SystemSchema.Role/RolePermission/RoleAssignment/AccessAudit` plus new columns on `UserCompanyAccess` and `Session`). Company databases are untouched: they hold no authorization data, and access to one company can never expose another company's data.

**Model.** Deny by default. A user may do `resource.action` in a company only if **all** hold, evaluated from the database on every request: the user is `Active`; the company is `Ready`; an *active membership* (`UserCompanyAccess`; revoked rows never count) exists; an *active role assignment* of that user in that company exists; the role is active and contains the permission; the assignment's scope covers the target. Authentication alone, and the `IsIdentityAdministrator` flag, grant nothing in any company.
- **Permission catalog** (`PermissionCatalog`, in code): resources with a granularity (`Company|Branch|Warehouse`) x actions (`View, Create, Update, Delete, Approve, Post, Export, Print`); code `resource.action`, e.g. `access.roles.update`. Unknown codes never grant. This phase registers only the access-administration resources (`access.members|roles|assignments|audit`); later phases add their resources together with a migration that extends the preset roles (a test pins the database presets to the code definitions). No business permissions were invented.
- **Roles:** *preset* (shipped with the software, company id NULL, immutable by database trigger: `Company Administrator`, `Access Manager`, `Access Auditor`) and *custom* (belong to one company; created, edited and deactivated by authorized users; never deleted). Role names are unique per company. A user may hold several roles; permissions are the union of the user's grants, each grant keeping its own scope.
- **Scopes:** an assignment is Company-wide, or limited to one Branch or one Warehouse id. Company-wide covers everything; Branch/Warehouse cover only their own id; narrower assignments never exercise coarser (e.g. company-level administrative) resources. Branch and warehouse ids are opaque uuids because those entities will live in the per-company databases (no business module exists yet); **scoped assignments fail closed**: `AssignRoleAsync` accepts a Branch/Warehouse scope only if an `IScopeTargetValidator` confirms the id exists in that company (a validator error counts as "no"). The default validator refuses everything, so until the branch/warehouse phase registers a real one, only company-wide assignments can be made (`400 scope_not_verifiable`). At evaluation time a scoped grant matches only its exact kind and id, never a company-wide target, another kind, or a coarser resource.
- **Last administrator guard:** revoking a membership or a company-wide Company Administrator assignment is refused (`409 last_administrator`) when it would leave the company with no active administrator. An administrator is an `Active` user with an active membership of a `Ready` company holding the active preset Company Administrator role company-wide, so disabled users, revoked memberships and narrower assignments do not count. The check runs inside the per-company lock, so concurrent removals serialize. A company that already has none is not blocked from cleanup, and the offline `grant-company-admin` recovery is unaffected. Disabling a user is an identity operation outside this lock and can still leave a company without a usable administrator (recover offline).
- **Account enumeration:** every `access.*` operation authorizes the caller before it looks anything up, so a caller without the permission (or without a selected company) gets one fixed answer regardless of whether a login or user id exists. Only a caller holding `access.members.create` sees `201` versus `user_not_found` (unknown, disabled, locked and malformed logins are indistinguishable); that is the legitimate add-by-login workflow.
- **Company selection:** `POST /api/v1/session/company` binds the server-side session to a company after a membership check; a non-member, an unknown company and an unavailable company all return the same `403 forbidden`. Company-scoped endpoints take **no company id from the client** (route, query, header and body claims are ignored); the company comes from the session row and every call re-validates it. Revoking a membership clears the selection of that user's sessions.
- **Escalation rules (enforced in `AccessService`, inside the transaction):** the actor needs the corresponding `access.*` permission company-wide **and** must possess every permission they hand out, for the scope they hand it out at (assigning a role; creating a role; editing a role - the editor must hold both its old and its new permissions). Revoking an assignment or membership requires holding what the target holds. Nobody may assign or revoke their own assignments or revoke their own membership. Preset roles cannot be changed. Mutations run in one transaction under a per-company advisory lock and re-read the actor's privileges inside it, so concurrent changes serialize (two administrators revoking each other cannot both succeed). Every change writes an append-only audit row (`AccessAudit`, trigger-protected) in the same transaction; denied or failed attempts write nothing.
- **Immediate effect:** nothing is cached or copied into the session. Revoked assignments and memberships, deactivated roles, disabled or locked users and non-Ready companies stop authorizing on the very next request, without a new login.

**Endpoints** (`/api/v1`; all require authentication, responses are `no-store`, state changes need `X-Hasm-Request`): `GET companies` (the caller's own selectable company ids only: no names, no physical identifiers), `POST|DELETE session/company`, `GET access/me`, `GET access/permissions`, `GET|POST access/roles`, `PUT access/roles/{id}/permissions`, `POST access/roles/{id}/deactivate`, `GET|POST access/members` (a member is added by login), `DELETE access/members/{userId}`, `GET|POST access/assignments`, `DELETE access/assignments/{id}`, `GET access/audit`. Another company's object ids yield `404` (indistinguishable from nonexistent ones), a missing permission `403`, no selected company `409 company_not_selected`.

**Initial administration (no public endpoint).** The first company administrator is created offline with the privileged account:
```
dotnet run --project src/backend/Hasm.Admin -- grant-company-admin <login> <company-id>
```
It grants membership and the preset Company Administrator role company-wide (audited with no actor user), is idempotent, and doubles as the recovery path. The user must already exist (`bootstrap-admin` or the admin API) and the company must be `Ready`. No HTTP route can grant a first administrator, and identity administrators get no company access from their flag.

**Runtime privileges** added for the API account (grants are not automated): `SELECT` on the company registry table; `SELECT, INSERT, UPDATE` on `UserCompanyAccess` (replaces the 2.3A `SELECT`-only grant), `Role` and `RoleAssignment`; `SELECT, INSERT, DELETE` on `RolePermission`; `SELECT, INSERT` on `AccessAudit`. `Session` needs the `UPDATE` it already has (selection column).

**Tests:** unit tests (`AccessModelTests`: catalog, presets versus migration seed, scope evaluation, escalation primitives) and real-PostgreSQL integration tests (`AccessMigrationTests`, `MembershipTests`, `RoleTests`, `AssignmentAndScopeTests`, `AccessRevocationTests`, `AccessAuditTests`, `OfflineAdministrationTests`, `ConcurrentAccessChangeTests`, `AccessApiTests`) under the same safety gate as above.

## Checks
```
dotnet test src/backend/Hasm.slnx
cd src/frontend && npm run typecheck && npm test && npm run build
cd src/desktop && npx tauri build --debug --no-bundle
```
CI runs the same checks (`.github/workflows/`).

## Phase 2.3A security limitations (must be resolved before production deployment)
1. **Temporary account-lockout denial of service.** The per-login throttle blocks a login identifier after 5 failures in 15 minutes regardless of who sent them, so anyone who knows or guesses a login can keep that user from signing in (the block expires, existing sessions and the account state are untouched). Unknown logins are throttled identically, so this does not reveal which accounts exist. Needs a mitigation such as a per-(login, source) counter or progressive delay, CAPTCHA/proof-of-work after repeated failures, and an administrator-visible alert, before production.
2. **Reverse-proxy client address.** The source address is the raw connection address and no forwarded-headers handling or trusted-proxy list is configured in code. Behind a proxy or load balancer every client shares one source counter (20 failures per 15 minutes), so one abuser can block sign-in for everyone. Configure `ForwardedHeaders` with explicit `KnownProxies`/`KnownNetworks` (never trust `X-Forwarded-For` from arbitrary peers) and test it before production.
3. No MFA, password reset, breached-password check or self-service recovery; the Desktop client credential-store handling is not implemented.
4. The `__Host-` session cookie requires HTTPS; web sign-in over plain HTTP does not persist a session.

## Known placeholders and limitations
- Desktop icons in `src/desktop/src-tauri/icons/` come from `assets/branding/hasm-icon.png` (`icon.ico` is the supplied `hasm-icon.ico`, byte-identical). The source raster is an upscaled 1024x1024 extraction from a 449x204 reference, so small icon sizes may look soft; replace with a true vector/high-resolution master before a commercial release (`npx tauri icon <square.png>`, then restore the approved `.ico`). Tauri does not always re-embed changed icons: touch `src-tauri/build.rs` before rebuilding.
- The Desktop CSP `connect-src` hardcodes the local development API origin (`https://localhost:7241`). A per-deployment mechanism for the API address is still to be decided.
- The Desktop app reaches the development backend only after the ASP.NET dev HTTPS certificate is trusted (`dotnet dev-certs https --trust`); certificate validation is never bypassed.
- The GitHub Actions workflows (`.github/workflows/`) have not run yet; they are first exercised by the pull request.
- The Desktop app has not been exercised end to end against a live backend.
- PostgreSQL integration tests are executed only by the CI `postgres-integration` job (no PostgreSQL in the managed development environment); they pass only once that job has run green.
- TypeScript 7 (`^7.0.2`) is used; it is a recent major version.
- Dev-server file access (`server.fs.allow` in `apps/web/vite.config.ts`) is limited to the frontend workspace and `assets/branding/`; keep it narrow.

## Phase 2.3B remaining production security requirements
1. **Audit-log tamper evidence (required before production).** `AccessAudit` is append-only by trigger only; a DBA or anyone with table ownership can still alter it. Needed: a cryptographic hash chain (each row hashes its content plus the previous row's hash, per company), periodic signed checkpoints anchored outside the database (WORM storage, separate log service or signed export), a verification command that detects gaps and edits, restriction of audit-table ownership/DDL to a separate role, and an export for auditors. Identity administrators cannot view company audit trails.
2. **Branch/warehouse validation.** Register a real `IScopeTargetValidator` when those entities exist, and revoke or flag assignments whose branch/warehouse is later deleted.
3. **Identity-side administrator loss.** Disabling or locking the last usable Company Administrator through the identity API is not blocked by the company guard; add an alert or guard with user management, or rely on offline `grant-company-admin`.
4. A company user holding `access.members.create` can confirm that a usable login exists (inherent to add-by-login). Add rate limiting/alerting for bulk probing together with the 2.3A throttling work.
5. No per-request authorization cache by design (a few small queries); measure before optimizing and keep revocation immediate.
6. Role/permission changes are validated against the catalog compiled into the running Backend; rolling-upgrade skew between instances with different catalogs is not handled.
7. Segregation of duties and approval workflows are not part of this phase.
8. The Phase 2.3A limitations above (lockout denial of service, forwarded headers, no MFA/reset/breached-password check, desktop credential-store handling, HTTPS-only cookie) are unchanged and must still be resolved before production.
