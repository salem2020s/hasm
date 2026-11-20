#!/usr/bin/env node
// Deterministic generator for company migration 0023 (Phase 3.9B customers and suppliers: roles, terms, credit-limit data, control accounts, sales representatives).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{PartyRole.Role}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0023.partyroles.v1:" + token)
// so the names are reproducible from this file alone, unique (a collision with a name already in the repository or inside this migration aborts the run, nothing is re-hashed
// silently) and fixed once committed (changing one needs a new migration). Prefix by the last segment of the token: table (no dot) t_, Fn* f_, Tr* r_, Pk/Uq*/Fk*/Ck*/Ix* k_, otherwise c_.
// Usage:  node tools/schema/company-0023-party-roles.cjs --write   writes the migration and the C# dictionary
//         node tools/schema/company-0023-party-roles.cjs --check   fails when the committed files differ from the generated ones (run by CI and the unit tests)
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0023_company_party_roles.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.PartyRoles.cs");

// Existing names referenced by literal: the local party (0021) and the chart account (0006) tables.
const EXTERNAL = { PartyTable: "t_8edce6b0", PartyId: "c_3f494703", AccTable: "t_11d107a9", AccId: "c_29a86598", AccType: "c_0caeac6a", AccIsGroup: "c_57d34c8a", AccIsActive: "c_9761052f" };

