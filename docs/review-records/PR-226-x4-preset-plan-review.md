# Independent review record

* **Pull request:** #226 (X4-PRESET-ROLE: plan for the Inventory Cost Approver, cash-account and short-close preset migrations; documentation only, Draft; author sonnetUI185). Reviewed as the full diff at the revised head. My findings drafted on 029f431, the first head, are carried over where they still apply and marked resolved where the revision fixed them.
* **Head SHA reviewed (full 40 characters):** f4b6e342cc33699679ce8ba1d3bdd61dc2c337bf
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author); model family Claude Opus. Prompt (summary): "review `docs/X4-PRESET-ROLES-PLAN.md` against the owner's section C of 2026-11-22 (Option A; new pairs allowlisted under PR 188; strict RBAC and SoD; 'Do not automatically grant privileged roles to existing users'; no activation of incomplete features). Apply the lead's direction: dedicated presets for new or sensitive codes, not a widening of Company Administrator; report any conflict with an approved decision. Judge:
  * whether the pair is genuinely required, with no pair-free route;
  * the allowlist entries;
  * the cost approver holding only approve, and whether it needs a read permission;
  * SoD against the approved RBAC rules;
  * the tests;
  * that no feature is activated.

  The head moved to f4b6e34 (dedicated presets) before the record landed; review that head."
* **Scope read:**
  * The full diff `origin/main...f4b6e34`: one file, `docs/X4-PRESET-ROLES-PLAN.md`, 80 lines, in commits 029f431 and f4b6e34.
  * The owner's section C (consolidated message of 2026-11-22).
  * System migration 0004 (trigger functions `f_8131c8b7` and `f_73f996bc`, the role-table constraints), 0029, 0030 and 0034.
  * `PermissionCatalog.cs` (the `PresetRoles` definitions, `CashAccountCodes`, the resource scopes) and `PrivilegedAccess.cs`.
  * The approval path:
    * `CompensationPlanApproval.cs`, `CompensationPlanStorage.cs`, `CostFlowSnapshot*.cs` and `ClassificationEvidence.cs`;
    * `Cash/CashAccountManagementService.cs`;
    * `Hasm.Admin/Program.cs` (the offline grant commands).
  * `docs/DECISIONS.md`: D-066, D-096, D-155, D-158, D-191, D-201 and D-211 to D-214.
* **Checks run (commands and results):** this review is by reading only; no build or test is needed for a documentation PR.
  * **Hosted CI for f4b6e34 was not read.** It is a docs-only change, so only the always-on verification jobs apply.
  * **Trigger semantics, read in system 0004.**
    * `f_8131c8b7` (trigger `r_d3408f58` on `t_359332ac`):
      * refuses every DELETE;
      * refuses an INSERT whose preset key is not NULL;
      * refuses any UPDATE of a row whose OLD preset key is not NULL;
      * on any other UPDATE, refuses a change of id, company or preset key ("role identity is immutable").
    * `f_73f996bc` (trigger `r_2601e368` on `t_3e3d4119`): refuses an INSERT, UPDATE or DELETE whose role is a preset, and every UPDATE.
    * Constraint `k_88edaaae`: (preset key is NULL) = (company is NOT NULL).

    A preset therefore cannot be created as a custom role and converted, and a preset permission cannot be added by any route other than pausing the trigger. Replacing the guard functions is a larger, allowlist-gated safeguard change. **No pair-free route exists.**
  * **Read permissions the services demand.**
    * **Compensation-plan approval:** the service checks `company.compensationplans.approve` on the root warehouse, then re-runs `CostFlowSnapshotService.LoadCompensationPlanAsync` **under the approver's own permissions**. That needs:
      * `ClassificationEvidenceService.Bootstrap`: `company.accounts.view` (company), `company.journal.view` (company-wide or a branch grant), `company.stockdocs.view` (company-wide or a warehouse grant);
      * the evidence authorisation: company-wide `accounts.view`, `journal.view`, `stockdocs.view` and `company.costcenters.view`, plus per-row checks;
      * `company.stockbalance.view` per warehouse.
    * **Full bank-account number:** `CashAccountManagementService` lines 84–87 require `company.cashaccountssensitive.view` AND `company.cashaccounts.view` on the account's branch. Without the view code, the account is answered as unknown.
  * **Privileged classification** (`PrivilegedAccess`):
    * resource `company.cashaccountssensitive` is privileged;
    * the permissions `company.cashaccounts.update` and `.activate` are privileged;
    * resource `company.compensationplans` is privileged;
    * `company.commercialdocs` is ordinary, and Close is not a privileged action.
  * **Offline grant commands.** `Hasm.Admin` has grant and revoke commands only for the Fiscal, Journal and Import Approver presets and for Company Administrator.
  * **Decisions:**
    * D-158: "Approve, reverse and viewsensitive are not in the Company Administrator preset"; update and activate are PRIVILEGED.
    * D-191 X4 (owner text, verbatim): "dedicated, scoped permissions and an appropriately privileged Inventory Cost Approver role; existing roles are not widened automatically".
    * Owner C, verbatim: "Do not automatically grant privileged roles to existing users".
