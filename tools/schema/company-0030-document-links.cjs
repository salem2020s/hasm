#!/usr/bin/env node
// Deterministic generator for company migration 0030 (Phase 3.13 slice S2: document links and partial fulfilment).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CommercialDocument.DocType}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0030.document-links.v1:" + token)
// (same rules as tools/schema/company-0026-pricing.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migration 0028 and 0029) are written
// [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0030-document-links.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0030_company_document_links.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.DocumentLink.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Commercial.cs"), ...parseDictionary("CompanySchema.CommercialApproval.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration 0030: document links and partial fulfilment (Phase 3.13 slice S2). Forward-only, additive: ONE new table and ONE new guard on the commercial document table; no existing row is read or modified,
-- no existing object is dropped or replaced, and nothing is seeded. Opaque physical names (D-029) generated deterministically by tools/schema/company-0030-document-links.cjs (logical dictionary: CompanySchema.DocumentLink.cs).
-- Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.13.md S2; decisions D-074..D-077): an append-only link from a line of one approved commercial document (the SOURCE, e.g. an order or a delivery) to a line of another approved document of the
-- same party, currency and branch (the TARGET, e.g. a delivery, an invoice or a return), with a kind (1 fulfils, 2 invoices, 3 returns, 4 amends) and a quantity. Ordered, fulfilled, invoiced and returned quantities are
-- DERIVED from these rows and never stored, so a counter cannot drift.
-- Invariants enforced by PostgreSQL:
--   * a link is never changed or removed (no UPDATE, DELETE or TRUNCATE); one link per (source line, target line, kind); one idempotency key per creator;
--   * both documents are APPROVED when the link is written (they are share-locked FIRST, so a concurrent cancel or status change cannot interleave and a link never deadlocks with a document edit), differ, share party, currency and branch, and the named lines belong to them;
--   * OVER-FULFILMENT IS REFUSED (Q3 default: tolerance 0, owner decision pending): the quantity linked from one source line for one kind never exceeds the quantity of that line, and the quantity linked into one target line
--     for one kind never exceeds the quantity of that line. Both lines are locked (FOR NO KEY UPDATE, in id order, so concurrent links never deadlock) BEFORE the sums are read, so concurrent links against the last remaining
--     quantity create exactly the allowed ones;
--   * an approved document that has links cannot leave the approved status (no cancellation, no re-approval): an interim restriction (D-075), stricter than the owner question Q8.

