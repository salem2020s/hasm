#!/usr/bin/env node
// Deterministic generator for company migration 0033 (Phase 3.14 slice S1: the cash and bank account master). The number is PROVISIONAL and follows the merge order: change MIGRATION below, rename the committed files and re-run --write (the number is part of the name seed, so a renumbering is a regeneration).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CashAccount.Code}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.cash-bank.v1:" + token)
// (same rules as tools/schema/company-0027-customer-groups.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist are written [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0033-cash-bank.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0033";   // the company migration number (provisional, by merge order)
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_cash_accounts.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.CashBank.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Accounts.cs"), ...parseDictionary("CompanySchema.Branches.cs"), ...parseDictionary("CompanySchema.Configuration.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration ${MIGRATION}: the cash and bank account master (Phase 3.14 slice S1; decisions D-155, D-211). Forward-only and ADDITIVE: one new table with its guards; no existing table, function or trigger is altered, nothing is seeded, and nothing here touches the ledger.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-${MIGRATION}-cash-bank.cjs (logical dictionary: CompanySchema.CashBank.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.14.md, D-211 Q1, Q2, Q8): a cashbox or a bank account is its own master record, bound 1:1 to ONE postable ACTIVE asset account of the chart, with a required branch, in the BASE currency only (foreign-currency
-- accounts wait for Phase 3.18). It is CONFIGURATION: it holds no balance (the ledger is the source of truth) and creates no journal entry. The full bank account number is sensitive: it is stored ONLY as an authenticated envelope
-- (the key-ring encryption of the attachments), with its last four characters kept apart as the masked form.
-- Invariants enforced by PostgreSQL:
--   * rows are never removed; the identity, code, kind and creation data never change; every change advances the revision by one;
--   * the code is unique; ONE general-ledger account serves at most ONE cash or bank account;
--   * the bound account must be an ACTIVE, non-group ASSET account whose currency policy admits the base currency (Base, Any, or Specific equal to the base currency); a company without a selected base currency has no cash account;
--   * a cashbox carries no bank details; a bank account's details are all or nothing for the number (cipher, key id and masked form together).

