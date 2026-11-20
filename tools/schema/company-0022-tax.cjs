#!/usr/bin/env node
// Deterministic generator for company migration 0022 (Phase 3.10 tax engine persistence).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{TaxDefinition.Code}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0022.tax.v1:" + token)
// so the names are reproducible from this file alone, unique (a collision with a name already in the repository or inside this migration aborts the run, nothing is re-hashed
// silently) and fixed once committed (changing one needs a new migration). Prefix by the last segment of the token: table (no dot) t_, Fn* f_, Tr* r_, Pk/Uq*/Fk*/Ck*/Ix* k_, otherwise c_.
// Usage:  node tools/schema/company-0022-tax.cjs --write   writes the migration and the C# dictionary
//         node tools/schema/company-0022-tax.cjs --check   fails when the committed files differ from the generated ones (run by CI-style checks and by the unit tests' docs)
// Referenced existing names (item and local party) are given as literals in EXTERNAL below, so the migration never depends on a generated value of another migration.
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0022_company_tax_engine.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.Tax.cs");

const EXTERNAL = { ItemTable: "t_2f2a7184", ItemId: "c_be51f22e", PartyTable: "t_8edce6b0", PartyId: "c_3f494703" };

const TEMPLATE = `-- Migration 0022: tax engine configuration (Phase 3.10 slice 2b). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0022-tax.cjs (logical dictionary: CompanySchema.Tax.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 6.1-6.3): the company's taxes, their DATED rate versions, item and party classifications and their assignments, the rules that say which taxes apply to which operation of which
-- document type, and the versioned rounding policy. It is CONFIGURATION: no document, amount or journal exists here. A final document stores an immutable snapshot of what applied (TaxSnapshots),
-- so a later change of any row below can never alter a document that already exists.
-- Invariants enforced by PostgreSQL:
--   * taxes, classes and rules are never removed; code/kind/identity and creation data never change; every change advances the revision by one;
--   * a rate version is append-only (no update, delete or truncate) and is strictly later than every earlier version of its tax; its treatment/rate are consistent (only Standard has a rate);
--   * a rounding-policy version is append-only; an item is assigned an item class and a party a party class (never the other kind);
--   * a rule names taxes and classes of the company only; its dates, priority and treatment override are within range.
-- tax class kind: 1 item, 2 party. treatment: 1 standard, 2 zero rated, 3 exempt, 4 not applicable. override: 2, 3 or 4 (null = the dated rate). rounding mode: 1 half away from zero, 2 half even; level: 1 per line, 2 per document.

CREATE TABLE {{TaxDefinition}} (
    {{TaxDefinition.Id}} uuid NOT NULL,
    {{TaxDefinition.Code}} varchar(30) NOT NULL,
    {{TaxDefinition.NameAr}} varchar(200) NOT NULL,
    {{TaxDefinition.NameEn}} varchar(200) NULL,
    {{TaxDefinition.IsActive}} boolean NOT NULL DEFAULT true,
    {{TaxDefinition.Revision}} bigint NOT NULL DEFAULT 1,
    {{TaxDefinition.CreatedBy}} uuid NOT NULL,
    {{TaxDefinition.CreatedAtUtc}} timestamptz NOT NULL,
    {{TaxDefinition.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{TaxDefinition.Pk}} PRIMARY KEY ({{TaxDefinition.Id}}),
    CONSTRAINT {{TaxDefinition.CkValues}} CHECK ({{TaxDefinition.Revision}} > 0 AND {{TaxDefinition.Code}} ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim({{TaxDefinition.NameAr}})) >= 1)
);
CREATE UNIQUE INDEX {{TaxDefinition.UqCode}} ON {{TaxDefinition}} ({{TaxDefinition.Code}});

CREATE FUNCTION {{TaxDefinition.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{TaxDefinition.Revision}} <> 1 THEN RAISE EXCEPTION 'a tax is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'taxes are never removed'; END IF;
    IF NEW.{{TaxDefinition.Id}} IS DISTINCT FROM OLD.{{TaxDefinition.Id}} OR NEW.{{TaxDefinition.Code}} IS DISTINCT FROM OLD.{{TaxDefinition.Code}}
       OR NEW.{{TaxDefinition.CreatedBy}} IS DISTINCT FROM OLD.{{TaxDefinition.CreatedBy}} OR NEW.{{TaxDefinition.CreatedAtUtc}} IS DISTINCT FROM OLD.{{TaxDefinition.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, code and creation data of a tax are immutable';
    END IF;
    IF NEW.{{TaxDefinition.Revision}} <> OLD.{{TaxDefinition.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{TaxDefinition.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{TaxDefinition}} FOR EACH ROW EXECUTE FUNCTION {{TaxDefinition.FnGuard}}();
CREATE TRIGGER {{TaxDefinition.TrTruncate}} BEFORE TRUNCATE ON {{TaxDefinition}} FOR EACH STATEMENT EXECUTE FUNCTION {{TaxDefinition.FnGuard}}();
REVOKE ALL ON TABLE {{TaxDefinition}} FROM PUBLIC;

CREATE TABLE {{TaxRateVersion}} (
    {{TaxRateVersion.Id}} uuid NOT NULL,
    {{TaxRateVersion.TaxId}} uuid NOT NULL,
    {{TaxRateVersion.EffectiveFrom}} date NOT NULL,
    {{TaxRateVersion.Treatment}} smallint NOT NULL,
    {{TaxRateVersion.RatePercent}} numeric(10,6) NOT NULL,
    {{TaxRateVersion.CreatedBy}} uuid NOT NULL,
    {{TaxRateVersion.CreatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{TaxRateVersion.Pk}} PRIMARY KEY ({{TaxRateVersion.Id}}),
    CONSTRAINT {{TaxRateVersion.FkTax}} FOREIGN KEY ({{TaxRateVersion.TaxId}}) REFERENCES {{TaxDefinition}} ({{TaxDefinition.Id}}),
    CONSTRAINT {{TaxRateVersion.CkValues}} CHECK ({{TaxRateVersion.Treatment}} IN (1, 2, 3, 4) AND {{TaxRateVersion.RatePercent}} BETWEEN 0 AND 1000
        AND ({{TaxRateVersion.Treatment}} = 1 OR {{TaxRateVersion.RatePercent}} = 0))
);
CREATE UNIQUE INDEX {{TaxRateVersion.UqVersion}} ON {{TaxRateVersion}} ({{TaxRateVersion.TaxId}}, {{TaxRateVersion.EffectiveFrom}});

CREATE FUNCTION {{TaxRateVersion.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'tax rate versions are append-only'; END IF;
    IF EXISTS (SELECT 1 FROM {{TaxRateVersion}} WHERE {{TaxRateVersion.TaxId}} = NEW.{{TaxRateVersion.TaxId}} AND {{TaxRateVersion.EffectiveFrom}} >= NEW.{{TaxRateVersion.EffectiveFrom}}) THEN
        RAISE EXCEPTION 'a tax rate version must be later than every earlier version of its tax';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{TaxRateVersion.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{TaxRateVersion}} FOR EACH ROW EXECUTE FUNCTION {{TaxRateVersion.FnGuard}}();
CREATE TRIGGER {{TaxRateVersion.TrTruncate}} BEFORE TRUNCATE ON {{TaxRateVersion}} FOR EACH STATEMENT EXECUTE FUNCTION {{TaxRateVersion.FnGuard}}();
REVOKE ALL ON TABLE {{TaxRateVersion}} FROM PUBLIC;

CREATE TABLE {{TaxClass}} (
    {{TaxClass.Id}} uuid NOT NULL,
    {{TaxClass.Kind}} smallint NOT NULL,
    {{TaxClass.Code}} varchar(30) NOT NULL,
    {{TaxClass.NameAr}} varchar(200) NOT NULL,
    {{TaxClass.NameEn}} varchar(200) NULL,
    {{TaxClass.IsActive}} boolean NOT NULL DEFAULT true,
    {{TaxClass.Revision}} bigint NOT NULL DEFAULT 1,
    {{TaxClass.CreatedBy}} uuid NOT NULL,
    {{TaxClass.CreatedAtUtc}} timestamptz NOT NULL,
    {{TaxClass.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{TaxClass.Pk}} PRIMARY KEY ({{TaxClass.Id}}),
    CONSTRAINT {{TaxClass.CkValues}} CHECK ({{TaxClass.Kind}} IN (1, 2) AND {{TaxClass.Revision}} > 0 AND {{TaxClass.Code}} ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim({{TaxClass.NameAr}})) >= 1)
);
CREATE UNIQUE INDEX {{TaxClass.UqKindId}} ON {{TaxClass}} ({{TaxClass.Id}}, {{TaxClass.Kind}});
CREATE UNIQUE INDEX {{TaxClass.UqCode}} ON {{TaxClass}} ({{TaxClass.Kind}}, {{TaxClass.Code}});

CREATE FUNCTION {{TaxClass.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{TaxClass.Revision}} <> 1 THEN RAISE EXCEPTION 'a tax class is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'tax classes are never removed'; END IF;
    IF NEW.{{TaxClass.Id}} IS DISTINCT FROM OLD.{{TaxClass.Id}} OR NEW.{{TaxClass.Kind}} IS DISTINCT FROM OLD.{{TaxClass.Kind}} OR NEW.{{TaxClass.Code}} IS DISTINCT FROM OLD.{{TaxClass.Code}}
       OR NEW.{{TaxClass.CreatedBy}} IS DISTINCT FROM OLD.{{TaxClass.CreatedBy}} OR NEW.{{TaxClass.CreatedAtUtc}} IS DISTINCT FROM OLD.{{TaxClass.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, kind, code and creation data of a tax class are immutable';
    END IF;
    IF NEW.{{TaxClass.Revision}} <> OLD.{{TaxClass.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{TaxClass.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{TaxClass}} FOR EACH ROW EXECUTE FUNCTION {{TaxClass.FnGuard}}();
CREATE TRIGGER {{TaxClass.TrTruncate}} BEFORE TRUNCATE ON {{TaxClass}} FOR EACH STATEMENT EXECUTE FUNCTION {{TaxClass.FnGuard}}();
REVOKE ALL ON TABLE {{TaxClass}} FROM PUBLIC;

CREATE TABLE {{TaxItemClass}} (
    {{TaxItemClass.ItemId}} uuid NOT NULL,
    {{TaxItemClass.ClassId}} uuid NOT NULL,
    {{TaxItemClass.ClassKind}} smallint NOT NULL DEFAULT 1,
    {{TaxItemClass.AssignedBy}} uuid NOT NULL,
    {{TaxItemClass.AssignedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{TaxItemClass.Pk}} PRIMARY KEY ({{TaxItemClass.ItemId}}),
    CONSTRAINT {{TaxItemClass.FkItem}} FOREIGN KEY ({{TaxItemClass.ItemId}}) REFERENCES ${EXTERNAL.ItemTable} (${EXTERNAL.ItemId}),
    CONSTRAINT {{TaxItemClass.FkClass}} FOREIGN KEY ({{TaxItemClass.ClassId}}, {{TaxItemClass.ClassKind}}) REFERENCES {{TaxClass}} ({{TaxClass.Id}}, {{TaxClass.Kind}}),
    CONSTRAINT {{TaxItemClass.CkKind}} CHECK ({{TaxItemClass.ClassKind}} = 1)
);
REVOKE ALL ON TABLE {{TaxItemClass}} FROM PUBLIC;

CREATE TABLE {{TaxPartyClass}} (
    {{TaxPartyClass.PartyId}} uuid NOT NULL,
    {{TaxPartyClass.ClassId}} uuid NOT NULL,
    {{TaxPartyClass.ClassKind}} smallint NOT NULL DEFAULT 2,
    {{TaxPartyClass.AssignedBy}} uuid NOT NULL,
    {{TaxPartyClass.AssignedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{TaxPartyClass.Pk}} PRIMARY KEY ({{TaxPartyClass.PartyId}}),
    CONSTRAINT {{TaxPartyClass.FkParty}} FOREIGN KEY ({{TaxPartyClass.PartyId}}) REFERENCES ${EXTERNAL.PartyTable} (${EXTERNAL.PartyId}),
    CONSTRAINT {{TaxPartyClass.FkClass}} FOREIGN KEY ({{TaxPartyClass.ClassId}}, {{TaxPartyClass.ClassKind}}) REFERENCES {{TaxClass}} ({{TaxClass.Id}}, {{TaxClass.Kind}}),
    CONSTRAINT {{TaxPartyClass.CkKind}} CHECK ({{TaxPartyClass.ClassKind}} = 2)
);
REVOKE ALL ON TABLE {{TaxPartyClass}} FROM PUBLIC;

CREATE TABLE {{TaxRule}} (
    {{TaxRule.Id}} uuid NOT NULL,
    {{TaxRule.DocumentType}} varchar(40) NOT NULL,
    {{TaxRule.Operation}} varchar(40) NOT NULL,
    {{TaxRule.ItemClassId}} uuid NULL,
    {{TaxRule.ItemClassKind}} smallint GENERATED ALWAYS AS (CASE WHEN {{TaxRule.ItemClassId}} IS NULL THEN NULL ELSE 1::smallint END) STORED,
    {{TaxRule.PartyClassId}} uuid NULL,
    {{TaxRule.PartyClassKind}} smallint GENERATED ALWAYS AS (CASE WHEN {{TaxRule.PartyClassId}} IS NULL THEN NULL ELSE 2::smallint END) STORED,
    {{TaxRule.TaxId}} uuid NOT NULL,
    {{TaxRule.Priority}} integer NOT NULL,
    {{TaxRule.ValidFrom}} date NOT NULL,
    {{TaxRule.ValidTo}} date NULL,
    {{TaxRule.IsActive}} boolean NOT NULL DEFAULT true,
    {{TaxRule.TreatmentOverride}} smallint NULL,
    {{TaxRule.Revision}} bigint NOT NULL DEFAULT 1,
    {{TaxRule.CreatedBy}} uuid NOT NULL,
    {{TaxRule.CreatedAtUtc}} timestamptz NOT NULL,
    {{TaxRule.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{TaxRule.Pk}} PRIMARY KEY ({{TaxRule.Id}}),
    CONSTRAINT {{TaxRule.FkTax}} FOREIGN KEY ({{TaxRule.TaxId}}) REFERENCES {{TaxDefinition}} ({{TaxDefinition.Id}}),
    CONSTRAINT {{TaxRule.FkItemClass}} FOREIGN KEY ({{TaxRule.ItemClassId}}, {{TaxRule.ItemClassKind}}) REFERENCES {{TaxClass}} ({{TaxClass.Id}}, {{TaxClass.Kind}}),
    CONSTRAINT {{TaxRule.FkPartyClass}} FOREIGN KEY ({{TaxRule.PartyClassId}}, {{TaxRule.PartyClassKind}}) REFERENCES {{TaxClass}} ({{TaxClass.Id}}, {{TaxClass.Kind}}),
    CONSTRAINT {{TaxRule.CkValues}} CHECK ({{TaxRule.Revision}} > 0 AND {{TaxRule.Priority}} BETWEEN 1 AND 1000 AND ({{TaxRule.ValidTo}} IS NULL OR {{TaxRule.ValidTo}} >= {{TaxRule.ValidFrom}})
        AND ({{TaxRule.TreatmentOverride}} IS NULL OR {{TaxRule.TreatmentOverride}} IN (2, 3, 4))
        AND {{TaxRule.DocumentType}} ~ '^[a-z][a-z0-9_.-]{0,39}$' AND {{TaxRule.Operation}} ~ '^[a-z][a-z0-9_.-]{0,39}$')
);
CREATE INDEX {{TaxRule.IxLookup}} ON {{TaxRule}} ({{TaxRule.DocumentType}}, {{TaxRule.Operation}});

CREATE FUNCTION {{TaxRule.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{TaxRule.Revision}} <> 1 THEN RAISE EXCEPTION 'a tax rule is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'tax rules are never removed'; END IF;
    IF NEW.{{TaxRule.Id}} IS DISTINCT FROM OLD.{{TaxRule.Id}} OR NEW.{{TaxRule.CreatedBy}} IS DISTINCT FROM OLD.{{TaxRule.CreatedBy}} OR NEW.{{TaxRule.CreatedAtUtc}} IS DISTINCT FROM OLD.{{TaxRule.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity and creation data of a tax rule are immutable';
    END IF;
    IF NEW.{{TaxRule.Revision}} <> OLD.{{TaxRule.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{TaxRule.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{TaxRule}} FOR EACH ROW EXECUTE FUNCTION {{TaxRule.FnGuard}}();
CREATE TRIGGER {{TaxRule.TrTruncate}} BEFORE TRUNCATE ON {{TaxRule}} FOR EACH STATEMENT EXECUTE FUNCTION {{TaxRule.FnGuard}}();
REVOKE ALL ON TABLE {{TaxRule}} FROM PUBLIC;

CREATE TABLE {{TaxRuleBase}} (
    {{TaxRuleBase.RuleId}} uuid NOT NULL,
    {{TaxRuleBase.IncludedTaxId}} uuid NOT NULL,
    CONSTRAINT {{TaxRuleBase.Pk}} PRIMARY KEY ({{TaxRuleBase.RuleId}}, {{TaxRuleBase.IncludedTaxId}}),
    CONSTRAINT {{TaxRuleBase.FkRule}} FOREIGN KEY ({{TaxRuleBase.RuleId}}) REFERENCES {{TaxRule}} ({{TaxRule.Id}}),
    CONSTRAINT {{TaxRuleBase.FkTax}} FOREIGN KEY ({{TaxRuleBase.IncludedTaxId}}) REFERENCES {{TaxDefinition}} ({{TaxDefinition.Id}})
);
REVOKE ALL ON TABLE {{TaxRuleBase}} FROM PUBLIC;

CREATE TABLE {{TaxPolicyVersion}} (
    {{TaxPolicyVersion.Id}} uuid NOT NULL,
    {{TaxPolicyVersion.Version}} integer NOT NULL,
    {{TaxPolicyVersion.MoneyDecimals}} smallint NOT NULL,
    {{TaxPolicyVersion.Mode}} smallint NOT NULL,
    {{TaxPolicyVersion.Level}} smallint NOT NULL,
    {{TaxPolicyVersion.CreatedBy}} uuid NOT NULL,
    {{TaxPolicyVersion.CreatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{TaxPolicyVersion.Pk}} PRIMARY KEY ({{TaxPolicyVersion.Id}}),
    CONSTRAINT {{TaxPolicyVersion.CkValues}} CHECK ({{TaxPolicyVersion.Version}} > 0 AND {{TaxPolicyVersion.MoneyDecimals}} BETWEEN 0 AND 6 AND {{TaxPolicyVersion.Mode}} IN (1, 2) AND {{TaxPolicyVersion.Level}} IN (1, 2))
);
CREATE UNIQUE INDEX {{TaxPolicyVersion.UqVersion}} ON {{TaxPolicyVersion}} ({{TaxPolicyVersion.Version}});

CREATE FUNCTION {{TaxPolicyVersion.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'tax policy versions are append-only'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{TaxPolicyVersion.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{TaxPolicyVersion}} FOR EACH ROW EXECUTE FUNCTION {{TaxPolicyVersion.FnGuard}}();
CREATE TRIGGER {{TaxPolicyVersion.TrTruncate}} BEFORE TRUNCATE ON {{TaxPolicyVersion}} FOR EACH STATEMENT EXECUTE FUNCTION {{TaxPolicyVersion.FnGuard}}();
REVOKE ALL ON TABLE {{TaxPolicyVersion}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0022.tax.v1:" + token).digest("hex").slice(0, 8);

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
  const tokens = [...new Set([...TEMPLATE.matchAll(/\{\{([A-Za-z0-9.]+)\}\}/g)].map((m) => m[1]))];
  const taken = existingNames();
  const byToken = new Map(), seen = new Map();
  for (const t of tokens) {
    const name = physical(t);
    if (taken.has(name)) throw new Error(`collision with an existing name: ${t} -> ${name}`);
    if (seen.has(name)) throw new Error(`collision inside the migration: ${t} and ${seen.get(name)} -> ${name}`);
    seen.set(name, t); byToken.set(t, name);
  }
  const sql = TEMPLATE.replace(/\{\{([A-Za-z0-9.]+)\}\}/g, (_, t) => byToken.get(t)).replace(/\$(t_|c_)([0-9a-f]{8})/g, "$1$2");
  const entities = [...new Set(tokens.map((t) => t.split(".")[0]))];
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0022-tax.cjs for company migration 0022 (Phase 3.10 slice 2b, tax engine configuration). Do not edit: re-run the generator (--write) or verify it (--check).\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> TaxMap { get; } = new Dictionary<string, string>\n    {\n";
  for (const t of tokens) cs += `        ["${t}"] = ${t.includes(".") ? t : t + ".Table"},\n`;
  cs += "    };\n}\n";
  return { sql: sql.replace(/\n/g, "\r\n").replace(/\r\n/g, "\n"), cs };
}

const { sql, cs } = generate();
const mode = process.argv[2];
if (mode === "--write") { fs.writeFileSync(outSql, sql); fs.writeFileSync(outCs, cs); console.log("written"); }
else if (mode === "--check") {
  const norm = (s) => s.replace(/\r\n/g, "\n");
  const ok = fs.existsSync(outSql) && fs.existsSync(outCs) && norm(fs.readFileSync(outSql, "utf8")) === sql && norm(fs.readFileSync(outCs, "utf8")) === cs;
  if (!ok) { console.error("generated tax schema differs from the committed files"); process.exit(1); }
  console.log("tax schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
