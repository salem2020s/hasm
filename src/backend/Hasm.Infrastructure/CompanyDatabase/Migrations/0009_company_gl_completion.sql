-- Migration 0009: General Ledger completion (Phase 3.2C), built slice by slice before the phase is merged. Forward-only, no DROP, nothing seeded, no data migrated.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.LedgerPolicies.cs.

-- ============================================================================================================================================
-- S1 - cost-center accounting policies
-- ============================================================================================================================================

-- Append-only history of the 3.2C policy entities (the 3.2B ledger history table restricts its entity codes and cannot be altered without a DROP).
-- entity: 1 account cost-center rule, 2 cost-center branch eligibility, 3 approval stage policy, 4 fiscal-year closing settings, 5 fiscal-year closing. entity_key is a natural key in text form.
CREATE TABLE t_a8b39266 (
    c_93186753 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_55e6d16b smallint NOT NULL,
    c_60cde077 varchar(80) NOT NULL,
    c_6416fcc4 bigint NOT NULL,
    c_09a62494 uuid NOT NULL,
    c_db94aeca timestamptz NOT NULL,
    c_36e7e7df jsonb NOT NULL,
    c_a4154dd1 varchar(48) NOT NULL,
    c_02354aed bigint NOT NULL,
    CONSTRAINT k_e634ceb2 PRIMARY KEY (c_93186753),
    CONSTRAINT k_16fd3b9e UNIQUE (c_55e6d16b, c_60cde077, c_6416fcc4),
    CONSTRAINT k_0972beda UNIQUE (c_a4154dd1, c_02354aed),
    CONSTRAINT k_c133f676 FOREIGN KEY (c_a4154dd1, c_02354aed) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_5d1e394e CHECK (c_55e6d16b BETWEEN 1 AND 20 AND c_6416fcc4 > 0 AND jsonb_typeof(c_36e7e7df) = 'object')
);
CREATE FUNCTION f_5eab45d2() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'policy history is append-only';
END;
$$;
CREATE TRIGGER r_e88a45e6 BEFORE UPDATE OR DELETE ON t_a8b39266 FOR EACH ROW EXECUTE FUNCTION f_5eab45d2();
CREATE TRIGGER r_e7b2e217 BEFORE TRUNCATE ON t_a8b39266 FOR EACH STATEMENT EXECUTE FUNCTION f_5eab45d2();
REVOKE ALL ON TABLE t_a8b39266 FROM PUBLIC;

-- A company may require a cost-center allocation on every line posted to a particular account (optional by default: no row = optional).
-- Only a postable (non-group) account can carry the rule. A row is never deleted: switching the rule off keeps the record (is_required = false).
CREATE TABLE t_6e4ce78a (
    c_7d1b8b04 uuid NOT NULL,
    c_5528e5aa boolean NOT NULL,
    c_7ee10b36 bigint NOT NULL,
    c_ccecab3c timestamptz NOT NULL,
    c_b0c95884 timestamptz NOT NULL,
    CONSTRAINT k_834015ed PRIMARY KEY (c_7d1b8b04),
    CONSTRAINT k_86da7d6a FOREIGN KEY (c_7d1b8b04) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_9ca3e5b6 CHECK (c_7ee10b36 >= 1)
);
CREATE FUNCTION f_15c15908() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cost-center rules cannot be deleted'; END IF;
    IF NEW.c_7d1b8b04 IS DISTINCT FROM OLD.c_7d1b8b04 THEN RAISE EXCEPTION 'the account of a cost-center rule is immutable'; END IF;
    IF NEW.c_ccecab3c IS DISTINCT FROM OLD.c_ccecab3c THEN RAISE EXCEPTION 'cost-center rule provenance is immutable'; END IF;
    IF NEW.c_7ee10b36 <> OLD.c_7ee10b36 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_c0f14fa3 BEFORE UPDATE OR DELETE ON t_6e4ce78a FOR EACH ROW EXECUTE FUNCTION f_15c15908();
CREATE TRIGGER r_3ffc9ccc BEFORE TRUNCATE ON t_6e4ce78a FOR EACH STATEMENT EXECUTE FUNCTION f_15c15908();
CREATE FUNCTION f_6442050a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_11d107a9 a WHERE a.c_29a86598 = NEW.c_7d1b8b04 AND a.c_57d34c8a) THEN
        RAISE EXCEPTION 'a cost-center rule applies to postable (non-group) accounts only';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_752a9fd8 BEFORE INSERT OR UPDATE ON t_6e4ce78a FOR EACH ROW EXECUTE FUNCTION f_6442050a();
REVOKE ALL ON TABLE t_6e4ce78a FROM PUBLIC;

-- Optional eligibility of a cost center for branches. Cost centers stay conceptually independent of branches: no restriction unless one is configured.
-- When is_restricted, an allocation to the cost center is accepted only for a line whose branch has an eligible row. Rows are never deleted (is_eligible toggles).
CREATE TABLE t_7a16c2a5 (
    c_5b013ba0 uuid NOT NULL,
    c_18cc6412 boolean NOT NULL,
    c_5d01f060 bigint NOT NULL,
    c_f574e60d timestamptz NOT NULL,
    c_7897de19 timestamptz NOT NULL,
    CONSTRAINT k_d85321d2 PRIMARY KEY (c_5b013ba0),
    CONSTRAINT k_1a073f8e FOREIGN KEY (c_5b013ba0) REFERENCES t_939ae118 (c_0770cd8f),
    CONSTRAINT k_737033a8 CHECK (c_5d01f060 >= 1)
);
CREATE FUNCTION f_f9af5ad2() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cost-center branch rules cannot be deleted'; END IF;
    IF NEW.c_5b013ba0 IS DISTINCT FROM OLD.c_5b013ba0 OR NEW.c_f574e60d IS DISTINCT FROM OLD.c_f574e60d THEN
        RAISE EXCEPTION 'identity and provenance of a branch rule are immutable';
    END IF;
    IF NEW.c_5d01f060 <> OLD.c_5d01f060 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_209cf8a5 BEFORE UPDATE OR DELETE ON t_7a16c2a5 FOR EACH ROW EXECUTE FUNCTION f_f9af5ad2();
CREATE TRIGGER r_07d64a25 BEFORE TRUNCATE ON t_7a16c2a5 FOR EACH STATEMENT EXECUTE FUNCTION f_f9af5ad2();
REVOKE ALL ON TABLE t_7a16c2a5 FROM PUBLIC;

