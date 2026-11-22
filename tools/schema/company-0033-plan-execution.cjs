#!/usr/bin/env node
// Deterministic generator for company migration 0033 (Phase 3.8D step 4: the X1-A database layer of plan storage, approval and atomic execution; PROVISIONAL number: it follows the merge order, change MIGRATION below,
// rename the committed files and regenerate if it moves).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CompensationPlan.Status}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.plan-execution.v1:" + token)
// (same rules as tools/schema/company-0032-commercial-policies.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist (migrations 0002, 0004 to 0006, 0011, 0013, 0014,
// 0016 and 0020) are written [[Entity.Name]] and resolved from the committed dictionaries, never re-derived.
// Usage:  node tools/schema/company-0033-plan-execution.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0033";   // the company migration number: it is part of the name seed, so a renumbering is a regeneration
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_plan_execution.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.CompensationPlan.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map(["CompanySchema.cs", "CompanySchema.Branches.cs", "CompanySchema.Warehouses.cs", "CompanySchema.Fiscal.cs", "CompanySchema.Accounts.cs", "CompanySchema.LedgerJournal.cs", "CompanySchema.Inventory.cs",
  "CompanySchema.StockDocuments.cs", "CompanySchema.Costing.cs", "CompanySchema.StockPosting.cs", "CompanySchema.NegativeStock.cs"].flatMap((f) => [...parseDictionary(f)]));
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

// the seven application fingerprints of a plan (design 3.1); the closure hash is the eighth member of the set and is computed by PostgreSQL only
const FP = ["PlanFingerprint", "ComparisonFingerprint", "EvidenceFingerprint", "PeriodsFingerprint", "PositionFingerprint", "PolicyFingerprint", "AccountingFingerprint"];
const fpColumns = (entity) => FP.map((f) => `    {{${entity}.${f}}} char(64) NOT NULL,`).join("\n");
const fpHex = (entity) => FP.map((f) => `{{${entity}.${f}}} ~ '^[0-9a-f]{64}$'`).join(" AND ");
const fpChanged = FP.map((f) => `NEW.{{CompensationPlan.${f}}} IS DISTINCT FROM OLD.{{CompensationPlan.${f}}}`).join(" OR ");
const fpDiffers = FP.map((f) => `a.{{CompensationPlanApproval.${f}}} <> p.{{CompensationPlan.${f}}}`).join(" OR ");