const TEMPLATE = `-- Migration 0023: customers and suppliers (Phase 3.9B). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0023-party-roles.cjs (logical dictionary: CompanySchema.PartyRoles.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 7.1, 10.1): a company-local ROLE of a local party (customer, supplier or both), its commercial terms (payment mode and days), credit-limit DATA, the control account
-- (receivables / payables) it posts to, and the company's sales representatives (OD-3 default: a company-local record with an optional user link and no commission calculation).
-- It is CONFIGURATION: no document, balance or journal exists here. The shared identity of the party stays in the system database and carries none of this.
-- Invariants enforced by PostgreSQL:
--   * roles and representatives are never removed (deactivated); identity, party, role and creation data never change; every change advances the revision by one;
--   * a party has at most one role of each kind; a role is customer (1) or supplier (2); only a customer has a representative;
--   * the control account of a customer is an active, postable ASSET account and that of a supplier an active, postable LIABILITY account (checked on every write that sets it);
--   * the credit limit is a non-negative amount in the company base currency (null = none recorded); payment terms are within range.
-- payment mode: 1 cash, 2 credit, 3 mixed.

CREATE TABLE {{SalesRepresentative}} (
    {{SalesRepresentative.Id}} uuid NOT NULL,
    {{SalesRepresentative.Code}} varchar(30) NOT NULL,
    {{SalesRepresentative.NameAr}} varchar(200) NOT NULL,
    {{SalesRepresentative.NameEn}} varchar(200) NULL,
    {{SalesRepresentative.UserId}} uuid NULL,
    {{SalesRepresentative.IsActive}} boolean NOT NULL DEFAULT true,
    {{SalesRepresentative.Revision}} bigint NOT NULL DEFAULT 1,
    {{SalesRepresentative.CreatedBy}} uuid NOT NULL,
    {{SalesRepresentative.CreatedAtUtc}} timestamptz NOT NULL,
    {{SalesRepresentative.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{SalesRepresentative.Pk}} PRIMARY KEY ({{SalesRepresentative.Id}}),
    CONSTRAINT {{SalesRepresentative.CkValues}} CHECK ({{SalesRepresentative.Revision}} > 0 AND {{SalesRepresentative.Code}} ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim({{SalesRepresentative.NameAr}})) >= 1)
);
CREATE UNIQUE INDEX {{SalesRepresentative.UqCode}} ON {{SalesRepresentative}} ({{SalesRepresentative.Code}});
CREATE UNIQUE INDEX {{SalesRepresentative.UqUser}} ON {{SalesRepresentative}} ({{SalesRepresentative.UserId}}) WHERE {{SalesRepresentative.UserId}} IS NOT NULL;

CREATE FUNCTION {{SalesRepresentative.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{SalesRepresentative.Revision}} <> 1 THEN RAISE EXCEPTION 'a sales representative is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'sales representatives are never removed'; END IF;
    IF NEW.{{SalesRepresentative.Id}} IS DISTINCT FROM OLD.{{SalesRepresentative.Id}} OR NEW.{{SalesRepresentative.Code}} IS DISTINCT FROM OLD.{{SalesRepresentative.Code}}
       OR NEW.{{SalesRepresentative.CreatedBy}} IS DISTINCT FROM OLD.{{SalesRepresentative.CreatedBy}} OR NEW.{{SalesRepresentative.CreatedAtUtc}} IS DISTINCT FROM OLD.{{SalesRepresentative.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, code and creation data of a sales representative are immutable';
    END IF;
    IF NEW.{{SalesRepresentative.Revision}} <> OLD.{{SalesRepresentative.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{SalesRepresentative.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{SalesRepresentative}} FOR EACH ROW EXECUTE FUNCTION {{SalesRepresentative.FnGuard}}();
CREATE TRIGGER {{SalesRepresentative.TrTruncate}} BEFORE TRUNCATE ON {{SalesRepresentative}} FOR EACH STATEMENT EXECUTE FUNCTION {{SalesRepresentative.FnGuard}}();
REVOKE ALL ON TABLE {{SalesRepresentative}} FROM PUBLIC;

CREATE TABLE {{PartyRole}} (
    {{PartyRole.Id}} uuid NOT NULL,
    {{PartyRole.PartyId}} uuid NOT NULL,
    {{PartyRole.Role}} smallint NOT NULL,
    {{PartyRole.IsActive}} boolean NOT NULL DEFAULT true,
    {{PartyRole.PaymentMode}} smallint NOT NULL,
    {{PartyRole.TermDays}} integer NOT NULL DEFAULT 0,
    {{PartyRole.CreditLimit}} numeric(20,4) NULL,
    {{PartyRole.ControlAccountId}} uuid NULL,
    {{PartyRole.RepresentativeId}} uuid NULL,
    {{PartyRole.Revision}} bigint NOT NULL DEFAULT 1,
    {{PartyRole.CreatedBy}} uuid NOT NULL,
    {{PartyRole.CreatedAtUtc}} timestamptz NOT NULL,
    {{PartyRole.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{PartyRole.Pk}} PRIMARY KEY ({{PartyRole.Id}}),
    CONSTRAINT {{PartyRole.FkParty}} FOREIGN KEY ({{PartyRole.PartyId}}) REFERENCES ${EXTERNAL.PartyTable} (${EXTERNAL.PartyId}),
    CONSTRAINT {{PartyRole.FkAccount}} FOREIGN KEY ({{PartyRole.ControlAccountId}}) REFERENCES ${EXTERNAL.AccTable} (${EXTERNAL.AccId}),
    CONSTRAINT {{PartyRole.FkRepresentative}} FOREIGN KEY ({{PartyRole.RepresentativeId}}) REFERENCES {{SalesRepresentative}} ({{SalesRepresentative.Id}}),
    CONSTRAINT {{PartyRole.CkValues}} CHECK ({{PartyRole.Revision}} > 0 AND {{PartyRole.Role}} IN (1, 2) AND {{PartyRole.PaymentMode}} IN (1, 2, 3) AND {{PartyRole.TermDays}} BETWEEN 0 AND 3650
        AND ({{PartyRole.CreditLimit}} IS NULL OR {{PartyRole.CreditLimit}} >= 0) AND ({{PartyRole.RepresentativeId}} IS NULL OR {{PartyRole.Role}} = 1))
);
CREATE UNIQUE INDEX {{PartyRole.UqPartyRole}} ON {{PartyRole}} ({{PartyRole.PartyId}}, {{PartyRole.Role}});
CREATE INDEX {{PartyRole.IxAccount}} ON {{PartyRole}} ({{PartyRole.ControlAccountId}}) WHERE {{PartyRole.ControlAccountId}} IS NOT NULL;

CREATE FUNCTION {{PartyRole.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE acc_type smallint; acc_group boolean; acc_active boolean;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{PartyRole.Revision}} <> 1 THEN RAISE EXCEPTION 'a party role is created at revision 1'; END IF;
    ELSIF TG_OP = 'UPDATE' THEN
        IF NEW.{{PartyRole.Id}} IS DISTINCT FROM OLD.{{PartyRole.Id}} OR NEW.{{PartyRole.PartyId}} IS DISTINCT FROM OLD.{{PartyRole.PartyId}} OR NEW.{{PartyRole.Role}} IS DISTINCT FROM OLD.{{PartyRole.Role}}
           OR NEW.{{PartyRole.CreatedBy}} IS DISTINCT FROM OLD.{{PartyRole.CreatedBy}} OR NEW.{{PartyRole.CreatedAtUtc}} IS DISTINCT FROM OLD.{{PartyRole.CreatedAtUtc}} THEN
            RAISE EXCEPTION 'the identity, party, kind and creation data of a party role are immutable';
        END IF;
        IF NEW.{{PartyRole.Revision}} <> OLD.{{PartyRole.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    ELSE
        RAISE EXCEPTION 'party roles are never removed';
    END IF;
    -- the control account must suit the kind of role whenever it is set or changed
    IF NEW.{{PartyRole.ControlAccountId}} IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.{{PartyRole.ControlAccountId}} IS DISTINCT FROM OLD.{{PartyRole.ControlAccountId}}) THEN
        SELECT a.${EXTERNAL.AccType}, a.${EXTERNAL.AccIsGroup}, a.${EXTERNAL.AccIsActive} INTO acc_type, acc_group, acc_active FROM ${EXTERNAL.AccTable} a WHERE a.${EXTERNAL.AccId} = NEW.{{PartyRole.ControlAccountId}};
        IF acc_group OR NOT acc_active THEN RAISE EXCEPTION 'the control account must be an active, postable account'; END IF;
        IF NEW.{{PartyRole.Role}} = 1 AND acc_type <> 1 THEN RAISE EXCEPTION 'the control account of a customer must be an asset account'; END IF;
        IF NEW.{{PartyRole.Role}} = 2 AND acc_type <> 2 THEN RAISE EXCEPTION 'the control account of a supplier must be a liability account'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{PartyRole.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{PartyRole}} FOR EACH ROW EXECUTE FUNCTION {{PartyRole.FnGuard}}();
CREATE TRIGGER {{PartyRole.TrTruncate}} BEFORE TRUNCATE ON {{PartyRole}} FOR EACH STATEMENT EXECUTE FUNCTION {{PartyRole.FnGuard}}();
REVOKE ALL ON TABLE {{PartyRole}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0023.partyroles.v1:" + token).digest("hex").slice(0, 8);

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
  const sql = TEMPLATE.replace(/\{\{([A-Za-z0-9.]+)\}\}/g, (_, t) => byToken.get(t));
  const entities = [...new Set(tokens.map((t) => t.split(".")[0]))];
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0023-party-roles.cjs for company migration 0023 (Phase 3.9B, customers and suppliers). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> PartyRoleMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated party-role schema differs from the committed files"); process.exit(1); }
  console.log("party-role schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
