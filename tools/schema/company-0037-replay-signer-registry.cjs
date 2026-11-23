#!/usr/bin/env node
// Deterministic generator for company migration 0037 (Phase 3.8D threat T2, slice A1: the signer key registry, the RSA verifier and the empty grant shells). The number was allocated by the lead; it is part of the name seed.
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{ReplaySignerKey.Modulus}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.replay-authorization.v1:" + token)
// (same rules as tools/schema/company-0036-plan-execution.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist are written [[Entity.Name]] and resolved from the committed dictionaries.
// Usage:  node tools/schema/company-0037-replay-signer-registry.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0037";
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_replay_signer_keys.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.ReplayAuthorization.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map(["CompanySchema.CompensationPlan.cs"].flatMap((f) => [...parseDictionary(f)]));
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration ${MIGRATION}: the replay authorization registry (Phase 3.8D threat T2, slice A1; owner decisions D-248 to D-253; design docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md sections 4.1 to 4.3 and 4.5). Forward-only and ADDITIVE:
-- three new tables, their guards and one verification function; no existing table, function or trigger is changed, replaced or switched off. INERT: nothing calls the verifier, the application role holds only SELECT,
-- no grant is issued and no execution is possible (activation stays closed).
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-${MIGRATION}-replay-signer-registry.cjs (logical dictionary: CompanySchema.ReplayAuthorization.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: only PUBLIC keys are stored. A signer key is RSA with a modulus of exactly 3072 bits and exponent 65537 (RSASSA-PKCS1-v1_5, SHA-256). The registry is written by the database OWNER only (the company runtime role holds
-- SELECT and nothing else, see CompanyRuntimePrivileges), is append-only apart from the one-time revoke-from (design 4.1, 4.6), and registered-at is set by the database clock, never supplied.
-- The two grant tables are EMPTY SHELLS of design 4.3 (append-only, structural checks only): the insert-time verification, the single-use rule and the commit-time checks arrive with slices A2 to A4. Because a row written before
-- those checks exist would escape them, the application role gets NO INSERT on these tables in this slice.
-- The verifier public.rsa_verify (design 4.2) is plain PL/pgSQL with invoker rights and no extension. It refuses unless the signature has exactly 384 bytes and is below the modulus, computes s^65537 mod n with 16 squarings and one
-- multiplication (mod() only, never a division) and compares the WHOLE expected encoding 00 01 FF.. 00 DigestInfo sha256(message) as a number, so no padding parse can be forged. Name resolution (design 4.5): no search_path and no
-- SECURITY DEFINER; every relation is written public.<name> inside function bodies, and the company runtime role holds neither TEMPORARY nor CREATE, so nothing can stand in for a name the functions use.

CREATE TABLE public.{{ReplaySignerKey}} (
    {{ReplaySignerKey.KeyId}} varchar(40) NOT NULL,
    {{ReplaySignerKey.Modulus}} numeric NOT NULL,
    {{ReplaySignerKey.SignerUserId}} uuid NULL,
    {{ReplaySignerKey.RegisteredAtUtc}} timestamptz NOT NULL DEFAULT clock_timestamp(),
    {{ReplaySignerKey.RevokeFrom}} timestamptz NULL,
    CONSTRAINT {{ReplaySignerKey.Pk}} PRIMARY KEY ({{ReplaySignerKey.KeyId}}),
    CONSTRAINT {{ReplaySignerKey.CkValues}} CHECK ({{ReplaySignerKey.KeyId}} ~ '^[a-z0-9-]{1,40}$'
        AND trunc({{ReplaySignerKey.Modulus}}) = {{ReplaySignerKey.Modulus}} AND mod({{ReplaySignerKey.Modulus}}, 2) = 1
        AND {{ReplaySignerKey.Modulus}} >= power(2::numeric, 3071) AND {{ReplaySignerKey.Modulus}} < power(2::numeric, 3072))
);

CREATE FUNCTION public.{{ReplaySignerKey.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{ReplaySignerKey.RevokeFrom}} IS NOT NULL THEN RAISE EXCEPTION 'a replay signer key is registered unrevoked'; END IF;
        NEW.{{ReplaySignerKey.RegisteredAtUtc}} := clock_timestamp();   -- never supplied by the writer
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'replay signer keys are never removed'; END IF;
    IF NEW.{{ReplaySignerKey.KeyId}} IS DISTINCT FROM OLD.{{ReplaySignerKey.KeyId}} OR NEW.{{ReplaySignerKey.Modulus}} IS DISTINCT FROM OLD.{{ReplaySignerKey.Modulus}}
       OR NEW.{{ReplaySignerKey.SignerUserId}} IS DISTINCT FROM OLD.{{ReplaySignerKey.SignerUserId}} OR NEW.{{ReplaySignerKey.RegisteredAtUtc}} IS DISTINCT FROM OLD.{{ReplaySignerKey.RegisteredAtUtc}} THEN
        RAISE EXCEPTION 'a replay signer key is immutable except for its one-time revoke-from';
    END IF;
    IF OLD.{{ReplaySignerKey.RevokeFrom}} IS NOT NULL THEN RAISE EXCEPTION 'the revoke-from of a replay signer key is set once'; END IF;
    IF NEW.{{ReplaySignerKey.RevokeFrom}} IS NULL THEN RAISE EXCEPTION 'an update of a replay signer key only sets its revoke-from'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{ReplaySignerKey.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON public.{{ReplaySignerKey}} FOR EACH ROW EXECUTE FUNCTION public.{{ReplaySignerKey.FnGuard}}();
CREATE TRIGGER {{ReplaySignerKey.TrTruncate}} BEFORE TRUNCATE ON public.{{ReplaySignerKey}} FOR EACH STATEMENT EXECUTE FUNCTION public.{{ReplaySignerKey.FnGuard}}();

-- the grant shell of design 4.3: the stored columns only; the message the signature covers is rebuilt from rows by later slices
CREATE TABLE public.{{ReplayExecutionGrant}} (
    {{ReplayExecutionGrant.Id}} uuid NOT NULL,
    {{ReplayExecutionGrant.KeyId}} varchar(40) NOT NULL,
    {{ReplayExecutionGrant.PlanId}} uuid NOT NULL,
    {{ReplayExecutionGrant.ExecutorUserId}} uuid NOT NULL,
    {{ReplayExecutionGrant.SignerUserId}} uuid NOT NULL,
    {{ReplayExecutionGrant.NotBefore}} bigint NOT NULL,
    {{ReplayExecutionGrant.NotAfter}} bigint NOT NULL,
    {{ReplayExecutionGrant.EpochDigest}} char(64) NOT NULL,
    {{ReplayExecutionGrant.Attestation}} text NOT NULL,
    {{ReplayExecutionGrant.Signature}} bytea NOT NULL,
    {{ReplayExecutionGrant.AcceptedAtUtc}} timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT {{ReplayExecutionGrant.Pk}} PRIMARY KEY ({{ReplayExecutionGrant.Id}}),
    CONSTRAINT {{ReplayExecutionGrant.FkKey}} FOREIGN KEY ({{ReplayExecutionGrant.KeyId}}) REFERENCES public.{{ReplaySignerKey}} ({{ReplaySignerKey.KeyId}}),
    CONSTRAINT {{ReplayExecutionGrant.FkPlan}} FOREIGN KEY ({{ReplayExecutionGrant.PlanId}}) REFERENCES public.[[CompensationPlan.Table]] ([[CompensationPlan.Id]]),
    CONSTRAINT {{ReplayExecutionGrant.CkValues}} CHECK ({{ReplayExecutionGrant.NotBefore}} >= 0 AND {{ReplayExecutionGrant.NotAfter}} > {{ReplayExecutionGrant.NotBefore}}
        AND {{ReplayExecutionGrant.EpochDigest}} ~ '^[0-9a-f]{64}$' AND char_length({{ReplayExecutionGrant.Attestation}}) >= 1 AND length({{ReplayExecutionGrant.Signature}}) = 384)
);
CREATE INDEX {{ReplayExecutionGrant.IxPlan}} ON public.{{ReplayExecutionGrant}} ({{ReplayExecutionGrant.PlanId}});
CREATE INDEX {{ReplayExecutionGrant.IxKey}} ON public.{{ReplayExecutionGrant}} ({{ReplayExecutionGrant.KeyId}});

-- a grant is used at most once and an execution has at most one grant (design 4.3)
CREATE TABLE public.{{ReplayGrantUse}} (
    {{ReplayGrantUse.ExecutionId}} uuid NOT NULL,
    {{ReplayGrantUse.GrantId}} uuid NOT NULL,
    CONSTRAINT {{ReplayGrantUse.Pk}} PRIMARY KEY ({{ReplayGrantUse.ExecutionId}}),
    CONSTRAINT {{ReplayGrantUse.UqGrant}} UNIQUE ({{ReplayGrantUse.GrantId}}),
    CONSTRAINT {{ReplayGrantUse.FkExecution}} FOREIGN KEY ({{ReplayGrantUse.ExecutionId}}) REFERENCES public.[[CompensationPlanExecution.Table]] ([[CompensationPlanExecution.Id]]),
    CONSTRAINT {{ReplayGrantUse.FkGrant}} FOREIGN KEY ({{ReplayGrantUse.GrantId}}) REFERENCES public.{{ReplayExecutionGrant}} ({{ReplayExecutionGrant.Id}})
);

-- append-only guards, as in 0036: a row is written once (the grant's accepted-at is the database clock) and is never updated, deleted or truncated
CREATE FUNCTION public.{{ReplayAuthorization.FnGrantGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' AND TG_TABLE_NAME = '{{ReplayExecutionGrant}}' THEN
        NEW.{{ReplayExecutionGrant.AcceptedAtUtc}} := clock_timestamp();   -- never supplied by the writer
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN RETURN NEW; END IF;
    RAISE EXCEPTION 'replay grants and their uses are append-only';
END;
$$;
CREATE TRIGGER {{ReplayExecutionGrant.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON public.{{ReplayExecutionGrant}} FOR EACH ROW EXECUTE FUNCTION public.{{ReplayAuthorization.FnGrantGuard}}();
CREATE TRIGGER {{ReplayExecutionGrant.TrTruncate}} BEFORE TRUNCATE ON public.{{ReplayExecutionGrant}} FOR EACH STATEMENT EXECUTE FUNCTION public.{{ReplayAuthorization.FnGrantGuard}}();
CREATE TRIGGER {{ReplayGrantUse.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON public.{{ReplayGrantUse}} FOR EACH ROW EXECUTE FUNCTION public.{{ReplayAuthorization.FnGrantGuard}}();
CREATE TRIGGER {{ReplayGrantUse.TrTruncate}} BEFORE TRUNCATE ON public.{{ReplayGrantUse}} FOR EACH STATEMENT EXECUTE FUNCTION public.{{ReplayAuthorization.FnGrantGuard}}();

-- the verifier (design 4.2): TRUE only for a valid RSASSA-PKCS1-v1_5 / SHA-256 signature, exponent 65537, modulus of exactly 3072 bits; anything else is FALSE (never an error for bad input)
CREATE FUNCTION public.{{ReplayAuthorization.FnRsaVerify}}(p_modulus numeric, p_signature bytea, p_message bytea) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE
    c_digest_info CONSTANT bytea := decode('3031300d060960864801650304020105000420', 'hex');
    v_s numeric := 0;
    v_em numeric := 0;
    v_r numeric;
    v_expected bytea;
    i integer;
BEGIN
    IF p_modulus IS NULL OR p_signature IS NULL OR p_message IS NULL THEN RETURN false; END IF;
    IF length(p_signature) <> 384 THEN RETURN false; END IF;
    IF trunc(p_modulus) <> p_modulus OR mod(p_modulus, 2) <> 1 OR p_modulus < power(2::numeric, 3071) OR p_modulus >= power(2::numeric, 3072) THEN RETURN false; END IF;
    FOR i IN 0..383 LOOP v_s := v_s * 256 + get_byte(p_signature, i); END LOOP;
    IF v_s >= p_modulus THEN RETURN false; END IF;
    v_r := v_s;
    FOR i IN 1..16 LOOP v_r := mod(v_r * v_r, p_modulus); END LOOP;
    v_r := mod(v_r * v_s, p_modulus);
    v_expected := decode('0001', 'hex') || decode(repeat('ff', 330), 'hex') || decode('00', 'hex') || c_digest_info || sha256(p_message);
    FOR i IN 0..383 LOOP v_em := v_em * 256 + get_byte(v_expected, i); END LOOP;
    RETURN v_r = v_em;
END;
$$;

REVOKE ALL ON TABLE public.{{ReplaySignerKey}} FROM PUBLIC;
REVOKE ALL ON TABLE public.{{ReplayExecutionGrant}} FROM PUBLIC;
REVOKE ALL ON TABLE public.{{ReplayGrantUse}} FROM PUBLIC;
`;

// inside every function body each table of the schema is named public.<table> (design 4.5, opusSEC Y1: a same-named temporary table can never stand in for it)
const qualify = (t) => t.replace(/\$\$[\s\S]*?\$\$/g, (b) => b.replace(/(?<!['.])\{\{([A-Za-z0-9]+)\}\}/g, "public.{{$1}}").replace(/(?<!\.)\[\[([A-Za-z0-9]+\.Table)\]\]/g, "public.[[$1]]"));

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company." + MIGRATION + ".replay-authorization.v1:" + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (!/\.(sql|cs)$/.test(e.name)) continue;
      const later = /^(\d{4})_company_/.exec(e.name);
      if (later && Number(later[1]) > Number(MIGRATION)) continue;   // a LATER company migration refers to this one's names (public.<table> references)
      for (const m of fs.readFileSync(p, "utf8").matchAll(/\b[tcfkr]_[0-9a-f]{8}\b/g)) names.add(m[0]);
    }
  };
  scan(path.join(infra, "CompanyDatabase")); scan(path.join(infra, "SystemDatabase"));
  return names;
}

function generate() {
  const body = qualify(TEMPLATE).replace(/\[\[([A-Za-z0-9.]+)\]\]/g, (_, k) => ext(k));
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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-" + MIGRATION + "-replay-signer-registry.cjs for company migration " + MIGRATION + " (Phase 3.8D threat T2, slice A1, the replay signer registry). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// ReplayAuthorization names the shared functions; every other entity names a table with its columns, constraints, indexes and triggers.\n" + "public static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> ReplayAuthorizationMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated replay-authorization schema differs from the committed files"); process.exit(1); }
  console.log("replay-authorization schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
