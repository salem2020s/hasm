# Independent review record

* **Pull request:** #86 (Frontend follow-ups 3)
* **Head SHA reviewed (full 40 characters):** 12387dfe949f321ddcc71f8e0f0a0b097ce43152
* **Reviewer context:** Agent C, a separate session from the author (Agent E); model family Claude Opus, a DIFFERENT family from the author. The reviewed head is the merge of main 16c994f into the work head d3c7778bf8fc8ee69c6a9fd049bb968f3815a9a0; the source is identical (verified with `git diff --quiet d3c7778 12387df -- src`). Prompt: independent FULL-diff review, read-only, of the code-first `codeName`, the ledger/party-role catalog collision fix (`field.pr.*` plus scoped `fieldErrors` / `useRunner`, every call site checked), `messagesUnique.test.ts` coverage, no fan-out after a failed item list, and the L-d / L-f tests.
* **Scope read:** `git diff origin/main...12387df` (9 frontend files) plus every `useRunner` / `fieldErrors` call site and every `account_not_*` definition.
* **Checks run (commands and results):** `npm ci --ignore-scripts` (0 vulnerabilities); typecheck exit 0; packages/ui 35/35 passed; apps/web 472/472 passed in 36 files (60 s timeout allowed for machine load, none needed); `npm run build` OK; a one-off scan of the inline keys of `messages.ts` against the module keys (one collision: `field.too_long`); hosted CI green on d3c7778 (10/10).
* **Findings:** no Critical, no High, no Medium. The party-role screens keep their texts; other screens showing `account_not_found` / `account_not_postable` now use the ledger wording, which is the intended fix. Three Low:

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| L1 | Low | `messagesUnique.test.ts` | Keys defined inline in `messages.ts` are not covered; an existing collision is `field.too_long` (base: "The value is longer than allowed." vs attachments: "The text is longer than allowed."). | Accepted follow-up (export or move the base catalogs, include them in the test, keep the base wording and give attachments a scoped key). |
| L2 | Low | tests | The "simply unset" base-currency case is still untested. | Accepted follow-up. |
| L3 | Low | `Pricing.tsx` | A 401 also sets `itemsFailed`. | Accepted; harmless (the session ends anyway). |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** browser type-ahead for the code-first option labels; a local Tauri build (hosted `desktop-build` ran green on d3c7778).
* **Verdict:** approve
