# Independent review - Phase 3.9B (customers, suppliers, sales representatives)

* Reviewed: commits adb0212 (backend), 263434e (UI), e2c5e73 (docs) of branch `feature/phase-3-9b-customers-suppliers` (stacked on PR #54 and the PR #53 branch). Reviewer: a separate subagent session (general-purpose agent, fresh context, read-only, code reading only; the PostgreSQL suite and frontend tests were not run by it).
* Result: **no Critical, no High.** Five Medium and six Low findings.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | The service re-validated the control account and representative even when unchanged, so a role whose representative or account was deactivated later could not be edited or deactivated. | Fixed: references are validated only when they change (PostgreSQL still checks the account on change); PostgreSQL test covers edit and deactivation after the references went inactive, and a NEW reference to an inactive one is still refused. |
| M2 | Medium | `num()` turned "5,000" into 5, accepted "Infinity" (serialized as null, silently clearing the limit), "1e3", "0x10". | Fixed with a strict shared parser (`numberInput.ts`, also used by the tax screen): one separator, no thousands separators, no exponent, finite only; unit and component tests. |
| M3 | Medium | After a 412 the editor kept the old revision, so every retry conflicted until the user left the editor. | Fixed: after a reload the edited role takes the stored revision while the typed values stay; component test retries without leaving. |
| M4 | Medium | An account used as a control account can later be deactivated, made a group, or have its nature changed; role use does not count as posting history in the chart service. | Accepted as a documented follow-up for 3.13 (the commercial document framework re-validates the control account at use and the chart guard will count control-account use as history). Not changed here because it touches the protected chart invariants. |
| M5 | Medium | The representative's user id is not verified (it lives in the system database; a company transaction cannot check it) and a PUT omitting it clears the link. | Accepted and documented: the id is informational in this phase (no authorization depends on it); PUT replaces the record by design. Verification belongs with the first feature that relies on the link. |
| L1 | Low | The trigger's account SELECT takes no FOR SHARE. | Accepted: the service path locks; only direct writes are exposed. |
| L2 | Low | Deadlock, serialization and FK errors were 500. | Fixed: mapped to Conflict `concurrent_change`. |
| L3 | Low | A no-op PUT bumps the revision; representative audit has no previous values. | Accepted (revision fencing is by design; detail is bounded). |
| L4 | Low | Bad If-Match is 400 before the permission check; `partyroles.view` shows party names without `parties.view`. | Accepted (nothing sensitive beyond the role holder's own company data). |
| L5 | Low | UI gaps: guard before a party is chosen, picker capped at 500, user id free text, representatives not editable. | Accepted for this slice; recorded as follow-ups. |
| L6 | Low | The diff includes the 3.9A follow-ups. | By design: this branch is stacked on the PR #53 branch to keep migrations contiguous. |

Checked without defect: generator determinism and CI `--check`, trigger revision fence/immutability/no delete, two separate permission families checked before any read, no DELETE grant, system migration 0025 pattern, request handling of unknown enum numbers and null bodies, CI registry (now 989), Tabs and translation parity.
