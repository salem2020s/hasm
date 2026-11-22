-- Migration 0034: plan storage, approval and atomic execution of a compensation plan, database layer (Phase 3.8D step 4; owner decision X1-A of 2026-11-21 with mandatory safeguards;
-- design docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md revision 3, confirmed by the independent review docs/review-records/PR-48-plan-execution-design-rev3.md). PROVISIONAL NUMBER: it follows the merge order.
-- Forward-only and ADDITIVE: new tables, new functions and new triggers only. Nothing existing is removed, replaced, altered, paused or relaxed: no existing function is redefined, no existing trigger is touched,
-- no existing constraint is changed, nothing is seeded and no existing row is read or modified. Opaque physical names (D-029) generated deterministically by tools/schema/company-0034-plan-execution.cjs
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
CREATE TABLE t_18aad15c (
    c_72ad935c uuid NOT NULL,
    c_a883233c uuid NOT NULL,
    c_f752e115 uuid NOT NULL,
    c_9019de55 uuid NOT NULL,
    c_e6eb39d9 bigint NOT NULL,
    c_3b330bff text NOT NULL,
    c_9160bf95 char(64) NOT NULL,
    c_ac8fa31e char(64) NOT NULL,
    c_66fa1129 char(64) NOT NULL,
    c_ff615883 char(64) NOT NULL,
    c_621f0a2d char(64) NOT NULL,
    c_9d5be335 char(64) NOT NULL,
    c_c832204c char(64) NOT NULL,
    c_cac31997 char(64) NOT NULL,
    c_c376fde2 smallint NOT NULL,
    c_31f8fb50 bigint NOT NULL,
    c_289fa56f uuid NOT NULL,
    c_99e2e637 timestamptz NOT NULL,
    c_39194310 xid8 NOT NULL,
    c_8f7fe2d6 uuid NULL,
    c_a2ed1389 uuid NULL,
    c_417bae10 timestamptz NULL,
    c_fb20b8da xid8 NULL,
    c_9f792877 timestamptz NOT NULL,
    CONSTRAINT k_714d2555 PRIMARY KEY (c_72ad935c),
    CONSTRAINT k_0d6e74e4 FOREIGN KEY (c_a883233c) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_3812d04e FOREIGN KEY (c_f752e115) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_6aef7ac5 FOREIGN KEY (c_9019de55) REFERENCES t_e92d29e5 (c_9a59d13e),
    CONSTRAINT k_9d460577 CHECK (c_c376fde2 IN (1, 2, 3, 4, 5, 6) AND c_31f8fb50 > 0 AND c_e6eb39d9 > 0
        AND char_length(c_3b330bff) >= 2 AND c_cac31997 ~ '^[0-9a-f]{64}$'
        AND c_9160bf95 ~ '^[0-9a-f]{64}$' AND c_ac8fa31e ~ '^[0-9a-f]{64}$' AND c_66fa1129 ~ '^[0-9a-f]{64}$' AND c_ff615883 ~ '^[0-9a-f]{64}$' AND c_621f0a2d ~ '^[0-9a-f]{64}$' AND c_9d5be335 ~ '^[0-9a-f]{64}$' AND c_c832204c ~ '^[0-9a-f]{64}$'),
    CONSTRAINT k_bccb857d CHECK (
        (c_8f7fe2d6 IS NULL) = (c_a2ed1389 IS NULL) AND (c_8f7fe2d6 IS NULL) = (c_417bae10 IS NULL)
        AND (c_c376fde2 NOT IN (2, 3) OR c_8f7fe2d6 IS NOT NULL) AND (c_c376fde2 NOT IN (1, 4) OR c_8f7fe2d6 IS NULL)
        AND (c_a2ed1389 IS NULL OR c_a2ed1389 <> c_289fa56f)
        AND (c_c376fde2 = 3) = (c_fb20b8da IS NOT NULL))
);
-- X7: a stock document is bound to at most one live (proposed or approved) plan
CREATE UNIQUE INDEX k_8b081ae7 ON t_18aad15c (c_9019de55) WHERE c_c376fde2 IN (1, 2);
REVOKE ALL ON TABLE t_18aad15c FROM PUBLIC;

-- The plan's closure: every (item, warehouse) position the replay may touch (the roots plus every position reached through transfers).
CREATE TABLE t_832b8c7b (
    c_546fe0a8 uuid NOT NULL,
    c_42b33ca1 uuid NOT NULL,
    c_36c90205 uuid NOT NULL,
    CONSTRAINT k_f879133d PRIMARY KEY (c_546fe0a8, c_42b33ca1, c_36c90205),
    CONSTRAINT k_dd8c2f16 FOREIGN KEY (c_546fe0a8) REFERENCES t_18aad15c (c_72ad935c),
    CONSTRAINT k_cfea5d39 FOREIGN KEY (c_42b33ca1) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_0f6d79ec FOREIGN KEY (c_36c90205) REFERENCES t_d2006f04 (c_a9f73cc2)
);
CREATE INDEX k_0a5da24b ON t_832b8c7b (c_42b33ca1, c_36c90205);
REVOKE ALL ON TABLE t_832b8c7b FROM PUBLIC;

-- The compensation lines the approver sees (per affected posting, period, branch and account).
CREATE TABLE t_876e2748 (
    c_75545c59 uuid NOT NULL,
    c_48effd51 integer NOT NULL,
    c_7b9d9803 uuid NULL,
    c_5b59e3da date NOT NULL,
    c_e79277ad uuid NOT NULL,
    c_288c450a uuid NOT NULL,
    c_a8848283 uuid NOT NULL,
    c_8ddae53a numeric(28,6) NOT NULL,
    c_a5175848 numeric(28,6) NOT NULL,
    CONSTRAINT k_6f62d3c0 PRIMARY KEY (c_75545c59, c_48effd51),
    CONSTRAINT k_7de2e220 FOREIGN KEY (c_75545c59) REFERENCES t_18aad15c (c_72ad935c),
    CONSTRAINT k_68ed9ef9 FOREIGN KEY (c_7b9d9803) REFERENCES t_4d4b251f (c_fc71418e),
    CONSTRAINT k_b138c33e FOREIGN KEY (c_e79277ad) REFERENCES t_ae996edc (c_d61cbb9c),
    CONSTRAINT k_647ddd50 FOREIGN KEY (c_288c450a) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_5b9a0fc6 FOREIGN KEY (c_a8848283) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_81c083ed CHECK (c_48effd51 > 0 AND c_8ddae53a >= 0 AND c_a5175848 >= 0
        AND (c_8ddae53a > 0) <> (c_a5175848 > 0))
);
REVOKE ALL ON TABLE t_876e2748 FROM PUBLIC;