const TEMPLATE = `-- Migration ${MIGRATION}: plan storage, approval and atomic execution of a compensation plan, database layer (Phase 3.8D step 4; owner decision X1-A of 2026-11-21 with mandatory safeguards;
-- design docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md revision 3, confirmed by the independent review docs/review-records/PR-48-plan-execution-design-rev3.md). PROVISIONAL NUMBER: it follows the merge order.
-- Forward-only and ADDITIVE: new tables, new functions and new triggers only. Nothing existing is removed, replaced, altered, paused or relaxed: no existing function is redefined, no existing trigger is touched,
-- no existing constraint is changed, nothing is seeded and no existing row is read or modified. Opaque physical names (D-029) generated deterministically by tools/schema/company-${MIGRATION}-plan-execution.cjs
-- (logical dictionary: CompanySchema.CompensationPlan.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: a back-dated ordinary stock movement changes the cost of every later movement of the positions it reaches. History is never edited (D-044/D-045): the later entries are taken back by storno entries,
-- the back-dated event is booked, and the later entries are booked again with the replayed amounts ("replay epoch"). This migration holds the records and the database rules of that exception; it contains NO path that
-- executes one. Until the executor exists nothing writes these tables, and the rules below only TIGHTEN what PostgreSQL accepts.
--
-- Invariants enforced by PostgreSQL (for every role, the owner included; never by a session setting):
--   * F1 closed: a cost entry dated before the last date of its position is refused, unless its operation is an epoch member of the execution of an approved plan written in THIS transaction (design 3.3(1));
--   * an entry-level reversal link outside a reversal operation is accepted only for an epoch storno, on the same position, of an entry of the member's original operation, with the original figures and date (3.3(2));
--   * every entry of an epoch member lies on a position of the plan's approved closure, in an Open fiscal period (read FOR SHARE), on a position without an open negative-stock obligation and outside the closure of
--     any other executed plan (3.3(2a), 3.3(3), X2, X5);
--   * a reversal operation of an epoch member (storno, event, rebook) or of an operation with stornoed entries is refused (3.3(4), X6);
--   * a stock document bound to a proposed or approved plan is posted only inside that plan's execution (X7);
--   * a plan's payload and fingerprints are immutable; its closure and lines are written only in its creating transaction; the closure hash is computed by PostgreSQL only, in a byte-exact form, and re-checked at
--     approval, at execution and at commit; status moves only along proposed -> approved/rejected/cancelled/stale and approved -> executed/cancelled/stale;
--   * an approval is unique per plan revision, repeats the plan's fingerprints exactly, is never given by the plan's creator or by the creator, an editor or the submitter of the event's stock document (X3), needs that
--     document approved at the planned revision, and is committed only together with the plan's status change;
--   * one execution per plan and one audit event per execution; the executor is not the plan's creator; at commit the plan is executed in the same transaction, the epoch is complete (members, storno/rebook pairs,
--     lineage, the event's posting) and the plan's business audit event exists with the plan-executed type, this plan as subject and this execution as target (3.4);
--   * (review round) an entry takes the next sequence number of its position and the position is moved at least to it at commit (sequence and last date), so the F1 anchor follows the entries; a reversal entry takes back
--     an entry of the operation its reversal operation names and never one of an epoch member; no plan, approval or execution exists for a document that already has a posting; every table named inside a function is public.-qualified;
--   * every new record is append-only (never updated or removed), except the plan's status transition.
-- plan status: 1 proposed, 2 approved, 3 executed, 4 rejected, 5 cancelled, 6 stale. plan event kind: 1 created, 2 approved, 3 rejected, 4 cancelled, 5 executed, 6 staled. epoch member role: 1 storno, 2 event, 3 rebook.
-- The business audit event type of an executed plan is 1154 (CostingAuditEvents.PlanExecuted).

-- ---------------------------------------------------------------------------------------------------------------- the plan
CREATE TABLE {{CompensationPlan}} (
    {{CompensationPlan.Id}} uuid NOT NULL,
    {{CompensationPlan.ItemId}} uuid NOT NULL,
    {{CompensationPlan.WarehouseId}} uuid NOT NULL,
    {{CompensationPlan.DocumentId}} uuid NOT NULL,
    {{CompensationPlan.DocumentRevision}} bigint NOT NULL,
    {{CompensationPlan.Payload}} text NOT NULL,
${fpColumns("CompensationPlan")}
    {{CompensationPlan.ClosureHash}} char(64) NOT NULL,
    {{CompensationPlan.Status}} smallint NOT NULL,
    {{CompensationPlan.Revision}} bigint NOT NULL,
    {{CompensationPlan.CreatedBy}} uuid NOT NULL,
    {{CompensationPlan.CreatedAtUtc}} timestamptz NOT NULL,
    {{CompensationPlan.CreatedXact}} xid8 NOT NULL,
    {{CompensationPlan.ApprovalId}} uuid NULL,
    {{CompensationPlan.ApprovedBy}} uuid NULL,
    {{CompensationPlan.ApprovedAtUtc}} timestamptz NULL,
    {{CompensationPlan.ExecutedXact}} xid8 NULL,
    {{CompensationPlan.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{CompensationPlan.PkId}} PRIMARY KEY ({{CompensationPlan.Id}}),
    CONSTRAINT {{CompensationPlan.FkItem}} FOREIGN KEY ({{CompensationPlan.ItemId}}) REFERENCES [[Item.Table]] ([[Item.Id]]),
    CONSTRAINT {{CompensationPlan.FkWarehouse}} FOREIGN KEY ({{CompensationPlan.WarehouseId}}) REFERENCES [[Warehouse.Table]] ([[Warehouse.Id]]),
    CONSTRAINT {{CompensationPlan.FkDocument}} FOREIGN KEY ({{CompensationPlan.DocumentId}}) REFERENCES [[StockDocument.Table]] ([[StockDocument.Id]]),
    CONSTRAINT {{CompensationPlan.CkValues}} CHECK ({{CompensationPlan.Status}} IN (1, 2, 3, 4, 5, 6) AND {{CompensationPlan.Revision}} > 0 AND {{CompensationPlan.DocumentRevision}} > 0
        AND char_length({{CompensationPlan.Payload}}) >= 2 AND {{CompensationPlan.ClosureHash}} ~ '^[0-9a-f]{64}$'
        AND ${fpHex("CompensationPlan")}),
    CONSTRAINT {{CompensationPlan.CkApproval}} CHECK (
        ({{CompensationPlan.ApprovalId}} IS NULL) = ({{CompensationPlan.ApprovedBy}} IS NULL) AND ({{CompensationPlan.ApprovalId}} IS NULL) = ({{CompensationPlan.ApprovedAtUtc}} IS NULL)
        AND ({{CompensationPlan.Status}} NOT IN (2, 3) OR {{CompensationPlan.ApprovalId}} IS NOT NULL) AND ({{CompensationPlan.Status}} NOT IN (1, 4) OR {{CompensationPlan.ApprovalId}} IS NULL)
        AND ({{CompensationPlan.ApprovedBy}} IS NULL OR {{CompensationPlan.ApprovedBy}} <> {{CompensationPlan.CreatedBy}})
        AND ({{CompensationPlan.Status}} = 3) = ({{CompensationPlan.ExecutedXact}} IS NOT NULL))
);
-- X7: a stock document is bound to at most one live (proposed or approved) plan
CREATE UNIQUE INDEX {{CompensationPlan.UqLiveDocument}} ON {{CompensationPlan}} ({{CompensationPlan.DocumentId}}) WHERE {{CompensationPlan.Status}} IN (1, 2);
REVOKE ALL ON TABLE {{CompensationPlan}} FROM PUBLIC;

-- The plan's closure: every (item, warehouse) position the replay may touch (the roots plus every position reached through transfers).
CREATE TABLE {{CompensationPlanPosition}} (
    {{CompensationPlanPosition.PlanId}} uuid NOT NULL,
    {{CompensationPlanPosition.ItemId}} uuid NOT NULL,
    {{CompensationPlanPosition.WarehouseId}} uuid NOT NULL,
    CONSTRAINT {{CompensationPlanPosition.PkPair}} PRIMARY KEY ({{CompensationPlanPosition.PlanId}}, {{CompensationPlanPosition.ItemId}}, {{CompensationPlanPosition.WarehouseId}}),
    CONSTRAINT {{CompensationPlanPosition.FkPlan}} FOREIGN KEY ({{CompensationPlanPosition.PlanId}}) REFERENCES {{CompensationPlan}} ({{CompensationPlan.Id}}),
    CONSTRAINT {{CompensationPlanPosition.FkItem}} FOREIGN KEY ({{CompensationPlanPosition.ItemId}}) REFERENCES [[Item.Table]] ([[Item.Id]]),
    CONSTRAINT {{CompensationPlanPosition.FkWarehouse}} FOREIGN KEY ({{CompensationPlanPosition.WarehouseId}}) REFERENCES [[Warehouse.Table]] ([[Warehouse.Id]])
);
CREATE INDEX {{CompensationPlanPosition.IxPair}} ON {{CompensationPlanPosition}} ({{CompensationPlanPosition.ItemId}}, {{CompensationPlanPosition.WarehouseId}});
REVOKE ALL ON TABLE {{CompensationPlanPosition}} FROM PUBLIC;

-- The compensation lines the approver sees (per affected posting, period, branch and account).
CREATE TABLE {{CompensationPlanLine}} (
    {{CompensationPlanLine.PlanId}} uuid NOT NULL,
    {{CompensationPlanLine.LineNo}} integer NOT NULL,
    {{CompensationPlanLine.PostingId}} uuid NULL,
    {{CompensationPlanLine.EntryDate}} date NOT NULL,
    {{CompensationPlanLine.PeriodId}} uuid NOT NULL,
    {{CompensationPlanLine.BranchId}} uuid NOT NULL,
    {{CompensationPlanLine.AccountId}} uuid NOT NULL,
    {{CompensationPlanLine.Debit}} numeric(28,6) NOT NULL,
    {{CompensationPlanLine.Credit}} numeric(28,6) NOT NULL,
    CONSTRAINT {{CompensationPlanLine.PkLine}} PRIMARY KEY ({{CompensationPlanLine.PlanId}}, {{CompensationPlanLine.LineNo}}),
    CONSTRAINT {{CompensationPlanLine.FkPlan}} FOREIGN KEY ({{CompensationPlanLine.PlanId}}) REFERENCES {{CompensationPlan}} ({{CompensationPlan.Id}}),
    CONSTRAINT {{CompensationPlanLine.FkPosting}} FOREIGN KEY ({{CompensationPlanLine.PostingId}}) REFERENCES [[StockPosting.Table]] ([[StockPosting.Id]]),
    CONSTRAINT {{CompensationPlanLine.FkPeriod}} FOREIGN KEY ({{CompensationPlanLine.PeriodId}}) REFERENCES [[FiscalPeriod.Table]] ([[FiscalPeriod.Id]]),
    CONSTRAINT {{CompensationPlanLine.FkBranch}} FOREIGN KEY ({{CompensationPlanLine.BranchId}}) REFERENCES [[Branch.Table]] ([[Branch.Id]]),
    CONSTRAINT {{CompensationPlanLine.FkAccount}} FOREIGN KEY ({{CompensationPlanLine.AccountId}}) REFERENCES [[Account.Table]] ([[Account.Id]]),
    CONSTRAINT {{CompensationPlanLine.CkValues}} CHECK ({{CompensationPlanLine.LineNo}} > 0 AND {{CompensationPlanLine.Debit}} >= 0 AND {{CompensationPlanLine.Credit}} >= 0
        AND ({{CompensationPlanLine.Debit}} > 0) <> ({{CompensationPlanLine.Credit}} > 0))
);
REVOKE ALL ON TABLE {{CompensationPlanLine}} FROM PUBLIC;

-- The plan's history (append-only).
CREATE TABLE {{CompensationPlanEvent}} (
    {{CompensationPlanEvent.Id}} uuid NOT NULL,
    {{CompensationPlanEvent.PlanId}} uuid NOT NULL,
    {{CompensationPlanEvent.Kind}} smallint NOT NULL,
    {{CompensationPlanEvent.ActorUserId}} uuid NOT NULL,
    {{CompensationPlanEvent.AtUtc}} timestamptz NOT NULL,
    {{CompensationPlanEvent.Note}} varchar(500) NULL,
    CONSTRAINT {{CompensationPlanEvent.PkId}} PRIMARY KEY ({{CompensationPlanEvent.Id}}),
    CONSTRAINT {{CompensationPlanEvent.FkPlan}} FOREIGN KEY ({{CompensationPlanEvent.PlanId}}) REFERENCES {{CompensationPlan}} ({{CompensationPlan.Id}}),
    CONSTRAINT {{CompensationPlanEvent.CkValues}} CHECK ({{CompensationPlanEvent.Kind}} IN (1, 2, 3, 4, 5, 6))
);
CREATE INDEX {{CompensationPlanEvent.IxPlan}} ON {{CompensationPlanEvent}} ({{CompensationPlanEvent.PlanId}});
REVOKE ALL ON TABLE {{CompensationPlanEvent}} FROM PUBLIC;

-- The approval: bound to the plan revision and to every fingerprint of the plan (the closure hash included).
CREATE TABLE {{CompensationPlanApproval}} (
    {{CompensationPlanApproval.Id}} uuid NOT NULL,
    {{CompensationPlanApproval.PlanId}} uuid NOT NULL,
    {{CompensationPlanApproval.PlanRevision}} bigint NOT NULL,
${fpColumns("CompensationPlanApproval")}
    {{CompensationPlanApproval.ClosureHash}} char(64) NOT NULL,
    {{CompensationPlanApproval.Approver}} uuid NOT NULL,
    {{CompensationPlanApproval.ApprovedAtUtc}} timestamptz NOT NULL,
    {{CompensationPlanApproval.Note}} varchar(500) NULL,
    CONSTRAINT {{CompensationPlanApproval.PkId}} PRIMARY KEY ({{CompensationPlanApproval.Id}}),
    CONSTRAINT {{CompensationPlanApproval.FkPlan}} FOREIGN KEY ({{CompensationPlanApproval.PlanId}}) REFERENCES {{CompensationPlan}} ({{CompensationPlan.Id}}),
    CONSTRAINT {{CompensationPlanApproval.UqPlanRevision}} UNIQUE ({{CompensationPlanApproval.PlanId}}, {{CompensationPlanApproval.PlanRevision}})
);
REVOKE ALL ON TABLE {{CompensationPlanApproval}} FROM PUBLIC;

-- The single execution of a plan. The posting of the event and the audit event are named when the row is written (the row is append-only); both must exist at commit.
CREATE TABLE {{CompensationPlanExecution}} (
    {{CompensationPlanExecution.Id}} uuid NOT NULL,
    {{CompensationPlanExecution.PlanId}} uuid NOT NULL,
    {{CompensationPlanExecution.PostingId}} uuid NOT NULL,
    {{CompensationPlanExecution.AuditEventId}} uuid NOT NULL,
    {{CompensationPlanExecution.ExecutedBy}} uuid NOT NULL,
    {{CompensationPlanExecution.ExecutedAtUtc}} timestamptz NOT NULL,
    {{CompensationPlanExecution.CreatedXact}} xid8 NOT NULL,
    CONSTRAINT {{CompensationPlanExecution.PkId}} PRIMARY KEY ({{CompensationPlanExecution.Id}}),
    CONSTRAINT {{CompensationPlanExecution.FkPlan}} FOREIGN KEY ({{CompensationPlanExecution.PlanId}}) REFERENCES {{CompensationPlan}} ({{CompensationPlan.Id}}),
    CONSTRAINT {{CompensationPlanExecution.UqPlan}} UNIQUE ({{CompensationPlanExecution.PlanId}}),
    CONSTRAINT {{CompensationPlanExecution.UqAuditEvent}} UNIQUE ({{CompensationPlanExecution.AuditEventId}}),
    CONSTRAINT {{CompensationPlanExecution.UqPosting}} UNIQUE ({{CompensationPlanExecution.PostingId}}),
    CONSTRAINT {{CompensationPlanExecution.FkPosting}} FOREIGN KEY ({{CompensationPlanExecution.PostingId}}) REFERENCES [[StockPosting.Table]] ([[StockPosting.Id]]) DEFERRABLE INITIALLY DEFERRED
);
REVOKE ALL ON TABLE {{CompensationPlanExecution}} FROM PUBLIC;

-- The compensation journals of an execution, one per bucket (the journal source key is plan + bucket, so a repeat cannot create a second set).
CREATE TABLE {{CompensationPlanExecutionJournal}} (
    {{CompensationPlanExecutionJournal.ExecutionId}} uuid NOT NULL,
    {{CompensationPlanExecutionJournal.BucketNo}} integer NOT NULL,
    {{CompensationPlanExecutionJournal.JournalEntryId}} uuid NOT NULL,
    CONSTRAINT {{CompensationPlanExecutionJournal.PkBucket}} PRIMARY KEY ({{CompensationPlanExecutionJournal.ExecutionId}}, {{CompensationPlanExecutionJournal.BucketNo}}),
    CONSTRAINT {{CompensationPlanExecutionJournal.FkExecution}} FOREIGN KEY ({{CompensationPlanExecutionJournal.ExecutionId}}) REFERENCES {{CompensationPlanExecution}} ({{CompensationPlanExecution.Id}}),
    CONSTRAINT {{CompensationPlanExecutionJournal.FkJournal}} FOREIGN KEY ({{CompensationPlanExecutionJournal.JournalEntryId}}) REFERENCES [[JournalEntry.Table]] ([[JournalEntry.Id]]),
    CONSTRAINT {{CompensationPlanExecutionJournal.UqJournal}} UNIQUE ({{CompensationPlanExecutionJournal.JournalEntryId}}),
    CONSTRAINT {{CompensationPlanExecutionJournal.CkValues}} CHECK ({{CompensationPlanExecutionJournal.BucketNo}} > 0)
);
REVOKE ALL ON TABLE {{CompensationPlanExecutionJournal}} FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- epoch membership and lineage
-- An epoch operation keeps its NATURAL kind (the kind CHECK of 0014 is not widened); its meaning comes from this side table (the pattern of 0020, DB-1).
CREATE TABLE {{EpochMember}} (
    {{EpochMember.OperationId}} uuid NOT NULL,
    {{EpochMember.ExecutionId}} uuid NOT NULL,
    {{EpochMember.Role}} smallint NOT NULL,
    {{EpochMember.OriginalOperationId}} uuid NULL,
    CONSTRAINT {{EpochMember.PkOperation}} PRIMARY KEY ({{EpochMember.OperationId}}),
    CONSTRAINT {{EpochMember.FkOperation}} FOREIGN KEY ({{EpochMember.OperationId}}) REFERENCES [[CostOperation.Table]] ([[CostOperation.Id]]),
    CONSTRAINT {{EpochMember.FkExecution}} FOREIGN KEY ({{EpochMember.ExecutionId}}) REFERENCES {{CompensationPlanExecution}} ({{CompensationPlanExecution.Id}}),
    CONSTRAINT {{EpochMember.FkOriginal}} FOREIGN KEY ({{EpochMember.OriginalOperationId}}) REFERENCES [[CostOperation.Table]] ([[CostOperation.Id]]),
    CONSTRAINT {{EpochMember.CkValues}} CHECK ({{EpochMember.Role}} IN (1, 2, 3) AND ({{EpochMember.Role}} = 2) = ({{EpochMember.OriginalOperationId}} IS NULL)
        AND {{EpochMember.OriginalOperationId}} IS DISTINCT FROM {{EpochMember.OperationId}})
);
-- an original operation is stornoed once and rebooked once, ever; an execution has one event
CREATE UNIQUE INDEX {{EpochMember.UqOriginalRole}} ON {{EpochMember}} ({{EpochMember.OriginalOperationId}}, {{EpochMember.Role}}) WHERE {{EpochMember.OriginalOperationId}} IS NOT NULL;
CREATE UNIQUE INDEX {{EpochMember.UqEvent}} ON {{EpochMember}} ({{EpochMember.ExecutionId}}) WHERE {{EpochMember.Role}} = 2;
CREATE INDEX {{EpochMember.IxExecution}} ON {{EpochMember}} ({{EpochMember.ExecutionId}});
REVOKE ALL ON TABLE {{EpochMember}} FROM PUBLIC;

CREATE TABLE {{EpochEntryLineage}} (
    {{EpochEntryLineage.OriginalEntryId}} bigint NOT NULL,
    {{EpochEntryLineage.StornoEntryId}} bigint NOT NULL,
    {{EpochEntryLineage.RebookEntryId}} bigint NOT NULL,
    {{EpochEntryLineage.ExecutionId}} uuid NOT NULL,
    CONSTRAINT {{EpochEntryLineage.PkOriginal}} PRIMARY KEY ({{EpochEntryLineage.OriginalEntryId}}),
    CONSTRAINT {{EpochEntryLineage.UqStorno}} UNIQUE ({{EpochEntryLineage.StornoEntryId}}),
    CONSTRAINT {{EpochEntryLineage.UqRebook}} UNIQUE ({{EpochEntryLineage.RebookEntryId}}),
    CONSTRAINT {{EpochEntryLineage.FkOriginal}} FOREIGN KEY ({{EpochEntryLineage.OriginalEntryId}}) REFERENCES [[CostEntry.Table]] ([[CostEntry.Id]]),
    CONSTRAINT {{EpochEntryLineage.FkStorno}} FOREIGN KEY ({{EpochEntryLineage.StornoEntryId}}) REFERENCES [[CostEntry.Table]] ([[CostEntry.Id]]),
    CONSTRAINT {{EpochEntryLineage.FkRebook}} FOREIGN KEY ({{EpochEntryLineage.RebookEntryId}}) REFERENCES [[CostEntry.Table]] ([[CostEntry.Id]]),
    CONSTRAINT {{EpochEntryLineage.FkExecution}} FOREIGN KEY ({{EpochEntryLineage.ExecutionId}}) REFERENCES {{CompensationPlanExecution}} ({{CompensationPlanExecution.Id}}),
    CONSTRAINT {{EpochEntryLineage.CkValues}} CHECK ({{EpochEntryLineage.OriginalEntryId}} <> {{EpochEntryLineage.StornoEntryId}} AND {{EpochEntryLineage.OriginalEntryId}} <> {{EpochEntryLineage.RebookEntryId}}
        AND {{EpochEntryLineage.StornoEntryId}} <> {{EpochEntryLineage.RebookEntryId}})
);
CREATE INDEX {{EpochEntryLineage.IxExecution}} ON {{EpochEntryLineage}} ({{EpochEntryLineage.ExecutionId}});
REVOKE ALL ON TABLE {{EpochEntryLineage}} FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- shared functions (all read table rows only; none reads a setting)
-- The closure hash, byte-exact (review L1): each pair is the lowercase hyphenated text of the item uuid, a colon and the lowercase hyphenated text of the warehouse uuid; the pairs are sorted by (item, warehouse)
-- in uuid order (the ORDER BY is on the uuid columns, never on text, so no collation is involved), joined by ONE newline without a trailing newline, encoded in UTF-8; the result is the lowercase hex SHA-256.
-- An empty closure hashes the empty string. PostgreSQL is the only place that computes it.
CREATE FUNCTION {{EpochGuard.FnClosureHash}}(p_plan uuid) RETURNS char(64) LANGUAGE sql AS $$
    SELECT encode(sha256(convert_to(COALESCE(string_agg(lower(c.{{CompensationPlanPosition.ItemId}}::text) || ':' || lower(c.{{CompensationPlanPosition.WarehouseId}}::text), E'\\n'
        ORDER BY c.{{CompensationPlanPosition.ItemId}}, c.{{CompensationPlanPosition.WarehouseId}}), ''), 'UTF8')), 'hex')::char(64)
    FROM {{CompensationPlanPosition}} c WHERE c.{{CompensationPlanPosition.PlanId}} = p_plan
$$;

-- X3: true when the user is independent of the plan: not its creator, and not the creator, an editor or the submitter of the event's stock document (every editor counts, which contains the last editor).
CREATE FUNCTION {{EpochGuard.FnIndependent}}(p_plan uuid, p_user uuid) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE
    p record;
BEGIN
    SELECT {{CompensationPlan.CreatedBy}} AS created_by, {{CompensationPlan.DocumentId}} AS document INTO p FROM {{CompensationPlan}} WHERE {{CompensationPlan.Id}} = p_plan;
    IF NOT FOUND OR p_user IS NULL THEN RETURN false; END IF;
    IF p_user = p.created_by THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM {{CompensationPlanEvent}} e WHERE e.{{CompensationPlanEvent.PlanId}} = p_plan AND e.{{CompensationPlanEvent.Kind}} = 1 AND e.{{CompensationPlanEvent.ActorUserId}} = p_user) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM [[StockDocument.Table]] d WHERE d.[[StockDocument.Id]] = p.document AND (d.[[StockDocument.CreatedBy]] = p_user OR d.[[StockDocument.SubmittedBy]] = p_user)) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM [[StockDocumentEvent.Table]] e WHERE e.[[StockDocumentEvent.DocumentId]] = p.document AND e.[[StockDocumentEvent.Kind]] IN (1, 2, 3) AND e.[[StockDocumentEvent.ActorUserId]] = p_user) THEN RETURN false; END IF;
    RETURN true;
END;
$$;

-- NULL when the approval is a sound approval of the plan; otherwise the reason. Sound: it belongs to the plan, repeats every fingerprint of the plan, the closure rows still hash to the plan's closure hash, the
-- approver is independent (X3), and the event's stock document is approved at the planned revision (X7).
CREATE FUNCTION {{EpochGuard.FnApprovalSound}}(p_plan uuid, p_approval uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    p {{CompensationPlan}}%ROWTYPE;
    a {{CompensationPlanApproval}}%ROWTYPE;
BEGIN
    SELECT * INTO p FROM {{CompensationPlan}} WHERE {{CompensationPlan.Id}} = p_plan;
    IF NOT FOUND THEN RETURN 'plan_not_found'; END IF;
    SELECT * INTO a FROM {{CompensationPlanApproval}} WHERE {{CompensationPlanApproval.Id}} = p_approval AND {{CompensationPlanApproval.PlanId}} = p_plan;
    IF NOT FOUND THEN RETURN 'approval_not_found'; END IF;
    IF ${fpDiffers} OR a.{{CompensationPlanApproval.ClosureHash}} <> p.{{CompensationPlan.ClosureHash}} THEN RETURN 'approval_fingerprint_mismatch'; END IF;
    IF {{EpochGuard.FnClosureHash}}(p_plan) <> p.{{CompensationPlan.ClosureHash}} THEN RETURN 'closure_hash_mismatch'; END IF;
    IF NOT {{EpochGuard.FnIndependent}}(p_plan, a.{{CompensationPlanApproval.Approver}}) THEN RETURN 'approver_not_independent'; END IF;
    IF NOT EXISTS (SELECT 1 FROM [[StockDocument.Table]] d WHERE d.[[StockDocument.Id]] = p.{{CompensationPlan.DocumentId}} AND d.[[StockDocument.Status]] = 3 AND d.[[StockDocument.Revision]] = p.{{CompensationPlan.DocumentRevision}}) THEN
        RETURN 'document_not_approved';
    END IF;
    -- review L1: a document that already has a stock posting is never executed by a plan (a posting is unique per document, so such a plan could never execute); this is read at approval and again at execution
    IF EXISTS (SELECT 1 FROM [[StockPosting.Table]] sp WHERE sp.[[StockPosting.DocumentId]] = p.{{CompensationPlan.DocumentId}}) THEN RETURN 'document_already_posted'; END IF;
    RETURN NULL;
END;
$$;

-- The plan of an execution that is LIVE: written in the current top-level transaction (xid8, correct inside savepoints), of a plan that is approved, by an approver who is not its creator, with an approval whose
-- fingerprints equal the plan's. NULL otherwise. This is the same-transaction binding of every epoch row; it reads rows only.
CREATE FUNCTION {{EpochGuard.FnLivePlan}}(p_execution uuid) RETURNS uuid LANGUAGE sql AS $$
    SELECT p.{{CompensationPlan.Id}}
    FROM {{CompensationPlanExecution}} x
    JOIN {{CompensationPlan}} p ON p.{{CompensationPlan.Id}} = x.{{CompensationPlanExecution.PlanId}}
    JOIN {{CompensationPlanApproval}} a ON a.{{CompensationPlanApproval.Id}} = p.{{CompensationPlan.ApprovalId}} AND a.{{CompensationPlanApproval.PlanId}} = p.{{CompensationPlan.Id}}
    WHERE x.{{CompensationPlanExecution.Id}} = p_execution AND x.{{CompensationPlanExecution.CreatedXact}} = pg_current_xact_id() AND p.{{CompensationPlan.Status}} = 2
      AND a.{{CompensationPlanApproval.Approver}} = p.{{CompensationPlan.ApprovedBy}} AND a.{{CompensationPlanApproval.Approver}} <> p.{{CompensationPlan.CreatedBy}}
      AND NOT (${fpDiffers}) AND a.{{CompensationPlanApproval.ClosureHash}} = p.{{CompensationPlan.ClosureHash}}
$$;

-- Append-only guard shared by every new table except the plan.
CREATE FUNCTION {{EpochGuard.FnAppendOnly}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'compensation plan and epoch records are append-only';
END;
$$;

-- ---------------------------------------------------------------------------------------------------------------- the plan: immutability, status transitions, closure hash
CREATE FUNCTION {{CompensationPlan.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_reason text;
BEGIN
    IF TG_LEVEL = 'STATEMENT' OR TG_OP = 'DELETE' THEN RAISE EXCEPTION 'compensation plans are never removed'; END IF;
    IF TG_OP = 'INSERT' THEN
        -- never client-supplied: the creating transaction and the closure hash (of the closure written so far: none yet)
        NEW.{{CompensationPlan.CreatedXact}} := pg_current_xact_id();
        NEW.{{CompensationPlan.ClosureHash}} := {{EpochGuard.FnClosureHash}}(NEW.{{CompensationPlan.Id}});
        IF EXISTS (SELECT 1 FROM [[StockPosting.Table]] sp WHERE sp.[[StockPosting.DocumentId]] = NEW.{{CompensationPlan.DocumentId}}) THEN RAISE EXCEPTION 'document_already_posted: a plan is not written for a document that already has a stock posting'; END IF;
        IF NEW.{{CompensationPlan.Status}} <> 1 OR NEW.{{CompensationPlan.Revision}} <> 1 OR NEW.{{CompensationPlan.ApprovalId}} IS NOT NULL OR NEW.{{CompensationPlan.ApprovedBy}} IS NOT NULL
           OR NEW.{{CompensationPlan.ApprovedAtUtc}} IS NOT NULL OR NEW.{{CompensationPlan.ExecutedXact}} IS NOT NULL THEN
            RAISE EXCEPTION 'a compensation plan starts as proposed, at revision 1';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.{{CompensationPlan.Id}} IS DISTINCT FROM OLD.{{CompensationPlan.Id}} OR NEW.{{CompensationPlan.ItemId}} IS DISTINCT FROM OLD.{{CompensationPlan.ItemId}}
       OR NEW.{{CompensationPlan.WarehouseId}} IS DISTINCT FROM OLD.{{CompensationPlan.WarehouseId}} OR NEW.{{CompensationPlan.DocumentId}} IS DISTINCT FROM OLD.{{CompensationPlan.DocumentId}}
       OR NEW.{{CompensationPlan.DocumentRevision}} IS DISTINCT FROM OLD.{{CompensationPlan.DocumentRevision}} OR NEW.{{CompensationPlan.Payload}} IS DISTINCT FROM OLD.{{CompensationPlan.Payload}}
       OR ${fpChanged}
       OR NEW.{{CompensationPlan.CreatedBy}} IS DISTINCT FROM OLD.{{CompensationPlan.CreatedBy}} OR NEW.{{CompensationPlan.CreatedAtUtc}} IS DISTINCT FROM OLD.{{CompensationPlan.CreatedAtUtc}}
       OR NEW.{{CompensationPlan.CreatedXact}} IS DISTINCT FROM OLD.{{CompensationPlan.CreatedXact}} THEN
        RAISE EXCEPTION 'the payload, the fingerprints and the origin of a compensation plan are immutable';
    END IF;
    -- the closure hash follows the closure rows while they can still be written (a proposed plan in its creating transaction); afterwards it is frozen
    IF OLD.{{CompensationPlan.Status}} = 1 AND OLD.{{CompensationPlan.CreatedXact}} = pg_current_xact_id() THEN
        NEW.{{CompensationPlan.ClosureHash}} := {{EpochGuard.FnClosureHash}}(OLD.{{CompensationPlan.Id}});
    ELSIF NEW.{{CompensationPlan.ClosureHash}} IS DISTINCT FROM OLD.{{CompensationPlan.ClosureHash}} THEN
        RAISE EXCEPTION 'the closure hash of a compensation plan is immutable';
    END IF;
    IF NEW.{{CompensationPlan.Status}} = OLD.{{CompensationPlan.Status}} THEN
        IF NEW.{{CompensationPlan.Revision}} IS DISTINCT FROM OLD.{{CompensationPlan.Revision}} OR NEW.{{CompensationPlan.ApprovalId}} IS DISTINCT FROM OLD.{{CompensationPlan.ApprovalId}}
           OR NEW.{{CompensationPlan.ApprovedBy}} IS DISTINCT FROM OLD.{{CompensationPlan.ApprovedBy}} OR NEW.{{CompensationPlan.ApprovedAtUtc}} IS DISTINCT FROM OLD.{{CompensationPlan.ApprovedAtUtc}}
           OR NEW.{{CompensationPlan.ExecutedXact}} IS DISTINCT FROM OLD.{{CompensationPlan.ExecutedXact}} OR NEW.{{CompensationPlan.UpdatedAtUtc}} IS DISTINCT FROM OLD.{{CompensationPlan.UpdatedAtUtc}} THEN
            RAISE EXCEPTION 'a compensation plan changes only by a status transition';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.{{CompensationPlan.Revision}} <> OLD.{{CompensationPlan.Revision}} + 1 THEN RAISE EXCEPTION 'a status transition advances the plan revision by exactly one'; END IF;
    IF NOT ((OLD.{{CompensationPlan.Status}} = 1 AND NEW.{{CompensationPlan.Status}} IN (2, 4, 5, 6)) OR (OLD.{{CompensationPlan.Status}} = 2 AND NEW.{{CompensationPlan.Status}} IN (3, 5, 6))) THEN
        RAISE EXCEPTION 'this status transition of a compensation plan is not allowed';
    END IF;
    IF NEW.{{CompensationPlan.Status}} = 2 THEN
        IF NEW.{{CompensationPlan.ApprovalId}} IS NULL OR NEW.{{CompensationPlan.ApprovedBy}} IS NULL OR NEW.{{CompensationPlan.ApprovedAtUtc}} IS NULL THEN RAISE EXCEPTION 'an approved plan names its approval'; END IF;
        IF NOT EXISTS (SELECT 1 FROM {{CompensationPlanApproval}} a WHERE a.{{CompensationPlanApproval.Id}} = NEW.{{CompensationPlan.ApprovalId}} AND a.{{CompensationPlanApproval.PlanId}} = OLD.{{CompensationPlan.Id}}
                       AND a.{{CompensationPlanApproval.PlanRevision}} = OLD.{{CompensationPlan.Revision}} AND a.{{CompensationPlanApproval.Approver}} = NEW.{{CompensationPlan.ApprovedBy}}) THEN
            RAISE EXCEPTION 'a plan is approved by the approval of its current revision';
        END IF;
        -- the approval re-checks the closure (hash recomputed from the closure rows), the fingerprints, the segregation of duties and the event document
        v_reason := {{EpochGuard.FnApprovalSound}}(OLD.{{CompensationPlan.Id}}, NEW.{{CompensationPlan.ApprovalId}});
        IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    ELSIF NEW.{{CompensationPlan.ApprovalId}} IS DISTINCT FROM OLD.{{CompensationPlan.ApprovalId}} OR NEW.{{CompensationPlan.ApprovedBy}} IS DISTINCT FROM OLD.{{CompensationPlan.ApprovedBy}}
          OR NEW.{{CompensationPlan.ApprovedAtUtc}} IS DISTINCT FROM OLD.{{CompensationPlan.ApprovedAtUtc}} THEN
        RAISE EXCEPTION 'the approval of a compensation plan is set once, by its approval';
    END IF;
    IF NEW.{{CompensationPlan.Status}} = 3 THEN
        IF NOT EXISTS (SELECT 1 FROM {{CompensationPlanExecution}} x WHERE x.{{CompensationPlanExecution.PlanId}} = OLD.{{CompensationPlan.Id}} AND x.{{CompensationPlanExecution.CreatedXact}} = pg_current_xact_id()) THEN
            RAISE EXCEPTION 'a plan is marked executed only in the transaction of its execution';
        END IF;
        IF {{EpochGuard.FnClosureHash}}(OLD.{{CompensationPlan.Id}}) <> OLD.{{CompensationPlan.ClosureHash}} THEN RAISE EXCEPTION 'the closure of the plan no longer matches its closure hash'; END IF;
        NEW.{{CompensationPlan.ExecutedXact}} := pg_current_xact_id();
    ELSIF NEW.{{CompensationPlan.ExecutedXact}} IS NOT NULL THEN
        RAISE EXCEPTION 'only an executed plan carries its executing transaction';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CompensationPlan.TrGuard}} BEFORE INSERT OR UPDATE OR DELETE ON {{CompensationPlan}} FOR EACH ROW EXECUTE FUNCTION {{CompensationPlan.FnGuard}}();
CREATE TRIGGER {{CompensationPlan.TrGuardStatement}} BEFORE TRUNCATE ON {{CompensationPlan}} FOR EACH STATEMENT EXECUTE FUNCTION {{CompensationPlan.FnGuard}}();

-- At commit (review L2): the plan and its closure were written together; the closure contains the plan's root position and hashes to the stored closure hash.
CREATE FUNCTION {{CompensationPlan.FnCommit}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
BEGIN
    SELECT {{CompensationPlan.ItemId}} AS item, {{CompensationPlan.WarehouseId}} AS warehouse, {{CompensationPlan.ClosureHash}} AS closure_hash INTO p FROM {{CompensationPlan}} WHERE {{CompensationPlan.Id}} = NEW.{{CompensationPlan.Id}};
    IF NOT EXISTS (SELECT 1 FROM {{CompensationPlanPosition}} c WHERE c.{{CompensationPlanPosition.PlanId}} = NEW.{{CompensationPlan.Id}} AND c.{{CompensationPlanPosition.ItemId}} = p.item
                   AND c.{{CompensationPlanPosition.WarehouseId}} = p.warehouse) THEN
        RAISE EXCEPTION 'the closure of a compensation plan contains its root position and is written with the plan';
    END IF;
    IF {{EpochGuard.FnClosureHash}}(NEW.{{CompensationPlan.Id}}) <> p.closure_hash THEN RAISE EXCEPTION 'the closure of the plan does not match its closure hash'; END IF;
    -- review L1: re-checked at commit, so a posting committed after the insert-time check (read committed) is seen; under REPEATABLE READ the approval and the execution re-read it again
    IF EXISTS (SELECT 1 FROM [[StockPosting.Table]] sp WHERE sp.[[StockPosting.DocumentId]] = NEW.{{CompensationPlan.DocumentId}}) THEN RAISE EXCEPTION 'document_already_posted: a plan is not written for a document that already has a stock posting'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{CompensationPlan.TrCommit}} AFTER INSERT ON {{CompensationPlan}} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {{CompensationPlan.FnCommit}}();

-- Closure rows and lines: written only while the plan is proposed AND in the transaction that created the plan (review M3 and L2). A new closure therefore needs a new plan and a new approval.
CREATE FUNCTION {{EpochGuard.FnPlanChild}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
BEGIN
    IF TG_TABLE_NAME = '{{CompensationPlanPosition}}' THEN v_plan := NEW.{{CompensationPlanPosition.PlanId}}; ELSE v_plan := NEW.{{CompensationPlanLine.PlanId}}; END IF;
    IF NOT EXISTS (SELECT 1 FROM {{CompensationPlan}} p WHERE p.{{CompensationPlan.Id}} = v_plan AND p.{{CompensationPlan.Status}} = 1 AND p.{{CompensationPlan.CreatedXact}} = pg_current_xact_id()) THEN
        RAISE EXCEPTION 'the closure and the lines of a compensation plan are written with the plan, in its creating transaction';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CompensationPlanPosition.TrInsert}} BEFORE INSERT ON {{CompensationPlanPosition}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnPlanChild}}();
CREATE TRIGGER {{CompensationPlanLine.TrInsert}} BEFORE INSERT ON {{CompensationPlanLine}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnPlanChild}}();
-- every closure row refreshes the plan's hash through the plan's own trigger (which computes it; the value written here is ignored)
CREATE FUNCTION {{CompensationPlanPosition.FnRefresh}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    UPDATE {{CompensationPlan}} SET {{CompensationPlan.ClosureHash}} = {{CompensationPlan.ClosureHash}} WHERE {{CompensationPlan.Id}} = NEW.{{CompensationPlanPosition.PlanId}};
    RETURN NULL;
END;
$$;
CREATE TRIGGER {{CompensationPlanPosition.TrRefresh}} AFTER INSERT ON {{CompensationPlanPosition}} FOR EACH ROW EXECUTE FUNCTION {{CompensationPlanPosition.FnRefresh}}();
CREATE TRIGGER {{CompensationPlanPosition.TrGuard}} BEFORE UPDATE OR DELETE ON {{CompensationPlanPosition}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{CompensationPlanPosition.TrGuardStatement}} BEFORE TRUNCATE ON {{CompensationPlanPosition}} FOR EACH STATEMENT EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{CompensationPlanLine.TrGuard}} BEFORE UPDATE OR DELETE ON {{CompensationPlanLine}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{CompensationPlanLine.TrGuardStatement}} BEFORE TRUNCATE ON {{CompensationPlanLine}} FOR EACH STATEMENT EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{CompensationPlanEvent.TrGuard}} BEFORE UPDATE OR DELETE ON {{CompensationPlanEvent}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{CompensationPlanEvent.TrGuardStatement}} BEFORE TRUNCATE ON {{CompensationPlanEvent}} FOR EACH STATEMENT EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();

-- ---------------------------------------------------------------------------------------------------------------- the approval
CREATE FUNCTION {{CompensationPlanApproval.FnInsert}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_reason text;
BEGIN
    -- the plan row is locked first: approvals, executions and status changes of one plan are serialized for every writer
    SELECT {{CompensationPlan.Status}} AS status, {{CompensationPlan.Revision}} AS revision INTO p FROM {{CompensationPlan}} WHERE {{CompensationPlan.Id}} = NEW.{{CompensationPlanApproval.PlanId}} FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'the plan of an approval does not exist'; END IF;
    IF p.status <> 1 OR p.revision <> NEW.{{CompensationPlanApproval.PlanRevision}} THEN RAISE EXCEPTION 'an approval is given to the current revision of a proposed plan'; END IF;
    v_reason := {{EpochGuard.FnApprovalSound}}(NEW.{{CompensationPlanApproval.PlanId}}, NEW.{{CompensationPlanApproval.Id}});
    IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER {{CompensationPlanApproval.TrInsert}} AFTER INSERT ON {{CompensationPlanApproval}} FOR EACH ROW EXECUTE FUNCTION {{CompensationPlanApproval.FnInsert}}();
-- at commit: the approval is the one the plan was approved with (an approval never exists without the status change of its plan)
CREATE FUNCTION {{CompensationPlanApproval.FnCommit}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM {{CompensationPlan}} p WHERE p.{{CompensationPlan.Id}} = NEW.{{CompensationPlanApproval.PlanId}} AND p.{{CompensationPlan.ApprovalId}} = NEW.{{CompensationPlanApproval.Id}}
                   AND p.{{CompensationPlan.ApprovedBy}} = NEW.{{CompensationPlanApproval.Approver}}) THEN
        RAISE EXCEPTION 'an approval is committed together with the approval of its plan';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{CompensationPlanApproval.TrCommit}} AFTER INSERT ON {{CompensationPlanApproval}} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {{CompensationPlanApproval.FnCommit}}();
CREATE TRIGGER {{CompensationPlanApproval.TrGuard}} BEFORE UPDATE OR DELETE ON {{CompensationPlanApproval}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{CompensationPlanApproval.TrGuardStatement}} BEFORE TRUNCATE ON {{CompensationPlanApproval}} FOR EACH STATEMENT EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();

-- ---------------------------------------------------------------------------------------------------------------- the execution
CREATE FUNCTION {{CompensationPlanExecution.FnInsert}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_reason text;
BEGIN
    -- never client-supplied: the top-level transaction id (64 bits, no wraparound, the same inside a savepoint)
    NEW.{{CompensationPlanExecution.CreatedXact}} := pg_current_xact_id();
    SELECT {{CompensationPlan.Status}} AS status, {{CompensationPlan.ApprovalId}} AS approval, {{CompensationPlan.CreatedBy}} AS created_by INTO p
      FROM {{CompensationPlan}} WHERE {{CompensationPlan.Id}} = NEW.{{CompensationPlanExecution.PlanId}} FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'the plan of an execution does not exist'; END IF;
    IF p.status <> 2 THEN RAISE EXCEPTION 'only an approved compensation plan is executed, once'; END IF;
    v_reason := {{EpochGuard.FnApprovalSound}}(NEW.{{CompensationPlanExecution.PlanId}}, p.approval);
    IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    IF NEW.{{CompensationPlanExecution.ExecutedBy}} = p.created_by THEN RAISE EXCEPTION 'a compensation plan is never executed by its creator'; END IF;
    -- X5: no position of this plan's closure lies in the closure of another executed plan
    IF EXISTS (SELECT 1 FROM {{CompensationPlanPosition}} c
               JOIN {{CompensationPlanPosition}} o ON o.{{CompensationPlanPosition.ItemId}} = c.{{CompensationPlanPosition.ItemId}} AND o.{{CompensationPlanPosition.WarehouseId}} = c.{{CompensationPlanPosition.WarehouseId}}
                    AND o.{{CompensationPlanPosition.PlanId}} <> c.{{CompensationPlanPosition.PlanId}}
               JOIN {{CompensationPlanExecution}} x ON x.{{CompensationPlanExecution.PlanId}} = o.{{CompensationPlanPosition.PlanId}}
               WHERE c.{{CompensationPlanPosition.PlanId}} = NEW.{{CompensationPlanExecution.PlanId}}) THEN
        RAISE EXCEPTION 'plan_on_epoch_position: a position of the closure was already rewritten by an executed plan';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CompensationPlanExecution.TrInsert}} BEFORE INSERT ON {{CompensationPlanExecution}} FOR EACH ROW EXECUTE FUNCTION {{CompensationPlanExecution.FnInsert}}();
CREATE TRIGGER {{CompensationPlanExecution.TrGuard}} BEFORE UPDATE OR DELETE ON {{CompensationPlanExecution}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{CompensationPlanExecution.TrGuardStatement}} BEFORE TRUNCATE ON {{CompensationPlanExecution}} FOR EACH STATEMENT EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();

CREATE FUNCTION {{CompensationPlanExecutionJournal.FnInsert}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF {{EpochGuard.FnLivePlan}}(NEW.{{CompensationPlanExecutionJournal.ExecutionId}}) IS NULL THEN RAISE EXCEPTION 'a compensation journal is recorded inside the execution of its approved plan'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CompensationPlanExecutionJournal.TrInsert}} BEFORE INSERT ON {{CompensationPlanExecutionJournal}} FOR EACH ROW EXECUTE FUNCTION {{CompensationPlanExecutionJournal.FnInsert}}();
CREATE TRIGGER {{CompensationPlanExecutionJournal.TrGuard}} BEFORE UPDATE OR DELETE ON {{CompensationPlanExecutionJournal}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{CompensationPlanExecutionJournal.TrGuardStatement}} BEFORE TRUNCATE ON {{CompensationPlanExecutionJournal}} FOR EACH STATEMENT EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();

-- ---------------------------------------------------------------------------------------------------------------- the epoch member (design 3.2)
CREATE FUNCTION {{EpochMember.FnInsert}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
    o record;
    v_original_kind smallint;
BEGIN
    v_plan := {{EpochGuard.FnLivePlan}}(NEW.{{EpochMember.ExecutionId}});
    IF v_plan IS NULL THEN RAISE EXCEPTION 'an epoch member belongs to the execution, written in this transaction, of an approved compensation plan'; END IF;
    SELECT [[CostOperation.Kind]] AS kind, [[CostOperation.ReversesOperationId]] AS reverses INTO o FROM [[CostOperation.Table]] WHERE [[CostOperation.Id]] = NEW.{{EpochMember.OperationId}};
    IF NOT FOUND THEN RAISE EXCEPTION 'the operation of an epoch member does not exist'; END IF;
    -- epoch operations are never reversal operations (review L1)
    IF o.kind = 4 OR o.reverses IS NOT NULL THEN RAISE EXCEPTION 'a reversal operation is never an epoch member'; END IF;
    -- the operation is being built now: it has no entry yet and no obligation or settlement refers to it (an empty operation of an earlier transaction exists only for those, 0020)
    IF EXISTS (SELECT 1 FROM [[CostEntry.Table]] WHERE [[CostEntry.OperationId]] = NEW.{{EpochMember.OperationId}}) THEN RAISE EXCEPTION 'an epoch member is an operation without entries yet'; END IF;
    IF EXISTS (SELECT 1 FROM [[NegativeObligation.Table]] WHERE [[NegativeObligation.IssueOperationId]] = NEW.{{EpochMember.OperationId}})
       OR EXISTS (SELECT 1 FROM [[NegativeSettlement.Table]] WHERE [[NegativeSettlement.ReceiptOperationId]] = NEW.{{EpochMember.OperationId}}) THEN
        RAISE EXCEPTION 'an operation of a negative-stock obligation or settlement is never an epoch member';
    END IF;
    IF NEW.{{EpochMember.Role}} IN (1, 3) THEN
        -- X5: no epoch on an epoch
        IF EXISTS (SELECT 1 FROM {{EpochMember}} m WHERE m.{{EpochMember.OperationId}} = NEW.{{EpochMember.OriginalOperationId}}) THEN RAISE EXCEPTION 'plan_on_epoch_position: an epoch operation is never rewritten by another epoch'; END IF;
        SELECT [[CostOperation.Kind]] INTO v_original_kind FROM [[CostOperation.Table]] WHERE [[CostOperation.Id]] = NEW.{{EpochMember.OriginalOperationId}};
        IF v_original_kind IS DISTINCT FROM o.kind THEN RAISE EXCEPTION 'a storno or rebook operation keeps the kind of its original operation'; END IF;
        -- the entries of the original that the epoch takes back lie in the plan's closure: an original without any entry there is not rewritten by this plan
        IF NOT EXISTS (SELECT 1 FROM [[CostEntry.Table]] e
                       JOIN [[CostPosition.Table]] ps ON ps.[[CostPosition.Id]] = e.[[CostEntry.PositionId]]
                       JOIN {{CompensationPlanPosition}} c ON c.{{CompensationPlanPosition.PlanId}} = v_plan AND c.{{CompensationPlanPosition.ItemId}} = ps.[[CostPosition.ItemId]]
                            AND c.{{CompensationPlanPosition.WarehouseId}} = ps.[[CostPosition.WarehouseId]]
                       WHERE e.[[CostEntry.OperationId]] = NEW.{{EpochMember.OriginalOperationId}}) THEN
            RAISE EXCEPTION 'the original operation of an epoch member has no entry in the closure of the plan';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{EpochMember.TrInsert}} BEFORE INSERT ON {{EpochMember}} FOR EACH ROW EXECUTE FUNCTION {{EpochMember.FnInsert}}();
CREATE TRIGGER {{EpochMember.TrGuard}} BEFORE UPDATE OR DELETE ON {{EpochMember}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{EpochMember.TrGuardStatement}} BEFORE TRUNCATE ON {{EpochMember}} FOR EACH STATEMENT EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();

-- Lineage: original entry -> storno entry -> rebook entry, all of one execution of this transaction.
CREATE FUNCTION {{EpochEntryLineage.FnInsert}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    oe record;
    se record;
    re record;
BEGIN
    IF {{EpochGuard.FnLivePlan}}(NEW.{{EpochEntryLineage.ExecutionId}}) IS NULL THEN RAISE EXCEPTION 'an epoch lineage row belongs to the execution, written in this transaction, of an approved compensation plan'; END IF;
    SELECT [[CostEntry.OperationId]] AS op, [[CostEntry.PositionId]] AS pos, [[CostEntry.Kind]] AS kind, [[CostEntry.Direction]] AS dir, [[CostEntry.EffectiveDate]] AS d, [[CostEntry.Quantity]] AS qty
      INTO oe FROM [[CostEntry.Table]] WHERE [[CostEntry.Id]] = NEW.{{EpochEntryLineage.OriginalEntryId}};
    SELECT [[CostEntry.OperationId]] AS op, [[CostEntry.ReversesEntryId]] AS reverses INTO se FROM [[CostEntry.Table]] WHERE [[CostEntry.Id]] = NEW.{{EpochEntryLineage.StornoEntryId}};
    SELECT [[CostEntry.OperationId]] AS op, [[CostEntry.PositionId]] AS pos, [[CostEntry.Kind]] AS kind, [[CostEntry.Direction]] AS dir, [[CostEntry.EffectiveDate]] AS d, [[CostEntry.Quantity]] AS qty,
           [[CostEntry.ReversesEntryId]] AS reverses INTO re FROM [[CostEntry.Table]] WHERE [[CostEntry.Id]] = NEW.{{EpochEntryLineage.RebookEntryId}};
    IF oe IS NULL OR se IS NULL OR re IS NULL THEN RAISE EXCEPTION 'the entries of an epoch lineage row exist'; END IF;
    IF se.reverses IS DISTINCT FROM NEW.{{EpochEntryLineage.OriginalEntryId}} THEN RAISE EXCEPTION 'the storno entry of a lineage row takes back its original entry'; END IF;
    IF NOT EXISTS (SELECT 1 FROM {{EpochMember}} m WHERE m.{{EpochMember.OperationId}} = se.op AND m.{{EpochMember.ExecutionId}} = NEW.{{EpochEntryLineage.ExecutionId}} AND m.{{EpochMember.Role}} = 1
                   AND m.{{EpochMember.OriginalOperationId}} = oe.op)
       OR NOT EXISTS (SELECT 1 FROM {{EpochMember}} m WHERE m.{{EpochMember.OperationId}} = re.op AND m.{{EpochMember.ExecutionId}} = NEW.{{EpochEntryLineage.ExecutionId}} AND m.{{EpochMember.Role}} = 3
                      AND m.{{EpochMember.OriginalOperationId}} = oe.op) THEN
        RAISE EXCEPTION 'the storno and rebook entries of a lineage row belong to the storno and rebook members of the same execution and original operation';
    END IF;
    -- X2: the rebooked entry repeats its original on the same position, with the original date, kind, direction and quantity (only the amount is replayed)
    IF re.pos <> oe.pos OR re.kind <> oe.kind OR re.dir <> oe.dir OR re.d <> oe.d OR re.qty <> oe.qty OR re.reverses IS NOT NULL THEN
        RAISE EXCEPTION 'a rebook entry repeats its original entry on the same position and date';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{EpochEntryLineage.TrInsert}} BEFORE INSERT ON {{EpochEntryLineage}} FOR EACH ROW EXECUTE FUNCTION {{EpochEntryLineage.FnInsert}}();
CREATE TRIGGER {{EpochEntryLineage.TrGuard}} BEFORE UPDATE OR DELETE ON {{EpochEntryLineage}} FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();
CREATE TRIGGER {{EpochEntryLineage.TrGuardStatement}} BEFORE TRUNCATE ON {{EpochEntryLineage}} FOR EACH STATEMENT EXECUTE FUNCTION {{EpochGuard.FnAppendOnly}}();

-- ---------------------------------------------------------------------------------------------------------------- the entry-level guard (design 3.3(1), (2), (2a), (3)); a NEW function and a NEW trigger
-- The position row is read FOR UPDATE: the costing engine already holds that lock when it appends an entry, so no new lock order arises. An ordinary entry takes no other lock here.
-- The position still holds the date of its previous entry at this point (the engine appends the entry first and moves the position afterwards).
CREATE FUNCTION {{EpochGuard.FnEntry}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ps record;
    m record;
    r record;
    v_member boolean;
    v_plan uuid;
    v_kind smallint;
    v_revop uuid;
    v_revent uuid;
    v_prev date;
    v_op boolean;
    v_period smallint;
BEGIN
    SELECT [[CostPosition.ItemId]] AS item, [[CostPosition.WarehouseId]] AS warehouse, [[CostPosition.LastEffectiveDate]] AS last_date, [[CostPosition.LastSeq]] AS last_seq INTO ps
      FROM [[CostPosition.Table]] WHERE [[CostPosition.Id]] = NEW.[[CostEntry.PositionId]] FOR UPDATE;
    IF NOT FOUND THEN RETURN NEW; END IF;   -- no such position: the foreign key of the entry answers, as before
    -- review M2 (a): an entry takes the next sequence number of its position (the engine's own invariant; the position is locked, so the number is stable). Together with the deferred check of the entry
    -- (below) this ties the position's last date, which the F1 comparison relies on, to the entries themselves instead of to a value the writer alone maintains.
    IF NEW.[[CostEntry.Seq]] <> ps.last_seq + 1 THEN RAISE EXCEPTION 'seq_not_next: a cost entry takes the next sequence number of its position'; END IF;
    SELECT {{EpochMember.Role}} AS role, {{EpochMember.ExecutionId}} AS execution, {{EpochMember.OriginalOperationId}} AS original INTO m FROM {{EpochMember}} WHERE {{EpochMember.OperationId}} = NEW.[[CostEntry.OperationId]];
    v_member := FOUND;
    IF v_member THEN
        -- every entry of an epoch member, back-dated or not, is written inside the execution of its approved plan, in this transaction
        v_plan := {{EpochGuard.FnLivePlan}}(m.execution);
        IF v_plan IS NULL THEN RAISE EXCEPTION 'an entry of an epoch operation is written only inside the execution, in this transaction, of its approved compensation plan'; END IF;
        -- (2a) closure binding: the position of the entry (the position row decides, not the copied columns) is in the plan's approved closure
        IF NEW.[[CostEntry.ItemId]] <> ps.item OR NEW.[[CostEntry.WarehouseId]] <> ps.warehouse THEN RAISE EXCEPTION 'an entry of an epoch operation names the item and warehouse of its position'; END IF;
        IF NOT EXISTS (SELECT 1 FROM {{CompensationPlanPosition}} c WHERE c.{{CompensationPlanPosition.PlanId}} = v_plan AND c.{{CompensationPlanPosition.ItemId}} = ps.item
                       AND c.{{CompensationPlanPosition.WarehouseId}} = ps.warehouse) THEN
            RAISE EXCEPTION 'an entry of an epoch operation lies on a position of the approved closure of its plan';
        END IF;
        -- (3) X2: an Open fiscal period, read FOR SHARE so that a concurrent close cannot commit between this check and the commit (lock order: positions, then periods)
        SELECT [[FiscalPeriod.Status]] INTO v_period FROM [[FiscalPeriod.Table]]
          WHERE [[FiscalPeriod.StartDate]] <= NEW.[[CostEntry.EffectiveDate]] AND [[FiscalPeriod.EndDate]] >= NEW.[[CostEntry.EffectiveDate]] FOR SHARE;
        IF NOT FOUND OR v_period <> 1 THEN RAISE EXCEPTION 'period_not_open: an entry of an epoch operation is dated in an Open fiscal period'; END IF;
        -- (3) no open negative-stock obligation on the position
        IF [[NegativeStockGuard.HasOpenFunction]](ps.item, ps.warehouse) THEN RAISE EXCEPTION 'an epoch never touches a position with an open negative-stock obligation'; END IF;
        -- (3) X5: the position is not in the closure of another executed plan
        IF EXISTS (SELECT 1 FROM {{CompensationPlanPosition}} c JOIN {{CompensationPlanExecution}} x ON x.{{CompensationPlanExecution.PlanId}} = c.{{CompensationPlanPosition.PlanId}}
                   WHERE c.{{CompensationPlanPosition.ItemId}} = ps.item AND c.{{CompensationPlanPosition.WarehouseId}} = ps.warehouse AND c.{{CompensationPlanPosition.PlanId}} <> v_plan) THEN
            RAISE EXCEPTION 'plan_on_epoch_position: the position was already rewritten by another executed plan';
        END IF;
    END IF;
    -- (1) F1: a back-dated entry is accepted only for an epoch member (whose execution, plan and approval were verified above)
    -- review M2 (residual): the position's date is also a value the writer maintains, so an ordinary entry is compared with the date of the position's PREVIOUS ENTRY as well (seq = last_seq, a one-row lookup on the
    -- unique (position, sequence) index); a position moved without the entry's date can no longer hide a later date
    IF NOT v_member THEN
        SELECT [[CostEntry.EffectiveDate]] INTO v_prev FROM [[CostEntry.Table]] WHERE [[CostEntry.PositionId]] = NEW.[[CostEntry.PositionId]] AND [[CostEntry.Seq]] = ps.last_seq;
        IF FOUND AND (ps.last_date IS NULL OR v_prev > ps.last_date) THEN ps.last_date := v_prev; END IF;
    END IF;
    IF ps.last_date IS NOT NULL AND NEW.[[CostEntry.EffectiveDate]] < ps.last_date AND NOT v_member THEN
        RAISE EXCEPTION 'a back-dated cost entry is accepted only inside the execution of an approved compensation plan';
    END IF;
    -- (2) an entry-level reversal link outside a reversal operation belongs to an epoch storno
    IF NEW.[[CostEntry.ReversesEntryId]] IS NOT NULL THEN
        SELECT [[CostOperation.Kind]], [[CostOperation.ReversesOperationId]] INTO v_kind, v_revop FROM [[CostOperation.Table]] WHERE [[CostOperation.Id]] = NEW.[[CostEntry.OperationId]];
        v_op := FOUND;
        IF v_op AND v_kind = 4 THEN
            -- review M1 (X6 at entry level): a reversal entry takes back an entry of the operation that its reversal operation names (the engine always writes it so), and never an entry of an epoch member
            -- (the epoch's own storno path is the branch below, outside a reversal operation)
            SELECT [[CostEntry.OperationId]] INTO v_revent FROM [[CostEntry.Table]] WHERE [[CostEntry.Id]] = NEW.[[CostEntry.ReversesEntryId]];
            IF FOUND THEN
                IF v_revent IS DISTINCT FROM v_revop THEN RAISE EXCEPTION 'a reversal entry takes back an entry of the operation that its reversal operation names'; END IF;
                IF EXISTS (SELECT 1 FROM {{EpochMember}} em WHERE em.{{EpochMember.OperationId}} = v_revent) THEN
                    RAISE EXCEPTION 'operation_rewritten_by_epoch: an entry of an epoch operation is never taken back by a reversal';
                END IF;
            END IF;
        END IF;
        IF v_op AND v_kind <> 4 THEN
            IF NOT v_member OR m.role <> 1 THEN RAISE EXCEPTION 'outside a reversal operation only an epoch storno takes back an entry'; END IF;
            SELECT [[CostEntry.OperationId]] AS op, [[CostEntry.PositionId]] AS pos, [[CostEntry.Kind]] AS kind, [[CostEntry.Direction]] AS dir, [[CostEntry.EffectiveDate]] AS d, [[CostEntry.Quantity]] AS qty,
                   [[CostEntry.Amount]] AS amount INTO r FROM [[CostEntry.Table]] WHERE [[CostEntry.Id]] = NEW.[[CostEntry.ReversesEntryId]];
            IF FOUND THEN
                IF r.pos <> NEW.[[CostEntry.PositionId]] THEN RAISE EXCEPTION 'a storno entry takes back an entry of its own position'; END IF;
                IF r.op IS DISTINCT FROM m.original THEN RAISE EXCEPTION 'a storno entry takes back an entry of the original operation of its member'; END IF;
                IF r.kind <> NEW.[[CostEntry.Kind]] OR r.dir <> -NEW.[[CostEntry.Direction]] OR r.d <> NEW.[[CostEntry.EffectiveDate]] OR r.qty <> NEW.[[CostEntry.Quantity]] OR r.amount <> NEW.[[CostEntry.Amount]] THEN
                    RAISE EXCEPTION 'a storno entry mirrors its original entry exactly, on the original date';
                END IF;
            END IF;
        END IF;
    ELSIF v_member AND m.role = 1 THEN
        RAISE EXCEPTION 'a storno entry names the entry it takes back';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{EpochGuard.TrEntry}} BEFORE INSERT ON [[CostEntry.Table]] FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnEntry}}();

-- review M2 (b): at commit, the position of every entry written in the transaction has been moved at least to that entry (sequence number and last date). The engine moves the position with every entry and the executor
-- keeps the date with GREATEST, so neither path changes; a writer that appends an entry without moving its position, or moves it without the entry's date, can no longer leave the F1 anchor behind.
CREATE FUNCTION {{EpochGuard.FnEntryCommit}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM [[CostPosition.Table]] ps WHERE ps.[[CostPosition.Id]] = NEW.[[CostEntry.PositionId]] AND ps.[[CostPosition.LastSeq]] >= NEW.[[CostEntry.Seq]]
                   AND ps.[[CostPosition.LastEffectiveDate]] >= NEW.[[CostEntry.EffectiveDate]]) THEN
        RAISE EXCEPTION 'position_not_moved: a cost entry is committed together with the move of its position to at least its sequence number and its date';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{EpochGuard.TrEntryCommit}} AFTER INSERT ON [[CostEntry.Table]] DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnEntryCommit}}();

-- ---------------------------------------------------------------------------------------------------------------- X6 (design 3.3(4)): no reversal operation of an epoch member or of an operation with stornoed entries
CREATE FUNCTION {{EpochGuard.FnOperation}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.[[CostOperation.ReversesOperationId]] IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM {{EpochMember}} m WHERE m.{{EpochMember.OperationId}} = NEW.[[CostOperation.ReversesOperationId]])
           OR EXISTS (SELECT 1 FROM {{EpochEntryLineage}} l JOIN [[CostEntry.Table]] e ON e.[[CostEntry.Id]] = l.{{EpochEntryLineage.OriginalEntryId}}
                      WHERE e.[[CostEntry.OperationId]] = NEW.[[CostOperation.ReversesOperationId]]) THEN
            RAISE EXCEPTION 'operation_rewritten_by_epoch: an operation rewritten by a replay epoch, or belonging to one, is never reversed';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{EpochGuard.TrOperation}} BEFORE INSERT ON [[CostOperation.Table]] FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnOperation}}();

-- ---------------------------------------------------------------------------------------------------------------- X7: a plan-bound stock document is posted only inside the execution of its plan
CREATE FUNCTION {{EpochGuard.FnPosting}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
BEGIN
    SELECT {{CompensationPlan.Id}} INTO v_plan FROM {{CompensationPlan}} WHERE {{CompensationPlan.DocumentId}} = NEW.[[StockPosting.DocumentId]] AND {{CompensationPlan.Status}} IN (1, 2);
    IF FOUND AND NOT EXISTS (SELECT 1 FROM {{CompensationPlanExecution}} x WHERE x.{{CompensationPlanExecution.PlanId}} = v_plan AND x.{{CompensationPlanExecution.CreatedXact}} = pg_current_xact_id()
                             AND x.{{CompensationPlanExecution.PostingId}} = NEW.[[StockPosting.Id]]) THEN
        RAISE EXCEPTION 'document_bound_to_plan: a stock document bound to a compensation plan is posted only by the execution of that plan';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{EpochGuard.TrPosting}} BEFORE INSERT ON [[StockPosting.Table]] FOR EACH ROW EXECUTE FUNCTION {{EpochGuard.FnPosting}}();

-- ---------------------------------------------------------------------------------------------------------------- deferred checks, judged at commit (design 3.4)
CREATE FUNCTION {{CompensationPlanExecution.FnCommit}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_members bigint;
    v_events bigint;
BEGIN
    SELECT {{CompensationPlan.Status}} AS status, {{CompensationPlan.ExecutedXact}} AS executed_xact, {{CompensationPlan.ClosureHash}} AS closure_hash, {{CompensationPlan.DocumentId}} AS document INTO p
      FROM {{CompensationPlan}} WHERE {{CompensationPlan.Id}} = NEW.{{CompensationPlanExecution.PlanId}};
    -- the plan is executed, and was marked so in the transaction that wrote the execution (this one)
    IF p.status IS DISTINCT FROM 3 OR p.executed_xact IS DISTINCT FROM NEW.{{CompensationPlanExecution.CreatedXact}} OR NEW.{{CompensationPlanExecution.CreatedXact}} <> pg_current_xact_id() THEN
        RAISE EXCEPTION 'an execution is committed together with the executed status of its plan';
    END IF;
    IF {{EpochGuard.FnClosureHash}}(NEW.{{CompensationPlanExecution.PlanId}}) <> p.closure_hash THEN RAISE EXCEPTION 'the closure of the plan does not match its closure hash'; END IF;
    SELECT count(*), count(*) FILTER (WHERE {{EpochMember.Role}} = 2) INTO v_members, v_events FROM {{EpochMember}} WHERE {{EpochMember.ExecutionId}} = NEW.{{CompensationPlanExecution.Id}};
    IF v_members = 0 OR v_events <> 1 THEN RAISE EXCEPTION 'an execution has members and exactly one event'; END IF;
    -- the event member is the cost operation of the execution's stock posting, and that posting is the posting of the plan's document
    IF NOT EXISTS (SELECT 1 FROM {{EpochMember}} m JOIN [[StockPosting.Table]] sp ON sp.[[StockPosting.CostOperationId]] = m.{{EpochMember.OperationId}}
                   WHERE m.{{EpochMember.ExecutionId}} = NEW.{{CompensationPlanExecution.Id}} AND m.{{EpochMember.Role}} = 2 AND sp.[[StockPosting.Id]] = NEW.{{CompensationPlanExecution.PostingId}}
                     AND sp.[[StockPosting.DocumentId]] = p.document) THEN
        RAISE EXCEPTION 'the event of an execution is the posting of the document of its plan';
    END IF;
    -- every storno member has its rebook member for the same original operation, and the reverse (each is unique per original by the index)
    IF EXISTS (SELECT 1 FROM {{EpochMember}} s WHERE s.{{EpochMember.ExecutionId}} = NEW.{{CompensationPlanExecution.Id}} AND s.{{EpochMember.Role}} IN (1, 3)
               AND NOT EXISTS (SELECT 1 FROM {{EpochMember}} t WHERE t.{{EpochMember.ExecutionId}} = s.{{EpochMember.ExecutionId}} AND t.{{EpochMember.Role}} = 4 - s.{{EpochMember.Role}}
                               AND t.{{EpochMember.OriginalOperationId}} = s.{{EpochMember.OriginalOperationId}})) THEN
        RAISE EXCEPTION 'every storno member has its rebook member for the same original operation';
    END IF;
    IF EXISTS (SELECT 1 FROM {{EpochMember}} m WHERE m.{{EpochMember.ExecutionId}} = NEW.{{CompensationPlanExecution.Id}}
               AND NOT EXISTS (SELECT 1 FROM [[CostEntry.Table]] e WHERE e.[[CostEntry.OperationId]] = m.{{EpochMember.OperationId}})) THEN
        RAISE EXCEPTION 'every epoch member has at least one entry';
    END IF;
    -- every storno entry and every rebook entry has its lineage row
    IF EXISTS (SELECT 1 FROM {{EpochMember}} m JOIN [[CostEntry.Table]] e ON e.[[CostEntry.OperationId]] = m.{{EpochMember.OperationId}}
               WHERE m.{{EpochMember.ExecutionId}} = NEW.{{CompensationPlanExecution.Id}}
                 AND ((m.{{EpochMember.Role}} = 1 AND NOT EXISTS (SELECT 1 FROM {{EpochEntryLineage}} l WHERE l.{{EpochEntryLineage.StornoEntryId}} = e.[[CostEntry.Id]] AND l.{{EpochEntryLineage.ExecutionId}} = m.{{EpochMember.ExecutionId}}))
                   OR (m.{{EpochMember.Role}} = 3 AND NOT EXISTS (SELECT 1 FROM {{EpochEntryLineage}} l WHERE l.{{EpochEntryLineage.RebookEntryId}} = e.[[CostEntry.Id]] AND l.{{EpochEntryLineage.ExecutionId}} = m.{{EpochMember.ExecutionId}})))) THEN
        RAISE EXCEPTION 'every storno and rebook entry has its lineage row';
    END IF;
    -- the business audit event of the execution: of the plan-executed type, with this plan as subject and this execution as target (0002 is not altered)
    IF NOT EXISTS (SELECT 1 FROM [[BusinessAudit.Table]] a WHERE a.[[BusinessAudit.EventId]] = NEW.{{CompensationPlanExecution.AuditEventId}} AND a.[[BusinessAudit.EventType]] = 1154
                   AND a.[[BusinessAudit.SubjectId]] = NEW.{{CompensationPlanExecution.PlanId}} AND a.[[BusinessAudit.TargetUserId]] = NEW.{{CompensationPlanExecution.Id}}) THEN
        RAISE EXCEPTION 'an execution is committed together with its business audit event';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{CompensationPlanExecution.TrCommit}} AFTER INSERT ON {{CompensationPlanExecution}} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {{CompensationPlanExecution.FnCommit}}();

-- orphan detection: no member without an execution of this transaction, and no member without entries
CREATE FUNCTION {{EpochMember.FnCommit}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM {{CompensationPlanExecution}} x WHERE x.{{CompensationPlanExecution.Id}} = NEW.{{EpochMember.ExecutionId}} AND x.{{CompensationPlanExecution.CreatedXact}} = pg_current_xact_id()) THEN
        RAISE EXCEPTION 'an epoch member is committed together with its execution';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM [[CostEntry.Table]] e WHERE e.[[CostEntry.OperationId]] = NEW.{{EpochMember.OperationId}}) THEN RAISE EXCEPTION 'every epoch member has at least one entry'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{EpochMember.TrCommit}} AFTER INSERT ON {{EpochMember}} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {{EpochMember.FnCommit}}();
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update(`hasm.company.${MIGRATION}.plan-execution.v1:` + token).digest("hex").slice(0, 8);

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

// review L2: inside every function body each table of the schema is named public.<table>, so a same-named temporary table can never stand in for it (a search_path clause is not an option: the migration guard refuses it)
const qualify = (t) => t.replace(/\$\$[\s\S]*?\$\$/g, (b) => b.replace(/(?<!')\{\{([A-Za-z0-9]+)\}\}/g, "public.{{$1}}").replace(/\[\[([A-Za-z0-9]+\.Table)\]\]/g, "public.[[$1]]"));

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
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-" + MIGRATION + "-plan-execution.cjs for company migration " + MIGRATION + " (Phase 3.8D step 4, plan storage and execution, database layer; provisional number). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// EpochGuard names the shared functions and the three triggers added to existing tables (cost entry, cost operation, stock posting); it has no table.\npublic static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> CompensationPlanMap { get; } = new Dictionary<string, string>\n    {\n";
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
  if (!ok) { console.error("generated plan-execution schema differs from the committed files"); process.exit(1); }
  console.log("plan-execution schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
