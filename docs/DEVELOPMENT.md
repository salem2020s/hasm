# HASM — Development Guide

Scope of the current foundation: Backend health endpoint, shared React frontend (Arabic RTL / English LTR), the Tauri 2 Windows shell, (Phase 2.1) the system database foundation with a minimal company registry, (Phase 2.3A) central identity with local authentication, (Phase 2.3B) company access with role-based access control, (Phase 2.3C-1) authentication and access security hardening, and (Phase 2.3C-2) cryptographic audit integrity. No business modules exist yet.

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

**CI** (`.github/workflows/backend.yml`, job `postgres-integration`, Ubuntu): starts an ephemeral `postgres:16` service container, creates a restricted `hasm_test_admin` role (`CREATEDB`, no superuser/role/replication) with a random per-run password that is masked in logs and never committed, runs the tests with `HASM_REQUIRE_PG_TESTS=1`, then verifies from the TRX results that exactly 275 database tests ran (per-class counts) and all passed (skipped or missing tests fail the job). Non-TLS (`SSL Mode=Disable`) exists only in that disposable job's loopback connection string; the only literal credential in the workflow is the throwaway bootstrap password of that job-local container. The `build-test` job still runs these tests as skipped.

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

**Brute force / credential stuffing / enumeration:** layered per-(login, client), per-client and per-login-ceiling counters (HTTP 429 + `Retry-After`; blocks are temporary and never change user state or end sessions). Every credential failure returns the same `401 {"error":"invalid_credentials"}` and unknown logins spend the same hashing time. Details, defaults and the trusted-proxy handling of the client address: see "Authentication and access security hardening (Phase 2.3C-1)" below. This replaces the Phase 2.3A single per-login counter, which allowed temporary lockout of a known user, and the earlier advice to set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, which is now refused at startup.

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
- **Last administrator guard:** revoking a membership or a company-wide Company Administrator assignment is refused (`409 last_administrator`) when it would leave the company with no active administrator. An administrator is an `Active` user with an active membership of a `Ready` company holding the active preset Company Administrator role company-wide, so disabled users, revoked memberships and narrower assignments do not count. The check runs inside the per-company lock, so concurrent removals serialize. A company that already has none is not blocked from cleanup, and the offline `grant-company-admin` recovery is unaffected. Since Phase 2.3C-1 disabling or locking a user takes the same lock and is refused when it would remove the last usable administrator (see "Company administrator continuity").
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

**Runtime privileges** added for the API account (grants are not automated; Phase 2.3C-2 adds `SELECT, INSERT` on the audit-event table, see "Upgrade and compatibility" under Cryptographic audit integrity): `SELECT` on the company registry table; `SELECT, INSERT, UPDATE` on `UserCompanyAccess` (replaces the 2.3A `SELECT`-only grant), `Role` and `RoleAssignment`; `SELECT, INSERT, DELETE` on `RolePermission`; `SELECT, INSERT` on `AccessAudit`. `Session` needs the `UPDATE` it already has (selection column).

**Tests:** unit tests (`AccessModelTests`: catalog, presets versus migration seed, scope evaluation, escalation primitives) and real-PostgreSQL integration tests (`AccessMigrationTests`, `MembershipTests`, `RoleTests`, `AssignmentAndScopeTests`, `AccessRevocationTests`, `AccessAuditTests`, `OfflineAdministrationTests`, `ConcurrentAccessChangeTests`, `AccessApiTests`) under the same safety gate as above.

## Authentication and access security hardening (Phase 2.3C-1)
Resolves Phase 2.3A limitations 1 and 2 and Phase 2.3B requirement 3. No schema change: throttle rows stay in the existing `LoginThrottle` table (key digests only; the new counters use distinct key namespaces), so there is no new migration and no new database privilege.

### Login abuse protection
**Root cause.** Failures were counted per login string alone, so anyone could submit five wrong passwords for a known login and block the real user for 15 minutes (a lockout denial of service). The source counter used the raw peer address, so behind a proxy all clients shared one counter.

**Design and trade-offs.** A per-login counter must stay small to stop guessing, but then lets a third party lock the user out. A per-address counter alone cannot stop a botnet. HASM therefore layers three independent counters, each counted within `Hasm__Auth__FailureWindowMinutes` (15) and answering HTTP 429 + `Retry-After` for `BlockMinutes` (15). Blocks never change the account state and never end existing sessions.

