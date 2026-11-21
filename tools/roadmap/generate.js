const fs = require("fs");
const path = require("path");
const OUT = path.join(__dirname, "..", "..", "docs") + "/";
const CHECK = process.argv.includes("--check");
let drift = 0;
function emit(name, text) {
  const file = OUT + name; const want = text.replace(/\r\n/g, "\n");
  if (CHECK) { const have = fs.existsSync(file) ? fs.readFileSync(file, "utf8").replace(/\r\n/g, "\n") : ""; if (have !== want) { drift++; console.error("DRIFT: docs/" + name + " differs from the generator output (run: node tools/roadmap/generate.js)"); } }
  else fs.writeFileSync(file, want);
}
const reqs = require("./requirements.js");
const phases = require("./phases.js");
const decisions = require("./decisions.js");
const SRS = fs.readFileSync(OUT + "SRS.md", "utf8").split(/\r?\n/);

// ---------- derive SRS requirement ids from the approved SRS itself ----------
const srsIds = []; let sec = "", n = 0;
for (const l of SRS) { const h = l.match(/^## (\d+)\./); if (h) { sec = h[1]; n = 0; continue; }
  if (sec && +sec >= 2 && +sec <= 21 && +sec !== 20 && +sec !== 22 && /^(- |\d+\. )/.test(l)) { n++; srsIds.push(`${sec}.${n}`); } }
const expectedIds = [...srsIds, ...Array.from({ length: 12 }, (_, i) => `RR-${i + 1}`)];
const DELIVERED = new Set(["2.x", "3.1A", "3.2", "3.2A", "3.2B", "3.2C", "3.1D", "3.3", "3.4", "3.6", "3.1B", "3.1C"]);
const phaseIds = new Set(phases.map(p => p.id));
const errors = [];
// every requirement present exactly once, no extras
const seen = new Map(); reqs.forEach(r => seen.set(r[0], (seen.get(r[0]) || 0) + 1));
expectedIds.forEach(id => { if (seen.get(id) !== 1) errors.push("requirement missing or duplicated: " + id); });
reqs.forEach(r => { if (!expectedIds.includes(r[0])) errors.push("unknown requirement id " + r[0]); });
// every phase reference exists
reqs.forEach(r => r[2].forEach(p => { if (!phaseIds.has(p) && !DELIVERED.has(p)) errors.push(`${r[0]} references unknown phase ${p}`); }));
// every non-merged, non-extension requirement maps to at least one remaining phase
reqs.forEach(r => { if (["N", "P", "R", "C"].includes(r[3]) && !r[2].some(p => phaseIds.has(p))) errors.push(`${r[0]} is open but maps to no remaining phase`); });
// prerequisites exist and graph is acyclic
phases.forEach(p => p.pre.forEach(q => { if (!phaseIds.has(q) && !DELIVERED.has(q)) errors.push(`${p.id} has unknown prerequisite ${q}`); }));
const state = {}; const order = [];
const visit = (id, path) => { if (state[id] === 2) return; if (state[id] === 1) { errors.push("cycle: " + [...path, id].join(" -> ")); return; }
  state[id] = 1; const p = phases.find(x => x.id === id); if (p) p.pre.forEach(q => phaseIds.has(q) && visit(q, [...path, id])); state[id] = 2; order.push(id); };
phases.forEach(p => visit(p.id, []));
// every phase is used by some requirement or is a delivery/verification phase
const used = new Set(); reqs.forEach(r => r[2].forEach(p => used.add(p)));
phases.forEach(p => { if (!used.has(p.id) && !["3.7B", "3.7D", "4.0"].includes(p.id) && !/3\.7|4\.0/.test(p.id)) errors.push("phase carries no requirement: " + p.id); });
if (errors.length) { console.error(errors.join("\n")); process.exit(1); }

const ST = { M: "MERGED", P: "PARTIAL", N: "NOT STARTED", R: "IN REVIEW", X: "NOT MANDATORY (R1)", C: "CONSTRAINT" };
const cnt = {}; reqs.forEach(r => cnt[r[3]] = (cnt[r[3]] || 0) + 1);
const total = reqs.length, srsTotal = srsIds.length;
const mandatoryOpen = reqs.filter(r => ["N", "P", "C"].includes(r[3])).length;
const esc = s => String(s).replace(/\|/g, "/");
const RISK = { C: "Critical", H: "High", M: "Medium", L: "Low" };

// ---------- RELEASE1-TRACEABILITY.md ----------
let t = `# HASM — Release 1 requirements traceability matrix

Status: planning record produced by the Release 1 roadmap audit (documentation only; no application code was changed). Authoritative sources: \`docs/SRS.md\` (approved baseline v1.0), \`docs/DECISIONS.md\`, \`docs/ARCHITECTURE.md\`, \`docs/REPORTING-REQUIREMENTS.md\`. Companion documents: \`docs/ROADMAP.md\` (phases), \`docs/RELEASE1-DEPENDENCIES.md\`, \`docs/RELEASE1-ACCEPTANCE.md\`.

## How requirement identifiers are derived

The SRS has no numbered requirement identifiers. An identifier here is **section.n**, where *section* is the SRS section number and *n* is the 1-based position of the bullet (or numbered item) inside that section, counted in document order. Sections 2 to 19 contain the functional and non-functional requirements; section 21 contains ten acceptance scenarios (21.1 to 21.10); section 1 (scope), section 20 (configurable details) and section 22 (approval) are not requirements and are referenced where relevant. Reporting requirements are \`RR-1\` to \`RR-12\`, numbered as the owner numbered them in \`docs/REPORTING-REQUIREMENTS.md\` (RR-n is item n there). Counts below are computed from those documents by the generator that produced this file, not estimated.

## Status vocabulary (implemented ≠ tested ≠ merged ≠ verified)

* **MERGED** — the capability is on \`main\`, its automated tests passed in CI on the exact head of the merged pull request, and the roadmap claim was checked against the merge history (see ROADMAP "Delivered").
* **PARTIAL** — part is merged; the remaining work is named in the last column.
* **IN REVIEW** — built on a branch, not merged.
* **NOT STARTED** — nothing in merged code or a branch.
* **CONSTRAINT** — a rule or negative requirement that is enforced by tests of the phases listed (not a feature).
* **NOT MANDATORY (R1)** — the SRS states it is optional, future, or a configurable value; kept visible and never silently dropped.
* **No requirement is "fully verified" yet.** Full verification (end-to-end, installation, restore, performance, manual visual review) is the final milestone 5.3 (see \`docs/RELEASE1-ACCEPTANCE.md\`). MERGED means merged and CI-tested only.

## Totals (derived)

* Items traced: **${total}** = ${srsTotal} SRS items (sections 2 to 19: ${srsIds.filter(i => +i.split(".")[0] <= 19).length} requirements; section 21: ${srsIds.filter(i => i.startsWith("21.")).length} acceptance scenarios) + 12 reporting requirements.
* MERGED: **${cnt.M || 0}** · PARTIAL: **${cnt.P || 0}** · NOT STARTED: **${cnt.N || 0}** · IN REVIEW: **${cnt.R || 0}** · CONSTRAINT: **${cnt.C || 0}** · NOT MANDATORY (R1): **${cnt.X || 0}**.
* Still incomplete (PARTIAL + NOT STARTED + CONSTRAINT awaiting its phase): **${mandatoryOpen}**.
* Phase evidence is reconciled through merged PR #46. Supplier-invoice GRNI remains PARTIAL, and the customer/supplier balance-query acceptance scenario remains PARTIAL until the corresponding subledger exists.

## Matrix

| ID | Capability | Phase(s) | Status | Evidence (code / documentation) | Remaining work |
|---|---|---|---|---|---|
`;
for (const r of reqs) t += `| ${r[0]} | ${esc(r[1])} | ${r[2].join(", ")} | ${ST[r[3]]} | ${esc(r[4])} | ${esc(r[5])} |\n`;
t += `
## Acceptance criteria and test coverage

* Acceptance criteria per requirement are the SRS text itself plus, for RR-1 to RR-12, the acceptance criteria of \`docs/REPORTING-REQUIREMENTS.md\` section 3; per phase they are stated in \`docs/ROADMAP.md\` (each phase lists acceptance criteria and mandatory tests). Scenario items 21.1 to 21.10 are acceptance scenarios and each must be present as an automated test (or a recorded manual drill where automation is not possible) before 5.3.
* Current automated coverage on \`main\` (at \`80eea1f\`): unit and PostgreSQL integration tests (861 PostgreSQL tests at PR #27 head \`e212a49\`), frontend component/interaction tests, TypeScript typecheck and production build, Tauri debug build. Per-requirement test names are in the phase documents (\`docs/PHASE-*.md\`) and are re-verified when each phase starts.

## Findings of the audit

* **Missing requirements:** none found in the SRS that lacked a phase; items that previously had no numbered phase (Party registry, tax, pricing, attachments, sales and purchasing, receivables/payables, cash and banking, cheques, bank reconciliation, recurring documents, designers, licensing, backup, updates, installer, diagnostics, offline drafts, performance) now have one.
* **Deferred or optional by the SRS:** 6.4, 11.5, 15.7, 16.3 (kept as ${ST.X}).
* **Duplicate implementations:** none found; the reporting layer, export engine and import framework are single shared components by design (REPORTING-REQUIREMENTS section 4).
* **Conflicting documentation:** none between SRS, DECISIONS and ARCHITECTURE. Stale statuses found in \`docs/ROADMAP.md\` (rows for 3.3, 3.4, 3.5, 3.6 still said "pending owner merge" although merged) were corrected. See \`docs/ROADMAP.md\` "Roadmap ambiguities: resolutions and open decisions" (OD-1 to OD-14, T-1 to T-4).
`;
emit("RELEASE1-TRACEABILITY.md", t);

// ---------- ROADMAP.md ----------
const appendix = fs.readFileSync(path.join(__dirname, "appendix-a.md"), "utf8").replace(/\r\n/g, "\n").trimEnd();
const prc = phases.map(p => `| ${p.id} | ${esc(p.title)}${p.state ? " — **" + p.state + "**" : ""} | ${RISK[p.risk]} | ${p.pre.join(", ")} | ${esc(p.mig)} |`).join("\n");
const exec = order.filter(id => phaseIds.has(id));
const decisionTable = ["| ID | Status | Topic | Question | Basis in approved documents | Resolution / proposal and consequence | Blocks |", "|---|---|---|---|---|---|---|", ...decisions.map(x => `| ${x.id} | ${x.status} | ${esc(x.topic)} | ${esc(x.question)} | ${esc(x.basis)} | ${esc(x.resolution)} | ${esc(x.blocks)} |`)].join("\n");
let r = `# HASM — Release 1 implementation roadmap (authoritative phase sequence)

Status of this file: authoritative, dependency-ordered plan for all remaining approved Release 1 work, rebuilt by the roadmap audit (see \`docs/RELEASE1-TRACEABILITY.md\`, \`docs/RELEASE1-DEPENDENCIES.md\`, \`docs/RELEASE1-ACCEPTANCE.md\`). It adds phases and statuses; it does not change the approved SRS, ARCHITECTURE or DECISIONS, and it introduces no requirement outside them. Phases not listed under "Delivered" are **not implemented**.

Status words: *merged* = on \`main\` with CI green on the pull request head (checked against GitHub); *built* = on a branch only; *planned* = nothing exists. "Verified" is reserved for Phase 5.3.

## Delivered (merged to main; reconciled against GitHub on 2026-11-17)
| Phase | Content | PR / merge commit |
|---|---|---|
| 2.1-2.3C | System database, provisioning, identity, RBAC, security hardening, cryptographic audit | #4-#9 |
| 3.1A-1, 3.1A-2 | Company runtime foundation; company profile, settings, currencies; first frontend | #10 (runtime), #11 (\`31a2b9c\`) |
| UI-1 | Visual design and UX foundation | #12 (\`8fbd161\`) |
| 3.1B | Branch management | #13 (\`20e3e30\`) |
| 3.1C | Warehouse management | #14 (\`9f3cf69\`) |
| 3.1D | Fiscal years and periods | #15 (\`83e8113\`) |
| 3.2A | Chart of accounts foundation | #16 (\`f145f00\`) |
| 3.2A-2 | Business-type chart templates (Service 54, Retail 66, Wholesale 64 accounts) | #20 (\`2d50ada\`) |
| 3.2B | General ledger and journal posting | #17 (\`d2d2a11\`) |
| 3.2C | General ledger completion (cost-center policy, approval policies, year-end close) | #18 (\`b23013d\`) |
| 3.2D / 3.2A-3 | Import framework and chart Excel import | #19 (\`c34f99c\`) |
| 3.3 | Excel/PDF export, financial statements, ledger and trial balance completion | #21 (\`85035d1\`) |
| 3.4 / 3.4B | Inventory master data and tabs standard; inventory master-data import | #24 (\`d4038ed\`), #25 (\`5f194d6\`) |
| 3.5 | Stock movement documents up to approval (non-posting) | #26 (\`4ffc4ec\`) |
| 3.6 | Inventory costing engine foundation (wired to documents by Phase 3.7B) | #27 (\`2d2f187\`, head \`e212a49\`, 861 PostgreSQL tests) |
| 3.7A-3.7D | Inventory accounting configuration, atomic posting, reversal/close protection, shared posting UI | #31, #33, #35, #37 |
| 3.8A | Stock balances and item ledger | #40 (9c43fc9) |
| 3.8B | Stocktakes, adjustments, negative-stock exception and settlement | #42 (84dbb4e), #43 (981e991), #45 (4b79030) |
| 3.9A | Shared party identity, local records, central approval, outbox and reconciliation | #46 (bd56a25; reviewed head 6f57651) |
| 4.0 | Verification infrastructure; full-stack E2E and desktop smoke coverage remain open | #32 (7e05052) |
| policy docs | GitHub persistence/PR/CI-repair policies; autonomous tracker | #22, #23, #28 |

All phases through 3.8B and Phase 3.9A listed above are merged. PR #46 passed all ten manifest-required checks on exact head 6f57651 and was independently reviewed before merge. Phase 3.8C remains blocked by OPEN decision OD-11; Phase 3.8D is the next dependency-ready inventory phase. The four Medium party-registry follow-ups are scheduled before 3.9B. No Release 1 completion or production-readiness claim is made.

## Remaining phases (summary)
Execution order is in \`docs/RELEASE1-DEPENDENCIES.md\`. Risk is the accounting and security risk of the phase.

| Phase | Title | Risk | Prerequisites | Migration implications |
|---|---|---|---|---|
${prc}

## Definition of Done (applies to every phase)
1. Scope exactly as listed; nothing outside the approved SRS; OPEN decisions (OD-n) that block it decided or explicitly deferred before the slice that needs them.
2. One shared React frontend delivered to Web and Tauri (no platform gap); Arabic RTL and English LTR; tabs standard where applicable.
3. Server-side authorization, company/branch/warehouse scoping, audit, optimistic concurrency, validation; no frontend-only security.
4. Migrations: fresh creation and upgrade tested; opaque physical names; append-only/immutability where the data is historical; nothing destructive on non-test databases; destructive tests only on the disposable cluster.
5. Automated tests for the acceptance criteria (accounting and inventory invariants, concurrency, authorization, idempotency); CI expected test counts updated, never reduced.
6. Frontend typecheck, production build and Tauri build check pass; documentation (phase document, roadmap, traceability) updated.
7. Pull request with exact-head CI green, adversarial self-review and independent review recorded, merge only under the master directive gates; checkpoint recorded.

## Phase definitions
`;
for (const p of phases) {
  r += `
### Phase ${p.id} — ${p.title}
* **Exact approved scope:** ${p.srs}
* **Prerequisites:** ${p.pre.join(", ")}
* **Deliverables:** ${p.deliver}
* **Acceptance criteria:** ${p.accept}
* **Mandatory tests:** ${p.tests}
* **Database migration implications:** ${p.mig}
* **Security and accounting risk:** ${RISK[p.risk]}
* **Definition of Done:** the common Definition of Done above, plus the acceptance criteria and tests of this phase${p.notes ? "\n* **Notes:** " + p.notes : ""}
`;
}
r += `
## Roadmap ambiguities: resolutions and open decisions
Every ambiguity found by the audit was checked against the approved SRS, DECISIONS, ARCHITECTURE, REPORTING-REQUIREMENTS and the merged phase documents. **APPROVED** = an approved document already answers it (nothing is reopened). **DEFAULT** = minor implementation ambiguity, documented default adopted. **OPEN** = material accounting, inventory, security or licensing question: a proposal and its consequences are recorded, the owner decides, and the blocked phases are named. No requirement is changed by this section.

${decisionTable}

Until an OPEN item is decided, only the blocked slice waits; unaffected work continues and the blocker is reported with the work preserved. No two approved requirements were found that contradict each other.

## Autonomous-development readiness
See \`docs/RELEASE1-DEPENDENCIES.md\` ("Autonomous workflow readiness") for the gaps found, the added verification milestones (VM-1 to VM-8) and Phase 4.0.

---

# Appendix A — carried-over notes recorded before this audit
The text below is preserved from the previous roadmap so that no earlier decision is lost. Its status columns are superseded by "Delivered" above and by \`docs/RELEASE1-TRACEABILITY.md\`; the notes themselves still apply.

${appendix}
`;
emit("ROADMAP.md", r);

// ---------- RELEASE1-DEPENDENCIES.md ----------
let d = `# HASM — Release 1 phase dependencies and execution order

Generated from the phase definitions in \`docs/ROADMAP.md\`. The generator checked that every prerequisite exists, that the graph has **no cycle**, that every open requirement of \`docs/RELEASE1-TRACEABILITY.md\` maps to a phase listed here, and that every requirement identifier of the SRS appears in the matrix. A prerequisite is never bypassed: a phase may start only when all its prerequisites are merged and verified.

## Execution order (topological, preferred)
`;
const pref = ["3.7A","4.0","3.7B","3.7C","3.7D","3.8A","3.8B","3.8C","3.8D","3.8E","3.9A","3.9B","3.9C","3.10","3.11","3.12","3.13","3.14","3.15","3.16","3.17","3.18","3.19","3.20","3.21","3.22","3.27","3.23","3.24","3.25","3.26","4.1","4.2","4.3","4.4","4.5","4.6","4.7","4.8","5.1","5.2","5.3"];
const ok = new Set(); const chk = [];
pref.forEach(id => { const p = phases.find(x => x.id === id); if (!p) { chk.push("missing " + id); return; } p.pre.forEach(q => { if (phaseIds.has(q) && !ok.has(q)) chk.push(`${id} before prerequisite ${q}`); }); ok.add(id); });
phases.forEach(p => { if (!pref.includes(p.id)) chk.push("not ordered " + p.id); });
if (chk.length) { console.error(chk.join("\n")); process.exit(1); }
pref.forEach((id, i) => { const p = phases.find(x => x.id === id); d += `${i + 1}. **${id}** — ${p.title} (needs: ${p.pre.filter(q => phaseIds.has(q)).join(", ") || "merged phases only"})\n`; });
d += `
Notes on ordering choices: 4.0 (verification infrastructure) is placed right after 3.7A so that the large posting and commercial phases run with end-to-end and security scanning in place; cash and bank accounts (3.14) precede purchasing and sales because invoices may be cash or mixed (SRS 7.1); credit-limit **enforcement** is delivered in 3.17 (receivable balances exist there) while 3.16 only defines the hook, which removes a sales/receivables circularity; 3.27 (opening balances and legacy migration) follows receivables because opening receivables need their ledger; 4.2 (licensing) follows the business modules so that "posted business transaction" counting has real transactions to count, but its entitlement states (read/export-only, drafts kept) are designed against every module from 3.13 onward.

## Dependency table
| Phase | Prerequisites | Unlocks |
|---|---|---|
`;
for (const p of phases) { const un = phases.filter(q => q.pre.includes(p.id)).map(q => q.id); d += `| ${p.id} | ${p.pre.join(", ")} | ${un.join(", ") || "-"} |\n`; }
d += `
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
| G-8 | Manual visual review and Windows installer/clean-machine tests cannot run in Actions. | Some acceptance items are manual. | Listed in \`docs/RELEASE1-ACCEPTANCE.md\` as manual drills with a recorded owner sign-off, deferred to 5.3 only for visual review. |
| G-9 | CI expected-test-count registry is edited by hand per phase. | Silent test loss or drift. | Count registry kept in the backend workflow and updated in the same PR; never reduced. |
| G-10 | Cross-database operations (Party, licensing sync) have no failure-injection harness. | Outbox and reconciliation could be untested. | Built with 3.9A. |
| G-11 | OPEN decisions (OD-1, OD-2, OD-11, OD-12, T-1, T-2) can stop a slice. | Autonomous work can stall. | Slices ordered so unaffected work continues; blockers reported with preserved work. |

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
OPEN decisions (OD-1, OD-2, OD-11, OD-12, T-1, T-2); gate G-5; the lack of a reference performance environment (G-7); Windows-only checks (installer, Tauri signing) that Actions cannot fully cover; credentials or signing keys that must never be committed (licence signing keys and code-signing certificates need an owner-provided secure store before 4.2 and 4.5); GitHub plan limits (G-4).
`;
emit("RELEASE1-DEPENDENCIES.md", d);

// ---------- RELEASE1-ACCEPTANCE.md ----------
const ac = `# HASM — Release 1 final acceptance criteria

Release 1 may be declared complete only when **every** criterion below is met or explicitly recorded as NOT VERIFIED with the missing means named (never estimated). Derived from SRS sections 15, 18, 19 and 21, \`docs/DECISIONS.md\` (D-038, D-039), \`docs/ARCHITECTURE.md\` and \`docs/REPORTING-REQUIREMENTS.md\`. Nothing here adds scope.

## A. Traceability closure
1. Every row of \`docs/RELEASE1-TRACEABILITY.md\` is MERGED, CONSTRAINT-verified, or NOT MANDATORY (R1); none NOT STARTED, PARTIAL or IN REVIEW.
2. Every phase of \`docs/ROADMAP.md\` is merged; no open pull request carries Release 1 scope.
3. No SaaS multi-tenant, payroll/HR, manufacturing, mandatory direct bank integration, country-specific e-invoicing or mandatory PITR feature was added (SRS section 1).

## B. Mandatory scenarios (SRS section 21), each with an automated test or a recorded drill
${Array.from({ length: 10 }, (_, i) => `${i + 1}. Scenario 21.${i + 1} — ${reqs.find(x => x[0] === "21." + (i + 1))[1].replace(/^Scenario: /, "")}`).join("\n")}

## C. Invariants (SRS section 19) proven by automated tests, including under concurrency
Company database is the source of truth; at most one financial/stock effect per business event; posted documents never changed or deleted; no cross-company visibility; no posting into Final Closed periods and no approval bypass by automatic tasks; no loss of drafts on licence expiry or disconnection; state tracks separated for sales/purchasing documents; full traceability of price, tax, exchange rate, cost and approval source.

## D. Test categories (SRS 18.4) all present and green on the release commit
Unit; integration (PostgreSQL, exact expected counts); end-to-end (Web and Desktop); general ledger; costing; recalculation; import; security (authorization, isolation, injection, uploads, designers); concurrency; backup and restore; installation; upgrade; load.

## E. Performance (SRS 18.1-18.3)
On the reference environment (8 vCPU, 32 GB RAM, 500 GB SSD; Backend and PostgreSQL together; 5 companies; 50 concurrent active sessions) with reference data (20,000 items, 10,000 customers/suppliers, 100,000 invoices, 500,000 entries and stock movements) and a sustained 60-minute load: P95 item/customer search < 1 s; ordinary screen < 2 s; save/post of an ordinary invoice < 3 s; ordinary report < 5 s. If the environment is unavailable the result is **NOT VERIFIED**; the figures are never estimated and the reference hardware is not published as a minimum production requirement.

## F. Licensing and Trial (SRS 15)
Named and Concurrent seats (each active session counts); annual and perpetual behaviour; read/export-only after expiry with drafts kept; Trial limits of one company, two users, one branch, one warehouse and 100 posted business transactions enforced server-side from both clients; offline activation; support policy of 36 months recorded.

## G. Backup, restore, update, installation (SRS 2, 16)
Scheduled and manual backups with retention and encryption; full and single-company restore with attachments without touching other companies or the system database; measured RPO/RTO recorded; signed manual updates with rollback; clean install and upgrade on the three deployment models; TLS to API and database.

Recorded limitations against G (dated; each must be closed or explicitly accepted before release):
* 2026-11-21 (D-118, Phase 4.3 slice 5): trial restore and the backup recovery test run without independent approval until the Phase 4.1 approval framework exists. They run only through the administrative tool under operating-system access, and each writes a system-stream audit event.
* 2026-11-21 (D-099, D-118, Phase 4.3 slice 5): backup-recovery-keygen, backup-signing-keygen and backup-keys run in an interim mode under operating-system access, without a named administrator with MFA, until the Phase 4.1 administrative-tool login (D-099) exists; backup-signing-keygen and backup-keys write system-stream audit events.

## H. Security (SRS 17, 4)
No open Critical or High finding; audit chain verifies after export; secrets never in repository, logs or support bundles; opaque physical names preserved by every migration; MFA and emergency account as specified.

## I. Quality gates
All mandatory tests pass on the final committed tree; all required GitHub Actions checks green on the exact release head; no weakened or disabled test, no reduced expected count; frontend typecheck, production build and Tauri build pass; manual visual review of Arabic RTL and English LTR on Web and Desktop recorded with owner sign-off.

## J. Documentation
SRS-aligned user and administrator documentation for installation, backup/restore, licensing, updates; this acceptance record completed with evidence (commit, run identifiers, drill logs).
`;
emit("RELEASE1-ACCEPTANCE.md", ac);

if (CHECK && drift) process.exit(1);
console.log(CHECK ? "CHECK OK" : "OK", JSON.stringify({ total, srsTotal, cnt, mandatoryOpen, phases: phases.length, ids: srsIds.length }));
