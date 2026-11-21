"use strict";
// Scenario runner skeleton. A scenario is { id, title, group, targetP95Ms, weight, run(ctx, k) } where run performs ONE iteration and returns a
// list of timed samples [{ scenario, ms, ok }] (several for a multi-step flow). Scenarios that cannot run yet are declared with
// implemented:false so a results file lists them as missing instead of omitting them.
const plan = require("./plan");
const { rng } = require("./prng");
const { REFERENCE } = require("./results");
const T = REFERENCE.p95TargetMs;

function terms(seed, k, counts) {
  const r = rng(seed, "query", k); const m = r.int(0, 3);
  if (m === 0) return `LT-I-${String(r.int(1, Math.max(1, counts.items))).padStart(6, "0")}`;  // exact code
  if (m === 1) return `LT-I-${String(r.int(1, 99)).padStart(2, "0")}`;                          // code prefix (many hits)
  if (m === 2) return r.pick(plan.EN_NOUN).split(" ")[0];                                       // English name word
  return r.pick(plan.AR_NOUN).split(" ")[0];                                                    // Arabic name word
}
const partyTerm = (seed, k, counts) => { const r = rng(seed, "pquery", k); const m = r.int(0, 2);
  return m === 0 ? `LT-P-${String(r.int(1, Math.max(1, counts.parties))).padStart(6, "0")}` : m === 1 ? r.pick(plan.FAMILY) : r.pick(plan.FIRST); };

async function timed(scenario, fn) {
  try { const r = await fn(); return { scenario, ms: r.ms, ok: r.status >= 200 && r.status < 300 }; }
  catch { return { scenario, ms: null, ok: false }; }
}
const get = (ctx, name, query) => ctx.client.request("GET", require("./endpoints").path(name), { query });

function defineScenarios() {
  return [
    { id: "item_search", title: "Item search (picker, 20 rows)", group: "search", targetP95Ms: T.search, weight: 30,
      run: async (c, k) => [await timed("item_search", () => get(c, "items", { search: terms(c.seed, k, c.counts), limit: 20 }))] },
    { id: "party_search", title: "Party search (picker, 20 rows)", group: "search", targetP95Ms: T.search, weight: 15,
      run: async (c, k) => [await timed("party_search", () => get(c, "parties", { search: partyTerm(c.seed, k, c.counts), limit: 20 }))] },
    { id: "customer_search", title: "Customer search (customer roles, 20 rows)", group: "search", targetP95Ms: T.search, weight: 15,
      run: async (c, k) => [await timed("customer_search", () => get(c, "partyRoles", { kind: 1, search: partyTerm(c.seed, k, c.counts), limit: 20 }))] },
    { id: "screen_load", title: "Ordinary screen-load API calls (one sample per call)", group: "screen", targetP95Ms: T.screen, weight: 20,
      run: async (c) => Promise.all([["accounts"], ["branches"], ["warehouses"], ["journalList", { limit: 50 }], ["stockDocList", { limit: 50 }], ["items", { limit: 50 }], ["parties", { limit: 50 }]]
        .map(([name, q]) => timed("screen_load", () => get(c, name, q)))) },
    { id: "save_post_journal", title: "Save and post a journal entry (create + submit + post, total)", group: "save_post", targetP95Ms: T.save_post, weight: 10,
      run: async (c, k) => {
        const body = { ...plan.journal(c.seed, 1_000_000 + k, c.accountIds, c.year), branchId: c.branchId }; const t0 = process.hrtime.bigint();
        try {
          const created = await c.client.call("journalCreate", undefined, { body }); const e = created.json.entry;
          const sub = await c.client.call("journalSubmit", { id: e.id }, { ifMatch: e.revision });
          await c.client.call("journalPost", { id: e.id }, { ifMatch: sub.json.entry.revision });
          return [{ scenario: "save_post_journal", ms: Number(process.hrtime.bigint() - t0) / 1e6, ok: true }];
        } catch { return [{ scenario: "save_post_journal", ms: null, ok: false }]; }
      } },
    { id: "report_trial_balance", title: "Trial balance, whole year", group: "report", targetP95Ms: T.report, weight: 5,
      run: async (c) => [await timed("report_trial_balance", () => get(c, "trialBalance", { from: `${c.year}-01-01`, to: `${c.year}-12-31` }))] },
    { id: "report_general_ledger", title: "General ledger of one account, 100 rows", group: "report", targetP95Ms: T.report, weight: 5,
      run: async (c, k) => [await timed("report_general_ledger", () => get(c, "generalLedger", { accountId: c.accountIds[k % c.accountIds.length], from: `${c.year}-01-01`, to: `${c.year}-12-31`, limit: 100 }))] },
    { id: "invoice_save_post", title: "Save and post an ordinary invoice", group: "save_post", targetP95Ms: T.save_post, weight: 0, implemented: false,
      run: async () => [] },
    { id: "stock_balance_report", title: "Stock balance report", group: "report", targetP95Ms: T.report, weight: 0, implemented: false,
      run: async () => [] },
  ];
}

/** Runs the weighted mix with `concurrency` workers until `iterations` (smoke) or `durationSeconds` is reached. Warm-up iterations are discarded. */
async function runLoad({ ctx, scenarios, concurrency, iterations, durationSeconds, warmup = 0 }) {
  const active = scenarios.filter((s) => s.implemented !== false && s.weight > 0);
  const bag = active.flatMap((s) => Array(s.weight).fill(s));
  const samples = []; let k = 0; const deadline = durationSeconds ? Date.now() + durationSeconds * 1000 : Infinity;
  const worker = async () => {
    for (;;) {
      const n = ++k; if (iterations !== undefined && n > iterations + warmup) return; if (Date.now() >= deadline) return;
      const s = bag[Math.floor(rng(ctx.seed, "mix", n).next() * bag.length)];
      const out = await s.run(ctx, n);
      if (n > warmup) samples.push(...out);
    }
  };
  const t0 = Date.now(); await Promise.all(Array.from({ length: concurrency }, worker));
  return { samples, elapsedSeconds: (Date.now() - t0) / 1000 };
}
module.exports = { defineScenarios, runLoad };
