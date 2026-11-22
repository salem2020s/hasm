-- Migration 0033: plan storage, approval and atomic execution of a compensation plan, database layer (Phase 3.8D step 4; owner decision X1-A of 2026-11-21 with mandatory safeguards;
-- design docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md revision 3, confirmed by the independent review docs/review-records/PR-48-plan-execution-design-rev3.md). PROVISIONAL NUMBER: it follows the merge order.
-- Forward-only and ADDITIVE: new tables, new functions and new triggers only. Nothing existing is removed, replaced, altered, paused or relaxed: no existing function is redefined, no existing trigger is touched,
-- no existing constraint is changed, nothing is seeded and no existing row is read or modified. Opaque physical names (D-029) generated deterministically by tools/schema/company-0033-plan-execution.cjs
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
CREATE TABLE t_96484f97 (
    c_621d79c0 uuid NOT NULL,
    c_4d700e9d uuid NOT NULL,
    c_beceb1db uuid NOT NULL,
    c_09237930 uuid NOT NULL,
    c_aaa05d6a bigint NOT NULL,
    c_ca0cca77 text NOT NULL,
    c_dec3e2ca char(64) NOT NULL,
    c_c472f350 char(64) NOT NULL,
    c_dd1e8733 char(64) NOT NULL,
    c_cdbe8eb1 char(64) NOT NULL,
    c_55963e05 char(64) NOT NULL,
    c_43e25975 char(64) NOT NULL,
    c_0f570e78 char(64) NOT NULL,
    c_7bfde04e char(64) NOT NULL,
    c_0df673f9 smallint NOT NULL,
    c_b82be849 bigint NOT NULL,
    c_d90f39d9 uuid NOT NULL,
    c_ca4318fd timestamptz NOT NULL,
    c_56265f1a xid8 NOT NULL,
    c_3318ed81 uuid NULL,
    c_95e58c86 uuid NULL,
    c_9bc8a099 timestamptz NULL,
    c_3ef7b3ba xid8 NULL,
    c_397fa145 timestamptz NOT NULL,
    CONSTRAINT k_5125839c PRIMARY KEY (c_621d79c0),
    CONSTRAINT k_138abb68 FOREIGN KEY (c_4d700e9d) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_9d5cb252 FOREIGN KEY (c_beceb1db) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_8185ed88 FOREIGN KEY (c_09237930) REFERENCES t_e92d29e5 (c_9a59d13e),
    CONSTRAINT k_3ad27004 CHECK (c_0df673f9 IN (1, 2, 3, 4, 5, 6) AND c_b82be849 > 0 AND c_aaa05d6a > 0
        AND char_length(c_ca0cca77) >= 2 AND c_7bfde04e ~ '^[0-9a-f]{64}$'
        AND c_dec3e2ca ~ '^[0-9a-f]{64}$' AND c_c472f350 ~ '^[0-9a-f]{64}$' AND c_dd1e8733 ~ '^[0-9a-f]{64}$' AND c_cdbe8eb1 ~ '^[0-9a-f]{64}$' AND c_55963e05 ~ '^[0-9a-f]{64}$' AND c_43e25975 ~ '^[0-9a-f]{64}$' AND c_0f570e78 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT k_cc7c29a2 CHECK (
        (c_3318ed81 IS NULL) = (c_95e58c86 IS NULL) AND (c_3318ed81 IS NULL) = (c_9bc8a099 IS NULL)
        AND (c_0df673f9 NOT IN (2, 3) OR c_3318ed81 IS NOT NULL) AND (c_0df673f9 NOT IN (1, 4) OR c_3318ed81 IS NULL)
        AND (c_95e58c86 IS NULL OR c_95e58c86 <> c_d90f39d9)
        AND (c_0df673f9 = 3) = (c_3ef7b3ba IS NOT NULL))
);
-- X7: a stock document is bound to at most one live (proposed or approved) plan
CREATE UNIQUE INDEX k_b95b781a ON t_96484f97 (c_09237930) WHERE c_0df673f9 IN (1, 2);
REVOKE ALL ON TABLE t_96484f97 FROM PUBLIC;

-- The plan's closure: every (item, warehouse) position the replay may touch (the roots plus every position reached through transfers).
CREATE TABLE t_8cb0e5ef (
    c_5a28af19 uuid NOT NULL,
    c_a6b7570c uuid NOT NULL,
    c_d413a7c5 uuid NOT NULL,
    CONSTRAINT k_52f44399 PRIMARY KEY (c_5a28af19, c_a6b7570c, c_d413a7c5),
    CONSTRAINT k_91589911 FOREIGN KEY (c_5a28af19) REFERENCES t_96484f97 (c_621d79c0),
    CONSTRAINT k_bac51915 FOREIGN KEY (c_a6b7570c) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_de47daf8 FOREIGN KEY (c_d413a7c5) REFERENCES t_d2006f04 (c_a9f73cc2)
);
CREATE INDEX k_65c260ab ON t_8cb0e5ef (c_a6b7570c, c_d413a7c5);
REVOKE ALL ON TABLE t_8cb0e5ef FROM PUBLIC;

-- The compensation lines the approver sees (per affected posting, period, branch and account).
CREATE TABLE t_973d21ee (
    c_23990f6a uuid NOT NULL,
    c_8a2e98f5 integer NOT NULL,
    c_7e950db1 uuid NULL,
    c_db8fb454 date NOT NULL,
    c_0fb19d9a uuid NOT NULL,
    c_0053a11a uuid NOT NULL,
    c_3588af4d uuid NOT NULL,
    c_07178f76 numeric(28,6) NOT NULL,
    c_92444505 numeric(28,6) NOT NULL,
    CONSTRAINT k_bb4321f2 PRIMARY KEY (c_23990f6a, c_8a2e98f5),
    CONSTRAINT k_d2574843 FOREIGN KEY (c_23990f6a) REFERENCES t_96484f97 (c_621d79c0),
    CONSTRAINT k_b9fd1adf FOREIGN KEY (c_7e950db1) REFERENCES t_4d4b251f (c_fc71418e),
    CONSTRAINT k_350c992f FOREIGN KEY (c_0fb19d9a) REFERENCES t_ae996edc (c_d61cbb9c),
    CONSTRAINT k_1b1d7671 FOREIGN KEY (c_0053a11a) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_bbf9afc8 FOREIGN KEY (c_3588af4d) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_f1541dbb CHECK (c_8a2e98f5 > 0 AND c_07178f76 >= 0 AND c_92444505 >= 0
        AND (c_07178f76 > 0) <> (c_92444505 > 0))
);
REVOKE ALL ON TABLE t_973d21ee FROM PUBLIC;

-- The plan's history (append-only).
CREATE TABLE t_a76c14d0 (
    c_a6a0998e uuid NOT NULL,
    c_ddc8b329 uuid NOT NULL,
    c_a0df2a1e smallint NOT NULL,
    c_fa415e50 uuid NOT NULL,
    c_743850ee timestamptz NOT NULL,
    c_e8c3465f varchar(500) NULL,
    CONSTRAINT k_46f3179d PRIMARY KEY (c_a6a0998e),
    CONSTRAINT k_bc98de59 FOREIGN KEY (c_ddc8b329) REFERENCES t_96484f97 (c_621d79c0),
    CONSTRAINT k_bf0abab8 CHECK (c_a0df2a1e IN (1, 2, 3, 4, 5, 6))
);
CREATE INDEX k_add5f78b ON t_a76c14d0 (c_ddc8b329);
REVOKE ALL ON TABLE t_a76c14d0 FROM PUBLIC;

-- The approval: bound to the plan revision and to every fingerprint of the plan (the closure hash included).
CREATE TABLE t_964512bc (
    c_43db9bce uuid NOT NULL,
    c_028bc185 uuid NOT NULL,
    c_1eabbc1d bigint NOT NULL,
    c_9fb6b16d char(64) NOT NULL,
    c_48183cc6 char(64) NOT NULL,
    c_4bd82fab char(64) NOT NULL,
    c_4c26b9b6 char(64) NOT NULL,
    c_dc702d45 char(64) NOT NULL,
    c_73066370 char(64) NOT NULL,
    c_c2116a32 char(64) NOT NULL,
    c_a50ac941 char(64) NOT NULL,
    c_44893b8a uuid NOT NULL,
    c_c0fb8131 timestamptz NOT NULL,
    c_bd3887aa varchar(500) NULL,
    CONSTRAINT k_48c05195 PRIMARY KEY (c_43db9bce),
    CONSTRAINT k_588cc9f8 FOREIGN KEY (c_028bc185) REFERENCES t_96484f97 (c_621d79c0),
    CONSTRAINT k_b85735ac UNIQUE (c_028bc185, c_1eabbc1d)
);
REVOKE ALL ON TABLE t_964512bc FROM PUBLIC;

-- The single execution of a plan. The posting of the event and the audit event are named when the row is written (the row is append-only); both must exist at commit.
CREATE TABLE t_e965da92 (
    c_64aab6aa uuid NOT NULL,
    c_81f8cb75 uuid NOT NULL,
    c_2f84a7ef uuid NOT NULL,
    c_31e9c559 uuid NOT NULL,
    c_adb5bbdf uuid NOT NULL,
    c_40664479 timestamptz NOT NULL,
    c_1994ea3f xid8 NOT NULL,
    CONSTRAINT k_bb508788 PRIMARY KEY (c_64aab6aa),
    CONSTRAINT k_129c8912 FOREIGN KEY (c_81f8cb75) REFERENCES t_96484f97 (c_621d79c0),
    CONSTRAINT k_b075eb95 UNIQUE (c_81f8cb75),
    CONSTRAINT k_a7172c9d UNIQUE (c_31e9c559),
    CONSTRAINT k_a44681c2 UNIQUE (c_2f84a7ef),
    CONSTRAINT k_a547ab83 FOREIGN KEY (c_2f84a7ef) REFERENCES t_4d4b251f (c_fc71418e) DEFERRABLE INITIALLY DEFERRED
);
REVOKE ALL ON TABLE t_e965da92 FROM PUBLIC;

-- The compensation journals of an execution, one per bucket (the journal source key is plan + bucket, so a repeat cannot create a second set).
CREATE TABLE t_2f7e3dcb (
    c_be4084de uuid NOT NULL,
    c_594cd0af integer NOT NULL,
    c_1ecde088 uuid NOT NULL,
    CONSTRAINT k_cd2b922b PRIMARY KEY (c_be4084de, c_594cd0af),
    CONSTRAINT k_e93aec0a FOREIGN KEY (c_be4084de) REFERENCES t_e965da92 (c_64aab6aa),
    CONSTRAINT k_6d475ce3 FOREIGN KEY (c_1ecde088) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_9054438c UNIQUE (c_1ecde088),
    CONSTRAINT k_f4df19bd CHECK (c_594cd0af > 0)
);
REVOKE ALL ON TABLE t_2f7e3dcb FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- epoch membership and lineage
-- An epoch operation keeps its NATURAL kind (the kind CHECK of 0014 is not widened); its meaning comes from this side table (the pattern of 0020, DB-1).
CREATE TABLE t_9083c68c (
    c_85ee124a uuid NOT NULL,
    c_3c11cd4a uuid NOT NULL,
    c_87c64961 smallint NOT NULL,
    c_05f49ec6 uuid NULL,
    CONSTRAINT k_ba6e66ae PRIMARY KEY (c_85ee124a),
    CONSTRAINT k_a54abda6 FOREIGN KEY (c_85ee124a) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_54f7550c FOREIGN KEY (c_3c11cd4a) REFERENCES t_e965da92 (c_64aab6aa),
    CONSTRAINT k_a3026bea FOREIGN KEY (c_05f49ec6) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_c61a6d76 CHECK (c_87c64961 IN (1, 2, 3) AND (c_87c64961 = 2) = (c_05f49ec6 IS NULL)
        AND c_05f49ec6 IS DISTINCT FROM c_85ee124a)
);
-- an original operation is stornoed once and rebooked once, ever; an execution has one event
CREATE UNIQUE INDEX k_b9bd2929 ON t_9083c68c (c_05f49ec6, c_87c64961) WHERE c_05f49ec6 IS NOT NULL;
CREATE UNIQUE INDEX k_6baafda9 ON t_9083c68c (c_3c11cd4a) WHERE c_87c64961 = 2;
CREATE INDEX k_1e32757b ON t_9083c68c (c_3c11cd4a);
REVOKE ALL ON TABLE t_9083c68c FROM PUBLIC;

CREATE TABLE t_1028a48d (
    c_eefb4728 bigint NOT NULL,
    c_a4678c70 bigint NOT NULL,
    c_b9f16b78 bigint NOT NULL,
    c_60dbf0cd uuid NOT NULL,
    CONSTRAINT k_ea792ff3 PRIMARY KEY (c_eefb4728),
    CONSTRAINT k_37fbf95b UNIQUE (c_a4678c70),
    CONSTRAINT k_8db1f1c4 UNIQUE (c_b9f16b78),
    CONSTRAINT k_1334b51e FOREIGN KEY (c_eefb4728) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_c36916a3 FOREIGN KEY (c_a4678c70) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_fc0f0242 FOREIGN KEY (c_b9f16b78) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_18824845 FOREIGN KEY (c_60dbf0cd) REFERENCES t_e965da92 (c_64aab6aa),
    CONSTRAINT k_a9ddd4b2 CHECK (c_eefb4728 <> c_a4678c70 AND c_eefb4728 <> c_b9f16b78
        AND c_a4678c70 <> c_b9f16b78)
);
CREATE INDEX k_410e89b8 ON t_1028a48d (c_60dbf0cd);
REVOKE ALL ON TABLE t_1028a48d FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- shared functions (all read table rows only; none reads a setting)
-- The closure hash, byte-exact (review L1): each pair is the lowercase hyphenated text of the item uuid, a colon and the lowercase hyphenated text of the warehouse uuid; the pairs are sorted by (item, warehouse)
-- in uuid order (the ORDER BY is on the uuid columns, never on text, so no collation is involved), joined by ONE newline without a trailing newline, encoded in UTF-8; the result is the lowercase hex SHA-256.
-- An empty closure hashes the empty string. PostgreSQL is the only place that computes it.
CREATE FUNCTION f_401f2ced(p_plan uuid) RETURNS char(64) LANGUAGE sql AS $$
    SELECT encode(sha256(convert_to(COALESCE(string_agg(lower(c.c_a6b7570c::text) || ':' || lower(c.c_d413a7c5::text), E'\n'
        ORDER BY c.c_a6b7570c, c.c_d413a7c5), ''), 'UTF8')), 'hex')::char(64)
    FROM public.t_8cb0e5ef c WHERE c.c_5a28af19 = p_plan
$$;

-- X3: true when the user is independent of the plan: not its creator, and not the creator, an editor or the submitter of the event's stock document (every editor counts, which contains the last editor).
CREATE FUNCTION f_e061ccfa(p_plan uuid, p_user uuid) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE
    p record;
BEGIN
    SELECT c_d90f39d9 AS created_by, c_09237930 AS document INTO p FROM public.t_96484f97 WHERE c_621d79c0 = p_plan;
    IF NOT FOUND OR p_user IS NULL THEN RETURN false; END IF;
    IF p_user = p.created_by THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_a76c14d0 e WHERE e.c_ddc8b329 = p_plan AND e.c_a0df2a1e = 1 AND e.c_fa415e50 = p_user) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_e92d29e5 d WHERE d.c_9a59d13e = p.document AND (d.c_6b6eb86b = p_user OR d.c_afb8d9de = p_user)) THEN RETURN false; END IF;
    IF EXISTS (SELECT 1 FROM public.t_b9231a6b e WHERE e.c_fd72fc35 = p.document AND e.c_2fe7652c IN (1, 2, 3) AND e.c_2a6bae0c = p_user) THEN RETURN false; END IF;
    RETURN true;
