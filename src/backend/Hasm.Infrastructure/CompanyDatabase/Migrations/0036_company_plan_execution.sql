-- Migration 0036: plan storage, approval and atomic execution of a compensation plan, database layer (Phase 3.8D step 4; owner decision X1-A of 2026-11-21 with mandatory safeguards;
-- design docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md revision 3, confirmed by the independent review docs/review-records/PR-48-plan-execution-design-rev3.md). PROVISIONAL NUMBER: it follows the merge order.
-- Forward-only and ADDITIVE: new tables, new functions and new triggers only. Nothing existing is removed, replaced, altered, paused or relaxed: no existing function is redefined, no existing trigger is touched,
-- no existing constraint is changed, nothing is seeded and no existing row is read or modified. Opaque physical names (D-029) generated deterministically by tools/schema/company-0036-plan-execution.cjs
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
CREATE TABLE t_d29d5b43 (
    c_57ecf0f3 uuid NOT NULL,
    c_958849c3 uuid NOT NULL,
    c_03c8ca55 uuid NOT NULL,
    c_8a883ba7 uuid NOT NULL,
    c_54faa029 bigint NOT NULL,
    c_d2183406 text NOT NULL,
    c_6185829f char(64) NOT NULL,
    c_7ee34d64 char(64) NOT NULL,
    c_469d9275 char(64) NOT NULL,
    c_8755e9a4 char(64) NOT NULL,
    c_0c818e38 char(64) NOT NULL,
    c_9523686c char(64) NOT NULL,
    c_0ada19bb char(64) NOT NULL,
    c_f00fd9d5 char(64) NOT NULL,
    c_b38a0440 smallint NOT NULL,
    c_d1c90d2e bigint NOT NULL,
    c_30e744d0 uuid NOT NULL,
    c_54b27562 timestamptz NOT NULL,
    c_8679a4cd xid8 NOT NULL,
    c_360cd595 uuid NULL,
    c_2e30ff50 uuid NULL,
    c_d7deef75 timestamptz NULL,
    c_2a7148b0 xid8 NULL,
    c_5adc7354 timestamptz NOT NULL,
    CONSTRAINT k_c55a350e PRIMARY KEY (c_57ecf0f3),
    CONSTRAINT k_4902e3e4 FOREIGN KEY (c_958849c3) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_f34e6154 FOREIGN KEY (c_03c8ca55) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_6d74a4ff FOREIGN KEY (c_8a883ba7) REFERENCES t_e92d29e5 (c_9a59d13e),
    CONSTRAINT k_6876378b CHECK (c_b38a0440 IN (1, 2, 3, 4, 5, 6) AND c_d1c90d2e > 0 AND c_54faa029 > 0
        AND char_length(c_d2183406) >= 2 AND c_f00fd9d5 ~ '^[0-9a-f]{64}$'
        AND c_6185829f ~ '^[0-9a-f]{64}$' AND c_7ee34d64 ~ '^[0-9a-f]{64}$' AND c_469d9275 ~ '^[0-9a-f]{64}$' AND c_8755e9a4 ~ '^[0-9a-f]{64}$' AND c_0c818e38 ~ '^[0-9a-f]{64}$' AND c_9523686c ~ '^[0-9a-f]{64}$' AND c_0ada19bb ~ '^[0-9a-f]{64}$'),
    CONSTRAINT k_c006c248 CHECK (
        (c_360cd595 IS NULL) = (c_2e30ff50 IS NULL) AND (c_360cd595 IS NULL) = (c_d7deef75 IS NULL)
        AND (c_b38a0440 NOT IN (2, 3) OR c_360cd595 IS NOT NULL) AND (c_b38a0440 NOT IN (1, 4) OR c_360cd595 IS NULL)
        AND (c_2e30ff50 IS NULL OR c_2e30ff50 <> c_30e744d0)
        AND (c_b38a0440 = 3) = (c_2a7148b0 IS NOT NULL))
);
-- X7: a stock document is bound to at most one live (proposed or approved) plan
CREATE UNIQUE INDEX k_24fb92aa ON t_d29d5b43 (c_8a883ba7) WHERE c_b38a0440 IN (1, 2);
REVOKE ALL ON TABLE t_d29d5b43 FROM PUBLIC;

-- The plan's closure: every (item, warehouse) position the replay may touch (the roots plus every position reached through transfers).
CREATE TABLE t_ce42c8d9 (
    c_7b502b96 uuid NOT NULL,
    c_838708e4 uuid NOT NULL,
    c_f01763cf uuid NOT NULL,
    CONSTRAINT k_0d3836a7 PRIMARY KEY (c_7b502b96, c_838708e4, c_f01763cf),
    CONSTRAINT k_1d0f680d FOREIGN KEY (c_7b502b96) REFERENCES t_d29d5b43 (c_57ecf0f3),
    CONSTRAINT k_776d7843 FOREIGN KEY (c_838708e4) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_87bebdd6 FOREIGN KEY (c_f01763cf) REFERENCES t_d2006f04 (c_a9f73cc2)
);
CREATE INDEX k_5110c425 ON t_ce42c8d9 (c_838708e4, c_f01763cf);
REVOKE ALL ON TABLE t_ce42c8d9 FROM PUBLIC;

-- The compensation lines the approver sees (per affected posting, period, branch and account).
CREATE TABLE t_33eef4c2 (
    c_d2a0bbdc uuid NOT NULL,
    c_d3162a08 integer NOT NULL,
    c_20686467 uuid NULL,
    c_3ef0fd55 date NOT NULL,
    c_87722ae4 uuid NOT NULL,
    c_7e924492 uuid NOT NULL,
    c_e4446dd3 uuid NOT NULL,
    c_e92133d8 numeric(28,6) NOT NULL,
    c_89f20e1e numeric(28,6) NOT NULL,
    CONSTRAINT k_2e35fd44 PRIMARY KEY (c_d2a0bbdc, c_d3162a08),
    CONSTRAINT k_4b4fb63a FOREIGN KEY (c_d2a0bbdc) REFERENCES t_d29d5b43 (c_57ecf0f3),
    CONSTRAINT k_2c4c0dd2 FOREIGN KEY (c_20686467) REFERENCES t_4d4b251f (c_fc71418e),
    CONSTRAINT k_a4519319 FOREIGN KEY (c_87722ae4) REFERENCES t_ae996edc (c_d61cbb9c),
    CONSTRAINT k_ca83ab13 FOREIGN KEY (c_7e924492) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_65bb018b FOREIGN KEY (c_e4446dd3) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_c0fb831f CHECK (c_d3162a08 > 0 AND c_e92133d8 >= 0 AND c_89f20e1e >= 0
        AND (c_e92133d8 > 0) <> (c_89f20e1e > 0))
);
REVOKE ALL ON TABLE t_33eef4c2 FROM PUBLIC;

