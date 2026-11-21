#!/usr/bin/env node
// Deterministic generator for company migration 0029 (Phase 3.13 slice S1b: commercial document approval, numbering and server-produced snapshots).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CommercialDocument.DocType}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0029.commercial-approval.v1:" + token)
// (same rules as tools/schema/company-0026-pricing.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migration 0026 and 0021/0023) are written
// [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0029-commercial-approval.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0029_company_commercial_approval.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.CommercialApproval.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Commercial.cs"), ...parseDictionary("CompanySchema.Fiscal.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration 0029: commercial document approval, numbering and server-produced snapshots (Phase 3.13 slice S1b). Forward-only: additive nullable columns on the two document tables, one new index, and CREATE OR REPLACE of
-- the three guard functions of migration 0028 (their names are kept; no object is dropped and no generated name is reused for a different object). No existing row is read or modified, nothing is seeded.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0029-commercial-approval.cjs (logical dictionary: CompanySchema.CommercialApproval.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS ADDS (docs/PHASE-3.13.md S1b; owner decisions D-049..D-052): the official number (allocated at the FIRST approval, kept through re-approval, voided and never reused on cancel), the approval record, the exchange rate,
-- the submit fingerprint and the price/tax snapshots (produced by the server; sealed at approval), plus the full commercial-status guard: draft -> submitted -> approved or rejected, re-approval (approved -> draft), reopening
-- (rejected -> draft), and the cancellation of an approved document. Physical stock and accounting effects are later slices: the guard knows nothing of them yet.
-- Invariants enforced by PostgreSQL:
--   * an approved document has its number, approver, seal and fingerprint; the approver is never the creator, the last editor or the submitter; the sealed fingerprint equals the one produced at submit (a changed price is refused);
--   * the date of a numbered document stays inside the fiscal year of its number; the quote of a submitted document is frozen when it is decided (approved, rejected or cancelled);
--   * the official number is set ONLY by the approval transition and never changes or disappears afterwards; the mode never changes once a number exists; an approved document changes only its note (anything material goes back to draft);
--   * a draft carries no submitter, approval, seal, fingerprint or snapshot (they are produced at submit and approval);
--   * every event must describe a transition the guard allows (kind, from-status and to-status), continue the status of the previous event and carry the document's current revision and status.

ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.OfficialNumber}} varchar(60) NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.FiscalYearId}} uuid NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.ApprovedBy}} uuid NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.ApprovedAtUtc}} timestamptz NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.SealedAtUtc}} timestamptz NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.LastEditedBy}} uuid NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.ExchangeRate}} numeric(28,12) NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.ExchangeRateRef}} varchar(60) NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.Fingerprint}} char(64) NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.TaxSnapshot}} text NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.TaxHash}} char(64) NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.TotalNet}} numeric(28,6) NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.TotalTax}} numeric(28,6) NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD COLUMN {{CommercialDocumentApproval.TotalGross}} numeric(28,6) NULL;
ALTER TABLE [[CommercialDocument.Table]] ADD CONSTRAINT {{CommercialDocumentApproval.FkYear}} FOREIGN KEY ({{CommercialDocumentApproval.FiscalYearId}}) REFERENCES [[FiscalYear.Table]] ([[FiscalYear.Id]]);
ALTER TABLE [[CommercialDocument.Table]] ADD CONSTRAINT {{CommercialDocumentApproval.CkApproval}} CHECK ([[CommercialDocument.Status]] <> 3 OR ({{CommercialDocumentApproval.OfficialNumber}} IS NOT NULL AND {{CommercialDocumentApproval.ApprovedBy}} IS NOT NULL
    AND {{CommercialDocumentApproval.ApprovedAtUtc}} IS NOT NULL AND {{CommercialDocumentApproval.SealedAtUtc}} IS NOT NULL AND {{CommercialDocumentApproval.Fingerprint}} IS NOT NULL AND {{CommercialDocumentApproval.FiscalYearId}} IS NOT NULL));
ALTER TABLE [[CommercialDocument.Table]] ADD CONSTRAINT {{CommercialDocumentApproval.CkSnapshot}} CHECK ({{CommercialDocumentApproval.Fingerprint}} IS NULL OR ({{CommercialDocumentApproval.TaxHash}} ~ '^[0-9a-f]{64}$' AND {{CommercialDocumentApproval.TaxSnapshot}} IS NOT NULL
    AND {{CommercialDocumentApproval.ExchangeRate}} IS NOT NULL AND {{CommercialDocumentApproval.ExchangeRate}} > 0 AND {{CommercialDocumentApproval.TotalNet}} IS NOT NULL AND {{CommercialDocumentApproval.TotalTax}} IS NOT NULL AND {{CommercialDocumentApproval.TotalGross}} IS NOT NULL));
