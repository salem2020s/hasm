# Phase 3.8D source-bound consistent read contract

Status: proposed implementation contract, not an executable plan or permission to post. Existing backdated, method-change, Final Closed and approval barriers remain. D-044/D-045 are unchanged.

## Whole-document evidence

A stock document can contain 500 lines and several items. Independent per-item snapshot calls are insufficient: they can observe different committed histories. The next internal reader must load all requested item/warehouse components using one company connection, one read-only REPEATABLE READ transaction, one deadline and one application-SQL quota. It must never concatenate results from separate transactions. Methods remain resolved per item; a document can contain both FIFO and moving-average items.

The bounded internal input is at most 500 distinct item requests and 10,000 raw item/warehouse root pairs. Reject empty/default arrays, empty identifiers and duplicate item requests before company reads; repeated warehouse roots within one item are canonicalized after the raw bound. Canonical output ordering is item Guid in ordinal N format, independent of caller order or culture. Each item retains its own closure; an authorized warehouse discovered for one item must not seed another item's unrelated history.

Authorize all requested warehouses before opening company data. Inside the shared transaction, discover and quarantine references for EVERY item before reading valuation/policy data or exposing structural diagnostics. Every newly discovered warehouse must enter the retained final-authorization set before a denied or failing operation can return. Final live authorization runs across that union on success and caught failures; exhausted quota/cancellation returns no evidence or detailed diagnostics. No permission cache replaces live checks.

Bounds apply to the whole operation, not independently to each item: at most 10,000 distinct (item, warehouse) positions, including empty roots and quarantined references; 100,000 distinct operation groups (one operation shared by several items is counted once); 200,000 loaded entries; 400,000 loaded layer/consumption components; 400,000 discovery rows including duplicates; 20,000 application-issued SQL commands including authorization, registry, schema and transaction cleanup; 30 seconds. Existing arithmetic work limits additionally apply to each complete item replay. No truncated or partial result is returned. Do not allocate an unbounded cross product of items and warehouses.

## Binding the actual source, next integration

The source header, revision, ordered lines, posting/reversal facts and all item histories must ultimately be read in that SAME transaction. An internal multi-item history read alone does not satisfy this source contract. A caller-supplied snapshot is never accepted as database truth. Source identifiers, revision, effective date, validated line payload and entered receipt amounts must be bound into the later server-generated plan fingerprint together with history, method/basis, fiscal/account configuration and complete proposed adjustment contents. Live permission revocation is checked independently of fingerprint equality.

Reuse normal document/posting validation. StockPostingService currently requires Approved and the expected revision, re-resolves warehouses/lines and refuses changed BaseQuantity with line_conversion_changed. Preserve persisted Factor/BaseQuantity and reject changed conversion; do not silently recalculate a different document. Receipt/issue children retain document line order and individual rounding. Existing transfer posting aggregates duplicate item quantities exactly before calling the engine; a source plan must reproduce that behavior rather than inventing per-line transfer allocations. ReadAmounts requires every receipt line exactly once and forbids supplied amounts for issues/transfers. Reuse these rules, plus existing currency precision/numeric bounds, rather than maintaining a divergent validator. Read-only evidence must not imply that Draft/Submitted documents are executable or that Approved means physically posted (StockPosting is a separate fact).

No ordinary journal or independently postable adjustment draft is created by preview. Original account/dimension compensation, complete reversal/negative/conversion modeling, approved atomic execution, stale-plan rejection and shared Web/Tauri UI remain mandatory unfinished scope. A history fingerprint is neither approval nor an executable plan token.

Normal ResolveWarehouses/ResolveLines currently issue FOR SHARE reads through CompanyAuditedWork. Those methods cannot simply be called inside a read-only transaction. The next integration must share their validation rules through an explicitly bounded read adapter/pure validation core, while retaining the existing locking behavior for posting. Do not substitute an audited write transaction for preview or remove posting locks. Source header/line reads also need explicit SQL/row caps; the general document display loader includes an unbounded event list and is not itself the bounded evidence contract.

## Mandatory regression evidence

- Both item methods in one read; canonical results unchanged by input order; no writes or empty position creation.
- A writer commits changes to a later item after the first snapshot read: all returned items retain the earlier snapshot; a fresh read observes the commit.
- A malformed first item plus a hidden downstream warehouse in a later item returns generic forbidden, never the earlier diagnostic; hidden roots fail before company reads.
- Peer/membership revocation masks both successful and failing multi-item reads; cancellation/deadline returns no partial result.
- Cross-company isolation; per-item closure isolation; whole-operation position/group/entry/component/discovery/SQL limits, including repeated groups shared across items and empty roots.
- Source integration subsequently proves revision/line/amount/status/posting changes, changed conversion, period/basis/mapping changes and source/approval binding before persistence or execution is enabled.

Independent full-diff design review is required before implementing this contract. Actual execution evidence must be recorded separately; this document claims no new runtime capability.