| Counter | Key | Default | Effect |
|---|---|---|---|
| Pair | login + client | `MaxFailuresPerLoginSource` = 5 | blocks only that client for that login, so an attacker cannot lock the real user out from any other address |
| Client | client | `MaxFailuresPerSource` = 20 | stops credential stuffing / spraying by one client |
| Login ceiling | login, all clients | `MaxFailuresPerLoginGlobal` = 100 (at least 4x the pair limit) | last resort against distributed guessing; blocks the login for everyone and logs `auth.login_distributed_attack_suspected` (user id only) |

A success clears only the caller's pair counter (the client and ceiling counters are kept, so failures cannot be laundered). Every counter exists for every attempted login string, real or not. Every credential failure (unknown, malformed, wrong password, Disabled, Locked) answers the same `401 invalid_credentials`, blocked answers are identical for existing and non-existing logins, and unknown logins spend the same hashing time. `change-password` failures use their own per-user counter, so other people's failed sign-ins can never block a password change. The "client" is the validated client address (next section); IPv4-mapped IPv6 folds to IPv4 and an IPv6 client is identified by its /64 (one subscriber cannot rotate addresses). No CAPTCHA or external service is used (none was strictly necessary and none was approved).

### Trusted reverse proxy
**Root cause.** No forwarded-header handling existed, and the earlier documentation suggested `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, which makes ASP.NET Core trust forwarded headers from **any** sender. HASM now refuses to start when that switch is set.

Settings (section `Hasm:ReverseProxy`, environment `Hasm__ReverseProxy__*`; all optional; the feature is **off** by default, meaning direct connections):

| Setting | Meaning |
|---|---|
| `Enabled` | `true` to honour forwarded headers from the listed proxies. Requires at least one proxy or network. |
| `KnownProxies` | Exact proxy addresses (IPv4/IPv6). `0.0.0.0` and `::` are refused. |
| `KnownNetworks` | CIDR ranges of proxies (internal ranges only: at least /8 for IPv4, /7 for IPv6; `0.0.0.0/0` and `::/0` are refused). |
| `ForwardLimit` | Number of trusted proxies in the chain (default 1, max 10). Only that many right-most entries are read; entries a client prepends are never used. |
| `TrustProtocol` | Also honour `X-Forwarded-Proto` from trusted proxies (default `true`). |

`X-Forwarded-For` and `X-Forwarded-Proto` are applied **only** when the immediate peer is a configured proxy or inside a configured network. From anyone else they are ignored and the connection address is used; a client cannot impersonate a proxy by naming its address in a header. The framework's implicit loopback trust is removed: list `127.0.0.1` and `::1` yourself if the proxy runs on the same host. Invalid configuration stops the process at startup with a message that names the setting but never its value. The middleware runs first in the pipeline, so HTTPS redirection, HSTS and throttling see the validated client.

The proxy must overwrite (or append to) the client-supplied `X-Forwarded-For`, and must be the only way to reach the API (bind Kestrel to loopback or a private interface, and firewall the port).

*Linux (nginx on the same host, Kestrel on loopback, systemd):*
```
# /etc/systemd/system/hasm-api.service.d/proxy.conf
[Service]
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=Hasm__ReverseProxy__Enabled=true
Environment=Hasm__ReverseProxy__KnownProxies__0=127.0.0.1
Environment=Hasm__ReverseProxy__KnownProxies__1=::1

