#!/usr/bin/env node
// Deterministic generator for company migration 0039 (Phase 3.8D threat T2, slice A3: the commit-time checks of an execution grant and the epoch text rebuilt from the rows; PROVISIONAL number, it follows the merge order).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{ReplayEpoch.FnRowsText}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.replay-epoch.v1:" + token)
// (same rules as the generators of 0036 to 0038: a collision aborts the run; names are fixed once committed). References to names that already exist are written [[Entity.Name]].
// The deferred trigger on the execution table must fire AFTER the completeness trigger of 0036 on the same event (deferred triggers run in name order): the generator checks it.
// Usage:  node tools/schema/company-0039-replay-epoch-commit.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0039";
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_replay_epoch_commit.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.ReplayEpoch.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map(["CompanySchema.cs", "CompanySchema.Costing.cs", "CompanySchema.CompensationPlan.cs", "CompanySchema.ReplayAuthorization.cs", "CompanySchema.ReplayGrant.cs"].flatMap((f) => [...parseDictionary(f)]));
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration ${MIGRATION}: the commit-time checks of a replay execution grant (Phase 3.8D threat T2, slice A3; design docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md sections 4.3, 4.4 and 16, message version hasm-replay-v5).
-- Forward-only and ADDITIVE: new functions and two new DEFERRED constraint triggers (one on the execution table of 0036, one on the grant-use table of 0037); no table is created or altered, nothing existing is replaced or switched off.
-- The company runtime role still holds SELECT only on the grant tables and on the four execution tables: nothing can reach these triggers through the application, activation stays closed.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-${MIGRATION}-replay-epoch-commit.cjs (logical dictionary: CompanySchema.ReplayEpoch.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: at commit every execution of the transaction must have exactly ONE grant use whose grant is the execution's (plan and executor), has not expired (database clock, epoch seconds), whose key was not revoked
-- before the grant was accepted, whose signature VERIFIES over the message rebuilt NOW from the rows (a closure, line or approval changed after the grant was accepted fails), and whose signed epoch digest equals the SHA-256 of the
-- EPOCH TEXT of design 4.4 rebuilt from the rows this execution wrote: operations (O), entries (E), layers (L), consumptions (C), the remaining figures of every layer touched (R) and the totals of every position touched (P).
-- A position's final figures are bound by its P line (the position row) and by the running figures of its last entry (E line); the cost position guard of 0014 keeps the two equal, so a mismatch cannot exist in the rows. The journal binding (design 4.8) is slice A4. A deviation anywhere (one amount, one quantity, an extra or missing row) changes the text and is refused.
-- The execution trigger is named so that it fires AFTER the completeness trigger of 0036 on the same event. Name resolution (design 4.5): no search_path and no SECURITY DEFINER; every relation is written public.<name>; the text uses only
-- fixed formats (lower-case uuids, decimal integers, numeric at its declared scale 10 or 6, dates by to_char with a fixed pattern, LF as chr(10)), so no session setting can change a byte.

-- the epoch text of an execution (design 4.4): O, E, L, C, R and P lines in this order, LF-joined with no trailing LF; an execution with no rows gives the empty string
CREATE FUNCTION public.{{ReplayEpoch.FnRowsText}}(p_execution uuid) RETURNS text LANGUAGE sql AS $$
    WITH x AS (
        SELECT e.[[CompensationPlanExecution.PostingId]] AS posting FROM public.[[CompensationPlanExecution.Table]] e WHERE e.[[CompensationPlanExecution.Id]] = p_execution
    ), mem AS (
        SELECT m.[[EpochMember.OperationId]] AS op, m.[[EpochMember.Role]] AS role, m.[[EpochMember.OriginalOperationId]] AS orig
          FROM public.[[EpochMember.Table]] m WHERE m.[[EpochMember.ExecutionId]] = p_execution
    ), ent AS (
        SELECT e.[[CostEntry.Id]] AS id, e.[[CostEntry.ItemId]] AS item, e.[[CostEntry.WarehouseId]] AS wh, e.[[CostEntry.Seq]] AS seq, m.role AS role, e.[[CostEntry.ReversesEntryId]] AS reverses,
               e.[[CostEntry.Kind]] AS kind, e.[[CostEntry.Direction]] AS direction, e.[[CostEntry.EffectiveDate]] AS d, e.[[CostEntry.Quantity]] AS q, e.[[CostEntry.Amount]] AS a,
               e.[[CostEntry.QuantityAfter]] AS qa, e.[[CostEntry.ValueAfter]] AS va
          FROM public.[[CostEntry.Table]] e JOIN mem m ON m.op = e.[[CostEntry.OperationId]]
    ), lay AS (
        SELECT l.[[CostLayer.Id]] AS id, l.[[CostLayer.ItemId]] AS item, l.[[CostLayer.WarehouseId]] AS wh, l.[[CostLayer.Seq]] AS seq, en.seq AS entry_seq, l.[[CostLayer.OriginalQuantity]] AS oq,
               l.[[CostLayer.OriginalAmount]] AS oa, l.[[CostLayer.SourceLayerId]] AS source_layer
          FROM public.[[CostLayer.Table]] l JOIN ent en ON en.id = l.[[CostLayer.EntryId]]
    ), con AS (
        SELECT en.item AS e_item, en.wh AS e_wh, en.seq AS e_seq, l.[[CostLayer.ItemId]] AS l_item, l.[[CostLayer.WarehouseId]] AS l_wh, l.[[CostLayer.Seq]] AS l_seq, c.[[CostConsumption.LayerId]] AS layer_id,
               c.[[CostConsumption.Quantity]] AS q, c.[[CostConsumption.Amount]] AS a, c.[[CostConsumption.Restores]] AS restores
          FROM public.[[CostConsumption.Table]] c JOIN ent en ON en.id = c.[[CostConsumption.EntryId]] JOIN public.[[CostLayer.Table]] l ON l.[[CostLayer.Id]] = c.[[CostConsumption.LayerId]]
    ), touched AS (
        SELECT id FROM lay UNION SELECT layer_id FROM con
    ), lines AS (
        SELECT 1 AS grp, m.role::bigint AS n0, m.orig AS u1, NULL::uuid AS u2, NULL::bigint AS n1, NULL::uuid AS u3, NULL::uuid AS u4, NULL::bigint AS n2, NULL::boolean AS b1,
               'O|' || m.role::text || '|' || COALESCE(m.orig::text, '-') || '|' || o.[[CostOperation.Kind]]::text || '|' || to_char(o.[[CostOperation.EffectiveDate]], 'YYYY-MM-DD')
               || '|' || COALESCE(o.[[CostOperation.ReversesOperationId]]::text, '-') || '|' || COALESCE(encode(sha256(convert_to(o.[[CostOperation.SourceType]], 'UTF8')), 'hex'), '-')
               || '|' || CASE WHEN o.[[CostOperation.SourceId]] IS NULL THEN '-' WHEN o.[[CostOperation.SourceId]] = (SELECT posting FROM x) THEN 'execution-posting' ELSE o.[[CostOperation.SourceId]]::text END
               || '|' || encode(sha256(convert_to(o.[[CostOperation.Key]], 'UTF8')), 'hex') || '|' || o.[[CostOperation.CreatedBy]]::text AS line
          FROM mem m JOIN public.[[CostOperation.Table]] o ON o.[[CostOperation.Id]] = m.op
        UNION ALL
        SELECT 2, 0, e.item, e.wh, e.seq, NULL, NULL, NULL, NULL,
               'E|' || e.item::text || '|' || e.wh::text || '|' || e.seq::text || '|' || e.role::text || '|'
               || CASE e.role WHEN 1 THEN COALESCE(e.reverses::text, '-')
                              WHEN 3 THEN COALESCE((SELECT g.[[EpochEntryLineage.OriginalEntryId]]::text FROM public.[[EpochEntryLineage.Table]] g
                                                     WHERE g.[[EpochEntryLineage.RebookEntryId]] = e.id AND g.[[EpochEntryLineage.ExecutionId]] = p_execution), '-')
                              ELSE '-' END
               || '|' || e.kind::text || '|' || e.direction::text || '|' || to_char(e.d, 'YYYY-MM-DD') || '|' || e.q::numeric(28,10)::text || '|' || e.a::numeric(28,6)::text
               || '|' || e.qa::numeric(28,10)::text || '|' || e.va::numeric(28,6)::text
          FROM ent e
        UNION ALL
        SELECT 3, 0, l.item, l.wh, l.seq, NULL, NULL, NULL, NULL,
               'L|' || l.item::text || '|' || l.wh::text || '|' || l.seq::text || '|' || l.entry_seq::text || '|' || l.oq::numeric(28,10)::text || '|' || l.oa::numeric(28,6)::text
               || '|' || COALESCE((SELECT s.[[CostLayer.ItemId]]::text || '/' || s.[[CostLayer.WarehouseId]]::text || '/' || s.[[CostLayer.Seq]]::text
                                     FROM public.[[CostLayer.Table]] s WHERE s.[[CostLayer.Id]] = l.source_layer), '-')
          FROM lay l
        UNION ALL
        SELECT 4, 0, c.e_item, c.e_wh, c.e_seq, c.l_item, c.l_wh, c.l_seq, c.restores,
               'C|' || c.e_item::text || '|' || c.e_wh::text || '|' || c.e_seq::text || '|' || c.l_item::text || '|' || c.l_wh::text || '|' || c.l_seq::text
               || '|' || c.q::numeric(28,10)::text || '|' || c.a::numeric(28,6)::text || '|' || CASE WHEN c.restores THEN 't' ELSE 'f' END
          FROM con c
        UNION ALL
        SELECT 5, 0, l.[[CostLayer.ItemId]], l.[[CostLayer.WarehouseId]], l.[[CostLayer.Seq]], NULL, NULL, NULL, NULL,
               'R|' || l.[[CostLayer.ItemId]]::text || '|' || l.[[CostLayer.WarehouseId]]::text || '|' || l.[[CostLayer.Seq]]::text || '|' || l.[[CostLayer.RemainingQuantity]]::numeric(28,10)::text
               || '|' || l.[[CostLayer.RemainingAmount]]::numeric(28,6)::text
          FROM public.[[CostLayer.Table]] l JOIN touched t ON t.id = l.[[CostLayer.Id]]
        UNION ALL
        SELECT 6, 0, g.item, g.wh, NULL, NULL, NULL, NULL, NULL,
               'P|' || g.item::text || '|' || g.wh::text || '|' || (g.first_seq - 1)::text || '|' || po.[[CostPosition.LastSeq]]::text || '|' || po.[[CostPosition.Quantity]]::numeric(28,10)::text
               || '|' || po.[[CostPosition.Value]]::numeric(28,6)::text
               || '|' || (SELECT count(*) FROM public.[[CostLayer.Table]] o WHERE o.[[CostLayer.ItemId]] = g.item AND o.[[CostLayer.WarehouseId]] = g.wh AND o.[[CostLayer.RemainingQuantity]] > 0)::text
          FROM (SELECT e.item, e.wh, min(e.seq) AS first_seq FROM ent e GROUP BY e.item, e.wh) g
          JOIN public.[[CostPosition.Table]] po ON po.[[CostPosition.ItemId]] = g.item AND po.[[CostPosition.WarehouseId]] = g.wh
    )
    SELECT COALESCE(string_agg(line, chr(10) ORDER BY grp, n0, u1, u2, n1, u3, u4, n2, b1), '') FROM lines
$$;

-- every commit-time rule for ONE execution; raises a reason when any fails
CREATE FUNCTION public.{{ReplayEpoch.FnCheckExecution}}(p_execution uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    x public.[[CompensationPlanExecution.Table]]%ROWTYPE;
    g public.[[ReplayExecutionGrant.Table]]%ROWTYPE;
    k public.[[ReplaySignerKey.Table]]%ROWTYPE;
    v_uses bigint;
    v_head text;
BEGIN
    SELECT * INTO x FROM public.[[CompensationPlanExecution.Table]] WHERE [[CompensationPlanExecution.Id]] = p_execution;
    IF NOT FOUND THEN RAISE EXCEPTION 'execution_not_found'; END IF;
    SELECT count(*) INTO v_uses FROM public.[[ReplayGrantUse.Table]] WHERE [[ReplayGrantUse.ExecutionId]] = p_execution;
    IF v_uses <> 1 THEN RAISE EXCEPTION 'execution_needs_exactly_one_grant_use'; END IF;
    SELECT gr.* INTO g FROM public.[[ReplayExecutionGrant.Table]] gr JOIN public.[[ReplayGrantUse.Table]] u ON u.[[ReplayGrantUse.GrantId]] = gr.[[ReplayExecutionGrant.Id]] WHERE u.[[ReplayGrantUse.ExecutionId]] = p_execution;
    IF g.[[ReplayExecutionGrant.PlanId]] <> x.[[CompensationPlanExecution.PlanId]] OR g.[[ReplayExecutionGrant.ExecutorUserId]] <> x.[[CompensationPlanExecution.ExecutedBy]] THEN
        RAISE EXCEPTION 'grant_does_not_match_the_execution';
    END IF;
    IF extract(epoch from clock_timestamp()) > g.[[ReplayExecutionGrant.NotAfter]] THEN RAISE EXCEPTION 'grant_expired_at_commit'; END IF;
    SELECT * INTO k FROM public.[[ReplaySignerKey.Table]] WHERE [[ReplaySignerKey.KeyId]] = g.[[ReplayExecutionGrant.KeyId]];
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_key_unknown'; END IF;
    IF k.[[ReplaySignerKey.RevokeFrom]] IS NOT NULL AND g.[[ReplayExecutionGrant.AcceptedAtUtc]] >= k.[[ReplaySignerKey.RevokeFrom]] THEN RAISE EXCEPTION 'grant_key_revoked'; END IF;
    -- the signature again, over the message rebuilt NOW from the rows
    v_head := public.[[ReplayGrant.FnPlanHead]](x.[[CompensationPlanExecution.PlanId]], g.[[ReplayExecutionGrant.EpochDigest]]::text, g.[[ReplayExecutionGrant.ExecutorUserId]]);
    IF v_head IS NULL THEN RAISE EXCEPTION 'grant_plan_not_found'; END IF;
    IF NOT public.[[ReplayAuthorization.FnRsaVerify]](k.[[ReplaySignerKey.Modulus]], g.[[ReplayExecutionGrant.Signature]], convert_to(public.[[ReplayGrant.FnGrantMessage]](v_head, g.[[ReplayExecutionGrant.Attestation]],
            g.[[ReplayExecutionGrant.Id]], g.[[ReplayExecutionGrant.SignerUserId]], g.[[ReplayExecutionGrant.KeyId]], g.[[ReplayExecutionGrant.NotBefore]], g.[[ReplayExecutionGrant.NotAfter]]), 'UTF8')) THEN
        RAISE EXCEPTION 'grant_signature_invalid_at_commit';
    END IF;
    -- the epoch text rebuilt from the rows hashes to the signed epoch digest
    IF encode(sha256(convert_to(public.{{ReplayEpoch.FnRowsText}}(p_execution), 'UTF8')), 'hex') <> g.[[ReplayExecutionGrant.EpochDigest]]::text THEN RAISE EXCEPTION 'epoch_differs_from_the_signed_epoch'; END IF;
END;
$$;

CREATE FUNCTION public.{{ReplayEpoch.FnCommitExecution}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM public.{{ReplayEpoch.FnCheckExecution}}(NEW.[[CompensationPlanExecution.Id]]);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{ReplayEpoch.TrCommitExecution}} AFTER INSERT ON public.[[CompensationPlanExecution.Table]] DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.{{ReplayEpoch.FnCommitExecution}}();

CREATE FUNCTION public.{{ReplayEpoch.FnCommitUse}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM public.{{ReplayEpoch.FnCheckExecution}}(NEW.[[ReplayGrantUse.ExecutionId]]);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{ReplayEpoch.TrCommitUse}} AFTER INSERT ON public.[[ReplayGrantUse.Table]] DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.{{ReplayEpoch.FnCommitUse}}();
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company." + MIGRATION + ".replay-epoch.v1:" + token).digest("hex").slice(0, 8);

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
  if (!(physical("ReplayEpoch.TrCommitExecution") > ext("CompensationPlanExecution.TrCommit"))) throw new Error("the execution trigger must sort after the completeness trigger of 0036");
  const sql = body.replace(/\{\{([A-Za-z0-9.]+)\}\}/g, (_, t) => byToken.get(t));
  const entities = [...new Set(tokens.map((t) => t.split(".")[0]))];
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-" + MIGRATION + "-replay-epoch-commit.cjs for company migration " + MIGRATION + " (Phase 3.8D threat T2, slice A3, the commit-time checks of a replay grant). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// ReplayEpoch names the functions and the two deferred triggers of the migration.\n" + "public static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> ReplayEpochMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated replay-epoch schema differs from the committed files"); process.exit(1); }
  console.log("replay-epoch schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
