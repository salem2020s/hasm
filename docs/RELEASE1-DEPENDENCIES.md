# HASM — Release 1 phase dependencies and execution order

Generated from the phase definitions in `docs/ROADMAP.md`. The generator checked that every prerequisite exists, that the graph has **no cycle**, that every open requirement of `docs/RELEASE1-TRACEABILITY.md` maps to a phase listed here, and that every requirement identifier of the SRS appears in the matrix. A prerequisite is never bypassed: a phase may start only when all its prerequisites are merged and verified.

## Execution order (topological, preferred)
1. **3.7A** — Inventory accounting configuration (first slice of 3.7) (needs: merged phases only)
2. **4.0** — Verification infrastructure (needs: 3.7A)
3. **3.7B** — Atomic inventory posting (core) (needs: 3.7A)
4. **3.7C** — Reversal, cancellation and period-close protection (needs: 3.7B)
5. **3.7D** — Posting UI and capabilities (needs: 3.7C)
6. **3.8A** — Stock balances and item ledger (needs: 3.7D, 4.0)
7. **3.8B** — Stocktaking, adjustments and the negative-stock exception (needs: 3.8A)
8. **3.8C** — Lots, serial numbers, expiry and reservations (needs: 3.8A)
9. **3.8D** — Back-dated movements, impact preview, recalculation, method change and revaluation (needs: 3.7C, 3.8A)
10. **3.8E** — Inventory reports and cost-of-sales statement (needs: 3.8A, 3.8D)
11. **3.9A** — Party registry (shared identity and company-local record) (needs: 3.7D)
12. **3.9B** — Customers and suppliers (company-local accounts) (needs: 3.9A)
13. **3.9C** — Party import adapter and reusable mapping templates (needs: 3.9B)
14. **3.10** — Tax engine (needs: merged phases only)
15. **3.11** — Pricing and discount engine (needs: 3.9B)
16. **3.12** — Attachments and key management (needs: merged phases only)
17. **3.13** — Commercial document framework (needs: 3.7C, 3.9B, 3.10, 3.11)
18. **3.14** — Cash and bank accounts, vouchers and transfers (needs: 3.13)
19. **3.15** — Purchasing (needs: 3.13, 3.14)
20. **3.16** — Sales (needs: 3.13, 3.14, 3.11)
21. **3.17** — Receivables and payables (needs: 3.15, 3.16)
22. **3.18** — Foreign-exchange differences (needs: 3.17)
23. **3.19** — Cheques (needs: 3.14, 3.17)
24. **3.20** — Bank reconciliation (needs: 3.14, 3.17, 3.19, 3.9C)
25. **3.21** — Recurring documents and scheduler (needs: 3.15, 3.16, 3.17)
26. **3.22** — Inter-branch settlements (needs: 3.17)
27. **3.27** — Opening balances and legacy migration (needs: 3.17, 3.8B, 3.9C)
28. **3.23** — Operational reports (needs: 3.17, 3.14, 3.8E)
29. **3.24** — Multi-company aggregation reports (needs: 3.18, 3.23)
30. **3.25** — Visual report designer (needs: 3.23)
31. **3.26** — Document and print designer (needs: 3.15, 3.16, 3.12)
32. **4.1** — Identity completion (needs: 3.7D)
33. **4.2** — Licensing and Trial (needs: 3.17)
34. **4.3** — Backup and restore (needs: 3.12)
35. **4.4** — Signed manual updates (needs: 4.3)
36. **4.5** — Installer and deployment models (needs: 4.1, 4.4)
37. **4.6** — Diagnostics, operations and audit retention (needs: 4.5)
38. **4.7** — Desktop offline drafts (needs: 3.17)
39. **4.8** — Localization and UX completion (needs: 3.26)
40. **5.1** — Performance and load validation (needs: 4.8)
41. **5.2** — Security review and hardening (needs: 4.6)
42. **5.3** — Final validation and Release 1 acceptance (needs: 5.1, 5.2)