-- The plan's history (append-only).
CREATE TABLE t_902ed59f (
    c_c4e72eea uuid NOT NULL,
    c_c37b2e06 uuid NOT NULL,
    c_b4c8e716 smallint NOT NULL,
    c_4fe0b3c4 uuid NOT NULL,
    c_b7e1f592 timestamptz NOT NULL,
    c_b3345e9b varchar(500) NULL,
    CONSTRAINT k_533f3563 PRIMARY KEY (c_c4e72eea),
    CONSTRAINT k_0c5301a0 FOREIGN KEY (c_c37b2e06) REFERENCES t_d29d5b43 (c_57ecf0f3),
    CONSTRAINT k_726be552 CHECK (c_b4c8e716 IN (1, 2, 3, 4, 5, 6))
);
CREATE INDEX k_7e62869e ON t_902ed59f (c_c37b2e06);
REVOKE ALL ON TABLE t_902ed59f FROM PUBLIC;

-- The approval: bound to the plan revision and to every fingerprint of the plan (the closure hash included).
CREATE TABLE t_a1458a29 (
    c_ea73ad4f uuid NOT NULL,
    c_ded9e195 uuid NOT NULL,
    c_1409be1e bigint NOT NULL,
    c_a5615fe1 char(64) NOT NULL,
    c_6a1e9ed3 char(64) NOT NULL,
    c_5c5f0997 char(64) NOT NULL,
    c_6ac9e2d3 char(64) NOT NULL,
    c_d6f5be21 char(64) NOT NULL,
    c_931d96e4 char(64) NOT NULL,
    c_a36fdae3 char(64) NOT NULL,
    c_841d0b61 char(64) NOT NULL,
    c_263ac270 uuid NOT NULL,
    c_d32ab80b timestamptz NOT NULL,
    c_31e7b8a6 varchar(500) NULL,
    CONSTRAINT k_12af30bf PRIMARY KEY (c_ea73ad4f),
    CONSTRAINT k_78225c3f FOREIGN KEY (c_ded9e195) REFERENCES t_d29d5b43 (c_57ecf0f3),
    CONSTRAINT k_d1686fa5 UNIQUE (c_ded9e195, c_1409be1e)
);
REVOKE ALL ON TABLE t_a1458a29 FROM PUBLIC;

-- The single execution of a plan. The posting of the event and the audit event are named when the row is written (the row is append-only); both must exist at commit.
CREATE TABLE t_5b1a018a (
    c_02771c43 uuid NOT NULL,
    c_934901c6 uuid NOT NULL,
    c_07602789 uuid NOT NULL,
    c_f462f8b9 uuid NOT NULL,
    c_17e57b73 uuid NOT NULL,
    c_f12bc65e timestamptz NOT NULL,
    c_fbd0bc4b xid8 NOT NULL,
    CONSTRAINT k_6d42f362 PRIMARY KEY (c_02771c43),
    CONSTRAINT k_721e5580 FOREIGN KEY (c_934901c6) REFERENCES t_d29d5b43 (c_57ecf0f3),
    CONSTRAINT k_d7bf6e5a UNIQUE (c_934901c6),
    CONSTRAINT k_66b036dc UNIQUE (c_f462f8b9),
    CONSTRAINT k_70b022d1 UNIQUE (c_07602789),
    CONSTRAINT k_49df5d6a FOREIGN KEY (c_07602789) REFERENCES t_4d4b251f (c_fc71418e) DEFERRABLE INITIALLY DEFERRED
);
REVOKE ALL ON TABLE t_5b1a018a FROM PUBLIC;

-- The compensation journals of an execution, one per bucket (the journal source key is plan + bucket, so a repeat cannot create a second set).
CREATE TABLE t_d710e042 (
    c_d5408ab4 uuid NOT NULL,
    c_796f7746 integer NOT NULL,
    c_576a109a uuid NOT NULL,
    CONSTRAINT k_36141ae1 PRIMARY KEY (c_d5408ab4, c_796f7746),
    CONSTRAINT k_367f3417 FOREIGN KEY (c_d5408ab4) REFERENCES t_5b1a018a (c_02771c43),
    CONSTRAINT k_2616d9d1 FOREIGN KEY (c_576a109a) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_5355e524 UNIQUE (c_576a109a),
    CONSTRAINT k_3a1cdf32 CHECK (c_796f7746 > 0)
);
REVOKE ALL ON TABLE t_d710e042 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- epoch membership and lineage
-- An epoch operation keeps its NATURAL kind (the kind CHECK of 0014 is not widened); its meaning comes from this side table (the pattern of 0020, DB-1).
CREATE TABLE t_3bd31c9d (
    c_317cffcf uuid NOT NULL,
    c_e909f2d4 uuid NOT NULL,
    c_1977183c smallint NOT NULL,
    c_6968655e uuid NULL,
    CONSTRAINT k_4a1ec9df PRIMARY KEY (c_317cffcf),
    CONSTRAINT k_9f9ab0fb FOREIGN KEY (c_317cffcf) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_290dd109 FOREIGN KEY (c_e909f2d4) REFERENCES t_5b1a018a (c_02771c43),
    CONSTRAINT k_b6536f84 FOREIGN KEY (c_6968655e) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_c8b4d8ed CHECK (c_1977183c IN (1, 2, 3) AND (c_1977183c = 2) = (c_6968655e IS NULL)
        AND c_6968655e IS DISTINCT FROM c_317cffcf)
);
-- an original operation is stornoed once and rebooked once, ever; an execution has one event
CREATE UNIQUE INDEX k_cad8b2cb ON t_3bd31c9d (c_6968655e, c_1977183c) WHERE c_6968655e IS NOT NULL;
CREATE UNIQUE INDEX k_e87f8ebe ON t_3bd31c9d (c_e909f2d4) WHERE c_1977183c = 2;
CREATE INDEX k_ac331325 ON t_3bd31c9d (c_e909f2d4);
REVOKE ALL ON TABLE t_3bd31c9d FROM PUBLIC;

CREATE TABLE t_c38ba586 (
    c_ea245623 bigint NOT NULL,
    c_d49e9cf0 bigint NOT NULL,
    c_1a01c03e bigint NOT NULL,
    c_dcb2e603 uuid NOT NULL,
    CONSTRAINT k_85708f58 PRIMARY KEY (c_ea245623),
    CONSTRAINT k_d6b3ffcc UNIQUE (c_d49e9cf0),
    CONSTRAINT k_d1de1ce5 UNIQUE (c_1a01c03e),
    CONSTRAINT k_536e4ec5 FOREIGN KEY (c_ea245623) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_ab6946f8 FOREIGN KEY (c_d49e9cf0) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_aafad032 FOREIGN KEY (c_1a01c03e) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_23eb69bc FOREIGN KEY (c_dcb2e603) REFERENCES t_5b1a018a (c_02771c43),
    CONSTRAINT k_30381427 CHECK (c_ea245623 <> c_d49e9cf0 AND c_ea245623 <> c_1a01c03e
        AND c_d49e9cf0 <> c_1a01c03e)
);
CREATE INDEX k_e3a78231 ON t_c38ba586 (c_dcb2e603);
REVOKE ALL ON TABLE t_c38ba586 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- shared functions (all read table rows only; none reads a setting)
-- The closure hash, byte-exact (review L1): each pair is the lowercase hyphenated text of the item uuid, a colon and the lowercase hyphenated text of the warehouse uuid; the pairs are sorted by (item, warehouse)
-- in uuid order (the ORDER BY is on the uuid columns, never on text, so no collation is involved), joined by ONE newline without a trailing newline, encoded in UTF-8; the result is the lowercase hex SHA-256.
-- An empty closure hashes the empty string. PostgreSQL is the only place that computes it.
CREATE FUNCTION f_724b73d6(p_plan uuid) RETURNS char(64) LANGUAGE sql AS $$
    SELECT encode(sha256(convert_to(COALESCE(string_agg(lower(c.c_838708e4::text) || ':' || lower(c.c_f01763cf::text), E'\n'
        ORDER BY c.c_838708e4, c.c_f01763cf), ''), 'UTF8')), 'hex')::char(64)
    FROM public.t_ce42c8d9 c WHERE c.c_7b502b96 = p_plan
