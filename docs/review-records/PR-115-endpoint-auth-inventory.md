# Independent review record

* **Pull request:** #115
* **Head SHA reviewed (full 40 characters):** abe2013452935d56ef4d19df2b0d361a9a23a18f
* **Reviewer context:** Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent full-diff review of #115 endpoint authorization inventory: enumeration source, allowlist justification, admin policy, mutation sensitivity, no production change"
* **Scope read:** `git diff origin/main...head`; AuthEndpoints, HasmAuthentication (CSRF, policies), Program.cs
* **Checks run (commands and results):** `dotnet test tests/integration/Hasm.Api.Tests` (105/105 passed); 5 local mutations (unprotected route, admin without policy, AllowAnonymous in protected group, no-leading-slash route, non-/api route; each reverted)
* **Findings:**
  * M1 (Medium): the `RawText.StartsWith("/api/")` filter misses routes mapped without a leading slash and routes outside /api. Fixed in 218bfeb48c84a18d23850db24eb0a75eeb9d0e60 (every RouteEndpoint enumerated, pattern normalised with a leading slash, non-/api routes must be protected or allowlisted). Re-run: the no-leading-slash and non-/api mutations now fail 3 of 6 tests each (author re-run, G to confirm the delta).
  * L1 (Low): endpoints without HttpMethodMetadata were probed with GET only. Fixed in 218bfeb48c84a18d23850db24eb0a75eeb9d0e60 (all of GET/POST/PUT/PATCH/DELETE probed; an `app.Map` mutation now fails).
  * L2 (Low): no authenticated non-administrator probe. Fixed in 218bfeb48c84a18d23850db24eb0a75eeb9d0e60 (policy evaluated for anonymous, plain, non-admin and admin principals; no database needed).
  * L3 (Low): `IsProtected` accepts any IAuthorizeData. Fixed in 218bfeb48c84a18d23850db24eb0a75eeb9d0e60 (comment states that the 401 probe and the policy test prove the behaviour).
* **Blocker or High findings open:** none
* **NOT VERIFIED:** G's confirmation of the delta after 218bfeb48c84a18d23850db24eb0a75eeb9d0e60; behaviour with a real session (needs PostgreSQL, out of scope)
* **Verdict:** request changes (M1 and L1-L3 fixed in 218bfeb48c84a18d23850db24eb0a75eeb9d0e60; awaiting G's confirmation of the delta)
