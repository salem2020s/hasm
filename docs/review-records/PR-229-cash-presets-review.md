# Independent review record

* **Pull request:** #229 (X4-PRESET-ROLE: the two dedicated cash and bank account presets with offline grant and revoke commands; Draft; author sonnetUI185). Reviewed against the merged plan `docs/X4-PRESET-ROLES-PLAN.md` (records `PR-226-x4-preset-plan-review.md` and `PR-226-x4-preset-plan-delta-confirmation.md`) and owner decision D-254 (Option A).
* **Head SHA reviewed (full 40 characters):** 41bfd8eb3018168a9d90699fbb51f9646e8be132
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author); model family Claude Opus. Prompt (summary): "review the FULL diff against origin/main:
  * the migration (provisional system 0036): exactly the two triggers paused, the 27th allowlist entry with a checksum equal to the real file, re-enable, presets immutable again;
  * the presets (Manager: view, create, update, activate; Sensitive Viewer: view and sensitive view; both privileged), with Company Administrator and every earlier preset byte-identical;
  * the offline grant and revoke commands (audited, refused for non-members and non-Ready companies, never by an administrator, no automatic assignment, mandatory MFA);
  * the existing tests that changed (none loosened, exact counts);
  * re-run two of the 7 mutations;
  * cash posting closed (D-214), no protected files, registry 1734."
* **Scope read:**
  * The full diff `origin/main...41bfd8e`: 16 files, +447/−26.
    * Code and tests:
      * `0036_cash_account_presets.sql`, `ApprovedTriggerSwitches.cs`;
      * `PermissionCatalog.cs` (`PresetRoles`), `AccessService.cs` (four offline methods), `Hasm.Admin/Program.cs`;
      * the new tests `CashPresetRolesTests.cs` (three PostgreSQL classes) and `CashPresetRoleTests.cs` (unit);
      * the changed tests `AccessApiTests`, `AccessDatabaseTests`, `SystemDatabaseTests`, `AccessModelTests`, `CompanySchemaTests` and `MigrationTriggerSwitchAllowlistTests`.
    * Docs and registry: `docs/DEVELOPMENT.md`, `docs/MIGRATION-GUARD.md`, `backend.yml`.
  * For comparison:
    * system migrations 0004 (triggers) and 0030 (the same role-insert shape);
    * `AccessService.GrantPresetApproverOfflineAsync`, `RevokePresetApproverOfflineAsync` and `FindMemberForDelegationAsync`;
    * `PrivilegedAccess.cs`;
    * the PR body.