-- The plan's history (append-only).
CREATE TABLE t_25c91f9f (
    c_31a28504 uuid NOT NULL,
    c_c852b313 uuid NOT NULL,
    c_0da4f0eb smallint NOT NULL,
    c_bb87c3a4 uuid NOT NULL,
    c_5967fe63 timestamptz NOT NULL,
    c_718cef5f varchar(500) NULL,
    CONSTRAINT k_8c393590 PRIMARY KEY (c_31a28504),
    CONSTRAINT k_b84762c1 FOREIGN KEY (c_c852b313) REFERENCES t_18aad15c (c_72ad935c),
    CONSTRAINT k_3b770926 CHECK (c_0da4f0eb IN (1, 2, 3, 4, 5, 6))
);
CREATE INDEX k_c436f8b8 ON t_25c91f9f (c_c852b313);
REVOKE ALL ON TABLE t_25c91f9f FROM PUBLIC;

-- The approval: bound to the plan revision and to every fingerprint of the plan (the closure hash included).
CREATE TABLE t_4ce74e45 (
    c_d469eef1 uuid NOT NULL,
    c_2c50a46b uuid NOT NULL,
    c_f6e9b151 bigint NOT NULL,
    c_1007fc6d char(64) NOT NULL,
    c_63bee9aa char(64) NOT NULL,
    c_1ba81b5f char(64) NOT NULL,
    c_d4657d62 char(64) NOT NULL,
    c_1ef20911 char(64) NOT NULL,
    c_bc7d76a4 char(64) NOT NULL,
    c_7ae25317 char(64) NOT NULL,
    c_fb1565b3 char(64) NOT NULL,
    c_66daa96a uuid NOT NULL,
    c_55b3d9d6 timestamptz NOT NULL,
    c_44216426 varchar(500) NULL,
    CONSTRAINT k_1d17c590 PRIMARY KEY (c_d469eef1),
    CONSTRAINT k_bfa3dbe6 FOREIGN KEY (c_2c50a46b) REFERENCES t_18aad15c (c_72ad935c),
    CONSTRAINT k_a49dad99 UNIQUE (c_2c50a46b, c_f6e9b151)
);
REVOKE ALL ON TABLE t_4ce74e45 FROM PUBLIC;

-- The single execution of a plan. The posting of the event and the audit event are named when the row is written (the row is append-only); both must exist at commit.
CREATE TABLE t_3ed621fb (
    c_c590e149 uuid NOT NULL,
    c_270dd1de uuid NOT NULL,
    c_ff2a9beb uuid NOT NULL,
    c_ae6ab815 uuid NOT NULL,
    c_0754b48c uuid NOT NULL,
    c_d27a82a4 timestamptz NOT NULL,
    c_707246ad xid8 NOT NULL,
    CONSTRAINT k_2eccc6ca PRIMARY KEY (c_c590e149),
    CONSTRAINT k_832881c7 FOREIGN KEY (c_270dd1de) REFERENCES t_18aad15c (c_72ad935c),
    CONSTRAINT k_3c5ea211 UNIQUE (c_270dd1de),
    CONSTRAINT k_994a7b53 UNIQUE (c_ae6ab815),
    CONSTRAINT k_31841e92 UNIQUE (c_ff2a9beb),
    CONSTRAINT k_05f053b7 FOREIGN KEY (c_ff2a9beb) REFERENCES t_4d4b251f (c_fc71418e) DEFERRABLE INITIALLY DEFERRED
);
REVOKE ALL ON TABLE t_3ed621fb FROM PUBLIC;

-- The compensation journals of an execution, one per bucket (the journal source key is plan + bucket, so a repeat cannot create a second set).
CREATE TABLE t_6847b3e3 (
    c_39484614 uuid NOT NULL,
    c_45976c53 integer NOT NULL,
    c_5d595f8f uuid NOT NULL,
    CONSTRAINT k_b9ebac69 PRIMARY KEY (c_39484614, c_45976c53),
    CONSTRAINT k_b8bc4430 FOREIGN KEY (c_39484614) REFERENCES t_3ed621fb (c_c590e149),
    CONSTRAINT k_7b277b53 FOREIGN KEY (c_5d595f8f) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_24d466f9 UNIQUE (c_5d595f8f),
    CONSTRAINT k_e7795cd8 CHECK (c_45976c53 > 0)
);
REVOKE ALL ON TABLE t_6847b3e3 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- epoch membership and lineage
-- An epoch operation keeps its NATURAL kind (the kind CHECK of 0014 is not widened); its meaning comes from this side table (the pattern of 0020, DB-1).
CREATE TABLE t_5aef6fe2 (
    c_cb9cfcc6 uuid NOT NULL,
    c_22781d6b uuid NOT NULL,
    c_20202c1f smallint NOT NULL,
    c_91c5049c uuid NULL,
    CONSTRAINT k_d335101f PRIMARY KEY (c_cb9cfcc6),
    CONSTRAINT k_1aaee503 FOREIGN KEY (c_cb9cfcc6) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_681f5abd FOREIGN KEY (c_22781d6b) REFERENCES t_3ed621fb (c_c590e149),
    CONSTRAINT k_fbe74af3 FOREIGN KEY (c_91c5049c) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_65d8f9c2 CHECK (c_20202c1f IN (1, 2, 3) AND (c_20202c1f = 2) = (c_91c5049c IS NULL)
        AND c_91c5049c IS DISTINCT FROM c_cb9cfcc6)
);
-- an original operation is stornoed once and rebooked once, ever; an execution has one event
CREATE UNIQUE INDEX k_8dc04621 ON t_5aef6fe2 (c_91c5049c, c_20202c1f) WHERE c_91c5049c IS NOT NULL;
CREATE UNIQUE INDEX k_d11c293d ON t_5aef6fe2 (c_22781d6b) WHERE c_20202c1f = 2;
CREATE INDEX k_c155a298 ON t_5aef6fe2 (c_22781d6b);
REVOKE ALL ON TABLE t_5aef6fe2 FROM PUBLIC;

