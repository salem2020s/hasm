#!/usr/bin/env node
// Deterministic generator for company migration 0032 (Phase 3.13 slice S5a: approval policies for commercial documents; the number follows the merge order, change MIGRATION below and rename the committed files if it moves).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CommercialDocument.DocType}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.commercial-policies.v1:" + token)
// (same rules as tools/schema/company-0026-pricing.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migrations 0008 to 0010, 0028 and 0029) are written
// [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0032-commercial-policies.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0032";   // the company migration number: it is part of the name seed, so a renumbering is a regeneration
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_commercial_policies.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.CommercialPolicy.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Configuration.cs"), ...parseDictionary("CompanySchema.Commercial.cs"), ...parseDictionary("CompanySchema.CommercialApproval.cs"), ...parseDictionary("CompanySchema.LedgerApprovals.cs"), ...parseDictionary("CompanySchema.LedgerPolicies.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration ${MIGRATION}: approval policies for commercial documents (Phase 3.13 slice S5a). Forward-only and ADDITIVE: new functions and two new triggers only; no table is created or altered, no existing function or trigger
-- (the journal's) is replaced or dropped, nothing is seeded and no existing row is read or modified. Opaque physical names (D-029) generated deterministically by tools/schema/company-${MIGRATION}-commercial-policies.cjs
-- (logical dictionary: CompanySchema.CommercialPolicy.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.13.md S5a; decisions D-140 and D-141): the ordered approval-policy engine of 3.2C (ApprovalPolicy, ApprovalPolicyStage, DocumentApprovalBinding, ApprovalDecision) was wired to the journal in SQL.
-- These functions give a SUBMITTED COMMERCIAL DOCUMENT the same rules: the governing policy is picked by document type, branch and the BASE value of the SEALED total (gross times the sealed exchange rate, rounded to the minor units of the base currency like the journal) at submit (D-140);
-- the plan is the stages of that policy, raised to the plan the revision was submitted under (an append-only floor, so a policy can only get stricter for a pending document); every decision is cast for the first open stage by
-- someone independent of the creator, the last editor and the submitter; and the document cannot become approved before the plan of its revision is satisfied. A document with NO matching policy has an empty plan and keeps
-- the single independent approval of S1b (D-141).
-- Invariants enforced by PostgreSQL:
--   * decisions on a commercial document are cast on its CURRENT revision while it is submitted (a stale decision never counts), approve only (a rejection is the status change), in stage order, one per approver per revision;
--   * the approver of a decision is never the creator, the last editor or the submitter;
--   * the submitted -> approved transition needs the plan of the submitted revision satisfied and the approver of the document must be one of the approvers of that revision;
--   * the policy conditions that only make sense for a journal (journal type, manual rate override, soft-closed period, closing kind) never match a commercial document: a policy that carries one does not apply to it.
-- The journal's own functions and triggers are NOT touched: the decision trigger below returns at once for any row that is not a decision on a commercial document.

CREATE FUNCTION {{CommercialPolicy.FnPick}}(p_doc uuid) RETURNS TABLE (policy_id uuid, policy_revision bigint) LANGUAGE sql STABLE AS $$
    WITH d AS (
        SELECT cd.[[CommercialDocument.Id]] AS id, cd.[[CommercialDocument.DocType]] AS doc_type, cd.[[CommercialDocument.BranchId]] AS branch,
               -- the BASE value, rounded half away from zero to the minor units of the base currency of the company exactly as the journal compares base amounts (F review M1); when the base currency or its minor units are
               -- undefined the value is NULL and every policy of the type and branch applies (fail closed: the stricter reading)
               (SELECT ROUND(COALESCE(cd.[[CommercialDocumentApproval.TotalGross]] * cd.[[CommercialDocumentApproval.ExchangeRate]], 0), cu.[[Currency.MinorUnits]]::integer)
                  FROM [[CompanyCurrency.Table]] cc JOIN [[Currency.Table]] cu ON cu.[[Currency.Code]] = cc.[[CompanyCurrency.Code]] WHERE cc.[[CompanyCurrency.IsBase]] AND cc.[[CompanyCurrency.Enabled]] LIMIT 1) AS base_total
        FROM [[CommercialDocument.Table]] cd WHERE cd.[[CommercialDocument.Id]] = p_doc),
    cand AS (
        SELECT p.[[ApprovalPolicy.Id]] AS pid, p.[[ApprovalPolicy.Revision]] AS prev, p.[[ApprovalPolicy.CreatedAtUtc]] AS created, COALESCE(st.[[ApprovalPolicyStage.Required]], p.[[ApprovalPolicy.RequiredApprovals]]) AS required
        FROM [[ApprovalPolicy.Table]] p CROSS JOIN d
        LEFT JOIN [[ApprovalPolicyStage.Table]] st ON st.[[ApprovalPolicyStage.PolicyId]] = p.[[ApprovalPolicy.Id]] AND st.[[ApprovalPolicyStage.PolicyRevision]] = p.[[ApprovalPolicy.Revision]]
        WHERE p.[[ApprovalPolicy.IsActive]] AND p.[[ApprovalPolicy.DocType]] = d.doc_type AND p.[[ApprovalPolicy.Source]] IN (1, 3)
          AND (p.[[ApprovalPolicy.BranchId]] IS NULL OR p.[[ApprovalPolicy.BranchId]] = d.branch) AND (d.base_total IS NULL OR d.base_total >= p.[[ApprovalPolicy.MinAmount]])
          AND p.[[ApprovalPolicyConditions.JournalType]] IS NULL AND p.[[ApprovalPolicyConditions.RateOverride]] IS NULL AND p.[[ApprovalPolicyConditions.SoftClosed]] IS NULL AND p.[[ClosingLogic.PolicyClosingKind]] IS NULL)
    SELECT pid, prev FROM cand GROUP BY pid, prev, created ORDER BY sum(required) DESC, count(*) DESC, created ASC, pid ASC LIMIT 1
$$;

CREATE FUNCTION {{CommercialPolicy.FnPlan}}(p_doc uuid, p_revision bigint) RETURNS TABLE (stage_no integer, required integer, role_id uuid) LANGUAGE sql STABLE AS $$
    WITH cur AS (
        SELECT COALESCE(st.[[ApprovalPolicyStage.StageNo]], 1)::integer AS stage_no, COALESCE(st.[[ApprovalPolicyStage.Required]], p.[[ApprovalPolicy.RequiredApprovals]])::integer AS required, st.[[ApprovalPolicyStage.RoleId]] AS role_id
        FROM {{CommercialPolicy.FnPick}}(p_doc) pk JOIN [[ApprovalPolicy.Table]] p ON p.[[ApprovalPolicy.Id]] = pk.policy_id
        LEFT JOIN [[ApprovalPolicyStage.Table]] st ON st.[[ApprovalPolicyStage.PolicyId]] = p.[[ApprovalPolicy.Id]] AND st.[[ApprovalPolicyStage.PolicyRevision]] = p.[[ApprovalPolicy.Revision]]),
    bound AS (
        SELECT b.[[DocumentApprovalBinding.StageNo]]::integer AS stage_no, b.[[DocumentApprovalBinding.Required]]::integer AS required, b.[[DocumentApprovalBinding.RoleId]] AS role_id
        FROM [[DocumentApprovalBinding.Table]] b JOIN [[CommercialDocument.Table]] cd ON cd.[[CommercialDocument.Id]] = b.[[DocumentApprovalBinding.DocumentId]] AND cd.[[CommercialDocument.DocType]] = b.[[DocumentApprovalBinding.DocType]]
        WHERE b.[[DocumentApprovalBinding.DocumentId]] = p_doc AND b.[[DocumentApprovalBinding.DocumentRevision]] = p_revision)
    SELECT COALESCE(c.stage_no, b.stage_no), GREATEST(COALESCE(c.required, 0), COALESCE(b.required, 0)), COALESCE(b.role_id, c.role_id)
    FROM cur c FULL OUTER JOIN bound b ON b.stage_no = c.stage_no
    ORDER BY 1
$$;

CREATE FUNCTION {{CommercialPolicy.FnSatisfied}}(p_doc uuid, p_revision bigint) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT NOT EXISTS (SELECT 1 FROM [[ApprovalDecision.Table]] d JOIN [[CommercialDocument.Table]] cd ON cd.[[CommercialDocument.Id]] = d.[[ApprovalDecision.DocumentId]] AND cd.[[CommercialDocument.DocType]] = d.[[ApprovalDecision.DocType]]
                       WHERE d.[[ApprovalDecision.DocumentId]] = p_doc AND d.[[ApprovalDecision.DocumentRevision]] = p_revision AND d.[[ApprovalDecision.Decision]] = 2)
       AND NOT EXISTS (SELECT 1 FROM {{CommercialPolicy.FnPlan}}(p_doc, p_revision) pl
                       WHERE (SELECT count(DISTINCT d.[[ApprovalDecision.Approver]]) FROM [[ApprovalDecision.Table]] d JOIN [[CommercialDocument.Table]] cd ON cd.[[CommercialDocument.Id]] = d.[[ApprovalDecision.DocumentId]] AND cd.[[CommercialDocument.DocType]] = d.[[ApprovalDecision.DocType]]
                              WHERE d.[[ApprovalDecision.DocumentId]] = p_doc AND d.[[ApprovalDecision.DocumentRevision]] = p_revision AND d.[[ApprovalDecision.Decision]] = 1 AND d.[[ApprovalStaging.DecisionStage]] = pl.stage_no) < pl.required)
$$;

CREATE FUNCTION {{CommercialPolicy.FnDecision}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    d record;
    v_open integer;
BEGIN
    -- the document row is locked first: every decision on one document is serialized here for every writer, so two approvers can never both see the same open stage
    SELECT cd.[[CommercialDocument.Status]] AS status, cd.[[CommercialDocument.Revision]] AS revision, cd.[[CommercialDocument.CreatedBy]] AS created_by, cd.[[CommercialDocument.SubmittedBy]] AS submitted_by,
           cd.[[CommercialDocumentApproval.LastEditedBy]] AS last_edited_by
      INTO d FROM [[CommercialDocument.Table]] cd WHERE cd.[[CommercialDocument.Id]] = NEW.[[ApprovalDecision.DocumentId]] AND cd.[[CommercialDocument.DocType]] = NEW.[[ApprovalDecision.DocType]] FOR NO KEY UPDATE;
    IF NOT FOUND THEN RETURN NEW; END IF;   -- not a decision on a commercial document: the journal and every other document keep their own rules, untouched
    IF NEW.[[ApprovalDecision.Decision]] <> 1 THEN RAISE EXCEPTION 'a commercial document is rejected by its status change, not by a decision row'; END IF;
    IF d.status <> 2 OR d.revision <> NEW.[[ApprovalDecision.DocumentRevision]] THEN RAISE EXCEPTION 'a decision is cast on the current revision of a submitted commercial document'; END IF;
    IF NEW.[[ApprovalDecision.Approver]] = d.created_by OR NEW.[[ApprovalDecision.Approver]] = d.submitted_by OR NEW.[[ApprovalDecision.Approver]] = d.last_edited_by THEN
        RAISE EXCEPTION 'the approver must be independent of the creator, the last editor and the submitter';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM {{CommercialPolicy.FnPlan}}(NEW.[[ApprovalDecision.DocumentId]], NEW.[[ApprovalDecision.DocumentRevision]])) THEN RAISE EXCEPTION 'no approval stage applies to this commercial document'; END IF;
    SELECT min(pl.stage_no) INTO v_open FROM {{CommercialPolicy.FnPlan}}(NEW.[[ApprovalDecision.DocumentId]], NEW.[[ApprovalDecision.DocumentRevision]]) pl
    WHERE (SELECT count(DISTINCT x.[[ApprovalDecision.Approver]]) FROM [[ApprovalDecision.Table]] x
           WHERE x.[[ApprovalDecision.DocType]] = NEW.[[ApprovalDecision.DocType]] AND x.[[ApprovalDecision.DocumentId]] = NEW.[[ApprovalDecision.DocumentId]] AND x.[[ApprovalDecision.DocumentRevision]] = NEW.[[ApprovalDecision.DocumentRevision]]
             AND x.[[ApprovalDecision.Decision]] = 1 AND x.[[ApprovalStaging.DecisionStage]] = pl.stage_no) < pl.required;
    IF v_open IS NULL THEN RAISE EXCEPTION 'every approval stage is already complete'; END IF;
    IF NEW.[[ApprovalStaging.DecisionStage]] <> v_open THEN RAISE EXCEPTION 'a decision must be cast for the open approval stage'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CommercialPolicy.TrDecision}} BEFORE INSERT ON [[ApprovalDecision.Table]] FOR EACH ROW EXECUTE FUNCTION {{CommercialPolicy.FnDecision}}();

