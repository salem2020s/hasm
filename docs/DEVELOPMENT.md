# HASM — Development Guide

Scope of the current foundation: Backend health endpoint, shared React frontend (Arabic RTL / English LTR), the Tauri 2 Windows shell, (Phase 2.1) the system database foundation with a minimal company registry, and (Phase 2.3A) central identity with local authentication. No business modules exist yet.

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

**CI** (`.github/workflows/backend.yml`, job `postgres-integration`, Ubuntu): starts an ephemeral `postgres:16` service container, creates a restricted `hasm_test_admin` role (`CREATEDB`, no superuser/role/replication) with a random per-run password that is masked in logs and never committed, runs the tests with `HASM_REQUIRE_PG_TESTS=1`, then verifies from the TRX results that exactly 100 database tests ran and all passed (skipped or missing tests fail the job). Non-TLS (`SSL Mode=Disable`) exists only in that disposable job's loopback connection string; the only literal credential in the workflow is the throwaway bootstrap password of that job-local container. The `build-test` job still runs these tests as skipped.

## Identity and local authentication (Phase 2.3A)
Central identities live in `Hasm_System` (system migration `0003_users_and_sessions.sql`, opaque physical names mapped in `SystemSchema.User/Session/LoginThrottle/UserCompanyAccess`). Scope: login/logout, sessions, throttling, administrative user creation and the initial administrator. **Not** in scope: RBAC, MFA, AD, licensing seats, a login UI.

**Data model:** `User` (stable `uuid`, normalized unique login, display name, password hash, state `Active|Disabled|Locked`, `IsIdentityAdministrator`, timestamps; rows are never deleted and id/login are immutable via trigger), `Session` (server-side; SHA-256 of a 256-bit random token, client kind, last-seen, idle and absolute expiry, revocation time/reason), `LoginThrottle` (SHA-256 key digests, failure counters, block-until), `UserCompanyAccess` (explicit user-to-company link with FK to the company registry; **no code creates rows yet**, roles/permissions arrive with RBAC). Authentication never reads or grants company access; the status response contains no company information.

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

**Runtime identity and privileges:** the API connects with `Hasm__Database__Username/Password` (never the provisioning admin account; if the runtime account is unusable the API returns 503 rather than falling back). The DBA must grant it, on the system database: `SELECT, INSERT, UPDATE` on the `User` table; `SELECT, INSERT, UPDATE, DELETE` on the `Session` and `LoginThrottle` tables; `SELECT` on `UserCompanyAccess` (physical names are in `SystemSchema`; grants are not automated). The runtime role needs no DDL rights. API connections to PostgreSQL are pooled; all other TLS rules above still apply.

**Tests:** unit tests (login normalization, password policy/hashing, tokens, options) run everywhere. PostgreSQL tests (`IdentityMigrationTests`, `UserCreationTests`, `LoginTests`, `SessionTests`, `BruteForceProtectionTests`, `AuthApiTests`, same safety gate as above) use an injectable clock instead of sleeping and drive the real API in-process via `WebApplicationFactory`. `AuthWithoutDatabaseTests` (no database) covers anonymous access, CSRF header and generic errors.

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
3. No MFA, password reset, breached-password check or self-service recovery; no company-level authorization (RBAC is a later phase); the Desktop client credential-store handling is not implemented.
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
