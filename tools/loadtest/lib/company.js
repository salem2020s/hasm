"use strict";
// What is already in the target company. The harness only ever writes into a company that holds nothing but its own data: items and parties
// whose code STARTS with "LT-" (case-sensitive), journal entries described "LT entry ..." and stock documents referenced "LT-S-...". It reads
// everything the signed-in user may see (a user with a narrower scope sees less: use a company-wide user). Counts are MEASURED here and are what
// a results file's verdict is based on.
const PAGE = 500;

async function pagedWithTotal(client, name, key) {
  const out = []; let offset = 0, total = Infinity;
  while (offset < total) {
    const r = await client.call(name, undefined, { query: { offset, limit: PAGE } });
    const rows = r.json[key]; out.push(...rows); total = r.json.total; offset += rows.length;   // advance by the rows RETURNED: the server may cap a page below the requested size
    if (rows.length === 0) break;
  }
  return out;
}
async function journalEntries(client) {
  const out = []; for (let offset = 0; ;) {
    const page = (await client.call("journalList", undefined, { query: { offset, limit: PAGE } })).json.entries; if (page.length === 0) return out; out.push(...page); offset += page.length;
  }
}

async function inspectCompany(client) {
  const foreign = []; const note = (kind, v) => { if (foreign.length < 5) foreign.push(`${kind} ${JSON.stringify(v)}`); foreign.count = (foreign.count || 0) + 1; };
  const items = await pagedWithTotal(client, "items", "items"); for (const x of items) if (!String(x.code).startsWith("LT-")) note("item", x.code);
  const parties = await pagedWithTotal(client, "parties", "parties"); for (const x of parties) if (!String(x.code).startsWith("LT-")) note("party", x.code);
  const journals = await journalEntries(client); for (const x of journals) if (!String(x.description || "").startsWith("LT entry ")) note("journal entry", x.number || x.id);
  const docs = await pagedWithTotal(client, "stockDocList", "documents"); for (const x of docs) if (!String(x.reference || "").startsWith("LT-S-")) note("stock document", x.number || x.id);
  return { measured: { items: items.length, parties: parties.length, journals: journals.length, stockDocs: docs.length }, foreign, foreignCount: foreign.count || 0 };
}

async function assertLoadTestOnly(client) {
  const r = await inspectCompany(client);
  if (r.foreignCount) { const e = new Error(`refused: the company holds ${r.foreignCount} record(s) that are not load-test data, for example ${r.foreign.join(", ")}`); e.code = "company_not_loadtest_only"; throw e; }
  return r.measured;
}
module.exports = { inspectCompany, assertLoadTestOnly };