CREATE FUNCTION {{CommercialPolicy.FnGate}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.[[CommercialDocument.Status]] = 2 AND NEW.[[CommercialDocument.Status]] = 3 AND EXISTS (SELECT 1 FROM {{CommercialPolicy.FnPlan}}(OLD.[[CommercialDocument.Id]], OLD.[[CommercialDocument.Revision]])) THEN
        IF NOT {{CommercialPolicy.FnSatisfied}}(OLD.[[CommercialDocument.Id]], OLD.[[CommercialDocument.Revision]]) THEN RAISE EXCEPTION 'the required approvals for this revision are missing'; END IF;
        IF NOT EXISTS (SELECT 1 FROM [[ApprovalDecision.Table]] d WHERE d.[[ApprovalDecision.DocType]] = OLD.[[CommercialDocument.DocType]] AND d.[[ApprovalDecision.DocumentId]] = OLD.[[CommercialDocument.Id]]
                       AND d.[[ApprovalDecision.DocumentRevision]] = OLD.[[CommercialDocument.Revision]] AND d.[[ApprovalDecision.Decision]] = 1 AND d.[[ApprovalDecision.Approver]] = NEW.[[CommercialDocumentApproval.ApprovedBy]]) THEN
            RAISE EXCEPTION 'the approver of a document must be one of the approvers of its revision';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CommercialPolicy.TrGate}} BEFORE UPDATE ON [[CommercialDocument.Table]] FOR EACH ROW EXECUTE FUNCTION {{CommercialPolicy.FnGate}}();
`
function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update(`hasm.company.${MIGRATION}.commercial-policies.v1:` + token).digest("hex").slice(0, 8);

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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-0032-commercial-policies.cjs for company migration " + MIGRATION + " (Phase 3.13 slice S5a, commercial policies). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// CommercialPolicy names the functions and the two triggers of slice S5a (no table).\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> CommercialPolicyMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated commercial-policies schema differs from the committed files"); process.exit(1); }
  console.log("commercial-policies schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
