---
name: hasm-pr
description: Create, review and verify HASM pull requests on their exact head.
---

Follow the persistence, review and autonomous merge policies in `CLAUDE.md`. Check for an existing PR before creating a draft; push verified checkpoints and monitor applicable Actions. Review the full diff against current `origin/main` adversarially and obtain the required independent full-diff review in a separate context, recording it using `docs/review-records/TEMPLATE.md` and including the reviewer prompt and model family; never claim independence for a self-review. Resolve Critical/High findings and record remaining Medium follow-ups. Check mergeability and all applicable gates in `.github/required-checks.json`. Run `node tools/verify/verify-head.js --pr <number> --expect-sha <full-sha>` from main's copy, then use the SHA-pinned merge specified in `CLAUDE.md` only if every gate holds. A new head requires all gates again. If review or CI is unavailable, leave the PR open and report the blocker. Respect the explicit owner-instruction exceptions in `CLAUDE.md`.