CREATE UNIQUE INDEX {{CommercialDocumentApproval.UqNumber}} ON [[CommercialDocument.Table]] ([[CommercialDocument.DocType]], {{CommercialDocumentApproval.OfficialNumber}}) WHERE {{CommercialDocumentApproval.OfficialNumber}} IS NOT NULL;

ALTER TABLE [[CommercialDocumentLine.Table]] ADD COLUMN {{CommercialDocumentSnapshot.PricingSnapshot}} text NULL;
ALTER TABLE [[CommercialDocumentLine.Table]] ADD COLUMN {{CommercialDocumentSnapshot.PricingHash}} char(64) NULL;
ALTER TABLE [[CommercialDocumentLine.Table]] ADD COLUMN {{CommercialDocumentSnapshot.LineNet}} numeric(28,6) NULL;
ALTER TABLE [[CommercialDocumentLine.Table]] ADD CONSTRAINT {{CommercialDocumentSnapshot.CkSnapshot}} CHECK (({{CommercialDocumentSnapshot.PricingSnapshot}} IS NULL) = ({{CommercialDocumentSnapshot.PricingHash}} IS NULL) AND ({{CommercialDocumentSnapshot.PricingHash}} IS NULL) = ({{CommercialDocumentSnapshot.LineNet}} IS NULL)
    AND ({{CommercialDocumentSnapshot.PricingHash}} IS NULL OR {{CommercialDocumentSnapshot.PricingHash}} ~ '^[0-9a-f]{64}$'));

