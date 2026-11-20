# Independent review - PR #66 (key retirement safety, decision K)

* Reviewed: the full diff against `origin/main` at head `c42218d`. Reviewer: a separate read-only subagent (general-purpose, Sonnet 5.5 family, fresh context; it built the project, it did not run the PostgreSQL suite). **No Critical, no High; two Medium, several Low.** The change is conservative: it can only turn `oldKeysMayBeRetired` false, never true, relative to the previous behavior.
* Verified: the count query (sensitive, removed or otherwise not active, key null or not the active one) and both DB/envelope disagreement directions are safe; `RewrapAsync` is otherwise unchanged (existing equality assertions hold); verify's `keyIdsInUse` includes removed rows' keys; registry raised 1015 -> 1016 (AttachmentTests 9 -> 10); the new fields are aggregates behind the existing `company.attachmentssensitive.upload` gate.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | The runbook said the import files follow "the same rule", but `ImportService.RewrapAsync` has no retirability signal and skips failures silently. | Fixed in the runbook: a key must not be retired on the attachments report alone; the import rewrap report is a tracked follow-up (decision K row). |
| M2 | Medium | The runbook did not say that retiring a key makes pre-rotation backups unreadable. | Fixed: retired keys are kept with the backup sets that need them. |
| L | Low | Post-loop count is not atomic (point-in-time); no API-response assertion in `AttachmentApiTests`; no test for a failed rewrap giving false or for null key ids; awkward doc comment. | Comment tidied and the point-in-time nature stated; the tests are recorded follow-ups. |