CREATE TABLE t_fc54b254 (
    c_66b1947c uuid NOT NULL,
    c_47cd1114 uuid NOT NULL,
    c_e40d44ac boolean NOT NULL,
    c_bb15f8ae timestamptz NOT NULL,
    CONSTRAINT k_9740f03f PRIMARY KEY (c_66b1947c, c_47cd1114),
    CONSTRAINT k_d48cd228 FOREIGN KEY (c_66b1947c) REFERENCES t_939ae118 (c_0770cd8f),
    CONSTRAINT k_0f4c85d5 FOREIGN KEY (c_47cd1114) REFERENCES t_8ba6c27d (c_358158d2)
);
CREATE FUNCTION f_711189a4() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cost-center branch eligibility rows cannot be deleted'; END IF;
    IF NEW.c_66b1947c IS DISTINCT FROM OLD.c_66b1947c OR NEW.c_47cd1114 IS DISTINCT FROM OLD.c_47cd1114 THEN
        RAISE EXCEPTION 'identity of an eligibility row is immutable';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_6a4c8ed5 BEFORE UPDATE OR DELETE ON t_fc54b254 FOR EACH ROW EXECUTE FUNCTION f_711189a4();
CREATE TRIGGER r_074e2207 BEFORE TRUNCATE ON t_fc54b254 FOR EACH STATEMENT EXECUTE FUNCTION f_711189a4();
REVOKE ALL ON TABLE t_fc54b254 FROM PUBLIC;

-- Cost-center activation epoch (maintained ONLY by the database): bumped on every reactivation, so cost-center-scoped role assignments recorded for an
-- earlier activation stay inert for good (same safeguard as branches and warehouses).
ALTER TABLE t_939ae118 ADD COLUMN c_10eea50a bigint NOT NULL DEFAULT 1;
ALTER TABLE t_939ae118 ADD CONSTRAINT k_42ba0bc5 CHECK (c_10eea50a >= 1);
CREATE FUNCTION f_ff034cd6() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_10eea50a IS DISTINCT FROM OLD.c_10eea50a THEN RAISE EXCEPTION 'the activation epoch is maintained by the database'; END IF;
    IF NEW.c_8a8af184 AND NOT OLD.c_8a8af184 THEN NEW.c_10eea50a := OLD.c_10eea50a + 1; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_6023e975 BEFORE UPDATE ON t_939ae118 FOR EACH ROW EXECUTE FUNCTION f_ff034cd6();

-- Submission and posting enforce the policies in PostgreSQL (the service validates earlier for friendly messages; a direct write cannot bypass this).
-- Reversals are exempt (they mirror an earlier posting and must stay possible after a policy was introduced); system rounding lines are exempt.
CREATE FUNCTION f_03f44dbc() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_4e4c6448 IN (2, 3, 4) AND NEW.c_4e4c6448 IS DISTINCT FROM OLD.c_4e4c6448 AND NEW.c_e11acf50 IS NULL THEN
        IF EXISTS (
            SELECT 1 FROM t_783c8d98 l JOIN t_6e4ce78a r ON r.c_7d1b8b04 = l.c_e6bfb072 AND r.c_5528e5aa
            WHERE l.c_39596059 = NEW.c_14f317c6 AND NOT l.c_781e3043
              AND NOT EXISTS (SELECT 1 FROM t_58417560 a WHERE a.c_0146172b = l.c_5ffa4fb6)) THEN
            RAISE EXCEPTION 'a line on an account that requires a cost center has no allocation';
        END IF;
        IF EXISTS (
            SELECT 1 FROM t_783c8d98 l JOIN t_58417560 a ON a.c_0146172b = l.c_5ffa4fb6
              JOIN t_7a16c2a5 b ON b.c_5b013ba0 = a.c_71376e84 AND b.c_18cc6412
            WHERE l.c_39596059 = NEW.c_14f317c6
              AND NOT EXISTS (SELECT 1 FROM t_fc54b254 e WHERE e.c_66b1947c = a.c_71376e84
                              AND e.c_47cd1114 = l.c_147d3982 AND e.c_e40d44ac)) THEN
            RAISE EXCEPTION 'the cost center is not eligible for the branch of the line';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_2e73af6e BEFORE UPDATE ON t_f3158869 FOR EACH ROW EXECUTE FUNCTION f_03f44dbc();


-- ============================================================================================================================================
-- S2 - risk-based and ordered approval policies
-- ============================================================================================================================================
-- A policy may now carry RISK CONDITIONS (all optional; NULL = "do not care"): the journal type (1 normal, 2 reversal, 3 adjusting), whether the entry uses a MANUAL exchange-rate
-- override, and whether the entry date lies in a SOFT CLOSED period right now; together with the existing document type, source, branch and base-currency threshold. It may also be
-- STAGED: an ordered list of stages, each needing a number of distinct approvers (optionally holders of one role). A policy without stage rows is the CLASSIC policy of 3.2B (exactly one
-- stage needing N distinct approvers), so every existing configuration keeps its exact meaning: nothing is converted, nothing changes.
ALTER TABLE t_8f2462c5 ADD COLUMN c_1a2f7f5e smallint NULL;
ALTER TABLE t_8f2462c5 ADD COLUMN c_6088d474 boolean NULL;
ALTER TABLE t_8f2462c5 ADD COLUMN c_ca9500fa boolean NULL;
ALTER TABLE t_8f2462c5 ADD CONSTRAINT k_2f349944 CHECK (c_1a2f7f5e IS NULL OR c_1a2f7f5e IN (1, 2, 3));

-- The stages of one policy REVISION (append-only versions: editing a policy writes a new revision's stage set; earlier sets stay for history). No upper bound on the number of levels
-- other than the data type; the set must be contiguous 1..n (checked at commit).
CREATE TABLE t_e8cadae9 (
    c_85629458 uuid NOT NULL,
    c_41aad198 bigint NOT NULL,
    c_6e0d1427 smallint NOT NULL,
    c_002a7676 smallint NOT NULL,
    c_22b0a7aa uuid NULL,
    CONSTRAINT k_eb36fd64 PRIMARY KEY (c_85629458, c_41aad198, c_6e0d1427),
    CONSTRAINT k_85b10301 FOREIGN KEY (c_85629458) REFERENCES t_8f2462c5 (c_a2b53e06),
    CONSTRAINT k_a805f03c CHECK (c_41aad198 > 0 AND c_6e0d1427 >= 1 AND c_002a7676 >= 1)
);
CREATE FUNCTION f_bdb6446b() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'approval policy stages are append-only';
END;
$$;
CREATE TRIGGER r_73cc750f BEFORE UPDATE OR DELETE ON t_e8cadae9 FOR EACH ROW EXECUTE FUNCTION f_bdb6446b();
CREATE TRIGGER r_5071d47e BEFORE TRUNCATE ON t_e8cadae9 FOR EACH STATEMENT EXECUTE FUNCTION f_bdb6446b();
CREATE FUNCTION f_34b9c229() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT min(s.c_6e0d1427) <> 1 OR count(*) <> max(s.c_6e0d1427) FROM t_e8cadae9 s
        WHERE s.c_85629458 = NEW.c_85629458 AND s.c_41aad198 = NEW.c_41aad198) THEN
        RAISE EXCEPTION 'the stages of an approval policy must be numbered 1..n without gaps';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_ea4636a5 AFTER INSERT ON t_e8cadae9 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_34b9c229();
