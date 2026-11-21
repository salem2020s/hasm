# Independent review record - PR #103 (server-side active filter for the customer-group picker)

* **Pull request:** #103
* **Head SHA reviewed (full 40 characters):** da5e415b2f1aba00b31bf47575ebd27c62db7fd4
* **Reviewer context:** Agent C (independent QA/security), a separate session from the author (Agent E); model family Claude Opus (claude-opus-5-5). Prompt: lead's full-diff review of #103 (server-side active filter for the customer-group picker: N of M correctness, membership-screen regression, removed filter prop, test strength, the missing inactive-selection picker test).
* **Scope read:** git diff origin/main...da5e415 (Pricing.tsx, pricing.test.tsx) plus the server's SearchCustomerGroupsAsync and the api-client call.
* **Checks run (commands and results):** npm ci --ignore-scripts; typecheck; web vitest 489/489 in 36 files (testTimeout 30 s); ui 37/37; production build OK; hosted CI on da5e415 all 10 checks green; merges cleanly with main.
* **Findings:** none. N of M is correct (the server applies IsActive in the same WHERE as the window total); the membership screen still uses the unparameterized preload including inactive groups; the removed filter prop had no other user; tests assert active=true on every picker request, the exact 2-of-600 notice and that an inactive group is never offered; saving with an inactive group is still refused server-side (customer_group_inactive). The picker-level 'selected inactive group still shows' test is not needed today (the picker is used only in create forms; selectedLabel comes from the preload, which includes inactive groups); add one when an edit form uses this picker.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** browser rendering; local Tauri build.
* **Verdict:** approve