CREATE OR REPLACE FUNCTION [[CommercialDocument.FnGuard]]() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.[[CommercialDocument.Status]] <> 1 THEN RAISE EXCEPTION 'a commercial document is created as a draft'; END IF;
        IF NEW.[[CommercialDocument.Revision]] <> 1 THEN RAISE EXCEPTION 'a commercial document is created at revision 1'; END IF;
        IF NEW.[[CommercialDocument.SubmittedBy]] IS NOT NULL OR NEW.{{CommercialDocumentApproval.OfficialNumber}} IS NOT NULL OR NEW.{{CommercialDocumentApproval.ApprovedBy}} IS NOT NULL
           OR NEW.{{CommercialDocumentApproval.SealedAtUtc}} IS NOT NULL OR NEW.{{CommercialDocumentApproval.Fingerprint}} IS NOT NULL THEN RAISE EXCEPTION 'a draft carries no submitter, number, approval or seal'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'commercial documents are never removed'; END IF;
    IF NEW.[[CommercialDocument.Id]] IS DISTINCT FROM OLD.[[CommercialDocument.Id]] OR NEW.[[CommercialDocument.DocType]] IS DISTINCT FROM OLD.[[CommercialDocument.DocType]]
       OR NEW.[[CommercialDocument.BranchId]] IS DISTINCT FROM OLD.[[CommercialDocument.BranchId]] OR NEW.[[CommercialDocument.CreatedBy]] IS DISTINCT FROM OLD.[[CommercialDocument.CreatedBy]]
       OR NEW.[[CommercialDocument.CreatedAtUtc]] IS DISTINCT FROM OLD.[[CommercialDocument.CreatedAtUtc]] OR NEW.[[CommercialDocument.IdempotencyKey]] IS DISTINCT FROM OLD.[[CommercialDocument.IdempotencyKey]]
       OR NEW.[[CommercialDocument.PayloadHash]] IS DISTINCT FROM OLD.[[CommercialDocument.PayloadHash]] THEN
        RAISE EXCEPTION 'the identity of a commercial document is immutable';
    END IF;
    IF NEW.[[CommercialDocument.Revision]] <> OLD.[[CommercialDocument.Revision]] + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NOT ((OLD.[[CommercialDocument.Status]] = 1 AND NEW.[[CommercialDocument.Status]] IN (1, 2, 5)) OR (OLD.[[CommercialDocument.Status]] = 2 AND NEW.[[CommercialDocument.Status]] IN (1, 3, 4, 5))
            OR (OLD.[[CommercialDocument.Status]] = 3 AND NEW.[[CommercialDocument.Status]] IN (1, 3, 5)) OR (OLD.[[CommercialDocument.Status]] = 4 AND NEW.[[CommercialDocument.Status]] IN (1, 5))) THEN
        RAISE EXCEPTION 'illegal commercial document status transition';
    END IF;
    -- the number: set ONLY by the approval, never changed or removed afterwards; the fiscal year it was issued in goes with it; the mode is frozen once numbered
    IF NEW.{{CommercialDocumentApproval.OfficialNumber}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.OfficialNumber}} THEN
        IF OLD.{{CommercialDocumentApproval.OfficialNumber}} IS NOT NULL THEN RAISE EXCEPTION 'the official number never changes'; END IF;
        IF NOT (OLD.[[CommercialDocument.Status]] = 2 AND NEW.[[CommercialDocument.Status]] = 3) THEN RAISE EXCEPTION 'the official number is issued only by the approval'; END IF;
    END IF;
    IF OLD.{{CommercialDocumentApproval.OfficialNumber}} IS NOT NULL AND (NEW.{{CommercialDocumentApproval.FiscalYearId}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.FiscalYearId}}
       OR NEW.[[CommercialDocument.Mode]] IS DISTINCT FROM OLD.[[CommercialDocument.Mode]]) THEN RAISE EXCEPTION 'the fiscal year of the number and the mode are frozen once the document is numbered'; END IF;
    -- the date of a NUMBERED document stays inside the fiscal year of its number (D-068): a date outside it would leave the number in a year the document no longer belongs to
    IF NEW.{{CommercialDocumentApproval.FiscalYearId}} IS NOT NULL AND (NEW.[[CommercialDocument.DocumentDate]] IS DISTINCT FROM OLD.[[CommercialDocument.DocumentDate]] OR NEW.[[CommercialDocument.Status]] = 3)
       AND NOT EXISTS (SELECT 1 FROM [[FiscalYear.Table]] y WHERE y.[[FiscalYear.Id]] = NEW.{{CommercialDocumentApproval.FiscalYearId}} AND NEW.[[CommercialDocument.DocumentDate]] BETWEEN y.[[FiscalYear.StartDate]] AND y.[[FiscalYear.EndDate]]) THEN
        RAISE EXCEPTION 'the date of a numbered document stays within the fiscal year of its number';
    END IF;
    -- the quote of a SUBMITTED document is frozen when it is decided: approval, rejection or cancellation never rewrite the sealed content
    IF OLD.[[CommercialDocument.Status]] = 2 AND NEW.[[CommercialDocument.Status]] IN (3, 4, 5) AND (NEW.{{CommercialDocumentApproval.TaxSnapshot}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TaxSnapshot}} OR NEW.{{CommercialDocumentApproval.TaxHash}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TaxHash}}
       OR NEW.{{CommercialDocumentApproval.ExchangeRate}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.ExchangeRate}} OR NEW.{{CommercialDocumentApproval.ExchangeRateRef}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.ExchangeRateRef}}
       OR NEW.{{CommercialDocumentApproval.TotalNet}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TotalNet}} OR NEW.{{CommercialDocumentApproval.TotalTax}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TotalTax}}
       OR NEW.{{CommercialDocumentApproval.TotalGross}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TotalGross}} OR NEW.{{CommercialDocumentApproval.Fingerprint}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.Fingerprint}}) THEN
        RAISE EXCEPTION 'the quote of a submitted document is frozen when it is decided';
    END IF;
    IF NEW.[[CommercialDocument.Status]] <> OLD.[[CommercialDocument.Status]] AND (NEW.[[CommercialDocument.PartyId]] IS DISTINCT FROM OLD.[[CommercialDocument.PartyId]] OR NEW.[[CommercialDocument.Currency]] IS DISTINCT FROM OLD.[[CommercialDocument.Currency]]
       OR NEW.[[CommercialDocument.DocumentDate]] IS DISTINCT FROM OLD.[[CommercialDocument.DocumentDate]] OR NEW.[[CommercialDocument.Mode]] IS DISTINCT FROM OLD.[[CommercialDocument.Mode]]
       OR NEW.[[CommercialDocument.Note]] IS DISTINCT FROM OLD.[[CommercialDocument.Note]]) THEN
        RAISE EXCEPTION 'a status change does not edit the document';
    END IF;
    IF OLD.[[CommercialDocument.Status]] = 3 AND NEW.[[CommercialDocument.Status]] = 3 AND (NEW.[[CommercialDocument.PartyId]] IS DISTINCT FROM OLD.[[CommercialDocument.PartyId]] OR NEW.[[CommercialDocument.Currency]] IS DISTINCT FROM OLD.[[CommercialDocument.Currency]]
       OR NEW.[[CommercialDocument.DocumentDate]] IS DISTINCT FROM OLD.[[CommercialDocument.DocumentDate]] OR NEW.[[CommercialDocument.Mode]] IS DISTINCT FROM OLD.[[CommercialDocument.Mode]]
       OR NEW.{{CommercialDocumentApproval.ApprovedBy}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.ApprovedBy}} OR NEW.{{CommercialDocumentApproval.SealedAtUtc}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.SealedAtUtc}}
       OR NEW.{{CommercialDocumentApproval.Fingerprint}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.Fingerprint}} OR NEW.{{CommercialDocumentApproval.TaxHash}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TaxHash}}
       OR NEW.{{CommercialDocumentApproval.TaxSnapshot}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TaxSnapshot}} OR NEW.{{CommercialDocumentApproval.ApprovedAtUtc}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.ApprovedAtUtc}}
       OR NEW.{{CommercialDocumentApproval.ExchangeRate}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.ExchangeRate}} OR NEW.{{CommercialDocumentApproval.ExchangeRateRef}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.ExchangeRateRef}}
       OR NEW.{{CommercialDocumentApproval.TotalNet}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TotalNet}} OR NEW.{{CommercialDocumentApproval.TotalTax}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TotalTax}}
       OR NEW.{{CommercialDocumentApproval.TotalGross}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.TotalGross}}) THEN
        RAISE EXCEPTION 'an approved document changes only its note';
    END IF;
    -- submit: the submitter and the fingerprint of the server-produced quote are recorded
    IF NEW.[[CommercialDocument.Status]] = 2 AND OLD.[[CommercialDocument.Status]] = 1 AND (NEW.[[CommercialDocument.SubmittedBy]] IS NULL OR NEW.{{CommercialDocumentApproval.Fingerprint}} IS NULL) THEN RAISE EXCEPTION 'a submitted document names its submitter and carries its quote fingerprint'; END IF;
    -- approve: independent approver, the number, the seal, and the sealed fingerprint EQUALS the submitted one (a changed price is refused)
    IF NEW.[[CommercialDocument.Status]] = 3 AND OLD.[[CommercialDocument.Status]] = 2 THEN
        IF NEW.{{CommercialDocumentApproval.ApprovedBy}} IS NULL OR NEW.{{CommercialDocumentApproval.ApprovedBy}} = NEW.[[CommercialDocument.SubmittedBy]] OR NEW.{{CommercialDocumentApproval.ApprovedBy}} = NEW.[[CommercialDocument.CreatedBy]]
           OR NEW.{{CommercialDocumentApproval.ApprovedBy}} = NEW.{{CommercialDocumentApproval.LastEditedBy}} THEN RAISE EXCEPTION 'the approver must be independent of the creator, the last editor and the submitter'; END IF;
        IF NEW.{{CommercialDocumentApproval.OfficialNumber}} IS NULL OR NEW.{{CommercialDocumentApproval.SealedAtUtc}} IS NULL OR NEW.{{CommercialDocumentApproval.Fingerprint}} IS DISTINCT FROM OLD.{{CommercialDocumentApproval.Fingerprint}} THEN
            RAISE EXCEPTION 'an approval issues the number and seals the fingerprint that was submitted'; END IF;
    END IF;
    IF NEW.[[CommercialDocument.Status]] = 1 AND (NEW.[[CommercialDocument.SubmittedBy]] IS NOT NULL OR NEW.{{CommercialDocumentApproval.ApprovedBy}} IS NOT NULL OR NEW.{{CommercialDocumentApproval.SealedAtUtc}} IS NOT NULL
       OR NEW.{{CommercialDocumentApproval.Fingerprint}} IS NOT NULL OR NEW.{{CommercialDocumentApproval.TaxHash}} IS NOT NULL) THEN RAISE EXCEPTION 'a draft carries no submitter, approval, seal or snapshot'; END IF;
    IF NEW.[[CommercialDocument.Status]] = 5 AND NEW.[[CommercialDocument.SubmittedBy]] IS DISTINCT FROM OLD.[[CommercialDocument.SubmittedBy]] THEN RAISE EXCEPTION 'cancellation keeps the submitter record'; END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION [[CommercialDocumentEvent.FnGuard]]() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        -- an event describes what ALREADY happened to its document in this transaction: its revision and resulting status must be the document's current ones (no forged or future event)
        IF NOT EXISTS (SELECT 1 FROM [[CommercialDocument.Table]] d WHERE d.[[CommercialDocument.Id]] = NEW.[[CommercialDocumentEvent.DocumentId]] AND d.[[CommercialDocument.Revision]] = NEW.[[CommercialDocumentEvent.Revision]] AND d.[[CommercialDocument.Status]] = NEW.[[CommercialDocumentEvent.ToStatus]]) THEN
            RAISE EXCEPTION 'a commercial document event must carry the current revision and status of its document';
        END IF;
        -- revision 1 is the creation (and only the creation); the event kind, from-status and to-status must be one of the transitions the document guard allows
        IF (NEW.[[CommercialDocumentEvent.Revision]] = 1) <> (NEW.[[CommercialDocumentEvent.Kind]] = 1) THEN RAISE EXCEPTION 'only the creation is the first event of a commercial document'; END IF;
        IF NOT ((NEW.[[CommercialDocumentEvent.Kind]] = 1 AND NEW.[[CommercialDocumentEvent.FromStatus]] IS NULL AND NEW.[[CommercialDocumentEvent.ToStatus]] = 1 AND NEW.[[CommercialDocumentEvent.Revision]] = 1)
             OR (NEW.[[CommercialDocumentEvent.Kind]] = 2 AND NEW.[[CommercialDocumentEvent.FromStatus]] = NEW.[[CommercialDocumentEvent.ToStatus]] AND NEW.[[CommercialDocumentEvent.ToStatus]] IN (1, 3))
             OR (NEW.[[CommercialDocumentEvent.Kind]] = 3 AND NEW.[[CommercialDocumentEvent.FromStatus]] = 1 AND NEW.[[CommercialDocumentEvent.ToStatus]] = 2)
             OR (NEW.[[CommercialDocumentEvent.Kind]] = 4 AND NEW.[[CommercialDocumentEvent.FromStatus]] = 2 AND NEW.[[CommercialDocumentEvent.ToStatus]] = 1)
             OR (NEW.[[CommercialDocumentEvent.Kind]] = 5 AND NEW.[[CommercialDocumentEvent.FromStatus]] IN (1, 2, 3, 4) AND NEW.[[CommercialDocumentEvent.ToStatus]] = 5)
             OR (NEW.[[CommercialDocumentEvent.Kind]] = 6 AND NEW.[[CommercialDocumentEvent.FromStatus]] = 2 AND NEW.[[CommercialDocumentEvent.ToStatus]] = 3)
             OR (NEW.[[CommercialDocumentEvent.Kind]] = 7 AND NEW.[[CommercialDocumentEvent.FromStatus]] = 2 AND NEW.[[CommercialDocumentEvent.ToStatus]] = 4)
             OR (NEW.[[CommercialDocumentEvent.Kind]] = 8 AND NEW.[[CommercialDocumentEvent.FromStatus]] = 3 AND NEW.[[CommercialDocumentEvent.ToStatus]] = 1)
             OR (NEW.[[CommercialDocumentEvent.Kind]] = 9 AND NEW.[[CommercialDocumentEvent.FromStatus]] = 4 AND NEW.[[CommercialDocumentEvent.ToStatus]] = 1)) THEN
            RAISE EXCEPTION 'a commercial document event kind does not match its transition';
        END IF;
        -- an event continues the status the previous event left
        IF NEW.[[CommercialDocumentEvent.Revision]] > 1 AND NOT EXISTS (SELECT 1 FROM [[CommercialDocumentEvent.Table]] e WHERE e.[[CommercialDocumentEvent.DocumentId]] = NEW.[[CommercialDocumentEvent.DocumentId]]
           AND e.[[CommercialDocumentEvent.Revision]] = NEW.[[CommercialDocumentEvent.Revision]] - 1 AND e.[[CommercialDocumentEvent.ToStatus]] = NEW.[[CommercialDocumentEvent.FromStatus]]) THEN
            RAISE EXCEPTION 'a commercial document event must continue the status of the previous event';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'commercial document events are append-only';
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
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0029.commercial-approval.v1:" + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (/\d{4}_company_commercial_policies\.sql$|CompanySchema\.CommercialPolicy\.cs$|0030_company_document_links\.sql$|CompanySchema\.DocumentLink\.cs$/.test(p)) continue;   // later migrations REFERENCE the names of this one
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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0029-commercial.cjs for company migration 0029 (Phase 3.13 slice S1b, commercial approval). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// CommercialDocumentApproval and CommercialDocumentSnapshot name the NEW columns and constraints added to the 0028 document and line tables (they have no table of their own).\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> CommercialApprovalMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated commercial-approval schema differs from the committed files"); process.exit(1); }
  console.log("commercial-approval schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