* **Findings:** 0 Critical, 0 High, 3 Medium (M1 to M3), 3 Low (L1 to L3). The High of the 029f431 draft is resolved. Each finding is listed below.
  * **Resolved (draft H1): Company Administrator is no longer widened.**
    * Proposal A is withdrawn.
    * The cash codes go to the dedicated presets "Cash Account Manager" and "Cash Account Sensitive Viewer". The close code goes to "Commercial Document Closer", and it is added to Company Administrator's exclusion list so the catalog cannot widen that role silently.
    * A test compares every pre-existing preset's permission rows before and after.
    * This now matches D-158 (the sensitive view is not in Company Administrator), D-191 X4 (existing roles are not widened) and owner C.
    * **The lead's direction conflicts with no approved decision; D-158 and D-191 X4 require it.** The owner's "established ... pattern" names the migration technique, not the role that receives the codes.
  * **Resolved (draft L2): the allowlist table.** All three migrations pause `t_359332ac/r_d3408f58` and `t_3e3d4119/r_2601e368`, because each creates new preset roles. That makes 29 entries in all, since the two cash presets share one migration.
    * The rest of the shape is correct: set System, the final number and name, and the checksum of the final file, written in the merging change.
    * The guard refuses the same text under another number, name or set.
  * **Medium M1: two presets cannot do their job when held on their own.**
    * **Inventory Cost Approver** (only `company.compensationplans.approve`): the approval re-runs the planner under the approver's own permissions. It needs the read set listed above (accounts, journal, stock documents, cost centres and stock balance views), so a user who holds only this preset is answered `forbidden`.
    * **Cash Account Sensitive Viewer** (only `company.cashaccountssensitive.view`): the full-number read also requires `company.cashaccounts.view` on the branch. The preset alone therefore shows nothing.
    * The established approver presets each pair approve with their resource's view: Fiscal, Journal, Import, Commercial and Retention Approver.
    * Read codes do not weaken SoD: the viewer still has no create, update or activate, and the approver has no create or execute.
    * Required:
      * seed the cost approver with the exact read set the approval path demands, derived from code by the slice;
      * seed the viewer with `company.cashaccounts.view` as well;
      * state each preset's assignment scope: the cost approver company-wide like the other approvers (D-066), because company-level resources are not exercisable through a warehouse assignment; the cash presets company-wide or per branch, as decided by the slice;
      * pin each with an "only this preset" PostgreSQL test (L3), with a mutation that drops one read code.
    * This replaces the open point in 3.1.
    * For Commercial Document Closer, whether the close endpoint also needs `company.commercialdocs.view` is to be checked when the slice exists; that slice is not on main.
  * **Medium M2: future widening of the Inventory Cost Approver, and execute in particular.**
    * Section 3.1 still says that the execute, revaluation and landed-cost codes will each be "added to this preset by its own later slice". Adding a code to a preset widens every existing holder automatically, which is what D-191 X4 and owner C rule out.
    * For execute, it also combines approval and execution in one role, which the executor plan and the T2 design keep separate.
    * Required:
      * execute never goes into the Inventory Cost Approver; a dedicated executor preset (or the T2 signer arrangement) carries it;
      * state once, for all presets, that any later addition of a code to an existing preset is a widening, recorded and approved as such. The owner's naming of the Inventory Cost Approver for revaluation and landed-cost approval (RQ-10, LQ-13) is that approval for those two codes.
  * **Medium M3: there is no way to assign the new presets.**
    * The plan says each preset is "assigned offline by the owner account through the administrative tool", but no such command exists for any of the four new presets: `Hasm.Admin` has grant and revoke only for Fiscal, Journal and Import Approver and Company Administrator.
    * A company administrator cannot assign them either, because they cannot grant a permission they lack (D-066).
    * Required: each slice adds audited offline `grant-<preset>` / `revoke-<preset>` commands, following `grant-import-approver`, with tests. Without them the presets cannot be used, or are assigned by hand-written SQL.
  * **Low L1: section 1 misstates what migration 0034 paused.** System 0034 paused BOTH triggers although it only inserted permissions; its `r_d3408f58` pause was unnecessary. Correct the sentence. The plan's own entries are minimal and correct, and the allowlist pins exact triggers.
  * **Low L2: the privileged classification of the Cash Account Manager is not stated.** Section 3.2 names only the sensitive viewer as privileged. `PrivilegedAccess` classifies `company.cashaccounts.update` and `.activate` as privileged (D-158), so the Cash Account Manager is privileged too, and mandatory MFA applies when it is assigned. The Inventory Cost Approver is privileged by resource and by action; the Closer is not privileged. State this, and test every new preset with `IsPrivilegedRole`.
  * **Low L3: tests to add (section 7).** The list is otherwise sound and now includes the pre-existing-preset equality and the unchanged assignment counts. Add:
    * **(a)** an "only this preset" usability test for each of the four presets (M1);
    * **(b)** `IsPrivilegedRole` for every new preset, with mandatory MFA on assignment (L2);
    * **(c)** no preset holds execute together with approve, in addition to the existing approve-with-create test (M2);
    * **(d)** the grant and revoke commands with their audit events (M3).
