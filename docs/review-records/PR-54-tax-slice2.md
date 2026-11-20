# Independent review - PR #54, Phase 3.10 slices 2a/2b (tax resolver and persistence)

* Reviewed head: `b33be89` (full diff against `origin/main`, focus on slices 2a/2b). Reviewer: a separate subagent session (general-purpose agent, fresh context, read-only; it was not given the author's reasoning). Method: code reading only; it did not run tests or the generator.
* Result: **no Critical, no High.** Four Medium and six Low findings; all Medium and the actionable Low findings are fixed in the follow-up commit; the rest are recorded below.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | A tax could be deactivated into an unfixable state: rules of an inactive tax could not be edited or switched off, and the resolver refuses an inactive tax. | Fixed: `tax_inactive` is refused only when a rule is being (newly) ACTIVE; a rule of an inactive tax can be switched off. Test covers deactivate, refusal on resolve, switch-off, clean resolve. |
| M2 | Medium | A rate version dated far ahead (typo) permanently blocks later versions (append-only, strictly later). | Fixed: `effectiveFrom` at most 366 days ahead (`rate_too_far`); test. |
| M3 | Medium | The rule overlap check raced (no lock). | Fixed: the tax row is locked `FOR UPDATE` before the overlap and order checks, serializing rule writes per tax. |
| M4 | Medium | Rule validation did not catch what the resolver refuses later (`duplicate_priority`, `invalid_base_reference`, equal-specificity item-only vs party-only for one tax). | Fixed: the three are refused when the rule is written, against the active, time-overlapping and class-compatible peers of the same document type and operation (`duplicate_priority`, `invalid_base_reference`, `rule_ambiguous`). The resolver keeps its own refusals as the second line. Tests updated. |
| L1 | Low | Assignment and rule validation read classes/taxes without `FOR UPDATE`. | Accepted: the tax row is locked for rules; a class deactivated concurrently with an assignment leaves one assignment of a just-retired class, which is harmless (inactive classes keep resolving). Follow-up: lock classes if a stricter policy is wanted. |
| L2 | Low | An update omitting `isActive` silently reactivated a tax/class. | Fixed: `IsActive` is nullable; null means true on create and unchanged on update; test. |
| L3 | Low | Rule-base replacement and assignment deletes are protected by the service only; removal audit lacks the previous class. | Accepted and documented; the nine-table DELETE allow-list extension was judged justified by the reviewer. Optional hardening (a trigger tying base changes to a revision bump) and a richer removal audit are follow-ups. |
| L4 | Low | An override rule needed a dated rate; an unknown item/party id was treated as unclassified. | Fixed (override needs no rate; unit test). The unknown-id behaviour is kept deliberately (an unclassified subject is valid); the document framework validates the subject. |
| L5 | Low | Trigger exceptions (P0001) surfaced as unhandled errors. | Fixed: mapped to `Conflict`/`invariant_violation`; nothing is written. |
| L6 | Low | The generator `--check` was not run by CI; a hand edit of the SQL body would pass the unit tests. | Fixed: `backend.yml` runs `node tools/schema/company-0022-tax.cjs --check` in `backend-build-test`. |

Checked by the reviewer without defect: append-only enforcement of rate and policy versions (trigger plus runtime privileges), item/party class kind safety (composite foreign key plus check, generated kind columns), revision fencing, authorization and company isolation, the 0024 trigger-pause pattern, SQL injection (constants and parameters only).

Verification after the fixes (local, disposable 5433 cluster): unit 917/917, `TaxConfigTests` 10/10; the full PostgreSQL suite ran 1028/1028 on the head before the fixes and is re-run on the exact final head in CI.