END;
$$;

-- NULL when the approval is a sound approval of the plan; otherwise the reason. Sound: it belongs to the plan, repeats every fingerprint of the plan, the closure rows still hash to the plan's closure hash, the
-- approver is independent (X3), and the event's stock document is approved at the planned revision (X7).
CREATE FUNCTION f_0be71dce(p_plan uuid, p_approval uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    p public.t_96484f97%ROWTYPE;
    a public.t_964512bc%ROWTYPE;
BEGIN
    SELECT * INTO p FROM public.t_96484f97 WHERE c_621d79c0 = p_plan;
    IF NOT FOUND THEN RETURN 'plan_not_found'; END IF;
    SELECT * INTO a FROM public.t_964512bc WHERE c_43db9bce = p_approval AND c_028bc185 = p_plan;
    IF NOT FOUND THEN RETURN 'approval_not_found'; END IF;
    IF a.c_9fb6b16d <> p.c_dec3e2ca OR a.c_48183cc6 <> p.c_c472f350 OR a.c_4bd82fab <> p.c_dd1e8733 OR a.c_4c26b9b6 <> p.c_cdbe8eb1 OR a.c_dc702d45 <> p.c_55963e05 OR a.c_73066370 <> p.c_43e25975 OR a.c_c2116a32 <> p.c_0f570e78 OR a.c_a50ac941 <> p.c_7bfde04e THEN RETURN 'approval_fingerprint_mismatch'; END IF;
    IF f_401f2ced(p_plan) <> p.c_7bfde04e THEN RETURN 'closure_hash_mismatch'; END IF;
    IF NOT f_e061ccfa(p_plan, a.c_44893b8a) THEN RETURN 'approver_not_independent'; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_e92d29e5 d WHERE d.c_9a59d13e = p.c_09237930 AND d.c_3fc0f063 = 3 AND d.c_9ba641c8 = p.c_aaa05d6a) THEN
        RETURN 'document_not_approved';
    END IF;
    -- review L1: a document that already has a stock posting is never executed by a plan (a posting is unique per document, so such a plan could never execute); this is read at approval and again at execution
    IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = p.c_09237930) THEN RETURN 'document_already_posted'; END IF;
    RETURN NULL;
