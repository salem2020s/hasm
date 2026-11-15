\## Project: Hasm

Hasm is an accounting and inventory management software project.



\## Source of Truth

\- The approved SRS is the authoritative source for functional requirements.

\- Do not invent accounting rules, business workflows, or architecture decisions.

\- Distinguish approved requirements from proposals.

\- If requirements conflict, report the conflict before implementing.



\## Development Workflow

\- Do not begin implementation until the SRS receives explicit final approval.

\- Work on one clearly defined task at a time.

\- Read only documentation and source files relevant to the task.

\- Propose an implementation plan before substantial changes.

\- Avoid unrelated modifications.

\- Validate accounting and inventory invariants with automated tests.

\- Never change production data or database schemas without explicit approval.



\## Token Efficiency

\- Keep responses concise.

\- Avoid loading the entire SRS into context unless necessary.

\- Do not repeat previously approved decisions.

\- Use Skills, MCP tools, and subagents only when their benefit justifies the overhead.

\- Maintain concise progress notes in project files rather than relying on long chat history.



\## Completion

\- Summarize changes, tests performed, and outstanding risks.

\- Do not claim tests passed unless executed.


\## Mandatory Shared Web/Desktop Frontend Architecture

\- HASM is one application with one shared React + TypeScript frontend, delivered by two clients: the Web app (browser) and the Windows Desktop app (Tauri 2). This applies to every phase and module.
\- Implement all business functionality once in the shared frontend (screens, forms, tables, dialogs, navigation, validation, translations, state, components) on the approved UI-1 design system. Never build separate Web and Windows implementations or accept platform-specific feature gaps.
\- Platform adapters are allowed only where genuinely required (cookie vs bearer-token authentication, native window management, secure desktop storage/OS integration, printing/filesystem, startup and packaging). They must keep equivalent business behavior, authorization, validation and UX, and stay small and isolated.
\- Both clients use the same ASP.NET Core APIs and server-side rules. Neither may bypass RBAC and company-scoped authorization, Trial/paid license limits, audit, optimistic concurrency, validation or multi-company isolation. Security-critical logic is never frontend-only.
\- For every feature: build the UI once; make it available through Web and Tauri; add shared component/interaction tests; run frontend typecheck and production build; run the Tauri compilation/build check; run the required backend and integration tests; confirm no platform-specific gap. Routine manual visual review may wait for the final validation milestone; automated Web/Desktop compatibility checks may not be deferred.
\- This section adds to, and never weakens, the security, testing and database-safety rules above.
## Mandatory GitHub Persistence and Autonomous Development Policy (owner-approved)
- All development work is regularly persisted to the private GitHub repository (canonical remote) to protect against shutdowns, hardware failures and session loss. This applies to every approved phase.
- Branches: only `main` (protected, owner-approved merged code) and `feature/<phase-name>` (the active development branch). Backup branches are permanently discontinued (owner decision); none is created or maintained.
- The owner authorizes, without asking each time: frequent logical local commits; regular pushes to the active feature branch without asking permission, after every verified slice and whenever substantial progress needs preserving; creation of the feature branch. Do not wait until the end of a phase to push.
- Verified slices are pushed as normal commits. Interrupted or failing work (failing tests, unresolved issue, long unfinished slice) is pushed to the same feature branch as clearly labelled work-in-progress commits (`wip(...)` prefix, stating what is unfinished); WIP checkpoints are never presented as verified. Never weaken or disable tests to create a checkpoint.
- Never: push to `main`; merge automatically; force-push or rewrite remote history; delete the active feature branch; bypass GitHub Actions or branch protection; deploy or release; start an unapproved phase. Never commit secrets, credentials, keys, production data, test databases or sensitive temporary files (review staged files first).
- Continue through all approved slices without routine authorization requests; stop only for consequential unresolved accounting, security, architecture, licensing or unsafe-migration conflicts, and preserve safe work remotely before reporting a blocker.
- At the start of every resumed session: inspect branch and working tree, fetch remote, identify the latest verified commit and any newer WIP commits on the feature branch, recover incomplete work without discarding commits, and continue from the most recent safe state without reimplementing existing functionality.
- At the end of a phase: run the full regression suites, verify the final committed tree, push the feature branch, give the Pull Request URL and results, and STOP before merging; the owner approves the merge.
- Pull Requests (owner-approved, permanent): GitHub CLI `gh` is installed (winget `GitHub.cli`) and authenticated once by the owner through the browser login (`gh auth login`); tokens are never requested, printed, logged or committed. For every approved phase: create the feature branch from the approved `main`; build the first meaningful buildable checkpoint, run the relevant tests, commit and push; then automatically open ONE Draft PR to `main` with `gh pr create --draft --base main --head <branch>` and an explicit title and description. Before creating, check for an existing open PR for the branch (`gh pr list --head <branch> --state open`); never create duplicates; update the existing PR description with progress, tests and remaining limitations. Continue through all approved slices without stopping after PR creation; commit and push after every verified milestone; let GitHub Actions run on each update and fix CI failures without weakening security or tests. After the phase is complete: run the full regression suites, push the final commit, mark the PR ready for review and report the PR URL, latest commit, test results and Actions status. No routine permission is needed for commits, pushes, creating/updating PRs, authorized tests or ordinary CI fixes. If `gh` authentication or permissions block PR creation, keep pushing checkpoints to the feature branch and report the one-time setup need. Never merge a PR, enable auto-merge, or bypass Actions or required reviews: the owner merges manually after inspecting CI.
- Autonomous CI monitoring and repair (owner-approved, permanent, all future approved phases): after each push to the feature branch, create or update the existing Draft PR and monitor GitHub Actions with the authenticated `gh` (`gh pr checks`, `gh run view --log-failed`, `gh pr view`). If a check fails, retrieve its logs, find the root cause, implement the correction, run the affected tests, commit and push again; repeat until all required checks pass. Do not stop merely because a check failed, and keep developing the remaining approved slices without waiting for the owner to inspect CI. Never weaken tests, disable checks, reduce expected test counts or bypass security controls to make CI pass. If a failure cannot be safely resolved, report the blocker and preserve the work on the feature branch. When the whole phase is complete and all required checks pass, run `gh pr ready` and report the PR URL, final commit and test results. Never merge automatically; the owner only reviews the completed PR and performs the final merge.
- Database safety is unchanged: no unapproved migrations or destructive tests on port 5432; destructive tests only on the isolated 5433 cluster; credentials, keys and database files are never committed.
