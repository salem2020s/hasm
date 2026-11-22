#!/usr/bin/env node
// Deterministic generator for company migration 0033 (Phase 3.13: duplicate approval policies are refused; owner decision of 2026-11-21; the number follows the merge order, change MIGRATION below and rename the committed files if it moves).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CommercialDocument.DocType}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.approval-policy-identity.v1:" + token)
// (same rules as tools/schema/company-0026-pricing.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migrations 0008 and 0009) are written
// [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0033-approval-policy-identity.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0033";   // the company migration number: it is part of the name seed, so a renumbering is a regeneration
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_approval_policy_identity.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.ApprovalPolicyIdentity.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Configuration.cs"), ...parseDictionary("CompanySchema.Commercial.cs"), ...parseDictionary("CompanySchema.CommercialApproval.cs"), ...parseDictionary("CompanySchema.LedgerApprovals.cs"), ...parseDictionary("CompanySchema.LedgerPolicies.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration ${MIGRATION}: a semantically identical ACTIVE approval policy is refused by PostgreSQL (Phase 3.13; owner decision of 2026-11-21). Forward-only and ADDITIVE: one new table, new functions and new
-- triggers only; no existing table, constraint, function or trigger is altered or replaced, and no approval policy row is read for change, modified, deactivated or removed. Opaque physical names (D-029) generated
-- deterministically by tools/schema/company-${MIGRATION}-approval-policy-identity.cjs (logical dictionary: CompanySchema.ApprovalPolicyIdentity.cs). Do not edit by hand: re-run the generator.
--
-- WHAT "IDENTICAL" MEANS. Two policies are semantically identical when they would be picked for exactly the same documents and demand exactly the same approvals: the same document type, source, branch (a policy for
-- ALL branches, NULL, is its own value and differs from every single branch), minimum amount, journal type, rate-override, soft-closed and closing-kind conditions, and the same ORDERED list of stages (required approvals and
-- role of each stage; a classic policy of N approvals is one stage of N without a role). The Arabic and English names, the identifier, the revision and the timestamps are not part of the identity.
-- SOURCE. For the JOURNAL the picker compares the policy source with the source of the entry (3 = either, 1 = manual, 2 = automatic), so the three values are three different selections and the literal value is part of the identity.
-- For every COMMERCIAL document type the picker (migration 0032) takes a policy when its source is IN (1, 3) and a commercial document has no source of its own: manual and any select exactly the same documents, so both
-- are written as 'doc'; an automatic (2) commercial policy is never picked at all and is written as 'never'. The journal is exactly DocumentTypes.UsesApprovalPolicies; every other type in this table is commercial.
-- The model has NO priority and NO effective period today; if either is ever added it MUST enter the fingerprint below (a new migration with a new fingerprint version).
-- Policies that differ in any of the listed parts stay allowed, also when they overlap (for example one for all branches and one for a single branch): the picker of migrations 0009 and 0032 takes the strictest.
-- The scope is this company database, so the company is not part of the fingerprint.
--
-- HOW. The fingerprint is computed BY POSTGRESQL from the stored rows (never supplied by a caller): SHA-256 over a canonical, typed and delimited text. One identity row per policy holds it; a partial UNIQUE index over the
-- identities of ACTIVE policies is the race-safe refusal (two concurrent writers: the second waits for the first and fails). The identity is refreshed by DEFERRED constraint triggers on the policy and on its stages, because
-- the stages of a revision are written after the policy row; a caller may ask for the check earlier with SET CONSTRAINTS ... IMMEDIATE (the service does, to answer 409 before it commits).
-- EXISTING DATA. A deployed database may already hold identical active policies. Nothing is changed for them: the backfill below keeps the OLDEST of each identical group (the one the picker prefers) as the holder of the
-- identity and marks the others as pre-existing; marked rows are left out of the unique index. The mark is written ONLY by this backfill: the guard trigger created after it resets the mark on every later write, so a marked
-- policy that is edited or reactivated must pass the same refusal as any other, and it can always be deactivated.