* **Checks run (commands and results):** all builds and test runs went through `cpu-gate.sh build`.
  * **Allowlist, checked independently** (my Python checker, run against the head's migration files with `git show`):
    * 72 migrations scanned; 27 contain a matched pair;
    * every entry's set, number, name, checksum and switched triggers equal the file;
    * the new entry `0036_cash_account_presets.sql` has checksum `22f90773c2601e80db36ce5282c3cb422746ae7a39bae84f1800ff6322dfb907` (recomputed from the file with CRLF→LF) and triggers `t_359332ac/r_d3408f58`, `t_3e3d4119/r_2601e368`, each disabled once and enabled once.

    Result: PASS.
  * **The migration, read:**
    * both triggers are paused;
    * two role rows are inserted (ids `…0a`, `…0b`, keys `cash-account-manager` and `cash-account-sensitive-viewer`, active, no company), the same shape as 0030;
    * six permission rows: the Manager gets view, create, update and activate; the Viewer gets view and the sensitive view;
    * both triggers are re-enabled;
    * no assignment is made and no other role is touched.
  * **Unit tests** (`dotnet test tests/unit/Hasm.Infrastructure.Tests` filtered to CashPresetRoleTests, AccessModelTests, MigrationTriggerSwitchAllowlistTests, CompanySchemaTests and MigrationGuardTests): 315/315 passed.
  * **PostgreSQL** (`scripts/test-pg/run-isolated.sh … --filter` CashPresetRoles*, SystemDatabaseTests, AccessMigrationTests, RoleTests, AccessApiTests, MigrationGuardPostCheckTests and CashAccount*): 105/105 passed, 0 skipped.
  * **Mutations** (each applied, run and reverted; the tree was clean afterwards):

    | Mutation | Killed by |
    |---|---|
    | `.. CashAccountCodes` dropped from Company Administrator's `Except` list | 3 tests: `Company_Administrator_is_not_widened_and_holds_no_cash_code`, `Migration_seed_equals_the_code_defined_preset_roles`, `Preset_roles_only_reference_catalog_permissions…` |
    | the Sensitive Viewer grant assigns the Manager role | 3 PostgreSQL tests: the viewer API test, the delegation and audit test, the Admin tool test |

    Both were killed. I chose these two from the author's set of seven (the author reports seven of seven killed).
  * **Registry:** 260 classes, sum 1734, title 1734 (main 1727 + 5 + 1 + 1).
  * **Main, protected files, cash posting:**
    * `git merge-tree --write-tree origin/main 41bfd8e` is clean against fed36d8;
    * main has no system 0036 yet;
    * no protected file is in the diff;
    * the catalog has no cash voucher, transfer or posting code, and `Hasm.Infrastructure/Cash` holds only the account master services, so there is no posting path (D-214).
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan); `mergeable_state` is clean.
* **Findings:** 0 Critical, 0 High, 0 Medium, 2 Low (L1, L2), 1 note (N1). Each is listed below.
  * **Low L1: refusal for a non-Ready company is not tested with an existing non-Ready company.**
    * `FindMemberForDelegationAsync` refuses a company that is not Ready (`CompanyUnavailable`), and the revoke query requires Ready too.
    * The new tests exercise only a company id that does not exist.
    * Add one case with an existing Pending or Failed company, for grant and for revoke.
  * **Low L2: the renumbering at the final merge round must move together.** System 0036 is provisional, and PR 227 also uses 0036. Whichever merges second must change, in one change:
    * the file name;
    * the header comment (which changes the checksum);
    * the allowlist entry's Version, Name and Checksum;
    * the pinned list in `MigrationTriggerSwitchAllowlistTests`;
    * the version lists in `SystemDatabaseTests`, `CompanySchemaTests` and `AccessModelTests`;
    * the `CashPresetRolesMigrationTests` lookup;
    * the row in `docs/MIGRATION-GUARD.md`.

    The allowlist test fails loudly if any of these is missed, which is the intended protection. The renumbered head needs an exact-head re-check.
  * **Note N1: MFA is enforced at use, not at grant.** The offline grant does not require the user to be enrolled at grant time. Holding either preset makes MFA mandatory (`MfaMandatoryReason.PrivilegedRole`, tested), which is the D-096 / Phase 4.1 model used for every privileged preset. This is consistent and needs no change.
* **Answers to the specific questions:**
  * **The migration:**
    * it pauses exactly the two triggers, `r_d3408f58` (role) and `r_2601e368` (role permission), which is required because it creates new presets;
    * the 27th entry is correct, with a checksum equal to the real file;
    * both triggers are re-enabled ('O' afterwards, tested);
    * the presets are immutable again: a later permission insert into the Manager is refused (tested).
  * **The presets:**
    * the Manager holds view, create, update and activate; it is privileged (update and activate, D-158) and cannot read a full number (tested through the real API);
    * the Sensitive Viewer holds view and the sensitive view; it is privileged (resource classification), reads the full number with an audited reveal, and cannot create, update or activate (tested);
    * on an upgrade of a database with a company, an administrator and an approver, every pre-existing role and permission row is unchanged (rows were only added: 2 roles, 6 permission rows), and so are assignments and memberships;
    * Company Administrator holds no cash code.
  * **The offline commands:** all audited (grant and revoke events with no actor, counted exactly). They are refused for:
    * a non-member (`NotMember`), with no membership created;
    * an unknown login;
    * an unknown company (see L1 for non-Ready).

    An administrator cannot assign the presets (`cannot_grant_permission`), cannot create a role holding the sensitive code, and cannot revoke the presets either. The migration and the commands make no automatic assignment. MFA is mandatory for both presets (N1). The `Hasm.Admin` commands are tested end to end with usage errors and no secrets in the output.
  * **Changed existing tests:** none is loosened.
    * The role count goes from 8 to 10 in three assertions, still exact.
    * The migration lists go from 1–35 to 1–36, still exact.
    * The seed-equality test now includes version 36.
    * The allowlist count goes from 26 to 27, still exact.
    * The refusal-message test computes the next number instead of the literal 0036, but still asserts the exact number and name.
  * **Mutations:** both that I re-ran were killed.
  * **D-214:** cash posting stays closed; there is no posting code or service.
  * **No protected files. Registry:** 1734, consistent.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the following three items.
  * The full local PostgreSQL suite: the author reports 1790/1790; my targeted subset was 105/105, and hosted postgres-integration passed on this exact head with the 1734-test registry. I did not reconcile the 1790 figure.
  * The author's other five mutations: I re-ran two.
  * `tools/verify/verify-head.js`: GitHub GraphQL is blocked in this session (HTTP 403); the checks were read by REST.
* **Verdict:** approve