$$;

-- X3: true when the user is independent of the plan: not its creator, and not the creator, an editor or the submitter of the event's stock document (every editor counts, which contains the last editor).
CREATE FUNCTION f_e08495de(p_plan uuid, p_user uuid) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE
    p record;
BEGIN
    SELECT c_30e744d0 AS created_by, c_8a883ba7 AS document INTO p FROM public.t_d29d5b43 WHERE c_57ecf0f3 = p_plan;
    IF NOT FOUND OR p_user IS NULL THEN RETURN false; END IF;
    IF p_user = p.created_by THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_902ed59f e WHERE e.c_c37b2e06 = p_plan AND e.c_b4c8e716 = 1 AND e.c_4fe0b3c4 = p_user) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_e92d29e5 d WHERE d.c_9a59d13e = p.document AND (d.c_6b6eb86b = p_user OR d.c_afb8d9de = p_user)) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_b9231a6b e WHERE e.c_fd72fc35 = p.document AND e.c_2fe7652c IN (1, 2, 3) AND e.c_2a6bae0c = p_user) THEN RETURN false; END IF;
    RETURN true;
END;
$$;

-- NULL when the approval is a sound approval of the plan; otherwise the reason. Sound: it belongs to the plan, repeats every fingerprint of the plan, the closure rows still hash to the plan's closure hash, the
-- approver is independent (X3), and the event's stock document is approved at the planned revision (X7).
CREATE FUNCTION f_73471693(p_plan uuid, p_approval uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    p public.t_d29d5b43%ROWTYPE;
    a public.t_a1458a29%ROWTYPE;
BEGIN
    SELECT * INTO p FROM public.t_d29d5b43 WHERE c_57ecf0f3 = p_plan;
    IF NOT FOUND THEN RETURN 'plan_not_found'; END IF;
    SELECT * INTO a FROM public.t_a1458a29 WHERE c_ea73ad4f = p_approval AND c_ded9e195 = p_plan;
    IF NOT FOUND THEN RETURN 'approval_not_found'; END IF;
    IF a.c_a5615fe1 <> p.c_6185829f OR a.c_6a1e9ed3 <> p.c_7ee34d64 OR a.c_5c5f0997 <> p.c_469d9275 OR a.c_6ac9e2d3 <> p.c_8755e9a4 OR a.c_d6f5be21 <> p.c_0c818e38 OR a.c_931d96e4 <> p.c_9523686c OR a.c_a36fdae3 <> p.c_0ada19bb OR a.c_841d0b61 <> p.c_f00fd9d5 THEN RETURN 'approval_fingerprint_mismatch'; END IF;
    IF f_724b73d6(p_plan) <> p.c_f00fd9d5 THEN RETURN 'closure_hash_mismatch'; END IF;
    IF NOT f_e08495de(p_plan, a.c_263ac270) THEN RETURN 'approver_not_independent'; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_e92d29e5 d WHERE d.c_9a59d13e = p.c_8a883ba7 AND d.c_3fc0f063 = 3 AND d.c_9ba641c8 = p.c_54faa029) THEN
        RETURN 'document_not_approved';
    END IF;
    -- review L1: a document that already has a stock posting is never executed by a plan (a posting is unique per document, so such a plan could never execute); this is read at approval and again at execution
    IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = p.c_8a883ba7) THEN RETURN 'document_already_posted'; END IF;
    RETURN NULL;
END;
$$;

-- The plan of an execution that is LIVE: written in the current top-level transaction (xid8, correct inside savepoints), of a plan that is approved, by an approver who is not its creator, with an approval whose
-- fingerprints equal the plan's. NULL otherwise. This is the same-transaction binding of every epoch row; it reads rows only.
CREATE FUNCTION f_4f422808(p_execution uuid) RETURNS uuid LANGUAGE sql AS $$
    SELECT p.c_57ecf0f3
    FROM public.t_5b1a018a x
    JOIN public.t_d29d5b43 p ON p.c_57ecf0f3 = x.c_934901c6
    JOIN public.t_a1458a29 a ON a.c_ea73ad4f = p.c_360cd595 AND a.c_ded9e195 = p.c_57ecf0f3
    WHERE x.c_02771c43 = p_execution AND x.c_fbd0bc4b = pg_current_xact_id() AND p.c_b38a0440 = 2
      AND a.c_263ac270 = p.c_2e30ff50 AND a.c_263ac270 <> p.c_30e744d0
      AND NOT (a.c_a5615fe1 <> p.c_6185829f OR a.c_6a1e9ed3 <> p.c_7ee34d64 OR a.c_5c5f0997 <> p.c_469d9275 OR a.c_6ac9e2d3 <> p.c_8755e9a4 OR a.c_d6f5be21 <> p.c_0c818e38 OR a.c_931d96e4 <> p.c_9523686c OR a.c_a36fdae3 <> p.c_0ada19bb) AND a.c_841d0b61 = p.c_f00fd9d5
$$;

-- Append-only guard shared by every new table except the plan.
CREATE FUNCTION f_9580f5d3() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'compensation plan and epoch records are append-only';
END;
$$;

