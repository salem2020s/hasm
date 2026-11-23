#!/usr/bin/env node
// Deterministic generator for the company migration of Phase 3.13 "short close" (owner decision D-201; number 0039, allocated by merge order; to renumber, change MIGRATION below and
// rename the committed SQL file; the name seed does NOT contain the number, so a renumbering touches nothing else).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{DocumentShortClose.Quantity}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.short-close.v1:" + token)
// (same rules as tools/schema/company-0030-document-links.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migrations 0028 and 0030) are written
// [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0039-short-close.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0039";   // company migration number (merge order); also the file name
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_short_close.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.ShortClose.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Commercial.cs"), ...parseDictionary("CompanySchema.CommercialApproval.cs"), ...parseDictionary("CompanySchema.DocumentLink.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration ${MIGRATION}: short close of commercial documents (Phase 3.13, owner decision D-201). Forward-only and ADDITIVE: ONE new table, ONE new guard on the document link table and ONE new guard on the commercial
-- document table; no existing object is dropped or replaced (the 0028, 0029 and 0030 functions are untouched), no existing row is read or modified, nothing is seeded. Opaque physical names (D-029) generated
-- deterministically by tools/schema/company-0039-short-close.cjs (logical dictionary: CompanySchema.ShortClose.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.13.md "Short close"; D-201: "An explicitly authorised short-close action may close the remaining quantity with a reason and audit, but must not invent deliveries, invoices or stock
-- movements."): an append-only record that a quantity of a line of an APPROVED commercial document will not be fulfilled or invoiced any more. It writes no link, no stock document, no journal entry. The quantity left
-- open on a line is DERIVED (ordered - max(fulfilled, invoiced) - short closed) and never stored. Reversing a short close is NOT offered (owner question, default: no).
-- Invariants enforced by PostgreSQL:
--   * a row is never changed or removed (no UPDATE, DELETE or TRUNCATE); one row per (creator, idempotency key, line); a reason of 1 to 500 characters without control characters is mandatory;
--   * the document is APPROVED, PARTIALLY FULFILLED (at least one link of kind 1 (fulfils) or 2 (invoices) leaves it: a document nothing was delivered or invoiced from is cancelled, not closed short) and the line belongs to it; the document is share-locked FIRST and the line locked (FOR NO KEY UPDATE) BEFORE any sum is read, in the order of a link and of a document edit, so
--     concurrent closes and links against the same line are serialized;
--   * TOLERANCE 0 STAYS: the quantity closed on a line never exceeds ordered - max(fulfilled, invoiced) (what was delivered or invoiced is never closed), and after a close a link of kind 1 (fulfils) or 2 (invoices)
--     from that line cannot take more than ordered - closed (an additive guard on the link table, same lock order as the 0030 guard, so the 0030 function is not replaced);
--   * an approved document that has a short close cannot leave the approved status (no cancellation, no re-approval): the same interim restriction as for linked documents (D-075).

CREATE TABLE {{DocumentShortClose}} (
    {{DocumentShortClose.Id}} uuid NOT NULL,
    {{DocumentShortClose.OperationId}} uuid NOT NULL,
    {{DocumentShortClose.DocumentId}} uuid NOT NULL,
    {{DocumentShortClose.LineId}} uuid NOT NULL,
    {{DocumentShortClose.Quantity}} numeric(28,10) NOT NULL,
    {{DocumentShortClose.Reason}} varchar(500) NOT NULL,
    {{DocumentShortClose.IdempotencyKey}} varchar(64) NOT NULL,
    {{DocumentShortClose.PayloadHash}} char(64) NOT NULL,
    {{DocumentShortClose.CreatedBy}} uuid NOT NULL,
    {{DocumentShortClose.CreatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{DocumentShortClose.Pk}} PRIMARY KEY ({{DocumentShortClose.Id}}),
    CONSTRAINT {{DocumentShortClose.FkDocument}} FOREIGN KEY ({{DocumentShortClose.DocumentId}}) REFERENCES [[CommercialDocument.Table]] ([[CommercialDocument.Id]]),
    CONSTRAINT {{DocumentShortClose.FkLine}} FOREIGN KEY ({{DocumentShortClose.LineId}}) REFERENCES [[CommercialDocumentLine.Table]] ([[CommercialDocumentLine.Id]]),
    CONSTRAINT {{DocumentShortClose.CkValues}} CHECK ({{DocumentShortClose.Quantity}} > 0 AND {{DocumentShortClose.Quantity}} < 1000000000
        AND char_length({{DocumentShortClose.Reason}}) BETWEEN 1 AND 500 AND {{DocumentShortClose.Reason}} !~ '[[:cntrl:]]' AND btrim({{DocumentShortClose.Reason}}) <> ''
        AND char_length({{DocumentShortClose.IdempotencyKey}}) BETWEEN 8 AND 64 AND {{DocumentShortClose.PayloadHash}} ~ '^[0-9a-f]{64}$')
);
CREATE UNIQUE INDEX {{DocumentShortClose.UqIdempotency}} ON {{DocumentShortClose}} ({{DocumentShortClose.CreatedBy}}, {{DocumentShortClose.IdempotencyKey}}, {{DocumentShortClose.LineId}});
CREATE INDEX {{DocumentShortClose.IxLine}} ON {{DocumentShortClose}} ({{DocumentShortClose.LineId}});
CREATE INDEX {{DocumentShortClose.IxDocument}} ON {{DocumentShortClose}} ({{DocumentShortClose.DocumentId}});
CREATE INDEX {{DocumentShortClose.IxOperation}} ON {{DocumentShortClose}} ({{DocumentShortClose.OperationId}});

CREATE FUNCTION {{DocumentShortClose.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ldoc uuid; lqty numeric; dstatus smallint; fulfilled numeric; invoiced numeric; closed numeric;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'short close records are append-only'; END IF;
    -- LOCK ORDER (the order of a link and of a document edit: the document row first, its line second): the document is share-locked, a concurrent cancel or re-approval waits for this transaction; THEN the line is
    -- locked BEFORE any sum is read, so every close and every link against the same line is serialized here
    PERFORM 1 FROM [[CommercialDocument.Table]] d WHERE d.[[CommercialDocument.Id]] = NEW.{{DocumentShortClose.DocumentId}} FOR SHARE;
    SELECT d.[[CommercialDocument.Status]] INTO dstatus FROM [[CommercialDocument.Table]] d WHERE d.[[CommercialDocument.Id]] = NEW.{{DocumentShortClose.DocumentId}};
    PERFORM 1 FROM [[CommercialDocumentLine.Table]] l WHERE l.[[CommercialDocumentLine.Id]] = NEW.{{DocumentShortClose.LineId}} FOR NO KEY UPDATE;
    SELECT l.[[CommercialDocumentLine.DocumentId]], l.[[CommercialDocumentLine.Quantity]] INTO ldoc, lqty FROM [[CommercialDocumentLine.Table]] l WHERE l.[[CommercialDocumentLine.Id]] = NEW.{{DocumentShortClose.LineId}};
    IF ldoc IS DISTINCT FROM NEW.{{DocumentShortClose.DocumentId}} THEN RAISE EXCEPTION 'a short close names a line of its own document'; END IF;
    IF dstatus IS DISTINCT FROM 3 THEN RAISE EXCEPTION 'only an approved document can be closed short'; END IF;
    -- links are append-only, so a link seen here stays; a link of a concurrent, uncommitted transaction is not seen and the close is refused (fail-closed)
    IF NOT EXISTS (SELECT 1 FROM [[DocumentLink.Table]] k WHERE k.[[DocumentLink.FromDocumentId]] = NEW.{{DocumentShortClose.DocumentId}} AND k.[[DocumentLink.Kind]] IN (1, 2)) THEN
        RAISE EXCEPTION 'only a partially fulfilled document can be closed short';
    END IF;
    SELECT COALESCE(sum(k.[[DocumentLink.Quantity]]) FILTER (WHERE k.[[DocumentLink.Kind]] = 1), 0), COALESCE(sum(k.[[DocumentLink.Quantity]]) FILTER (WHERE k.[[DocumentLink.Kind]] = 2), 0) INTO fulfilled, invoiced
        FROM [[DocumentLink.Table]] k WHERE k.[[DocumentLink.FromLineId]] = NEW.{{DocumentShortClose.LineId}};
    SELECT COALESCE(sum(s.{{DocumentShortClose.Quantity}}), 0) INTO closed FROM {{DocumentShortClose}} s WHERE s.{{DocumentShortClose.LineId}} = NEW.{{DocumentShortClose.LineId}};
    IF closed + NEW.{{DocumentShortClose.Quantity}} > lqty - GREATEST(fulfilled, invoiced) THEN
        RAISE EXCEPTION 'a short close never exceeds the open quantity (ordered minus the larger of fulfilled and invoiced minus already closed)';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{DocumentShortClose.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{DocumentShortClose}} FOR EACH ROW EXECUTE FUNCTION {{DocumentShortClose.FnGuard}}();
CREATE TRIGGER {{DocumentShortClose.TrTruncate}} BEFORE TRUNCATE ON {{DocumentShortClose}} FOR EACH STATEMENT EXECUTE FUNCTION {{DocumentShortClose.FnGuard}}();
REVOKE ALL ON TABLE {{DocumentShortClose}} FROM PUBLIC;

-- tolerance 0 after a close: a link that fulfils or invoices a line cannot take more than ordered - closed. This guard takes the SAME locks in the SAME order as the link guard of 0030 (both documents share-locked in
-- id order, then both lines in id order), so whichever of the two guards fires first, no lock order is ever inverted and no sum is read before the line is locked.
CREATE FUNCTION {{DocumentShortClose.FnLinkCap}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    fqty numeric; closed numeric; used numeric;
BEGIN
    IF NEW.[[DocumentLink.Kind]] NOT IN (1, 2) THEN RETURN NEW; END IF;
    PERFORM 1 FROM [[CommercialDocument.Table]] d WHERE d.[[CommercialDocument.Id]] IN (NEW.[[DocumentLink.FromDocumentId]], NEW.[[DocumentLink.ToDocumentId]]) ORDER BY d.[[CommercialDocument.Id]] FOR SHARE;
    PERFORM 1 FROM [[CommercialDocumentLine.Table]] l WHERE l.[[CommercialDocumentLine.Id]] IN (NEW.[[DocumentLink.FromLineId]], NEW.[[DocumentLink.ToLineId]]) ORDER BY l.[[CommercialDocumentLine.Id]] FOR NO KEY UPDATE;
    SELECT COALESCE(sum(s.{{DocumentShortClose.Quantity}}), 0) INTO closed FROM {{DocumentShortClose}} s WHERE s.{{DocumentShortClose.LineId}} = NEW.[[DocumentLink.FromLineId]];
    IF closed > 0 THEN
        SELECT l.[[CommercialDocumentLine.Quantity]] INTO fqty FROM [[CommercialDocumentLine.Table]] l WHERE l.[[CommercialDocumentLine.Id]] = NEW.[[DocumentLink.FromLineId]];
        SELECT COALESCE(sum(k.[[DocumentLink.Quantity]]), 0) INTO used FROM [[DocumentLink.Table]] k WHERE k.[[DocumentLink.FromLineId]] = NEW.[[DocumentLink.FromLineId]] AND k.[[DocumentLink.Kind]] = NEW.[[DocumentLink.Kind]];
        IF used + NEW.[[DocumentLink.Quantity]] > fqty - closed THEN RAISE EXCEPTION 'over-fulfilment: the quantity linked from a source line never exceeds its quantity minus what was closed short'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{DocumentShortClose.TrLinkCap}} BEFORE INSERT ON [[DocumentLink.Table]] FOR EACH ROW EXECUTE FUNCTION {{DocumentShortClose.FnLinkCap}}();

-- a document that was closed short stays approved (the same interim restriction as for linked documents, D-075): no cancellation and no re-approval until the owner decides how closed documents may be reversed
CREATE FUNCTION {{DocumentShortClose.FnDocGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.[[CommercialDocument.Status]] = 3 AND NEW.[[CommercialDocument.Status]] <> 3
       AND EXISTS (SELECT 1 FROM {{DocumentShortClose}} s WHERE s.{{DocumentShortClose.DocumentId}} = OLD.[[CommercialDocument.Id]]) THEN
        RAISE EXCEPTION 'an approved document that was closed short cannot leave the approved status';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{DocumentShortClose.TrDocGuard}} BEFORE UPDATE ON [[CommercialDocument.Table]] FOR EACH ROW EXECUTE FUNCTION {{DocumentShortClose.FnDocGuard}}();
`;
function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.short-close.v1:" + token).digest("hex").slice(0, 8);

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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0039-short-close.cjs for the company migration of Phase 3.13 short close (D-201). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// DocumentShortClose names the new table together with its columns, constraints, indexes, functions and triggers (the guards added to the link and document tables are listed here too).\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> ShortCloseMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated short-close schema differs from the committed files"); process.exit(1); }
  console.log("short-close schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
