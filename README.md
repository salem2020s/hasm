# HASM — Accounting & Inventory Management

HASM (حسم) is a modular accounting and inventory management system.

## Technology Stack
- Frontend: React + TypeScript (shared by web and desktop)
- Desktop: Tauri 2 (Windows)
- Backend: ASP.NET Core (C#)
- Database: PostgreSQL (accessed only through the Backend API)

## Repository Structure
- `docs/` — Requirements, architecture and [development guide](docs/DEVELOPMENT.md)
- `assets/branding/` — Official HASM logo
- `src/backend/`, `src/frontend/`, `src/desktop/` — Application source
- `tests/` — Automated tests

## Project Status
SRS v1.0 approved. Project foundation in place; business modules not started.

## Development Policy
- The `main` branch contains reviewed changes.
- New work is developed on feature branches.
- Changes are integrated through pull requests.
