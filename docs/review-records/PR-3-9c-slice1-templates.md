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