END;
$$;

-- The plan of an execution that is LIVE: written in the current top-level transaction (xid8, correct inside savepoints), of a plan that is approved, by an approver who is not its creator, with an approval whose
-- fingerprints equal the plan's. NULL otherwise. This is the same-transaction binding of every epoch row; it reads rows only.
CREATE FUNCTION f_17688ad3(p_execution uuid) RETURNS uuid LANGUAGE sql AS $$
    SELECT p.c_621d79c0
    FROM public.t_e965da92 x
    JOIN public.t_96484f97 p ON p.c_621d79c0 = x.c_81f8cb75
    JOIN public.t_964512bc a ON a.c_43db9bce = p.c_3318ed81 AND a.c_028bc185 = p.c_621d79c0
    WHERE x.c_64aab6aa = p_execution AND x.c_1994ea3f = pg_current_xact_id() AND p.c_0df673f9 = 2
      AND a.c_44893b8a = p.c_95e58c86 AND a.c_44893b8a <> p.c_d90f39d9
      AND NOT (a.c_9fb6b16d <> p.c_dec3e2ca OR a.c_48183cc6 <> p.c_c472f350 OR a.c_4bd82fab <> p.c_dd1e8733 OR a.c_4c26b9b6 <> p.c_cdbe8eb1 OR a.c_dc702d45 <> p.c_55963e05 OR a.c_73066370 <> p.c_43e25975 OR a.c_c2116a32 <> p.c_0f570e78) AND a.c_a50ac941 = p.c_7bfde04e