CREATE TABLE t_07854767 (
    c_c81d75a1 bigint NOT NULL,
    c_d3b54ab3 bigint NOT NULL,
    c_6b1a5275 bigint NOT NULL,
    c_fdc21bcc uuid NOT NULL,
    CONSTRAINT k_5faa7f66 PRIMARY KEY (c_c81d75a1),
    CONSTRAINT k_ac70962f UNIQUE (c_d3b54ab3),
    CONSTRAINT k_7172cca0 UNIQUE (c_6b1a5275),
    CONSTRAINT k_778e4a78 FOREIGN KEY (c_c81d75a1) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_0ba5d067 FOREIGN KEY (c_d3b54ab3) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_bb0c0ce2 FOREIGN KEY (c_6b1a5275) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_6f712793 FOREIGN KEY (c_fdc21bcc) REFERENCES t_3ed621fb (c_c590e149),
    CONSTRAINT k_688e241a CHECK (c_c81d75a1 <> c_d3b54ab3 AND c_c81d75a1 <> c_6b1a5275
        AND c_d3b54ab3 <> c_6b1a5275)
);
CREATE INDEX k_17e98211 ON t_07854767 (c_fdc21bcc);
REVOKE ALL ON TABLE t_07854767 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- shared functions (all read table rows only; none reads a setting)
-- The closure hash, byte-exact (review L1): each pair is the lowercase hyphenated text of the item uuid, a colon and the lowercase hyphenated text of the warehouse uuid; the pairs are sorted by (item, warehouse)
-- in uuid order (the ORDER BY is on the uuid columns, never on text, so no collation is involved), joined by ONE newline without a trailing newline, encoded in UTF-8; the result is the lowercase hex SHA-256.
-- An empty closure hashes the empty string. PostgreSQL is the only place that computes it.
CREATE FUNCTION f_3cdca6ba(p_plan uuid) RETURNS char(64) LANGUAGE sql AS $$
    SELECT encode(sha256(convert_to(COALESCE(string_agg(lower(c.c_42b33ca1::text) || ':' || lower(c.c_36c90205::text), E'\n'
        ORDER BY c.c_42b33ca1, c.c_36c90205), ''), 'UTF8')), 'hex')::char(64)
    FROM public.t_832b8c7b c WHERE c.c_546fe0a8 = p_plan
$$;

-- X3: true when the user is independent of the plan: not its creator, and not the creator, an editor or the submitter of the event's stock document (every editor counts, which contains the last editor).
CREATE FUNCTION f_17b9c93f(p_plan uuid, p_user uuid) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE
    p record;
BEGIN
    SELECT c_289fa56f AS created_by, c_9019de55 AS document INTO p FROM public.t_18aad15c WHERE c_72ad935c = p_plan;
    IF NOT FOUND OR p_user IS NULL THEN RETURN false; END IF;
    IF p_user = p.created_by THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_25c91f9f e WHERE e.c_c852b313 = p_plan AND e.c_0da4f0eb = 1 AND e.c_bb87c3a4 = p_user) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_e92d29e5 d WHERE d.c_9a59d13e = p.document AND (d.c_6b6eb86b = p_user OR d.c_afb8d9de = p_user)) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_b9231a6b e WHERE e.c_fd72fc35 = p.document AND e.c_2fe7652c IN (1, 2, 3) AND e.c_2a6bae0c = p_user) THEN RETURN false; END IF;
    RETURN true;
END;
$$;

-- NULL when the approval is a sound approval of the plan; otherwise the reason. Sound: it belongs to the plan, repeats every fingerprint of the plan, the closure rows still hash to the plan's closure hash, the
-- approver is independent (X3), and the event's stock document is approved at the planned revision (X7).
CREATE FUNCTION f_aef94638(p_plan uuid, p_approval uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    p public.t_18aad15c%ROWTYPE;
    a public.t_4ce74e45%ROWTYPE;
BEGIN
    SELECT * INTO p FROM public.t_18aad15c WHERE c_72ad935c = p_plan;
    IF NOT FOUND THEN RETURN 'plan_not_found'; END IF;
    SELECT * INTO a FROM public.t_4ce74e45 WHERE c_d469eef1 = p_approval AND c_2c50a46b = p_plan;
    IF NOT FOUND THEN RETURN 'approval_not_found'; END IF;
    IF a.c_1007fc6d <> p.c_9160bf95 OR a.c_63bee9aa <> p.c_ac8fa31e OR a.c_1ba81b5f <> p.c_66fa1129 OR a.c_d4657d62 <> p.c_ff615883 OR a.c_1ef20911 <> p.c_621f0a2d OR a.c_bc7d76a4 <> p.c_9d5be335 OR a.c_7ae25317 <> p.c_c832204c OR a.c_fb1565b3 <> p.c_cac31997 THEN RETURN 'approval_fingerprint_mismatch'; END IF;
    IF f_3cdca6ba(p_plan) <> p.c_cac31997 THEN RETURN 'closure_hash_mismatch'; END IF;
    IF NOT f_17b9c93f(p_plan, a.c_66daa96a) THEN RETURN 'approver_not_independent'; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_e92d29e5 d WHERE d.c_9a59d13e = p.c_9019de55 AND d.c_3fc0f063 = 3 AND d.c_9ba641c8 = p.c_e6eb39d9) THEN
        RETURN 'document_not_approved';
    END IF;
    -- review L1: a document that already has a stock posting is never executed by a plan (a posting is unique per document, so such a plan could never execute); this is read at approval and again at execution
    IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = p.c_9019de55) THEN RETURN 'document_already_posted'; END IF;
    RETURN NULL;
END;
$$;

-- The plan of an execution that is LIVE: written in the current top-level transaction (xid8, correct inside savepoints), of a plan that is approved, by an approver who is not its creator, with an approval whose
-- fingerprints equal the plan's. NULL otherwise. This is the same-transaction binding of every epoch row; it reads rows only.
CREATE FUNCTION f_126b872f(p_execution uuid) RETURNS uuid LANGUAGE sql AS $$
    SELECT p.c_72ad935c
    FROM public.t_3ed621fb x
    JOIN public.t_18aad15c p ON p.c_72ad935c = x.c_270dd1de
    JOIN public.t_4ce74e45 a ON a.c_d469eef1 = p.c_8f7fe2d6 AND a.c_2c50a46b = p.c_72ad935c
    WHERE x.c_c590e149 = p_execution AND x.c_707246ad = pg_current_xact_id() AND p.c_c376fde2 = 2
      AND a.c_66daa96a = p.c_a2ed1389 AND a.c_66daa96a <> p.c_289fa56f
      AND NOT (a.c_1007fc6d <> p.c_9160bf95 OR a.c_63bee9aa <> p.c_ac8fa31e OR a.c_1ba81b5f <> p.c_66fa1129 OR a.c_d4657d62 <> p.c_ff615883 OR a.c_1ef20911 <> p.c_621f0a2d OR a.c_bc7d76a4 <> p.c_9d5be335 OR a.c_7ae25317 <> p.c_c832204c) AND a.c_fb1565b3 = p.c_cac31997