REVOKE ALL ON TABLE t_e8cadae9 FROM PUBLIC;

-- The plan a document was SUBMITTED under, per document revision (append-only snapshot). It is a FLOOR: later policy changes can only add requirements for a pending document,
-- never silently remove what it was submitted under. A new revision (edit, return to draft) is bound again at its next submission.
CREATE TABLE t_9e0faeaf (
    c_2ea61267 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_17ea6905 varchar(40) NOT NULL,
    c_5e3e1a37 uuid NOT NULL,
    c_63b7acab bigint NOT NULL,
    c_d293a70b smallint NOT NULL,
    c_95045342 smallint NOT NULL,
    c_2b451767 uuid NULL,
    c_4b12f026 uuid NULL,
    c_ec5ab3ff bigint NULL,
    c_35b46080 timestamptz NOT NULL,
    CONSTRAINT k_6b06f5c7 PRIMARY KEY (c_2ea61267),
    CONSTRAINT k_424b5a35 UNIQUE (c_17ea6905, c_5e3e1a37, c_63b7acab, c_d293a70b),
    CONSTRAINT k_e9ef623d CHECK (c_63b7acab > 0 AND c_d293a70b >= 1 AND c_95045342 >= 1)
);
CREATE FUNCTION f_07fd9f8a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'approval bindings are append-only';
END;
$$;
CREATE TRIGGER r_cd6baace BEFORE UPDATE OR DELETE ON t_9e0faeaf FOR EACH ROW EXECUTE FUNCTION f_07fd9f8a();
CREATE TRIGGER r_86e6ce91 BEFORE TRUNCATE ON t_9e0faeaf FOR EACH STATEMENT EXECUTE FUNCTION f_07fd9f8a();
REVOKE ALL ON TABLE t_9e0faeaf FROM PUBLIC;

-- Every decision is cast for ONE stage (legacy decisions: stage 1). One decision per approver per revision still holds, so distinctness spans the whole chain.
ALTER TABLE t_3fa074e5 ADD COLUMN c_da356dc3 smallint NOT NULL DEFAULT 1;
ALTER TABLE t_3fa074e5 ADD CONSTRAINT k_b274f0c2 CHECK (c_da356dc3 >= 1);

-- The classic evaluation of 3.2B (kept for the classic policies only: no risk condition, no stages): the strictest matching classic policy.
CREATE OR REPLACE FUNCTION f_660e4c73(p_doc varchar, p_source smallint, p_branch uuid, p_amount numeric) RETURNS integer LANGUAGE sql STABLE AS $$
    SELECT COALESCE(max(p.c_a874380a), 0)::integer FROM t_8f2462c5 p
    WHERE p.c_61d707fd AND p.c_9c04c465 = p_doc AND (p.c_b130782f = 3 OR p.c_b130782f = p_source)
      AND (p.c_5d70e226 IS NULL OR p.c_5d70e226 = p_branch) AND p_amount >= p.c_a0b367ec
      AND p.c_1a2f7f5e IS NULL AND p.c_6088d474 IS NULL AND p.c_ca9500fa IS NULL
      AND NOT EXISTS (SELECT 1 FROM t_e8cadae9 st WHERE st.c_85629458 = p.c_a2b53e06 AND st.c_41aad198 = p.c_c9bff476)
$$;

-- The policy that governs a journal entry: among the active policies whose conditions match the entry, the most demanding one (most approvals in total, then most stages, then the
-- oldest policy, then the lowest id). Deterministic: the same facts always select the same policy.
CREATE FUNCTION f_e565501a(p_entry uuid) RETURNS TABLE (policy_id uuid, policy_revision bigint) LANGUAGE sql STABLE AS $$
    WITH e AS (
        SELECT je.c_14f317c6 AS id, je.c_41c437ea AS source, je.c_59a672dc AS branch, je.c_225ac5cd AS base_total, je.c_960dbf25 AS jtype, je.c_4cb99522 AS d,
               EXISTS (SELECT 1 FROM t_783c8d98 l WHERE l.c_39596059 = je.c_14f317c6 AND l.c_7931ab9f IS NOT NULL) AS rate_override,
               COALESCE((SELECT fp.c_0a0b7f19 = 2 FROM t_ae996edc fp WHERE je.c_4cb99522 BETWEEN fp.c_8299fb3d AND fp.c_c0322bcf), false) AS soft_closed
        FROM t_f3158869 je WHERE je.c_14f317c6 = p_entry),
    cand AS (
        SELECT p.c_a2b53e06 AS pid, p.c_c9bff476 AS prev, p.c_bc3b07c1 AS created, COALESCE(st.c_002a7676, p.c_a874380a) AS required
        FROM t_8f2462c5 p CROSS JOIN e
        LEFT JOIN t_e8cadae9 st ON st.c_85629458 = p.c_a2b53e06 AND st.c_41aad198 = p.c_c9bff476
        WHERE p.c_61d707fd AND p.c_9c04c465 = 'journal_entry' AND (p.c_b130782f = 3 OR p.c_b130782f = e.source)
          AND (p.c_5d70e226 IS NULL OR p.c_5d70e226 = e.branch) AND e.base_total >= p.c_a0b367ec
          AND (p.c_1a2f7f5e IS NULL OR p.c_1a2f7f5e = e.jtype)
          AND (p.c_6088d474 IS NULL OR p.c_6088d474 = e.rate_override)
          AND (p.c_ca9500fa IS NULL OR p.c_ca9500fa = e.soft_closed))
    SELECT pid, prev FROM cand GROUP BY pid, prev, created ORDER BY sum(required) DESC, count(*) DESC, created ASC, pid ASC LIMIT 1
