#!/usr/bin/env node
// Deterministic generator for company migration 0027 (Phase 3.11 customer groups: the customer-group master, the customer -> group assignment, and the group scope of price lists and discount rules).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CustomerGroup.Code}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0027.customer-groups.v1:" + token)
// (same rules as tools/schema/company-0026-pricing.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migration 0026 and 0021/0023) are written
// [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0027-customer-groups.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0027_company_customer_groups.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.CustomerGroups.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Pricing.cs"), ...parseDictionary("CompanySchema.Parties.cs"), ...parseDictionary("CompanySchema.PartyRoles.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration 0027: customer groups (Phase 3.11). Forward-only, additive (two new tables and one nullable column on each of two tables); no existing row is read or modified, nothing is seeded.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0027-customer-groups.cjs (logical dictionary: CompanySchema.CustomerGroups.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 8.1: price lists and discounts by customer GROUP as well as by customer): the company's customer-group master (code, names, active flag), the assignment of a customer party to AT MOST ONE group
-- at a time (one row per party: the group, or none), and the group scope of price lists and discount rules. It is CONFIGURATION: no document or amount exists here. Immutable pricing snapshots of final
-- documents keep the values that applied, so a later regrouping or list change never re-prices them.
-- Invariants enforced by PostgreSQL:
--   * groups and assignments are never removed; a group's code and creation data never change; every change advances the revision by one;
--   * a party has at most one assignment row (primary key on the party), so it belongs to at most one group at a time;
--   * a price list or discount rule is scoped to a customer OR a customer group, never both (the engine refuses both); the group of a price list or rule is immutable (a different group is a new list or rule);
--   * two ACTIVE discount rules never share customer, group, item scope, priority, minimum quantity and start (the earlier tie index, now including the group).

CREATE TABLE {{CustomerGroup}} (
    {{CustomerGroup.Id}} uuid NOT NULL,
    {{CustomerGroup.Code}} varchar(30) NOT NULL,
    {{CustomerGroup.NameAr}} varchar(200) NOT NULL,
    {{CustomerGroup.NameEn}} varchar(200) NULL,
    {{CustomerGroup.IsActive}} boolean NOT NULL DEFAULT true,
    {{CustomerGroup.Revision}} bigint NOT NULL DEFAULT 1,
    {{CustomerGroup.CreatedBy}} uuid NOT NULL,
    {{CustomerGroup.CreatedAtUtc}} timestamptz NOT NULL,
    {{CustomerGroup.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{CustomerGroup.Pk}} PRIMARY KEY ({{CustomerGroup.Id}}),
    CONSTRAINT {{CustomerGroup.CkValues}} CHECK ({{CustomerGroup.Revision}} > 0 AND {{CustomerGroup.Code}} ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim({{CustomerGroup.NameAr}})) >= 1)
);
CREATE UNIQUE INDEX {{CustomerGroup.UqCode}} ON {{CustomerGroup}} ({{CustomerGroup.Code}});

