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
- UI standard (owner-approved, permanent): screens with several meaningful groups of related information are evaluated for tabs using the single shared `Tabs` component (`packages/ui`); simple screens get none; group by business meaning; no nested tabs unless necessary, no placeholder tabs; switching tabs never saves, discards or resets input; tab-level error/unsaved indicators; record identity and Save/Cancel stay visible from every tab; Arabic RTL and English LTR, keyboard accessible, identical on Web and Tauri. Details: `docs/PHASE-UI-1.md` (Permanent UI standard). Existing screens adopt it incrementally when next revised.
## Mandatory GitHub Persistence and Autonomous Development Policy (owner-approved)
- All development work is regularly persisted to the private GitHub repository (canonical remote) to protect against shutdowns, hardware failures and session loss. This applies to every approved phase.
- Branches: only `main` (protected, owner-approved merged code) and `feature/<phase-name>` (the active development branch). Backup branches are permanently discontinued (owner decision); none is created or maintained.
- The owner authorizes, without asking each time: frequent logical local commits; regular pushes to the active feature branch without asking permission, after every verified slice and whenever substantial progress needs preserving; creation of the feature branch. Do not wait until the end of a phase to push.
- Verified slices are pushed as normal commits. Interrupted or failing work (failing tests, unresolved issue, long unfinished slice) is pushed to the same feature branch as clearly labelled work-in-progress commits (`wip(...)` prefix, stating what is unfinished); WIP checkpoints are never presented as verified. Never weaken or disable tests to create a checkpoint.
- Never: push to `main` directly (changes reach `main` only through a merged Pull Request); force-push or rewrite remote history; delete the active feature branch; bypass GitHub Actions or branch protection; deploy or release; start an unapproved phase. Never commit secrets, credentials, keys, production data, test databases or sensitive temporary files (review staged files first).
- Continue through all approved slices without routine authorization requests; stop only for consequential unresolved accounting, security, architecture, licensing or unsafe-migration conflicts, and preserve safe work remotely before reporting a blocker.
- At the start of every resumed session: inspect branch and working tree, fetch remote, identify the latest verified commit and any newer WIP commits on the feature branch, recover incomplete work without discarding commits, and continue from the most recent safe state without reimplementing existing functionality.
- At the end of a phase: run the full regression suites, verify the final committed tree, push the feature branch, give the Pull Request URL and results, and continue under the Autonomous merge policy below (no stop for owner approval).
- Pull Requests (owner-approved, permanent): GitHub CLI `gh` is installed (winget `GitHub.cli`) and authenticated once by the owner through the browser login (`gh auth login`); tokens are never requested, printed, logged or committed. For every approved phase: create the feature branch from the approved `main`; build the first meaningful buildable checkpoint, run the relevant tests, commit and push; then automatically open ONE Draft PR to `main` with `gh pr create --draft --base main --head <branch>` and an explicit title and description. Before creating, check for an existing open PR for the branch (`gh pr list --head <branch> --state open`); never create duplicates; update the existing PR description with progress, tests and remaining limitations. Continue through all approved slices without stopping after PR creation; commit and push after every verified milestone; let GitHub Actions run on each update and fix CI failures without weakening security or tests. After the phase is complete: run the full regression suites, push the final commit, mark the PR ready for review and report the PR URL, latest commit, test results and Actions status. No routine permission is needed for commits, pushes, creating/updating PRs, authorized tests or ordinary CI fixes. If `gh` authentication or permissions block PR creation, keep pushing checkpoints to the feature branch and report the one-time setup need. Never enable GitHub auto-merge, and never bypass Actions or required reviews; merging follows the Autonomous merge policy below.
- Autonomous CI monitoring and repair (owner-approved, permanent, all future approved phases): after each push to the feature branch, create or update the existing Draft PR and monitor GitHub Actions with the authenticated `gh` (`gh pr checks`, `gh run view --log-failed`, `gh pr view`). If a check fails, retrieve its logs, find the root cause, implement the correction, run the affected tests, commit and push again; repeat until all required checks pass. Do not stop merely because a check failed, and keep developing the remaining approved slices without waiting for the owner to inspect CI. Never weaken tests, disable checks, reduce expected test counts or bypass security controls to make CI pass. If a failure cannot be safely resolved, report the blocker and preserve the work on the feature branch. When the whole phase is complete and all required checks pass, run `gh pr ready` and report the PR URL, final commit and test results. Merge only under the Autonomous merge policy below; the owner no longer has to approve routine merges. This cycle is mandatory for every future PR, including documentation-only PRs, whenever checks are applicable (path-filtered workflows may not run on a docs-only change: then state that no checks were triggered). Never report a task as finished while required checks are pending, running or unverified; if Actions is unavailable or blocked, report the specific blocker.
- Database safety is unchanged: no unapproved migrations or destructive tests on port 5432; destructive tests only on the isolated 5433 cluster; credentials, keys and database files are never committed.

