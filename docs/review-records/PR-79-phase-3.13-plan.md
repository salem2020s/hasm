# Independent review record - PR #79, Phase 3.13 commercial document framework PLAN (documentation only)

* **Pull request:** #79
* **Head SHA reviewed (full 40 characters):** 085f1d2d8116c26c702003103879be242e8b452e
* **Reviewer context:** Agent C, a separate session (the same model family as the author, fresh context, read-only). Prompt: independent adversarial review of the plan against the approved SRS and the merged code, report to the lead only. Independent of the author's context, but NOT a different model family. Round 1 reviewed head 0c08ed8ee555b47e4762f6928da36985c4b1c4e9; round 2 reviewed head 67cb609bfee1e3f104ff5be6ca09abc0695fa92f; round 3 reviewed head 085f1d2d8116c26c702003103879be242e8b452e.
* **Scope read:** the whole `docs/PHASE-3.13.md` at both heads, `docs/ROADMAP.md` Phase 3.13, `docs/RELEASE1-ACCEPTANCE.md`, and the cited source (SRS sections, `StockPosting.cs`, `NumberingModel.cs`, `PricingConfigService.cs` and the phase documents 3.7A-3.7D, 3.10, 3.11).
* **Checks run (commands and results):** reading and grep only; nothing executed. Documentation CI (`check-docs.js`) passes on the author's heads.
* **Round 3 (head 085f1d2d8116c26c702003103879be242e8b452e):** Agent C (same model family, delta since 67cb609 read-only, nothing executed) confirmed by reading that all round-2 findings are fixed (M1 design (a) via the merged 0013 life cycle with stock approve + warehouse post permissions, Q17 for option (b), stock_authority_missing, no_independent_approver definition; M2 Q19; M3 slice/test boundary; Lows). New findings: Medium S1a SQL guard text self-contradictory; Lows: invoice SoD must exclude the submitter; Q18 S1b-vs-S3 inconsistency; BranchScope S1a mentions approve. Dispositions: fixed in the next commit (wording verified by the lead against the diff, not re-reviewed in full).
* **Findings:** round 1 - one High and several Medium; round 2 - three Medium and several Low. No code exists.

| Round | Sev | Finding | Disposition |
|---|---|---|---|
| 1 | High | Line snapshots were described as stored from the client; a hash proves consistency, not authenticity. | Fixed in 67cb609: price, tax, rate and cost are produced server-side in the line-save transaction; client-supplied values and overrides without permission and reason are refused. |
| 1 | Medium | `StockPostingService.PostAsync` owns its transaction, so "one stock effect in the same transaction as the approval" was impossible. | Fixed in 67cb609: stated as a required, separately reviewed change to merged 3.7B code. |
| 1 | Medium | Approval of the auto-created stock document unspecified. | Fixed in 67cb609, refined in round 2 (below). |
| 1 | Medium | "One stock AND one ledger effect" cannot be met without the account mapping (Q5). | Fixed in 67cb609: acceptance caveat stated; S4 tests the pending path only until Q5. |
| 1 | Medium | Direct/separate Mode scope; material-field list presented as fact; permission scope; purchase receipt valuation; missing concurrency tests; S1 too large and its test type unnamed. | All fixed in 67cb609 (Q15, Q16, proposal wording, branch scope with test, added race/forced-failure/period-close tests, S1a/S1b split). |
| 2 | Medium | The auto-created stock document "born approved" contradicted the merged stock-document life cycle (0013). | Fixed in 085f1d2d8116c26c702003103879be242e8b452e: design (a) system drafts, submits as the invoice's submitter and the invoice approver approves in the same transaction (needs stock approve and `company.stockdocs.post` per warehouse); option (b) derived approval is an owner option (a change to 0013); Q17; when `no_independent_approver` can arise. |
| 2 | Medium | The moment the price/tax snapshot is sealed was unspecified. | Fixed in 085f1d2d8116c26c702003103879be242e8b452e: Q19 listed as S1b-blocking with options and the recommended default (re-quote at submit and approval, `pricing_changed`, seal at approval). |
| 2 | Medium | S1a/S1b boundary and test placement unclear. | Fixed in 085f1d2d8116c26c702003103879be242e8b452e: S1a lines carry no price/tax; each test class is placed in exactly one slice (registry rises by each slice's tests); cancel-of-approved voiding the number moved to S1b. |
| 2 | Low | Q1 test-type registration, missing-cost rule, Q16 default, Q8 split, Q14-Q16 ordering, naming of the `JournalService` in-work pattern. | Fixed in 085f1d2d8116c26c702003103879be242e8b452e (test composition only; Q18; explicit amount only with the PO price as an owner option; self-approval labelled as required by SRS 4.3; reordered; `JournalService.CancelInWorkAsync/ReverseInWorkAsync` named). |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** nothing was executed; no code exists; the owner decisions Q1-Q19 have not been made; the plan's feasibility against the merged code beyond what the reviewer read (for example the exact shape of the ambient-transaction change to 3.7B) is not proved.
* **Verdict:** approve
