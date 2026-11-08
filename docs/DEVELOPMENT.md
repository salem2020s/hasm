# HASM — Development Guide

Scope of the current foundation: Backend health endpoint, shared React frontend (Arabic RTL / English LTR), and the Tauri 2 Windows shell. No business modules or database schemas exist yet.

## Prerequisites
- .NET SDK 10 (`dotnet --list-sdks`)
- Node.js 24 + npm 11
- Rust stable (`x86_64-pc-windows-msvc`) + Visual Studio Build Tools with "Desktop development with C++"; WebView2 (included in Windows 11)
- Tauri 2 is a project dependency (`src/desktop/package.json`); nothing is installed globally.
- PostgreSQL is not required yet.

## Structure
```
assets/branding/                Official branding assets, see BRANDING.md (never modify or regenerate)
src/backend/                    Hasm.slnx, Hasm.Api (ASP.NET Core)
src/frontend/                   npm workspace: apps/web, packages/ui, packages/api-client
src/desktop/                    Tauri 2 shell (src-tauri) loading the shared frontend
tests/integration/              Hasm.Api.Tests (xUnit + WebApplicationFactory)
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
- The Desktop app has not been exercised end to end against a live backend. PostgreSQL is not used yet.
- TypeScript 7 (`^7.0.2`) is used; it is a recent major version.
- Dev-server file access (`server.fs.allow` in `apps/web/vite.config.ts`) is limited to the frontend workspace and `assets/branding/`; keep it narrow.