$$;

-- The effective plan of an entry: the stages of the governing policy right now, raised to the plan the CURRENT REVISION was submitted under where that was stricter
-- (per stage: the larger count; the role it was submitted under wins). Empty = no approval needed.
CREATE FUNCTION f_177b9f61(p_entry uuid) RETURNS TABLE (stage_no integer, required integer, role_id uuid) LANGUAGE sql STABLE AS $$
    WITH cur AS (
        SELECT COALESCE(st.c_6e0d1427, 1)::integer AS stage_no, COALESCE(st.c_002a7676, p.c_a874380a)::integer AS required, st.c_22b0a7aa AS role_id
        FROM f_e565501a(p_entry) pk JOIN t_8f2462c5 p ON p.c_a2b53e06 = pk.policy_id
        LEFT JOIN t_e8cadae9 st ON st.c_85629458 = p.c_a2b53e06 AND st.c_41aad198 = p.c_c9bff476),
    bound AS (
        SELECT b.c_d293a70b::integer AS stage_no, b.c_95045342::integer AS required, b.c_2b451767 AS role_id
        FROM t_9e0faeaf b JOIN t_f3158869 je ON je.c_14f317c6 = b.c_5e3e1a37 AND je.c_dfe6b7c5 = b.c_63b7acab
        WHERE b.c_17ea6905 = 'journal_entry' AND b.c_5e3e1a37 = p_entry)
    SELECT COALESCE(c.stage_no, b.stage_no), GREATEST(COALESCE(c.required, 0), COALESCE(b.required, 0)), COALESCE(b.role_id, c.role_id)
    FROM cur c FULL OUTER JOIN bound b ON b.stage_no = c.stage_no
    ORDER BY 1
$$;

-- True when nobody rejected the current revision and EVERY stage of the plan has its required number of distinct approvers (stage order is enforced when each decision is recorded).
CREATE FUNCTION f_a4b5fe50(p_entry uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT NOT EXISTS (SELECT 1 FROM t_3fa074e5 d JOIN t_f3158869 je ON je.c_14f317c6 = d.c_175a20c9
                       WHERE d.c_9adee5c9 = 'journal_entry' AND d.c_175a20c9 = p_entry AND d.c_f399e237 = je.c_dfe6b7c5 AND d.c_3a789977 = 2)
       AND NOT EXISTS (SELECT 1 FROM f_177b9f61(p_entry) pl
                       WHERE (SELECT count(DISTINCT d.c_942d312c) FROM t_3fa074e5 d JOIN t_f3158869 je ON je.c_14f317c6 = d.c_175a20c9
                              WHERE d.c_9adee5c9 = 'journal_entry' AND d.c_175a20c9 = p_entry AND d.c_f399e237 = je.c_dfe6b7c5
                                AND d.c_3a789977 = 1 AND d.c_da356dc3 = pl.stage_no) < pl.required)
$$;

-- Stage order, enforced for every writer: a decision on a journal entry's CURRENT revision must be cast for the first stage that is not complete (a later stage waits for the earlier
-- ones; a complete stage takes no more decisions). Decisions on other documents and on stale revisions are not subject to this (they never count).
CREATE FUNCTION f_7cb412a3() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_open integer;
BEGIN
    IF NEW.c_9adee5c9 <> 'journal_entry' THEN RETURN NEW; END IF;
    IF NOT EXISTS (SELECT 1 FROM t_f3158869 je WHERE je.c_14f317c6 = NEW.c_175a20c9 AND je.c_dfe6b7c5 = NEW.c_f399e237) THEN RETURN NEW; END IF;
    IF NOT EXISTS (SELECT 1 FROM f_177b9f61(NEW.c_175a20c9)) THEN RETURN NEW; END IF;
    SELECT min(pl.stage_no) INTO v_open FROM f_177b9f61(NEW.c_175a20c9) pl
    WHERE (SELECT count(DISTINCT d.c_942d312c) FROM t_3fa074e5 d
           WHERE d.c_9adee5c9 = 'journal_entry' AND d.c_175a20c9 = NEW.c_175a20c9 AND d.c_f399e237 = NEW.c_f399e237
             AND d.c_3a789977 = 1 AND d.c_da356dc3 = pl.stage_no) < pl.required;
    IF v_open IS NULL THEN RAISE EXCEPTION 'every approval stage is already complete'; END IF;
    IF NEW.c_da356dc3 <> v_open THEN RAISE EXCEPTION 'a decision must be cast for the open approval stage'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_01accc03 BEFORE INSERT ON t_3fa074e5 FOR EACH ROW EXECUTE FUNCTION f_7cb412a3();

-- Approval gate on the entry itself, for every writer (manual and automatic alike): leaving for Approved or Posted needs the effective plan satisfied for exactly this revision.
-- Posting re-evaluates it, so a policy tightened after approval is honoured and one loosened after submission cannot lower what the entry was submitted under.
CREATE FUNCTION f_7df31fbf() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_4e4c6448 IN (3, 4) AND NEW.c_4e4c6448 IS DISTINCT FROM OLD.c_4e4c6448 THEN
        IF NOT f_a4b5fe50(NEW.c_14f317c6) THEN RAISE EXCEPTION 'the required approvals for this revision are missing'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_b57e2508 BEFORE UPDATE ON t_f3158869 FOR EACH ROW EXECUTE FUNCTION f_7df31fbf();


-- ============================================================================================================================================
-- S3 - fiscal-year closing and opening balances
-- ============================================================================================================================================
-- Sequence (consistent with immutable postings, FiscalPeriodGate and the two-person reopening): the closing entry is an ordinary journal entry dated the LAST DAY of the year (inside the
-- last period, which must be Open or Soft Closed) and the opening-balance entry is an ordinary journal entry dated the FIRST DAY of the next year; both go through the normal journal
-- workflow (numbering, approvals, FiscalPeriodGate, atomic posting). The last period is Final Closed only AFTER the closing entry is posted. A correction is a reversal of the opening
-- balances and of the closing followed by a new closing version; nothing posted is ever edited. Closing and opening entries are linked to the records below, so duplicates are impossible.