$$;

-- Append-only guard shared by every new table except the plan.
CREATE FUNCTION f_c7e7f081() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'compensation plan and epoch records are append-only';
END;
$$;

-- ---------------------------------------------------------------------------------------------------------------- the plan: immutability, status transitions, closure hash
CREATE FUNCTION f_f34eb2da() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_reason text;
BEGIN
    IF TG_LEVEL = 'STATEMENT' OR TG_OP = 'DELETE' THEN RAISE EXCEPTION 'compensation plans are never removed'; END IF;
    IF TG_OP = 'INSERT' THEN
        -- never client-supplied: the creating transaction and the closure hash (of the closure written so far: none yet)
        NEW.c_56265f1a := pg_current_xact_id();
        NEW.c_7bfde04e := f_401f2ced(NEW.c_621d79c0);
        IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = NEW.c_09237930) THEN RAISE EXCEPTION 'document_already_posted: a plan is not written for a document that already has a stock posting'; END IF;
        IF NEW.c_0df673f9 <> 1 OR NEW.c_b82be849 <> 1 OR NEW.c_3318ed81 IS NOT NULL OR NEW.c_95e58c86 IS NOT NULL
           OR NEW.c_9bc8a099 IS NOT NULL OR NEW.c_3ef7b3ba IS NOT NULL THEN
            RAISE EXCEPTION 'a compensation plan starts as proposed, at revision 1';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_621d79c0 IS DISTINCT FROM OLD.c_621d79c0 OR NEW.c_4d700e9d IS DISTINCT FROM OLD.c_4d700e9d
       OR NEW.c_beceb1db IS DISTINCT FROM OLD.c_beceb1db OR NEW.c_09237930 IS DISTINCT FROM OLD.c_09237930
       OR NEW.c_aaa05d6a IS DISTINCT FROM OLD.c_aaa05d6a OR NEW.c_ca0cca77 IS DISTINCT FROM OLD.c_ca0cca77
       OR NEW.c_dec3e2ca IS DISTINCT FROM OLD.c_dec3e2ca OR NEW.c_c472f350 IS DISTINCT FROM OLD.c_c472f350 OR NEW.c_dd1e8733 IS DISTINCT FROM OLD.c_dd1e8733 OR NEW.c_cdbe8eb1 IS DISTINCT FROM OLD.c_cdbe8eb1 OR NEW.c_55963e05 IS DISTINCT FROM OLD.c_55963e05 OR NEW.c_43e25975 IS DISTINCT FROM OLD.c_43e25975 OR NEW.c_0f570e78 IS DISTINCT FROM OLD.c_0f570e78
       OR NEW.c_d90f39d9 IS DISTINCT FROM OLD.c_d90f39d9 OR NEW.c_ca4318fd IS DISTINCT FROM OLD.c_ca4318fd
       OR NEW.c_56265f1a IS DISTINCT FROM OLD.c_56265f1a THEN
        RAISE EXCEPTION 'the payload, the fingerprints and the origin of a compensation plan are immutable';
    END IF;
    -- the closure hash follows the closure rows while they can still be written (a proposed plan in its creating transaction); afterwards it is frozen
    IF OLD.c_0df673f9 = 1 AND OLD.c_56265f1a = pg_current_xact_id() THEN
        NEW.c_7bfde04e := f_401f2ced(OLD.c_621d79c0);
    ELSIF NEW.c_7bfde04e IS DISTINCT FROM OLD.c_7bfde04e THEN
        RAISE EXCEPTION 'the closure hash of a compensation plan is immutable';
    END IF;
    IF NEW.c_0df673f9 = OLD.c_0df673f9 THEN
        IF NEW.c_b82be849 IS DISTINCT FROM OLD.c_b82be849 OR NEW.c_3318ed81 IS DISTINCT FROM OLD.c_3318ed81
           OR NEW.c_95e58c86 IS DISTINCT FROM OLD.c_95e58c86 OR NEW.c_9bc8a099 IS DISTINCT FROM OLD.c_9bc8a099
           OR NEW.c_3ef7b3ba IS DISTINCT FROM OLD.c_3ef7b3ba OR NEW.c_397fa145 IS DISTINCT FROM OLD.c_397fa145 THEN
            RAISE EXCEPTION 'a compensation plan changes only by a status transition';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_b82be849 <> OLD.c_b82be849 + 1 THEN RAISE EXCEPTION 'a status transition advances the plan revision by exactly one'; END IF;
    IF NOT ((OLD.c_0df673f9 = 1 AND NEW.c_0df673f9 IN (2, 4, 5, 6)) OR (OLD.c_0df673f9 = 2 AND NEW.c_0df673f9 IN (3, 5, 6))) THEN
        RAISE EXCEPTION 'this status transition of a compensation plan is not allowed';
    END IF;
    IF NEW.c_0df673f9 = 2 THEN
        IF NEW.c_3318ed81 IS NULL OR NEW.c_95e58c86 IS NULL OR NEW.c_9bc8a099 IS NULL THEN RAISE EXCEPTION 'an approved plan names its approval'; END IF;
        IF NOT EXISTS (SELECT 1 FROM public.t_964512bc a WHERE a.c_43db9bce = NEW.c_3318ed81 AND a.c_028bc185 = OLD.c_621d79c0
                       AND a.c_1eabbc1d = OLD.c_b82be849 AND a.c_44893b8a = NEW.c_95e58c86) THEN
            RAISE EXCEPTION 'a plan is approved by the approval of its current revision';
        END IF;
        -- the approval re-checks the closure (hash recomputed from the closure rows), the fingerprints, the segregation of duties and the event document
        v_reason := f_0be71dce(OLD.c_621d79c0, NEW.c_3318ed81);
        IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    ELSIF NEW.c_3318ed81 IS DISTINCT FROM OLD.c_3318ed81 OR NEW.c_95e58c86 IS DISTINCT FROM OLD.c_95e58c86
          OR NEW.c_9bc8a099 IS DISTINCT FROM OLD.c_9bc8a099 THEN
        RAISE EXCEPTION 'the approval of a compensation plan is set once, by its approval';
    END IF;
    IF NEW.c_0df673f9 = 3 THEN
        IF NOT EXISTS (SELECT 1 FROM public.t_e965da92 x WHERE x.c_81f8cb75 = OLD.c_621d79c0 AND x.c_1994ea3f = pg_current_xact_id()) THEN
            RAISE EXCEPTION 'a plan is marked executed only in the transaction of its execution';
        END IF;
        IF f_401f2ced(OLD.c_621d79c0) <> OLD.c_7bfde04e THEN RAISE EXCEPTION 'the closure of the plan no longer matches its closure hash'; END IF;
        NEW.c_3ef7b3ba := pg_current_xact_id();
    ELSIF NEW.c_3ef7b3ba IS NOT NULL THEN
        RAISE EXCEPTION 'only an executed plan carries its executing transaction';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_c4487038 BEFORE INSERT OR UPDATE OR DELETE ON t_96484f97 FOR EACH ROW EXECUTE FUNCTION f_f34eb2da();