CREATE FUNCTION {{ApprovalPolicyIdentity.FnFingerprint}}(p_policy uuid) RETURNS text LANGUAGE sql STABLE AS $$
    -- canonical text, version v2 (the source is canonicalized per document type, see SOURCE above): every part is named and typed; the document type is length-prefixed; the amount is numeric(20,4) and so always printed with four decimals; NULL is written as a token no value can produce
    SELECT encode(sha256(convert_to(
           'v2'
        || '|type=' || char_length(p.[[ApprovalPolicy.DocType]])::text || ':' || p.[[ApprovalPolicy.DocType]]
        || '|source=' || CASE WHEN p.[[ApprovalPolicy.DocType]] = 'journal_entry' THEN p.[[ApprovalPolicy.Source]]::text
                              WHEN p.[[ApprovalPolicy.Source]] IN (1, 3) THEN 'doc'
                              ELSE 'never' END
        || '|branch=' || COALESCE('one:' || p.[[ApprovalPolicy.BranchId]]::text, 'all')
        || '|min=' || p.[[ApprovalPolicy.MinAmount]]::numeric(20,4)::text
        || '|jtype=' || COALESCE(p.[[ApprovalPolicyConditions.JournalType]]::text, '-')
        || '|rate=' || COALESCE(p.[[ApprovalPolicyConditions.RateOverride]]::integer::text, '-')
        || '|soft=' || COALESCE(p.[[ApprovalPolicyConditions.SoftClosed]]::integer::text, '-')
        || '|closing=' || COALESCE(p.[[ClosingLogic.PolicyClosingKind]]::text, '-')
        || '|stages=' || COALESCE(
               (SELECT string_agg(st.[[ApprovalPolicyStage.Required]]::text || '@' || COALESCE(st.[[ApprovalPolicyStage.RoleId]]::text, '-'), '>' ORDER BY st.[[ApprovalPolicyStage.StageNo]])
                  FROM [[ApprovalPolicyStage.Table]] st WHERE st.[[ApprovalPolicyStage.PolicyId]] = p.[[ApprovalPolicy.Id]] AND st.[[ApprovalPolicyStage.PolicyRevision]] = p.[[ApprovalPolicy.Revision]]),
               p.[[ApprovalPolicy.RequiredApprovals]]::text || '@-'), 'UTF8')), 'hex')
    FROM [[ApprovalPolicy.Table]] p WHERE p.[[ApprovalPolicy.Id]] = p_policy
$$;

CREATE TABLE {{ApprovalPolicyIdentity}} (
    {{ApprovalPolicyIdentity.PolicyId}} uuid NOT NULL,
    {{ApprovalPolicyIdentity.Fingerprint}} text NOT NULL,
    {{ApprovalPolicyIdentity.IsActive}} boolean NOT NULL,
    {{ApprovalPolicyIdentity.PreExisting}} boolean NOT NULL DEFAULT false,
    CONSTRAINT {{ApprovalPolicyIdentity.Pk}} PRIMARY KEY ({{ApprovalPolicyIdentity.PolicyId}}),
    CONSTRAINT {{ApprovalPolicyIdentity.FkPolicy}} FOREIGN KEY ({{ApprovalPolicyIdentity.PolicyId}}) REFERENCES [[ApprovalPolicy.Table]] ([[ApprovalPolicy.Id]]),
    CONSTRAINT {{ApprovalPolicyIdentity.CkFingerprint}} CHECK ({{ApprovalPolicyIdentity.Fingerprint}} ~ '^[0-9a-f]{64}$'),
    CONSTRAINT {{ApprovalPolicyIdentity.CkPreExisting}} CHECK ({{ApprovalPolicyIdentity.IsActive}} OR NOT {{ApprovalPolicyIdentity.PreExisting}})
);
REVOKE ALL ON TABLE {{ApprovalPolicyIdentity}} FROM PUBLIC;