-- The company-chosen equity account that receives the annual profit or loss (never hard-coded) and whether closing/opening entries always need an independent approval.
CREATE TABLE t_b80fbcc5 (
    c_a9d7d7ac boolean NOT NULL DEFAULT true,
    c_b4f2a328 uuid NOT NULL,
    c_ccdfd1a6 boolean NOT NULL DEFAULT true,
    c_e421441c bigint NOT NULL,
    c_21160187 timestamptz NOT NULL,
    CONSTRAINT k_09835e78 PRIMARY KEY (c_a9d7d7ac),
    CONSTRAINT k_8620669f FOREIGN KEY (c_b4f2a328) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_f5ab1009 CHECK (c_a9d7d7ac AND c_e421441c >= 1)
);
CREATE FUNCTION f_fde6d5fe() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'closing settings cannot be deleted'; END IF;
    IF NEW.c_e421441c <> OLD.c_e421441c + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_edf794c1 BEFORE UPDATE OR DELETE ON t_b80fbcc5 FOR EACH ROW EXECUTE FUNCTION f_fde6d5fe();
CREATE TRIGGER r_1e8a2ca7 BEFORE TRUNCATE ON t_b80fbcc5 FOR EACH STATEMENT EXECUTE FUNCTION f_fde6d5fe();
-- The profit/loss account must be a postable, ACTIVE EQUITY account of this company (the foreign key keeps it inside the company).
CREATE FUNCTION f_3bdf358f() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM t_11d107a9 a WHERE a.c_29a86598 = NEW.c_b4f2a328 AND a.c_0caeac6a = 3 AND NOT a.c_57d34c8a AND a.c_9761052f) THEN
        RAISE EXCEPTION 'the profit and loss account must be an active, postable equity account';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_7d268211 BEFORE INSERT OR UPDATE ON t_b80fbcc5 FOR EACH ROW EXECUTE FUNCTION f_3bdf358f();
REVOKE ALL ON TABLE t_b80fbcc5 FROM PUBLIC;

-- A year-end CLOSING: one row per version of a year's closing entry (the year, the equity account and the net result are recorded as evidence).
CREATE TABLE t_e040bbbc (
    c_36778b5b uuid NOT NULL DEFAULT gen_random_uuid(),
    c_9b5d60ca uuid NOT NULL,
    c_105a683c integer NOT NULL,
    c_d87a8437 numeric(20,4) NOT NULL,
    c_0867cfb5 uuid NOT NULL,
    c_bb26888c uuid NOT NULL,
    c_14a6d58d jsonb NOT NULL,
    c_9581993a uuid NOT NULL,
    c_d17789b9 timestamptz NOT NULL,
    CONSTRAINT k_2b414f61 PRIMARY KEY (c_36778b5b),
    CONSTRAINT k_9589961f FOREIGN KEY (c_9b5d60ca) REFERENCES t_4b5bb222 (c_cae1c56a),
    CONSTRAINT k_de014d4e FOREIGN KEY (c_bb26888c) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_52a70dc3 FOREIGN KEY (c_0867cfb5) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_a4d388b7 UNIQUE (c_9b5d60ca, c_105a683c),
    CONSTRAINT k_526dbda7 UNIQUE (c_bb26888c),
    CONSTRAINT k_b0db6b95 CHECK (c_105a683c >= 1 AND jsonb_typeof(c_14a6d58d) = 'object')
);
-- The opening balances carried from one year into the NEXT. A year whose profit and loss was closed references that closing; a year with no profit-and-loss activity needs none.
CREATE TABLE t_c1779e24 (
    c_2526ddd0 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_5f38151d uuid NOT NULL,
    c_af0a82fd uuid NULL,
    c_2435b2f2 uuid NOT NULL,
    c_8fcce06f uuid NOT NULL,
    c_1386051a jsonb NOT NULL,
    c_c3941478 uuid NOT NULL,
    c_2b3c0f3a timestamptz NOT NULL,
    CONSTRAINT k_fac07acb PRIMARY KEY (c_2526ddd0),
    CONSTRAINT k_8568f3ff FOREIGN KEY (c_5f38151d) REFERENCES t_4b5bb222 (c_cae1c56a),
    CONSTRAINT k_82789067 FOREIGN KEY (c_af0a82fd) REFERENCES t_e040bbbc (c_36778b5b),
    CONSTRAINT k_5485e4c8 FOREIGN KEY (c_2435b2f2) REFERENCES t_4b5bb222 (c_cae1c56a),
    CONSTRAINT k_9b528a38 FOREIGN KEY (c_8fcce06f) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_2a95437b UNIQUE (c_af0a82fd),
    CONSTRAINT k_e16fc837 UNIQUE (c_8fcce06f),
    CONSTRAINT k_0c26b442 CHECK (jsonb_typeof(c_1386051a) = 'object')
);
CREATE FUNCTION f_5bed5c2c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'year-end closing records are append-only';
END;
$$;
CREATE TRIGGER r_5d14ddcb BEFORE UPDATE OR DELETE ON t_e040bbbc FOR EACH ROW EXECUTE FUNCTION f_5bed5c2c();
CREATE TRIGGER r_64e3d0e1 BEFORE TRUNCATE ON t_e040bbbc FOR EACH STATEMENT EXECUTE FUNCTION f_5bed5c2c();
CREATE FUNCTION f_a3b6d4d4() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'year-end carryforward records are append-only';
END;
$$;
CREATE TRIGGER r_d0a9d030 BEFORE UPDATE OR DELETE ON t_c1779e24 FOR EACH ROW EXECUTE FUNCTION f_a3b6d4d4();
CREATE TRIGGER r_812681c5 BEFORE TRUNCATE ON t_c1779e24 FOR EACH STATEMENT EXECUTE FUNCTION f_a3b6d4d4();
REVOKE ALL ON TABLE t_e040bbbc FROM PUBLIC;
REVOKE ALL ON TABLE t_c1779e24 FROM PUBLIC;

-- What a journal entry is in relation to year-end processing: 1 closing entry, 2 opening-balance entry, 3 reversal of a closing entry, 4 reversal of an opening-balance entry, NULL otherwise.
CREATE FUNCTION f_09413dbf(p_entry uuid) RETURNS smallint LANGUAGE sql STABLE AS $$
    SELECT CASE
        WHEN EXISTS (SELECT 1 FROM t_e040bbbc c WHERE c.c_bb26888c = p_entry) THEN 1
        WHEN EXISTS (SELECT 1 FROM t_c1779e24 k WHERE k.c_8fcce06f = p_entry) THEN 2
        WHEN EXISTS (SELECT 1 FROM t_f3158869 r JOIN t_e040bbbc c ON c.c_bb26888c = r.c_e11acf50 WHERE r.c_14f317c6 = p_entry) THEN 3
        WHEN EXISTS (SELECT 1 FROM t_f3158869 r JOIN t_c1779e24 k ON k.c_8fcce06f = r.c_e11acf50 WHERE r.c_14f317c6 = p_entry) THEN 4
        ELSE NULL END::smallint
