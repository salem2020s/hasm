#!/usr/bin/env node
// Deterministic generator for company migration 0028 (Phase 3.13 slice S1a: the commercial document skeleton - header, lines and the append-only event history).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CommercialDocument.DocType}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0028.commercial.v1:" + token)
// (same rules as tools/schema/company-0026-pricing.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migration 0026 and 0021/0023) are written
// [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0028-commercial.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0028_company_commercial_documents.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.Commercial.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Branches.cs"), ...parseDictionary("CompanySchema.Parties.cs"), ...parseDictionary("CompanySchema.Inventory.cs"), ...parseDictionary("CompanySchema.Warehouses.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration 0028: commercial document skeleton (Phase 3.13 slice S1a). Forward-only, additive (three new tables); no existing row is read or modified, nothing is seeded.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0028-commercial.cjs (logical dictionary: CompanySchema.Commercial.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 7.2, 7.6, 4.3, 19.2, 19.7; docs/PHASE-3.13.md): the commercial-status track of a sales/purchase document: a header, its lines (item, unit, quantity and an optional warehouse; NO price, tax or cost in
-- this slice) and an append-only history of status changes. It is a business document skeleton: no stock, no journal and no amount exists here. Physical and accounting tracks are later slices and are DERIVED, never flags here.
-- Invariants enforced by PostgreSQL:
--   * documents and events are never removed; the identity (type, branch, creator, idempotency key, payload hash) never changes; every change advances the revision by one;
--   * the status guard allows ONLY draft -> submitted, submitted -> draft (withdraw), and draft/submitted -> cancelled (S1a). Approval, rejection and the cancellation of an approved document are NOT in this guard:
--     a direct statement attempting them is refused. Slice S1b adds them through a new migration that replaces this guard. A cancelled document is final;
--   * a submitted or cancelled document is not edited (a status change edits nothing else); the lines are editable (insert, update, delete) ONLY while the document is a draft;
--   * the events are append-only, one per revision, and each must carry the CURRENT revision and status of its document (events are written after the document change in the same transaction); one idempotency key per (document type, creator).

