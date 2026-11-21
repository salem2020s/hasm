# Independent review - PR #69, Phase 3.11 slice 2c (pricing API and shared screens)

* Reviewed: the full diff against `origin/main` at head `5da344c`. Reviewer: a separate read-only subagent (general-purpose, Sonnet family, fresh context; it ran the pricing and message component tests and the typecheck, not the PostgreSQL suite). **No Critical, no High; four Medium, seven Low.**
* Verified: every route behind `RequireAuthorization` plus the session company; view/configure/override permissions (an override object with all-null fields still needs the permission); version fence for the policy and revision fences elsewhere; immutable fields enforced server-side; no DELETE; explicit response shapes; shared `Tabs` with per-tab dirty marks, panels kept mounted; labels, `bdi`/ltr for numbers and codes; every `t()` key defined with ar/en parity; update payloads send stored values plus the revision read; customer groups stated as an unfinished Release 1 requirement in both docs; handoff matches GitHub.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | A mistyped minimum quantity on a discount was sent as null (= 0). | Fixed: the create button is disabled for an invalid minimum; test added. |
| M2 | Medium | Pickers silently truncated at 500 items and swallowed every failure. | Fixed: a warning banner when a helper list fails and when the item list reaches the limit; test for the failure notice. A searchable/paged picker is a recorded follow-up. |
| M3 | Medium | A half-edited price kept the Lists tab dirty after switching lists. | Fixed: the edit is cleared when the selected list changes. |
| M4 | Medium | A policy conflict discarded the typed values. | Fixed: the form is no longer reset on a version change; the conflict banner shows and the typed values stay; test added. |
| L1 | Low | The UI does not say that customer and item scopes combine. | Recorded follow-up (copy). |
| L2 | Low | An empty minimum on an entry disables the button without an error. | Accepted (the button state is the signal; default 0 is shown). |
| L3 | Low | "JOD" is the hard-coded default currency. | Recorded follow-up: take it from the company base currency. |
| L4 | Low | If-Match is parsed before the permission check (400 instead of 403 for a malformed header). | Recorded follow-up (same shape as the tax endpoints). |
| L5 | Low | A missing quote currency reached the engine. | Fixed: explicit 400 `invalid_request`. |
| L6 | Low | The quote error mapping relies on `t(key) !== key`. | Accepted (same convention as `errorText`). |
| L7 | Low | Test gaps (discount PUT, entry immutability errors over HTTP, cross-company, body limit, picker truncation, RTL). | Recorded follow-ups; the service tests cover immutability and the discount update. |