$$;

-- The profit-and-loss balances of a year per account and branch (debit minus credit), counting POSTED and REVERSED entries dated inside the year and excluding the year-end closing
-- entries and their reversals themselves (so the figure is the same before and after closing, whatever the closing version).
CREATE FUNCTION f_455769bc(p_year uuid) RETURNS TABLE (account uuid, branch uuid, balance numeric) LANGUAGE sql STABLE AS $$
    SELECT l.c_e6bfb072, l.c_147d3982, SUM(l.c_c560d388 - l.c_03200b86)
    FROM t_783c8d98 l
    JOIN t_f3158869 e ON e.c_14f317c6 = l.c_39596059
    JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 AND a.c_4f5cb580 = 2
    JOIN t_4b5bb222 y ON y.c_cae1c56a = p_year
    WHERE e.c_4e4c6448 IN (4, 5) AND e.c_4cb99522 BETWEEN y.c_5fd77e71 AND y.c_a4d1e8a9
      AND COALESCE(f_09413dbf(e.c_14f317c6), 0) NOT IN (1, 3)
    GROUP BY l.c_e6bfb072, l.c_147d3982
    HAVING SUM(l.c_c560d388 - l.c_03200b86) <> 0
$$;

-- True when a closing ENTRY still matches the ledger exactly: every profit-and-loss balance of its year inverted per account and branch, the result of every branch on the chosen equity
-- account, nothing else.
CREATE FUNCTION f_b6fc8d8c(p_entry uuid) RETURNS boolean LANGUAGE plpgsql STABLE AS $$
DECLARE
    c record;
BEGIN
    SELECT cl.c_9b5d60ca AS year_id, cl.c_0867cfb5 AS equity INTO c FROM t_e040bbbc cl WHERE cl.c_bb26888c = p_entry;
    IF c.year_id IS NULL THEN RETURN false; END IF;
    IF EXISTS (
        (SELECT p.account, p.branch, -p.balance FROM f_455769bc(c.year_id) p)
        EXCEPT
        (SELECT l.c_e6bfb072, l.c_147d3982, SUM(l.c_c560d388 - l.c_03200b86) FROM t_783c8d98 l JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 AND a.c_4f5cb580 = 2
          WHERE l.c_39596059 = p_entry GROUP BY l.c_e6bfb072, l.c_147d3982)) THEN RETURN false; END IF;
    IF EXISTS (
        (SELECT l.c_e6bfb072, l.c_147d3982, SUM(l.c_c560d388 - l.c_03200b86) FROM t_783c8d98 l JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 AND a.c_4f5cb580 = 2
          WHERE l.c_39596059 = p_entry GROUP BY l.c_e6bfb072, l.c_147d3982)
        EXCEPT
        (SELECT p.account, p.branch, -p.balance FROM f_455769bc(c.year_id) p)) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM t_783c8d98 l JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 WHERE l.c_39596059 = p_entry AND a.c_4f5cb580 = 1 AND l.c_e6bfb072 <> c.equity) THEN RETURN false; END IF;
    IF EXISTS (
        SELECT 1 FROM (SELECT p.branch, SUM(p.balance) AS b FROM f_455769bc(c.year_id) p GROUP BY p.branch) x
        FULL JOIN (SELECT l.c_147d3982 AS branch, SUM(l.c_c560d388 - l.c_03200b86) AS b FROM t_783c8d98 l WHERE l.c_39596059 = p_entry AND l.c_e6bfb072 = c.equity GROUP BY l.c_147d3982) y USING (branch)
        WHERE COALESCE(x.b, 0) <> COALESCE(y.b, 0)) THEN RETURN false; END IF;
    RETURN true;
END;
$$;

-- The balance-sheet FLOOR of a date: the start of the latest fiscal year (starting on or before the date) that received POSTED, not reversed opening balances; NULL when there is none. Balance-sheet balances
-- are cumulative from that floor (its opening entry plus the activity after it); before any carryforward they are cumulative over the whole ledger. Reports apply the same rule, so carried-forward
-- balances are never counted twice and reports of earlier years never change when a later year is carried forward.
CREATE FUNCTION f_ff94e130(p_date date) RETURNS date LANGUAGE sql STABLE AS $$
    SELECT max(y.c_5fd77e71) FROM t_c1779e24 k
    JOIN t_f3158869 ke ON ke.c_14f317c6 = k.c_8fcce06f AND ke.c_4e4c6448 = 4
    JOIN t_4b5bb222 y ON y.c_cae1c56a = k.c_2435b2f2
    WHERE y.c_5fd77e71 <= p_date
$$;

-- True when an opening-balance ENTRY still matches the balance-sheet balances (per account and branch, base currency) at the end of the year it carries forward, and nothing but balance-sheet
-- accounts; a year that had profit-and-loss activity needs its closing posted first.
CREATE FUNCTION f_457ef32f(p_entry uuid) RETURNS boolean LANGUAGE plpgsql STABLE AS $$
DECLARE
    k record;
