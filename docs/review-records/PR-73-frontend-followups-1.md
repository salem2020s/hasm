# Independent review record

* **Pull request:** #73 (Frontend follow-ups 1)
* **Head SHA reviewed (full 40 characters):** d923743fbf5e3593d430288b5e1cb4c6aa0053e6
* **Reviewer context:** Agent C, a separate session from the author (Agent E); model claude-sonnet-5-5, the SAME model family as the author. Prompt: "independent review of PR #73 ..., read-only, focus Web/Tauri parity, server-side authz, RTL/ar-en, a11y, SearchPicker race/selection, test adequacy".
* **Scope read:** the full 11-file diff of the PR against `origin/main` (Imports.tsx, messages.imptpl.ts, importTemplates.test.tsx, PartyRoles.tsx, partyRoles.test.tsx, SearchPicker.tsx and its test, ui index.ts, messages.ts, app.css, api-client index.test.ts).
* **Checks run (commands and results):** by the author, from `src/frontend` at the reviewed head: typecheck clean; web 446, api-client 38, ui 27 tests passed; production build OK; hosted CI on the reviewed head: all 10 jobs green. After the fixes below (author, local): typecheck clean; web 447, api-client 38, ui 33 tests passed; production build OK. Hosted CI for the fix commit is recorded in the PR.
* **Findings:** no Critical, no High, three Medium, several Low.

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| M1 | Medium | `SearchPicker.tsx` effect deps | `load` is held in a ref and the effect depends only on search/run/debounce, so a changed parent filter never reloaded. | Fixed: `reloadKey` prop in the effect deps; tests for reload on key change and no extra request on the same key. |
| M2 | Medium | `SearchPicker.tsx` | Every picker search box had the same accessible name; the live region announced "Loading" per keystroke. | Fixed: name is "<label> — <search>"; loading text is visual only (`aria-hidden`), the live region announces results and errors only; tests. |
| M3 | Medium | PR description | The picker is used by no screen, so PR-69 M2 (silent truncation in the pricing pickers) is NOT closed; the description said done. | Fixed: description corrected; PR-69 M2 stays open until Agent A (or a later slice) adopts the component. |
| L1 | Low | `SearchPicker.tsx` | An unknown selected id could show the raw GUID. | Fixed: generic `picker.unknown_selection` text; callers should pass `selectedLabel`; test. |
| L2 | Low | `SearchPicker.tsx` | Stale results stayed after a failed search. | Fixed: results are cleared on failure; test. |
| L3 | Low | `SearchPicker.tsx` | No `dir="auto"` on the search input; codes cannot be wrapped in `<bdi>` inside `<option>`. | Fixed: `dir="auto"`. Option text is plain by HTML rules; documented in the component comment (callers may use Unicode isolates). |
| L4 | Low | `messages.imptpl.ts` | The template name was not isolated in the Arabic retire message. | Fixed: wrapped in FSI/PDI isolates. |
| L5 | Low | `Imports.tsx` retire handler | Version 0 was sent when the template was not in the list; a click swallowed by the in-flight guard gave no feedback. | Fixed: aborts with a clear message and reloads; a message is shown when the guard swallows the click; test for the missing-template path. The busy path is not reachable through the UI (the fieldset is disabled while busy) and is not separately tested. |
| L6 | Low | tests | Gaps: debounce coalescing, disabled, `allowNone=false`, load/filter change, unmount during a request. | Fixed: tests added for each. |
| L7 | Low | `Imports.tsx` | The `danger` button class. | Checked: `button.danger` exists in `app.css` and is the class already used by `ConfirmDialog` and the import cancel button; no change. |
| F1 | Low (copy) | `messages.imptpl.ts` | The Arabic wording "إحالة إلى التقاعد" is an owner copy decision. | Kept as is; **recorded follow-up** for owner review. |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** no visual review of RTL/LTR rendering or of the new picker styling in a browser; no local Tauri compile or Playwright run (hosted `desktop-build` and `e2e-smoke` ran green on the reviewed head); the reviewer was read-only and ran no code; the reviewer shares the author's model family, so independence is limited to a separate session and context.
* **Verdict:** approve (after the Medium fixes above; subject to green hosted checks on the final head)
