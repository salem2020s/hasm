# Multiline operation replay contract

Status: internal scheduler/block arithmetic implemented; general database snapshot/adapter integration is still pending. No public feature or execution is enabled. Existing ReceiveAsync/IssueAsync preserve individual input lines, including repeated item/warehouse positions; transfers alone aggregate/refuse duplicate item lines. Replaying an ordinary operation as one summed line is incorrect.

## Indivisible groups and exact ordinal ranges

One stored operation/item remains one scheduling group. For each distinct affected position, declare its first stored entry ordinal and positive entry count. This is a contiguous range, not a quantity aggregation. Receipt/issue groups may have several positions and several entries per position. All individual entries remain available in their original intra-operation order.

On every position, ranges must cover exactly1 through LastSeq without overlap, gap, duplicate position leg or interleaving another operation inside a group. First+count arithmetic must not overflow. A transfer still has exactly two distinct positions with one entry each for the selected item. A whole-operation reversal must match its original's complete position set AND entry count on each position, not just warehouse membership; scheduler acceptance is not reversal-cost feasibility.

Use the existing canonical date/identity priority, preserving group atomicity and per-position edges. A proposed group may touch multiple distinct positions, with a declared positive entry count for each. Insert after all existing groups on its effective date on EVERY touched position and before later groups. It may not split a historical range. Count all individual historical/proposed entries against the existing200,000-entry work bound, rather than counting each range once; retain100,000 groups,10,000 positions and400,000 edges. Unsupported kinds, malformed ranges, cycles, bounds and cancellation refuse in full.

## Ordered children, never netted amounts

An ordinary group's children retain stable unique simulation entry IDs, explicit original/proposed line order and actual per-position ordinals. Validate child counts and exact increasing contiguous ordinals against each declared range. The future snapshot binds stored child order to CostEntry.Id insertion order and retains each real entry link; proposed child order must be stable server-validated document line order. Never use UUID order or price to infer child order.

After scheduling groups, flatten each ordinary group's children in its recorded order into CalculateFlow, without summing quantities, receipt amounts or issue costs. A transfer group has one paired flow event whose source and destination ordinals match its two declared ranges. Ordinary children must have the same receipt/issue kind as their parent; reversal/negative/conversion arithmetic remains unsupported until its separate contracts are implemented. No cross-method/epoch behavior is invented here.

The block adapter validates ALL group/child shapes before invoking arithmetic. Group IDs, child IDs and parent/position linkage remain distinct evidence; the existing flow primitive reserves every child identity and preserves every receipt layer and per-line issue rounding. Synthetic IDs are never persisted-layer IDs or approval tokens. Only a complete immutable result escapes; no writes, public caller, authorization shortcut or execution.

Retain the existing500-line costing-operation limit per block. The original ungrouped CalculateFlow remains capped at100,000 events. Only the block entry point may flatten up to200,000 child events after proving the parent-group and individual-entry bounds; both paths use the same private arithmetic and400,000-component budget. This is not a configurable limit bypass for ungrouped callers.

## Mandatory numerical and structural evidence

- FIFO one receipt operation has lines1/value1 and2/value100 on A. Issue2 costs51, not67.33 from a falsely merged3/value101 layer. Preserve both receipt layer identities and ordered consumption components.
- Either method: A holds6/value0.02, then one issue operation has two1-unit lines. Each line costs0.00; remaining4/value0.02. A merged2-unit issue would cost0.01. Assert both original child costs and the final balance.
- Ranges spanning several positions and proposed positions remain indivisible, follow every same-date tie and propagate into later transfers. Returning transfers remain valid; crossed per-position group dependencies fail.
- Refuse missing/extra/reordered children, mismatched kinds/parents, duplicate IDs/positions, gaps/overlaps/interleaved ranges, zero/negative/overflowing counts, transfer ranges with more than one entry and reversal range-count mismatch. Test the individual-entry resource bound, precision/shortage/cancellation and deterministic repeated output.

This replaces the future adapter's temporary multiline refusal ONLY after the new model/tests and real-ledger mapping are independently reviewed and integrated. Existing ordinary snapshot behavior stays unchanged. Authorized closure, all persisted-layer/consumption proofs, full reversal/negative/conversion matrix, guarded persistence/execution and shared UI remain required. Original posted journals/history and Final Closed/approval/audit/legal safeguards remain unchanged; PR48 stays Draft until the full phase and every exact-head gate pass.
