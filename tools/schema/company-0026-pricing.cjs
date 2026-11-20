#!/usr/bin/env node
// Deterministic generator for company migration 0026 (Phase 3.11 slice 2b: pricing persistence).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{PriceList.Code}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0026.pricing.v1:" + token)
// so the names are reproducible from this file alone, unique (a collision with a name already in the repository or inside this migration aborts the run, nothing is re-hashed
// silently) and fixed once committed (changing one needs a new migration). Prefix by the last segment of the token: table (no dot) t_, Fn* f_, Tr* r_, Pk/Uq*/Fk*/Ck*/Ix* k_, otherwise c_.
// Usage:  node tools/schema/company-0026-pricing.cjs --write   writes the migration and the C# dictionary
//         node tools/schema/company-0026-pricing.cjs --check   fails when the committed files differ from the generated ones (run by CI and the unit tests)
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0026_company_pricing.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.Pricing.cs");

// physical names of the tables this migration references (inventory master data 0012, local parties 0021)
const EXTERNAL = { ItemTable: "t_2f2a7184", ItemId: "c_be51f22e", GroupTable: "t_2640a66e", GroupId: "c_01551296", UnitTable: "t_aa91e313", UnitId: "c_1a9eb66f", PartyTable: "t_8edce6b0", PartyId: "c_3f494703" };

