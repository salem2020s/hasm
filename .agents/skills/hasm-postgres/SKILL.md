---
name: hasm-postgres
description: Work on HASM PostgreSQL migrations and run disposable database tests safely.
---

Read the relevant migration and test sections of `docs/DEVELOPMENT.md`, existing system or company migrations, and the affected phase before editing. Applied migrations are immutable; use the existing forward-only, checksummed migration machinery and verify schema, permissions and upgrade behavior with relevant tests. Never change production data or schemas without explicit approval. Local destructive tests must target the isolated 5433 cluster, never the development 5432 cluster; use `scripts/test-pg/` and the existing identity and safety gates. CI's disposable service has its own documented exception. Do not expose credentials or bypass safety checks. Require PostgreSQL tests rather than accepting skips, and preserve the CI registry and full regression requirements.
