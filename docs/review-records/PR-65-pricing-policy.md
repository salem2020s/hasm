# Independent review - PR #65, Phase 3.11 slice 2a (configurable pricing precedence)

* Reviewed: the full diff against `origin/main` at head `7ba9293`. Reviewer: a separate read-only subagent (general-purpose, Sonnet 5.5 family, fresh context; it ran the 21 pricing tests). **No Critical, no High; one Medium, four Low.**
* Verified: default ordering byte-identical to slice 1 (lists, discounts, unchanged tails); the ambiguity check stays sound in priority-first mode (it compares the full key tuple and equal tuples sort adjacent); both modes end in code then id (total order); v1/v2 verification cannot be fooled by version mislabelling, policy swap or precedence edit; unknown enum values yield invalid evidence, not a crash; the `policy` trace step appears only for non-default modes and affects no default flow.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | `Resolve`'s policy parameter is optional and `Create`/`Verify` did not tie the trace's policy steps to the snapshot's policy, so a snapshot could state a policy that was not applied (or carry a stray policy step). | Fixed: `Create` refuses (`policy_mismatch`) and `Verify` returns false unless the trace's `policy` steps equal those the snapshot's policy implies (none for defaults, so the v1 golden hash is untouched); test added. |
| L1 | Low | Redundant null check in `Verify`. | Fixed. |
| L2 | Low | Missing tests: out-of-range enum in `Verify`, policy mismatch, discount tie by specificity at equal priority. | Fixed (one new test covers all three). |
| L3/L4 | Low | Comment accuracy; docs state the slice-2b storage as a plan. | Accepted; docs label slice 2b as next. |
