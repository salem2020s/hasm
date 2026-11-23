#!/usr/bin/env node
// Deterministic generator for company migration 0038 (Phase 3.8D threat T2, slice A2: insert-time verification of an execution grant; PROVISIONAL number, it follows the merge order). Number and names:
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{ReplayGrant.FnHead}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.replay-grant.v1:" + token)
// (same rules as the generators of 0036 and 0037: a collision aborts the run; names are fixed once committed). References to names that already exist are written [[Entity.Name]].
// Usage:  node tools/schema/company-0038-replay-grant-verification.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0038";
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_replay_grant_verification.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.ReplayGrant.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map(["CompanySchema.cs", "CompanySchema.CompensationPlan.cs", "CompanySchema.ReplayAuthorization.cs"].flatMap((f) => [...parseDictionary(f)]));
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration ${MIGRATION}: insert-time verification of a replay execution grant (Phase 3.8D threat T2, slice A2; design docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md sections 4.3 to 4.5 and 16, message version hasm-replay-v5).
-- Forward-only and ADDITIVE: new functions and two new BEFORE INSERT triggers on the two grant tables of 0037; no table is created or altered, no existing function or trigger is replaced or switched off. INERT: the company
-- runtime role still holds SELECT only on the grant tables (no INSERT grant), nothing calls these functions but the two triggers, and no execution can be written.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-${MIGRATION}-replay-grant-verification.cjs (logical dictionary: CompanySchema.ReplayGrant.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: the grant message of design 4.4 is REBUILT here from the rows of the plan, its approval, its closure lines and the company identity (never taken from the grant), and the signature is verified over those exact bytes with
-- the verifier of 0037. The builders are pure functions of their arguments (formatting only) plus thin row readers; every text uses only fixed formats (lower-case uuids, decimal integers, numeric at its declared scale, dates by
-- to_char with a fixed pattern, LF as chr(10)), so TimeZone, DateStyle, IntervalStyle, bytea_output, extra_float_digits and lc_numeric cannot change a byte (design 4.5).
-- The insert of a grant is refused unless: the plan is approved and its approval is sound (0036); the key exists and is not revoked at the database clock; the key's signer (when it names one) is the grant's signer; the window is at
-- most 2 hours (D4) and contains the database clock (epoch seconds); the signer is independent of the plan (creator, event editors, document creator, editors and submitter), is not the approver and is not the executor; the
-- attestation is well formed, names the plan's creator and the approval's approver, uses a known method, has confirmation times inside the allowed band and carries the request digest rebuilt from the rows; and the signature
-- verifies. The insert of a grant USE is refused unless the grant's plan and executor are the execution's and the window still holds (the commit-time re-verification and the epoch and journal bindings are slices A3 and A4).
-- Name resolution (design 4.5): no search_path and no SECURITY DEFINER; every relation is written public.<name>; the company runtime role holds neither TEMPORARY nor CREATE. EXECUTE on these functions stays with PUBLIC (decision of slice A2,
-- review L1 of PR 233): they are invoker-rights readers of rows the caller may already read, they write nothing, and the triggers that call them run as the inserting role, which therefore needs to execute them.

-- one plan line, exactly as the lines text of 4.4 writes it
CREATE FUNCTION public.{{ReplayGrant.FnLineText}}(p_no integer, p_posting uuid, p_date date, p_period uuid, p_branch uuid, p_account uuid, p_debit numeric, p_credit numeric) RETURNS text LANGUAGE sql AS $$
    SELECT p_no::text || '|' || COALESCE(p_posting::text, '-') || '|' || to_char(p_date, 'YYYY-MM-DD') || '|' || p_period::text || '|' || p_branch::text || '|' || p_account::text
        || '|' || p_debit::numeric(28,6)::text || '|' || p_credit::numeric(28,6)::text
$$;

-- the lines text of a plan: one line per plan line ordered by line number, LF-joined, no trailing LF; a plan with no lines gives the empty string
CREATE FUNCTION public.{{ReplayGrant.FnLinesText}}(p_plan uuid) RETURNS text LANGUAGE sql AS $$
    SELECT COALESCE(string_agg(public.{{ReplayGrant.FnLineText}}(l.[[CompensationPlanLine.LineNo]], l.[[CompensationPlanLine.PostingId]], l.[[CompensationPlanLine.EntryDate]], l.[[CompensationPlanLine.PeriodId]],
        l.[[CompensationPlanLine.BranchId]], l.[[CompensationPlanLine.AccountId]], l.[[CompensationPlanLine.Debit]], l.[[CompensationPlanLine.Credit]]), chr(10) ORDER BY l.[[CompensationPlanLine.LineNo]]), '')
    FROM public.[[CompensationPlanLine.Table]] l WHERE l.[[CompensationPlanLine.PlanId]] = p_plan
$$;

-- the first 17 lines of the grant message (version through executor), LF-joined: this is also what the request digest covers
CREATE FUNCTION public.{{ReplayGrant.FnHead}}(p_company uuid, p_plan uuid, p_revision bigint, p_approval uuid, p_approver uuid, p_creator uuid, p_document uuid, p_document_revision bigint, p_item uuid, p_warehouse uuid,
    p_payload_hash text, p_fingerprints text, p_closure text, p_lines_hash text, p_epoch text, p_executor uuid) RETURNS text LANGUAGE sql AS $$
    SELECT 'hasm-replay-v5' || chr(10) || 'grant' || chr(10) || 'company=' || p_company::text || chr(10) || 'plan=' || p_plan::text || chr(10) || 'revision=' || p_revision::text
        || chr(10) || 'approval=' || p_approval::text || chr(10) || 'approver=' || p_approver::text || chr(10) || 'creator=' || p_creator::text
        || chr(10) || 'document=' || p_document::text || '@' || p_document_revision::text || chr(10) || 'item=' || p_item::text || chr(10) || 'warehouse=' || p_warehouse::text
        || chr(10) || 'payload=' || p_payload_hash || chr(10) || 'fingerprints=' || p_fingerprints || chr(10) || 'closure=' || p_closure || chr(10) || 'lines=' || p_lines_hash
        || chr(10) || 'epoch=' || p_epoch || chr(10) || 'executor=' || p_executor::text
$$;

-- the request digest: the first 16 hex characters of the SHA-256 of those 17 lines
CREATE FUNCTION public.{{ReplayGrant.FnRequestDigest}}(p_head text) RETURNS text LANGUAGE sql AS $$
    SELECT left(encode(sha256(convert_to(p_head, 'UTF8')), 'hex'), 16)
$$;

-- the whole grant message: the 17 lines, then the attestation hash, the grant id, the signer, the key id and the window
CREATE FUNCTION public.{{ReplayGrant.FnGrantMessage}}(p_head text, p_attestation text, p_grant uuid, p_signer uuid, p_key text, p_not_before bigint, p_not_after bigint) RETURNS text LANGUAGE sql AS $$
    SELECT p_head || chr(10) || 'attestation=' || encode(sha256(convert_to(p_attestation, 'UTF8')), 'hex') || chr(10) || 'grant=' || p_grant::text || chr(10) || 'signer=' || p_signer::text
        || chr(10) || 'key=' || p_key || chr(10) || 'not-before=' || p_not_before::text || chr(10) || 'not-after=' || p_not_after::text
$$;

-- the 17 lines of a plan, read from its rows: NULL when the plan or its approval does not exist
CREATE FUNCTION public.{{ReplayGrant.FnPlanHead}}(p_plan uuid, p_epoch text, p_executor uuid) RETURNS text LANGUAGE sql AS $$
    SELECT public.{{ReplayGrant.FnHead}}(i.[[Identity.CompanyId]], p.[[CompensationPlan.Id]], a.[[CompensationPlanApproval.PlanRevision]], p.[[CompensationPlan.ApprovalId]], a.[[CompensationPlanApproval.Approver]],
        p.[[CompensationPlan.CreatedBy]], p.[[CompensationPlan.DocumentId]], p.[[CompensationPlan.DocumentRevision]], p.[[CompensationPlan.ItemId]], p.[[CompensationPlan.WarehouseId]],
        encode(sha256(convert_to(p.[[CompensationPlan.Payload]], 'UTF8')), 'hex'),
        p.[[CompensationPlan.PlanFingerprint]]::text || ',' || p.[[CompensationPlan.ComparisonFingerprint]]::text || ',' || p.[[CompensationPlan.EvidenceFingerprint]]::text || ',' || p.[[CompensationPlan.PeriodsFingerprint]]::text
            || ',' || p.[[CompensationPlan.PositionFingerprint]]::text || ',' || p.[[CompensationPlan.PolicyFingerprint]]::text || ',' || p.[[CompensationPlan.AccountingFingerprint]]::text,
        p.[[CompensationPlan.ClosureHash]]::text, encode(sha256(convert_to(public.{{ReplayGrant.FnLinesText}}(p.[[CompensationPlan.Id]]), 'UTF8')), 'hex'), p_epoch, p_executor)
    FROM public.[[CompensationPlan.Table]] p
    JOIN public.[[CompensationPlanApproval.Table]] a ON a.[[CompensationPlanApproval.Id]] = p.[[CompensationPlan.ApprovalId]] AND a.[[CompensationPlanApproval.PlanId]] = p.[[CompensationPlan.Id]]
    CROSS JOIN public.[[Identity.Table]] i
    WHERE p.[[CompensationPlan.Id]] = p_plan
$$;

-- NULL when the attestation (design 4.4, N2) is well formed and consistent with the plan and the request, otherwise the reason
CREATE FUNCTION public.{{ReplayGrant.FnAttestationProblem}}(p_attestation text, p_plan uuid, p_epoch text, p_executor uuid, p_not_before bigint) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    l text[];
    v_creator uuid;
    v_approver uuid;
    v_head text;
    v_at bigint;
BEGIN
    IF p_attestation IS NULL THEN RETURN 'attestation_malformed'; END IF;
    l := string_to_array(p_attestation, chr(10));
    IF array_length(l, 1) IS DISTINCT FROM 6 OR l[1] <> 'hasm-replay-attestation-v1' THEN RETURN 'attestation_malformed'; END IF;
    IF l[2] !~ '^creator=[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}[|](in-person|phone|video)[|](0|[1-9][0-9]{0,11})$' THEN RETURN 'attestation_creator_line'; END IF;
    IF l[3] !~ '^approver=[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}[|](in-person|phone|video)[|](0|[1-9][0-9]{0,11})$' THEN RETURN 'attestation_approver_line'; END IF;
    IF l[4] !~ '^request=[0-9a-f]{16}$' THEN RETURN 'attestation_request_line'; END IF;
    IF l[5] !~ '^participants=(0|[1-9][0-9]{0,8})$' THEN RETURN 'attestation_participants_line'; END IF;
    IF left(l[6], 5) <> 'note=' OR char_length(substr(l[6], 6)) > 500 THEN RETURN 'attestation_note'; END IF;
    SELECT p.[[CompensationPlan.CreatedBy]], a.[[CompensationPlanApproval.Approver]] INTO v_creator, v_approver
      FROM public.[[CompensationPlan.Table]] p
      JOIN public.[[CompensationPlanApproval.Table]] a ON a.[[CompensationPlanApproval.Id]] = p.[[CompensationPlan.ApprovalId]] AND a.[[CompensationPlanApproval.PlanId]] = p.[[CompensationPlan.Id]]
     WHERE p.[[CompensationPlan.Id]] = p_plan;
    IF NOT FOUND THEN RETURN 'attestation_plan_not_found'; END IF;
    IF split_part(split_part(l[2], '|', 1), '=', 2) <> v_creator::text THEN RETURN 'attestation_creator_mismatch'; END IF;
    IF split_part(split_part(l[3], '|', 1), '=', 2) <> v_approver::text THEN RETURN 'attestation_approver_mismatch'; END IF;
    v_at := split_part(l[2], '|', 3)::bigint;
    IF v_at > p_not_before + 300 OR v_at < p_not_before - 86400 THEN RETURN 'attestation_creator_time'; END IF;
    v_at := split_part(l[3], '|', 3)::bigint;
    IF v_at > p_not_before + 300 OR v_at < p_not_before - 86400 THEN RETURN 'attestation_approver_time'; END IF;
    v_head := public.{{ReplayGrant.FnPlanHead}}(p_plan, p_epoch, p_executor);
    IF v_head IS NULL THEN RETURN 'attestation_plan_not_found'; END IF;
    IF substr(l[4], 9) <> public.{{ReplayGrant.FnRequestDigest}}(v_head) THEN RETURN 'attestation_request_mismatch'; END IF;
    RETURN NULL;
END;
$$;

-- the insert-time rules of a grant (design 4.3)
CREATE FUNCTION public.{{ReplayGrant.FnGrantInsert}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p public.[[CompensationPlan.Table]]%ROWTYPE;
    k public.[[ReplaySignerKey.Table]]%ROWTYPE;
    v_approver uuid;
    v_problem text;
    v_now numeric;
    v_head text;
BEGIN
    SELECT * INTO p FROM public.[[CompensationPlan.Table]] WHERE [[CompensationPlan.Id]] = NEW.[[ReplayExecutionGrant.PlanId]];
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_plan_not_found'; END IF;
    IF p.[[CompensationPlan.Status]] <> 2 OR p.[[CompensationPlan.ApprovalId]] IS NULL THEN RAISE EXCEPTION 'grant_plan_not_approved'; END IF;
    v_problem := public.[[EpochGuard.FnApprovalSound]](p.[[CompensationPlan.Id]], p.[[CompensationPlan.ApprovalId]]);
    IF v_problem IS NOT NULL THEN RAISE EXCEPTION 'grant_plan_approval_unsound: %', v_problem; END IF;
    SELECT * INTO k FROM public.[[ReplaySignerKey.Table]] WHERE [[ReplaySignerKey.KeyId]] = NEW.[[ReplayExecutionGrant.KeyId]];
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_key_unknown'; END IF;
    IF k.[[ReplaySignerKey.RevokeFrom]] IS NOT NULL AND clock_timestamp() >= k.[[ReplaySignerKey.RevokeFrom]] THEN RAISE EXCEPTION 'grant_key_revoked'; END IF;
    -- a key with no signer is the installation key of resolution (ii), which is not built: until it is, every key must name the person it belongs to, and the grant's signer must be that person (decision D1: a separately registered signer)
    IF k.[[ReplaySignerKey.SignerUserId]] IS NULL THEN RAISE EXCEPTION 'grant_key_has_no_signer'; END IF;
    IF k.[[ReplaySignerKey.SignerUserId]] <> NEW.[[ReplayExecutionGrant.SignerUserId]] THEN RAISE EXCEPTION 'grant_signer_is_not_the_key_owner'; END IF;
    IF NEW.[[ReplayExecutionGrant.NotAfter]] - NEW.[[ReplayExecutionGrant.NotBefore]] > 7200 THEN RAISE EXCEPTION 'grant_window_longer_than_2_hours'; END IF;
    v_now := extract(epoch from clock_timestamp());
    IF v_now < NEW.[[ReplayExecutionGrant.NotBefore]] OR v_now > NEW.[[ReplayExecutionGrant.NotAfter]] THEN RAISE EXCEPTION 'grant_outside_its_window'; END IF;
    SELECT a.[[CompensationPlanApproval.Approver]] INTO v_approver FROM public.[[CompensationPlanApproval.Table]] a WHERE a.[[CompensationPlanApproval.Id]] = p.[[CompensationPlan.ApprovalId]];
    IF NOT public.[[EpochGuard.FnIndependent]](p.[[CompensationPlan.Id]], NEW.[[ReplayExecutionGrant.SignerUserId]]) OR NEW.[[ReplayExecutionGrant.SignerUserId]] = v_approver
       OR NEW.[[ReplayExecutionGrant.SignerUserId]] = NEW.[[ReplayExecutionGrant.ExecutorUserId]] THEN
        RAISE EXCEPTION 'grant_signer_not_independent';
    END IF;
    v_problem := public.{{ReplayGrant.FnAttestationProblem}}(NEW.[[ReplayExecutionGrant.Attestation]], p.[[CompensationPlan.Id]], NEW.[[ReplayExecutionGrant.EpochDigest]]::text, NEW.[[ReplayExecutionGrant.ExecutorUserId]], NEW.[[ReplayExecutionGrant.NotBefore]]);
    IF v_problem IS NOT NULL THEN RAISE EXCEPTION 'grant_attestation_invalid: %', v_problem; END IF;
    v_head := public.{{ReplayGrant.FnPlanHead}}(p.[[CompensationPlan.Id]], NEW.[[ReplayExecutionGrant.EpochDigest]]::text, NEW.[[ReplayExecutionGrant.ExecutorUserId]]);
    IF v_head IS NULL THEN RAISE EXCEPTION 'grant_plan_not_found'; END IF;
    IF NOT public.[[ReplayAuthorization.FnRsaVerify]](k.[[ReplaySignerKey.Modulus]], NEW.[[ReplayExecutionGrant.Signature]], convert_to(public.{{ReplayGrant.FnGrantMessage}}(v_head, NEW.[[ReplayExecutionGrant.Attestation]],
            NEW.[[ReplayExecutionGrant.Id]], NEW.[[ReplayExecutionGrant.SignerUserId]], NEW.[[ReplayExecutionGrant.KeyId]], NEW.[[ReplayExecutionGrant.NotBefore]], NEW.[[ReplayExecutionGrant.NotAfter]]), 'UTF8')) THEN
        RAISE EXCEPTION 'grant_signature_invalid';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{ReplayGrant.TrGrantVerify}} BEFORE INSERT ON public.[[ReplayExecutionGrant.Table]] FOR EACH ROW EXECUTE FUNCTION public.{{ReplayGrant.FnGrantInsert}}();

-- the insert-time rules of a grant use: the grant is the execution's (plan and executor) and its window still holds
CREATE FUNCTION public.{{ReplayGrant.FnUseInsert}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    g public.[[ReplayExecutionGrant.Table]]%ROWTYPE;
    x public.[[CompensationPlanExecution.Table]]%ROWTYPE;
    v_now numeric;
BEGIN
    SELECT * INTO g FROM public.[[ReplayExecutionGrant.Table]] WHERE [[ReplayExecutionGrant.Id]] = NEW.[[ReplayGrantUse.GrantId]];
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_use_grant_not_found'; END IF;
    SELECT * INTO x FROM public.[[CompensationPlanExecution.Table]] WHERE [[CompensationPlanExecution.Id]] = NEW.[[ReplayGrantUse.ExecutionId]];
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_use_execution_not_found'; END IF;
    IF x.[[CompensationPlanExecution.PlanId]] <> g.[[ReplayExecutionGrant.PlanId]] THEN RAISE EXCEPTION 'grant_use_wrong_plan'; END IF;
    IF x.[[CompensationPlanExecution.ExecutedBy]] <> g.[[ReplayExecutionGrant.ExecutorUserId]] THEN RAISE EXCEPTION 'grant_use_wrong_executor'; END IF;
    v_now := extract(epoch from clock_timestamp());
    IF v_now < g.[[ReplayExecutionGrant.NotBefore]] OR v_now > g.[[ReplayExecutionGrant.NotAfter]] THEN RAISE EXCEPTION 'grant_use_outside_its_window'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{ReplayGrant.TrUseVerify}} BEFORE INSERT ON public.[[ReplayGrantUse.Table]] FOR EACH ROW EXECUTE FUNCTION public.{{ReplayGrant.FnUseInsert}}();
`;

// inside every function body each table of the schema is named public.<table> (design 4.5): the template writes the prefix itself, so nothing is rewritten

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company." + MIGRATION + ".replay-grant.v1:" + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (!/\.(sql|cs)$/.test(e.name)) continue;
      const later = /^(\d{4})_company_/.exec(e.name);
      if (later && Number(later[1]) > Number(MIGRATION)) continue;   // a LATER company migration refers to this one's names
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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-" + MIGRATION + "-replay-grant-verification.cjs for company migration " + MIGRATION + " (Phase 3.8D threat T2, slice A2, insert-time verification of a replay grant). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// ReplayGrant names the functions and the two triggers of the migration.\n" + "public static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> ReplayGrantMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated replay-grant schema differs from the committed files"); process.exit(1); }
  console.log("replay-grant schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