CREATE TRIGGER r_53849840 BEFORE TRUNCATE ON t_96484f97 FOR EACH STATEMENT EXECUTE FUNCTION f_f34eb2da();

-- At commit (review L2): the plan and its closure were written together; the closure contains the plan's root position and hashes to the stored closure hash.
CREATE FUNCTION f_960bccae() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
BEGIN
    SELECT c_4d700e9d AS item, c_beceb1db AS warehouse, c_7bfde04e AS closure_hash INTO p FROM public.t_96484f97 WHERE c_621d79c0 = NEW.c_621d79c0;
    IF NOT EXISTS (SELECT 1 FROM public.t_8cb0e5ef c WHERE c.c_5a28af19 = NEW.c_621d79c0 AND c.c_a6b7570c = p.item
                   AND c.c_d413a7c5 = p.warehouse) THEN
        RAISE EXCEPTION 'the closure of a compensation plan contains its root position and is written with the plan';
    END IF;
    IF f_401f2ced(NEW.c_621d79c0) <> p.closure_hash THEN RAISE EXCEPTION 'the closure of the plan does not match its closure hash'; END IF;
    -- review L1: re-checked at commit, so a posting committed after the insert-time check (read committed) is seen; under REPEATABLE READ the approval and the execution re-read it again
    IF EXISTS (SELECT 1 FROM public.t_4d4b251f sp WHERE sp.c_eb889fd0 = NEW.c_09237930) THEN RAISE EXCEPTION 'document_already_posted: a plan is not written for a document that already has a stock posting'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_e6a8a380 AFTER INSERT ON t_96484f97 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_960bccae();

-- Closure rows and lines: written only while the plan is proposed AND in the transaction that created the plan (review M3 and L2). A new closure therefore needs a new plan and a new approval.
CREATE FUNCTION f_a3ccf9dc() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
BEGIN
    IF TG_TABLE_NAME = 't_8cb0e5ef' THEN v_plan := NEW.c_5a28af19; ELSE v_plan := NEW.c_23990f6a; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_96484f97 p WHERE p.c_621d79c0 = v_plan AND p.c_0df673f9 = 1 AND p.c_56265f1a = pg_current_xact_id()) THEN
        RAISE EXCEPTION 'the closure and the lines of a compensation plan are written with the plan, in its creating transaction';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_7c764da4 BEFORE INSERT ON t_8cb0e5ef FOR EACH ROW EXECUTE FUNCTION f_a3ccf9dc();
CREATE TRIGGER r_a478e1c4 BEFORE INSERT ON t_973d21ee FOR EACH ROW EXECUTE FUNCTION f_a3ccf9dc();
-- every closure row refreshes the plan's hash through the plan's own trigger (which computes it; the value written here is ignored)
CREATE FUNCTION f_a27bc836() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    UPDATE public.t_96484f97 SET c_7bfde04e = c_7bfde04e WHERE c_621d79c0 = NEW.c_5a28af19;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_9dae1b1f AFTER INSERT ON t_8cb0e5ef FOR EACH ROW EXECUTE FUNCTION f_a27bc836();
CREATE TRIGGER r_7c831de2 BEFORE UPDATE OR DELETE ON t_8cb0e5ef FOR EACH ROW EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_fc5f7287 BEFORE TRUNCATE ON t_8cb0e5ef FOR EACH STATEMENT EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_3300b760 BEFORE UPDATE OR DELETE ON t_973d21ee FOR EACH ROW EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_d67b8b33 BEFORE TRUNCATE ON t_973d21ee FOR EACH STATEMENT EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_23f4f8ee BEFORE UPDATE OR DELETE ON t_a76c14d0 FOR EACH ROW EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_e5d9bdaf BEFORE TRUNCATE ON t_a76c14d0 FOR EACH STATEMENT EXECUTE FUNCTION f_c7e7f081();

-- ---------------------------------------------------------------------------------------------------------------- the approval
CREATE FUNCTION f_aab0e23c() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_reason text;
BEGIN
    -- the plan row is locked first: approvals, executions and status changes of one plan are serialized for every writer
    SELECT c_0df673f9 AS status, c_b82be849 AS revision INTO p FROM public.t_96484f97 WHERE c_621d79c0 = NEW.c_028bc185 FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'the plan of an approval does not exist'; END IF;
    IF p.status <> 1 OR p.revision <> NEW.c_1eabbc1d THEN RAISE EXCEPTION 'an approval is given to the current revision of a proposed plan'; END IF;
    v_reason := f_0be71dce(NEW.c_028bc185, NEW.c_43db9bce);
    IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_5b654d90 AFTER INSERT ON t_964512bc FOR EACH ROW EXECUTE FUNCTION f_aab0e23c();
-- at commit: the approval is the one the plan was approved with (an approval never exists without the status change of its plan)
CREATE FUNCTION f_11d587c2() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_96484f97 p WHERE p.c_621d79c0 = NEW.c_028bc185 AND p.c_3318ed81 = NEW.c_43db9bce
                   AND p.c_95e58c86 = NEW.c_44893b8a) THEN
        RAISE EXCEPTION 'an approval is committed together with the approval of its plan';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_1f7a26c7 AFTER INSERT ON t_964512bc DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_11d587c2();
CREATE TRIGGER r_eb9f0029 BEFORE UPDATE OR DELETE ON t_964512bc FOR EACH ROW EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_fb24db33 BEFORE TRUNCATE ON t_964512bc FOR EACH STATEMENT EXECUTE FUNCTION f_c7e7f081();

