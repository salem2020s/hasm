# Independent review record - PR #74, backend Medium follow-ups (attachments #57, representatives #56)

* **Pull request:** #74
* **Head SHA reviewed (full 40 characters):** 85feef1caabc81d9c37975c01ddd7ca4f33b2109
* **Reviewer context:** Agent C, a separate session (the same model family as the authors, fresh context, read-only). Prompt: "independent FULL-diff adversarial review, read-only" of the complete diff against `origin/main`. Independence is limited to a separate context; the model family is the same as the authors'.
* **Review history:** first review of head `a3a231eb499b7e2324f67b1a19e94597f0612f26` (findings below); re-review of head `85feef1caabc81d9c37975c01ddd7ca4f33b2109` after the fixes (section "Re-review" below). The 40-character SHA above is the re-reviewed head.
* **Scope read:** the full diff against `origin/main` at the reviewed head (upload endpoint, attachment service/options/sweep, party-role management, tests, `backend.yml` registry).
* **Checks run (commands and results):** code reading only by the reviewer. The author ran nothing locally (no dotnet in the sandbox); hosted CI on the reviewed head: backend-build-test success, ci-guard, docs-traceability, secret-scan, guard-selftest, dependency-scan success; postgres-integration was still running when the review was written.
* **Findings:** no Critical, no High, three Medium, several Low.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | The upload gate was process-global with no per-user share and no deadline or minimum data rate: one user or a slow sender could hold every slot. | Fixed in the follow-up commit: `UploadGate` has a per-user ceiling (`MaxConcurrentUploadsPerUser`, default 2) beside the total; every upload has an overall deadline (`UploadTimeoutSeconds`, default 120, 408 `upload_timeout`) and Kestrel's minimum request data rate is demanded on the route; slots are disposed on every path. Unit tests of the gate and options. |
| M2 | Medium | No HTTP tests of the new paths. | Fixed: three PostgreSQL HTTP tests (a held slot gives 503 `upload_busy` and 200 after release; oversize non-file field, second file part, too many parts, truncated and non-multipart bodies, an oversize chunked file without Content-Length, and a chunked file within the limit; the slot is released after every kind of failure with a single-slot configuration). Registry 1026 to 1029. |
| M3 | Medium | `ReadBounded` grew a MemoryStream and copied with `ToArray` (2-3x peak) while the comment said "one bounded copy". | Fixed: the buffer is preallocated from the request length when known (no growth copy), the exact-length array is taken without a copy when the sizes match, and the real worst case (about 3 to 4 times the file size per upload, times the gate) is stated in the code comment and in `docs/PHASE-3.12.md`. |
| L1 | Low | Field count was unbounded. | Fixed: at most 16 parts, otherwise 400. |
| L2 | Low | `filename*` was taken raw. | Fixed: the plain `filename` is preferred; `filename*` is RFC 5987 decoded only when the plain one is absent. |
| L3 | Low | A configured `MaxFileBytes` above the cap silently became the default. | Fixed: clamped to the 100 MB hard cap (a non-positive or unparsable value still uses the default); unit tests. |
| L4 | Low | `SweepOrphansAsync` accepted a zero or negative age. | Fixed: the age is clamped to at least 5 minutes. |
| L5 | Low | No `att.error.upload_busy` message. | Fixed: Arabic and English `att.error.upload_busy` and `att.error.upload_timeout` in `messages.attachments.ts` (the only frontend line touched). |
| L6 | Low | A malformed multipart body answered 413. | Fixed: malformed or truncated bodies are 400 `invalid_request`; 413 is only for a too-large file (or a 413 from the host). |
| L7 | Low | `docs/PHASE-3.12.md` did not describe the new limits. | Fixed: new section (100 MB cap, `MaxConcurrentUploads`, per-user limit, 503, deadline, 404 for a hidden owner, temp-file sweep, memory worst case). |

## Re-review (head 85feef1caabc81d9c37975c01ddd7ca4f33b2109)

Reviewer: Agent C, a separate session (the same model family as the authors), full diff against `origin/main`, code reading. It verified that M1, M2, M3 and L1-L7 above are fixed as recorded (per-user gate, deadline and minimum data rate, the three HTTP tests, the preallocated bounded read and the stated memory worst case, field cap, filename handling, clamps, 400 for malformed multipart, messages, docs) and that the compile fix (qualified Kestrel feature type) is the only change since the previous head. **No Critical, High or Medium finding is open.** Three new Low findings:

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| L8 | Low | `ReadBounded` allocates its buffer from the client-supplied Content-Length before the data arrives. | Accepted and documented: the allocation is capped by `MaxFileBytes` and the number of uploads by the gate, so the exposure is gate size times the cap. |
| L9 | Low | A timeout from Kestrel's minimum data rate surfaces as 400 `invalid_request` rather than 408. | Accepted (cosmetic): the connection is being aborted for a stalled sender; the overall deadline path does return 408. |
| L10 | Low | The 16 KB/s floor may abort uploads over very slow links. | Accepted (policy call): make the rate configurable if field use shows a problem. |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** behaviour under a real Kestrel host (the HTTP tests run on the in-memory test server, which has no minimum-data-rate feature, so that setting is exercised only by code review); memory use under a real concurrent load at the 100 MB cap.
* **Verdict:** approve
