#!/usr/bin/env node
// Deterministic generator for company migration 0025 (Phase 3.9C reusable import mapping templates).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{ImportMappingTemplate.Sha256}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0025.importtemplates.v1:" + token)
// so the names are reproducible from this file alone, unique (a collision with a name already in the repository or inside this migration aborts the run, nothing is re-hashed
// silently) and fixed once committed (changing one needs a new migration). Prefix by the last segment of the token: table (no dot) t_, Fn* f_, Tr* r_, Pk/Uq*/Fk*/Ck*/Ix* k_, otherwise c_.
// Usage:  node tools/schema/company-0025-import-templates.cjs --write   writes the migration and the C# dictionary
//         node tools/schema/company-0025-import-templates.cjs --check   fails when the committed files differ from the generated ones (run by CI and the unit tests)
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0025_company_import_templates.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.ImportTemplates.cs");

const TEMPLATE = `-- Migration 0025: reusable import mapping templates (Phase 3.9C, OD-9 default). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0025-import-templates.cjs (logical dictionary: CompanySchema.ImportTemplates.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (REPORTING-REQUIREMENTS 3 item 11, ROADMAP 3.9C, OD-9): a company-wide, per-adapter reusable mapping of FILE HEADERS to adapter fields (never column positions), versioned. It is
-- CONFIGURATION: applying a template only pre-fills the ordinary mapping of a batch; validation, dry run, approval and the plan hash are untouched, so a template can never skip them.
-- Invariants enforced by PostgreSQL: a template version is append-only (no update, delete or truncate); versions of one (adapter, name) are 1, 2, 3 ... without gaps; retirement is a new version
-- flagged retired; the content is bounded; names and adapter codes are well formed.

CREATE TABLE {{ImportMappingTemplate}} (
    {{ImportMappingTemplate.Id}} uuid NOT NULL,
    {{ImportMappingTemplate.Adapter}} varchar(60) NOT NULL,
    {{ImportMappingTemplate.Name}} varchar(100) NOT NULL,
    {{ImportMappingTemplate.Version}} integer NOT NULL,
    {{ImportMappingTemplate.IsRetired}} boolean NOT NULL DEFAULT false,
    {{ImportMappingTemplate.Content}} text NOT NULL,
    {{ImportMappingTemplate.CreatedBy}} uuid NOT NULL,
    {{ImportMappingTemplate.CreatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{ImportMappingTemplate.Pk}} PRIMARY KEY ({{ImportMappingTemplate.Id}}),
    CONSTRAINT {{ImportMappingTemplate.CkValues}} CHECK ({{ImportMappingTemplate.Version}} > 0 AND {{ImportMappingTemplate.Adapter}} ~ '^[a-z][a-z0-9-]{1,59}$' AND char_length(btrim({{ImportMappingTemplate.Name}})) BETWEEN 1 AND 100
        AND char_length({{ImportMappingTemplate.Content}}) BETWEEN 2 AND 65536)
);
CREATE UNIQUE INDEX {{ImportMappingTemplate.UqVersion}} ON {{ImportMappingTemplate}} ({{ImportMappingTemplate.Adapter}}, {{ImportMappingTemplate.Name}}, {{ImportMappingTemplate.Version}});

CREATE FUNCTION {{ImportMappingTemplate.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        -- the next version of a template is exactly one above the latest (serialized by the service per template; the unique index refuses a duplicate)
        IF NEW.{{ImportMappingTemplate.Version}} <> COALESCE((SELECT max(t.{{ImportMappingTemplate.Version}}) FROM {{ImportMappingTemplate}} t WHERE t.{{ImportMappingTemplate.Adapter}} = NEW.{{ImportMappingTemplate.Adapter}} AND t.{{ImportMappingTemplate.Name}} = NEW.{{ImportMappingTemplate.Name}}), 0) + 1 THEN
            RAISE EXCEPTION 'a template version must be exactly one above the latest';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'import mapping template versions are append-only';
END;
$$;
CREATE TRIGGER {{ImportMappingTemplate.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{ImportMappingTemplate}} FOR EACH ROW EXECUTE FUNCTION {{ImportMappingTemplate.FnGuard}}();
CREATE TRIGGER {{ImportMappingTemplate.TrTruncate}} BEFORE TRUNCATE ON {{ImportMappingTemplate}} FOR EACH STATEMENT EXECUTE FUNCTION {{ImportMappingTemplate.FnGuard}}();
REVOKE ALL ON TABLE {{ImportMappingTemplate}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0025.importtemplates.v1:" + token).digest("hex").slice(0, 8);

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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0025-import-templates.cjs for company migration 0025 (Phase 3.9C, import mapping templates). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> ImportTemplateMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated import-template schema differs from the committed files"); process.exit(1); }
  console.log("import-template schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