$$;

-- Append-only guard shared by every new table except the plan.
CREATE FUNCTION f_6ee32fd0() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'compensation plan and epoch records are append-only';
END;
$$;

-- ---------------------------------------------------------------------------------------------------------------- the plan: immutability, status transitions, closure hash
CREATE FUNCTION f_3340741b() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_reason text;
BEGIN
    IF TG_LEVEL = 'STATEMENT' OR TG_OP = 'DELETE' THEN RAISE EXCEPTION 'compensation plans are never removed'; END IF;
    IF TG_OP = 'INSERT' THEN
        -- never client-supplied: the creating transaction and the closure hash (of the closure written so far: none yet)
        NEW.c_39194310 := pg_current_xact_id();
        NEW.c_cac31997 := f_3cdca6ba(NEW.c_72ad935c);
        IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = NEW.c_9019de55) THEN RAISE EXCEPTION 'document_already_posted: a plan is not written for a document that already has a stock posting'; END IF;
        IF NEW.c_c376fde2 <> 1 OR NEW.c_31f8fb50 <> 1 OR NEW.c_8f7fe2d6 IS NOT NULL OR NEW.c_a2ed1389 IS NOT NULL
           OR NEW.c_417bae10 IS NOT NULL OR NEW.c_fb20b8da IS NOT NULL THEN
            RAISE EXCEPTION 'a compensation plan starts as proposed, at revision 1';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_72ad935c IS DISTINCT FROM OLD.c_72ad935c OR NEW.c_a883233c IS DISTINCT FROM OLD.c_a883233c
       OR NEW.c_f752e115 IS DISTINCT FROM OLD.c_f752e115 OR NEW.c_9019de55 IS DISTINCT FROM OLD.c_9019de55
       OR NEW.c_e6eb39d9 IS DISTINCT FROM OLD.c_e6eb39d9 OR NEW.c_3b330bff IS DISTINCT FROM OLD.c_3b330bff
       OR NEW.c_9160bf95 IS DISTINCT FROM OLD.c_9160bf95 OR NEW.c_ac8fa31e IS DISTINCT FROM OLD.c_ac8fa31e OR NEW.c_66fa1129 IS DISTINCT FROM OLD.c_66fa1129 OR NEW.c_ff615883 IS DISTINCT FROM OLD.c_ff615883 OR NEW.c_621f0a2d IS DISTINCT FROM OLD.c_621f0a2d OR NEW.c_9d5be335 IS DISTINCT FROM OLD.c_9d5be335 OR NEW.c_c832204c IS DISTINCT FROM OLD.c_c832204c
       OR NEW.c_289fa56f IS DISTINCT FROM OLD.c_289fa56f OR NEW.c_99e2e637 IS DISTINCT FROM OLD.c_99e2e637
       OR NEW.c_39194310 IS DISTINCT FROM OLD.c_39194310 THEN
        RAISE EXCEPTION 'the payload, the fingerprints and the origin of a compensation plan are immutable';
    END IF;
    -- the closure hash follows the closure rows while they can still be written (a proposed plan in its creating transaction); afterwards it is frozen
    IF OLD.c_c376fde2 = 1 AND OLD.c_39194310 = pg_current_xact_id() THEN
        NEW.c_cac31997 := f_3cdca6ba(OLD.c_72ad935c);
    ELSIF NEW.c_cac31997 IS DISTINCT FROM OLD.c_cac31997 THEN
        RAISE EXCEPTION 'the closure hash of a compensation plan is immutable';
    END IF;
    IF NEW.c_c376fde2 = OLD.c_c376fde2 THEN
        IF NEW.c_31f8fb50 IS DISTINCT FROM OLD.c_31f8fb50 OR NEW.c_8f7fe2d6 IS DISTINCT FROM OLD.c_8f7fe2d6
           OR NEW.c_a2ed1389 IS DISTINCT FROM OLD.c_a2ed1389 OR NEW.c_417bae10 IS DISTINCT FROM OLD.c_417bae10
           OR NEW.c_fb20b8da IS DISTINCT FROM OLD.c_fb20b8da OR NEW.c_9f792877 IS DISTINCT FROM OLD.c_9f792877 THEN
            RAISE EXCEPTION 'a compensation plan changes only by a status transition';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_31f8fb50 <> OLD.c_31f8fb50 + 1 THEN RAISE EXCEPTION 'a status transition advances the plan revision by exactly one'; END IF;
    IF NOT ((OLD.c_c376fde2 = 1 AND NEW.c_c376fde2 IN (2, 4, 5, 6)) OR (OLD.c_c376fde2 = 2 AND NEW.c_c376fde2 IN (3, 5, 6))) THEN
        RAISE EXCEPTION 'this status transition of a compensation plan is not allowed';
    END IF;
    IF NEW.c_c376fde2 = 2 THEN
        IF NEW.c_8f7fe2d6 IS NULL OR NEW.c_a2ed1389 IS NULL OR NEW.c_417bae10 IS NULL THEN RAISE EXCEPTION 'an approved plan names its approval'; END IF;
        IF NOT EXISTS (SELECT 1 FROM public.t_4ce74e45 a WHERE a.c_d469eef1 = NEW.c_8f7fe2d6 AND a.c_2c50a46b = OLD.c_72ad935c
                       AND a.c_f6e9b151 = OLD.c_31f8fb50 AND a.c_66daa96a = NEW.c_a2ed1389) THEN
            RAISE EXCEPTION 'a plan is approved by the approval of its current revision';
        END IF;
        -- the approval re-checks the closure (hash recomputed from the closure rows), the fingerprints, the segregation of duties and the event document
        v_reason := f_aef94638(OLD.c_72ad935c, NEW.c_8f7fe2d6);
        IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    ELSIF NEW.c_8f7fe2d6 IS DISTINCT FROM OLD.c_8f7fe2d6 OR NEW.c_a2ed1389 IS DISTINCT FROM OLD.c_a2ed1389
          OR NEW.c_417bae10 IS DISTINCT FROM OLD.c_417bae10 THEN
        RAISE EXCEPTION 'the approval of a compensation plan is set once, by its approval';
    END IF;
    IF NEW.c_c376fde2 = 3 THEN
        IF NOT EXISTS (SELECT 1 FROM public.t_3ed621fb x WHERE x.c_270dd1de = OLD.c_72ad935c AND x.c_707246ad = pg_current_xact_id()) THEN
            RAISE EXCEPTION 'a plan is marked executed only in the transaction of its execution';
        END IF;
        IF f_3cdca6ba(OLD.c_72ad935c) <> OLD.c_cac31997 THEN RAISE EXCEPTION 'the closure of the plan no longer matches its closure hash'; END IF;
        NEW.c_fb20b8da := pg_current_xact_id();
    ELSIF NEW.c_fb20b8da IS NOT NULL THEN
        RAISE EXCEPTION 'only an executed plan carries its executing transaction';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_c8e8d52d BEFORE INSERT OR UPDATE OR DELETE ON t_18aad15c FOR EACH ROW EXECUTE FUNCTION f_3340741b();