-- ---------------------------------------------------------------------------------------------------------------- the execution
CREATE FUNCTION f_5812be86() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_reason text;
BEGIN
    -- never client-supplied: the top-level transaction id (64 bits, no wraparound, the same inside a savepoint)
    NEW.c_1994ea3f := pg_current_xact_id();
    SELECT c_0df673f9 AS status, c_3318ed81 AS approval, c_d90f39d9 AS created_by INTO p
      FROM public.t_96484f97 WHERE c_621d79c0 = NEW.c_81f8cb75 FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'the plan of an execution does not exist'; END IF;
    IF p.status <> 2 THEN RAISE EXCEPTION 'only an approved compensation plan is executed, once'; END IF;
    v_reason := f_0be71dce(NEW.c_81f8cb75, p.approval);
    IF v_reason IS NOT NULL THEN RAISE EXCEPTION 'the approval of a compensation plan is not sound: %', v_reason; END IF;
    IF NEW.c_adb5bbdf = p.created_by THEN RAISE EXCEPTION 'a compensation plan is never executed by its creator'; END IF;
    -- X5: no position of this plan's closure lies in the closure of another executed plan
    IF EXISTS (SELECT 1 FROM public.t_8cb0e5ef c
               JOIN public.t_8cb0e5ef o ON o.c_a6b7570c = c.c_a6b7570c AND o.c_d413a7c5 = c.c_d413a7c5
                    AND o.c_5a28af19 <> c.c_5a28af19
               JOIN public.t_e965da92 x ON x.c_81f8cb75 = o.c_5a28af19
               WHERE c.c_5a28af19 = NEW.c_81f8cb75) THEN
        RAISE EXCEPTION 'plan_on_epoch_position: a position of the closure was already rewritten by an executed plan';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_9050c86f BEFORE INSERT ON t_e965da92 FOR EACH ROW EXECUTE FUNCTION f_5812be86();
CREATE TRIGGER r_100a514e BEFORE UPDATE OR DELETE ON t_e965da92 FOR EACH ROW EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_78dcfec8 BEFORE TRUNCATE ON t_e965da92 FOR EACH STATEMENT EXECUTE FUNCTION f_c7e7f081();

CREATE FUNCTION f_45d645a4() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF f_17688ad3(NEW.c_be4084de) IS NULL THEN RAISE EXCEPTION 'a compensation journal is recorded inside the execution of its approved plan'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_c60dad1f BEFORE INSERT ON t_2f7e3dcb FOR EACH ROW EXECUTE FUNCTION f_45d645a4();
CREATE TRIGGER r_7b73034b BEFORE UPDATE OR DELETE ON t_2f7e3dcb FOR EACH ROW EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_148c5ac3 BEFORE TRUNCATE ON t_2f7e3dcb FOR EACH STATEMENT EXECUTE FUNCTION f_c7e7f081();

-- ---------------------------------------------------------------------------------------------------------------- the epoch member (design 3.2)
CREATE FUNCTION f_041a6ba2() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
    o record;
    v_original_kind smallint;
BEGIN
    v_plan := f_17688ad3(NEW.c_3c11cd4a);
    IF v_plan IS NULL THEN RAISE EXCEPTION 'an epoch member belongs to the execution, written in this transaction, of an approved compensation plan'; END IF;
    SELECT c_7f2ea712 AS kind, c_03a68cea AS reverses INTO o FROM public.t_a648ca40 WHERE c_f47f15da = NEW.c_85ee124a;
    IF NOT FOUND THEN RAISE EXCEPTION 'the operation of an epoch member does not exist'; END IF;
    -- epoch operations are never reversal operations (review L1)
    IF o.kind = 4 OR o.reverses IS NOT NULL THEN RAISE EXCEPTION 'a reversal operation is never an epoch member'; END IF;
    -- the operation is being built now: it has no entry yet and no obligation or settlement refers to it (an empty operation of an earlier transaction exists only for those, 0020)
    IF EXISTS (SELECT 1 FROM public.t_bc727c1d WHERE c_eb34b7d8 = NEW.c_85ee124a) THEN RAISE EXCEPTION 'an epoch member is an operation without entries yet'; END IF;
    IF EXISTS (SELECT 1 FROM public.t_b56b119d WHERE c_1872eef0 = NEW.c_85ee124a)
       OR EXISTS (SELECT 1 FROM public.t_d370b1d6 WHERE c_7208ba7b = NEW.c_85ee124a) THEN
        RAISE EXCEPTION 'an operation of a negative-stock obligation or settlement is never an epoch member';
    END IF;
    IF NEW.c_87c64961 IN (1, 3) THEN
        -- X5: no epoch on an epoch
        IF EXISTS (SELECT 1 FROM public.t_9083c68c m WHERE m.c_85ee124a = NEW.c_05f49ec6) THEN RAISE EXCEPTION 'plan_on_epoch_position: an epoch operation is never rewritten by another epoch'; END IF;
        SELECT c_7f2ea712 INTO v_original_kind FROM public.t_a648ca40 WHERE c_f47f15da = NEW.c_05f49ec6;
        IF v_original_kind IS DISTINCT FROM o.kind THEN RAISE EXCEPTION 'a storno or rebook operation keeps the kind of its original operation'; END IF;
        -- the entries of the original that the epoch takes back lie in the plan's closure: an original without any entry there is not rewritten by this plan
        IF NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e
                       JOIN public.t_a2903683 ps ON ps.c_6b4abdd9 = e.c_98321e7f
                       JOIN public.t_8cb0e5ef c ON c.c_5a28af19 = v_plan AND c.c_a6b7570c = ps.c_3380c61e
                            AND c.c_d413a7c5 = ps.c_f0a91938
                       WHERE e.c_eb34b7d8 = NEW.c_05f49ec6) THEN
            RAISE EXCEPTION 'the original operation of an epoch member has no entry in the closure of the plan';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_fde642fc BEFORE INSERT ON t_9083c68c FOR EACH ROW EXECUTE FUNCTION f_041a6ba2();
CREATE TRIGGER r_63ccdbe0 BEFORE UPDATE OR DELETE ON t_9083c68c FOR EACH ROW EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_77bdf748 BEFORE TRUNCATE ON t_9083c68c FOR EACH STATEMENT EXECUTE FUNCTION f_c7e7f081();

-- Lineage: original entry -> storno entry -> rebook entry, all of one execution of this transaction.
CREATE FUNCTION f_4139c7d3() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    oe record;
    se record;
    re record;