-- ---------------------------------------------------------------------------------------------------------------- the plan: immutability, status transitions, closure hash
CREATE FUNCTION f_a2ab9092() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_reason text;
BEGIN
    IF TG_LEVEL = 'STATEMENT' OR TG_OP = 'DELETE' THEN RAISE EXCEPTION 'compensation plans are never removed'; END IF;
    IF TG_OP = 'INSERT' THEN
        -- never client-supplied: the creating transaction and the closure hash (of the closure written so far: none yet)
        NEW.c_8679a4cd := pg_current_xact_id();
        NEW.c_f00fd9d5 := f_724b73d6(NEW.c_57ecf0f3);
        IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = NEW.c_8a883ba7) THEN RAISE EXCEPTION 'document_already_posted: a plan is not written for a document that already has a stock posting'; END IF;
        IF NEW.c_b38a0440 <> 1 OR NEW.c_d1c90d2e <> 1 OR NEW.c_360cd595 IS NOT NULL OR NEW.c_2e30ff50 IS NOT NULL
           OR NEW.c_d7deef75 IS NOT NULL OR NEW.c_2a7148b0 IS NOT NULL THEN
            RAISE EXCEPTION 'a compensation plan starts as proposed, at revision 1';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_57ecf0f3 IS DISTINCT FROM OLD.c_57ecf0f3 OR NEW.c_958849c3 IS DISTINCT FROM OLD.c_958849c3
       OR NEW.c_03c8ca55 IS DISTINCT FROM OLD.c_03c8ca55 OR NEW.c_8a883ba7 IS DISTINCT FROM OLD.c_8a883ba7
       OR NEW.c_54faa029 IS DISTINCT FROM OLD.c_54faa029 OR NEW.c_d2183406 IS DISTINCT FROM OLD.c_d2183406
       OR NEW.c_6185829f IS DISTINCT FROM OLD.c_6185829f OR NEW.c_7ee34d64 IS DISTINCT FROM OLD.c_7ee34d64 OR NEW.c_469d9275 IS DISTINCT FROM OLD.c_469d9275 OR NEW.c_8755e9a4 IS DISTINCT FROM OLD.c_8755e9a4 OR NEW.c_0c818e38 IS DISTINCT FROM OLD.c_0c818e38 OR NEW.c_9523686c IS DISTINCT FROM OLD.c_9523686c OR NEW.c_0ada19bb IS DISTINCT FROM OLD.c_0ada19bb
       OR NEW.c_30e744d0 IS DISTINCT FROM OLD.c_30e744d0 OR NEW.c_54b27562 IS DISTINCT FROM OLD.c_54b27562
       OR NEW.c_8679a4cd IS DISTINCT FROM OLD.c_8679a4cd THEN
        RAISE EXCEPTION 'the payload, the fingerprints and the origin of a compensation plan are immutable';
    END IF;
    -- the closure hash follows the closure rows while they can still be written (a proposed plan in its creating transaction); afterwards it is frozen
    IF OLD.c_b38a0440 = 1 AND OLD.c_8679a4cd = pg_current_xact_id() THEN
        NEW.c_f00fd9d5 := f_724b73d6(OLD.c_57ecf0f3);
    ELSIF NEW.c_f00fd9d5 IS DISTINCT FROM OLD.c_f00fd9d5 THEN
        RAISE EXCEPTION 'the closure hash of a compensation plan is immutable';
    END IF;
    IF NEW.c_b38a0440 = OLD.c_b38a0440 THEN
        IF NEW.c_d1c90d2e IS DISTINCT FROM OLD.c_d1c90d2e OR NEW.c_360cd595 IS DISTINCT FROM OLD.c_360cd595
           OR NEW.c_2e30ff50 IS DISTINCT FROM OLD.c_2e30ff50 OR NEW.c_d7deef75 IS DISTINCT FROM OLD.c_d7deef75
           OR NEW.c_2a7148b0 IS DISTINCT FROM OLD.c_2a7148b0 OR NEW.c_5adc7354 IS DISTINCT FROM OLD.c_5adc7354 THEN
            RAISE EXCEPTION 'a compensation plan changes only by a status transition';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_d1c90d2e <> OLD.c_d1c90d2e + 1 THEN RAISE EXCEPTION 'a status transition advances the plan revision by exactly one'; END IF;
    IF NOT ((OLD.c_b38a0440 = 1 AND NEW.c_b38a0440 IN (2, 4, 5, 6)) OR (OLD.c_b38a0440 = 2 AND NEW.c_b38a0440 IN (3, 5, 6))) THEN
        RAISE EXCEPTION 'this status transition of a compensation plan is not allowed';
    END IF;
    IF NEW.c_b38a0440 = 2 THEN
        IF NEW.c_360cd595 IS NULL OR NEW.c_2e30ff50 IS NULL OR NEW.c_d7deef75 IS NULL THEN RAISE EXCEPTION 'an approved plan names its approval'; END IF;
        IF NOT EXISTS (SELECT 1 FROM public.t_a1458a29 a WHERE a.c_ea73ad4f = NEW.c_360cd595 AND a.c_ded9e195 = OLD.c_57ecf0f3
                       AND a.c_1409be1e = OLD.c_d1c90d2e AND a.c_263ac270 = NEW.c_2e30ff50) THEN
            RAISE EXCEPTION 'a plan is approved by the approval of its current revision';
        END IF;
        -- the approval re-checks the closure (hash recomputed from the closure rows), the fingerprints, the segregation of duties and the event document
        v_reason := f_73471693(OLD.c_57ecf0f3, NEW.c_360cd595);
        IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    ELSIF NEW.c_360cd595 IS DISTINCT FROM OLD.c_360cd595 OR NEW.c_2e30ff50 IS DISTINCT FROM OLD.c_2e30ff50
          OR NEW.c_d7deef75 IS DISTINCT FROM OLD.c_d7deef75 THEN
        RAISE EXCEPTION 'the approval of a compensation plan is set once, by its approval';
    END IF;
    IF NEW.c_b38a0440 = 3 THEN
        IF NOT EXISTS (SELECT 1 FROM public.t_5b1a018a x WHERE x.c_934901c6 = OLD.c_57ecf0f3 AND x.c_fbd0bc4b = pg_current_xact_id()) THEN
            RAISE EXCEPTION 'a plan is marked executed only in the transaction of its execution';
        END IF;
        IF f_724b73d6(OLD.c_57ecf0f3) <> OLD.c_f00fd9d5 THEN RAISE EXCEPTION 'the closure of the plan no longer matches its closure hash'; END IF;
        NEW.c_2a7148b0 := pg_current_xact_id();
    ELSIF NEW.c_2a7148b0 IS NOT NULL THEN
        RAISE EXCEPTION 'only an executed plan carries its executing transaction';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_6b7f214a BEFORE INSERT OR UPDATE OR DELETE ON t_d29d5b43 FOR EACH ROW EXECUTE FUNCTION f_a2ab9092();
CREATE TRIGGER r_2e7ebe3b BEFORE TRUNCATE ON t_d29d5b43 FOR EACH STATEMENT EXECUTE FUNCTION f_a2ab9092();

-- At commit (review L2): the plan and its closure were written together; the closure contains the plan's root position and hashes to the stored closure hash.
CREATE FUNCTION f_c58d3bdf() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
BEGIN
    SELECT c_958849c3 AS item, c_03c8ca55 AS warehouse, c_f00fd9d5 AS closure_hash INTO p FROM public.t_d29d5b43 WHERE c_57ecf0f3 = NEW.c_57ecf0f3;
    IF NOT EXISTS (SELECT 1 FROM public.t_ce42c8d9 c WHERE c.c_7b502b96 = NEW.c_57ecf0f3 AND c.c_838708e4 = p.item
                   AND c.c_f01763cf = p.warehouse) THEN
        RAISE EXCEPTION 'the closure of a compensation plan contains its root position and is written with the plan';
    END IF;
    IF f_724b73d6(NEW.c_57ecf0f3) <> p.closure_hash THEN RAISE EXCEPTION 'the closure of the plan does not match its closure hash'; END IF;
    -- review L1: re-checked at commit, so a posting committed after the insert-time check (read committed) is seen; under REPEATABLE READ the approval and the execution re-read it again
    IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = NEW.c_8a883ba7) THEN RAISE EXCEPTION 'document_already_posted: a plan is not written for a document that already has a stock posting'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_09cf2b7b AFTER INSERT ON t_d29d5b43 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_c58d3bdf();

