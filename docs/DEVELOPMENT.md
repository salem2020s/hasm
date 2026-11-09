# HASM — Development Guide

Scope of the current foundation: Backend health endpoint, shared React frontend (Arabic RTL / English LTR), the Tauri 2 Windows shell, and (Phase 2.1) the system database foundation with a minimal company registry. No business modules exist yet.

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

**Central vs. company data:** central (`Hasm_System`): company id, opaque physical database name (immutable, unique), provisioning state, timestamps. Later central phases: licensing, users/roles, global parties. Exclusively per-company: chart of accounts, ledger, documents, inventory, local parties, branches, warehouses, settings, attachments metadata. Company display names/tax data are deliberately not in the registry yet (to be decided with company management). The registry has no HTTP endpoints until authentication/authorization exist.

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

**CI** (`.github/workflows/backend.yml`, job `postgres-integration`, Ubuntu): starts an ephemeral `postgres:16` service container, creates a restricted `hasm_test_admin` role (`CREATEDB`, no superuser/role/replication) with a random per-run password that is masked in logs and never committed, runs the tests with `HASM_REQUIRE_PG_TESTS=1`, then verifies from the TRX results that exactly 27 database tests ran and all passed (skipped or missing tests fail the job). Non-TLS (`SSL Mode=Disable`) exists only in that disposable job's loopback connection string; the only literal credential in the workflow is the throwaway bootstrap password of that job-local container. The `build-test` job still runs these tests as skipped.

## Checks
```
dotnet test src/backend/Hasm.slnx
cd src/frontend && npm run typecheck && npm test && npm run build
cd src/desktop && npx tauri build --debug --no-bundle
```
CI runs the same checks (`.github/workflows/`).

## Known placeholders and limitations
- Desktop icons in `src/desktop/src-tauri/icons/` come from `assets/branding/hasm-icon.png` (`icon.ico` is the supplied `hasm-icon.ico`, byte-identical). The source raster is an upscaled 1024x1024 extraction from a 449x204 reference, so small icon sizes may look soft; replace with a true vector/high-resolution master before a commercial release (`npx tauri icon <square.png>`, then restore the approved `.ico`). Tauri does not always re-embed changed icons: touch `src-tauri/build.rs` before rebuilding.
- The Desktop CSP `connect-src` hardcodes the local development API origin (`https://localhost:7241`). A per-deployment mechanism for the API address is still to be decided.
- The Desktop app reaches the development backend only after the ASP.NET dev HTTPS certificate is trusted (`dotnet dev-certs https --trust`); certificate validation is never bypassed.
- The GitHub Actions workflows (`.github/workflows/`) have not run yet; they are first exercised by the pull request.
- The Desktop app has not been exercised end to end against a live backend.
- PostgreSQL integration tests are executed only by the CI `postgres-integration` job (no PostgreSQL in the managed development environment); they pass only once that job has run green.
- TypeScript 7 (`^7.0.2`) is used; it is a recent major version.
- Dev-server file access (`server.fs.allow` in `apps/web/vite.config.ts`) is limited to the frontend workspace and `assets/branding/`; keep it narrow.
