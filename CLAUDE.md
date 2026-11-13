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