BEGIN
    SELECT cf.c_5f38151d AS from_year, cf.c_af0a82fd AS closing_id, fy.c_a4d1e8a9 AS year_end INTO k
    FROM t_c1779e24 cf JOIN t_4b5bb222 fy ON fy.c_cae1c56a = cf.c_5f38151d WHERE cf.c_8fcce06f = p_entry;
    IF k.from_year IS NULL THEN RETURN false; END IF;
    IF k.closing_id IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM t_e040bbbc cl JOIN t_f3158869 ce ON ce.c_14f317c6 = cl.c_bb26888c WHERE cl.c_36778b5b = k.closing_id AND ce.c_4e4c6448 = 4) THEN RETURN false; END IF;
    ELSIF EXISTS (SELECT 1 FROM f_455769bc(k.from_year)) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM t_783c8d98 l JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 WHERE l.c_39596059 = p_entry AND a.c_4f5cb580 <> 1) THEN RETURN false; END IF;
    IF EXISTS (
        (SELECT l.c_e6bfb072, l.c_147d3982, SUM(l.c_c560d388 - l.c_03200b86) FROM t_783c8d98 l
          JOIN t_f3158869 e ON e.c_14f317c6 = l.c_39596059 JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 AND a.c_4f5cb580 = 1
          WHERE e.c_4e4c6448 IN (4, 5) AND e.c_4cb99522 <= k.year_end AND e.c_4cb99522 >= COALESCE(f_ff94e130(k.year_end), '-infinity'::date) AND e.c_14f317c6 <> p_entry
          GROUP BY l.c_e6bfb072, l.c_147d3982 HAVING SUM(l.c_c560d388 - l.c_03200b86) <> 0)
        EXCEPT
        (SELECT l.c_e6bfb072, l.c_147d3982, SUM(l.c_c560d388 - l.c_03200b86) FROM t_783c8d98 l WHERE l.c_39596059 = p_entry GROUP BY l.c_e6bfb072, l.c_147d3982)) THEN RETURN false; END IF;
    IF EXISTS (
        (SELECT l.c_e6bfb072, l.c_147d3982, SUM(l.c_c560d388 - l.c_03200b86) FROM t_783c8d98 l WHERE l.c_39596059 = p_entry GROUP BY l.c_e6bfb072, l.c_147d3982)
        EXCEPT
        (SELECT l.c_e6bfb072, l.c_147d3982, SUM(l.c_c560d388 - l.c_03200b86) FROM t_783c8d98 l
          JOIN t_f3158869 e ON e.c_14f317c6 = l.c_39596059 JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 AND a.c_4f5cb580 = 1
          WHERE e.c_4e4c6448 IN (4, 5) AND e.c_4cb99522 <= k.year_end AND e.c_4cb99522 >= COALESCE(f_ff94e130(k.year_end), '-infinity'::date) AND e.c_14f317c6 <> p_entry
          GROUP BY l.c_e6bfb072, l.c_147d3982 HAVING SUM(l.c_c560d388 - l.c_03200b86) <> 0)) THEN RETURN false; END IF;
    RETURN true;
END;
$$;

-- A closing is STALE when it was posted but the ledger changed since (a posting into the reopened year): its closing entry or its carried-forward opening entry no longer matches.
CREATE FUNCTION f_26ca2fc5(p_closing uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE((SELECT ce.c_4e4c6448 = 4 AND NOT f_b6fc8d8c(ce.c_14f317c6)
                     FROM t_e040bbbc cl JOIN t_f3158869 ce ON ce.c_14f317c6 = cl.c_bb26888c WHERE cl.c_36778b5b = p_closing), false)
        OR EXISTS (SELECT 1 FROM t_c1779e24 k JOIN t_f3158869 ke ON ke.c_14f317c6 = k.c_8fcce06f
                   WHERE k.c_af0a82fd = p_closing AND ke.c_4e4c6448 = 4 AND NOT f_457ef32f(ke.c_14f317c6))
$$;

-- Duplicates are impossible, even for direct writers: a year has at most one ACTIVE closing (its closing entry or its carryforward is not Reversed (5) or Cancelled (6)) and one active
-- carryforward; a new version needs the previous one fully reversed.
CREATE FUNCTION f_d80dfe38() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    y record;
    e record;
BEGIN
    SELECT * INTO y FROM t_4b5bb222 WHERE c_cae1c56a = NEW.c_9b5d60ca;
    SELECT c_4cb99522 AS d, c_4e4c6448 AS s INTO e FROM t_f3158869 WHERE c_14f317c6 = NEW.c_bb26888c;
    IF e.d IS DISTINCT FROM y.c_a4d1e8a9 THEN RAISE EXCEPTION 'the closing entry must be dated the last day of the fiscal year'; END IF;
    IF e.s NOT IN (1, 2, 3) THEN RAISE EXCEPTION 'a closing starts from a draft closing entry'; END IF;
    IF EXISTS (SELECT 1 FROM t_e040bbbc c JOIN t_f3158869 ce ON ce.c_14f317c6 = c.c_bb26888c
               WHERE c.c_9b5d60ca = NEW.c_9b5d60ca
                 AND (ce.c_4e4c6448 NOT IN (5, 6)
                      OR EXISTS (SELECT 1 FROM t_c1779e24 k JOIN t_f3158869 ke ON ke.c_14f317c6 = k.c_8fcce06f WHERE k.c_af0a82fd = c.c_36778b5b AND ke.c_4e4c6448 NOT IN (5, 6)))) THEN
        RAISE EXCEPTION 'the fiscal year already has an active closing; reverse it before closing again';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_695531d4 BEFORE INSERT ON t_e040bbbc FOR EACH ROW EXECUTE FUNCTION f_d80dfe38();
CREATE FUNCTION f_79e11403() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    f record;
    t record;
BEGIN
    SELECT c_a4d1e8a9 AS e1 INTO f FROM t_4b5bb222 WHERE c_cae1c56a = NEW.c_5f38151d;
    SELECT c_5fd77e71 AS s2 INTO t FROM t_4b5bb222 WHERE c_cae1c56a = NEW.c_2435b2f2;
    IF t.s2 IS DISTINCT FROM f.e1 + 1 THEN RAISE EXCEPTION 'opening balances go into the fiscal year that directly follows the closed year'; END IF;
    IF NEW.c_af0a82fd IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM t_e040bbbc cl JOIN t_f3158869 ce ON ce.c_14f317c6 = cl.c_bb26888c
                       WHERE cl.c_36778b5b = NEW.c_af0a82fd AND cl.c_9b5d60ca = NEW.c_5f38151d AND ce.c_4e4c6448 = 4) THEN
            RAISE EXCEPTION 'opening balances can only follow a POSTED closing entry of the same year';
        END IF;
    ELSIF EXISTS (SELECT 1 FROM f_455769bc(NEW.c_5f38151d)) THEN
        RAISE EXCEPTION 'a year with profit-and-loss activity must be closed before its balances are carried forward';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM t_f3158869 je WHERE je.c_14f317c6 = NEW.c_8fcce06f AND je.c_4cb99522 = t.s2 AND je.c_4e4c6448 IN (1, 2, 3)) THEN
        RAISE EXCEPTION 'the opening entry must be a draft dated the first day of the next fiscal year';
    END IF;
    IF EXISTS (SELECT 1 FROM t_c1779e24 k JOIN t_f3158869 ke ON ke.c_14f317c6 = k.c_8fcce06f
               WHERE k.c_5f38151d = NEW.c_5f38151d AND ke.c_4e4c6448 NOT IN (5, 6)) THEN
        RAISE EXCEPTION 'the balances of this year are already carried forward; reverse the opening balances before carrying them forward again';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_6e7b68e4 BEFORE INSERT ON t_c1779e24 FOR EACH ROW EXECUTE FUNCTION f_79e11403();