CREATE TABLE {{CashAccount}} (
    {{CashAccount.Id}} uuid NOT NULL,
    {{CashAccount.Kind}} smallint NOT NULL,
    {{CashAccount.Code}} varchar(30) NOT NULL,
    {{CashAccount.NameAr}} varchar(200) NULL,
    {{CashAccount.NameEn}} varchar(200) NULL,
    {{CashAccount.BranchId}} uuid NOT NULL,
    {{CashAccount.AccountId}} uuid NOT NULL,
    {{CashAccount.Currency}} varchar(3) NOT NULL,
    {{CashAccount.BankName}} varchar(200) NULL,
    {{CashAccount.BankBranchName}} varchar(200) NULL,
    {{CashAccount.NumberCipher}} bytea NULL,
    {{CashAccount.NumberKeyId}} varchar(40) NULL,
    {{CashAccount.NumberLast4}} varchar(4) NULL,
    {{CashAccount.IsActive}} boolean NOT NULL DEFAULT true,
    {{CashAccount.Revision}} bigint NOT NULL DEFAULT 1,
    {{CashAccount.CreatedBy}} uuid NOT NULL,
    {{CashAccount.CreatedAtUtc}} timestamptz NOT NULL,
    {{CashAccount.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{CashAccount.Pk}} PRIMARY KEY ({{CashAccount.Id}}),
    CONSTRAINT {{CashAccount.FkBranch}} FOREIGN KEY ({{CashAccount.BranchId}}) REFERENCES [[Branch.Table]] ([[Branch.Id]]),
    CONSTRAINT {{CashAccount.FkAccount}} FOREIGN KEY ({{CashAccount.AccountId}}) REFERENCES [[Account.Table]] ([[Account.Id]]),
    CONSTRAINT {{CashAccount.CkValues}} CHECK ({{CashAccount.Revision}} > 0 AND {{CashAccount.Kind}} IN (1, 2) AND {{CashAccount.Code}} ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$'
        AND ({{CashAccount.NameAr}} IS NOT NULL OR {{CashAccount.NameEn}} IS NOT NULL)
        AND ({{CashAccount.NameAr}} IS NULL OR char_length(btrim({{CashAccount.NameAr}})) >= 1) AND ({{CashAccount.NameEn}} IS NULL OR char_length(btrim({{CashAccount.NameEn}})) >= 1)
        AND {{CashAccount.Currency}} ~ '^[A-Z]{3}$'),
    CONSTRAINT {{CashAccount.CkBank}} CHECK (
        ({{CashAccount.Kind}} = 1 AND {{CashAccount.BankName}} IS NULL AND {{CashAccount.BankBranchName}} IS NULL AND {{CashAccount.NumberCipher}} IS NULL AND {{CashAccount.NumberKeyId}} IS NULL AND {{CashAccount.NumberLast4}} IS NULL)
        OR ({{CashAccount.Kind}} = 2 AND (({{CashAccount.NumberCipher}} IS NULL AND {{CashAccount.NumberKeyId}} IS NULL AND {{CashAccount.NumberLast4}} IS NULL)
            OR ({{CashAccount.NumberCipher}} IS NOT NULL AND {{CashAccount.NumberKeyId}} IS NOT NULL AND {{CashAccount.NumberLast4}} IS NOT NULL AND char_length({{CashAccount.NumberLast4}}) BETWEEN 1 AND 4)))
    )
);
CREATE UNIQUE INDEX {{CashAccount.UqCode}} ON {{CashAccount}} ({{CashAccount.Code}});
CREATE UNIQUE INDEX {{CashAccount.UqAccount}} ON {{CashAccount}} ({{CashAccount.AccountId}});
CREATE INDEX {{CashAccount.IxBranch}} ON {{CashAccount}} ({{CashAccount.BranchId}});

CREATE FUNCTION {{CashAccount.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{CashAccount.Revision}} <> 1 THEN RAISE EXCEPTION 'a cash account is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cash and bank accounts are never removed'; END IF;
    IF NEW.{{CashAccount.Id}} IS DISTINCT FROM OLD.{{CashAccount.Id}} OR NEW.{{CashAccount.Code}} IS DISTINCT FROM OLD.{{CashAccount.Code}} OR NEW.{{CashAccount.Kind}} IS DISTINCT FROM OLD.{{CashAccount.Kind}}
       OR NEW.{{CashAccount.CreatedBy}} IS DISTINCT FROM OLD.{{CashAccount.CreatedBy}} OR NEW.{{CashAccount.CreatedAtUtc}} IS DISTINCT FROM OLD.{{CashAccount.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, code, kind and creation data of a cash or bank account are immutable';
    END IF;
    IF NEW.{{CashAccount.Revision}} <> OLD.{{CashAccount.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CashAccount.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CashAccount}} FOR EACH ROW EXECUTE FUNCTION {{CashAccount.FnGuard}}();
CREATE TRIGGER {{CashAccount.TrTruncate}} BEFORE TRUNCATE ON {{CashAccount}} FOR EACH STATEMENT EXECUTE FUNCTION {{CashAccount.FnGuard}}();

-- the binding rule, checked whenever a row is written or its account or currency changes: the account is an active, non-group asset account that admits the base currency, and the row's currency IS the base currency
CREATE FUNCTION {{CashAccount.FnBinding}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    a record;
    v_base varchar(3);
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.{{CashAccount.AccountId}} IS NOT DISTINCT FROM OLD.{{CashAccount.AccountId}} AND NEW.{{CashAccount.Currency}} IS NOT DISTINCT FROM OLD.{{CashAccount.Currency}} AND NEW.{{CashAccount.IsActive}} IS NOT DISTINCT FROM OLD.{{CashAccount.IsActive}} THEN
        RETURN NEW;   -- names, bank details and branch do not touch the binding rule
    END IF;
    SELECT cc.[[CompanyCurrency.Code]] INTO v_base FROM [[CompanyCurrency.Table]] cc WHERE cc.[[CompanyCurrency.IsBase]] AND cc.[[CompanyCurrency.Enabled]] LIMIT 1;
    IF v_base IS NULL THEN RAISE EXCEPTION 'cash_base_currency_not_selected'; END IF;
    IF NEW.{{CashAccount.Currency}} <> v_base THEN RAISE EXCEPTION 'cash_account_currency_not_base'; END IF;
    SELECT x.[[Account.Type]] AS type, x.[[Account.IsGroup]] AS is_group, x.[[Account.IsActive]] AS is_active, x.[[Account.CurrencyPolicy]] AS policy, x.[[Account.CurrencyCode]] AS ccy
      INTO a FROM [[Account.Table]] x WHERE x.[[Account.Id]] = NEW.{{CashAccount.AccountId}};
    IF NOT FOUND THEN RAISE EXCEPTION 'cash_account_gl_not_found'; END IF;
    IF a.type <> 1 THEN RAISE EXCEPTION 'cash_account_gl_not_asset'; END IF;
    IF a.is_group OR NOT a.is_active THEN RAISE EXCEPTION 'cash_account_gl_not_postable'; END IF;
    IF NOT (a.policy IN (1, 2) OR (a.policy = 3 AND a.ccy = v_base)) THEN RAISE EXCEPTION 'cash_account_gl_currency'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CashAccount.TrBinding}} BEFORE INSERT OR UPDATE ON {{CashAccount}} FOR EACH ROW EXECUTE FUNCTION {{CashAccount.FnBinding}}();

REVOKE ALL ON TABLE {{CashAccount}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company." + MIGRATION + ".cash-bank.v1:" + token).digest("hex").slice(0, 8);

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
  const entities = [...new Set(tokens.map((t) => t.split(".")[0]))];
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-" + MIGRATION + "-cash-bank.cjs for company migration " + MIGRATION + " (Phase 3.14 slice S1, cash and bank accounts). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// CashAccount names the table, its columns, constraints, indexes, functions and triggers.\n" + "public static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> CashBankMap { get; } = new Dictionary<string, string>\n    {\n";
  for (const t of tokens) cs += `        ["${t}"] = ${t.includes(".") ? t : t + ".Table"},\n`;
  cs += "    };\n}\n";
  return { sql, cs };
}

const { sql, cs } = generate();
const mode = process.argv[2];
if (mode === "--write") { fs.writeFileSync(outSql, sql); fs.writeFileSync(outCs, cs); console.log("written"); }
else if (mode === "--check") {
  const norm = (s) => s.replace(/\r\n/g, "\n");
  const ok = fs.existsSync(outSql) && fs.existsSync(outCs) && norm(fs.readFileSync(outSql, "utf8")) === sql && norm(fs.readFileSync(outCs, "utf8")) === cs;
  if (!ok) { console.error("generated cash-bank schema differs from the committed files"); process.exit(1); }
  console.log("cash-bank schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