BEGIN
    IF f_17688ad3(NEW.c_60dbf0cd) IS NULL THEN RAISE EXCEPTION 'an epoch lineage row belongs to the execution, written in this transaction, of an approved compensation plan'; END IF;
    SELECT c_eb34b7d8 AS op, c_98321e7f AS pos, c_4105796c AS kind, c_e0cd1a14 AS dir, c_12665ffb AS d, c_dba27822 AS qty
      INTO oe FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_eefb4728;
    SELECT c_eb34b7d8 AS op, c_4f9e777a AS reverses INTO se FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_a4678c70;
    SELECT c_eb34b7d8 AS op, c_98321e7f AS pos, c_4105796c AS kind, c_e0cd1a14 AS dir, c_12665ffb AS d, c_dba27822 AS qty,
           c_4f9e777a AS reverses INTO re FROM public.t_bc727c1d WHERE c_ed61ae1a = NEW.c_b9f16b78;
    IF oe IS NULL OR se IS NULL OR re IS NULL THEN RAISE EXCEPTION 'the entries of an epoch lineage row exist'; END IF;
    IF se.reverses IS DISTINCT FROM NEW.c_eefb4728 THEN RAISE EXCEPTION 'the storno entry of a lineage row takes back its original entry'; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_9083c68c m WHERE m.c_85ee124a = se.op AND m.c_3c11cd4a = NEW.c_60dbf0cd AND m.c_87c64961 = 1
                   AND m.c_05f49ec6 = oe.op)
       OR NOT EXISTS (SELECT 1 FROM public.t_9083c68c m WHERE m.c_85ee124a = re.op AND m.c_3c11cd4a = NEW.c_60dbf0cd AND m.c_87c64961 = 3
                      AND m.c_05f49ec6 = oe.op) THEN
        RAISE EXCEPTION 'the storno and rebook entries of a lineage row belong to the storno and rebook members of the same execution and original operation';
    END IF;
    -- X2: the rebooked entry repeats its original on the same position, with the original date, kind, direction and quantity (only the amount is replayed)
    IF re.pos <> oe.pos OR re.kind <> oe.kind OR re.dir <> oe.dir OR re.d <> oe.d OR re.qty <> oe.qty OR re.reverses IS NOT NULL THEN
        RAISE EXCEPTION 'a rebook entry repeats its original entry on the same position and date';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d729bf9a BEFORE INSERT ON t_1028a48d FOR EACH ROW EXECUTE FUNCTION f_4139c7d3();
CREATE TRIGGER r_8003b501 BEFORE UPDATE OR DELETE ON t_1028a48d FOR EACH ROW EXECUTE FUNCTION f_c7e7f081();
CREATE TRIGGER r_890edfab BEFORE TRUNCATE ON t_1028a48d FOR EACH STATEMENT EXECUTE FUNCTION f_c7e7f081();

-- ---------------------------------------------------------------------------------------------------------------- the entry-level guard (design 3.3(1), (2), (2a), (3)); a NEW function and a NEW trigger
-- The position row is read FOR UPDATE: the costing engine already holds that lock when it appends an entry, so no new lock order arises. An ordinary entry takes no other lock here.
-- The position still holds the date of its previous entry at this point (the engine appends the entry first and moves the position afterwards).
CREATE FUNCTION f_52c8a673() RETURNS trigger LANGUAGE plpgsql AS $$
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
    SELECT c_87c64961 AS role, c_3c11cd4a AS execution, c_05f49ec6 AS original INTO m FROM public.t_9083c68c WHERE c_85ee124a = NEW.c_eb34b7d8;
    v_member := FOUND;
    IF v_member THEN
        -- every entry of an epoch member, back-dated or not, is written inside the execution of its approved plan, in this transaction
        v_plan := f_17688ad3(m.execution);
        IF v_plan IS NULL THEN RAISE EXCEPTION 'an entry of an epoch operation is written only inside the execution, in this transaction, of its approved compensation plan'; END IF;
        -- (2a) closure binding: the position of the entry (the position row decides, not the copied columns) is in the plan's approved closure
        IF NEW.c_9ee52677 <> ps.item OR NEW.c_0f65c03c <> ps.warehouse THEN RAISE EXCEPTION 'an entry of an epoch operation names the item and warehouse of its position'; END IF;
        IF NOT EXISTS (SELECT 1 FROM public.t_8cb0e5ef c WHERE c.c_5a28af19 = v_plan AND c.c_a6b7570c = ps.item
                       AND c.c_d413a7c5 = ps.warehouse) THEN
            RAISE EXCEPTION 'an entry of an epoch operation lies on a position of the approved closure of its plan';
        END IF;
        -- (3) X2: an Open fiscal period, read FOR SHARE so that a concurrent close cannot commit between this check and the commit (lock order: positions, then periods)
        SELECT c_0a0b7f19 INTO v_period FROM public.t_ae996edc
          WHERE c_8299fb3d <= NEW.c_12665ffb AND c_c0322bcf >= NEW.c_12665ffb FOR SHARE;
        IF NOT FOUND OR v_period <> 1 THEN RAISE EXCEPTION 'period_not_open: an entry of an epoch operation is dated in an Open fiscal period'; END IF;
        -- (3) no open negative-stock obligation on the position
        IF f_cf64ca57(ps.item, ps.warehouse) THEN RAISE EXCEPTION 'an epoch never touches a position with an open negative-stock obligation'; END IF;
        -- (3) X5: the position is not in the closure of another executed plan
        IF EXISTS (SELECT 1 FROM public.t_8cb0e5ef c JOIN public.t_e965da92 x ON x.c_81f8cb75 = c.c_5a28af19
                   WHERE c.c_a6b7570c = ps.item AND c.c_d413a7c5 = ps.warehouse AND c.c_5a28af19 <> v_plan) THEN
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
                IF EXISTS (SELECT 1 FROM public.t_9083c68c em WHERE em.c_85ee124a = v_revent) THEN
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
CREATE TRIGGER r_628f9022 BEFORE INSERT ON t_bc727c1d FOR EACH ROW EXECUTE FUNCTION f_52c8a673();

-- review M2 (b): at commit, the position of every entry written in the transaction has been moved at least to that entry (sequence number and last date). The engine moves the position with every entry and the executor
-- keeps the date with GREATEST, so neither path changes; a writer that appends an entry without moving its position, or moves it without the entry's date, can no longer leave the F1 anchor behind.
CREATE FUNCTION f_53b76fbf() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_a2903683 ps WHERE ps.c_6b4abdd9 = NEW.c_98321e7f AND ps.c_ad2af2e5 >= NEW.c_2affbce9
                   AND ps.c_3c880b93 >= NEW.c_12665ffb) THEN
        RAISE EXCEPTION 'position_not_moved: a cost entry is committed together with the move of its position to at least its sequence number and its date';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_ada856d0 AFTER INSERT ON t_bc727c1d DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_53b76fbf();