const TEMPLATE = `-- Migration 0026: pricing configuration (Phase 3.11 slice 2b). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0026-pricing.cjs (logical dictionary: CompanySchema.Pricing.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 8.1-8.4): the company's pricing policy (rounding scales, margin control, and the company-configurable precedence of price lists and discounts; owner decision P), its price lists with
-- their price entries (per item or item group, unit and quantity tier) and its percentage discount rules. It is CONFIGURATION: no document, amount or journal exists here. A final document stores an immutable
-- pricing snapshot of what applied (PricingSnapshots), so a later change of any row below can never re-price a document that already exists.
-- NOT here yet (a later slice, forward migration): the customer-group scope. There is no customer-group registry in the system, so no group column exists; the engine's group scope stays unused until then.
-- Invariants enforced by PostgreSQL:
--   * price lists, price entries and discount rules are never removed; their code/identity/scope and creation data never change; every change advances the revision by one;
--   * a policy version is append-only and is exactly one above the latest (versions 1, 2, 3 ...); scales, margin, action and precedences are within their ranges;
--   * a price list is for ONE customer or general; its currency is three capital letters; its dates are in range; an entry names an item OR an item group (never both, never neither), a unit, a
--     non-negative quantity tier and a non-negative price within the engine's bounds; two entries of one list never share the same item/group, unit and tier;
--   * a discount rule has a percentage within 0-100 and a non-negative minimum quantity; its item scope is an item, an item group or neither; two active rules never share scope, priority, minimum quantity and start.
-- margin action: 1 warn, 2 require approval, 3 block. list precedence: 1 scope then priority (default), 2 priority then scope. discount precedence: 1 specificity then priority (default), 2 priority then specificity.

CREATE TABLE {{PricingPolicyVersion}} (
    {{PricingPolicyVersion.Id}} uuid NOT NULL,
    {{PricingPolicyVersion.Version}} integer NOT NULL,
    {{PricingPolicyVersion.PriceDecimals}} smallint NOT NULL,
    {{PricingPolicyVersion.MoneyDecimals}} smallint NOT NULL,
    {{PricingPolicyVersion.MinMarginPercent}} numeric(9,4) NULL,
    {{PricingPolicyVersion.MarginAction}} smallint NOT NULL,
    {{PricingPolicyVersion.ListPrecedence}} smallint NOT NULL,
    {{PricingPolicyVersion.DiscountPrecedence}} smallint NOT NULL,
    {{PricingPolicyVersion.CreatedBy}} uuid NOT NULL,
    {{PricingPolicyVersion.CreatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{PricingPolicyVersion.Pk}} PRIMARY KEY ({{PricingPolicyVersion.Id}}),
    CONSTRAINT {{PricingPolicyVersion.CkValues}} CHECK ({{PricingPolicyVersion.Version}} > 0 AND {{PricingPolicyVersion.PriceDecimals}} BETWEEN 0 AND 6 AND {{PricingPolicyVersion.MoneyDecimals}} BETWEEN 0 AND 6
        AND ({{PricingPolicyVersion.MinMarginPercent}} IS NULL OR {{PricingPolicyVersion.MinMarginPercent}} BETWEEN -1000 AND 100) AND {{PricingPolicyVersion.MarginAction}} IN (1, 2, 3)
        AND {{PricingPolicyVersion.ListPrecedence}} IN (1, 2) AND {{PricingPolicyVersion.DiscountPrecedence}} IN (1, 2))
);
CREATE UNIQUE INDEX {{PricingPolicyVersion.UqVersion}} ON {{PricingPolicyVersion}} ({{PricingPolicyVersion.Version}});

CREATE FUNCTION {{PricingPolicyVersion.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{PricingPolicyVersion.Version}} <> COALESCE((SELECT max(p.{{PricingPolicyVersion.Version}}) FROM {{PricingPolicyVersion}} p), 0) + 1 THEN
            RAISE EXCEPTION 'a pricing policy version must be exactly one above the latest';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'pricing policy versions are append-only';
END;
$$;
CREATE TRIGGER {{PricingPolicyVersion.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{PricingPolicyVersion}} FOR EACH ROW EXECUTE FUNCTION {{PricingPolicyVersion.FnGuard}}();
CREATE TRIGGER {{PricingPolicyVersion.TrTruncate}} BEFORE TRUNCATE ON {{PricingPolicyVersion}} FOR EACH STATEMENT EXECUTE FUNCTION {{PricingPolicyVersion.FnGuard}}();
REVOKE ALL ON TABLE {{PricingPolicyVersion}} FROM PUBLIC;

CREATE TABLE {{PriceList}} (
    {{PriceList.Id}} uuid NOT NULL,
    {{PriceList.Code}} varchar(30) NOT NULL,
    {{PriceList.NameAr}} varchar(200) NOT NULL,
    {{PriceList.NameEn}} varchar(200) NULL,
    {{PriceList.Priority}} integer NOT NULL,
    {{PriceList.Currency}} char(3) NOT NULL,
    {{PriceList.ValidFrom}} date NOT NULL,
    {{PriceList.ValidTo}} date NULL,
    {{PriceList.IsActive}} boolean NOT NULL DEFAULT true,
    {{PriceList.CustomerId}} uuid NULL,
    {{PriceList.Revision}} bigint NOT NULL DEFAULT 1,
    {{PriceList.CreatedBy}} uuid NOT NULL,
    {{PriceList.CreatedAtUtc}} timestamptz NOT NULL,
    {{PriceList.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{PriceList.Pk}} PRIMARY KEY ({{PriceList.Id}}),
    CONSTRAINT {{PriceList.FkCustomer}} FOREIGN KEY ({{PriceList.CustomerId}}) REFERENCES \${EXTERNAL.PartyTable} (\${EXTERNAL.PartyId}),
    CONSTRAINT {{PriceList.CkValues}} CHECK ({{PriceList.Revision}} > 0 AND {{PriceList.Code}} ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim({{PriceList.NameAr}})) >= 1
        AND {{PriceList.Priority}} BETWEEN 0 AND 1000000 AND {{PriceList.Currency}} ~ '^[A-Z]{3}$' AND ({{PriceList.ValidTo}} IS NULL OR {{PriceList.ValidTo}} >= {{PriceList.ValidFrom}}))
);
CREATE UNIQUE INDEX {{PriceList.UqCode}} ON {{PriceList}} ({{PriceList.Code}});

CREATE FUNCTION {{PriceList.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{PriceList.Revision}} <> 1 THEN RAISE EXCEPTION 'a price list is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'price lists are never removed'; END IF;
    IF NEW.{{PriceList.Id}} IS DISTINCT FROM OLD.{{PriceList.Id}} OR NEW.{{PriceList.Code}} IS DISTINCT FROM OLD.{{PriceList.Code}} OR NEW.{{PriceList.Currency}} IS DISTINCT FROM OLD.{{PriceList.Currency}}
       OR NEW.{{PriceList.CustomerId}} IS DISTINCT FROM OLD.{{PriceList.CustomerId}}
       OR NEW.{{PriceList.CreatedBy}} IS DISTINCT FROM OLD.{{PriceList.CreatedBy}} OR NEW.{{PriceList.CreatedAtUtc}} IS DISTINCT FROM OLD.{{PriceList.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, code, currency, customer and creation data of a price list are immutable';
    END IF;
    IF NEW.{{PriceList.Revision}} <> OLD.{{PriceList.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{PriceList.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{PriceList}} FOR EACH ROW EXECUTE FUNCTION {{PriceList.FnGuard}}();
CREATE TRIGGER {{PriceList.TrTruncate}} BEFORE TRUNCATE ON {{PriceList}} FOR EACH STATEMENT EXECUTE FUNCTION {{PriceList.FnGuard}}();
REVOKE ALL ON TABLE {{PriceList}} FROM PUBLIC;

CREATE TABLE {{PriceEntry}} (
    {{PriceEntry.Id}} uuid NOT NULL,
    {{PriceEntry.PriceListId}} uuid NOT NULL,
    {{PriceEntry.ItemId}} uuid NULL,
    {{PriceEntry.ItemGroupId}} uuid NULL,
    {{PriceEntry.UnitId}} uuid NOT NULL,
    {{PriceEntry.MinQuantity}} numeric(28,10) NOT NULL,
    {{PriceEntry.UnitPrice}} numeric(28,6) NOT NULL,
    {{PriceEntry.IsActive}} boolean NOT NULL DEFAULT true,
    {{PriceEntry.Revision}} bigint NOT NULL DEFAULT 1,
    {{PriceEntry.CreatedBy}} uuid NOT NULL,
    {{PriceEntry.CreatedAtUtc}} timestamptz NOT NULL,
    {{PriceEntry.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{PriceEntry.Pk}} PRIMARY KEY ({{PriceEntry.Id}}),
    CONSTRAINT {{PriceEntry.FkList}} FOREIGN KEY ({{PriceEntry.PriceListId}}) REFERENCES {{PriceList}} ({{PriceList.Id}}),
    CONSTRAINT {{PriceEntry.FkItem}} FOREIGN KEY ({{PriceEntry.ItemId}}) REFERENCES \${EXTERNAL.ItemTable} (\${EXTERNAL.ItemId}),
    CONSTRAINT {{PriceEntry.FkGroup}} FOREIGN KEY ({{PriceEntry.ItemGroupId}}) REFERENCES \${EXTERNAL.GroupTable} (\${EXTERNAL.GroupId}),
    CONSTRAINT {{PriceEntry.FkUnit}} FOREIGN KEY ({{PriceEntry.UnitId}}) REFERENCES \${EXTERNAL.UnitTable} (\${EXTERNAL.UnitId}),
    CONSTRAINT {{PriceEntry.CkValues}} CHECK ({{PriceEntry.Revision}} > 0 AND (({{PriceEntry.ItemId}} IS NULL) <> ({{PriceEntry.ItemGroupId}} IS NULL))
        AND {{PriceEntry.MinQuantity}} >= 0 AND {{PriceEntry.MinQuantity}} < 1000000000 AND {{PriceEntry.UnitPrice}} >= 0 AND {{PriceEntry.UnitPrice}} <= 1000000000000)
);
CREATE UNIQUE INDEX {{PriceEntry.UqItemTier}} ON {{PriceEntry}} ({{PriceEntry.PriceListId}}, {{PriceEntry.ItemId}}, {{PriceEntry.UnitId}}, {{PriceEntry.MinQuantity}}) WHERE {{PriceEntry.ItemId}} IS NOT NULL;
CREATE UNIQUE INDEX {{PriceEntry.UqGroupTier}} ON {{PriceEntry}} ({{PriceEntry.PriceListId}}, {{PriceEntry.ItemGroupId}}, {{PriceEntry.UnitId}}, {{PriceEntry.MinQuantity}}) WHERE {{PriceEntry.ItemGroupId}} IS NOT NULL;

CREATE FUNCTION {{PriceEntry.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{PriceEntry.Revision}} <> 1 THEN RAISE EXCEPTION 'a price entry is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'price entries are never removed'; END IF;
    IF NEW.{{PriceEntry.Id}} IS DISTINCT FROM OLD.{{PriceEntry.Id}} OR NEW.{{PriceEntry.PriceListId}} IS DISTINCT FROM OLD.{{PriceEntry.PriceListId}}
       OR NEW.{{PriceEntry.ItemId}} IS DISTINCT FROM OLD.{{PriceEntry.ItemId}} OR NEW.{{PriceEntry.ItemGroupId}} IS DISTINCT FROM OLD.{{PriceEntry.ItemGroupId}}
       OR NEW.{{PriceEntry.UnitId}} IS DISTINCT FROM OLD.{{PriceEntry.UnitId}} OR NEW.{{PriceEntry.MinQuantity}} IS DISTINCT FROM OLD.{{PriceEntry.MinQuantity}}
       OR NEW.{{PriceEntry.CreatedBy}} IS DISTINCT FROM OLD.{{PriceEntry.CreatedBy}} OR NEW.{{PriceEntry.CreatedAtUtc}} IS DISTINCT FROM OLD.{{PriceEntry.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, list, scope, unit, tier and creation data of a price entry are immutable';
    END IF;
    IF NEW.{{PriceEntry.Revision}} <> OLD.{{PriceEntry.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{PriceEntry.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{PriceEntry}} FOR EACH ROW EXECUTE FUNCTION {{PriceEntry.FnGuard}}();
CREATE TRIGGER {{PriceEntry.TrTruncate}} BEFORE TRUNCATE ON {{PriceEntry}} FOR EACH STATEMENT EXECUTE FUNCTION {{PriceEntry.FnGuard}}();
REVOKE ALL ON TABLE {{PriceEntry}} FROM PUBLIC;

CREATE TABLE {{DiscountRule}} (
    {{DiscountRule.Id}} uuid NOT NULL,
    {{DiscountRule.Code}} varchar(30) NOT NULL,
    {{DiscountRule.NameAr}} varchar(200) NOT NULL,
    {{DiscountRule.NameEn}} varchar(200) NULL,
    {{DiscountRule.Priority}} integer NOT NULL,
    {{DiscountRule.ValidFrom}} date NOT NULL,
    {{DiscountRule.ValidTo}} date NULL,
    {{DiscountRule.IsActive}} boolean NOT NULL DEFAULT true,
    {{DiscountRule.Percent}} numeric(9,4) NOT NULL,
    {{DiscountRule.MinQuantity}} numeric(28,10) NOT NULL DEFAULT 0,
    {{DiscountRule.CustomerId}} uuid NULL,
    {{DiscountRule.ItemId}} uuid NULL,
    {{DiscountRule.ItemGroupId}} uuid NULL,
    {{DiscountRule.Revision}} bigint NOT NULL DEFAULT 1,
    {{DiscountRule.CreatedBy}} uuid NOT NULL,
    {{DiscountRule.CreatedAtUtc}} timestamptz NOT NULL,
    {{DiscountRule.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{DiscountRule.Pk}} PRIMARY KEY ({{DiscountRule.Id}}),
    CONSTRAINT {{DiscountRule.FkCustomer}} FOREIGN KEY ({{DiscountRule.CustomerId}}) REFERENCES \${EXTERNAL.PartyTable} (\${EXTERNAL.PartyId}),
    CONSTRAINT {{DiscountRule.FkItem}} FOREIGN KEY ({{DiscountRule.ItemId}}) REFERENCES \${EXTERNAL.ItemTable} (\${EXTERNAL.ItemId}),
    CONSTRAINT {{DiscountRule.FkGroup}} FOREIGN KEY ({{DiscountRule.ItemGroupId}}) REFERENCES \${EXTERNAL.GroupTable} (\${EXTERNAL.GroupId}),
    CONSTRAINT {{DiscountRule.CkValues}} CHECK ({{DiscountRule.Revision}} > 0 AND {{DiscountRule.Code}} ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim({{DiscountRule.NameAr}})) >= 1
        AND {{DiscountRule.Priority}} BETWEEN 0 AND 1000000 AND ({{DiscountRule.ValidTo}} IS NULL OR {{DiscountRule.ValidTo}} >= {{DiscountRule.ValidFrom}})
        AND {{DiscountRule.Percent}} >= 0 AND {{DiscountRule.Percent}} <= 100 AND {{DiscountRule.MinQuantity}} >= 0 AND {{DiscountRule.MinQuantity}} < 1000000000
        AND NOT ({{DiscountRule.ItemId}} IS NOT NULL AND {{DiscountRule.ItemGroupId}} IS NOT NULL))
);
CREATE UNIQUE INDEX {{DiscountRule.UqCode}} ON {{DiscountRule}} ({{DiscountRule.Code}});
-- two ACTIVE rules of exactly the same scope, priority, minimum quantity and start can never both apply cleanly (the engine refuses such a tie as ambiguous), so PostgreSQL refuses to hold them: one badly
-- configured pair can never block pricing. (NULL scope columns are compared as the all-zero uuid.)
CREATE UNIQUE INDEX {{DiscountRule.UqActiveKey}} ON {{DiscountRule}} (COALESCE({{DiscountRule.CustomerId}}, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE({{DiscountRule.ItemId}}, '00000000-0000-0000-0000-000000000000'::uuid),
    COALESCE({{DiscountRule.ItemGroupId}}, '00000000-0000-0000-0000-000000000000'::uuid), {{DiscountRule.Priority}}, {{DiscountRule.MinQuantity}}, {{DiscountRule.ValidFrom}}) WHERE {{DiscountRule.IsActive}};

CREATE FUNCTION {{DiscountRule.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{DiscountRule.Revision}} <> 1 THEN RAISE EXCEPTION 'a discount rule is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'discount rules are never removed'; END IF;
    IF NEW.{{DiscountRule.Id}} IS DISTINCT FROM OLD.{{DiscountRule.Id}} OR NEW.{{DiscountRule.Code}} IS DISTINCT FROM OLD.{{DiscountRule.Code}}
       OR NEW.{{DiscountRule.CustomerId}} IS DISTINCT FROM OLD.{{DiscountRule.CustomerId}} OR NEW.{{DiscountRule.ItemId}} IS DISTINCT FROM OLD.{{DiscountRule.ItemId}}
       OR NEW.{{DiscountRule.ItemGroupId}} IS DISTINCT FROM OLD.{{DiscountRule.ItemGroupId}}
       OR NEW.{{DiscountRule.CreatedBy}} IS DISTINCT FROM OLD.{{DiscountRule.CreatedBy}} OR NEW.{{DiscountRule.CreatedAtUtc}} IS DISTINCT FROM OLD.{{DiscountRule.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, code, scope and creation data of a discount rule are immutable';
    END IF;
    IF NEW.{{DiscountRule.Revision}} <> OLD.{{DiscountRule.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{DiscountRule.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{DiscountRule}} FOR EACH ROW EXECUTE FUNCTION {{DiscountRule.FnGuard}}();
CREATE TRIGGER {{DiscountRule.TrTruncate}} BEFORE TRUNCATE ON {{DiscountRule}} FOR EACH STATEMENT EXECUTE FUNCTION {{DiscountRule.FnGuard}}();
REVOKE ALL ON TABLE {{DiscountRule}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0026.pricing.v1:" + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (/0027_company_customer_groups\.sql$|CompanySchema\.CustomerGroups\.cs$/.test(p)) continue;   // later migration: it REFERENCES the names of this one
      if (!/\.(sql|cs)$/.test(e.name)) continue;
      for (const m of fs.readFileSync(p, "utf8").matchAll(/\b[tcfkr]_[0-9a-f]{8}\b/g)) names.add(m[0]);
    }
  };
  scan(path.join(infra, "CompanyDatabase")); scan(path.join(infra, "SystemDatabase"));
  return names;
}

function generate() {
  const body = TEMPLATE.replace(/\$\{EXTERNAL\.(\w+)\}/g, (_, k) => EXTERNAL[k]);
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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0026-pricing.cjs for company migration 0026 (Phase 3.11 slice 2b, pricing configuration). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> PricingMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated pricing schema differs from the committed files"); process.exit(1); }
  console.log("pricing schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
