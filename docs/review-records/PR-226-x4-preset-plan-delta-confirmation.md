# Independent review record

* **Pull request:** #226 (X4-PRESET-ROLE preset plan, documentation only). Delta confirmation f4b6e34..cc5e2b3 of `PR-226-x4-preset-plan-review.md` (record 02e1b78, request changes: M1 to M3, L1 to L3).
* **Head SHA reviewed (full 40 characters):** cc5e2b313242a6e22b131bb365c6b68d07a23038
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author sonnetUI185); model family Claude Opus. Prompt (summary): "confirm the delta (M1 read codes and scopes with 'only this preset' tests; M2 the widening rule of 3.4, execute never in the approver, and a no-execute-with-approve test; M3 the grant and revoke commands of 3.5; L1 to L3). Check in particular whether the read codes give the approver more than the re-plan needs (SoD), and whether the closer's scope is adequate as stated."
* **Scope read:**
  * `git diff f4b6e34 cc5e2b3`: `docs/X4-PRESET-ROLES-PLAN.md` (+35/−10) and my record 02e1b78, merged into the branch.
  * Re-read for the questions:
    * `ClassificationEvidence.cs` lines 117–125 (`Bootstrap` and the company-wide authorisation);
    * `CostFlowSnapshot*.cs` (stock balance and stock document checks);
    * `CompensationPlanApproval.cs` and `CompensationPlanStorage.cs` (approve, cancel, propose);
    * `Cash/CashAccountManagementService.cs`;
    * `PrivilegedAccess.cs`.
  * On main b318e21 (after PR 225): D-254, D-255 and D-258 in `docs/DECISIONS.md`; RQ-10 in `docs/PHASE-3.8D-REVALUATION-CONTRACT.md`; LQ-13 in `docs/PHASE-3.15-LANDED-COST-CONTRACT.md`.
* **Checks run (commands and results):** this review is by reading only (documentation PR).
  * **Hosted checks on the exact head, read by REST:** ci-guard, dependency-scan, docs-traceability, guard-selftest and secret-scan succeeded; a skipped duplicate of each also ran.
  * **Mergeability:** `mergeable_state` clean; `git merge-tree --write-tree origin/main cc5e2b3` is clean against b318e21.
  * **Cited decisions.** D-254, D-255 and D-258 exist on main. The phrase quoted in 3.4, "the Inventory Cost Approver role of X4", is the wording of RQ-10 (revaluation contract, line 419) and LQ-13 (landed-cost contract, line 288). The owner accepted both: D-255 "independent Inventory Cost Approver authorization"; D-258 "independent Inventory Cost Approver approval".
* **Findings:** 0 Critical, 0 High, 0 Medium, 2 Low notes (N1, N2). Every finding of the review at f4b6e34 is resolved:
  * **M1 resolved.**
    * The Inventory Cost Approver holds `company.compensationplans.approve` plus the five read codes the approval path demands: `accounts.view`, `journal.view`, `stockdocs.view`, `costcenters.view`, `stockbalance.view`. The slice re-derives and pins the set.
    * The assignment is company-wide (D-066).
    * The Cash Account Sensitive Viewer also holds `company.cashaccounts.view`, which the full-number read requires.
    * Every preset gets an "only this preset" PostgreSQL test, with a mutation that drops a read code. The test also checks the negatives: the approver is refused execute and create, the viewer is refused update, and the manager is refused the full number.
  * **M2 resolved.** Section 3.4 states that any addition of a code to an existing preset is a widening, ruled out unless recorded and approved.
    * Only the revaluation and landed-cost approve codes carry that approval (D-255 and D-258, through RQ-10 and LQ-13).
    * Execute is never added to the Inventory Cost Approver; it goes to a separate preset or the T2 signer arrangement.
    * A test forbids execute together with approve.
  * **M3 resolved.** Section 3.5 adds audited offline `grant-`/`revoke-` commands per preset, following `grant-import-approver`:
    * owner account only;
    * an audit event for every grant and revoke;
    * refusals for an unknown login or company and for an already-held or not-held assignment;
    * the mandatory-MFA rule for privileged presets.

    The commands land in the same pull request as their preset, with tests.
  * **L1 resolved.** Section 1 now states that 0034 paused both triggers, its `r_d3408f58` pause being unnecessary, and that the new entries pause exactly what each migration needs.
  * **L2 resolved.** Section 4 classifies each new preset under `PrivilegedAccess`, which I confirmed in code, and a test asserts `IsPrivilegedRole` for each:
    * Inventory Cost Approver: privileged (resource and approve action);
    * Cash Account Manager: privileged (`update` and `activate` are in `PrivilegedPermissions`);
    * Cash Account Sensitive Viewer: privileged (resource `company.cashaccountssensitive`);
    * Commercial Document Closer: not privileged (`company.commercialdocs` is ordinary, and Close is not a privileged action).
  * **L3 resolved.** The tests are added: each preset on its own, classification with MFA, the assignment commands, and no execute together with approve.
  * **Low N1: the approver's read reach (the question of whether it exceeds the re-plan).**
    * The five read codes are exactly what the re-plan demands, no more. `ClassificationEvidenceService` requires `accounts.view`, `journal.view`, `stockdocs.view` and `costcenters.view` **company-wide** (`HasCompanyWide`), and the snapshot requires `stockbalance.view` per warehouse. A company-wide assignment is therefore needed, and the plan states it.
    * The consequence is that an Inventory Cost Approver can read the whole company's journal, chart of accounts, stock documents, cost centres and stock balances through the ordinary read screens.
    * That is visibility, not a conflicting duty. The role still cannot propose (`ProposeAsync` needs `create`), cannot execute, and cannot approve a plan it created, edited or submitted (D-191 X3, enforced in the service). It matches the Journal Approver, which also holds `journal.view` company-wide.
    * **No SoD issue.** A narrower read (only the plan's evidence) would need a dedicated evidence-read path in code; that is not required by any decision and not recommended now.
    * Recommendation: the slice's role description states this read reach in one sentence, so whoever assigns the role knows what it sees.
  * **Low N2: the wording of the widening rule.**
    * Both contracts say their new permissions are "never added to an existing preset automatically" and "no existing role widened". The same rows name "the Inventory Cost Approver role of X4", and the owner accepted that.
    * Section 3.4 resolves the tension correctly: the owner's acceptance of RQ-10 and LQ-13 is the recorded approval for exactly those approve codes (and their read needs), and nothing else.
    * Recommendation: when those slices land, each records the widening of the Inventory Cost Approver in the owner-decision register as "approved by D-255 (or D-258)", so the rule of 3.4 stays traceable.
* **Answers to the specific questions:**
  * **Do the read codes exceed what the re-plan needs?** No. They are exactly the codes the approval path checks, and the company-wide scope is required by the code. The wider visibility is a read reach with no SoD effect (N1).
  * **Is the Commercial Document Closer's scope adequate as stated?** Yes, for a plan:
    * `company.commercialdocs.close` is branch-scoped, with assignment per branch or company-wide as the slice decides;
    * the role is not privileged, and is never combined with approve;
    * the need for `company.commercialdocs.view` is checked when the slice exists (it is not on main) and is then seeded into the same preset;
    * the "only this preset" test ("the closer closes a document") makes a missing code a failing test, not a silent gap.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the following three items.
  * The complete read set of the approval path beyond the checks I re-read: the slice re-derives it, and the "only this preset" test pins it.
  * The short-close endpoint's checks: that slice is not on main.
  * GitHub GraphQL is blocked in this session (HTTP 403); the checks and mergeability were read by REST.
* **Verdict:** approve