-- Closure rows and lines: written only while the plan is proposed AND in the transaction that created the plan (review M3 and L2). A new closure therefore needs a new plan and a new approval.
CREATE FUNCTION f_23245381() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
BEGIN
    IF TG_TABLE_NAME = 't_ce42c8d9' THEN v_plan := NEW.c_7b502b96; ELSE v_plan := NEW.c_d2a0bbdc; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_d29d5b43 p WHERE p.c_57ecf0f3 = v_plan AND p.c_b38a0440 = 1 AND p.c_8679a4cd = pg_current_xact_id()) THEN
        RAISE EXCEPTION 'the closure and the lines of a compensation plan are written with the plan, in its creating transaction';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_e79521a9 BEFORE INSERT ON t_ce42c8d9 FOR EACH ROW EXECUTE FUNCTION f_23245381();
CREATE TRIGGER r_8e2884ba BEFORE INSERT ON t_33eef4c2 FOR EACH ROW EXECUTE FUNCTION f_23245381();
-- every closure row refreshes the plan's hash through the plan's own trigger (which computes it; the value written here is ignored)
CREATE FUNCTION f_b19d357c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    UPDATE public.t_d29d5b43 SET c_f00fd9d5 = c_f00fd9d5 WHERE c_57ecf0f3 = NEW.c_7b502b96;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_62087036 AFTER INSERT ON t_ce42c8d9 FOR EACH ROW EXECUTE FUNCTION f_b19d357c();
CREATE TRIGGER r_2f573573 BEFORE UPDATE OR DELETE ON t_ce42c8d9 FOR EACH ROW EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_848cd07e BEFORE TRUNCATE ON t_ce42c8d9 FOR EACH STATEMENT EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_eaa2fa95 BEFORE UPDATE OR DELETE ON t_33eef4c2 FOR EACH ROW EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_ea361749 BEFORE TRUNCATE ON t_33eef4c2 FOR EACH STATEMENT EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_85b77e7d BEFORE UPDATE OR DELETE ON t_902ed59f FOR EACH ROW EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_cdb40449 BEFORE TRUNCATE ON t_902ed59f FOR EACH STATEMENT EXECUTE FUNCTION f_9580f5d3();

-- ---------------------------------------------------------------------------------------------------------------- the approval
CREATE FUNCTION f_1e352f8c() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_reason text;
BEGIN
    -- the plan row is locked first: approvals, executions and status changes of one plan are serialized for every writer
    SELECT c_b38a0440 AS status, c_d1c90d2e AS revision INTO p FROM public.t_d29d5b43 WHERE c_57ecf0f3 = NEW.c_ded9e195 FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'the plan of an approval does not exist'; END IF;
    IF p.status <> 1 OR p.revision <> NEW.c_1409be1e THEN RAISE EXCEPTION 'an approval is given to the current revision of a proposed plan'; END IF;
    v_reason := f_73471693(NEW.c_ded9e195, NEW.c_ea73ad4f);
    IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_cad7a14f AFTER INSERT ON t_a1458a29 FOR EACH ROW EXECUTE FUNCTION f_1e352f8c();
-- at commit: the approval is the one the plan was approved with (an approval never exists without the status change of its plan)
CREATE FUNCTION f_7be48dcb() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_d29d5b43 p WHERE p.c_57ecf0f3 = NEW.c_ded9e195 AND p.c_360cd595 = NEW.c_ea73ad4f
                   AND p.c_2e30ff50 = NEW.c_263ac270) THEN
        RAISE EXCEPTION 'an approval is committed together with the approval of its plan';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_384b91b5 AFTER INSERT ON t_a1458a29 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_7be48dcb();
CREATE TRIGGER r_57661a3f BEFORE UPDATE OR DELETE ON t_a1458a29 FOR EACH ROW EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_38b906cc BEFORE TRUNCATE ON t_a1458a29 FOR EACH STATEMENT EXECUTE FUNCTION f_9580f5d3();

-- ---------------------------------------------------------------------------------------------------------------- the execution
CREATE FUNCTION f_9350f1b3() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_reason text;
BEGIN
    -- never client-supplied: the top-level transaction id (64 bits, no wraparound, the same inside a savepoint)
    NEW.c_fbd0bc4b := pg_current_xact_id();
    SELECT c_b38a0440 AS status, c_360cd595 AS approval, c_30e744d0 AS created_by INTO p
      FROM public.t_d29d5b43 WHERE c_57ecf0f3 = NEW.c_934901c6 FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'the plan of an execution does not exist'; END IF;
    IF p.status <> 2 THEN RAISE EXCEPTION 'only an approved compensation plan is executed, once'; END IF;
    v_reason := f_73471693(NEW.c_934901c6, p.approval);
    IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    IF NEW.c_17e57b73 = p.created_by THEN RAISE EXCEPTION 'a compensation plan is never executed by its creator'; END IF;
    -- X5: no position of this plan's closure lies in the closure of another executed plan
    IF EXISTS (SELECT 1 FROM public.t_ce42c8d9 c
               JOIN public.t_ce42c8d9 o ON o.c_838708e4 = c.c_838708e4 AND o.c_f01763cf = c.c_f01763cf
                    AND o.c_7b502b96 <> c.c_7b502b96
               JOIN public.t_5b1a018a x ON x.c_934901c6 = o.c_7b502b96
               WHERE c.c_7b502b96 = NEW.c_934901c6) THEN
        RAISE EXCEPTION 'plan_on_epoch_position: a position of the closure was already rewritten by an executed plan';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_b71dd7e6 BEFORE INSERT ON t_5b1a018a FOR EACH ROW EXECUTE FUNCTION f_9350f1b3();
CREATE TRIGGER r_9e299e18 BEFORE UPDATE OR DELETE ON t_5b1a018a FOR EACH ROW EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_c7243cb5 BEFORE TRUNCATE ON t_5b1a018a FOR EACH STATEMENT EXECUTE FUNCTION f_9580f5d3();

CREATE FUNCTION f_23ba2963() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF f_4f422808(NEW.c_d5408ab4) IS NULL THEN RAISE EXCEPTION 'a compensation journal is recorded inside the execution of its approved plan'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_8bb4333d BEFORE INSERT ON t_d710e042 FOR EACH ROW EXECUTE FUNCTION f_23ba2963();
CREATE TRIGGER r_d3254521 BEFORE UPDATE OR DELETE ON t_d710e042 FOR EACH ROW EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_14b18476 BEFORE TRUNCATE ON t_d710e042 FOR EACH STATEMENT EXECUTE FUNCTION f_9580f5d3();

-- ---------------------------------------------------------------------------------------------------------------- the epoch member (design 3.2)
CREATE FUNCTION f_218ccd76() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
    o record;
    v_original_kind smallint;
