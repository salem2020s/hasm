#!/usr/bin/env node
// Deterministic generator for company migration 0024 (Phase 3.12 attachments: metadata of files stored on the server, plaintext hash, sensitivity and key id).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{Attachment.Sha256}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0024.attachments.v1:" + token)
// so the names are reproducible from this file alone, unique (a collision with a name already in the repository or inside this migration aborts the run, nothing is re-hashed
// silently) and fixed once committed (changing one needs a new migration). Prefix by the last segment of the token: table (no dot) t_, Fn* f_, Tr* r_, Pk/Uq*/Fk*/Ck*/Ix* k_, otherwise c_.
// Usage:  node tools/schema/company-0024-attachments.cjs --write   writes the migration and the C# dictionary
//         node tools/schema/company-0024-attachments.cjs --check   fails when the committed files differ from the generated ones (run by CI and the unit tests)
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0024_company_attachments.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.Attachments.cs");

const TEMPLATE = `-- Migration 0024: attachments (Phase 3.12). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0024-attachments.cjs (logical dictionary: CompanySchema.Attachments.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 14.3, 14.4, 17.1, 17.2): the METADATA of a file kept on the server (never in the database): which business record it belongs to (a registered kind and the record id; a polymorphic
-- reference is validated by the service, which is the only writer), its display name and type, its size, the SHA-256 of the PLAINTEXT (verified on every read), its sensitivity, the opaque name of the
-- stored file, and for a sensitive file the id of the key that wraps it. The file itself is plain on disk when normal and application-level encrypted (envelope: AES-256-GCM, versioned master key) when sensitive.
-- Invariants enforced by PostgreSQL:
--   * a row is never removed: an attachment is REMOVED (state 2) with a reason, once, and a removed row never changes again; every change advances the revision by one;
--   * identity, record, name, type, size, hash, sensitivity, stored name and creation data are immutable; only the key id of an ACTIVE sensitive attachment may change (re-wrap) and the state may go 1 -> 2;
--   * a sensitive attachment has a key id and a normal one has none; the hash is 64 lowercase hex digits; the size is positive; a removal carries who, when and why.
-- sensitivity: 1 normal, 2 sensitive. state: 1 active, 2 removed.

CREATE TABLE {{Attachment}} (
    {{Attachment.Id}} uuid NOT NULL,
    {{Attachment.SubjectKind}} varchar(40) NOT NULL,
    {{Attachment.SubjectId}} uuid NOT NULL,
    {{Attachment.FileName}} varchar(200) NOT NULL,
    {{Attachment.ContentType}} varchar(100) NOT NULL,
    {{Attachment.SizeBytes}} bigint NOT NULL,
    {{Attachment.Sha256}} char(64) NOT NULL,
    {{Attachment.Sensitivity}} smallint NOT NULL,
    {{Attachment.StorageName}} varchar(40) NOT NULL,
    {{Attachment.KeyId}} varchar(40) NULL,
    {{Attachment.State}} smallint NOT NULL DEFAULT 1,
    {{Attachment.RemovedBy}} uuid NULL,
    {{Attachment.RemovedAtUtc}} timestamptz NULL,
    {{Attachment.RemovalReason}} varchar(500) NULL,
    {{Attachment.Revision}} bigint NOT NULL DEFAULT 1,
    {{Attachment.CreatedBy}} uuid NOT NULL,
    {{Attachment.CreatedAtUtc}} timestamptz NOT NULL,
    {{Attachment.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{Attachment.Pk}} PRIMARY KEY ({{Attachment.Id}}),
    CONSTRAINT {{Attachment.CkValues}} CHECK ({{Attachment.Revision}} > 0 AND {{Attachment.SubjectKind}} ~ '^[a-z][a-z0-9_]{1,39}$' AND char_length(btrim({{Attachment.FileName}})) >= 1
        AND {{Attachment.SizeBytes}} > 0 AND {{Attachment.Sha256}} ~ '^[0-9a-f]{64}$' AND {{Attachment.Sensitivity}} IN (1, 2) AND {{Attachment.State}} IN (1, 2)
        AND {{Attachment.StorageName}} ~ '^[0-9a-f]{32}\\\.att$' AND (({{Attachment.Sensitivity}} = 2) = ({{Attachment.KeyId}} IS NOT NULL))
        AND (({{Attachment.State}} = 2) = ({{Attachment.RemovedAtUtc}} IS NOT NULL AND {{Attachment.RemovedBy}} IS NOT NULL AND char_length(btrim(coalesce({{Attachment.RemovalReason}}, ''))) >= 1)))
);
CREATE UNIQUE INDEX {{Attachment.UqStorage}} ON {{Attachment}} ({{Attachment.StorageName}});
CREATE INDEX {{Attachment.IxSubject}} ON {{Attachment}} ({{Attachment.SubjectKind}}, {{Attachment.SubjectId}});

CREATE FUNCTION {{Attachment.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{Attachment.Revision}} <> 1 OR NEW.{{Attachment.State}} <> 1 THEN RAISE EXCEPTION 'an attachment is created active at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'attachments are never removed from the database'; END IF;
    IF OLD.{{Attachment.State}} = 2 THEN RAISE EXCEPTION 'a removed attachment never changes again'; END IF;
    IF NEW.{{Attachment.Id}} IS DISTINCT FROM OLD.{{Attachment.Id}} OR NEW.{{Attachment.SubjectKind}} IS DISTINCT FROM OLD.{{Attachment.SubjectKind}} OR NEW.{{Attachment.SubjectId}} IS DISTINCT FROM OLD.{{Attachment.SubjectId}}
       OR NEW.{{Attachment.FileName}} IS DISTINCT FROM OLD.{{Attachment.FileName}} OR NEW.{{Attachment.ContentType}} IS DISTINCT FROM OLD.{{Attachment.ContentType}} OR NEW.{{Attachment.SizeBytes}} IS DISTINCT FROM OLD.{{Attachment.SizeBytes}}
       OR NEW.{{Attachment.Sha256}} IS DISTINCT FROM OLD.{{Attachment.Sha256}} OR NEW.{{Attachment.Sensitivity}} IS DISTINCT FROM OLD.{{Attachment.Sensitivity}} OR NEW.{{Attachment.StorageName}} IS DISTINCT FROM OLD.{{Attachment.StorageName}}
       OR NEW.{{Attachment.CreatedBy}} IS DISTINCT FROM OLD.{{Attachment.CreatedBy}} OR NEW.{{Attachment.CreatedAtUtc}} IS DISTINCT FROM OLD.{{Attachment.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the record, name, type, size, hash, sensitivity, stored name and creation data of an attachment are immutable';
    END IF;
    IF NEW.{{Attachment.Revision}} <> OLD.{{Attachment.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NEW.{{Attachment.State}} = 1 AND (NEW.{{Attachment.RemovedBy}} IS NOT NULL OR NEW.{{Attachment.RemovedAtUtc}} IS NOT NULL OR NEW.{{Attachment.RemovalReason}} IS NOT NULL) THEN RAISE EXCEPTION 'removal data on an active attachment'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{Attachment.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{Attachment}} FOR EACH ROW EXECUTE FUNCTION {{Attachment.FnGuard}}();
CREATE TRIGGER {{Attachment.TrTruncate}} BEFORE TRUNCATE ON {{Attachment}} FOR EACH STATEMENT EXECUTE FUNCTION {{Attachment.FnGuard}}();
REVOKE ALL ON TABLE {{Attachment}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0024.attachments.v1:" + token).digest("hex").slice(0, 8);

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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0024-attachments.cjs for company migration 0024 (Phase 3.12, attachments). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> AttachmentMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated attachment schema differs from the committed files"); process.exit(1); }
  console.log("attachment schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