CREATE TRIGGER r_9baa8ce8 BEFORE TRUNCATE ON t_18aad15c FOR EACH STATEMENT EXECUTE FUNCTION f_3340741b();

-- At commit (review L2): the plan and its closure were written together; the closure contains the plan's root position and hashes to the stored closure hash.
CREATE FUNCTION f_91c93908() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
BEGIN
    SELECT c_a883233c AS item, c_f752e115 AS warehouse, c_cac31997 AS closure_hash INTO p FROM public.t_18aad15c WHERE c_72ad935c = NEW.c_72ad935c;
    IF NOT EXISTS (SELECT 1 FROM public.t_832b8c7b c WHERE c.c_546fe0a8 = NEW.c_72ad935c AND c.c_42b33ca1 = p.item
                   AND c.c_36c90205 = p.warehouse) THEN
        RAISE EXCEPTION 'the closure of a compensation plan contains its root position and is written with the plan';
    END IF;
    IF f_3cdca6ba(NEW.c_72ad935c) <> p.closure_hash THEN RAISE EXCEPTION 'the closure of the plan does not match its closure hash'; END IF;
    -- review L1: re-checked at commit, so a posting committed after the insert-time check (read committed) is seen; under REPEATABLE READ the approval and the execution re-read it again
    IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = NEW.c_9019de55) THEN RAISE EXCEPTION 'document_already_posted: a plan is not written for a document that already has a stock posting'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_cce24262 AFTER INSERT ON t_18aad15c DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_91c93908();

-- Closure rows and lines: written only while the plan is proposed AND in the transaction that created the plan (review M3 and L2). A new closure therefore needs a new plan and a new approval.
CREATE FUNCTION f_9c0b8a85() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
BEGIN
    IF TG_TABLE_NAME = 't_832b8c7b' THEN v_plan := NEW.c_546fe0a8; ELSE v_plan := NEW.c_75545c59; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_18aad15c p WHERE p.c_72ad935c = v_plan AND p.c_c376fde2 = 1 AND p.c_39194310 = pg_current_xact_id()) THEN
        RAISE EXCEPTION 'the closure and the lines of a compensation plan are written with the plan, in its creating transaction';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_a883ecc3 BEFORE INSERT ON t_832b8c7b FOR EACH ROW EXECUTE FUNCTION f_9c0b8a85();
CREATE TRIGGER r_c7e4edb5 BEFORE INSERT ON t_876e2748 FOR EACH ROW EXECUTE FUNCTION f_9c0b8a85();
-- every closure row refreshes the plan's hash through the plan's own trigger (which computes it; the value written here is ignored)
CREATE FUNCTION f_54fba596() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    UPDATE public.t_18aad15c SET c_cac31997 = c_cac31997 WHERE c_72ad935c = NEW.c_546fe0a8;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_03a9145e AFTER INSERT ON t_832b8c7b FOR EACH ROW EXECUTE FUNCTION f_54fba596();
CREATE TRIGGER r_368b6647 BEFORE UPDATE OR DELETE ON t_832b8c7b FOR EACH ROW EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_802481ab BEFORE TRUNCATE ON t_832b8c7b FOR EACH STATEMENT EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_cb1f939b BEFORE UPDATE OR DELETE ON t_876e2748 FOR EACH ROW EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_ffa8b318 BEFORE TRUNCATE ON t_876e2748 FOR EACH STATEMENT EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_7623b450 BEFORE UPDATE OR DELETE ON t_25c91f9f FOR EACH ROW EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_aad183f4 BEFORE TRUNCATE ON t_25c91f9f FOR EACH STATEMENT EXECUTE FUNCTION f_6ee32fd0();

-- ---------------------------------------------------------------------------------------------------------------- the approval
CREATE FUNCTION f_5ac22af0() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_reason text;
BEGIN
    -- the plan row is locked first: approvals, executions and status changes of one plan are serialized for every writer
    SELECT c_c376fde2 AS status, c_31f8fb50 AS revision INTO p FROM public.t_18aad15c WHERE c_72ad935c = NEW.c_2c50a46b FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'the plan of an approval does not exist'; END IF;
    IF p.status <> 1 OR p.revision <> NEW.c_f6e9b151 THEN RAISE EXCEPTION 'an approval is given to the current revision of a proposed plan'; END IF;
    v_reason := f_aef94638(NEW.c_2c50a46b, NEW.c_d469eef1);
    IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_89e7b248 AFTER INSERT ON t_4ce74e45 FOR EACH ROW EXECUTE FUNCTION f_5ac22af0();
-- at commit: the approval is the one the plan was approved with (an approval never exists without the status change of its plan)
CREATE FUNCTION f_f596b5be() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_18aad15c p WHERE p.c_72ad935c = NEW.c_2c50a46b AND p.c_8f7fe2d6 = NEW.c_d469eef1
                   AND p.c_a2ed1389 = NEW.c_66daa96a) THEN
        RAISE EXCEPTION 'an approval is committed together with the approval of its plan';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_2637527d AFTER INSERT ON t_4ce74e45 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_f596b5be();
CREATE TRIGGER r_523cf98d BEFORE UPDATE OR DELETE ON t_4ce74e45 FOR EACH ROW EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_37e2ed6a BEFORE TRUNCATE ON t_4ce74e45 FOR EACH STATEMENT EXECUTE FUNCTION f_6ee32fd0();