-- Posting integrity of year-end entries, for every writer: a CLOSING entry posts only while it matches the ledger exactly, an OPENING entry only while it matches the balance sheet at the
-- end of the closed year (and after its closing is posted), and the reversal of a closing needs the opening balances it produced to be reversed first. A stale draft must be regenerated.
CREATE FUNCTION f_64d2a45a() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_kind smallint;
BEGIN
    IF NEW.c_4e4c6448 <> 4 OR OLD.c_4e4c6448 = 4 THEN RETURN NEW; END IF;
    v_kind := f_09413dbf(NEW.c_14f317c6);
    IF v_kind = 1 THEN
        IF NOT f_b6fc8d8c(NEW.c_14f317c6) THEN RAISE EXCEPTION 'the year-end closing no longer matches the ledger; regenerate it'; END IF;
    ELSIF v_kind = 2 THEN
        IF NOT f_457ef32f(NEW.c_14f317c6) THEN RAISE EXCEPTION 'the opening balances do not match the balance sheet at the end of the closed year; regenerate them'; END IF;
    ELSIF v_kind = 3 THEN
        IF EXISTS (SELECT 1 FROM t_c1779e24 k JOIN t_e040bbbc cl ON cl.c_36778b5b = k.c_af0a82fd
                   JOIN t_f3158869 ke ON ke.c_14f317c6 = k.c_8fcce06f
                   WHERE cl.c_bb26888c = NEW.c_e11acf50 AND ke.c_4e4c6448 NOT IN (5, 6)) THEN
            RAISE EXCEPTION 'reverse the opening balances carried forward by this closing first';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_a0ce1bf4 BEFORE UPDATE ON t_f3158869 FOR EACH ROW EXECUTE FUNCTION f_64d2a45a();

-- Approval policies may now also target year-end processing (1 closing entries and their reversals, 2 opening-balance entries and their reversals).
ALTER TABLE t_8f2462c5 ADD COLUMN c_43137a95 smallint NULL;
ALTER TABLE t_8f2462c5 ADD CONSTRAINT k_7286962e CHECK (c_43137a95 IS NULL OR c_43137a95 IN (1, 2));

-- The governing-policy and plan functions of S2, extended: the year-end condition, and an implicit floor of ONE independent approval for every year-end entry while the company keeps
-- "require approval" switched on (the default), whatever the policies say.
CREATE OR REPLACE FUNCTION f_e565501a(p_entry uuid) RETURNS TABLE (policy_id uuid, policy_revision bigint) LANGUAGE sql STABLE AS $$
    WITH e AS (
        SELECT je.c_14f317c6 AS id, je.c_41c437ea AS source, je.c_59a672dc AS branch, je.c_225ac5cd AS base_total, je.c_960dbf25 AS jtype, je.c_4cb99522 AS d,
               EXISTS (SELECT 1 FROM t_783c8d98 l WHERE l.c_39596059 = je.c_14f317c6 AND l.c_7931ab9f IS NOT NULL) AS rate_override,
               COALESCE((SELECT fp.c_0a0b7f19 = 2 FROM t_ae996edc fp WHERE je.c_4cb99522 BETWEEN fp.c_8299fb3d AND fp.c_c0322bcf), false) AS soft_closed,
               CASE f_09413dbf(je.c_14f317c6) WHEN 1 THEN 1 WHEN 3 THEN 1 WHEN 2 THEN 2 WHEN 4 THEN 2 ELSE NULL END AS cclass
        FROM t_f3158869 je WHERE je.c_14f317c6 = p_entry),
    cand AS (
        SELECT p.c_a2b53e06 AS pid, p.c_c9bff476 AS prev, p.c_bc3b07c1 AS created, COALESCE(st.c_002a7676, p.c_a874380a) AS required
        FROM t_8f2462c5 p CROSS JOIN e
        LEFT JOIN t_e8cadae9 st ON st.c_85629458 = p.c_a2b53e06 AND st.c_41aad198 = p.c_c9bff476
        WHERE p.c_61d707fd AND p.c_9c04c465 = 'journal_entry' AND (p.c_b130782f = 3 OR p.c_b130782f = e.source)
          AND (p.c_5d70e226 IS NULL OR p.c_5d70e226 = e.branch) AND e.base_total >= p.c_a0b367ec
          AND (p.c_1a2f7f5e IS NULL OR p.c_1a2f7f5e = e.jtype)
          AND (p.c_6088d474 IS NULL OR p.c_6088d474 = e.rate_override)
          AND (p.c_ca9500fa IS NULL OR p.c_ca9500fa = e.soft_closed)
          AND (p.c_43137a95 IS NULL OR p.c_43137a95 = e.cclass))
    SELECT pid, prev FROM cand GROUP BY pid, prev, created ORDER BY sum(required) DESC, count(*) DESC, created ASC, pid ASC LIMIT 1
$$;

CREATE OR REPLACE FUNCTION f_177b9f61(p_entry uuid) RETURNS TABLE (stage_no integer, required integer, role_id uuid) LANGUAGE sql STABLE AS $$
    WITH parts AS (
        SELECT COALESCE(st.c_6e0d1427, 1)::integer AS stage_no, COALESCE(st.c_002a7676, p.c_a874380a)::integer AS required, st.c_22b0a7aa AS role_id, 2 AS rank
        FROM f_e565501a(p_entry) pk JOIN t_8f2462c5 p ON p.c_a2b53e06 = pk.policy_id
        LEFT JOIN t_e8cadae9 st ON st.c_85629458 = p.c_a2b53e06 AND st.c_41aad198 = p.c_c9bff476
        UNION ALL
        SELECT b.c_d293a70b::integer, b.c_95045342::integer, b.c_2b451767, 1
        FROM t_9e0faeaf b JOIN t_f3158869 je ON je.c_14f317c6 = b.c_5e3e1a37 AND je.c_dfe6b7c5 = b.c_63b7acab
        WHERE b.c_17ea6905 = 'journal_entry' AND b.c_5e3e1a37 = p_entry
        UNION ALL
        SELECT 1, 1, NULL::uuid, 3 WHERE f_09413dbf(p_entry) IS NOT NULL
          AND COALESCE((SELECT cs.c_ccdfd1a6 FROM t_b80fbcc5 cs LIMIT 1), true))
    SELECT stage_no, max(required)::integer, (array_agg(role_id ORDER BY rank) FILTER (WHERE role_id IS NOT NULL))[1]
    FROM parts GROUP BY stage_no ORDER BY stage_no
$$;
