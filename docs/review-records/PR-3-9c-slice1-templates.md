# Independent review - Phase 3.9C slice 1 (reusable import mapping templates, PR #63)

* Reviewed: `git diff ecc5c03..6572026`. Reviewer: a separate subagent session (general-purpose, fresh context, read-only, static). Result: **no Critical, one High**, four Medium, several Low. All High and Medium findings are fixed in the follow-up commit.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| H1 | High | The template audit codes 1110/1111 collided with the report export events (`ReportAuditEvents`, documented as never renumbered) in the same business stream. | Fixed: templates use 1700/1701 (unused); a new unit test (`AuditEventCodesTests`) reflects over every `*AuditEvents` class and fails on any duplicate code (the party registry's separate system-stream events are listed explicitly). |
| M1 | Medium | A default over 400 characters could be saved but made every later apply `template_incompatible`. | Fixed: refused at save (`default_too_long`); unit test at 400/401. |
| M2 | Medium | Names `.`/`..` (unaddressable in a URL) and lookalike names (zero-width, bidi, no NFC) were accepted. | Fixed: NFC, trimmed, no control/format/separator/private-use/unassigned characters, not dots only; applied to save, retire and history; test with `..`, zero-width, RLO, NEL and composed/decomposed `cafe` (one template). |
| M3 | Medium | The save response re-read the latest row after commit, so a concurrent writer's version could be returned. | Fixed: the inserted row is read inside the transaction under the template lock. |
| M4 | Medium | Double click on Save sent two saves on one version (a spurious conflict after success); apply replaces unsaved form edits. | Fixed: a local pending flag disables the box while a request runs. Applying replacing the form is the documented intent ("fills the form below"); nothing is saved until the ordinary save. |
| L1 | Low | Retire skipped name cleaning. | Fixed (part of M2). |
| L2 | Low | Any unique or raise violation reports `version_conflict`; a check violation (23514) was a 500. | Check violation now returns Invalid `template`; the version message stays (the trigger and the index are the only raisers). |
| L3 | Low | Content is `text` with no JSON check in the database. | Accepted: the service is the only writer and validates before apply; the size check constraint bounds it. |
| L4 | Low | Existence probing of a batch id (Forbidden vs NotFound) and listing needs `imports.view` while upload-only users see an empty picker. | Accepted: identical to the existing import endpoints. |
| L5 | Low | `Norm` (same as the suggestion) does not strip BOM, zero-width or tatweel from headers; the Arabic comma joins unmatched field names in English. | Accepted for this slice (shared normalizer; changing it affects the suggestion); recorded. |

Checked without defect: the version-gap trigger plus the unique index is race-safe; append-only for update/delete/truncate; anchored check regexes; SELECT/INSERT-only runtime privileges; deterministic generator checked in CI; the advisory-lock key is unambiguous; Apply writes nothing to the batch and everything then goes through the ordinary validated mapping step (required fields, duplicate columns, options), dry run, approval and plan hash; `Validate` runs before apply; the literal `templates` route does not collide with `{id:guid}`; registry and pinned counts consistent.

## Re-review - full diff against main after the integration of #53/#54/#56/#57 (2026-11-20, head 48693b6)

Reviewer: a separate read-only subagent (general-purpose, Sonnet family, fresh context; static, it did not run the PostgreSQL suite). **No Critical, no High.** The earlier H1 and M1-M3 were verified fixed (audit codes 1700/1701 unique across every `*AuditEvents` class; default length refused at save; name cleaning; own-row response); pinned migration counts (24 -> 25, `0026_extra` test migrations) not weakened; registry 1003 -> 1011 raise-only; migration 0025 contiguous, additive, append-only, runtime SELECT/INSERT only; company scope and permissions on every endpoint.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M-a | Medium | Saving over a retired name silently revives it. | Documented as the intended lifecycle rule in `docs/PHASE-3.9C.md`. |
| M-b | Medium | The double-submit guard was state-only; sibling mapping buttons were not disabled during a template call. | Fixed: a synchronous `useRef` in-flight guard; Suggest and Save mapping are disabled while a template call runs. |
| M-c | Medium | The client sent the name without NFC; no retire UI. | Fixed: the name is normalized to NFC before the version lookup; the retire UI is a recorded follow-up (outside slice 1). |
| Lows | Low | Route-encoding tests for `/ % ? #` names; case/space-variant names distinct; DB does not enforce the name policy (service is the guard); Arabic comma in the English unmatched list; apply needs upload permission; empty-field-set validation uses an error-field prefix (unit-tested); missing tests for sibling-button disabling and the 412 text. | Recorded follow-ups; none blocks. |
