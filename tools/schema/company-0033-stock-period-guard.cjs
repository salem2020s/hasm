#!/usr/bin/env node
// Deterministic generator for the company migration of the STOCK-PERIOD-DB guard (owner decision D-220, option b). The number follows the merge order: change MIGRATION below, rename this file and the
// committed migration, and re-run with --write (the number is part of the name seed, so a renumbering is a regeneration).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{StockPeriodGuard.FnGuard}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.stock-period-guard.v1:" + token)
// (same rules as tools/schema/company-0032-commercial-policies.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migrations 0006, 0014, 0016, 0017,
// 0019 and 0020) are written [[Entity.Name]] and resolved from the committed dictionaries, never re-derived.
// Usage:  node tools/schema/company-0033-stock-period-guard.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0033";   // the company migration number: it is part of the name seed, so a renumbering is a regeneration
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_stock_period_guard.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.StockPeriodGuard.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map(["CompanySchema.Fiscal.cs", "CompanySchema.Costing.cs", "CompanySchema.StockPosting.cs", "CompanySchema.StockReversal.cs", "CompanySchema.StockAdjustment.cs", "CompanySchema.NegativeStock.cs"]
  .flatMap((f) => [...parseDictionary(f)]));
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

// The guarded tables: [trigger token, table, date column]. One row per kind of stock or cost posting record that carries its own effective date.
const GUARDED = [
  ["TrCostOperation", "CostOperation.Table", "CostOperation.EffectiveDate", "cost operations (receipt, issue, transfer, reversal)"],
  ["TrCostEntry", "CostEntry.Table", "CostEntry.EffectiveDate", "cost entries (nothing else ties an entry's date to its operation's date)"],
  ["TrStockPosting", "StockPosting.Table", "StockPosting.EffectiveDate", "stock postings (receipt, issue, transfer documents)"],
  ["TrStockReversal", "StockPostingReversal.Table", "StockPostingReversal.EffectiveDate", "reversals of stock postings (dated on the reversal's own date)"],
  ["TrAdjustmentPosting", "StockAdjustmentPosting.Table", "StockAdjustmentPosting.EffectiveDate", "stock adjustment postings (also the effect of a stocktake)"],
  ["TrAdjustmentReversal", "StockAdjustmentReversal.Table", "StockAdjustmentReversal.EffectiveDate", "reversals of stock adjustments"],
  ["TrNegativeObligation", "NegativeObligation.Table", "NegativeObligation.EffectiveDate", "negative-stock obligations"],
  ["TrNegativeSettlement", "NegativeSettlement.Table", "NegativeSettlement.EffectiveDate", "negative-stock settlements (dated on the settling receipt)"],
  ["TrNegativeObligationReversal", "NegativeObligationReversal.Table", "NegativeObligationReversal.EffectiveDate", "reversals of negative-stock obligations"],
];

