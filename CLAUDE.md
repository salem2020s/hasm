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

