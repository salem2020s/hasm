# HASM Codex entry point

Read `CLAUDE.md` for the binding project policies; this file supplements it. The approved `docs/SRS.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, and dependency-ordered `docs/ROADMAP.md` are authoritative for their respective subjects. Read only relevant sections. Roadmap documents are generated; see `tools/roadmap/README.md` before editing them.

At resumption reconcile the current branch, `docs/AUTONOMOUS-PROGRESS.md`, `docs/AUTONOMOUS-STATE.json`, and GitHub; checkpoints are evidence, not proof of current remote state. Preserve unfinished work and record concise, truthful handoffs. Use the project skills in `.agents/skills/` when applicable: `hasm-resume`, `hasm-testing`, `hasm-pr`, and `hasm-postgres`.

During implementation use targeted searches and focused tests. Before merging preserve all applicable full regression, independent review, and exact-head CI gates in `CLAUDE.md` and `.github/required-checks.json`. Do not weaken tests or database safety. Use existing scripts and avoid duplicate analysis or reviews; compact context when useful while retaining decisions, evidence, and outstanding work.