Notes on ordering choices: 4.0 (verification infrastructure) is placed right after 3.7A so that the large posting and commercial phases run with end-to-end and security scanning in place; cash and bank accounts (3.14) precede purchasing and sales because invoices may be cash or mixed (SRS 7.1); credit-limit **enforcement** is delivered in 3.17 (receivable balances exist there) while 3.16 only defines the hook, which removes a sales/receivables circularity; 3.27 (opening balances and legacy migration) follows receivables because opening receivables need their ledger; 4.2 (licensing) follows the business modules so that "posted business transaction" counting has real transactions to count, but its entitlement states (read/export-only, drafts kept) are designed against every module from 3.13 onward.

## Dependency table
| Phase | Prerequisites | Unlocks |
|---|---|---|
| 3.7A | 3.6 | 3.7B, 4.0 |
| 3.7B | 3.7A | 3.7C |
| 3.7C | 3.7B | 3.7D, 3.8D, 3.13 |
| 3.7D | 3.7C | 3.8A, 3.9A, 4.1 |
| 3.8A | 3.7D, 4.0 | 3.8B, 3.8C, 3.8D, 3.8E |
| 3.8B | 3.8A | 3.27 |
| 3.8C | 3.8A | - |
| 3.8D | 3.7C, 3.8A | 3.8E |
| 3.8E | 3.8A, 3.8D | 3.23 |
| 3.9A | 3.7D | 3.9B |
| 3.9B | 3.9A | 3.9C, 3.11, 3.13 |
| 3.9C | 3.9B | 3.20, 3.27 |
| 3.10 | 3.2B | 3.13 |
| 3.11 | 3.4, 3.9B | 3.13, 3.16 |
| 3.12 | 3.2A | 3.26, 4.3 |
| 3.13 | 3.7C, 3.9B, 3.10, 3.11 | 3.14, 3.15, 3.16 |
| 3.14 | 3.13 | 3.15, 3.16, 3.19, 3.20, 3.23 |
| 3.15 | 3.13, 3.14 | 3.17, 3.21, 3.26 |
| 3.16 | 3.13, 3.14, 3.11 | 3.17, 3.21, 3.26 |
| 3.17 | 3.15, 3.16 | 3.18, 3.19, 3.20, 3.21, 3.22, 3.23, 3.27, 4.2, 4.7 |
| 3.18 | 3.17 | 3.24 |
| 3.19 | 3.14, 3.17 | 3.20 |
| 3.20 | 3.14, 3.17, 3.19, 3.9C | - |
| 3.21 | 3.15, 3.16, 3.17 | - |
| 3.22 | 3.17 | - |
| 3.23 | 3.17, 3.14, 3.8E | 3.24, 3.25 |
| 3.24 | 3.18, 3.23 | - |
| 3.25 | 3.23 | - |
| 3.26 | 3.15, 3.16, 3.12 | 4.8 |
| 3.27 | 3.17, 3.8B, 3.9C | - |
| 4.0 | 3.7A | 3.8A |
| 4.1 | 3.7D | 4.5 |
| 4.2 | 3.17 | - |
| 4.3 | 3.12 | 4.4 |
| 4.4 | 4.3 | 4.5 |
| 4.5 | 4.1, 4.4 | 4.6 |
| 4.6 | 4.5 | 5.2 |
| 4.7 | 3.17 | - |
| 4.8 | 3.26 | 5.1 |
| 5.1 | 4.8 | 5.3 |
| 5.2 | 4.6 | 5.3 |
| 5.3 | 5.1, 5.2 | - |

## Slice rule
Large phases are already split into independently mergeable slices (3.7A-D, 3.8A-E, 3.9A-C). Any other phase whose implementation plan exceeds a reviewable size is split into slices before work starts, each slice with its own acceptance criteria; slices never reorder the prerequisites above.

## Autonomous workflow readiness
Workflow: Implement → Test → Review → Commit → Push → PR → CI → Fix → Verify → Merge → Checkpoint → Next phase.

