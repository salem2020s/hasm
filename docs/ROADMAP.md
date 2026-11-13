# HASM — Implementation Roadmap (company structure phases)

Status of this file: records the owner-confirmed phase sequence (confirmed 2026-11-13; decided in earlier requirements discussions and not previously transferred into the repository). It does not change the approved SRS, ARCHITECTURE or DECISIONS.

## Delivered
| Phase | Content | State |
|---|---|---|
| 2.1-2.3C | System database, provisioning, identity, RBAC, security hardening, cryptographic audit | merged |
| 3.1A-1 | Company runtime foundation (least-privilege company roles, schema gate, business audit) | merged |
| 3.1A-2 | Company profile, settings, currencies, history; first frontend | merged (PR #11) |
| UI-1 | Visual design and UX foundation | merged (PR #12) |

## Approved sequence (functional)
| Phase | Content | State |
|---|---|---|
| **3.1B** | **Branch Management** — see `docs/PHASE-3.1B.md` | in progress (Checkpoint A) |
| 3.1C | Warehouse Management | not started |
| 3.1D | Fiscal Years and Periods | not started |

Rules: one phase at a time; no Warehouse or Fiscal-Period work inside 3.1B; no inter-branch accounting entries or branch financial reports before their accounting modules exist (the branch model must leave room for them).
