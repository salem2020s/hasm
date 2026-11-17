# Phase 3.8D accounting bases and remaining contract proposals

Status: **accounting bases approved by the owner on 2026-11-17 as D-044/D-045; detailed transition contracts and execution remain incomplete**. PR #48 remains Draft. Approved SRS section 9 requires back-dated replay, separate approved adjustments, immutable original journals and valuation-method controls. The owner explicitly selected restoration of original accounts/dimensions and prospective conversion preserving quantity/value. See the authoritative [decision register](DECISIONS.md). Existing back-dated and method-change guards remain active; no execution is authorized until the reviewed contracts and mandatory regressions pass.

## 1. Reconciliation of settled negative-stock history

Proposal: reconcile the hypothetical revised history against the original posted effects by their original account, branch, cost center, currency and period dimensions. Produce separately approved compensation documents, linked to the original operations and settlements. Never edit or reopen an original closed obligation or posted journal. Account-map changes must not silently redirect an old correction; missing/inactive accounts or prohibited periods require explicit remapping/approval or refusal. An unresolved or accounting-pending original must not be treated as a posted effect. Adjustment dates must be explicitly previewed, authorized and permitted; Final Closed periods remain barriers.

**Approved choice (D-044):** restore original accounting classifications wherever valid and legally permitted. Routing the net difference into an arbitrary new account is not the selected default. Accounting-standard and jurisdiction-specific restrictions must be explicit configuration or release-validation requirements. Detailed transition/approval contracts remain to be independently reviewed.

Illustrative base-currency example (all journals assumed posted, accounts unchanged and all relevant periods open):

1. An uncovered issue of 7 units has provisional cost 35: debit issue expense **K 35**, credit negative-stock obligation **O 35**.
2. A later receipt of 7 units costs 28 and fully settles it: debit **O 35**, credit receipt counter-account **G 28**, credit settlement variance **V 7**. Remaining inventory is zero. The original obligation is settled.
3. A proposed earlier receipt of 7 units costs 42: debit inventory **I 42**, credit **G 42**. Chronological replay makes the issue cost 42 and leaves the later receipt's 7 units worth 28 on hand.
4. To restore the original classifications, the separate compensation is debit **K 7**, debit **V 7**, credit **I 14**. Final balances: inventory 28, expense K 42, variance V zero, obligation O zero. The original issue, receipt, settlement and journals remain unchanged. Net new debits and credits balance; the new receipt is counted exactly once.

Crediting inventory 14 and debiting only K 14 would instead leave K 49 and V credit 7. Net expense is still 42 but classification differs; this is not the owner-selected restoration basis. The approved rule must cover partial coverage, multiple settlements, returns and reversals through a reviewed transition matrix before execution is enabled. This example alone is not that matrix or a proof of all execution cases.

## 2. Prospective conversion of a nonempty position

Proposal: an approved method change is prospective at an explicit effective date. Preserve all earlier history and the carrying quantity/value; separately approve any requested value change. FIFO to average converts the remaining position total into the moving-average basis. Average to FIFO creates one auditable opening conversion layer for the remaining quantity and value, linked to the conversion document. Historical sales costs are not retrospectively recomputed merely by changing the method.

Example: FIFO layers 5 units worth 50 and 5 worth 100 become average quantity 10/value 150. An issue of 6 then costs 90 under the new method, compared with 70 if FIFO continued. Conversely, an average position 10/value 150 becomes one FIFO conversion layer 10/value 150. Carrying value does not change on conversion; any explicit revaluation needs its own counter-account policy and approval. Existing transfer and reversal links across the conversion require deterministic tests before activation.

**Approved choice (D-045):** prospective conversion preserving existing quantity and carrying value, using the basis above. Zero-stock-only conversion and retrospective method replacement were alternatives, not selected product behavior. This approval does not activate execution or define cross-conversion return/reversal/transfer contracts.

Independent review identified a further material prerequisite: reversals/returns linked to pre-conversion operations need an explicit accounting contract, not only tests. The current engine restores exactly the original consumed layers/amounts. A conversion must specify how those original layers relate to the new basis, how compensation preserves carrying value, and how linked transfer legs remain reconciled. No alternative is silently selected here; the prospective-basis proposal alone does not authorize cross-conversion execution.

## Boundaries and next work

These decisions do not resolve OD-2 purchase-cost composition or OD-11 lot/serial costing. No OPEN roadmap decision is marked approved by the agent. The internal read-only snapshot/comparison checkpoints can be verified and preserved independently. Source scheduling, complete obligation/transfer/reversal replay, proposal storage, guarded execution, revaluation policy, shared UI and phase acceptance are still incomplete. No merge of PR #48 is permitted on the strength of these checkpoints alone.