### Gaps found
| # | Gap | Effect | Planned remedy |
|---|---|---|---|
| G-1 | Only three path-filtered workflows (backend, frontend, desktop). A documentation-only PR triggers no check. | A docs PR can merge with no automated gate. | 4.0: a documentation check (links, required files, generated-table consistency) that always runs. |
| G-2 | No end-to-end (browser) tests; Web/Desktop parity is shared-component tests only. | Integration defects between UI, API and database are found late. | 4.0: Web E2E in CI; Desktop smoke. |
| G-3 | No dependency vulnerability or secret scanning in CI. | Supply-chain and committed-secret risk. | 4.0: scanning added without weakening existing checks. |
| G-4 | Branch protection and required reviews cannot be configured or read (private repository on a plan without that API). | The only enforced gates are Actions checks; reviews are the agent's own. | The master directive's self-review plus independent review gates; never claim a review that did not happen; state the limitation in every merge record. |
| G-5 | Independent review needs a separate reviewing context; none is automatically available outside the agent sandbox. | A gate can be unsatisfiable. | If it cannot be done, the gate is unsatisfied and the merge is not made. |
| G-6 | No performance, install, upgrade, restore or load harness. | SRS 18 acceptance cannot be shown. | Harnesses built in 4.0 (skeleton), 4.3, 4.5, 5.1; results otherwise recorded as NOT VERIFIED. |
| G-7 | Reference performance environment (8 vCPU / 32 GB / 500 GB SSD, 5 companies, 50 sessions) is not available in this development environment. | 5.1 may not be runnable. | Recorded as NOT VERIFIED with the missing environment named; never estimated. |
| G-8 | Manual visual review and Windows installer/clean-machine tests cannot run in Actions. | Some acceptance items are manual. | Listed in `docs/RELEASE1-ACCEPTANCE.md` as manual drills with a recorded owner sign-off, deferred to 5.3 only for visual review. |
| G-9 | CI expected-test-count registry is edited by hand per phase. | Silent test loss or drift. | Count registry kept in the backend workflow and updated in the same PR; never reduced. |
| G-10 | Cross-database operations (Party, licensing sync) have no failure-injection harness. | Outbox and reconciliation could be untested. | Built with 3.9A. |
| G-11 | OPEN decisions (OD-1, OD-2, OD-12, T-1) can stop a slice. | Autonomous work can stall. | Slices ordered so unaffected work continues; blockers reported with preserved work. |

### Added verification milestones (must pass before the next phase starts)
| ID | After | Check |
|---|---|---|
| VM-1 | 3.7D | Inventory-to-ledger reconciliation suite (stock value = inventory account balance), duplicate-posting and rollback invariants, concurrency stress on one item x warehouse. |
| VM-2 | 3.8E | Full inventory regression with back-dating, lots/serials, stocktake; reports reconcile to ledger. |
| VM-3 | 3.9A | Cross-company isolation matrix including shared party identity; outbox failure injection. |
| VM-4 | 3.16 | Scenario 21.1 end-to-end for purchases and sales in both modes. |
| VM-5 | 3.17 | Subledger-to-control-account reconciliation; duplicate-receipt prevention under retries (scenario 21.6 part 1). |
| VM-6 | 3.20 | Scenario 21.6 complete; bank adjustment approval. |
| VM-7 | 4.3 | Scenario 21.8 restore drill and 21.10. |
| VM-8 | 4.5 | Clean install and upgrade on each deployment model. |

### Risks that could prevent fully autonomous development
OPEN decisions (OD-1, OD-2, OD-12, T-1); gate G-5; the lack of a reference performance environment (G-7); Windows-only checks (installer, Tauri signing) that Actions cannot fully cover; credentials or signing keys that must never be committed (licence signing keys and code-signing certificates need an owner-provided secure store before 4.2 and 4.5); GitHub plan limits (G-4).
