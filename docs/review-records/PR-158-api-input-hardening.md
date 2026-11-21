# Independent review record

* **Pull request:** #158 (test+fix(api): hostile-input sweep over every endpoint; NUL in text input is a mapped 400, PostgreSQL 22021 maps to 400)
* **Head SHA reviewed (full 40 characters):** c18ce97081730c567b5e3832b26e25004e1cdb88
* **Reviewer context:** Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent full-diff review of Draft PR #158 API input hardening and the NUL guard / 22021 mapping: outages not masked, unscanned bodies fall back without 5xx, body rewind and buffering, 400-before-401 leaks nothing, matrices' 5xx detection, registry +3"; Head SHA reviewed c18ce97081730c567b5e3832b26e25004e1cdb88; executed: full diff vs origin/main read, Hasm.Api.Tests InputHardening 28/28, isolated PostgreSQL ApiInputHardeningTests 3/3 passed, temporary uncommitted probe (chunked JSON bodies with NUL on six endpoints: all 4xx, no 5xx, deleted), mutation (guard and 22021 mapping removed) failed 2 of 3 tests with 503s detected (14 of 4271 query requests), reverted; verdict approve; findings L1 (pre-auth allocation sized by the declared Content-Length before the endpoint limit applies), L2 (a 22021 wrapped in the audit, encryption or company exceptions still maps to 503), L3 (no committed chunked-body NUL test).
* **Scope read:** the whole diff against `origin/main`: `Hasm.Api/Hosting/InputHardening.cs`, the exception-handler change, `Program.cs`, `Hasm.Api.Tests/InputHardeningTests.cs`, `ApiInputHardeningTests.cs` and `backend.yml`.
* **Checks run (commands and results):** by the reviewer: as listed above (the mutation check confirms the matrices detect the defect). By the author: isolated PostgreSQL `ApiInputHardeningTests` 3/3 passed with zero 5xx; `Hasm.Api.Tests` 145 passed; unit suite 1333 passed; `ci-guard` against `origin/main` clean.
* **Findings:** verdict approve; 0 Critical/High/Medium, 3 Low.

| ID | Severity | Finding | Resolution |
|----|----------|---------|------------|
| L1 | Low | the guard allocates a pre-authentication buffer sized by the declared Content-Length (up to 1 MiB) before the endpoint's own limit applies | Recorded as a follow-up: size the read with `ArrayPool` or cap it at the endpoint's limit; no change in this PR (the scan limit already bounds it at 1 MiB and only JSON requests with a known length are buffered). |
| L2 | Low | a 22021 wrapped in the audit, encryption or company exceptions still maps to 503 | Recorded as a follow-up: those exception types are handled by their own arms before the generic mapping; unwrap them for 22021 as well. Not reachable through the guarded query and JSON inputs (the NUL never reaches the database). |
| L3 | Low | no committed test for a chunked body containing NUL | Recorded as a follow-up: add a committed chunked-body NUL test (the reviewer's temporary probe showed all 4xx and no 5xx on six endpoints; unscanned bodies fall back to the 22021 mapping). |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** hosted CI on the reviewed head; the full PostgreSQL suite (only the hardening class was run in isolation).
* **Verdict:** approve
