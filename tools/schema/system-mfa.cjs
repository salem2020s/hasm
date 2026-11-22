#!/usr/bin/env node
// Deterministic generator for the SYSTEM migration of Phase 4.1 slice S1 (MFA state on the user row, the TOTP secret store, the second-factor throttle scope).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{UserTotp.Cipher}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.system.mfa-identity.v1:" + token)
// The seed deliberately does NOT contain the migration number: the number is assigned when the slice merges (the next free system number), so the names never change when the file is renumbered.
// (same prefix rules as tools/schema/company-0030-document-links.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist are written [[Entity.Name]]
// and resolved from the committed system dictionaries, never re-derived.
// Usage:  node tools/schema/system-mfa.cjs --write | --check
// RENUMBERING at merge: change MIGRATION_FILE below, re-run --write, and update the pinned migration counts in the tests (the journal count assertions).
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const MIGRATION_FILE = "0031_mfa_state_and_totp.sql";   // assigned by the lead (0030 is R1, 0032 is #134, 0033 is the S2 login migration)
const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const outSql = path.join(infra, "SystemDatabase", "Migrations", MIGRATION_FILE);
const outCs = path.join(infra, "SystemDatabase", "SystemSchema.Mfa.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "SystemDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("SystemSchema.cs"), ...parseDictionary("SystemSchema.Parties.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration (system): MFA state and TOTP secrets (Phase 4.1 slice S1, docs/PHASE-4.1.md section 4, decisions D-080, D-096..D-107, D-120). Forward-only, additive: five columns and two constraints on the user table, ONE
-- new table; nothing existing is dropped or replaced (the second-factor failure counters reuse the login throttle table with domain-separated key hashes). Opaque physical names (D-029) are generated deterministically by
-- tools/schema/system-mfa.cjs (logical dictionary: SystemSchema.Mfa.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: the MFA requirement and enrolment STATE lives on the user row (the user row is in the system dump, so a restore never loses the fact that a user is enrolled): mfa_state 0 None, 1 EnrolmentRequired,
-- 2 Enrolled, 3 RecoveryRequired. Only the SECRET goes to the new secret store, which the backup run excludes (SystemSchema.SecretStoreTables, D-120). A user whose row says Enrolled but who has no stored secret is
-- RecoveryRequired and returns only through recovery or the DR procedure, never by enrolling with a password.

ALTER TABLE [[User.Table]]
    ADD COLUMN {{UserMfa.State}} smallint NOT NULL DEFAULT 0,
    ADD COLUMN {{UserMfa.StateChangedAtUtc}} timestamptz NULL,
    ADD COLUMN {{UserMfa.EnrolmentCodeHash}} bytea NULL,
    ADD COLUMN {{UserMfa.EnrolmentCodeExpiresAtUtc}} timestamptz NULL,
    ADD COLUMN {{UserMfa.EnrolmentCodeConsumedAtUtc}} timestamptz NULL;
ALTER TABLE [[User.Table]] ADD CONSTRAINT {{UserMfa.CkState}} CHECK ({{UserMfa.State}} BETWEEN 0 AND 3);
-- the MFA state moves only along the graph (review M1; MfaStateGraph is the C# twin, a PostgreSQL test pins them equal): None -> EnrolmentRequired or Enrolled (a voluntary enrolment), EnrolmentRequired -> Enrolled,
-- Enrolled -> EnrolmentRequired (recovery, DR) or RecoveryRequired (restore), RecoveryRequired -> EnrolmentRequired (recovery, DR). Nothing ever returns to None: MFA is never silently off.
CREATE FUNCTION {{UserMfa.FnStateGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.{{UserMfa.State}} IS DISTINCT FROM OLD.{{UserMfa.State}} AND NOT (
        (OLD.{{UserMfa.State}} = 0 AND NEW.{{UserMfa.State}} IN (1, 2))
        OR (OLD.{{UserMfa.State}} = 1 AND NEW.{{UserMfa.State}} = 2)
        OR (OLD.{{UserMfa.State}} = 2 AND NEW.{{UserMfa.State}} IN (1, 3))
        OR (OLD.{{UserMfa.State}} = 3 AND NEW.{{UserMfa.State}} = 1)) THEN
        RAISE EXCEPTION 'this MFA state change is not allowed';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{UserMfa.TrStateGuard}} BEFORE UPDATE ON [[User.Table]] FOR EACH ROW EXECUTE FUNCTION {{UserMfa.FnStateGuard}}();

-- the one-time enrolment code of a bootstrap administrator (every other code lives in its approval request): a SHA-256 hash with an expiry, consumed at most once
ALTER TABLE [[User.Table]] ADD CONSTRAINT {{UserMfa.CkEnrolmentCode}} CHECK (
    ({{UserMfa.EnrolmentCodeHash}} IS NULL AND {{UserMfa.EnrolmentCodeExpiresAtUtc}} IS NULL AND {{UserMfa.EnrolmentCodeConsumedAtUtc}} IS NULL)
    OR (octet_length({{UserMfa.EnrolmentCodeHash}}) = 32 AND {{UserMfa.EnrolmentCodeExpiresAtUtc}} IS NOT NULL));

-- TOTP secrets: AES-256-GCM ciphertext under a purpose-bound subkey of the protected key ring; the authenticated data is user id | key id | secret id. state: 0 Pending, 1 Active, 2 Revoked.
-- At most one Active and one Pending secret per user (database-enforced). Rows are never deleted; revocation is state 2.
CREATE TABLE {{UserTotp}} (
    {{UserTotp.SecretId}} uuid NOT NULL,
    {{UserTotp.UserId}} uuid NOT NULL,
    {{UserTotp.Cipher}} bytea NOT NULL,
    {{UserTotp.Nonce}} bytea NOT NULL,
    {{UserTotp.Tag}} bytea NOT NULL,
    {{UserTotp.KekId}} varchar(32) NOT NULL,
    {{UserTotp.State}} smallint NOT NULL,
    {{UserTotp.CreatedAtUtc}} timestamptz NOT NULL,
    {{UserTotp.ConfirmedAtUtc}} timestamptz NULL,
    {{UserTotp.LastStep}} bigint NULL,
    {{UserTotp.FailedConfirmations}} integer NOT NULL DEFAULT 0,
    CONSTRAINT {{UserTotp.Pk}} PRIMARY KEY ({{UserTotp.SecretId}}),
    CONSTRAINT {{UserTotp.FkUser}} FOREIGN KEY ({{UserTotp.UserId}}) REFERENCES [[User.Table]] ([[User.Id]]),
    CONSTRAINT {{UserTotp.CkState}} CHECK ({{UserTotp.State}} IN (0, 1, 2)),
    CONSTRAINT {{UserTotp.CkLengths}} CHECK (octet_length({{UserTotp.Cipher}}) = 20 AND octet_length({{UserTotp.Nonce}}) = 12 AND octet_length({{UserTotp.Tag}}) = 16
        AND {{UserTotp.KekId}} ~ '^[a-z0-9][a-z0-9-]{0,31}$' AND {{UserTotp.FailedConfirmations}} >= 0),
    CONSTRAINT {{UserTotp.CkConfirmed}} CHECK (({{UserTotp.State}} = 0 AND {{UserTotp.ConfirmedAtUtc}} IS NULL) OR ({{UserTotp.State}} = 1 AND {{UserTotp.ConfirmedAtUtc}} IS NOT NULL) OR {{UserTotp.State}} = 2)
);
CREATE UNIQUE INDEX {{UserTotp.UqActive}} ON {{UserTotp}} ({{UserTotp.UserId}}) WHERE {{UserTotp.State}} = 1;
CREATE UNIQUE INDEX {{UserTotp.UqPending}} ON {{UserTotp}} ({{UserTotp.UserId}}) WHERE {{UserTotp.State}} = 0;

CREATE FUNCTION {{UserTotp.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'TOTP secrets are never deleted or truncated (revoke instead)'; END IF;
    IF NEW.{{UserTotp.SecretId}} IS DISTINCT FROM OLD.{{UserTotp.SecretId}} OR NEW.{{UserTotp.UserId}} IS DISTINCT FROM OLD.{{UserTotp.UserId}} OR NEW.{{UserTotp.CreatedAtUtc}} IS DISTINCT FROM OLD.{{UserTotp.CreatedAtUtc}} THEN
        RAISE EXCEPTION 'the identity of a TOTP secret is immutable';
    END IF;
    -- the protected material changes only inside the rewrap procedure, which sets the transaction-local flag
    IF (NEW.{{UserTotp.Cipher}} IS DISTINCT FROM OLD.{{UserTotp.Cipher}} OR NEW.{{UserTotp.Nonce}} IS DISTINCT FROM OLD.{{UserTotp.Nonce}} OR NEW.{{UserTotp.Tag}} IS DISTINCT FROM OLD.{{UserTotp.Tag}}
        OR NEW.{{UserTotp.KekId}} IS DISTINCT FROM OLD.{{UserTotp.KekId}}) AND COALESCE(current_setting('hasm.totp_rewrap', true), '') <> 'on' THEN
        RAISE EXCEPTION 'TOTP secret material changes only through rewrap';
    END IF;
    IF (OLD.{{UserTotp.LastStep}} IS NOT NULL AND NEW.{{UserTotp.LastStep}} IS NULL) OR (NEW.{{UserTotp.LastStep}} IS NOT NULL AND OLD.{{UserTotp.LastStep}} IS NOT NULL AND NEW.{{UserTotp.LastStep}} < OLD.{{UserTotp.LastStep}}) THEN
        RAISE EXCEPTION 'the accepted TOTP step never decreases and is never cleared';
    END IF;
    IF NEW.{{UserTotp.State}} <> OLD.{{UserTotp.State}} AND NOT ((OLD.{{UserTotp.State}} = 0 AND NEW.{{UserTotp.State}} IN (1, 2)) OR (OLD.{{UserTotp.State}} = 1 AND NEW.{{UserTotp.State}} = 2)) THEN
        RAISE EXCEPTION 'a TOTP secret moves only from pending to active or revoked, and from active to revoked';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{UserTotp.TrRow}} BEFORE UPDATE OR DELETE ON {{UserTotp}} FOR EACH ROW EXECUTE FUNCTION {{UserTotp.FnGuard}}();
CREATE TRIGGER {{UserTotp.TrTruncate}} BEFORE TRUNCATE ON {{UserTotp}} FOR EACH STATEMENT EXECUTE FUNCTION {{UserTotp.FnGuard}}();
REVOKE ALL ON TABLE {{UserTotp}} FROM PUBLIC;
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.system.mfa-identity.v1:" + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (/\d{4}_totp_owner_only_trigger\.sql$|SystemSchema\.TotpGuard\.cs$/.test(p)) continue;   // a later migration: it REFERENCES the names of this one
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
  let cs = "namespace Hasm.Infrastructure.SystemDatabase;\n\n// GENERATED by tools/schema/system-mfa.cjs for the system migration of Phase 4.1 slice S1 (MFA state and TOTP secrets). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token (the seed does not contain the migration number), and fixed: changing one requires a new migration.\n" +
    "// UserMfa holds the columns and constraints ADDED to the user table; UserTotp is the new secret-store table.\npublic static partial class SystemSchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> MfaMap { get; } = new Dictionary<string, string>\n    {\n";
  for (const t of tokens) cs += `        ["${t.includes(".") ? t : t + ".Table"}"] = ${t.includes(".") ? t : t + ".Table"},\n`;   // the system dictionary names a table "Entity.Table"
  cs += "    };\n}\n";
  return { sql, cs };
}

const { sql, cs } = generate();
const mode = process.argv[2];
if (mode === "--write") { fs.writeFileSync(outSql, sql); fs.writeFileSync(outCs, cs); console.log("written"); }
else if (mode === "--check") {
  const norm = (s) => s.replace(/\r\n/g, "\n");
  const ok = fs.existsSync(outSql) && fs.existsSync(outCs) && norm(fs.readFileSync(outSql, "utf8")) === sql && norm(fs.readFileSync(outCs, "utf8")) === cs;
  if (!ok) { console.error("generated MFA schema differs from the committed files"); process.exit(1); }
  console.log("MFA schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