* **Answers to the specific questions:**
  * **The pair is required:** yes, for every migration; there is no pair-free route (see the trigger semantics above).
  * **Allowlist entries:** correct at 29, with both triggers in each (resolved draft L2).
  * **The cost approver holding only approve:** insufficient; the read set is needed (M1). The role's SoD is otherwise sound: no create (D-191 X3 is also enforced in the service), and no execute (M2).
  * **SoD against the approved RBAC rules:**
    * D-066: approvers stay outside Company Administrator and are assigned offline;
    * D-096 and D-158: classification (L2);
    * D-191 X3 and X4: independent approver, a dedicated role, no widening;
    * D-155 and D-158: the sensitive view is separate and outside Company Administrator;
    * D-201: closing is audited and not an approval; the Closer and the Commercial Approver hold neither other's code.

    All are satisfied by f4b6e34 apart from M2's future widening.
  * **No activation:**
    * compensation-plan execution stays closed (nothing grants execute);
    * cash and bank posting stays closed (D-214), with cash-account master data in its configuration stage (owner H);
    * the closer preset lands only with its reviewed slice, together with the catalog code and the exclusion;
    * no migration inserts role assignments.

    Confirmed, provided each preset migration lands with its slice.
* **Blocker or High findings open:** none. M1 to M3 (Medium) are open; L1 to L3 (Low) are open.
* **NOT VERIFIED:** the following three items.
  * The complete read set of the approval path beyond the checks listed above (for example item or warehouse master views): to be derived by the slice and pinned by the M1 test.
  * The short-close endpoint's permission checks: that slice is not on main.
  * Hosted CI for f4b6e34 was not read (docs-only change).
* **Verdict:** request changes