-- Backfill (the ONLY writer of the pre-existing mark): one identity per existing policy. Within a group of identical ACTIVE policies the oldest (creation time, then id: the order of the picker) holds the identity and
-- the others are marked. No policy row is touched.
INSERT INTO {{ApprovalPolicyIdentity}} ({{ApprovalPolicyIdentity.PolicyId}}, {{ApprovalPolicyIdentity.Fingerprint}}, {{ApprovalPolicyIdentity.IsActive}}, {{ApprovalPolicyIdentity.PreExisting}})
SELECT x.id, x.fp, x.active, x.active AND x.rn > 1
FROM (SELECT p.[[ApprovalPolicy.Id]] AS id, {{ApprovalPolicyIdentity.FnFingerprint}}(p.[[ApprovalPolicy.Id]]) AS fp, p.[[ApprovalPolicy.IsActive]] AS active,
             row_number() OVER (PARTITION BY {{ApprovalPolicyIdentity.FnFingerprint}}(p.[[ApprovalPolicy.Id]]), p.[[ApprovalPolicy.IsActive]] ORDER BY p.[[ApprovalPolicy.CreatedAtUtc]], p.[[ApprovalPolicy.Id]]) AS rn
      FROM [[ApprovalPolicy.Table]] p) x;

-- The race-safe refusal: at most one unmarked identity per fingerprint among ACTIVE policies.
CREATE UNIQUE INDEX {{ApprovalPolicyIdentity.UqActive}} ON {{ApprovalPolicyIdentity}} ({{ApprovalPolicyIdentity.Fingerprint}}) WHERE {{ApprovalPolicyIdentity.IsActive}} AND NOT {{ApprovalPolicyIdentity.PreExisting}};

-- How many pre-existing duplicates were marked (0 on a database without duplicates). The same number is read at any time with: SELECT count(*) FROM <identity table> WHERE <pre-existing column>.
DO $$
DECLARE
    v_marked bigint;
BEGIN
    SELECT count(*) INTO v_marked FROM {{ApprovalPolicyIdentity}} i WHERE i.{{ApprovalPolicyIdentity.PreExisting}};
    RAISE NOTICE 'approval policy identity: % pre-existing identical active policy row(s) marked; none was changed or deactivated', v_marked;
END;
$$;

