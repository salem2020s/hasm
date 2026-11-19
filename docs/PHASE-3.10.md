# Phase 3.10 - Tax engine (design proposal and slice plan)

Scope: SRS 6.1-6.4 and 19.8 only (flexible extensible engine; dated rates; classifications of operations, items and parties; several taxes; exemption, exception and zero rate; inclusive/exclusive; calculation base, rounding and priority rules per document type and operation; an immutable snapshot of rule, rates and results on the final document; an extension point for future national localization). **No country-specific compliance and no e-invoicing** (SRS 6.4). Acceptance (ROADMAP 3.10): deterministic exact decimal results; a snapshot never changes when rules change. No open owner decision (OD-n) applies.

Status: **slice 1 (pure arithmetic engine and snapshot) is implemented in this branch; slices 2-4 are not.** Document/framework integration belongs to 3.13; nothing here posts, stores or edits a financial document.

## Slice plan

1. **Pure engine + snapshot (this branch).** `Taxes/TaxModel.cs`, `TaxEngine.cs`, `TaxSnapshot.cs`: no database, clock, culture or floating point; arithmetic on exact rationals (`BigInteger`), ONE rounding step per amount.
2. **Persistence and rule resolution (company migration + system permissions).** Tax definitions, dated rates (no overlap per tax, never edited: a change is a new dated version), classifications (item, party, operation), rules per document type/operation (which taxes, priority, base flags, rounding policy), all immutable-versioned with audit; resolver that turns (document type, operation, item class, party class, date) into the ordered components the engine takes. Migration number: coordinate with the open 3.8D/3.9A work (0022 is contended).
3. **Management API and shared UI (Web and Tauri, Tabs standard).**
4. **Localization extension point:** a registered, versioned `ITaxLocalization` that may only ADD classifications/rules and validate inputs, never change engine arithmetic (documented and tested as an interface; no country module).

## Engine contract (slice 1)

* Inputs per line: quantity (<= 10 decimals), unit price (<= 6), discount (<= money decimals), price-includes-tax flag, ordered tax components. Non-negative only: credit notes are modelled by the document framework (reversal semantics), not by negative arithmetic.
* Component: tax id and code, **treatment** (Standard, ZeroRated, Exempt, NotApplicable), rate in percent (<= 6 decimals; zero for the three non-standard treatments), unique strictly increasing priority, and the set of EARLIER taxes whose amount is added to this tax's base (compound taxes).
* Treatments: Zero-rated, exempt and not-applicable lines contribute 0 tax but their turnover is reported separately. `TotalsByTax` is keyed by (tax, treatment, rate), so a Standard and an Exempt line of the same tax, or two rates of one tax, are never merged; a tax id keeps one code across all lines (`inconsistent_tax`). A Standard tax with a 0 % rate is non-taxable but keeps the Standard label.
* Rounding policy: money decimals (0-6); mode half-away-from-zero or half-even; level **per line** (every amount rounded once; later compound bases use the ROUNDED earlier amounts, as printed) or **per document** (exact amounts summed per tax, rounded once, then allocated to the lines by largest remainder, ties to the lower line number, so lines add up exactly; line bases are informational). Inclusive prices support per-line rounding only (`inclusive_requires_line_rounding`).
* Exclusive: net = round(quantity x price - discount); tax_i = base_i x rate_i / 100.
* Inclusive: gross G = round(quantity x price - discount) is AUTHORITATIVE. With multipliers m_i = 1 + sum(r_j m_j for the earlier taxes included in tax i's base), net0 = G / (1 + sum r_i m_i); net = round(net0); every taxable tax except the LAST taxable one is rounded; the last taxable tax is G - net - the others, so net + taxes = G exactly (no cent is invented or lost); a NEGATIVE residual is taken from the last taxable tax first, then from the earlier taxable taxes (last to first), then from the net, so no amount is ever negative. No taxable tax: net = G.
* Refusals with stable codes (no partial result): invalid precision, negative values, discount above gross, inputs or results beyond the supported range (`amount_too_large`, never an overflow), rate out of range, treatment/rate mismatch, duplicate tax or priority, a base reference that is duplicated, self-referencing, later or unknown, more than 8 taxes per line or 5000 lines (all fractions are reduced after every operation: 5000 lines x 8 compound taxes per document run in well under a second).
* **Snapshot:** `tax-snapshot-v1` stores VALUES, not references: the policy, the resolved components (with opaque rule/rate version references as strings supplied by the resolver), the inputs and the results, plus a SHA-256 over the canonical, scale-normalized JSON. `Verify` recomputes the engine from the snapshot's own inputs and compares results and hash, so a later rule/rate change cannot alter or invalidate an existing snapshot, and tampering is detected. The hash is tamper-evidence, NOT authentication: authenticity belongs to the document store and its audit chain. `Create` keeps defensive read-only copies and normalizes a missing base list to empty.

## Held for slice 2 (found by the independent review; not representable in the arithmetic slice)

* SRS 6.1 names "exceptions" (استثناء) next to exemption and zero rate. `TaxTreatment` has Standard, ZeroRated, Exempt and NotApplicable and is not persisted yet. Proposal: an "exception" is a RULE-level override (a party or item classification that resolves to Exempt/NotApplicable or to another rate), not a fifth treatment; confirm this mapping before the enum is persisted.
* The calculation base is always the net after discount. A per-document-type choice of base (before discount, plus other charges) is a rule attribute of slice 2 (`TaxBaseRule`), not an engine argument yet.
* Dated rates, classifications, per-document-type rules and priority resolution, and the localization extension point are slices 2 and 4.

## Mandatory tests (this slice)

Arithmetic matrix (single/multiple/compound taxes, zero/exempt reporting, discount, fractional quantity, both rounding modes, per-line and per-document allocation with exact sums, inclusive pricing with the residual rule, every refusal), property-style invariants (sum of lines = totals; inclusive net + taxes = gross; deterministic across repeated calls; scale-independent hash), snapshot round trip and tamper detection. PostgreSQL tests arrive with slice 2.