-- ---------------------------------------------------------------------------------------------------------------- the execution
CREATE FUNCTION f_94afc72e() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_reason text;
BEGIN
    -- never client-supplied: the top-level transaction id (64 bits, no wraparound, the same inside a savepoint)
    NEW.c_707246ad := pg_current_xact_id();
    SELECT c_c376fde2 AS status, c_8f7fe2d6 AS approval, c_289fa56f AS created_by INTO p
      FROM public.t_18aad15c WHERE c_72ad935c = NEW.c_270dd1de FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'the plan of an execution does not exist'; END IF;
    IF p.status <> 2 THEN RAISE EXCEPTION 'only an approved compensation plan is executed, once'; END IF;
    v_reason := f_aef94638(NEW.c_270dd1de, p.approval);
    IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    IF NEW.c_0754b48c = p.created_by THEN RAISE EXCEPTION 'a compensation plan is never executed by its creator'; END IF;
    -- X5: no position of this plan's closure lies in the closure of another executed plan
    IF EXISTS (SELECT 1 FROM public.t_832b8c7b c
               JOIN public.t_832b8c7b o ON o.c_42b33ca1 = c.c_42b33ca1 AND o.c_36c90205 = c.c_36c90205
                    AND o.c_546fe0a8 <> c.c_546fe0a8
               JOIN public.t_3ed621fb x ON x.c_270dd1de = o.c_546fe0a8
               WHERE c.c_546fe0a8 = NEW.c_270dd1de) THEN
        RAISE EXCEPTION 'plan_on_epoch_position: a position of the closure was already rewritten by an executed plan';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_85ab1c2b BEFORE INSERT ON t_3ed621fb FOR EACH ROW EXECUTE FUNCTION f_94afc72e();
CREATE TRIGGER r_c02af764 BEFORE UPDATE OR DELETE ON t_3ed621fb FOR EACH ROW EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_8167fafb BEFORE TRUNCATE ON t_3ed621fb FOR EACH STATEMENT EXECUTE FUNCTION f_6ee32fd0();

CREATE FUNCTION f_d1b1c6ca() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF f_126b872f(NEW.c_39484614) IS NULL THEN RAISE EXCEPTION 'a compensation journal is recorded inside the execution of its approved plan'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_cf8a064e BEFORE INSERT ON t_6847b3e3 FOR EACH ROW EXECUTE FUNCTION f_d1b1c6ca();
CREATE TRIGGER r_c184c6bc BEFORE UPDATE OR DELETE ON t_6847b3e3 FOR EACH ROW EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_528770f1 BEFORE TRUNCATE ON t_6847b3e3 FOR EACH STATEMENT EXECUTE FUNCTION f_6ee32fd0();

-- ---------------------------------------------------------------------------------------------------------------- the epoch member (design 3.2)
CREATE FUNCTION f_5a5d4932() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
    o record;
    v_original_kind smallint;
BEGIN
    v_plan := f_126b872f(NEW.c_22781d6b);
    IF v_plan IS NULL THEN RAISE EXCEPTION 'an epoch member belongs to the execution, written in this transaction, of an approved compensation plan'; END IF;
    SELECT c_7f2ea712 AS kind, c_03a68cea AS reverses INTO o FROM public.t_a648ca40 WHERE c_f47f15da = NEW.c_cb9cfcc6;
    IF NOT FOUND THEN RAISE EXCEPTION 'the operation of an epoch member does not exist'; END IF;
    -- epoch operations are never reversal operations (review L1)
    IF o.kind = 4 OR o.reverses IS NOT NULL THEN RAISE EXCEPTION 'a reversal operation is never an epoch member'; END IF;
    -- the operation is being built now: it has no entry yet and no obligation or settlement refers to it (an empty operation of an earlier transaction exists only for those, 0020)
    IF EXISTS (SELECT 1 FROM public.t_bc727c1d WHERE c_eb34b7d8 = NEW.c_cb9cfcc6) THEN RAISE EXCEPTION 'an epoch member is an operation without entries yet'; END IF;
    IF EXISTS (SELECT 1 FROM public.t_b56b119d WHERE c_1872eef0 = NEW.c_cb9cfcc6)
       OR EXISTS (SELECT 1 FROM public.t_d370b1d6 WHERE c_7208ba7b = NEW.c_cb9cfcc6) THEN
        RAISE EXCEPTION 'an operation of a negative-stock obligation or settlement is never an epoch member';
    END IF;
    IF NEW.c_20202c1f IN (1, 3) THEN
        -- X5: no epoch on an epoch
        IF EXISTS (SELECT 1 FROM public.t_5aef6fe2 m WHERE m.c_cb9cfcc6 = NEW.c_91c5049c) THEN RAISE EXCEPTION 'plan_on_epoch_position: an epoch operation is never rewritten by another epoch'; END IF;
        SELECT c_7f2ea712 INTO v_original_kind FROM public.t_a648ca40 WHERE c_f47f15da = NEW.c_91c5049c;
        IF v_original_kind IS DISTINCT FROM o.kind THEN RAISE EXCEPTION 'a storno or rebook operation keeps the kind of its original operation'; END IF;
        -- the entries of the original that the epoch takes back lie in the plan's closure: an original without any entry there is not rewritten by this plan
        IF NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e
                       JOIN public.t_a2903683 ps ON ps.c_6b4abdd9 = e.c_98321e7f
                       JOIN public.t_832b8c7b c ON c.c_546fe0a8 = v_plan AND c.c_42b33ca1 = ps.c_3380c61e
                            AND c.c_36c90205 = ps.c_f0a91938
                       WHERE e.c_eb34b7d8 = NEW.c_91c5049c) THEN
            RAISE EXCEPTION 'the original operation of an epoch member has no entry in the closure of the plan';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_89def7a8 BEFORE INSERT ON t_5aef6fe2 FOR EACH ROW EXECUTE FUNCTION f_5a5d4932();
CREATE TRIGGER r_10621ccb BEFORE UPDATE OR DELETE ON t_5aef6fe2 FOR EACH ROW EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_d89ed68e BEFORE TRUNCATE ON t_5aef6fe2 FOR EACH STATEMENT EXECUTE FUNCTION f_6ee32fd0();

-- Lineage: original entry -> storno entry -> rebook entry, all of one execution of this transaction.
CREATE FUNCTION f_4e3553e9() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    oe record;
    se record;
    re record;