const TEMPLATE = `-- Migration ${MIGRATION}: the STOCK-PERIOD-DB guard (owner decision D-220, option b). Forward-only and ADDITIVE: one new function and nine new BEFORE INSERT triggers. No table is created or altered, no
-- existing function, trigger or constraint is replaced, disabled or dropped, nothing is seeded and no existing row is read, validated or modified. Opaque physical names (D-029) generated deterministically by
-- tools/schema/company-${MIGRATION}-stock-period-guard.cjs (logical dictionary: CompanySchema.StockPeriodGuard.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: until now only the services refused a stock or cost posting into a fiscal period that is not Open (FiscalPeriodGate, allowSoftClosed = false), while journals have a database guard
-- (migrations 0008 and 0009). From this migration on PostgreSQL itself refuses to INSERT a stock or cost posting record whose effective date lies in a Soft Closed period, in a Final Closed period, or in no
-- fiscal period at all, whoever writes it (a service, a tool, plain SQL). There is no soft-close override for stock, exactly as in the services.
-- HOW: the trigger function reads the date column named by its argument and calls the posting-availability function of migration 0006, unchanged, with allow_soft_closed = false. That function locks the
-- period row FOR SHARE until the end of the transaction, so a concurrent close (which updates the row) waits for the posting, and a posting that waited for a close sees the closed state and is refused.
-- A posting service already holds that share lock (it calls FiscalPeriodGate before its first insert), so the guard adds no lock and no lock order to any service path.
-- The function returns NEW unchanged and writes nothing. It is not SECURITY DEFINER and sets no search_path: it runs with the rights of the writer, like every other trigger of this schema.
-- Records without an effective date of their own (cost layers and consumptions, which must reference a cost entry) are not guarded here.

CREATE FUNCTION {{StockPeriodGuard.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_date date;
BEGIN
    IF TG_NARGS <> 1 THEN RAISE EXCEPTION 'the stock period guard needs the name of the date column'; END IF;
    v_date := (to_jsonb(NEW) ->> TG_ARGV[0])::date;
    IF v_date IS NULL THEN RAISE EXCEPTION 'a stock or cost posting needs an effective date'; END IF;
    PERFORM [[FiscalCalendar.PostableFunction]](v_date, false);
    RETURN NEW;
END;
$$;

${GUARDED.map(([tr, table, column, what]) => `-- ${what}\nCREATE TRIGGER {{StockPeriodGuard.${tr}}} BEFORE INSERT ON [[${table}]] FOR EACH ROW EXECUTE FUNCTION {{StockPeriodGuard.FnGuard}}('[[${column}]]');`).join("\n")}
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update(`hasm.company.${MIGRATION}.stock-period-guard.v1:` + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (!/\.(sql|cs)$/.test(e.name)) continue;
      for (const m of fs.readFileSync(p, "utf8").matchAll(/\b[tcfkr]_[0-9a-f]{8}\b/g)) names.add(m[0]);
    }
  };
  scan(path.join(infra, "CompanyDatabase")); scan(path.join(infra, "SystemDatabase"));
  return names;
}

function generate() {
  const body = TEMPLATE.replace(/\[\[([A-Za-z0-9.]+)\]\]/g, (_, k) => ext(k));
  const tokens = [...new Set([...body.matchAll(/\{\{([A-Za-z0-9.]+)\}\}/g)].map((m) => m[1]))];
  const taken = existingNames();
  const byToken = new Map(), seen = new Map();
  for (const t of tokens) {
    const name = physical(t);
    if (taken.has(name)) throw new Error(`collision with an existing name: ${t} -> ${name}`);
    if (seen.has(name)) throw new Error(`collision inside the migration: ${t} and ${seen.get(name)} -> ${name}`);
    seen.set(name, t); byToken.set(t, name);
  }
  const sql = body.replace(/\{\{([A-Za-z0-9.]+)\}\}/g, (_, t) => byToken.get(t));
  let cs = `namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-${MIGRATION}-stock-period-guard.cjs for company migration ${MIGRATION} (STOCK-PERIOD-DB guard, owner decision D-220). Do not edit: re-run the generator.\n` +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// StockPeriodGuard names the guard function and its nine insert triggers (no table).\npublic static partial class CompanySchema\n{\n" +
    "    public static class StockPeriodGuard\n    {\n";
  for (const t of tokens) cs += `        public const string ${t.split(".")[1]} = "${byToken.get(t)}";\n`;
  cs += "    }\n\n    private static IReadOnlyDictionary<string, string> StockPeriodGuardMap { get; } = new Dictionary<string, string>\n    {\n";
  for (const t of tokens) cs += `        ["${t}"] = ${t},\n`;
  cs += "    };\n}\n";
  return { sql, cs };
}

const { sql, cs } = generate();
const mode = process.argv[2];
if (mode === "--write") { fs.writeFileSync(outSql, sql); fs.writeFileSync(outCs, cs); console.log("written"); }
else if (mode === "--check") {
  const norm = (s) => s.replace(/\r\n/g, "\n");
  const ok = fs.existsSync(outSql) && fs.existsSync(outCs) && norm(fs.readFileSync(outSql, "utf8")) === sql && norm(fs.readFileSync(outCs, "utf8")) === cs;
  if (!ok) { console.error("generated stock-period-guard schema differs from the committed files"); process.exit(1); }
  console.log("stock-period-guard schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
