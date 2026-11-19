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
* Treatments: Zero-rated, exempt and not-applicable lines contribute 0 tax but their turnover is reported separately per treatment (`TotalsByTax` carries base and amount).
* Rounding policy: money decimals (0-6); mode half-away-from-zero or half-even; level **per line** (every amount rounded once; later compound bases use the ROUNDED earlier amounts, as printed) or **per document** (exact amounts summed per tax, rounded once, then allocated to the lines by largest remainder, ties to the lower line number, so lines add up exactly; line bases are informational). Inclusive prices support per-line rounding only (`inclusive_requires_line_rounding`).
* Exclusive: net = round(quantity x price - discount); tax_i = base_i x rate_i / 100.
* Inclusive: gross G = round(quantity x price - discount) is AUTHORITATIVE. With multipliers m_i = 1 + sum(r_j m_j for the earlier taxes included in tax i's base), net0 = G / (1 + sum r_i m_i); net = round(net0); every taxable tax except the LAST taxable one is rounded; the last taxable tax is G - net - the others, so net + taxes = G exactly (no cent is invented or lost); if absorbing the residual would make that tax negative, the net absorbs it instead. No taxable tax: net = G.
* Refusals with stable codes (no partial result): invalid precision, negative values, discount above gross, rate out of range, treatment/rate mismatch, duplicate tax or priority, include referencing a later/unknown tax, more than 8 taxes per line or 5000 lines, mismatched snapshot.
* **Snapshot:** `tax-snapshot-v1` stores VALUES, not references: the policy, the resolved components (with opaque rule/rate version references as strings supplied by the resolver), the inputs and the results, plus a SHA-256 over the canonical, scale-normalized JSON. `Verify` recomputes the engine from the snapshot's own inputs and compares results and hash, so a later rule/rate change cannot alter or invalidate an existing snapshot, and tampering is detected.

## Mandatory tests (this slice)

Arithmetic matrix (single/multiple/compound taxes, zero/exempt reporting, discount, fractional quantity, both rounding modes, per-line and per-document allocation with exact sums, inclusive pricing with the residual rule, every refusal), property-style invariants (sum of lines = totals; inclusive net + taxes = gross; deterministic across repeated calls; scale-independent hash), snapshot round trip and tamper detection. PostgreSQL tests arrive with slice 2.