BEGIN
    IF f_126b872f(NEW.c_fdc21bcc) IS NULL THEN RAISE EXCEPTION 'an epoch lineage row belongs to the execution, written in this transaction, of an approved compensation plan'; END IF;
    SELECT c_eb34b7d8 AS op, c_98321e7f AS pos, c_4105796c AS kind, c_e0cd1a14 AS dir, c_12665ffb AS d, c_dba27822 AS qty
      INTO oe FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_c81d75a1;
    SELECT c_eb34b7d8 AS op, c_4f9e777a AS reverses INTO se FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_d3b54ab3;
    SELECT c_eb34b7d8 AS op, c_98321e7f AS pos, c_4105796c AS kind, c_e0cd1a14 AS dir, c_12665ffb AS d, c_dba27822 AS qty,
           c_4f9e777a AS reverses INTO re FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_6b1a5275;
    IF oe IS NULL OR se IS NULL OR re IS NULL THEN RAISE EXCEPTION 'the entries of an epoch lineage row exist'; END IF;
    IF se.reverses IS DISTINCT FROM NEW.c_c81d75a1 THEN RAISE EXCEPTION 'the storno entry of a lineage row takes back its original entry'; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_5aef6fe2 m WHERE m.c_cb9cfcc6 = se.op AND m.c_22781d6b = NEW.c_fdc21bcc AND m.c_20202c1f = 1
                   AND m.c_91c5049c = oe.op)
       OR NOT EXISTS (SELECT 1 FROM public.t_5aef6fe2 m WHERE m.c_cb9cfcc6 = re.op AND m.c_22781d6b = NEW.c_fdc21bcc AND m.c_20202c1f = 3
                      AND m.c_91c5049c = oe.op) THEN
        RAISE EXCEPTION 'the storno and rebook entries of a lineage row belong to the storno and rebook members of the same execution and original operation';
    END IF;
    -- X2: the rebooked entry repeats its original on the same position, with the original date, kind, direction and quantity (only the amount is replayed)
    IF re.pos <> oe.pos OR re.kind <> oe.kind OR re.dir <> oe.dir OR re.d <> oe.d OR re.qty <> oe.qty OR re.reverses IS NOT NULL THEN
        RAISE EXCEPTION 'a rebook entry repeats its original entry on the same position and date';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_1eb7f868 BEFORE INSERT ON t_07854767 FOR EACH ROW EXECUTE FUNCTION f_4e3553e9();
CREATE TRIGGER r_15272cd8 BEFORE UPDATE OR DELETE ON t_07854767 FOR EACH ROW EXECUTE FUNCTION f_6ee32fd0();
CREATE TRIGGER r_64150d8a BEFORE TRUNCATE ON t_07854767 FOR EACH STATEMENT EXECUTE FUNCTION f_6ee32fd0();

-- ---------------------------------------------------------------------------------------------------------------- the entry-level guard (design 3.3(1), (2), (2a), (3)); a NEW function and a NEW trigger
-- The position row is read FOR UPDATE: the costing engine already holds that lock when it appends an entry, so no new lock order arises. An ordinary entry takes no other lock here.
-- The position still holds the date of its previous entry at this point (the engine appends the entry first and moves the position afterwards).
CREATE FUNCTION f_b99e42c6() RETURNS trigger LANGUAGE plpgsql AS $$
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
    SELECT c_20202c1f AS role, c_22781d6b AS execution, c_91c5049c AS original INTO m FROM public.t_5aef6fe2 WHERE c_cb9cfcc6 = NEW.c_eb34b7d8;
    v_member := FOUND;
    IF v_member THEN
        -- every entry of an epoch member, back-dated or not, is written inside the execution of its approved plan, in this transaction
        v_plan := f_126b872f(m.execution);
        IF v_plan IS NULL THEN RAISE EXCEPTION 'an entry of an epoch operation is written only inside the execution, in this transaction, of its approved compensation plan'; END IF;
        -- (2a) closure binding: the position of the entry (the position row decides, not the copied columns) is in the plan's approved closure
        IF NEW.c_9ee52677 <> ps.item OR NEW.c_0f65c03c <> ps.warehouse THEN RAISE EXCEPTION 'an entry of an epoch operation names the item and warehouse of its position'; END IF;
        IF NOT EXISTS (SELECT 1 FROM public.t_832b8c7b c WHERE c.c_546fe0a8 = v_plan AND c.c_42b33ca1 = ps.item
                       AND c.c_36c90205 = ps.warehouse) THEN
            RAISE EXCEPTION 'an entry of an epoch operation lies on a position of the approved closure of its plan';
        END IF;
        -- (3) X2: an Open fiscal period, read FOR SHARE so that a concurrent close cannot commit between this check and the commit (lock order: positions, then periods)
        SELECT c_0a0b7f19 INTO v_period FROM public.t_ae996edc
          WHERE c_8299fb3d <= NEW.c_12665ffb AND c_c0322bcf >= NEW.c_12665ffb FOR SHARE;
        IF NOT FOUND OR v_period <> 1 THEN RAISE EXCEPTION 'period_not_open: an entry of an epoch operation is dated in an Open fiscal period'; END IF;
        -- (3) no open negative-stock obligation on the position
        IF f_cf64ca57(ps.item, ps.warehouse) THEN RAISE EXCEPTION 'an epoch never touches a position with an open negative-stock obligation'; END IF;
        -- (3) X5: the position is not in the closure of another executed plan
        IF EXISTS (SELECT 1 FROM public.t_832b8c7b c JOIN public.t_3ed621fb x ON x.c_270dd1de = c.c_546fe0a8
                   WHERE c.c_42b33ca1 = ps.item AND c.c_36c90205 = ps.warehouse AND c.c_546fe0a8 <> v_plan) THEN
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
                IF EXISTS (SELECT 1 FROM public.t_5aef6fe2 em WHERE em.c_cb9cfcc6 = v_revent) THEN
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
CREATE TRIGGER r_968d3449 BEFORE INSERT ON t_bc727c1d FOR EACH ROW EXECUTE FUNCTION f_b99e42c6();

-- review M2 (b): at commit, the position of every entry written in the transaction has been moved at least to that entry (sequence number and last date). The engine moves the position with every entry and the executor
-- keeps the date with GREATEST, so neither path changes; a writer that appends an entry without moving its position, or moves it without the entry's date, can no longer leave the F1 anchor behind.
CREATE FUNCTION f_d7ff2fd3() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_a2903683 ps WHERE ps.c_6b4abdd9 = NEW.c_98321e7f AND ps.c_ad2af2e5 >= NEW.c_2affbce9
                   AND ps.c_3c880b93 >= NEW.c_12665ffb) THEN
        RAISE EXCEPTION 'position_not_moved: a cost entry is committed together with the move of its position to at least its sequence number and its date';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_d89c9857 AFTER INSERT ON t_bc727c1d DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_d7ff2fd3();