BEGIN
    v_plan := f_4f422808(NEW.c_e909f2d4);
    IF v_plan IS NULL THEN RAISE EXCEPTION 'an epoch member belongs to the execution, written in this transaction, of an approved compensation plan'; END IF;
    SELECT c_7f2ea712 AS kind, c_03a68cea AS reverses INTO o FROM public.t_a648ca40 WHERE c_f47f15da = NEW.c_317cffcf;
    IF NOT FOUND THEN RAISE EXCEPTION 'the operation of an epoch member does not exist'; END IF;
    -- epoch operations are never reversal operations (review L1)
    IF o.kind = 4 OR o.reverses IS NOT NULL THEN RAISE EXCEPTION 'a reversal operation is never an epoch member'; END IF;
    -- the operation is being built now: it has no entry yet and no obligation or settlement refers to it (an empty operation of an earlier transaction exists only for those, 0020)
    IF EXISTS (SELECT 1 FROM public.t_bc727c1d WHERE c_eb34b7d8 = NEW.c_317cffcf) THEN RAISE EXCEPTION 'an epoch member is an operation without entries yet'; END IF;
    IF EXISTS (SELECT 1 FROM public.t_b56b119d WHERE c_1872eef0 = NEW.c_317cffcf)
       OR EXISTS (SELECT 1 FROM public.t_d370b1d6 WHERE c_7208ba7b = NEW.c_317cffcf) THEN
        RAISE EXCEPTION 'an operation of a negative-stock obligation or settlement is never an epoch member';
    END IF;
    IF NEW.c_1977183c IN (1, 3) THEN
        -- X5: no epoch on an epoch
        IF EXISTS (SELECT 1 FROM public.t_3bd31c9d m WHERE m.c_317cffcf = NEW.c_6968655e) THEN RAISE EXCEPTION 'plan_on_epoch_position: an epoch operation is never rewritten by another epoch'; END IF;
        SELECT c_7f2ea712 INTO v_original_kind FROM public.t_a648ca40 WHERE c_f47f15da = NEW.c_6968655e;
        IF v_original_kind IS DISTINCT FROM o.kind THEN RAISE EXCEPTION 'a storno or rebook operation keeps the kind of its original operation'; END IF;
        -- the entries of the original that the epoch takes back lie in the plan's closure: an original without any entry there is not rewritten by this plan
        IF NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e
                       JOIN public.t_a2903683 ps ON ps.c_6b4abdd9 = e.c_98321e7f
                       JOIN public.t_ce42c8d9 c ON c.c_7b502b96 = v_plan AND c.c_838708e4 = ps.c_3380c61e
                            AND c.c_f01763cf = ps.c_f0a91938
                       WHERE e.c_eb34b7d8 = NEW.c_6968655e) THEN
            RAISE EXCEPTION 'the original operation of an epoch member has no entry in the closure of the plan';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_f9b106ce BEFORE INSERT ON t_3bd31c9d FOR EACH ROW EXECUTE FUNCTION f_218ccd76();
CREATE TRIGGER r_088791ac BEFORE UPDATE OR DELETE ON t_3bd31c9d FOR EACH ROW EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_5a40a449 BEFORE TRUNCATE ON t_3bd31c9d FOR EACH STATEMENT EXECUTE FUNCTION f_9580f5d3();

-- Lineage: original entry -> storno entry -> rebook entry, all of one execution of this transaction.
CREATE FUNCTION f_4ae787a0() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    oe record;
    se record;
    re record;
BEGIN
    IF f_4f422808(NEW.c_dcb2e603) IS NULL THEN RAISE EXCEPTION 'an epoch lineage row belongs to the execution, written in this transaction, of an approved compensation plan'; END IF;
    SELECT c_eb34b7d8 AS op, c_98321e7f AS pos, c_4105796c AS kind, c_e0cd1a14 AS dir, c_12665ffb AS d, c_dba27822 AS qty
      INTO oe FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_ea245623;
    SELECT c_eb34b7d8 AS op, c_4f9e777a AS reverses INTO se FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_d49e9cf0;
    SELECT c_eb34b7d8 AS op, c_98321e7f AS pos, c_4105796c AS kind, c_e0cd1a14 AS dir, c_12665ffb AS d, c_dba27822 AS qty,
           c_4f9e777a AS reverses INTO re FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_1a01c03e;
    IF oe IS NULL OR se IS NULL OR re IS NULL THEN RAISE EXCEPTION 'the entries of an epoch lineage row exist'; END IF;
    IF se.reverses IS DISTINCT FROM NEW.c_ea245623 THEN RAISE EXCEPTION 'the storno entry of a lineage row takes back its original entry'; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_3bd31c9d m WHERE m.c_317cffcf = se.op AND m.c_e909f2d4 = NEW.c_dcb2e603 AND m.c_1977183c = 1
                   AND m.c_6968655e = oe.op)
       OR NOT EXISTS (SELECT 1 FROM public.t_3bd31c9d m WHERE m.c_317cffcf = re.op AND m.c_e909f2d4 = NEW.c_dcb2e603 AND m.c_1977183c = 3
                      AND m.c_6968655e = oe.op) THEN
        RAISE EXCEPTION 'the storno and rebook entries of a lineage row belong to the storno and rebook members of the same execution and original operation';
    END IF;
    -- X2: the rebooked entry repeats its original on the same position, with the original date, kind, direction and quantity (only the amount is replayed)
    IF re.pos <> oe.pos OR re.kind <> oe.kind OR re.dir <> oe.dir OR re.d <> oe.d OR re.qty <> oe.qty OR re.reverses IS NOT NULL THEN
        RAISE EXCEPTION 'a rebook entry repeats its original entry on the same position and date';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_9214a347 BEFORE INSERT ON t_c38ba586 FOR EACH ROW EXECUTE FUNCTION f_4ae787a0();
CREATE TRIGGER r_c84ed064 BEFORE UPDATE OR DELETE ON t_c38ba586 FOR EACH ROW EXECUTE FUNCTION f_9580f5d3();
CREATE TRIGGER r_4dd7536d BEFORE TRUNCATE ON t_c38ba586 FOR EACH STATEMENT EXECUTE FUNCTION f_9580f5d3();