# nginx location (edge proxy: set, do not append, the client address)
proxy_pass http://127.0.0.1:5000;
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header Host $host;
```
With a further trusted hop in front of nginx (load balancer, CDN), use `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`, list both hops in `KnownProxies`/`KnownNetworks` and set `Hasm__ReverseProxy__ForwardLimit=2`.

*Windows Server (API as a Windows service behind a reverse proxy on another host, e.g. IIS ARR, HAProxy or nginx at 10.20.0.5):* use an untracked production configuration file next to the API, for example `appsettings.Production.json`:
```json
{ "Hasm": { "ReverseProxy": { "Enabled": true, "KnownProxies": [ "10.20.0.5" ], "KnownNetworks": [ "10.20.1.0/24" ], "ForwardLimit": 1 } } }
```
or machine environment variables (elevated shell, then restart the service): `setx /M Hasm__ReverseProxy__Enabled true` and `setx /M Hasm__ReverseProxy__KnownProxies__0 10.20.0.5`. For IIS ARR enable "Preserve client IP in the following header" = `X-Forwarded-For`, and restrict the API's firewall rule to the proxy host. Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. These Windows/IIS steps follow the ASP.NET Core documentation and have **not** been exercised on a Windows server in this phase.

### Company administrator continuity
**Root cause.** The last-administrator guard covered only membership and assignment revocation. Disabling or locking a user through the identity API bypassed it, and two identity administrators could disable each other concurrently.

`IdentityStore.SetStateAsync` now runs these steps in one transaction (`AdministratorContinuity` is the single definition of a usable administrator, shared with `AccessService`):
1. Take an identity-administrator advisory lock and require the acting user to still be `Active`.
2. Refuse (`409 last_identity_administrator`) to take the last Active identity administrator out of Active.
3. Find the Ready companies where the target is a usable administrator, take each company's advisory lock (the same lock `AccessService` uses; sorted order, restarting with a larger set if a membership appears meanwhile), and refuse (`409 last_company_administrator`) if any would be left with none.

The check, the update and the session revocation commit together, so disabling, locking and revoking serialize against each other and the loser sees the winner. The response carries no company information. Re-activation and moves between non-Active states are never blocked. A company that already has no usable administrator blocks nothing (cleanup stays possible) and the offline `grant-company-admin` recovery is unchanged. Identity administrators still get no company access from their flag.

Trade-off: a compromised sole Company Administrator cannot be disabled through the API. First appoint a second administrator (offline `grant-company-admin`, or another administrator through the access API), then disable the account. A direct database change, or an administrator's own password loss, still needs offline recovery.

### Tests
Unit: `SourceAddressTests`, `IdentityPrimitivesTests` (option limits). No-database API tests: `ReverseProxyTests` (trusted and untrusted proxies, spoofing, chains, IPv6, validation, refusal of the unsafe framework switch). PostgreSQL: `LoginAbuseProtectionTests`, `AdministratorContinuityTests`, `SecurityHardeningApiTests`, plus the revised `BruteForceProtectionTests`, `AuthApiTests` and `LastAdministratorTests`. Tests whose earlier assertions encoded the old per-login lockout were rewritten to the new behaviour (the same client stays blocked, other clients do not). One test that used the identity API to simulate "company without administrator" now applies that state by direct SQL, because the API call is now refused.

## Cryptographic audit integrity (Phase 2.3C-2)
Resolves Phase 2.3B production requirement 1 (audit-log tamper evidence). System migration `0005_cryptographic_audit_integrity.sql` adds one append-only table (`SystemSchema.AuditEvent`, opaque physical names). Existing history (`AccessAudit`, migration 0004) is **not** rewritten, backfilled or deleted.

### Architecture
- **Independent streams.** One `system` stream (identity events) and one stream per company (`company:<id>`). Each has its own gap-free sequence (1, 2, 3, …), its own hash chain and its own checkpoints; they share no lock and no key-derived state, and verification of one stream reads only that stream (company isolation). A database CHECK ties a row's stream to its company.
- **Event record.** `stream, seq, event id (uuid), occurred-at (UTC, microseconds), event type, company, actor, target, subject, legacy ref, detail (≤1000 chars, short structured text, never secrets), format version, key id, prev hash, hash, mac`. Event types are numeric and never renumbered: 1-7 company access (existing), 100-103 identity (`UserCreated`, `InitialAdministratorCreated`, `UserStateChanged`, `PasswordChanged`), **1000+ reserved** for future accounting, inventory, approval and privileged-administration events: a future module appends `new AuditEventData(type, …)` in its own transaction through `AuditTrail.AppendAsync`; no change to the mechanism is needed. Successful logins/failures are deliberately not chained (volume; they remain in the throttle table and log).
- **Deterministic, versioned serialization** (`AuditCanonical`, format version 1; documented in the class): fixed magic, big-endian integers, length-prefixed UTF-8 strings, explicit presence flags (so `null`, empty and absent differ), the key id and the previous hash inside the authenticated bytes. A golden test pins version 1 forever; a change needs a new version number and a retained old reader. Unknown versions are reported as *unverifiable*, never as valid.
- **Chain and authentication.** `hash = SHA-256(canonical)`; the next record's `prev hash` is this hash (the first record uses a per-stream genesis value). `mac = HMAC-SHA-256(eventKey, canonical)`. The hash alone proves nothing against someone who can write the database (they can recompute it); the keyed MAC means a database-only attacker cannot produce a valid record.
- **Atomicity.** `AccessService` and `IdentityStore` append the chained event **inside the same PostgreSQL transaction** as the mutation (`AuditTrail.AppendAsync(conn, tx, …)`), then commit through `AuditTrail.CommitAsync`. If the event cannot be written (key missing, damaged chain, checkpoint store failing) an exception is thrown, the transaction is abandoned and the mutation does not happen (HTTP `503 audit_unavailable`; the client sees one generic code, the log carries only the short reason code). Denied or failed attempts write nothing. Audited today: all seven company-access events (including the offline `grant-company-admin`), user creation, initial administrator, user state change (with its session revocation) and real password changes (a transparent rehash is not an event). The `AccessAudit` table is still written in the same transaction as a readable projection (the access API still serves it); each chained event carries the projection row id, and verification cross-checks both directions.
- **Concurrency.** Appends to one stream are serialized by a transaction-scoped advisory lock taken **last** in every transaction (after the company/identity locks), so there is one order and no gap even under contention; a rolled-back append consumes no sequence number. A transaction that appends twice to one stream (e.g. `grant-company-admin`) validates and anchors only committed state.
- **Append-only.** A trigger rejects UPDATE and row DELETE, even for the table owner; `PUBLIC` has no privileges; the runtime role gets `SELECT, INSERT` only. `TRUNCATE` cannot be intercepted by a trigger under the migration-safety rules (and the table owner can disable triggers), so these are **detected** (below), not prevented.

### Cryptographic mechanism, trade-offs and key management
Only established .NET primitives are used (`SHA256`, `HMACSHA256`, `HKDF`, `RandomNumberGenerator`, `CryptographicOperations.FixedTimeEquals`); nothing custom.
- **Why HMAC-SHA-256 (not per-event digital signatures).** An HMAC is fast on the write path, needs no key-pair management and is secure against anyone without the key, which is the threat here (a DBA or database-level attacker). The cost: verification needs the same secret, so the verifier host must be trusted with the key, and a third party cannot verify without it. ECDSA P-256 (built into .NET) would let auditors verify with a public key only, at the price of private-key handling, signature size and speed; it can be added later as a new format version (checkpoint signing is the natural first use) without touching existing records.
- **Key derivation.** One 256-bit master secret per *key id*; `HKDF-SHA-256` derives two independent sub-keys (`event`, `checkpoint`) bound to the key id, so an event code can never be replayed as a checkpoint code and vice versa. Keys are random (≥ 32 bytes), never derived from a password.
- **Key files.** `Hasm__Audit__KeyDirectory` holds `<keyId>.hkey` (one base64 line). No key is in Git (`*.hkey`/`*.hcp` are git-ignored), in the database, in a record, a log, an exception or a command line. `Hasm.Admin audit-keygen <key-id>` creates one with a CSPRNG, **never overwrites**, restricts it to the owner (Linux 0600 in a 0700 directory; Windows: protected ACL, current user only) and prints only the id. At load the permissions are checked: Linux refuses any group/other access; Windows refuses read access for Everyone, Users or Authenticated Users. A missing, malformed, short or exposed key makes audited mutations fail closed. Key identifiers are `[a-z0-9][a-z0-9-]{0,31}`.
- **Rotation.** Create the new key (`audit-keygen 2027-a`), set `Hasm__Audit__ActiveKeyId=2027-a` and restart. New records and checkpoints use it; **every old key file must stay in the directory** so older records keep verifying (a record carries its key id; the id is authenticated). Removing a retired key does not break integrity: its records become *incomplete* (unverifiable), never *verified*. If the head record's key is unavailable the system refuses to append. Keys are cached per process; a restart picks up changes.
- **Backup and recovery of keys.** The key directory is the one thing that cannot be rebuilt. Back it up when created and after every rotation (offline: encrypted removable media or a sealed printout in a safe, stored apart from the database backups and from the checkpoint copies). Restore it with the same ownership/permissions (Linux `chown <service-user> && chmod 700 dir && chmod 600 *.hkey`; Windows: remove inherited ACEs, grant only the service account, `icacls <dir> /inheritance:r /grant:r "<service-account>:(OI)(CI)F"`). If *all* copies of a key are lost, records written under it can never be verified again (the data is intact but unauthenticated); rotate to a new key immediately, keep the checkpoints and document the break. A compromised key: rotate, then treat records authenticated by that key (and anything after the suspected time) as untrusted; checkpoints made by other keys, kept elsewhere, bound what could have changed.
- **Windows/Linux.** Same configuration on both. Windows paths such as `D:\HasmAudit\keys`; run the service under a dedicated account. The Windows ACL checks run in the unit tests on Windows (this phase was developed on Windows); the Linux mode checks run in CI, and the Linux behaviour was **not** exercised locally.

### Protected checkpoints (outside the database, offline)
- A **checkpoint** states "record *N* of stream *S* has hash *H*", authenticated with the checkpoint sub-key, and chained to the previous checkpoint of that stream (so a checkpoint removed from the middle is detected). One append-only JSON-lines file per stream in `Hasm__Audit__CheckpointDirectory`; file names are hashes of the stream key (no company ids); each append is flushed to disk. The directory must **not** be inside the audited database host's data directory and should be on separate media or a separate machine's mount, with write access only for the service account (Linux: refuses a group/other-writable directory; Windows: refuses broad write ACLs). No network, cloud service or internet is used; copying the directory to removable or write-once media is the offline anchoring step, and `audit-verify --checkpoint-dir <copy>` verifies against any copy.
- A checkpoint is written **after every commit** (`MaxUnanchoredEvents` = 0). Checkpoints only move forward; the same sequence with another hash is refused as a fork.
- **Fail-safe.** If anchoring fails after a commit, the mutation stays committed (it cannot be undone) and the failure is logged (reason code only); the *next* audited mutation first tries to catch the checkpoint up and is refused (`503`) if it cannot, so the unanchored window never exceeds `MaxUnanchoredEvents` (0..1000; raise it only if you accept a larger window and want to tolerate brief storage outages). Before every first write of a transaction the head is also re-verified against its MAC and the newest checkpoint: a damaged head, a stream shorter than its checkpoint (deleted tail, restored older database), an emptied stream with checkpoints or a head that contradicts its checkpoint refuses all new events. The chain is **never reset or silently extended**; an operator investigates, restores or consciously re-baselines.
- **Configuration** (environment variables; none have defaults and none are committed): `Hasm__Audit__KeyDirectory`, `Hasm__Audit__ActiveKeyId`, `Hasm__Audit__CheckpointDirectory`, optional `Hasm__Audit__MaxUnanchoredEvents`. `Hasm__Audit__AllowInsecureFilePermissions=true` exists only for disposable test environments. Invalid or missing settings are reported by setting name, never by value. The API still starts without them (anonymous endpoints work) but every audited mutation returns `503 audit_unavailable`.

### Upgrade and compatibility
The migration only adds a table (repeatable: checksummed, run once, a no-op afterwards). An upgraded installation keeps all `AccessAudit` rows untouched; the chain of each stream starts at the first event written after the upgrade (genesis prev hash). Rows from before that point cannot be authenticated retroactively; verification reports them as *legacy, unauthenticated* (a note, not an error). Runtime database grants to add: `SELECT, INSERT` on the audit-event table (physical name in `SystemSchema.AuditEvent`) and `SELECT` on it for the account that runs `audit-verify`; no `UPDATE`, `DELETE` or `TRUNCATE` for any application account.

### Verification tool (read-only)
```
export Hasm__Database__Host=… Hasm__Database__SystemDatabaseName=… Hasm__Database__Username=… Hasm__Database__Password=…   # runtime (least-privilege) account; no admin account is needed
export Hasm__Audit__KeyDirectory=/secure/keys Hasm__Audit__CheckpointDirectory=/mnt/audit-anchor
dotnet run --project src/backend/Hasm.Admin -- audit-verify system
dotnet run --project src/backend/Hasm.Admin -- audit-verify <company-id>                       # or company:<id>
dotnet run --project src/backend/Hasm.Admin -- audit-verify <company-id> --checkpoint-dir /media/offline-copy
```
It runs in a READ ONLY, snapshot-consistent transaction and never repairs, rewrites or deletes anything. It prints the stream, the verdict, the record count and range, the **verified range** (contiguous prefix whose records are fully authenticated), checkpoint counts, unanchored records and findings (code, sequence number, fixed message). It never prints keys, credentials, connection details, physical names or event payloads.

| Exit code | Meaning |
|---|---|
| 0 | **Verified**: chain, authentication codes and checkpoints all check out and the head is anchored |
| 6 | **Failed**: tampering or corruption detected (see findings) |
| 7 | **Incomplete**: nothing contradicts the evidence but integrity cannot be proven (missing key or checkpoint, unanchored tail, unreadable checkpoint line, unknown format) |
| 1 / 2 / 3 | operational failure / usage / bad configuration (as for the other commands) |

Findings: failures `seq_gap`, `seq_order`, `link_broken`, `hash_mismatch`, `mac_invalid`, `stream_mismatch`, `checkpoint_mismatch`, `checkpoint_unmatched`, `tail_truncated`, `checkpoint_mac_invalid`, `checkpoint_chain_broken`, `checkpoint_order`, `projection_mismatch`/`projection_missing`/`projection_unchained`; incomplete `key_missing`, `checkpoint_key_missing`, `no_checkpoints`, `unanchored_tail`, `checkpoint_unreadable`, `unsupported_format`; note `legacy_unprotected_rows`.

`audit-anchor <stream> [--establish-baseline]` writes the current head of an otherwise **sound** stream to the checkpoint store; it refuses (exit 6) if the stream shows any failure or unverifiable record, and never writes to the database. Catching up an unanchored tail needs no flag. When **no** checkpoint exists at all it refuses unless `--establish-baseline` is given, because anchoring then defines what is trusted from now on (a stream whose tail was deleted together with the checkpoints looks perfect): review the stream and compare with any offline copy first. The same rule applies at runtime: if events exist but no checkpoint does (store wiped, replaced, or the very first anchor never succeeded) audited mutations are refused with `audit_baseline_missing` until an administrator establishes the baseline. `audit-keygen <key-id>` creates a key (above).

*Scheduling later (not implemented):* run `audit-verify system` and each company from cron / a systemd timer or Windows Task Scheduler (e.g. nightly), alert on any non-zero exit (6 = incident, 7 = investigate evidence), and copy the checkpoint directory to offline media after each run. A scheduled in-process verifier or an alert channel is future work.

### Crash consistency and recovery
| Situation | Behaviour |
|---|---|
| PostgreSQL commits, checkpoint write fails (storage down, full, permissions) | The mutation and its event stay committed. The failure is logged (reason code). The stream is **Incomplete** (unanchored tail) until anchored; the next audited mutation first tries to catch up and is refused with `503` if it cannot. |
| Process dies after commit, before or during the checkpoint write | Same as above on restart: committed data is preserved, the next verification reports `unanchored_tail` (never Verified), the first audited write after restart anchors the head if the store works. A torn checkpoint line is ignored by readers (reported `checkpoint_unreadable`, Incomplete) and the next append starts on a fresh line. |
| Process dies before commit | Nothing is persisted: no event, no mutation, no sequence gap, no checkpoint (checkpoints are written only after a successful commit). |
| Ambiguous commit (connection lost during COMMIT) | If it committed, it is treated as the first case; if not, as the previous one. |
| Checkpoint store wiped, replaced or empty while events exist | No silent re-baselining (`audit_baseline_missing`; see `audit-anchor`). |
| Checkpoint ahead of the database, head differing from its checkpoint, damaged head | New events refused; nothing is repaired automatically. |
Verification never reports **Verified** unless every record's hash and MAC check out, the sequence is contiguous from 1, the head is covered by a valid checkpoint, and no key, checkpoint or format is missing. An empty stream beside unauthenticated history (projection rows, users) is **Incomplete** (`no_authenticated_history`), not Verified. Events written in the window between commit and anchoring (at most `MaxUnanchoredEvents`, default 0 before the next write is refused) are the only ones whose silent loss could go unnoticed if an attacker also removes the matching checkpoints.

### Checkpoint trust boundaries
Storing checkpoints outside PostgreSQL does **not** make them immutable: they are ordinary files. What they add is a second place an attacker must also reach.
- **Who can touch the files.** The API/Admin service account creates the directory and appends; it necessarily has write access to the files, so a compromised API process **can** alter or delete previous checkpoints (the code only appends, but the account could do more). Other unprivileged OS accounts have no access (Linux: refused if group/other-writable; Windows: refused if Everyone/Users/Authenticated Users can write). Root / local Administrators can read, alter or delete anything, including keys.
- **Database administrator** (database access, no OS access to the application host): can alter or delete records but cannot produce valid codes without the key and cannot rewrite checkpoints, so tampering, deletion and tail truncation are detected. **Not** protected if the checkpoint directory or key directory is on the database host or readable/writable by the DBA's account. Keep keys and checkpoints off the database server.
- **Compromised OS administrator of the application host:** not protected (keys, checkpoints and the running process are all in reach). Only checkpoint copies already taken to independent offline media bound what they can rewrite (everything up to the last offline copy remains checkable).
- **Optional OS hardening (not implemented, recommended):** on Linux make each checkpoint file append-only for the service account (`chattr +a`, set by root after the first append of a stream) or keep the directory on a mount the service can only append to; on Windows grant the service account "append data" but not "delete"/"write attributes" on the files via NTFS ACLs. Verify that the service still works before relying on it.
- **Offline backup to an independent protected destination (no cloud, no new service).** Checkpoint files are append-only, so each copy must be a *prefix-compatible superset* of the previous one: (1) after each `audit-verify` run, copy the checkpoint directory to removable media (or a write-once / read-only-mounted disk) kept in a different place from the database backups and the key backup; (2) before overwriting an older copy confirm the new file begins with the old one (`cmp -n <old size> old new` on Linux, `fc /b` or a hash of the old byte range on Windows) and keep both generations; (3) record the file size and SHA-256 of each copy in a signed or physically secured log; (4) periodically run `audit-verify <stream> --checkpoint-dir <copy>` (read-only) from a trusted machine that holds a key backup; (5) store the key backup apart from the checkpoint copies, because the key alone lets someone forge checkpoints and the copies alone are useless to them. A copy older than the attacker's actions is the only evidence that survives a full compromise of the host.
- **HMAC, not signatures.** Checkpoints and events are authenticated with a shared secret (HMAC-SHA-256). Anyone who holds the key can create valid records and checkpoints; verifying them needs the same secret. This is *not* a public-key digital signature: it gives no third-party verifiability or non-repudiation. A public-key scheme (ECDSA) would let auditors verify without holding a forging capability and is future work.

### Database roles
The migration/administrative account (`AdminUsername`) owns the audit table; the API runtime account (`Username`) must **not** own it and must hold only `SELECT, INSERT` on it (no `UPDATE`, `DELETE`, `TRUNCATE`, no DDL). `audit-verify` runs under the runtime account and adds the informational finding `verifier_account_can_modify_audit` when the connected account owns the table or holds one of those privileges, so a mis-granted runtime role is visible. Do not give the runtime account membership in the owning role.

### What is and is not detected
| Scenario | Result |
|---|---|
| Modified payload, timestamp, actor/target, event type, id, key id, legacy ref, stream/company, sequence | Detected (hash mismatch; a recomputed hash still fails the MAC) |
| Inserted event (end or middle) without the key | Detected (`mac_invalid`, gap, checkpoint disagreement) |
| Reordered events | Detected |
| Deleted middle event | Detected (gap, broken link, unmatched checkpoint) |
| Deleted **tail** events / whole table truncated / database restored from an older backup | Detected **only through the external checkpoints** (`tail_truncated`) and refused at the next write; a database-only chain cannot show it and reports `Incomplete` without checkpoints |
| Broken chain link; swapped key id; unknown format version | Detected / reported unverifiable |
| Altered, removed, reordered or forged checkpoints | Detected (MAC, chain) |
| Newest checkpoints removed from the file | Reported as an *unanchored tail* (Incomplete), indistinguishable from an anchoring lag; the chain itself still verifies. Compare with an offline copy |
| Tampering with the readable `AccessAudit` projection | Detected through the authenticated events (mismatch, missing row, unchained row) |
| Pre-upgrade `AccessAudit` history | **Not authenticated**; reported as legacy |
| An attacker who holds the audit key **and** database access **and** the checkpoint directory (and its offline copies) | **Not protected**: they can rebuild a consistent chain and checkpoints. This includes a compromised server administrator controlling all keys, checkpoints and backups |
| An attacker with the key and database access but without the checkpoint store | Can forge records, but cannot make the new head match old checkpoints without rewriting those too; anything newer than the last offline copy is exposed to rewriting |
| Loss of every copy of a key; deletion of both database and checkpoints and backups | Evidence is lost (not forgeable, but unrecoverable) |
| Events never written because the application skipped auditing (a bug) or a mutation done directly in SQL | Not detected (only mutations through the audited code paths are covered) |
| Reading audit data, denial of service, host compromise of the running API | Out of scope |
| Timestamps | Come from the Backend clock; the chain proves they were not changed, not that the clock was right |

### Operational limits of this phase
- **Several API instances** may share one checkpoint directory only on a filesystem with reliable exclusive file locking (a local disk, or a correctly configured share); each append holds an exclusive handle across "read newest checkpoint" and "append". Multi-host locking over network filesystems was not tested.
- The checkpoint files grow by about 330 bytes per audited commit and are never rotated or pruned yet (rotation must keep the chain: archive whole files only).
- No export for auditors, no scheduled/in-process verification and no alerting yet; the offline copy of the checkpoint directory is a manual step.
- HMAC verification requires the secret on the verifier's host (see trade-offs); there is no public-key verification yet.
- Audit-table ownership stays with the account that ran the migration; the runtime role privilege rules are under "Database roles" (reported, not enforced).
- A first anchor that never succeeded (bad checkpoint directory at the very first write) leaves events without any checkpoint: an administrator must run `audit-anchor --establish-baseline` after fixing the storage.
- Only mutations that go through `AccessService`/`IdentityStore` are audited; future modules must append their own events in their transactions (extension point above).

### Tests
Unit (no database): canonical format golden value and per-field sensitivity, MAC/hash verification, key derivation, key file creation/permissions/rotation, checkpoint store (chain, forward-only, fork, torn write, permissions), options. PostgreSQL (same safety gate as above): `AuditMigrationTests`, `AuditChainTests`, `AuditTamperDetectionTests`, `AuditAtomicityTests`, `AuditConcurrencyTests`, `AuditKeyRotationTests`, `AuditFileEvidenceTests` (real key files and checkpoint directory), `AuditApiTests`, `AuditAdminToolTests` (runs the real tool and checks exit codes and output), `AuditRecoveryTests` (restart with unanchored events, wiped checkpoint store, explicit baseline, empty-stream honesty, privilege note). **Not yet verified by real CI:** the Linux file-permission checks (modes 0600/0700, group/other refusal) and the GitHub Actions workflow itself (including its 275-test table) have only been exercised locally on Windows; the first CI run must be green before these are relied upon. The existing identity/access tests now run against an in-memory key ring and checkpoint store; the migration-count assertions and the CI per-class table were extended, nothing was removed or weakened.

## Checks
```
dotnet test src/backend/Hasm.slnx
cd src/frontend && npm run typecheck && npm test && npm run build
cd src/desktop && npx tauri build --debug --no-bundle
```
CI runs the same checks (`.github/workflows/`).

## Phase 2.3A security limitations
1. ~~Temporary account-lockout denial of service.~~ **Resolved in Phase 2.3C-1** (see above). Residual: an attacker who produces `MaxFailuresPerLoginGlobal` failures for one login within the window across many clients can still block that login for everyone for `BlockMinutes` (existing sessions are unaffected). The `auth.login_distributed_attack_suspected` log event is the alert signal; there is no automated alerting, known-device allowance, progressive delay or CAPTCHA/proof-of-work yet.
2. ~~Reverse-proxy client address.~~ **Resolved in Phase 2.3C-1** (see above). Residual: correctness depends on the proxy overwriting `X-Forwarded-For` and on the API being unreachable except through it; the Windows/IIS guidance is untested.
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
1. ~~**Audit-log tamper evidence (required before production).**~~ **Resolved in Phase 2.3C-2** (see "Cryptographic audit integrity"); remaining limits are listed there. The original requirement was: `AccessAudit` is append-only by trigger only; a DBA or anyone with table ownership can still alter it. Needed: a cryptographic hash chain (each row hashes its content plus the previous row's hash, per company), periodic signed checkpoints anchored outside the database (WORM storage, separate log service or signed export), a verification command that detects gaps and edits, restriction of audit-table ownership/DDL to a separate role, and an export for auditors. Identity administrators cannot view company audit trails.
2. **Branch/warehouse validation.** Register a real `IScopeTargetValidator` when those entities exist, and revoke or flag assignments whose branch/warehouse is later deleted.
3. ~~Identity-side administrator loss.~~ **Resolved in Phase 2.3C-1**: disabling or locking the last usable Company Administrator (or the last identity administrator) through the identity API is refused, serialized with access changes. See "Company administrator continuity".
4. A company user holding `access.members.create` can confirm that a usable login exists (inherent to add-by-login). Add rate limiting/alerting for bulk probing together with the 2.3A throttling work.
5. No per-request authorization cache by design (a few small queries); measure before optimizing and keep revocation immediate.
6. Role/permission changes are validated against the catalog compiled into the running Backend; rolling-upgrade skew between instances with different catalogs is not handled.
7. Segregation of duties and approval workflows are not part of this phase.
8. The remaining Phase 2.3A limitations (no MFA/reset/breached-password check, desktop credential-store handling, HTTPS-only cookie) are unchanged; the lockout denial of service and forwarded-header limitations were addressed in Phase 2.3C-1 (see its residual risks above).
