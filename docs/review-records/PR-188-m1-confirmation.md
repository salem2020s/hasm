# Independent review record

* **Pull request:** #188 (migration-guard hardening). Delta confirmation 05a5ce9..5819d8b of the final review `PR-188-final-review.md` (record e76376e, request changes for M1). This is the last gate before the owner-authorized merge (owner decisions of 2026-11-22, merge step 6).
* **Head SHA reviewed (full 40 characters):** 5819d8bab815e26ee53b22de0b5094dc49afc6fb
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author sonnetUI185); model family Claude Opus. Prompt (summary): "confirm:
  * M1 (`\bSECURITY\s+DEFINER\b`, case-insensitive, after comment stripping): try to slip past it with an identifier like `security_definer`, a comment between the words, a dollar-quoted body, `ALTER FUNCTION ... SECURITY DEFINER`, and unicode or escape tricks; check that every migration on main still loads and that the mutation proof holds;
  * L1 (internal init on Set) and L2 (runbook wording);
  * that L3 and L4 are recorded as follow-ups."
* **Scope read:**
  * The full delta `git diff 05a5ce9 5819d8b` (6 files):
    * `MigrationGuard.cs` (+1: the neutralizer);
    * `MigrationScript.cs` (`Set { get; internal init; }`);
    * `MigrationGuardTests.cs` (+47: a theory of refused spellings, a theory of accepted forms, "refused by the SECURITY DEFINER rule itself", and "no migration mentions it in code");
    * `docs/MIGRATION-GUARD.md` (the runbook step 2; "What the guard refuses today" and "Consequence");
    * `docs/DEVELOPMENT.md` (the neutralizer list);
    * my record e76376e, merged into the branch.
  * The PR body's follow-up table.
* **Checks run (commands and results):**
  * **Bypass probe** (local unit test, not committed). Run through `cpu-gate.sh build`, together with MigrationGuardTests, MigrationTriggerSwitchAllowlistTests and MigrationAccountPolicyTests: 306/306 passed.

    **Refused**, each as expected:
    * `SECURITY/**/DEFINER` (a comment with no spaces);
    * `SECURITY -- x⏎DEFINER`;
    * a nested block comment between the words;
    * a tab, a form feed or CRLF between the words;
    * mixed case;
    * the clause after a `$body$` body;
    * a function whose dollar-quoted body creates a definer function;
    * `ALTER FUNCTION f() SECURITY DEFINER` and `ALTER ROUTINE …`;
    * `CREATE OR REPLACE … SECURITY DEFINER`;
    * `EXTERNAL⏎SECURITY⏎DEFINER` on a procedure;
    * the words inside a string literal;
    * `SECURITY U&"DEFINER"`.

    The lexer removes comments and keeps the text of strings and dollar-quoted bodies for the scan, and `U&"…"` identifiers are refused outright.

    **Accepted**, each as expected:
    * a column `security_definer`;
    * columns `securitydefiner` and `definer_security`;
    * `SECURITY INVOKER`;
    * the words in a `--` comment;
    * the words in a `/* */` comment.

    PostgreSQL treats only space, tab, LF, CR and FF as whitespace between keywords, and keywords cannot be quoted or escaped, so no spelling PostgreSQL would accept as the clause escapes the rule. A non-ASCII space would be a syntax error in PostgreSQL. The only change of a function's security mode after creation is `ALTER FUNCTION/ROUTINE … SECURITY DEFINER`, already refused by its own rule. A catalog update would need `pg_proc` (refused) and a superuser.
  * **Mutation:** deleting the neutralizer line made 24 tests fail:
    * all 11 cases of `SECURITY_DEFINER_is_refused_in_every_spelling`;
    * `Every_SECURITY_DEFINER_spelling_is_refused_by_the_SECURITY_DEFINER_rule_itself`;
    * 12 of my probe's 15 refusal cases: every case except the three that other rules also refuse (`ALTER FUNCTION`, `ALTER ROUTINE`, the `U&` lexer rule).

    The file was reverted afterwards.
  * **Every migration on main still loads:** `No_migration_of_either_set_mentions_SECURITY_DEFINER_in_code` runs `Validate` over both embedded sets and passed. No migration file differs from main. The words occur only in comments of company 0034.
  * **L1:** `Set { get; internal init; }`. The only writers are the loader (`MigrationScript.cs:58`) and two test files in the assemblies named by `InternalsVisibleTo` (`Hasm.Infrastructure.Tests`, `Hasm.Database.IntegrationTests`). The build succeeded.
  * **L2:** runbook step 2 now says the messages name no database, and states which database to use for `migrate-companies` (the company on the `Company <id>: Failed [migration]` line), for `provision-company` and for `init-system-db`.
  * **L3 and L4:** both are recorded as FOLLOW-UP (not in this PR) in the PR body's table:
    * L3: a committed real-superuser test against the bootstrap superuser of the disposable cluster;
    * L4: `backup-recovery-test` under the non-superuser account.
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan); a skipped duplicate of each also ran.
  * **Mergeability:** `mergeable_state` clean; base 6ee0653 equals origin/main.
  * **Files:** no protected file or workflow file changed in the delta, and the PostgreSQL registry is unchanged (the new tests are unit tests).
  * The author reports a full local PostgreSQL suite of 1776/1776 on this head. I did not re-run it.
  * The bypass probe was removed afterwards, and `git status` was clean.
* **Findings:** 0 Critical, 0 High, 0 Medium, 2 Low (R1, R2). M1, L1 and L2 of the final review are resolved; L3 and L4 are recorded as follow-ups. Each finding is listed below.
  * **Resolved M1.** `SECURITY DEFINER` is refused in every spelling PostgreSQL accepts, in code, bodies and strings, with a mutation proof. Every migration on main still loads. The documentation now matches the enforcement ("No migration can create a `SECURITY DEFINER` function today: the guard refuses it").
  * **Resolved L1.** `internal init`.
  * **Resolved L2.** The runbook wording.
  * **Low R1: an attribution in `docs/DEVELOPMENT.md`.** The neutralizer list says "(owner decision 2026-11-22: no `SECURITY DEFINER` function can be created by a migration)". The owner's text asks that the SECURITY DEFINER / search_path limitation be documented and that any future exception be narrowly specified, reviewed and tested. The refusal itself implements that request, on this review's recommendation; it is not a separate owner decision. Reword as, for example, "(the documented SECURITY DEFINER limitation, owner direction 2026-11-22; docs/MIGRATION-GUARD.md)" in a later documentation change. This does not block the merge.
  * **Low R2: where L3 and L4 are tracked.** The two follow-ups are recorded in the PR body and in the review records. After the merge, carry them into the #188 Lows of `docs/INTEGRATION-STATUS.md`, with the other #188 follow-ups (L12 to L15), so they survive in the repository.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the following two items.
  * `tools/verify/verify-head.js` from main: GitHub GraphQL is blocked in this session (HTTP 403); the check runs were read by REST.
  * The full local PostgreSQL suite on 5819d8b: the author's run of 1776/1776, not repeated. Hosted postgres-integration passed on this exact head.
* **Verdict:** approve