-- ---------------------------------------------------------------------------------------------------------------- the entry-level guard (design 3.3(1), (2), (2a), (3)); a NEW function and a NEW trigger
-- The position row is read FOR UPDATE: the costing engine already holds that lock when it appends an entry, so no new lock order arises. An ordinary entry takes no other lock here.
-- The position still holds the date of its previous entry at this point (the engine appends the entry first and moves the position afterwards).
CREATE FUNCTION f_71306314() RETURNS trigger LANGUAGE plpgsql AS $$
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
    SELECT c_3380c61e AS item, c_f0a91938 AS warehouse, c_3c880b93 AS last_date, c_ad2af2e5 AS last_seq INTO ps
      FROM public.t_a2903683 WHERE c_6b4abdd9 = NEW.c_98321e7f FOR UPDATE;
    IF NOT FOUND THEN RETURN NEW; END IF;   -- no such position: the foreign key of the entry answers, as before
    -- review M2 (a): an entry takes the next sequence number of its position (the engine's own invariant; the position is locked, so the number is stable). Together with the deferred check of the entry
    -- (below) this ties the position's last date, which the F1 comparison relies on, to the entries themselves instead of to a value the writer alone maintains.
    IF NEW.c_2affbce9 <> ps.last_seq + 1 THEN RAISE EXCEPTION 'seq_not_next: a cost entry takes the next sequence number of its position'; END IF;
    SELECT c_1977183c AS role, c_e909f2d4 AS execution, c_6968655e AS original INTO m FROM public.t_3bd31c9d WHERE c_317cffcf = NEW.c_eb34b7d8;
    v_member := FOUND;
    IF v_member THEN
        -- every entry of an epoch member, back-dated or not, is written inside the execution of its approved plan, in this transaction
        v_plan := f_4f422808(m.execution);
        IF v_plan IS NULL THEN RAISE EXCEPTION 'an entry of an epoch operation is written only inside the execution, in this transaction, of its approved compensation plan'; END IF;
        -- (2a) closure binding: the position of the entry (the position row decides, not the copied columns) is in the plan's approved closure
        IF NEW.c_9ee52677 <> ps.item OR NEW.c_0f65c03c <> ps.warehouse THEN RAISE EXCEPTION 'an entry of an epoch operation names the item and warehouse of its position'; END IF;
        IF NOT EXISTS (SELECT 1 FROM public.t_ce42c8d9 c WHERE c.c_7b502b96 = v_plan AND c.c_838708e4 = ps.item
                       AND c.c_f01763cf = ps.warehouse) THEN
            RAISE EXCEPTION 'an entry of an epoch operation lies on a position of the approved closure of its plan';
        END IF;
        -- (3) X2: an Open fiscal period, read FOR SHARE so that a concurrent close cannot commit between this check and the commit (lock order: positions, then periods)
        SELECT c_0a0b7f19 INTO v_period FROM public.t_ae996edc
          WHERE c_8299fb3d <= NEW.c_12665ffb AND c_c0322bcf >= NEW.c_12665ffb FOR SHARE;
        IF NOT FOUND OR v_period <> 1 THEN RAISE EXCEPTION 'period_not_open: an entry of an epoch operation is dated in an Open fiscal period'; END IF;
        -- (3) no open negative-stock obligation on the position
        IF f_cf64ca57(ps.item, ps.warehouse) THEN RAISE EXCEPTION 'an epoch never touches a position with an open negative-stock obligation'; END IF;
        -- (3) X5: the position is not in the closure of another executed plan
        IF EXISTS (SELECT 1 FROM public.t_ce42c8d9 c JOIN public.t_5b1a018a x ON x.c_934901c6 = c.c_7b502b96
                   WHERE c.c_838708e4 = ps.item AND c.c_f01763cf = ps.warehouse AND c.c_7b502b96 <> v_plan) THEN
            RAISE EXCEPTION 'plan_on_epoch_position: the position was already rewritten by another executed plan';
        END IF;
    END IF;
    -- (1) F1: a back-dated entry is accepted only for an epoch member (whose execution, plan and approval were verified above)
    -- review M2 (residual): the position's date is also a value the writer maintains, so an ordinary entry is compared with the date of the position's PREVIOUS ENTRY as well (seq = last_seq, a one-row lookup on the
    -- unique (position, sequence) index); a position moved without the entry's date can no longer hide a later date
    IF NOT v_member THEN
        SELECT c_12665ffb INTO v_prev FROM public.t_bc727c1d WHERE c_98321e7f = NEW.c_98321e7f AND c_2affbce9 = ps.last_seq;
        IF FOUND AND (ps.last_date IS NULL OR v_prev > ps.last_date) THEN ps.last_date := v_prev; END IF;
    END IF;
    IF ps.last_date IS NOT NULL AND NEW.c_12665ffb < ps.last_date AND NOT v_member THEN
        RAISE EXCEPTION 'a back-dated cost entry is accepted only inside the execution of an approved compensation plan';
    END IF;
    -- (2) an entry-level reversal link outside a reversal operation belongs to an epoch storno
    IF NEW.c_4f9e777a IS NOT NULL THEN
        SELECT c_7f2ea712, c_03a68cea INTO v_kind, v_revop FROM public.t_a648ca40 WHERE c_f47f15da = NEW.c_eb34b7d8;
        v_op := FOUND;
        IF v_op AND v_kind = 4 THEN
            -- review M1 (X6 at entry level): a reversal entry takes back an entry of the operation that its reversal operation names (the engine always writes it so), and never an entry of an epoch member
            -- (the epoch's own storno path is the branch below, outside a reversal operation)
            SELECT c_eb34b7d8 INTO v_revent FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_4f9e777a;
            IF FOUND THEN
                IF v_revent IS DISTINCT FROM v_revop THEN RAISE EXCEPTION 'a reversal entry takes back an entry of the operation that its reversal operation names'; END IF;
                IF EXISTS (SELECT 1 FROM public.t_3bd31c9d em WHERE em.c_317cffcf = v_revent) THEN
                    RAISE EXCEPTION 'operation_rewritten_by_epoch: an entry of an epoch operation is never taken back by a reversal';
                END IF;
            END IF;
        END IF;
        IF v_op AND v_kind <> 4 THEN
            IF NOT v_member OR m.role <> 1 THEN RAISE EXCEPTION 'outside a reversal operation only an epoch storno takes back an entry'; END IF;
            SELECT c_eb34b7d8 AS op, c_98321e7f AS pos, c_4105796c AS kind, c_e0cd1a14 AS dir, c_12665ffb AS d, c_dba27822 AS qty,
                   c_ece66ed5 AS amount INTO r FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_4f9e777a;
            IF FOUND THEN
                IF r.pos <> NEW.c_98321e7f THEN RAISE EXCEPTION 'a storno entry takes back an entry of its own position'; END IF;
                IF r.op IS DISTINCT FROM m.original THEN RAISE EXCEPTION 'a storno entry takes back an entry of the original operation of its member'; END IF;
                IF r.kind <> NEW.c_4105796c OR r.dir <> -NEW.c_e0cd1a14 OR r.d <> NEW.c_12665ffb OR r.qty <> NEW.c_dba27822 OR r.amount <> NEW.c_ece66ed5 THEN
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
CREATE TRIGGER r_ec35eb66 BEFORE INSERT ON t_bc727c1d FOR EACH ROW EXECUTE FUNCTION f_71306314();

-- review M2 (b): at commit, the position of every entry written in the transaction has been moved at least to that entry (sequence number and last date). The engine moves the position with every entry and the executor
-- keeps the date with GREATEST, so neither path changes; a writer that appends an entry without moving its position, or moves it without the entry's date, can no longer leave the F1 anchor behind.
CREATE FUNCTION f_7831ff06() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_a2903683 ps WHERE ps.c_6b4abdd9 = NEW.c_98321e7f AND ps.c_ad2af2e5 >= NEW.c_2affbce9
                   AND ps.c_3c880b93 >= NEW.c_12665ffb) THEN
        RAISE EXCEPTION 'position_not_moved: a cost entry is committed together with the move of its position to at least its sequence number and its date';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_9706efe9 AFTER INSERT ON t_bc727c1d DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_7831ff06();

-- ---------------------------------------------------------------------------------------------------------------- X6 (design 3.3(4)): no reversal operation of an epoch member or of an operation with stornoed entries
CREATE FUNCTION f_7e9e9c5e() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_03a68cea IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM public.t_3bd31c9d m WHERE m.c_317cffcf = NEW.c_03a68cea)
           OR EXISTS (SELECT 1 FROM public.t_c38ba586 l JOIN public.t_bc727c1d e ON e.c_ed61ae1a = l.c_ea245623
                      WHERE e.c_eb34b7d8 = NEW.c_03a68cea) THEN
            RAISE EXCEPTION 'operation_rewritten_by_epoch: an operation rewritten by a replay epoch, or belonging to one, is never reversed';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_f8ff58da BEFORE INSERT ON t_a648ca40 FOR EACH ROW EXECUTE FUNCTION f_7e9e9c5e();