CREATE FUNCTION {{CustomerGroup.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{CustomerGroup.Revision}} <> 1 THEN RAISE EXCEPTION 'a customer group is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'customer groups are never removed'; END IF;
    IF NEW.{{CustomerGroup.Id}} IS DISTINCT FROM OLD.{{CustomerGroup.Id}} OR NEW.{{CustomerGroup.Code}} IS DISTINCT FROM OLD.{{CustomerGroup.Code}}
       OR NEW.{{CustomerGroup.CreatedBy}} IS DISTINCT FROM OLD.{{CustomerGroup.CreatedBy}} OR NEW.{{CustomerGroup.CreatedAtUtc}} IS DISTINCT FROM OLD.{{CustomerGroup.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, code and creation data of a customer group are immutable';
    END IF;
    IF NEW.{{CustomerGroup.Revision}} <> OLD.{{CustomerGroup.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CustomerGroup.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CustomerGroup}} FOR EACH ROW EXECUTE FUNCTION {{CustomerGroup.FnGuard}}();
CREATE TRIGGER {{CustomerGroup.TrTruncate}} BEFORE TRUNCATE ON {{CustomerGroup}} FOR EACH STATEMENT EXECUTE FUNCTION {{CustomerGroup.FnGuard}}();
REVOKE ALL ON TABLE {{CustomerGroup}} FROM PUBLIC;

CREATE TABLE {{CustomerGroupMember}} (
    {{CustomerGroupMember.PartyId}} uuid NOT NULL,
    {{CustomerGroupMember.CustomerGroupId}} uuid NULL,
    {{CustomerGroupMember.Revision}} bigint NOT NULL DEFAULT 1,
    {{CustomerGroupMember.CreatedBy}} uuid NOT NULL,
    {{CustomerGroupMember.CreatedAtUtc}} timestamptz NOT NULL,
    {{CustomerGroupMember.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{CustomerGroupMember.Pk}} PRIMARY KEY ({{CustomerGroupMember.PartyId}}),
    CONSTRAINT {{CustomerGroupMember.FkParty}} FOREIGN KEY ({{CustomerGroupMember.PartyId}}) REFERENCES [[LocalParty.Table]] ([[LocalParty.Id]]),
    CONSTRAINT {{CustomerGroupMember.FkGroup}} FOREIGN KEY ({{CustomerGroupMember.CustomerGroupId}}) REFERENCES {{CustomerGroup}} ({{CustomerGroup.Id}}),
    CONSTRAINT {{CustomerGroupMember.CkValues}} CHECK ({{CustomerGroupMember.Revision}} > 0)
);
CREATE INDEX {{CustomerGroupMember.IxGroup}} ON {{CustomerGroupMember}} ({{CustomerGroupMember.CustomerGroupId}}) WHERE {{CustomerGroupMember.CustomerGroupId}} IS NOT NULL;

CREATE FUNCTION {{CustomerGroupMember.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{CustomerGroupMember.Revision}} <> 1 THEN RAISE EXCEPTION 'a customer group assignment is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'customer group assignments are never removed'; END IF;
    IF NEW.{{CustomerGroupMember.PartyId}} IS DISTINCT FROM OLD.{{CustomerGroupMember.PartyId}}
       OR NEW.{{CustomerGroupMember.CreatedBy}} IS DISTINCT FROM OLD.{{CustomerGroupMember.CreatedBy}} OR NEW.{{CustomerGroupMember.CreatedAtUtc}} IS DISTINCT FROM OLD.{{CustomerGroupMember.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the party and creation data of a customer group assignment are immutable';
    END IF;
    IF NEW.{{CustomerGroupMember.Revision}} <> OLD.{{CustomerGroupMember.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CustomerGroupMember.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CustomerGroupMember}} FOR EACH ROW EXECUTE FUNCTION {{CustomerGroupMember.FnGuard}}();
CREATE TRIGGER {{CustomerGroupMember.TrTruncate}} BEFORE TRUNCATE ON {{CustomerGroupMember}} FOR EACH STATEMENT EXECUTE FUNCTION {{CustomerGroupMember.FnGuard}}();
REVOKE ALL ON TABLE {{CustomerGroupMember}} FROM PUBLIC;

-- price lists: the optional group scope (a customer OR a group, never both; the group is fixed at creation)
ALTER TABLE [[PriceList.Table]] ADD COLUMN {{PriceListGroupScope.CustomerGroupId}} uuid NULL;
ALTER TABLE [[PriceList.Table]] ADD CONSTRAINT {{PriceListGroupScope.FkGroup}} FOREIGN KEY ({{PriceListGroupScope.CustomerGroupId}}) REFERENCES {{CustomerGroup}} ({{CustomerGroup.Id}});
ALTER TABLE [[PriceList.Table]] ADD CONSTRAINT {{PriceListGroupScope.CkScope}} CHECK ([[PriceList.CustomerId]] IS NULL OR {{PriceListGroupScope.CustomerGroupId}} IS NULL);

CREATE OR REPLACE FUNCTION [[PriceList.FnGuard]]() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.[[PriceList.Revision]] <> 1 THEN RAISE EXCEPTION 'a price list is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'price lists are never removed'; END IF;
    IF NEW.[[PriceList.Id]] IS DISTINCT FROM OLD.[[PriceList.Id]] OR NEW.[[PriceList.Code]] IS DISTINCT FROM OLD.[[PriceList.Code]] OR NEW.[[PriceList.Currency]] IS DISTINCT FROM OLD.[[PriceList.Currency]]
       OR NEW.[[PriceList.CustomerId]] IS DISTINCT FROM OLD.[[PriceList.CustomerId]] OR NEW.{{PriceListGroupScope.CustomerGroupId}} IS DISTINCT FROM OLD.{{PriceListGroupScope.CustomerGroupId}}
       OR NEW.[[PriceList.CreatedBy]] IS DISTINCT FROM OLD.[[PriceList.CreatedBy]] OR NEW.[[PriceList.CreatedAtUtc]] IS DISTINCT FROM OLD.[[PriceList.CreatedAtUtc]] THEN
        RAISE EXCEPTION 'the identity, code, currency, customer, customer group and creation data of a price list are immutable';
    END IF;
    IF NEW.[[PriceList.Revision]] <> OLD.[[PriceList.Revision]] + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;

-- discount rules: the optional group scope, and the active-tie index extended with the group
ALTER TABLE [[DiscountRule.Table]] ADD COLUMN {{DiscountRuleGroupScope.CustomerGroupId}} uuid NULL;
ALTER TABLE [[DiscountRule.Table]] ADD CONSTRAINT {{DiscountRuleGroupScope.FkGroup}} FOREIGN KEY ({{DiscountRuleGroupScope.CustomerGroupId}}) REFERENCES {{CustomerGroup}} ({{CustomerGroup.Id}});
ALTER TABLE [[DiscountRule.Table]] ADD CONSTRAINT {{DiscountRuleGroupScope.CkScope}} CHECK ([[DiscountRule.CustomerId]] IS NULL OR {{DiscountRuleGroupScope.CustomerGroupId}} IS NULL);

DROP INDEX [[DiscountRule.UqActiveKey]];
CREATE UNIQUE INDEX [[DiscountRule.UqActiveKey]] ON [[DiscountRule.Table]] (COALESCE([[DiscountRule.CustomerId]], '00000000-0000-0000-0000-000000000000'::uuid),
    COALESCE({{DiscountRuleGroupScope.CustomerGroupId}}, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE([[DiscountRule.ItemId]], '00000000-0000-0000-0000-000000000000'::uuid),
    COALESCE([[DiscountRule.ItemGroupId]], '00000000-0000-0000-0000-000000000000'::uuid), [[DiscountRule.Priority]], [[DiscountRule.MinQuantity]], [[DiscountRule.ValidFrom]]) WHERE [[DiscountRule.IsActive]];

CREATE OR REPLACE FUNCTION [[DiscountRule.FnGuard]]() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.[[DiscountRule.Revision]] <> 1 THEN RAISE EXCEPTION 'a discount rule is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'discount rules are never removed'; END IF;
    IF NEW.[[DiscountRule.Id]] IS DISTINCT FROM OLD.[[DiscountRule.Id]] OR NEW.[[DiscountRule.Code]] IS DISTINCT FROM OLD.[[DiscountRule.Code]]
       OR NEW.[[DiscountRule.CustomerId]] IS DISTINCT FROM OLD.[[DiscountRule.CustomerId]] OR NEW.{{DiscountRuleGroupScope.CustomerGroupId}} IS DISTINCT FROM OLD.{{DiscountRuleGroupScope.CustomerGroupId}}
       OR NEW.[[DiscountRule.ItemId]] IS DISTINCT FROM OLD.[[DiscountRule.ItemId]] OR NEW.[[DiscountRule.ItemGroupId]] IS DISTINCT FROM OLD.[[DiscountRule.ItemGroupId]]
       OR NEW.[[DiscountRule.CreatedBy]] IS DISTINCT FROM OLD.[[DiscountRule.CreatedBy]] OR NEW.[[DiscountRule.CreatedAtUtc]] IS DISTINCT FROM OLD.[[DiscountRule.CreatedAtUtc]] THEN
        RAISE EXCEPTION 'the identity, code, scope and creation data of a discount rule are immutable';
    END IF;
    IF NEW.[[DiscountRule.Revision]] <> OLD.[[DiscountRule.Revision]] + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0027.customer-groups.v1:" + token).digest("hex").slice(0, 8);

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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0027-customer-groups.cjs for company migration 0027 (Phase 3.11 customer groups). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// PriceListGroupScope and DiscountRuleGroupScope name the NEW columns and constraints added to the existing price list and discount rule tables (they have no table of their own).\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> CustomerGroupMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated customer-group schema differs from the committed files"); process.exit(1); }
  console.log("customer-group schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