## Autonomous merge policy (owner-approved; supersedes every earlier "stop before merging" / "the owner merges" sentence above)
- Routine development Pull Requests (feature phases, slices, documentation, fixes) may be merged by the agent WITHOUT asking the owner, and the agent then proceeds to the next phase of `docs/ROADMAP.md` in dependency order, but only when ALL of these gates hold on the EXACT head SHA being merged (any new commit, including merging `main` into the branch to resolve a conflict, changes the SHA and every gate is repeated):
  1. the approved scope of the slice is complete and no Critical or High defect is known (one severity scale: Critical, High, Medium, Low; a reviewer's "Blocker" counts as Critical);
  2. every mandatory check named in `.github/required-checks.json` for the paths the PR touches is green on that SHA, verified with `node tools/verify/verify-head.js --pr <n> --expect-sha <sha>` run from `main`'s copy of the tool; for a documentation-only PR the always-on verification jobs are the required checks; the merge command is `gh pr merge --merge --match-head-commit <sha>`;
  3. the PR is mergeable without conflicts;
  4. an adversarial self-review AND an independent review exist and are recorded (`docs/review-records/TEMPLATE.md`): the reviewer is a separate agent or session, NOT the author's own context, receives the FULL diff against `origin/main` (never a chosen slice), its prompt and model family are noted in the record, and every Critical and High finding is fixed. Medium findings that are not fixed are listed in the record with a follow-up. If an independent review cannot be obtained, the gate is unsatisfied: the PR stays open, the blocker is reported, and no dependent phase is started; a review is never fabricated.
- Never self-merged without a recorded explicit owner instruction in the same conversation: a PR that changes this `CLAUDE.md`, the merge/review gates, `.github/required-checks.json`, `.github/secret-scan-allow.json`, `.github/CODEOWNERS`, `.github/workflows/verification.yml`, `tools/verify/**`, or that removes, renames, skips or weakens any other workflow job or test. (Raising an expected test count and adding tests are normal changes.)
- Required reviews, branch protection (where the plan provides it), GitHub Actions and every security, accounting-integrity and test gate are NEVER bypassed, weakened, disabled or worked around; expected test counts are never reduced; settings and rulesets are never changed to circumvent a check.
- OPEN decisions of `docs/ROADMAP.md` (OD-n, T-n) stay with the owner: a slice that needs one is recorded as blocked and the agent continues with the next phase that does not depend on it; the agent never treats "deferred" as its own decision and never changes approved requirements or accounting policy.
- After every merge: verify the merge commit on `main`, then record the result in `docs/AUTONOMOUS-PROGRESS.md` and `docs/AUTONOMOUS-STATE.json` so they agree with GitHub (these edits reach `main` only through a Pull Request, in the next phase's branch or a dedicated documentation PR). Unverified results are recorded as NOT VERIFIED.
- Still forbidden without a new explicit owner instruction: production deployment or release, pushing to `main` directly, force-push or history rewriting, deleting a feature branch that has unmerged work, and starting a phase that is not in `docs/ROADMAP.md`.

## Autonomous development and integration principles (owner-approved 2026-11-20; adds to, never weakens, every rule above)
- **Standing authorization.** Routine, reversible engineering decisions are made without asking: choosing the next dependency-ready task, implementing, testing, fixing review findings, creating feature branches, worktrees, commits and Draft PRs, pushing to feature branches, running tests and CI, delegating independent reviews. Do not ask "should I continue", "may I start the next slice", "should I fix these findings" or "should I run these tests": the answer is yes. A report is not a stopping point; continue with the next safe task.
- **Only these need the owner:** changes to protected security policy or gates (`tools/verify/**`, `.github/workflows/verification.yml`, `.github/required-checks.json`, policy JSON, secret-scan allow-list, CODEOWNERS, this file); material accounting-policy changes not covered by an approved decision; weakening a database safeguard; irreversible production operations. Record such a decision ONCE in `docs/INTEGRATION-STATUS.md` (owner-decision register), keep the affected behavior disabled or fail-closed, and switch to independent work; never re-ask the same question. **Routine development PRs must be merged automatically whenever all mandatory review, security, accounting-integrity, dependency, and exact-head CI gates pass (owner directive 2026-11-20); owner approval is no longer required for each routine merge.** This supersedes the temporary 2026-11-20 instruction requiring explicit approval for every merge. Automatic merge applies only when EVERY gate of the Autonomous merge policy holds; if any gate is unsatisfied or unverifiable, do not merge. The gates of the Autonomous merge policy (exact-head checks via `verify-head.js` from `main`, independent full-diff review, no Critical/High, mergeable, merge with `--match-head-commit`) are unchanged, and the never-self-merged list of that policy governs in full, including, without limitation, everything it lists (protected files and gates, `.github/required-checks.json`, CODEOWNERS, validation-only, blocked or superseded PRs) still requires an explicit owner instruction.
- **Integration.** `main` is the only integration branch. Stacked PRs say so in their description and contain their prerequisites; migrations are contiguous per database (the loader refuses gaps), so company and system numbers follow the merge order and are allocated in `docs/INTEGRATION-STATUS.md`; generated names are never reused. When a PR merges, every dependent branch is updated by merging `main` into it (never rebase published work, never force-push, never delete or overwrite unmerged work or another worktree), the PostgreSQL registry and pinned migration counts are re-reconciled, and exact-head checks are repeated. A security gate that fails is never skipped, disabled or worked around; if it fails identically on every branch, resolve it through the approved security process.
- **Per task.** Implement, test, run an independent review (separate agent; full diff; recorded in `docs/review-records/`), fix every Critical, High and Medium finding (Lows fixed or recorded), push, verify hosted CI on the exact commit, update the handoff, continue.
- **Durable handoff and recovery.** `docs/INTEGRATION-STATUS.md` (open PRs with exact heads and CI, dependency graph, migration allocation, merge sequence, owner-decision register, next task) and `docs/AUTONOMOUS-PROGRESS.md` are kept current after every merge or milestone. After a context reset or a new session, recover state from the repository and GitHub (`git fetch`, `gh pr list`, `gh pr checks`), continue from the latest verified commit, and never redo completed work.

## Standing autonomous decision-making authorization (owner-approved 2026-11-21, D-048; adds to, never weakens, every rule above)
- **Routine decisions are the agents' own.** Technical, architectural, implementation and workflow decisions (naming, component design, configuration defaults, internal workflows, routine technical alternatives, and approved-phase design choices the SRS does not prescribe) are made autonomously across all Release 1 phases and by all agents. Do not ask repetitive multiple-choice questions about them.
- **How to decide.** When several valid approaches exist, evaluate them against the approved SRS and decision register, accounting correctness and data integrity, security and reliability, maintainability and scalability, established industry practice, and user experience and operational simplicity. Prefer a recommended option when it is consistent with approved requirements, without accepting it blindly. Where the specifications are silent, make a reasonable, configurable assumption.
- **Record it.** Significant autonomous decisions, with rationale and the alternatives considered, are recorded in `docs/DECISIONS.md` (the existing register).
- **Escalate only when** a decision conflicts with an already approved requirement; changes fundamental accounting, valuation or financial-reporting policy; weakens database integrity, security controls or protected CI/release gates; introduces irreversible data changes or significant legal/compliance implications; or changes licensing, commercial scope or an explicitly owner-reserved policy. Present ONE consolidated recommendation package (preferred solution and its implications), not a series of questions, and mark only the affected phase blocked while other approved, independent work continues.
- **Unchanged.** The never-self-merged list, the protected files, the merge gates (independent full-diff review, exact-head checks, no Critical/High), database safety, and the OPEN decisions of `docs/ROADMAP.md` stay as written above. Tests and safeguards are never weakened to obtain a pass; decisions already approved in the SRS or register are not re-asked.
