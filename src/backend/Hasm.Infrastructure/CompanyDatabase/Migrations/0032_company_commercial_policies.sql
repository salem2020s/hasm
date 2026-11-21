-- Migration 0032: approval policies for commercial documents (Phase 3.13 slice S5a). Forward-only and ADDITIVE: new functions and two new triggers only; no table is created or altered, no existing function or trigger
-- (the journal's) is replaced or dropped, nothing is seeded and no existing row is read or modified. Opaque physical names (D-029) generated deterministically by tools/schema/company-0032-commercial-policies.cjs
-- (logical dictionary: CompanySchema.CommercialPolicy.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.13.md S5a; decisions D-140 and D-141): the ordered approval-policy engine of 3.2C (ApprovalPolicy, ApprovalPolicyStage, DocumentApprovalBinding, ApprovalDecision) was wired to the journal in SQL.
-- These functions give a SUBMITTED COMMERCIAL DOCUMENT the same rules: the governing policy is picked by document type, branch and the BASE value of the SEALED total (gross times the sealed exchange rate) at submit (D-140);
-- the plan is the stages of that policy, raised to the plan the revision was submitted under (an append-only floor, so a policy can only get stricter for a pending document); every decision is cast for the first open stage by
-- someone independent of the creator, the last editor and the submitter; and the document cannot become approved before the plan of its revision is satisfied. A document with NO matching policy has an empty plan and keeps
-- the single independent approval of S1b (D-141).
-- Invariants enforced by PostgreSQL:
--   * decisions on a commercial document are cast on its CURRENT revision while it is submitted (a stale decision never counts), approve only (a rejection is the status change), in stage order, one per approver per revision;
--   * the approver of a decision is never the creator, the last editor or the submitter;
--   * the submitted -> approved transition needs the plan of the submitted revision satisfied and the approver of the document must be one of the approvers of that revision;
--   * the policy conditions that only make sense for a journal (journal type, manual rate override, soft-closed period, closing kind) never match a commercial document: a policy that carries one does not apply to it.
-- The journal's own functions and triggers are NOT touched: the decision trigger below returns at once for any row that is not a decision on a commercial document.

CREATE FUNCTION f_19221e35(p_doc uuid) RETURNS TABLE (policy_id uuid, policy_revision bigint) LANGUAGE sql STABLE AS $$
    WITH d AS (
        SELECT cd.c_cceb8d4c AS id, cd.c_56e38e95 AS doc_type, cd.c_aae9b28d AS branch,
               COALESCE(cd.c_f510980d * cd.c_e05c15dc, 0) AS base_total
        FROM t_04d17dc0 cd WHERE cd.c_cceb8d4c = p_doc),
    cand AS (
        SELECT p.c_a2b53e06 AS pid, p.c_c9bff476 AS prev, p.c_bc3b07c1 AS created, COALESCE(st.c_002a7676, p.c_a874380a) AS required
        FROM t_8f2462c5 p CROSS JOIN d
        LEFT JOIN t_e8cadae9 st ON st.c_85629458 = p.c_a2b53e06 AND st.c_41aad198 = p.c_c9bff476
        WHERE p.c_61d707fd AND p.c_9c04c465 = d.doc_type AND p.c_b130782f IN (1, 3)
          AND (p.c_5d70e226 IS NULL OR p.c_5d70e226 = d.branch) AND d.base_total >= p.c_a0b367ec
          AND p.c_1a2f7f5e IS NULL AND p.c_6088d474 IS NULL AND p.c_ca9500fa IS NULL AND p.c_43137a95 IS NULL)
    SELECT pid, prev FROM cand GROUP BY pid, prev, created ORDER BY sum(required) DESC, count(*) DESC, created ASC, pid ASC LIMIT 1
$$;

CREATE FUNCTION f_ac5920e3(p_doc uuid, p_revision bigint) RETURNS TABLE (stage_no integer, required integer, role_id uuid) LANGUAGE sql STABLE AS $$
    WITH cur AS (
        SELECT COALESCE(st.c_6e0d1427, 1)::integer AS stage_no, COALESCE(st.c_002a7676, p.c_a874380a)::integer AS required, st.c_22b0a7aa AS role_id
        FROM f_19221e35(p_doc) pk JOIN t_8f2462c5 p ON p.c_a2b53e06 = pk.policy_id
        LEFT JOIN t_e8cadae9 st ON st.c_85629458 = p.c_a2b53e06 AND st.c_41aad198 = p.c_c9bff476),
    bound AS (
        SELECT b.c_d293a70b::integer AS stage_no, b.c_95045342::integer AS required, b.c_2b451767 AS role_id
        FROM t_9e0faeaf b JOIN t_04d17dc0 cd ON cd.c_cceb8d4c = b.c_5e3e1a37 AND cd.c_56e38e95 = b.c_17ea6905
        WHERE b.c_5e3e1a37 = p_doc AND b.c_63b7acab = p_revision)
    SELECT COALESCE(c.stage_no, b.stage_no), GREATEST(COALESCE(c.required, 0), COALESCE(b.required, 0)), COALESCE(b.role_id, c.role_id)
    FROM cur c FULL OUTER JOIN bound b ON b.stage_no = c.stage_no
    ORDER BY 1
$$;

CREATE FUNCTION f_6b74e17c(p_doc uuid, p_revision bigint) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT NOT EXISTS (SELECT 1 FROM t_3fa074e5 d JOIN t_04d17dc0 cd ON cd.c_cceb8d4c = d.c_175a20c9 AND cd.c_56e38e95 = d.c_9adee5c9
                       WHERE d.c_175a20c9 = p_doc AND d.c_f399e237 = p_revision AND d.c_3a789977 = 2)
       AND NOT EXISTS (SELECT 1 FROM f_ac5920e3(p_doc, p_revision) pl
                       WHERE (SELECT count(DISTINCT d.c_942d312c) FROM t_3fa074e5 d JOIN t_04d17dc0 cd ON cd.c_cceb8d4c = d.c_175a20c9 AND cd.c_56e38e95 = d.c_9adee5c9
                              WHERE d.c_175a20c9 = p_doc AND d.c_f399e237 = p_revision AND d.c_3a789977 = 1 AND d.c_da356dc3 = pl.stage_no) < pl.required)
$$;

CREATE FUNCTION f_376bb81d() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    d record;
    v_open integer;
BEGIN
    -- the document row is locked first: every decision on one document is serialized here for every writer, so two approvers can never both see the same open stage
    SELECT cd.c_8cb62046 AS status, cd.c_d2cb57e3 AS revision, cd.c_6258e1f8 AS created_by, cd.c_abe96b9c AS submitted_by,
           cd.c_087f9d00 AS last_edited_by
      INTO d FROM t_04d17dc0 cd WHERE cd.c_cceb8d4c = NEW.c_175a20c9 AND cd.c_56e38e95 = NEW.c_9adee5c9 FOR NO KEY UPDATE;
    IF NOT FOUND THEN RETURN NEW; END IF;   -- not a decision on a commercial document: the journal and every other document keep their own rules, untouched
    IF NEW.c_3a789977 <> 1 THEN RAISE EXCEPTION 'a commercial document is rejected by its status change, not by a decision row'; END IF;
    IF d.status <> 2 OR d.revision <> NEW.c_f399e237 THEN RAISE EXCEPTION 'a decision is cast on the current revision of a submitted commercial document'; END IF;
    IF NEW.c_942d312c = d.created_by OR NEW.c_942d312c = d.submitted_by OR NEW.c_942d312c = d.last_edited_by THEN
        RAISE EXCEPTION 'the approver must be independent of the creator, the last editor and the submitter';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM f_ac5920e3(NEW.c_175a20c9, NEW.c_f399e237)) THEN RAISE EXCEPTION 'no approval stage applies to this commercial document'; END IF;
    SELECT min(pl.stage_no) INTO v_open FROM f_ac5920e3(NEW.c_175a20c9, NEW.c_f399e237) pl
    WHERE (SELECT count(DISTINCT x.c_942d312c) FROM t_3fa074e5 x
           WHERE x.c_9adee5c9 = NEW.c_9adee5c9 AND x.c_175a20c9 = NEW.c_175a20c9 AND x.c_f399e237 = NEW.c_f399e237
             AND x.c_3a789977 = 1 AND x.c_da356dc3 = pl.stage_no) < pl.required;
    IF v_open IS NULL THEN RAISE EXCEPTION 'every approval stage is already complete'; END IF;
    IF NEW.c_da356dc3 <> v_open THEN RAISE EXCEPTION 'a decision must be cast for the open approval stage'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_ee07a7cc BEFORE INSERT ON t_3fa074e5 FOR EACH ROW EXECUTE FUNCTION f_376bb81d();

CREATE FUNCTION f_084de2b6() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.c_8cb62046 = 2 AND NEW.c_8cb62046 = 3 AND EXISTS (SELECT 1 FROM f_ac5920e3(OLD.c_cceb8d4c, OLD.c_d2cb57e3)) THEN
        IF NOT f_6b74e17c(OLD.c_cceb8d4c, OLD.c_d2cb57e3) THEN RAISE EXCEPTION 'the required approvals for this revision are missing'; END IF;
        IF NOT EXISTS (SELECT 1 FROM t_3fa074e5 d WHERE d.c_9adee5c9 = OLD.c_56e38e95 AND d.c_175a20c9 = OLD.c_cceb8d4c
                       AND d.c_f399e237 = OLD.c_d2cb57e3 AND d.c_3a789977 = 1 AND d.c_942d312c = NEW.c_2f938a9f) THEN
            RAISE EXCEPTION 'the approver of a document must be one of the approvers of its revision';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_99c39659 BEFORE UPDATE ON t_04d17dc0 FOR EACH ROW EXECUTE FUNCTION f_084de2b6();
