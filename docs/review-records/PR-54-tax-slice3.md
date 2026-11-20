# Independent review - PR #54, Phase 3.10 slices 2c and 3 (tax management API and shared UI)

* Reviewed head: `682bf61` (full diff against `origin/main`, focus on the last two commits `adacba9` and `682bf61`). Reviewer: a separate subagent session (general-purpose agent, fresh context, read-only; it was not given the author's reasoning). Method: code reading and greps; it did not run the PostgreSQL suite.
* Result: **no Critical, no High.** Six Medium and ten Low findings. All Medium and the actionable Low findings are fixed in the follow-up commit; the rest are recorded below.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | A null body, `{}` or a misspelled property on the item/party class assignment endpoints bound to `classId == null`, which is the REMOVAL path: a malformed request silently removed an assignment and wrote a "removed" audit event. | Fixed: the body must carry `classId` explicitly (a GUID, or null to remove); anything else is 400 `invalid_request` (after authentication and company selection). Test covers null body, `{}`, misspelled property, non-GUID, number. |
| M2 | Medium | `ValidateRule` locked only the rule's own tax row; `duplicate_priority` and `invalid_base_reference` compare against rules of OTHER taxes, so two concurrent rules for taxes A and B with one priority both passed (write skew). | Fixed: every rule write takes a transaction-scoped advisory lock keyed by document type and operation before the peer checks. Concurrency test: of two concurrent rules for different taxes with one priority exactly one succeeds. |
| M3 | Medium | `TaxRuleInput.IsActive = true` default: a rule PUT omitting `isActive` silently reactivated a switched-off rule. | Fixed: `IsActive` is nullable (null = true on create, unchanged on update); API and service tests. |
| M4 | Medium | Omitted JSON fields on non-nullable value types became defaults (`EffectiveFrom`/`ValidFrom` = 0001-01-01, `MoneyDecimals` = 0), e.g. a first, append-only rate version dated year 1. | Fixed: those three are nullable and refused as `required`; tests at service and HTTP level. Mode, level, treatment and priority already refused 0. |
| M5 | Medium | The rule-base trigger compares the rule row's `xmin` with the current transaction id: correct for every present path (create, update, replace; concurrent writers serialized by the row lock) but a savepoint/subtransaction (PL/pgSQL EXCEPTION block, a future Npgsql savepoint) would make `xmin` a subtransaction id and refuse a legitimate write. | Accepted and documented: no savepoint is used anywhere in the tax path; the failure mode is fail-closed (P0001 is mapped to Conflict `invariant_violation`, nothing is written). The limitation is recorded in the migration comment of the generator. The revision advance itself remains enforced by the TaxRule trigger. |
| M6 | Medium | `Number()` on typed values: Arabic-Indic digits or a decimal comma gave NaN, sent as null; the server answered a generic 400. | Fixed: `num()` accepts Arabic-Indic/Persian digits and `,`/`٫`; a non-number disables the action and shows the field error; component test. |
| L1 | Low | Selecting another tax kept the typed rate and date. | Fixed: selecting a tax resets the rate form. |
| L2 | Low | A failed reload replaced the whole page and discarded unsaved input. | Fixed: the error shows as a banner while the tabs stay mounted once data exists. |
| L3 | Low | `today()` used the UTC date. | Fixed: local calendar day. |
| L4 | Low | 404 codes had no `tax.error.*` text. | Fixed (Arabic and English); parity test passes. |
| L5 | Low | `Assign`: `FOR UPDATE` on a missing row locks nothing; a concurrent deactivation can still be assigned. | Accepted: harmless (inactive classes keep resolving; two first assignments both audit `previous=none`). |
| L6 | Low | `Tax:MaxRateLeadDays` negative / huge / non-numeric. | Fixed: validated at startup (0..3650). |
| L7 | Low | `UpdateClassAsync` ignored an undefined kind. | Fixed: any kind different from the stored one is `kind_immutable`. |
| L8 | Low | A new active rule could name an inactive class. | Fixed: `class_inactive`. |
| L9 | Low | If-Match parsed (400) before the permission check (403). | Accepted: nothing is disclosed. |
| L10 | Low | Test gaps (413, unknown enum numbers, cross-company over HTTP, savepoint case, strict DELETE assertion). | Partly closed (null/malformed bodies, unknown class kind over HTTP, omitted fields, race); the rest follow with the document framework tests. |

Checked by the reviewer without defect: If-Match semantics (428/412/400, after authorization), RBAC (`company.tax.view`/`configure`, company only from the session, no DELETE route), 64 KB request limit on every write, no UPDATE grant on the rule-base table, audit of previous class, CI registry consistency, Tabs (panels mounted), Arabic/English parity, no Web/Tauri gap or RTL issue.