-- Guard of the identity table (created AFTER the backfill, so the backfill is the only statement that ever wrote the mark). Whatever a writer supplies, the stored identity is the truth read from the policy and its
-- stages; the mark is reset on every write; rows are never removed. A policy whose identity is NEW (first identity, a changed fingerprint, a reactivation, or a marked row being written) is refused when an identical
-- ACTIVE policy exists that is still marked (those are outside the unique index); every other collision is refused by the unique index itself.
CREATE FUNCTION {{ApprovalPolicyIdentity.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_new boolean;
BEGIN
    IF TG_OP <> 'INSERT' AND TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'approval policy identities are never removed'; END IF;
    IF TG_OP = 'UPDATE' AND NEW.{{ApprovalPolicyIdentity.PolicyId}} IS DISTINCT FROM OLD.{{ApprovalPolicyIdentity.PolicyId}} THEN RAISE EXCEPTION 'the policy of an approval policy identity is immutable'; END IF;
    SELECT {{ApprovalPolicyIdentity.FnFingerprint}}(p.[[ApprovalPolicy.Id]]), p.[[ApprovalPolicy.IsActive]] INTO NEW.{{ApprovalPolicyIdentity.Fingerprint}}, NEW.{{ApprovalPolicyIdentity.IsActive}}
      FROM [[ApprovalPolicy.Table]] p WHERE p.[[ApprovalPolicy.Id]] = NEW.{{ApprovalPolicyIdentity.PolicyId}};
    IF NOT FOUND THEN RAISE EXCEPTION 'an approval policy identity needs its policy'; END IF;
    NEW.{{ApprovalPolicyIdentity.PreExisting}} := false;
    IF TG_OP = 'INSERT' THEN v_new := true;
    ELSE v_new := OLD.{{ApprovalPolicyIdentity.PreExisting}} OR NOT OLD.{{ApprovalPolicyIdentity.IsActive}} OR OLD.{{ApprovalPolicyIdentity.Fingerprint}} <> NEW.{{ApprovalPolicyIdentity.Fingerprint}};
    END IF;
    IF NEW.{{ApprovalPolicyIdentity.IsActive}} AND v_new AND EXISTS (SELECT 1 FROM {{ApprovalPolicyIdentity}} o WHERE o.{{ApprovalPolicyIdentity.Fingerprint}} = NEW.{{ApprovalPolicyIdentity.Fingerprint}}
            AND o.{{ApprovalPolicyIdentity.IsActive}} AND o.{{ApprovalPolicyIdentity.PreExisting}} AND o.{{ApprovalPolicyIdentity.PolicyId}} <> NEW.{{ApprovalPolicyIdentity.PolicyId}}) THEN
        RAISE EXCEPTION 'an identical active approval policy already exists' USING ERRCODE = 'unique_violation', CONSTRAINT = '{{ApprovalPolicyIdentity.UqActive}}';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{ApprovalPolicyIdentity.TrGuard}} BEFORE INSERT OR UPDATE OR DELETE ON {{ApprovalPolicyIdentity}} FOR EACH ROW EXECUTE FUNCTION {{ApprovalPolicyIdentity.FnGuard}}();
CREATE TRIGGER {{ApprovalPolicyIdentity.TrGuardAll}} BEFORE TRUNCATE ON {{ApprovalPolicyIdentity}} FOR EACH STATEMENT EXECUTE FUNCTION {{ApprovalPolicyIdentity.FnGuard}}();

-- Refresh of one identity: an existing row is rewritten (the guard recomputes it), a missing one is created. Not an upsert on purpose: the guard must see whether the identity existed.
CREATE FUNCTION {{ApprovalPolicyIdentity.FnRefresh}}(p_policy uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    UPDATE {{ApprovalPolicyIdentity}} SET {{ApprovalPolicyIdentity.Fingerprint}} = {{ApprovalPolicyIdentity.Fingerprint}} WHERE {{ApprovalPolicyIdentity.PolicyId}} = p_policy;
    IF NOT FOUND THEN
        INSERT INTO {{ApprovalPolicyIdentity}} ({{ApprovalPolicyIdentity.PolicyId}}, {{ApprovalPolicyIdentity.Fingerprint}}, {{ApprovalPolicyIdentity.IsActive}}) VALUES (p_policy, repeat('0', 64), false);
    END IF;
END;
$$;

-- Every write of a policy, and every stage written for it, refreshes the identity at COMMIT (or earlier on SET CONSTRAINTS ... IMMEDIATE), when the policy row and the stages of its revision are both in place.
CREATE FUNCTION {{ApprovalPolicyIdentity.FnOnPolicy}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM {{ApprovalPolicyIdentity.FnRefresh}}(NEW.[[ApprovalPolicy.Id]]);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{ApprovalPolicyIdentity.TrOnPolicy}} AFTER INSERT OR UPDATE ON [[ApprovalPolicy.Table]] DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {{ApprovalPolicyIdentity.FnOnPolicy}}();

CREATE FUNCTION {{ApprovalPolicyIdentity.FnOnStage}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM {{ApprovalPolicyIdentity.FnRefresh}}(NEW.[[ApprovalPolicyStage.PolicyId]]);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{ApprovalPolicyIdentity.TrOnStage}} AFTER INSERT ON [[ApprovalPolicyStage.Table]] DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {{ApprovalPolicyIdentity.FnOnStage}}();
`
function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update(`hasm.company.${MIGRATION}.approval-policy-identity.v1:` + token).digest("hex").slice(0, 8);

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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0033-approval-policy-identity.cjs for company migration " + MIGRATION + " (Phase 3.13, duplicate approval policies refused). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// ApprovalPolicyIdentity is the identity table (one fingerprint per policy), its unique index, the fingerprint function and the triggers that keep it true.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> ApprovalPolicyIdentityMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated approval-policy-identity schema differs from the committed files"); process.exit(1); }
  console.log("approval-policy-identity schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