CREATE TABLE {{CommercialDocument}} (
    {{CommercialDocument.Id}} uuid NOT NULL,
    {{CommercialDocument.DocType}} varchar(40) NOT NULL,
    {{CommercialDocument.BranchId}} uuid NOT NULL,
    {{CommercialDocument.PartyId}} uuid NOT NULL,
    {{CommercialDocument.Currency}} char(3) NOT NULL,
    {{CommercialDocument.DocumentDate}} date NOT NULL,
    {{CommercialDocument.Mode}} smallint NOT NULL,
    {{CommercialDocument.Status}} smallint NOT NULL,
    {{CommercialDocument.Note}} varchar(500) NULL,
    {{CommercialDocument.IdempotencyKey}} varchar(64) NOT NULL,
    {{CommercialDocument.PayloadHash}} char(64) NOT NULL,
    {{CommercialDocument.SubmittedBy}} uuid NULL,
    {{CommercialDocument.Revision}} bigint NOT NULL DEFAULT 1,
    {{CommercialDocument.CreatedBy}} uuid NOT NULL,
    {{CommercialDocument.CreatedAtUtc}} timestamptz NOT NULL,
    {{CommercialDocument.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{CommercialDocument.Pk}} PRIMARY KEY ({{CommercialDocument.Id}}),
    CONSTRAINT {{CommercialDocument.FkBranch}} FOREIGN KEY ({{CommercialDocument.BranchId}}) REFERENCES [[Branch.Table]] ([[Branch.Id]]),
    CONSTRAINT {{CommercialDocument.FkParty}} FOREIGN KEY ({{CommercialDocument.PartyId}}) REFERENCES [[LocalParty.Table]] ([[LocalParty.Id]]),
    CONSTRAINT {{CommercialDocument.CkValues}} CHECK ({{CommercialDocument.Revision}} > 0 AND {{CommercialDocument.DocType}} ~ '^[a-z][a-z0-9_]{1,39}$' AND {{CommercialDocument.Currency}} ~ '^[A-Z]{3}$'
        AND {{CommercialDocument.Mode}} IN (1, 2) AND {{CommercialDocument.Status}} IN (1, 2, 3, 4, 5) AND {{CommercialDocument.PayloadHash}} ~ '^[0-9a-f]{64}$'
        AND char_length({{CommercialDocument.IdempotencyKey}}) BETWEEN 8 AND 64)
);
CREATE UNIQUE INDEX {{CommercialDocument.UqIdempotency}} ON {{CommercialDocument}} ({{CommercialDocument.DocType}}, {{CommercialDocument.CreatedBy}}, {{CommercialDocument.IdempotencyKey}});
CREATE INDEX {{CommercialDocument.IxBranch}} ON {{CommercialDocument}} ({{CommercialDocument.BranchId}}, {{CommercialDocument.CreatedAtUtc}});

CREATE FUNCTION {{CommercialDocument.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{CommercialDocument.Status}} <> 1 THEN RAISE EXCEPTION 'a commercial document is created as a draft'; END IF;
        IF NEW.{{CommercialDocument.Revision}} <> 1 THEN RAISE EXCEPTION 'a commercial document is created at revision 1'; END IF;
        IF NEW.{{CommercialDocument.SubmittedBy}} IS NOT NULL THEN RAISE EXCEPTION 'a draft has no submitter'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'commercial documents are never removed'; END IF;
    IF NEW.{{CommercialDocument.Id}} IS DISTINCT FROM OLD.{{CommercialDocument.Id}} OR NEW.{{CommercialDocument.DocType}} IS DISTINCT FROM OLD.{{CommercialDocument.DocType}}
       OR NEW.{{CommercialDocument.BranchId}} IS DISTINCT FROM OLD.{{CommercialDocument.BranchId}} OR NEW.{{CommercialDocument.CreatedBy}} IS DISTINCT FROM OLD.{{CommercialDocument.CreatedBy}}
       OR NEW.{{CommercialDocument.CreatedAtUtc}} IS DISTINCT FROM OLD.{{CommercialDocument.CreatedAtUtc}} OR NEW.{{CommercialDocument.IdempotencyKey}} IS DISTINCT FROM OLD.{{CommercialDocument.IdempotencyKey}}
       OR NEW.{{CommercialDocument.PayloadHash}} IS DISTINCT FROM OLD.{{CommercialDocument.PayloadHash}} THEN
        RAISE EXCEPTION 'the identity of a commercial document is immutable';
    END IF;
    IF NEW.{{CommercialDocument.Revision}} <> OLD.{{CommercialDocument.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NOT ((OLD.{{CommercialDocument.Status}} = 1 AND NEW.{{CommercialDocument.Status}} IN (1, 2, 5)) OR (OLD.{{CommercialDocument.Status}} = 2 AND NEW.{{CommercialDocument.Status}} IN (1, 5))) THEN
        RAISE EXCEPTION 'illegal commercial document status transition';
    END IF;
    IF NEW.{{CommercialDocument.Status}} <> OLD.{{CommercialDocument.Status}} AND (NEW.{{CommercialDocument.PartyId}} IS DISTINCT FROM OLD.{{CommercialDocument.PartyId}} OR NEW.{{CommercialDocument.Currency}} IS DISTINCT FROM OLD.{{CommercialDocument.Currency}}
       OR NEW.{{CommercialDocument.DocumentDate}} IS DISTINCT FROM OLD.{{CommercialDocument.DocumentDate}} OR NEW.{{CommercialDocument.Mode}} IS DISTINCT FROM OLD.{{CommercialDocument.Mode}}
       OR NEW.{{CommercialDocument.Note}} IS DISTINCT FROM OLD.{{CommercialDocument.Note}}) THEN
        RAISE EXCEPTION 'a status change does not edit the document';
    END IF;
    IF NEW.{{CommercialDocument.Status}} = 2 AND NEW.{{CommercialDocument.SubmittedBy}} IS NULL THEN RAISE EXCEPTION 'a submitted document names its submitter'; END IF;
    IF NEW.{{CommercialDocument.Status}} = 1 AND NEW.{{CommercialDocument.SubmittedBy}} IS NOT NULL THEN RAISE EXCEPTION 'a draft has no submitter'; END IF;
    IF NEW.{{CommercialDocument.Status}} = 5 AND NEW.{{CommercialDocument.SubmittedBy}} IS DISTINCT FROM OLD.{{CommercialDocument.SubmittedBy}} THEN RAISE EXCEPTION 'cancellation keeps the submitter record'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CommercialDocument.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CommercialDocument}} FOR EACH ROW EXECUTE FUNCTION {{CommercialDocument.FnGuard}}();
CREATE TRIGGER {{CommercialDocument.TrTruncate}} BEFORE TRUNCATE ON {{CommercialDocument}} FOR EACH STATEMENT EXECUTE FUNCTION {{CommercialDocument.FnGuard}}();
REVOKE ALL ON TABLE {{CommercialDocument}} FROM PUBLIC;

CREATE TABLE {{CommercialDocumentLine}} (
    {{CommercialDocumentLine.Id}} uuid NOT NULL,
    {{CommercialDocumentLine.DocumentId}} uuid NOT NULL,
    {{CommercialDocumentLine.LineNo}} integer NOT NULL,
    {{CommercialDocumentLine.ItemId}} uuid NOT NULL,
    {{CommercialDocumentLine.UnitId}} uuid NOT NULL,
    {{CommercialDocumentLine.Quantity}} numeric(28,10) NOT NULL,
    {{CommercialDocumentLine.WarehouseId}} uuid NULL,
    CONSTRAINT {{CommercialDocumentLine.Pk}} PRIMARY KEY ({{CommercialDocumentLine.Id}}),
    CONSTRAINT {{CommercialDocumentLine.FkDocument}} FOREIGN KEY ({{CommercialDocumentLine.DocumentId}}) REFERENCES {{CommercialDocument}} ({{CommercialDocument.Id}}),
    CONSTRAINT {{CommercialDocumentLine.FkItem}} FOREIGN KEY ({{CommercialDocumentLine.ItemId}}) REFERENCES [[Item.Table]] ([[Item.Id]]),
    CONSTRAINT {{CommercialDocumentLine.FkUnit}} FOREIGN KEY ({{CommercialDocumentLine.UnitId}}) REFERENCES [[Unit.Table]] ([[Unit.Id]]),
    CONSTRAINT {{CommercialDocumentLine.FkWarehouse}} FOREIGN KEY ({{CommercialDocumentLine.WarehouseId}}) REFERENCES [[Warehouse.Table]] ([[Warehouse.Id]]),
    CONSTRAINT {{CommercialDocumentLine.CkValues}} CHECK ({{CommercialDocumentLine.LineNo}} BETWEEN 1 AND 5000 AND {{CommercialDocumentLine.Quantity}} > 0 AND {{CommercialDocumentLine.Quantity}} < 1000000000)
);
CREATE UNIQUE INDEX {{CommercialDocumentLine.UqLine}} ON {{CommercialDocumentLine}} ({{CommercialDocumentLine.DocumentId}}, {{CommercialDocumentLine.LineNo}});

CREATE FUNCTION {{CommercialDocumentLine.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE doc uuid; st smallint;
BEGIN
    IF TG_OP = 'DELETE' THEN doc := OLD.{{CommercialDocumentLine.DocumentId}}; ELSE doc := NEW.{{CommercialDocumentLine.DocumentId}}; END IF;
    SELECT d.{{CommercialDocument.Status}} INTO st FROM {{CommercialDocument}} d WHERE d.{{CommercialDocument.Id}} = doc FOR SHARE;   -- a share lock conflicts with a concurrent status UPDATE (FOR NO KEY UPDATE): a line write can never commit beside a submit or cancel
    IF st IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'the lines of a commercial document are editable only while it is a draft'; END IF;
    IF TG_OP = 'UPDATE' AND NEW.{{CommercialDocumentLine.DocumentId}} IS DISTINCT FROM OLD.{{CommercialDocumentLine.DocumentId}} THEN RAISE EXCEPTION 'a line never moves to another document'; END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CommercialDocumentLine.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CommercialDocumentLine}} FOR EACH ROW EXECUTE FUNCTION {{CommercialDocumentLine.FnGuard}}();
CREATE FUNCTION {{CommercialDocumentLine.FnTruncate}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'commercial document lines are never truncated';
END;
$$;
CREATE TRIGGER {{CommercialDocumentLine.TrTruncate}} BEFORE TRUNCATE ON {{CommercialDocumentLine}} FOR EACH STATEMENT EXECUTE FUNCTION {{CommercialDocumentLine.FnTruncate}}();
REVOKE ALL ON TABLE {{CommercialDocumentLine}} FROM PUBLIC;

CREATE TABLE {{CommercialDocumentEvent}} (
    {{CommercialDocumentEvent.Id}} uuid NOT NULL,
    {{CommercialDocumentEvent.DocumentId}} uuid NOT NULL,
    {{CommercialDocumentEvent.Kind}} smallint NOT NULL,
    {{CommercialDocumentEvent.FromStatus}} smallint NULL,
    {{CommercialDocumentEvent.ToStatus}} smallint NOT NULL,
    {{CommercialDocumentEvent.Revision}} bigint NOT NULL,
    {{CommercialDocumentEvent.ActorUserId}} uuid NOT NULL,
    {{CommercialDocumentEvent.Reason}} varchar(500) NULL,
    {{CommercialDocumentEvent.AtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{CommercialDocumentEvent.Pk}} PRIMARY KEY ({{CommercialDocumentEvent.Id}}),
    CONSTRAINT {{CommercialDocumentEvent.FkDocument}} FOREIGN KEY ({{CommercialDocumentEvent.DocumentId}}) REFERENCES {{CommercialDocument}} ({{CommercialDocument.Id}}),
    CONSTRAINT {{CommercialDocumentEvent.CkValues}} CHECK ({{CommercialDocumentEvent.Kind}} BETWEEN 1 AND 9 AND {{CommercialDocumentEvent.ToStatus}} BETWEEN 1 AND 5 AND ({{CommercialDocumentEvent.FromStatus}} IS NULL OR {{CommercialDocumentEvent.FromStatus}} BETWEEN 1 AND 5) AND {{CommercialDocumentEvent.Revision}} > 0)
);
CREATE UNIQUE INDEX {{CommercialDocumentEvent.UqRevision}} ON {{CommercialDocumentEvent}} ({{CommercialDocumentEvent.DocumentId}}, {{CommercialDocumentEvent.Revision}});

CREATE FUNCTION {{CommercialDocumentEvent.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        -- an event describes what ALREADY happened to its document in this transaction: its revision and resulting status must be the document's current ones (no forged or future event)
        IF NOT EXISTS (SELECT 1 FROM {{CommercialDocument}} d WHERE d.{{CommercialDocument.Id}} = NEW.{{CommercialDocumentEvent.DocumentId}} AND d.{{CommercialDocument.Revision}} = NEW.{{CommercialDocumentEvent.Revision}} AND d.{{CommercialDocument.Status}} = NEW.{{CommercialDocumentEvent.ToStatus}}) THEN
            RAISE EXCEPTION 'a commercial document event must carry the current revision and status of its document';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'commercial document events are append-only';
END;
$$;
CREATE TRIGGER {{CommercialDocumentEvent.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CommercialDocumentEvent}} FOR EACH ROW EXECUTE FUNCTION {{CommercialDocumentEvent.FnGuard}}();
CREATE TRIGGER {{CommercialDocumentEvent.TrTruncate}} BEFORE TRUNCATE ON {{CommercialDocumentEvent}} FOR EACH STATEMENT EXECUTE FUNCTION {{CommercialDocumentEvent.FnGuard}}();
REVOKE ALL ON TABLE {{CommercialDocumentEvent}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0028.commercial.v1:" + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (/0029_company_commercial_approval\.sql$|CompanySchema\.CommercialApproval\.cs$|0030_company_document_links\.sql$|CompanySchema\.DocumentLink\.cs$|\d{4}_company_commercial_policies\.sql$|CompanySchema\.CommercialPolicy\.cs$/.test(p)) continue;   // a later migration: it REFERENCES the names of this one
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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0028-commercial.cjs for company migration 0028 (Phase 3.13 slice S1a, commercial documents). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> CommercialMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated commercial-document schema differs from the committed files"); process.exit(1); }
  console.log("commercial-document schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