-- ---------------------------------------------------------------------------------------------------------------- X6 (design 3.3(4)): no reversal operation of an epoch member or of an operation with stornoed entries
CREATE FUNCTION f_f1182525() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_03a68cea IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM public.t_5aef6fe2 m WHERE m.c_cb9cfcc6 = NEW.c_03a68cea)
           OR EXISTS (SELECT 1 FROM public.t_07854767 l JOIN public.t_bc727c1d e ON e.c_ed61ae1a = l.c_c81d75a1
                      WHERE e.c_eb34b7d8 = NEW.c_03a68cea) THEN
            RAISE EXCEPTION 'operation_rewritten_by_epoch: an operation rewritten by a replay epoch, or belonging to one, is never reversed';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_300c836a BEFORE INSERT ON t_a648ca40 FOR EACH ROW EXECUTE FUNCTION f_f1182525();

-- ---------------------------------------------------------------------------------------------------------------- X7: a plan-bound stock document is posted only inside the execution of its plan
CREATE FUNCTION f_cb21e64d() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
BEGIN
    SELECT c_72ad935c INTO v_plan FROM public.t_18aad15c WHERE c_9019de55 = NEW.c_eb889fd0 AND c_c376fde2 IN (1, 2);
    IF FOUND AND NOT EXISTS (SELECT 1 FROM public.t_3ed621fb x WHERE x.c_270dd1de = v_plan AND x.c_707246ad = pg_current_xact_id()
                             AND x.c_ff2a9beb = NEW.c_fc71418e) THEN
        RAISE EXCEPTION 'document_bound_to_plan: a stock document bound to a compensation plan is posted only by the execution of that plan';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_db374980 BEFORE INSERT ON t_4d4b251f FOR EACH ROW EXECUTE FUNCTION f_cb21e64d();

-- ---------------------------------------------------------------------------------------------------------------- deferred checks, judged at commit (design 3.4)
CREATE FUNCTION f_2fd28e44() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_members bigint;
    v_events bigint;
BEGIN
    SELECT c_c376fde2 AS status, c_fb20b8da AS executed_xact, c_cac31997 AS closure_hash, c_9019de55 AS document INTO p
      FROM public.t_18aad15c WHERE c_72ad935c = NEW.c_270dd1de;
    -- the plan is executed, and was marked so in the transaction that wrote the execution (this one)
    IF p.status IS DISTINCT FROM 3 OR p.executed_xact IS DISTINCT FROM NEW.c_707246ad OR NEW.c_707246ad <> pg_current_xact_id() THEN
        RAISE EXCEPTION 'an execution is committed together with the executed status of its plan';
    END IF;
    IF f_3cdca6ba(NEW.c_270dd1de) <> p.closure_hash THEN RAISE EXCEPTION 'the closure of the plan does not match its closure hash'; END IF;
    SELECT count(*), count(*) FILTER (WHERE c_20202c1f = 2) INTO v_members, v_events FROM public.t_5aef6fe2 WHERE c_22781d6b = NEW.c_c590e149;
    IF v_members = 0 OR v_events <> 1 THEN RAISE EXCEPTION 'an execution has members and exactly one event'; END IF;
    -- the event member is the cost operation of the execution's stock posting, and that posting is the posting of the plan's document
    IF NOT EXISTS (SELECT 1 FROM public.t_5aef6fe2 m JOIN public.t_4d4b251f sp ON sp.c_4ce1587f = m.c_cb9cfcc6
                   WHERE m.c_22781d6b = NEW.c_c590e149 AND m.c_20202c1f = 2 AND sp.c_fc71418e = NEW.c_ff2a9beb
                     AND sp.c_eb889fd0 = p.document) THEN
        RAISE EXCEPTION 'the event of an execution is the posting of the document of its plan';
    END IF;
    -- every storno member has its rebook member for the same original operation, and the reverse (each is unique per original by the index)
    IF EXISTS (SELECT 1 FROM public.t_5aef6fe2 s WHERE s.c_22781d6b = NEW.c_c590e149 AND s.c_20202c1f IN (1, 3)
               AND NOT EXISTS (SELECT 1 FROM public.t_5aef6fe2 t WHERE t.c_22781d6b = s.c_22781d6b AND t.c_20202c1f = 4 - s.c_20202c1f
                               AND t.c_91c5049c = s.c_91c5049c)) THEN
        RAISE EXCEPTION 'every storno member has its rebook member for the same original operation';
    END IF;
    IF EXISTS (SELECT 1 FROM public.t_5aef6fe2 m WHERE m.c_22781d6b = NEW.c_c590e149
               AND NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e WHERE e.c_eb34b7d8 = m.c_cb9cfcc6)) THEN
        RAISE EXCEPTION 'every epoch member has at least one entry';
    END IF;
    -- every storno entry and every rebook entry has its lineage row
    IF EXISTS (SELECT 1 FROM public.t_5aef6fe2 m JOIN public.t_bc727c1d e ON e.c_eb34b7d8 = m.c_cb9cfcc6
               WHERE m.c_22781d6b = NEW.c_c590e149
                 AND ((m.c_20202c1f = 1 AND NOT EXISTS (SELECT 1 FROM public.t_07854767 l WHERE l.c_d3b54ab3 = e.c_ed61ae1a AND l.c_fdc21bcc = m.c_22781d6b))
                   OR (m.c_20202c1f = 3 AND NOT EXISTS (SELECT 1 FROM public.t_07854767 l WHERE l.c_6b1a5275 = e.c_ed61ae1a AND l.c_fdc21bcc = m.c_22781d6b)))) THEN
        RAISE EXCEPTION 'every storno and rebook entry has its lineage row';
    END IF;
    -- the business audit event of the execution: of the plan-executed type, with this plan as subject and this execution as target (0002 is not altered)
    IF NOT EXISTS (SELECT 1 FROM public.t_2db8cf1f a WHERE a.c_ef116a9d = NEW.c_ae6ab815 AND a.c_4c6c6496 = 1154
                   AND a.c_629e971c = NEW.c_270dd1de AND a.c_deb363a5 = NEW.c_c590e149) THEN
        RAISE EXCEPTION 'an execution is committed together with its business audit event';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_a2b8338d AFTER INSERT ON t_3ed621fb DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_2fd28e44();

-- orphan detection: no member without an execution of this transaction, and no member without entries
CREATE FUNCTION f_cad68ba9() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_3ed621fb x WHERE x.c_c590e149 = NEW.c_22781d6b AND x.c_707246ad = pg_current_xact_id()) THEN
        RAISE EXCEPTION 'an epoch member is committed together with its execution';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e WHERE e.c_eb34b7d8 = NEW.c_cb9cfcc6) THEN RAISE EXCEPTION 'every epoch member has at least one entry'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_86258955 AFTER INSERT ON t_5aef6fe2 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_cad68ba9();
