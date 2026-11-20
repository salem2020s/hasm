# Independent review - Phase 3.12 (attachments and key management)

* Reviewed: commits e857aea (backend) and 22fcd89 (UI) of branch `feature/phase-3-12-attachments` (stacked on PR #56). Reviewer: a separate subagent session (general-purpose agent, fresh context, read-only, security-focused code reading; it did not run the PostgreSQL suite).
* Result: **no Critical, no High.** Eight Medium and seven Low findings.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | The multipart body (up to the limit) was buffered before the upload permission was checked. | Fixed: the upload capability is checked first (403 before any form read). |
| M2 | Medium | A truncated or malformed sensitive file (`encryption_format_invalid`) was not an audited integrity failure; an IO error during a concurrent re-wrap escaped as 500. | Fixed: every decryption failure except a missing/invalid key is an audited integrity failure (409 `integrity_failure`); an IO error on read is 409 `file_unavailable`. Tests: truncated and 3-byte ciphertext. |
| M3 | Medium | Re-wrap failures were neither audited nor logged. | Fixed: each failure is audited (event 1605) and counted. A row removed mid-run keeps its file under the new key (the envelope names its key, so it stays readable) and is counted as failed. |
| M4 | Medium | Bidirectional overrides, zero-width/format characters, C1 controls and U+2028/2029 survived in names (extension spoofing). | Fixed: format, separator, private-use, unassigned and surrogate characters are removed, controls become underscores; a name of nothing else is refused. Tests. |
| M5 | Medium | Truncation to 200 units could split a surrogate pair. | Fixed: truncation never splits a pair; test uploads a name ending in an emoji. |
| M6 | Medium | Upload consistency: file written inside the open transaction (a row lock held during I/O), orphans after a crash, an ambiguous commit deleted a file whose row existed. | Fixed: cheap existence check, then the file is encrypted and written BEFORE the transaction (no lock during I/O); a failed or ambiguous commit deletes the file only when no row exists; `SweepOrphansAsync` (and `POST .../maintenance/sweep`, same permission as the rotation) removes files no row names that are older than one hour, audited (1606). Directory-entry fsync after the rename is not done: recorded as a hardening follow-up. |
| M7 | Medium | The plaintext SHA-256 of a sensitive file was returned by the API and its prefix copied into the audit (a confirmation oracle for low-entropy documents). | Fixed: never returned and not audited for a sensitive file (stored only for the integrity check). |
| M8 | Medium | API authorization was company-wide by `company.attachments.*` only, without the owning module's view permission (the UI tab honoured it, the API did not). | Fixed: list, upload, download and removal also require the view permission of the owning module (`company.parties.view` for parties, `company.items.view` for items); per-record scoping (branch/document) remains with 3.13. |
| L1 | Low | Content checks are prefix-only; any ZIP passes as docx/xlsx; no malware scan; CSV formula injection. | Accepted and documented: downloads are always attachments (never rendered), a malware scanner and Office-structure validation are deployment/feature follow-ups. |
| L2 | Low | Content type was the raw client value (charset rejected; Windows labels .csv as application/vnd.ms-excel). | Fixed: parameters are dropped; ms-excel is read as text/csv only for a .csv name. |
| L3 | Low | No per-company quota; `MaxFileBytes` up to 512 MB vs the form default. | Accepted: quota policy is an owner decision (blocker register); the upload path enforces its own limit before buffering. |
| L4 | Low | `$` in the name regex also matches before a trailing newline. | Accepted (not exploitable: names are generated). |
| L5 | Low | Windows temp-file ACL window; no reparse-point check. | Accepted: the directory is owner-only and checked; revisit with the installer. |
| L6 | Low | Each failed read of a corrupt file adds an audit row. | Accepted (a flood needs an authorized user and a corrupt file; rate limiting is a platform follow-up). |
| L7 | Low | The removal dialog ignores an empty reason silently. | Accepted (the server also requires it). |

Checked without defect: CSRF (custom header, SameSite=Strict, JSON + If-Match on removal), download headers, hiding of sensitive files, encryption context binding and re-wrap crash window, file store names and company directories, schema regex and trigger, runtime privileges, permission catalog and preset count, CI registry (now 1003), translation parity.

## Second independent review - full diff against main after the integration of #53/#54/#56 (2026-11-20, head 70fbe87)

Reviewer: a separate read-only subagent (general-purpose, Sonnet 5.5 family, fresh context); code reading only, it did not run the PostgreSQL suite. **Verdict: no Critical, no High.** Verified without defect: company isolation (company only from the session), generated storage names and company directory, download authorization and headers, magic-byte allow-list (no HTML/SVG), sanitized names, size limits and CSRF, removal concurrency, migration 0024/0026 (additive, no delete/truncate, runtime SELECT/INSERT/UPDATE only, contiguous after 0023/0025), registry raise-only (989 -> 1003).

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | Removed sensitive attachments are never re-wrapped; the old master key cannot be retired while any exists. | Follow-up (owner decision on key retirement): either a narrow trigger exception for re-wrapping removed rows or keep old keys; until decided the drill documentation must say the old key stays available. Fail-closed today (`KeyUnavailable`). |
| M2 | Medium | ASP.NET spools multipart parts over 64 KB to the OS temp directory unencrypted. | Follow-up: stream or relocate the temp path; deployment requirement (encrypted temp disk) recorded in docs/INTEGRATION-STATUS.md. |
| M3 | Medium | A file is held in memory 2-3 times per request; `MaxFileBytes` is configurable up to 512 MB (default 25 MB). | Follow-up: lower hard cap and concurrency limit. Default configuration is safe. |
| M4 | Medium | Record said registry 1002; workflow says 1003. | Fixed in this commit. |
| Lows | Low | 403 vs 404 permission oracle; ZIP-prefix check for docx/xlsx (accepted L1); stale `*.att.tmp-*` files not swept; sensitive file names stored in clear; non-JSON 413 handling in the UI; only sensitive downloads audited; re-wrap bumps revision. | Recorded as follow-ups; none blocks. |
