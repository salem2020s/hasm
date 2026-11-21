# Independent review record

* **Pull request:** #139 (Phase R slice R3: shared Web/Tauri attachment-retention screen; frontend only)
* **Head SHA reviewed (full 40 characters):** 4b0cd0733455b92a76712aeedf9ded7f5d8164b7
* **Reviewer context:** Reviewer: Agent H (independent review, separate agent; not the author); model family Claude Sonnet; prompt: lead's independent full-diff review of draft PR #139 (R3, the shared attachment-retention screen, frontend only) against docs/PHASE-R-attachment-retention.md (D-108..D-115), R1's endpoints and permissions on main, and CLAUDE.md: the UI mirrors the server in both directions (approve, revoke, hold release, the two-person rules), no purge action and an accurate D-109 banner, the Tabs standard, ar/en key parity and RTL/LTR, keyboard access, shared frontend with Web/Tauri parity, server refusal codes mapped to messages, and the new tests covering the rules; Head SHA reviewed 4b0cd0733455b92a76712aeedf9ded7f5d8164b7; executed: git diff origin/main...head (6 files, +673), full read of `AttachmentRetention.tsx`, the message file and the test file, comparison with `AttachmentRetentionEndpoints.cs`, `AttachmentRetentionService.cs` and `PermissionCatalog.cs` on main, ar/en key-parity script, export of the head with `npm ci`, typecheck and the retention and catalog tests; Tauri compile not run; verdict request changes; findings 1 High, 2 Medium, 5 Low.
* **Scope read:** the 6 changed files of the PR (page, messages, tests, api-client, App, messages registration) against the R1 endpoints, service and permission catalog on main.
* **Checks run (commands and results):** by the reviewer: as listed above. By the author after the fixes: `npm run typecheck` clean; web suite 44 files, 899 tests passed (28 in `retention.test.tsx`); `npm run build` OK. The hosted checks on 4b0cd07 (including `desktop-build`) were green before the fixes; they are repeated on the new head.
* **Findings:** verdict request changes; 1 High, 2 Medium, 5 Low.

| ID | Severity | Finding | Resolution |
|----|----------|---------|------------|
| H | High | Release was shown on `caps.hold && isActive && placedBy !== me`, but the server's release requires `company.attachmentretention.approve`: an approver could not see Release and an administrator saw a button the server always refuses (403) | Fixed in 338aa63: Release and the own-hold hint are gated on `caps.approve`. The other rows were compared with the endpoints: view (all reads), configure (draft), hold (place), approve (approve, revoke, release); all match. Tests: a hold-only user sees no Release and still sees Place; an approve-only user sees Release and no Place. |
| M1 | Medium | missing messages for `shortening_acknowledgement_required`, `effective_date_passed`, `concurrent_change`, `attachment_not_found`; the approve dialog did not require the acknowledgement | Fixed in 338aa63: four `ret.error.*` messages in Arabic and English (parity and uniqueness tests pass); the shared `ConfirmDialog` gained an optional `confirmDisabled` prop (backwards compatible), and Approve stays disabled while the version shortens a period and the acknowledgement is empty. Tests map each refusal to its own message and check the disabled state. |
| M2 | Medium | revoke and release dialogs allowed an empty reason | Fixed in 338aa63: Confirm stays disabled until the reason is non-empty; tests for both dialogs. |
| L1 | Low | no unsaved-changes guard for the draft | Fixed in 338aa63: `useReportUnsaved` for the policy draft and the hold form. |
| L2 | Low | SearchPicker for hold targets | Recorded as a follow-up in `docs/PHASE-R-attachment-retention.md` ("R3 follow-ups"); it needs a server search for removed attachments. |
| L3 | Low | tables without captions | Fixed in 338aa63: visually-hidden captions on the three tables. |
| L4 | Low | `.replace` instead of `t(key, params)` | Fixed in 338aa63: both places use the params form. |
| L5 | Low | Arabic-Indic digits in the months field | Recorded as a follow-up (same shared numeric-input decision as the fiscal-year screen). |

* **Blocker or High findings open:** none after 338aa63, pending H's confirmation and hosted CI on the new head.
* **NOT VERIFIED:** the Tauri compile was not run locally (no webkit2gtk on the build host); the hosted `desktop-build` check is the arbiter and was green on 4b0cd07. Routine manual visual review is deferred to the final validation milestone.
* **Verdict:** request changes
