---
name: hasm-testing
description: Select focused HASM tests and preserve required regression coverage.
---

Identify affected behavior and existing tests with targeted searches. Run focused tests during implementation, including accounting, security and inventory invariants as relevant. Use `docs/DEVELOPMENT.md`, the applicable phase, and `.github/workflows/` for commands and coverage; use `hasm-postgres` for database tests. Before merging run the full regression suites required by `CLAUDE.md` and the phase, plus exact-head Actions checks per `hasm-pr`. Preserve shared Web/Tauri checks when applicable. Never reduce counts, skip mandatory tests, weaken assertions or present partial runs as full success. Record actual counts, skips, failures and environment limitations; diagnose failures without rerunning unrelated suites unless warranted.
