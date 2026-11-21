"use strict";
// Results format and the verdict rule. A measurement is never acceptance evidence by itself: the verdict is NOT VERIFIED unless EVERY
// reference condition below is met by what the run declares and records. Numbers are only ever measured samples, never estimates.
const os = require("os");
const { summarize } = require("./stats");

// SRS 18 / docs/RELEASE1-ACCEPTANCE.md (D-038): the reference environment, reference data, sustained load and P95 targets.
const REFERENCE = {
  environment: { vcpu: 8, ramGb: 32, diskGb: 500, ssd: true, backendAndPostgresTogether: true, companies: 5, concurrentSessions: 50 },
  data: { items: 20000, parties: 10000 /* customers and suppliers */, invoices: 100000, entriesAndStockMovements: 500000 },
  loadSeconds: 3600,
  p95TargetMs: { search: 1000, screen: 2000, save_post: 3000, report: 5000 },
};

function evaluateReference({ declared, data, load }) {
  const reasons = [];
  if (!declared) reasons.push("no reference environment declared (--environment <file>)");
  else {
    const R = REFERENCE.environment;
    if (!(declared.vcpu >= R.vcpu)) reasons.push(`declared vCPU ${declared.vcpu} < ${R.vcpu}`);
    if (!(declared.ramGb >= R.ramGb)) reasons.push(`declared RAM ${declared.ramGb} GB < ${R.ramGb} GB`);
    if (!(declared.diskGb >= R.diskGb)) reasons.push(`declared disk ${declared.diskGb} GB < ${R.diskGb} GB`);
    if (declared.ssd !== true) reasons.push("declared disk is not an SSD");
    if (declared.backendAndPostgresTogether !== true) reasons.push("backend and PostgreSQL are not declared to share the one reference machine");
    if (!(declared.companies >= R.companies)) reasons.push(`declared companies ${declared.companies} < ${R.companies}`);
  }
  const D = REFERENCE.data;
  if (!(data.items >= D.items)) reasons.push(`items ${data.items} < ${D.items}`);
  if (!(data.parties >= D.parties)) reasons.push(`parties ${data.parties} < ${D.parties}`);
  if (!(data.invoices >= D.invoices)) reasons.push(`invoices ${data.invoices || 0} < ${D.invoices} (invoice seeding is not implemented in stage 1)`);
  if (!(data.entriesAndStockMovements >= D.entriesAndStockMovements)) reasons.push(`entries and stock movements ${data.entriesAndStockMovements} < ${D.entriesAndStockMovements}`);
  if (!(load.sessions >= REFERENCE.environment.concurrentSessions)) reasons.push(`concurrent sessions ${load.sessions} < ${REFERENCE.environment.concurrentSessions}`);
  if (!(load.durationSeconds >= REFERENCE.loadSeconds)) reasons.push(`load duration ${load.durationSeconds}s < ${REFERENCE.loadSeconds}s`);
  if (load.notImplemented && load.notImplemented.length) reasons.push(`scenarios not implemented: ${load.notImplemented.join(", ")}`);
  return reasons;
}

function buildResults({ tool, seed, data, load, samples, declaredEnvironment, startedAtUtc, finishedAtUtc, scenarios }) {
  const byScenario = scenarios.map((s) => {
    const ok = samples.filter((x) => x.scenario === s.id && x.ok).map((x) => x.ms);
    const failed = samples.filter((x) => x.scenario === s.id && !x.ok).length;
    const sum = summarize(ok);
    return { id: s.id, title: s.title, group: s.group, targetP95Ms: s.targetP95Ms ?? null, ...sum, errors: failed, implemented: s.implemented !== false };
  });
  const reasons = evaluateReference({ declared: declaredEnvironment, data, load: { ...load, notImplemented: scenarios.filter((s) => s.implemented === false).map((s) => s.id) } });
  let verdict = "NOT VERIFIED";
  const perScenario = {};
  if (reasons.length === 0) {
    for (const s of byScenario) perScenario[s.id] = s.errors === 0 && s.p95 !== null && s.targetP95Ms !== null && s.p95 < s.targetP95Ms ? "within target" : "NOT within target";
    verdict = Object.values(perScenario).every((v) => v === "within target") ? "MEASURED WITHIN TARGETS" : "MEASURED, TARGETS NOT MET";
  }
  return {
    schema: "hasm-loadtest-results/1",
    verdict,
    verdictReasons: reasons,
    notice: "Measured samples only; nothing here is estimated. Without a verified reference environment these numbers are NOT acceptance evidence (SRS 18, D-038).",
    perScenarioVerdict: perScenario,
    startedAtUtc, finishedAtUtc,
    tool,
    environment: {
      declaredReferenceEnvironment: declaredEnvironment || null,
      declaredIsNotVerifiedByTheTool: true,
      loadGenerator: { platform: os.platform(), release: os.release(), arch: os.arch(), cpus: os.cpus().length, memTotalGb: Math.round(os.totalmem() / 2 ** 30), node: process.version },
    },
    data, load, referenceRequirements: REFERENCE,
    scenarios: byScenario,
  };
}
module.exports = { REFERENCE, evaluateReference, buildResults };