-- ---------------------------------------------------------------------------------------------------------------- X7: a plan-bound stock document is posted only inside the execution of its plan
CREATE FUNCTION f_83fecb4e() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
BEGIN
    SELECT c_57ecf0f3 INTO v_plan FROM public.t_d29d5b43 WHERE c_8a883ba7 = NEW.c_eb889fd0 AND c_b38a0440 IN (1, 2);
    IF FOUND AND NOT EXISTS (SELECT 1 FROM public.t_5b1a018a x WHERE x.c_934901c6 = v_plan AND x.c_fbd0bc4b = pg_current_xact_id()
                             AND x.c_07602789 = NEW.c_fc71418e) THEN
        RAISE EXCEPTION 'document_bound_to_plan: a stock document bound to a compensation plan is posted only by the execution of that plan';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_43bec038 BEFORE INSERT ON t_4d4b251f FOR EACH ROW EXECUTE FUNCTION f_83fecb4e();

-- ---------------------------------------------------------------------------------------------------------------- deferred checks, judged at commit (design 3.4)
CREATE FUNCTION f_a28a6a70() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_members bigint;
    v_events bigint;
BEGIN
    SELECT c_b38a0440 AS status, c_2a7148b0 AS executed_xact, c_f00fd9d5 AS closure_hash, c_8a883ba7 AS document INTO p
      FROM public.t_d29d5b43 WHERE c_57ecf0f3 = NEW.c_934901c6;
    -- the plan is executed, and was marked so in the transaction that wrote the execution (this one)
    IF p.status IS DISTINCT FROM 3 OR p.executed_xact IS DISTINCT FROM NEW.c_fbd0bc4b OR NEW.c_fbd0bc4b <> pg_current_xact_id() THEN
        RAISE EXCEPTION 'an execution is committed together with the executed status of its plan';
    END IF;
    IF f_724b73d6(NEW.c_934901c6) <> p.closure_hash THEN RAISE EXCEPTION 'the closure of the plan does not match its closure hash'; END IF;
    SELECT count(*), count(*) FILTER (WHERE c_1977183c = 2) INTO v_members, v_events FROM public.t_3bd31c9d WHERE c_e909f2d4 = NEW.c_02771c43;
    IF v_members = 0 OR v_events <> 1 THEN RAISE EXCEPTION 'an execution has members and exactly one event'; END IF;
    -- the event member is the cost operation of the execution's stock posting, and that posting is the posting of the plan's document
    IF NOT EXISTS (SELECT 1 FROM public.t_3bd31c9d m JOIN public.t_4d4b251f sp ON sp.c_4ce1587f = m.c_317cffcf
                   WHERE m.c_e909f2d4 = NEW.c_02771c43 AND m.c_1977183c = 2 AND sp.c_fc71418e = NEW.c_07602789
                     AND sp.c_eb889fd0 = p.document) THEN
        RAISE EXCEPTION 'the event of an execution is the posting of the document of its plan';
    END IF;
    -- every storno member has its rebook member for the same original operation, and the reverse (each is unique per original by the index)
    IF EXISTS (SELECT 1 FROM public.t_3bd31c9d s WHERE s.c_e909f2d4 = NEW.c_02771c43 AND s.c_1977183c IN (1, 3)
               AND NOT EXISTS (SELECT 1 FROM public.t_3bd31c9d t WHERE t.c_e909f2d4 = s.c_e909f2d4 AND t.c_1977183c = 4 - s.c_1977183c
                               AND t.c_6968655e = s.c_6968655e)) THEN
        RAISE EXCEPTION 'every storno member has its rebook member for the same original operation';
    END IF;
    IF EXISTS (SELECT 1 FROM public.t_3bd31c9d m WHERE m.c_e909f2d4 = NEW.c_02771c43
               AND NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e WHERE e.c_eb34b7d8 = m.c_317cffcf)) THEN
        RAISE EXCEPTION 'every epoch member has at least one entry';
    END IF;
    -- every storno entry and every rebook entry has its lineage row
    IF EXISTS (SELECT 1 FROM public.t_3bd31c9d m JOIN public.t_bc727c1d e ON e.c_eb34b7d8 = m.c_317cffcf
               WHERE m.c_e909f2d4 = NEW.c_02771c43
                 AND ((m.c_1977183c = 1 AND NOT EXISTS (SELECT 1 FROM public.t_c38ba586 l WHERE l.c_d49e9cf0 = e.c_ed61ae1a AND l.c_dcb2e603 = m.c_e909f2d4))
                   OR (m.c_1977183c = 3 AND NOT EXISTS (SELECT 1 FROM public.t_c38ba586 l WHERE l.c_1a01c03e = e.c_ed61ae1a AND l.c_dcb2e603 = m.c_e909f2d4)))) THEN
        RAISE EXCEPTION 'every storno and rebook entry has its lineage row';
    END IF;
    -- the business audit event of the execution: of the plan-executed type, with this plan as subject and this execution as target (0002 is not altered)
    IF NOT EXISTS (SELECT 1 FROM public.t_2db8cf1f a WHERE a.c_ef116a9d = NEW.c_f462f8b9 AND a.c_4c6c6496 = 1154
                   AND a.c_629e971c = NEW.c_934901c6 AND a.c_deb363a5 = NEW.c_02771c43) THEN
        RAISE EXCEPTION 'an execution is committed together with its business audit event';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_cb401ad1 AFTER INSERT ON t_5b1a018a DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_a28a6a70();

-- orphan detection: no member without an execution of this transaction, and no member without entries
CREATE FUNCTION f_245cfe63() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_5b1a018a x WHERE x.c_02771c43 = NEW.c_e909f2d4 AND x.c_fbd0bc4b = pg_current_xact_id()) THEN
        RAISE EXCEPTION 'an epoch member is committed together with its execution';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e WHERE e.c_eb34b7d8 = NEW.c_317cffcf) THEN RAISE EXCEPTION 'every epoch member has at least one entry'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_885d49c2 AFTER INSERT ON t_3bd31c9d DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_245cfe63();
