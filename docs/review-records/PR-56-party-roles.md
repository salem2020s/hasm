# Independent review - PR #56, Phase 3.9B customers and suppliers (party roles)

* Reviewed: the full diff against `origin/main` at `2ec1b83` (37 files). Reviewer: a separate read-only subagent (general-purpose, Sonnet family, fresh context, not given the author's reasoning). Method: code reading; it did not run the PostgreSQL suite. Verdict: **APPROVE, no Critical, no High.** Two Medium, three Low.
* Verified by the reviewer: registry raise-only (979 -> 989 = PartyRoleTests 7 + PartyRoleApiTests 3); migrations contiguous (company 0022 -> 0023, system 0024 -> 0025); 0023 additive with immutability, TRUNCATE and revision triggers; company scope only from the session, company-wide permission checks, no DELETE route, If-Match with 428; FOR UPDATE and revision conflicts; credit limit stored as data only (no enforcement invented); one audit event per write; shared Web/Tauri screen with Tabs and RTL/LTR.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | A sales representative's `UserId` is not validated against the system database (no cross-database FK). | Not fixed here: nothing (commission, authorization) uses the link yet. **Follow-up:** add a membership check before any feature consumes `UserId`; tracked in docs/INTEGRATION-STATUS.md. |
| M2 | Medium | Update does not re-check that referenced party/account/representative is still active (deactivation must stay possible). | Intentional and documented in the service comment (line 192); no new accounting rule. **Follow-up:** a test that a role whose account/representative was later deactivated stays editable and deactivatable. |
| L1 | Low | A viewer without `company.salesreps.view` sees the raw representative id. | Accepted; follow-up with the sales documents slice. |
| L2 | Low | `userId` is a free-text GUID input (binding 400 instead of a field error). | Accepted; follow-up. |
| L3 | Low | A foreign-key violation maps to `concurrent_change`. | Accepted; create pre-checks the party. |