-- ---------------------------------------------------------------------------------------------------------------- X6 (design 3.3(4)): no reversal operation of an epoch member or of an operation with stornoed entries
CREATE FUNCTION f_bad61ffa() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_03a68cea IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM public.t_9083c68c m WHERE m.c_85ee124a = NEW.c_03a68cea)
           OR EXISTS (SELECT 1 FROM public.t_1028a48d l JOIN public.t_bc727c1d e ON e.c_ed61ae1a = l.c_eefb4728
                      WHERE e.c_eb34b7d8 = NEW.c_03a68cea) THEN
            RAISE EXCEPTION 'operation_rewritten_by_epoch: an operation rewritten by a replay epoch, or belonging to one, is never reversed';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_745aa703 BEFORE INSERT ON t_a648ca40 FOR EACH ROW EXECUTE FUNCTION f_bad61ffa();

-- ---------------------------------------------------------------------------------------------------------------- X7: a plan-bound stock document is posted only inside the execution of its plan
CREATE FUNCTION f_999b9ddf() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid;
BEGIN
    SELECT c_621d79c0 INTO v_plan FROM public.t_96484f97 WHERE c_09237930 = NEW.c_eb889fd0 AND c_0df673f9 IN (1, 2);
    IF FOUND AND NOT EXISTS (SELECT 1 FROM public.t_e965da92 x WHERE x.c_81f8cb75 = v_plan AND x.c_1994ea3f = pg_current_xact_id()
                             AND x.c_2f84a7ef = NEW.c_fc71418e) THEN
        RAISE EXCEPTION 'document_bound_to_plan: a stock document bound to a compensation plan is posted only by the execution of that plan';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d3f10f0f BEFORE INSERT ON t_4d4b251f FOR EACH ROW EXECUTE FUNCTION f_999b9ddf();

-- ---------------------------------------------------------------------------------------------------------------- deferred checks, judged at commit (design 3.4)
CREATE FUNCTION f_fc87a470() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p record;
    v_members bigint;
    v_events bigint;
BEGIN
    SELECT c_0df673f9 AS status, c_3ef7b3ba AS executed_xact, c_7bfde04e AS closure_hash, c_09237930 AS document INTO p
      FROM public.t_96484f97 WHERE c_621d79c0 = NEW.c_81f8cb75;
    -- the plan is executed, and was marked so in the transaction that wrote the execution (this one)
    IF p.status IS DISTINCT FROM 3 OR p.executed_xact IS DISTINCT FROM NEW.c_1994ea3f OR NEW.c_1994ea3f <> pg_current_xact_id() THEN
        RAISE EXCEPTION 'an execution is committed together with the executed status of its plan';
    END IF;
    IF f_401f2ced(NEW.c_81f8cb75) <> p.closure_hash THEN RAISE EXCEPTION 'the closure of the plan does not match its closure hash'; END IF;
    SELECT count(*), count(*) FILTER (WHERE c_87c64961 = 2) INTO v_members, v_events FROM public.t_9083c68c WHERE c_3c11cd4a = NEW.c_64aab6aa;
    IF v_members = 0 OR v_events <> 1 THEN RAISE EXCEPTION 'an execution has members and exactly one event'; END IF;
    -- the event member is the cost operation of the execution's stock posting, and that posting is the posting of the plan's document
    IF NOT EXISTS (SELECT 1 FROM public.t_9083c68c m JOIN public.t_4d4b251f sp ON sp.c_4ce1587f = m.c_85ee124a
                   WHERE m.c_3c11cd4a = NEW.c_64aab6aa AND m.c_87c64961 = 2 AND sp.c_fc71418e = NEW.c_2f84a7ef
                     AND sp.c_eb889fd0 = p.document) THEN
        RAISE EXCEPTION 'the event of an execution is the posting of the document of its plan';
    END IF;
    -- every storno member has its rebook member for the same original operation, and the reverse (each is unique per original by the index)
    IF EXISTS (SELECT 1 FROM public.t_9083c68c s WHERE s.c_3c11cd4a = NEW.c_64aab6aa AND s.c_87c64961 IN (1, 3)
               AND NOT EXISTS (SELECT 1 FROM public.t_9083c68c t WHERE t.c_3c11cd4a = s.c_3c11cd4a AND t.c_87c64961 = 4 - s.c_87c64961
                               AND t.c_05f49ec6 = s.c_05f49ec6)) THEN
        RAISE EXCEPTION 'every storno member has its rebook member for the same original operation';
    END IF;
    IF EXISTS (SELECT 1 FROM public.t_9083c68c m WHERE m.c_3c11cd4a = NEW.c_64aab6aa
               AND NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e WHERE e.c_eb34b7d8 = m.c_85ee124a)) THEN
        RAISE EXCEPTION 'every epoch member has at least one entry';
    END IF;
    -- every storno entry and every rebook entry has its lineage row
    IF EXISTS (SELECT 1 FROM public.t_9083c68c m JOIN public.t_bc727c1d e ON e.c_eb34b7d8 = m.c_85ee124a
               WHERE m.c_3c11cd4a = NEW.c_64aab6aa
                 AND ((m.c_87c64961 = 1 AND NOT EXISTS (SELECT 1 FROM public.t_1028a48d l WHERE l.c_a4678c70 = e.c_ed61ae1a AND l.c_60dbf0cd = m.c_3c11cd4a))
                   OR (m.c_87c64961 = 3 AND NOT EXISTS (SELECT 1 FROM public.t_1028a48d l WHERE l.c_b9f16b78 = e.c_ed61ae1a AND l.c_60dbf0cd = m.c_3c11cd4a)))) THEN
        RAISE EXCEPTION 'every storno and rebook entry has its lineage row';
    END IF;
    -- the business audit event of the execution: of the plan-executed type, with this plan as subject and this execution as target (0002 is not altered)
    IF NOT EXISTS (SELECT 1 FROM public.t_2db8cf1f a WHERE a.c_ef116a9d = NEW.c_31e9c559 AND a.c_4c6c6496 = 1154
                   AND a.c_629e971c = NEW.c_81f8cb75 AND a.c_deb363a5 = NEW.c_64aab6aa) THEN
        RAISE EXCEPTION 'an execution is committed together with its business audit event';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_b9337ccc AFTER INSERT ON t_e965da92 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_fc87a470();

-- orphan detection: no member without an execution of this transaction, and no member without entries
CREATE FUNCTION f_c25934c5() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.t_e965da92 x WHERE x.c_64aab6aa = NEW.c_3c11cd4a AND x.c_1994ea3f = pg_current_xact_id()) THEN
        RAISE EXCEPTION 'an epoch member is committed together with its execution';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.t_bc727c1d e WHERE e.c_eb34b7d8 = NEW.c_85ee124a) THEN RAISE EXCEPTION 'every epoch member has at least one entry'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_6f57c363 AFTER INSERT ON t_9083c68c DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_c25934c5();
