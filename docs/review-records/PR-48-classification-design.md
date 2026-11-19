# Review record - PR #48 original-classification evidence contract (design only)

Reviewer: separate general-purpose subagent (Claude Sonnet 5.5 family), read-only; prompt: review `PHASE-3.8D-CLASSIFICATION-CONTRACT.md` against the accounting proposal, fiscal contract, posting/reversal/negative-stock code and schema for field existence, accounting-integrity gaps, authorization/bounds gaps and silently invented rules. Author context was not shared.

Findings on revision 1: High H1 allocations live in `JournalAllocation`, stock journals carry none; H2 group not stored, cannot derive account from version row; H3 obligation/settlement/reversal tables and netting missing; H4 journal-status vs stock-effect mapping. Medium M1 transfers, M2 conditional version, M3 ledger authorization, M4 closure bounds, M5 current-validity evidence, M6 fingerprint scope. Low L1-L3 rounding/currency/pending definitions. Two invented rules flagged as open decisions.

Resolution: all addressed in revision 2 of the contract; the cancelled-journal, stock-reversed-draft-cancelled and `RemapRequired` policies are recorded as OPEN owner decisions (plan refuses until decided). Revision 2 has NOT been re-reviewed; implementation must not start the affected paths until it is.