CREATE TABLE {{DocumentLink}} (
    {{DocumentLink.Id}} uuid NOT NULL,
    {{DocumentLink.FromDocumentId}} uuid NOT NULL,
    {{DocumentLink.FromLineId}} uuid NOT NULL,
    {{DocumentLink.ToDocumentId}} uuid NOT NULL,
    {{DocumentLink.ToLineId}} uuid NOT NULL,
    {{DocumentLink.Kind}} smallint NOT NULL,
    {{DocumentLink.Quantity}} numeric(28,10) NOT NULL,
    {{DocumentLink.IdempotencyKey}} varchar(64) NOT NULL,
    {{DocumentLink.PayloadHash}} char(64) NOT NULL,
    {{DocumentLink.CreatedBy}} uuid NOT NULL,
    {{DocumentLink.CreatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{DocumentLink.Pk}} PRIMARY KEY ({{DocumentLink.Id}}),
    CONSTRAINT {{DocumentLink.FkFromDocument}} FOREIGN KEY ({{DocumentLink.FromDocumentId}}) REFERENCES [[CommercialDocument.Table]] ([[CommercialDocument.Id]]),
    CONSTRAINT {{DocumentLink.FkFromLine}} FOREIGN KEY ({{DocumentLink.FromLineId}}) REFERENCES [[CommercialDocumentLine.Table]] ([[CommercialDocumentLine.Id]]),
    CONSTRAINT {{DocumentLink.FkToDocument}} FOREIGN KEY ({{DocumentLink.ToDocumentId}}) REFERENCES [[CommercialDocument.Table]] ([[CommercialDocument.Id]]),
    CONSTRAINT {{DocumentLink.FkToLine}} FOREIGN KEY ({{DocumentLink.ToLineId}}) REFERENCES [[CommercialDocumentLine.Table]] ([[CommercialDocumentLine.Id]]),
    CONSTRAINT {{DocumentLink.CkValues}} CHECK ({{DocumentLink.Kind}} IN (1, 2, 3, 4) AND {{DocumentLink.Quantity}} > 0 AND {{DocumentLink.Quantity}} < 1000000000
        AND {{DocumentLink.FromDocumentId}} <> {{DocumentLink.ToDocumentId}} AND {{DocumentLink.FromLineId}} <> {{DocumentLink.ToLineId}}
        AND char_length({{DocumentLink.IdempotencyKey}}) BETWEEN 8 AND 64 AND {{DocumentLink.PayloadHash}} ~ '^[0-9a-f]{64}$')
);
CREATE UNIQUE INDEX {{DocumentLink.UqPair}} ON {{DocumentLink}} ({{DocumentLink.FromLineId}}, {{DocumentLink.ToLineId}}, {{DocumentLink.Kind}});
CREATE UNIQUE INDEX {{DocumentLink.UqIdempotency}} ON {{DocumentLink}} ({{DocumentLink.CreatedBy}}, {{DocumentLink.IdempotencyKey}});
CREATE INDEX {{DocumentLink.IxFromLine}} ON {{DocumentLink}} ({{DocumentLink.FromLineId}}, {{DocumentLink.Kind}});
CREATE INDEX {{DocumentLink.IxToLine}} ON {{DocumentLink}} ({{DocumentLink.ToLineId}}, {{DocumentLink.Kind}});
CREATE INDEX {{DocumentLink.IxFromDocument}} ON {{DocumentLink}} ({{DocumentLink.FromDocumentId}});
CREATE INDEX {{DocumentLink.IxToDocument}} ON {{DocumentLink}} ({{DocumentLink.ToDocumentId}});

CREATE FUNCTION {{DocumentLink.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    fdoc uuid; tdoc uuid; fqty numeric; tqty numeric; fstatus smallint; tstatus smallint;
    fparty uuid; tparty uuid; fcur char(3); tcur char(3); fbranch uuid; tbranch uuid; used numeric;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'document links are append-only'; END IF;
    -- LOCK ORDER (same as a document edit: the document row first, its lines second, so a link and a material edit can never deadlock): the two documents are share-locked in id order, a concurrent status change
    -- (cancel, re-approval) waits for this transaction and this transaction sees the status it left; THEN the two lines are locked in id order BEFORE any sum is read, so every link against the same line is serialized
    -- here and the statements below see every link that committed before the lock was granted
    PERFORM 1 FROM [[CommercialDocument.Table]] d WHERE d.[[CommercialDocument.Id]] IN (NEW.{{DocumentLink.FromDocumentId}}, NEW.{{DocumentLink.ToDocumentId}}) ORDER BY d.[[CommercialDocument.Id]] FOR SHARE;
    SELECT d.[[CommercialDocument.Status]], d.[[CommercialDocument.PartyId]], d.[[CommercialDocument.Currency]], d.[[CommercialDocument.BranchId]] INTO fstatus, fparty, fcur, fbranch FROM [[CommercialDocument.Table]] d WHERE d.[[CommercialDocument.Id]] = NEW.{{DocumentLink.FromDocumentId}};
    SELECT d.[[CommercialDocument.Status]], d.[[CommercialDocument.PartyId]], d.[[CommercialDocument.Currency]], d.[[CommercialDocument.BranchId]] INTO tstatus, tparty, tcur, tbranch FROM [[CommercialDocument.Table]] d WHERE d.[[CommercialDocument.Id]] = NEW.{{DocumentLink.ToDocumentId}};
    PERFORM 1 FROM [[CommercialDocumentLine.Table]] l WHERE l.[[CommercialDocumentLine.Id]] IN (NEW.{{DocumentLink.FromLineId}}, NEW.{{DocumentLink.ToLineId}}) ORDER BY l.[[CommercialDocumentLine.Id]] FOR NO KEY UPDATE;
    SELECT l.[[CommercialDocumentLine.DocumentId]], l.[[CommercialDocumentLine.Quantity]] INTO fdoc, fqty FROM [[CommercialDocumentLine.Table]] l WHERE l.[[CommercialDocumentLine.Id]] = NEW.{{DocumentLink.FromLineId}};
    SELECT l.[[CommercialDocumentLine.DocumentId]], l.[[CommercialDocumentLine.Quantity]] INTO tdoc, tqty FROM [[CommercialDocumentLine.Table]] l WHERE l.[[CommercialDocumentLine.Id]] = NEW.{{DocumentLink.ToLineId}};
    IF fdoc IS DISTINCT FROM NEW.{{DocumentLink.FromDocumentId}} OR tdoc IS DISTINCT FROM NEW.{{DocumentLink.ToDocumentId}} THEN RAISE EXCEPTION 'a link names lines of its own documents'; END IF;
    IF fstatus IS DISTINCT FROM 3 OR tstatus IS DISTINCT FROM 3 THEN RAISE EXCEPTION 'both documents of a link must be approved'; END IF;
    IF fparty IS DISTINCT FROM tparty OR fcur IS DISTINCT FROM tcur OR fbranch IS DISTINCT FROM tbranch THEN RAISE EXCEPTION 'linked documents must share the party, the currency and the branch'; END IF;
    IF NEW.{{DocumentLink.Quantity}} > fqty OR NEW.{{DocumentLink.Quantity}} > tqty THEN RAISE EXCEPTION 'a link never exceeds the quantity of either line'; END IF;
    IF NEW.{{DocumentLink.Kind}} IN (1, 2, 3) THEN
        SELECT COALESCE(sum(k.{{DocumentLink.Quantity}}), 0) INTO used FROM {{DocumentLink}} k WHERE k.{{DocumentLink.FromLineId}} = NEW.{{DocumentLink.FromLineId}} AND k.{{DocumentLink.Kind}} = NEW.{{DocumentLink.Kind}};
        IF used + NEW.{{DocumentLink.Quantity}} > fqty THEN RAISE EXCEPTION 'over-fulfilment: the quantity linked from a source line never exceeds its quantity'; END IF;
        SELECT COALESCE(sum(k.{{DocumentLink.Quantity}}), 0) INTO used FROM {{DocumentLink}} k WHERE k.{{DocumentLink.ToLineId}} = NEW.{{DocumentLink.ToLineId}} AND k.{{DocumentLink.Kind}} = NEW.{{DocumentLink.Kind}};
        IF used + NEW.{{DocumentLink.Quantity}} > tqty THEN RAISE EXCEPTION 'over-fulfilment: the quantity linked into a target line never exceeds its quantity'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{DocumentLink.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{DocumentLink}} FOR EACH ROW EXECUTE FUNCTION {{DocumentLink.FnGuard}}();
CREATE TRIGGER {{DocumentLink.TrTruncate}} BEFORE TRUNCATE ON {{DocumentLink}} FOR EACH STATEMENT EXECUTE FUNCTION {{DocumentLink.FnGuard}}();
REVOKE ALL ON TABLE {{DocumentLink}} FROM PUBLIC;

-- a document that has links stays approved (interim restriction D-075): no cancellation and no re-approval until the owner decides how linked documents may be reversed
CREATE FUNCTION {{DocumentLink.FnDocGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.[[CommercialDocument.Status]] = 3 AND NEW.[[CommercialDocument.Status]] <> 3
       AND EXISTS (SELECT 1 FROM {{DocumentLink}} k WHERE k.{{DocumentLink.FromDocumentId}} = OLD.[[CommercialDocument.Id]] OR k.{{DocumentLink.ToDocumentId}} = OLD.[[CommercialDocument.Id]]) THEN
        RAISE EXCEPTION 'an approved document that has links cannot leave the approved status';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{DocumentLink.TrDocGuard}} BEFORE UPDATE ON [[CommercialDocument.Table]] FOR EACH ROW EXECUTE FUNCTION {{DocumentLink.FnDocGuard}}();
`
function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0030.document-links.v1:" + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (/\d{4}_company_short_close\.sql$|CompanySchema\.ShortClose\.cs$/.test(p)) continue;   // a later migration: it REFERENCES the names of this one
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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0030-document-links.cjs for company migration 0030 (Phase 3.13 slice S2, document links). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// DocumentLink names the new table of slice S2 together with its columns, constraints, indexes, functions and triggers (the guard trigger added to the 0028 document table is listed here too).\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> DocumentLinkMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated document-links schema differs from the committed files"); process.exit(1); }
  console.log("document-links schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
