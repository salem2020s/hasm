#!/usr/bin/env node
// Deterministic generator for company migration 0031 (Phase R slice R1: retention policy versions and rules, legal holds for REMOVED attachments; decision D-082).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{RetentionPolicy.Version}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.0031.attachment-retention.v1:" + token)
// (same rules as tools/schema/company-0027-customer-groups.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (the Attachment table of 0024) are written
// [[Entity.Name]] and resolved from the committed generated dictionary, never re-derived.
// Usage:  node tools/schema/company-0031-attachment-retention.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "CompanyDatabase", "Migrations", "0031_company_attachment_retention.sql");
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.AttachmentRetention.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Attachments.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration 0031: retention policy and legal holds for removed attachments (Phase R slice R1, owner decision D-082). Forward-only, additive (three new tables); no existing row is read or modified and nothing is deleted.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0031-attachment-retention.cjs (logical dictionary: CompanySchema.AttachmentRetention.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: the company-approved legal retention POLICY (versions, each with rules per record kind and sensitivity) and LEGAL HOLDS that block any later purge. R1 only RECORDS them and previews eligibility; no
-- bytes are deleted by this slice (the purge plan and its tables come with R2). The guarded Attachment table is NOT touched. OWNER-PENDING (D-109): the retention basis (counted from the removal date); while it is
-- pending the eligibility check answers retention_basis_pending for everything.
-- Invariants enforced by PostgreSQL:
--   * policy versions, rules and holds are never deleted or truncated; every change to a version or hold advances the revision by one;
--   * a policy version is created as a DRAFT; it is approved by a user OTHER than its author (a CHECK), then only superseded or revoked; superseded and revoked versions never change again; at most one version is Approved;
--   * rules are immutable and can be added only while their version is a draft; a rule is a finite period (1..1200 months) or Indefinite, never both;
--   * a version that shortens a period carries a non-empty written acknowledgement;
--   * a legal hold covers the company, one record or one attachment (consistent columns); it is released at most once, by a user OTHER than the one who placed it, with a reason; a released hold never changes again.

CREATE TABLE {{RetentionPolicy}} (
    {{RetentionPolicy.Id}} uuid NOT NULL,
    {{RetentionPolicy.Version}} integer NOT NULL,
    {{RetentionPolicy.Status}} smallint NOT NULL,
    {{RetentionPolicy.EffectiveFrom}} date NOT NULL,
    {{RetentionPolicy.ShortensAnyPeriod}} boolean NOT NULL DEFAULT false,
    {{RetentionPolicy.ShorteningAck}} varchar(1000) NULL,
    {{RetentionPolicy.Note}} varchar(500) NULL,
    {{RetentionPolicy.DraftedBy}} uuid NOT NULL,
    {{RetentionPolicy.DraftedAtUtc}} timestamptz NOT NULL,
    {{RetentionPolicy.ApprovedBy}} uuid NULL,
    {{RetentionPolicy.ApprovedAtUtc}} timestamptz NULL,
    {{RetentionPolicy.RevokedBy}} uuid NULL,
    {{RetentionPolicy.RevokedAtUtc}} timestamptz NULL,
    {{RetentionPolicy.RevokeReason}} varchar(500) NULL,
    {{RetentionPolicy.EndedAtUtc}} timestamptz NULL,
    {{RetentionPolicy.Revision}} bigint NOT NULL DEFAULT 1,
    {{RetentionPolicy.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{RetentionPolicy.Pk}} PRIMARY KEY ({{RetentionPolicy.Id}}),
    CONSTRAINT {{RetentionPolicy.CkValues}} CHECK ({{RetentionPolicy.Version}} > 0 AND {{RetentionPolicy.Revision}} > 0 AND {{RetentionPolicy.Status}} BETWEEN 1 AND 4),
    CONSTRAINT {{RetentionPolicy.CkApproval}} CHECK (
        ({{RetentionPolicy.Status}} = 1 AND {{RetentionPolicy.ApprovedBy}} IS NULL AND {{RetentionPolicy.ApprovedAtUtc}} IS NULL)
        OR ({{RetentionPolicy.Status}} > 1 AND {{RetentionPolicy.ApprovedBy}} IS NOT NULL AND {{RetentionPolicy.ApprovedAtUtc}} IS NOT NULL AND {{RetentionPolicy.ApprovedBy}} <> {{RetentionPolicy.DraftedBy}})),
    CONSTRAINT {{RetentionPolicy.CkEnd}} CHECK (
        ({{RetentionPolicy.Status}} IN (1, 2) AND {{RetentionPolicy.EndedAtUtc}} IS NULL)
        OR ({{RetentionPolicy.Status}} IN (3, 4) AND {{RetentionPolicy.EndedAtUtc}} IS NOT NULL)),
    CONSTRAINT {{RetentionPolicy.CkRevoke}} CHECK (
        ({{RetentionPolicy.Status}} = 3 AND {{RetentionPolicy.RevokedBy}} IS NOT NULL AND {{RetentionPolicy.RevokedAtUtc}} IS NOT NULL AND char_length(btrim({{RetentionPolicy.RevokeReason}})) >= 1)
        OR ({{RetentionPolicy.Status}} <> 3 AND {{RetentionPolicy.RevokedBy}} IS NULL AND {{RetentionPolicy.RevokedAtUtc}} IS NULL AND {{RetentionPolicy.RevokeReason}} IS NULL)),
    CONSTRAINT {{RetentionPolicy.CkShortening}} CHECK ({{RetentionPolicy.Status}} = 1 OR NOT {{RetentionPolicy.ShortensAnyPeriod}} OR char_length(btrim({{RetentionPolicy.ShorteningAck}})) >= 1)
);
CREATE UNIQUE INDEX {{RetentionPolicy.UqVersion}} ON {{RetentionPolicy}} ({{RetentionPolicy.Version}});
CREATE UNIQUE INDEX {{RetentionPolicy.UqApproved}} ON {{RetentionPolicy}} ({{RetentionPolicy.Status}}) WHERE {{RetentionPolicy.Status}} = 2;

CREATE FUNCTION {{RetentionPolicy.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{RetentionPolicy.Revision}} <> 1 OR NEW.{{RetentionPolicy.Status}} <> 1 THEN RAISE EXCEPTION 'a retention policy version is created as a draft at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'retention policy versions are never removed'; END IF;
    IF NEW.{{RetentionPolicy.Id}} IS DISTINCT FROM OLD.{{RetentionPolicy.Id}} OR NEW.{{RetentionPolicy.Version}} IS DISTINCT FROM OLD.{{RetentionPolicy.Version}}
       OR NEW.{{RetentionPolicy.EffectiveFrom}} IS DISTINCT FROM OLD.{{RetentionPolicy.EffectiveFrom}} OR NEW.{{RetentionPolicy.Note}} IS DISTINCT FROM OLD.{{RetentionPolicy.Note}}
       OR NEW.{{RetentionPolicy.DraftedBy}} IS DISTINCT FROM OLD.{{RetentionPolicy.DraftedBy}} OR NEW.{{RetentionPolicy.DraftedAtUtc}} IS DISTINCT FROM OLD.{{RetentionPolicy.DraftedAtUtc}} THEN
        RAISE EXCEPTION 'the identity, version, effective date, note and draft data of a retention policy version are immutable';
    END IF;
    IF OLD.{{RetentionPolicy.Status}} IN (3, 4) THEN RAISE EXCEPTION 'a revoked or superseded retention policy version never changes again'; END IF;
    IF NOT ((OLD.{{RetentionPolicy.Status}} = 1 AND NEW.{{RetentionPolicy.Status}} = 2) OR (OLD.{{RetentionPolicy.Status}} = 2 AND NEW.{{RetentionPolicy.Status}} IN (3, 4))) THEN
        RAISE EXCEPTION 'a retention policy version moves only draft -> approved -> revoked or superseded';
    END IF;
    IF OLD.{{RetentionPolicy.Status}} = 2 AND (NEW.{{RetentionPolicy.ApprovedBy}} IS DISTINCT FROM OLD.{{RetentionPolicy.ApprovedBy}} OR NEW.{{RetentionPolicy.ApprovedAtUtc}} IS DISTINCT FROM OLD.{{RetentionPolicy.ApprovedAtUtc}}
       OR NEW.{{RetentionPolicy.ShortensAnyPeriod}} IS DISTINCT FROM OLD.{{RetentionPolicy.ShortensAnyPeriod}} OR NEW.{{RetentionPolicy.ShorteningAck}} IS DISTINCT FROM OLD.{{RetentionPolicy.ShorteningAck}}) THEN
        RAISE EXCEPTION 'the approval data of a retention policy version never changes';
    END IF;
    IF NEW.{{RetentionPolicy.Revision}} <> OLD.{{RetentionPolicy.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{RetentionPolicy.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{RetentionPolicy}} FOR EACH ROW EXECUTE FUNCTION {{RetentionPolicy.FnGuard}}();
CREATE TRIGGER {{RetentionPolicy.TrTruncate}} BEFORE TRUNCATE ON {{RetentionPolicy}} FOR EACH STATEMENT EXECUTE FUNCTION {{RetentionPolicy.FnGuard}}();
REVOKE ALL ON TABLE {{RetentionPolicy}} FROM PUBLIC;

CREATE TABLE {{RetentionRule}} (
    {{RetentionRule.Id}} uuid NOT NULL,
    {{RetentionRule.PolicyId}} uuid NOT NULL,
    {{RetentionRule.SubjectKind}} varchar(40) NOT NULL,
    {{RetentionRule.Sensitivity}} smallint NOT NULL,
    {{RetentionRule.Months}} integer NULL,
    {{RetentionRule.Indefinite}} boolean NOT NULL,
    CONSTRAINT {{RetentionRule.Pk}} PRIMARY KEY ({{RetentionRule.Id}}),
    CONSTRAINT {{RetentionRule.FkPolicy}} FOREIGN KEY ({{RetentionRule.PolicyId}}) REFERENCES {{RetentionPolicy}} ({{RetentionPolicy.Id}}),
    CONSTRAINT {{RetentionRule.CkValues}} CHECK (
        {{RetentionRule.Sensitivity}} BETWEEN 0 AND 2
        AND ({{RetentionRule.SubjectKind}} = '*' OR {{RetentionRule.SubjectKind}} ~ '^[a-z][a-z0-9_]{0,39}$')
        AND (({{RetentionRule.Indefinite}} AND {{RetentionRule.Months}} IS NULL) OR (NOT {{RetentionRule.Indefinite}} AND {{RetentionRule.Months}} BETWEEN 1 AND 1200)))
);
CREATE UNIQUE INDEX {{RetentionRule.UqKey}} ON {{RetentionRule}} ({{RetentionRule.PolicyId}}, {{RetentionRule.SubjectKind}}, {{RetentionRule.Sensitivity}});

CREATE FUNCTION {{RetentionRule.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF (SELECT {{RetentionPolicy.Status}} FROM {{RetentionPolicy}} WHERE {{RetentionPolicy.Id}} = NEW.{{RetentionRule.PolicyId}}) IS DISTINCT FROM 1 THEN
            RAISE EXCEPTION 'rules can be added only to a draft retention policy version';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'retention rules are immutable and never removed';
END;
$$;
CREATE TRIGGER {{RetentionRule.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{RetentionRule}} FOR EACH ROW EXECUTE FUNCTION {{RetentionRule.FnGuard}}();
CREATE TRIGGER {{RetentionRule.TrTruncate}} BEFORE TRUNCATE ON {{RetentionRule}} FOR EACH STATEMENT EXECUTE FUNCTION {{RetentionRule.FnGuard}}();
REVOKE ALL ON TABLE {{RetentionRule}} FROM PUBLIC;

CREATE TABLE {{LegalHold}} (
    {{LegalHold.Id}} uuid NOT NULL,
    {{LegalHold.Scope}} smallint NOT NULL,
    {{LegalHold.SubjectKind}} varchar(40) NULL,
    {{LegalHold.SubjectId}} uuid NULL,
    {{LegalHold.AttachmentId}} uuid NULL,
    {{LegalHold.Reason}} varchar(500) NOT NULL,
    {{LegalHold.Reference}} varchar(100) NULL,
    {{LegalHold.PlacedBy}} uuid NOT NULL,
    {{LegalHold.PlacedAtUtc}} timestamptz NOT NULL,
    {{LegalHold.ReleasedBy}} uuid NULL,
    {{LegalHold.ReleasedAtUtc}} timestamptz NULL,
    {{LegalHold.ReleaseReason}} varchar(500) NULL,
    {{LegalHold.Revision}} bigint NOT NULL DEFAULT 1,
    {{LegalHold.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{LegalHold.Pk}} PRIMARY KEY ({{LegalHold.Id}}),
    CONSTRAINT {{LegalHold.FkAttachment}} FOREIGN KEY ({{LegalHold.AttachmentId}}) REFERENCES [[Attachment.Table]] ([[Attachment.Id]]),
    CONSTRAINT {{LegalHold.CkValues}} CHECK ({{LegalHold.Revision}} > 0 AND char_length(btrim({{LegalHold.Reason}})) >= 1),
    CONSTRAINT {{LegalHold.CkScope}} CHECK (
        ({{LegalHold.Scope}} = 1 AND {{LegalHold.SubjectKind}} IS NULL AND {{LegalHold.SubjectId}} IS NULL AND {{LegalHold.AttachmentId}} IS NULL)
        OR ({{LegalHold.Scope}} = 2 AND {{LegalHold.SubjectKind}} IS NOT NULL AND {{LegalHold.SubjectId}} IS NOT NULL AND {{LegalHold.AttachmentId}} IS NULL)
        OR ({{LegalHold.Scope}} = 3 AND {{LegalHold.AttachmentId}} IS NOT NULL AND {{LegalHold.SubjectKind}} IS NULL AND {{LegalHold.SubjectId}} IS NULL)),
    CONSTRAINT {{LegalHold.CkRelease}} CHECK (
        ({{LegalHold.ReleasedBy}} IS NULL AND {{LegalHold.ReleasedAtUtc}} IS NULL AND {{LegalHold.ReleaseReason}} IS NULL)
        OR ({{LegalHold.ReleasedBy}} IS NOT NULL AND {{LegalHold.ReleasedAtUtc}} IS NOT NULL AND char_length(btrim({{LegalHold.ReleaseReason}})) >= 1 AND {{LegalHold.ReleasedBy}} <> {{LegalHold.PlacedBy}}))
);
CREATE INDEX {{LegalHold.IxRecord}} ON {{LegalHold}} ({{LegalHold.SubjectKind}}, {{LegalHold.SubjectId}}) WHERE {{LegalHold.ReleasedAtUtc}} IS NULL AND {{LegalHold.Scope}} = 2;
CREATE INDEX {{LegalHold.IxAttachment}} ON {{LegalHold}} ({{LegalHold.AttachmentId}}) WHERE {{LegalHold.AttachmentId}} IS NOT NULL;

CREATE FUNCTION {{LegalHold.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{LegalHold.Revision}} <> 1 OR NEW.{{LegalHold.ReleasedAtUtc}} IS NOT NULL THEN RAISE EXCEPTION 'a legal hold is placed at revision 1 and is not yet released'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'legal holds are never removed'; END IF;
    IF OLD.{{LegalHold.ReleasedAtUtc}} IS NOT NULL THEN RAISE EXCEPTION 'a released legal hold never changes again'; END IF;
    IF NEW.{{LegalHold.Id}} IS DISTINCT FROM OLD.{{LegalHold.Id}} OR NEW.{{LegalHold.Scope}} IS DISTINCT FROM OLD.{{LegalHold.Scope}} OR NEW.{{LegalHold.SubjectKind}} IS DISTINCT FROM OLD.{{LegalHold.SubjectKind}}
       OR NEW.{{LegalHold.SubjectId}} IS DISTINCT FROM OLD.{{LegalHold.SubjectId}} OR NEW.{{LegalHold.AttachmentId}} IS DISTINCT FROM OLD.{{LegalHold.AttachmentId}}
       OR NEW.{{LegalHold.Reason}} IS DISTINCT FROM OLD.{{LegalHold.Reason}} OR NEW.{{LegalHold.Reference}} IS DISTINCT FROM OLD.{{LegalHold.Reference}}
       OR NEW.{{LegalHold.PlacedBy}} IS DISTINCT FROM OLD.{{LegalHold.PlacedBy}} OR NEW.{{LegalHold.PlacedAtUtc}} IS DISTINCT FROM OLD.{{LegalHold.PlacedAtUtc}} THEN
        RAISE EXCEPTION 'a legal hold changes only by its release';
    END IF;
    IF NEW.{{LegalHold.ReleasedAtUtc}} IS NULL THEN RAISE EXCEPTION 'the only change of a legal hold is its release'; END IF;
    IF NEW.{{LegalHold.Revision}} <> OLD.{{LegalHold.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{LegalHold.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{LegalHold}} FOR EACH ROW EXECUTE FUNCTION {{LegalHold.FnGuard}}();
CREATE TRIGGER {{LegalHold.TrTruncate}} BEFORE TRUNCATE ON {{LegalHold}} FOR EACH STATEMENT EXECUTE FUNCTION {{LegalHold.FnGuard}}();
REVOKE ALL ON TABLE {{LegalHold}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company.0031.attachment-retention.v1:" + token).digest("hex").slice(0, 8);

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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0031-attachment-retention.cjs for company migration 0031 (Phase R slice R1, attachment retention policy and legal holds). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> AttachmentRetentionMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated attachment-retention schema differs from the committed files"); process.exit(1); }
  console.log("attachment-retention schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
