-- Company database migration 0008: General Ledger foundation (Phase 3.2B). Built in slices; this file is immutable once applied (checksummed).
-- Physical identifiers come from CompanySchema.Ledger* (generated, opaque). No journal entry, balance or posting exists in this slice, nothing is seeded,
-- and existing company data is neither read nor modified. Privileges come only from 'Hasm.Admin grant-company-runtime'.

-- ======================================================================================================================================
-- Slice S1: document numbering
-- ======================================================================================================================================
-- Ledger history (append-only): one row per audited configuration change of the general ledger foundation, linked to exactly one authenticated business
-- audit event written in the SAME transaction. entity: 1 numbering rule, 2 exchange rate (reserved), 3 cost center, 4 approval policy, 5 ledger settings.
-- entity_key is the natural key of the entity (a document type, or a uuid in text form).
CREATE TABLE t_6eec2227 (
    c_a7885cb2 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_03da32ec smallint NOT NULL,
    c_f1c80996 varchar(60) NOT NULL,
    c_10a3a331 bigint NOT NULL,
    c_1194b7b0 uuid NOT NULL,
    c_31315446 timestamptz NOT NULL,
    c_1826908c jsonb NOT NULL,
    c_e2b8b005 varchar(48) NOT NULL,
    c_d3212691 bigint NOT NULL,
    CONSTRAINT k_3c3e2427 PRIMARY KEY (c_a7885cb2),
    CONSTRAINT k_43abeb9e UNIQUE (c_03da32ec, c_f1c80996, c_10a3a331),
    CONSTRAINT k_5dd892cc UNIQUE (c_e2b8b005, c_d3212691),
    CONSTRAINT k_4a7c34e0 FOREIGN KEY (c_e2b8b005, c_d3212691) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_2d8d01ee CHECK (c_03da32ec BETWEEN 1 AND 5 AND c_10a3a331 > 0 AND jsonb_typeof(c_1826908c) = 'object')
);
CREATE FUNCTION f_0e4aded2() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'ledger history is append-only';
END;
$$;
CREATE TRIGGER r_652b3b09 BEFORE UPDATE OR DELETE ON t_6eec2227 FOR EACH ROW EXECUTE FUNCTION f_0e4aded2();
CREATE TRIGGER r_f403d7b8 BEFORE TRUNCATE ON t_6eec2227 FOR EACH STATEMENT EXECUTE FUNCTION f_0e4aded2();
REVOKE ALL ON TABLE t_6eec2227 FROM PUBLIC;

-- Numbering rules: one per document type, created on the first change (a read shows the documented default). The number is
-- prefix + [fiscal-year label + separator] + [branch code + separator] + zero-padded sequence (assembled by the service).
CREATE TABLE t_5fda8ef3 (
    c_095d6370 varchar(40) NOT NULL,
    c_0ac3602f varchar(20) NOT NULL DEFAULT '',
    c_1630474f varchar(3) NOT NULL DEFAULT '-',
    c_baec1230 smallint NOT NULL DEFAULT 6,
    c_80cf6dab boolean NOT NULL DEFAULT false,
    c_724258b6 boolean NOT NULL DEFAULT true,
    c_dd953656 bigint NOT NULL DEFAULT 1,
    c_837d82b7 timestamptz NOT NULL,
    CONSTRAINT k_259acd92 PRIMARY KEY (c_095d6370),
    CONSTRAINT k_5cd883dc CHECK (c_095d6370 ~ '^[a-z][a-z_]{0,39}$'),
    CONSTRAINT k_b6a2eebc CHECK (c_0ac3602f ~ '^[A-Za-z0-9._-]{0,20}$'),
    CONSTRAINT k_7f92f98a CHECK (c_1630474f ~ '^[A-Za-z0-9._/-]{0,3}$'),
    CONSTRAINT k_e7fc456c CHECK (c_baec1230 BETWEEN 1 AND 12),
    CONSTRAINT k_c979c3d4 CHECK (c_dd953656 > 0)
);
-- Counters: one per (document type, branch or none, fiscal year or none); the all-zero uuid stands for "none". A counter only ever advances by exactly one.
CREATE TABLE t_2dd9917f (
    c_21d1110f varchar(40) NOT NULL,
    c_708d4efd uuid NOT NULL,
    c_2e69fe44 uuid NOT NULL,
    c_9df284e3 bigint NOT NULL,
    CONSTRAINT k_a8d49511 PRIMARY KEY (c_21d1110f, c_708d4efd, c_2e69fe44),
    CONSTRAINT k_f2d24336 CHECK (c_9df284e3 >= 1)
);
-- Issued numbers (the issue ledger). Every number ever issued stays here: UNIQUE(document type, number) is what makes a voided number impossible to reuse.
CREATE TABLE t_738092a9 (
    c_a52512d6 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_547ade38 varchar(40) NOT NULL,
    c_39d67608 varchar(80) NOT NULL,
    c_53cb6b88 uuid NOT NULL,
    c_7aa75e94 uuid NOT NULL,
    c_8573021c bigint NOT NULL,
    c_9a091df7 uuid NOT NULL,
    c_dc70ea5c timestamptz NOT NULL,
    c_5dafa34c uuid NOT NULL,
    c_7b2ec8bb timestamptz NULL,
    c_f0eb68b6 uuid NULL,
    c_b02aa3e9 varchar(200) NULL,
    CONSTRAINT k_6fecea87 PRIMARY KEY (c_a52512d6),
    CONSTRAINT k_026e90ca UNIQUE (c_547ade38, c_39d67608),
    CONSTRAINT k_13a56f8e UNIQUE (c_547ade38, c_53cb6b88, c_7aa75e94, c_8573021c),
    CONSTRAINT k_0f947540 CHECK (c_8573021c >= 1),
    CONSTRAINT k_e850a063 CHECK ((c_7b2ec8bb IS NULL) = (c_f0eb68b6 IS NULL) AND (c_7b2ec8bb IS NULL) = (c_b02aa3e9 IS NULL))
);

CREATE FUNCTION f_d3834231() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'numbering rules cannot be deleted'; END IF;
    IF NEW.c_095d6370 IS DISTINCT FROM OLD.c_095d6370 THEN RAISE EXCEPTION 'the document type of a numbering rule is immutable'; END IF;
    IF NEW.c_dd953656 <> OLD.c_dd953656 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    -- The scope of the counters (per branch, per fiscal year) cannot change once a number of this type exists: it would restart the sequences.
    IF (NEW.c_80cf6dab IS DISTINCT FROM OLD.c_80cf6dab OR NEW.c_724258b6 IS DISTINCT FROM OLD.c_724258b6)
       AND EXISTS (SELECT 1 FROM t_738092a9 i WHERE i.c_547ade38 = OLD.c_095d6370) THEN
        RAISE EXCEPTION 'the numbering scope cannot change after numbers were issued';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_f46025ec BEFORE UPDATE OR DELETE ON t_5fda8ef3 FOR EACH ROW EXECUTE FUNCTION f_d3834231();
CREATE TRIGGER r_8535f654 BEFORE TRUNCATE ON t_5fda8ef3 FOR EACH STATEMENT EXECUTE FUNCTION f_d3834231();
REVOKE ALL ON TABLE t_5fda8ef3 FROM PUBLIC;

CREATE FUNCTION f_0fd105d1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'numbering counters cannot be deleted'; END IF;
    IF NEW.c_21d1110f IS DISTINCT FROM OLD.c_21d1110f OR NEW.c_708d4efd IS DISTINCT FROM OLD.c_708d4efd OR NEW.c_2e69fe44 IS DISTINCT FROM OLD.c_2e69fe44 THEN
        RAISE EXCEPTION 'the key of a numbering counter is immutable';
    END IF;
    IF NEW.c_9df284e3 <> OLD.c_9df284e3 + 1 THEN RAISE EXCEPTION 'a numbering counter only advances by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_43f2a9d6 BEFORE UPDATE OR DELETE ON t_2dd9917f FOR EACH ROW EXECUTE FUNCTION f_0fd105d1();
CREATE TRIGGER r_11acc643 BEFORE TRUNCATE ON t_2dd9917f FOR EACH STATEMENT EXECUTE FUNCTION f_0fd105d1();
-- A counter value exists only together with its issue record (checked at commit), so the sequence has no phantom values.
CREATE FUNCTION f_bfc637d5() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM t_738092a9 i WHERE i.c_547ade38 = NEW.c_21d1110f AND i.c_53cb6b88 = NEW.c_708d4efd AND i.c_7aa75e94 = NEW.c_2e69fe44 AND i.c_8573021c = NEW.c_9df284e3) THEN
        RAISE EXCEPTION 'a numbering counter value requires its issued number';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_267e6ad3 AFTER INSERT OR UPDATE ON t_2dd9917f DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_bfc637d5();
REVOKE ALL ON TABLE t_2dd9917f FROM PUBLIC;

CREATE FUNCTION f_ec66526a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'issued numbers cannot be deleted'; END IF;
    IF OLD.c_7b2ec8bb IS NOT NULL THEN RAISE EXCEPTION 'a voided number is final'; END IF;
    IF NEW.c_a52512d6 IS DISTINCT FROM OLD.c_a52512d6 OR NEW.c_547ade38 IS DISTINCT FROM OLD.c_547ade38 OR NEW.c_39d67608 IS DISTINCT FROM OLD.c_39d67608
       OR NEW.c_53cb6b88 IS DISTINCT FROM OLD.c_53cb6b88 OR NEW.c_7aa75e94 IS DISTINCT FROM OLD.c_7aa75e94 OR NEW.c_8573021c IS DISTINCT FROM OLD.c_8573021c
       OR NEW.c_9a091df7 IS DISTINCT FROM OLD.c_9a091df7 OR NEW.c_dc70ea5c IS DISTINCT FROM OLD.c_dc70ea5c OR NEW.c_5dafa34c IS DISTINCT FROM OLD.c_5dafa34c THEN
        RAISE EXCEPTION 'an issued number is immutable (it can only be voided)';
    END IF;
    IF NEW.c_7b2ec8bb IS NULL THEN RAISE EXCEPTION 'an issued number can only change by being voided'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_45b8c86a BEFORE UPDATE OR DELETE ON t_738092a9 FOR EACH ROW EXECUTE FUNCTION f_ec66526a();
CREATE TRIGGER r_563a795d BEFORE TRUNCATE ON t_738092a9 FOR EACH STATEMENT EXECUTE FUNCTION f_ec66526a();
REVOKE ALL ON TABLE t_738092a9 FROM PUBLIC;

-- ======================================================================================================================================
-- Slice S2: exchange rates
-- ======================================================================================================================================
-- A rate is "units of the BASE currency for ONE unit of the foreign currency" (numeric(20,10), strictly positive). The base currency has the implicit rate 1 and is
-- never stored. Rows are append-only: a correction is a NEW row for the same date and the latest row (highest id) for the latest valid_from <= the date is the
-- effective rate, so history is preserved and the choice is deterministic. A posted document stores its own applied-rate snapshot (rate id and value, slice S5);
-- changing rate master data therefore never recalculates posted amounts.
CREATE TABLE t_eba5b513 (
    c_e3463fc9 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_3fdf88c5 varchar(3) NOT NULL,
    c_515a2107 numeric(20,10) NOT NULL,
    c_94455323 date NOT NULL,
    c_fc1d8c8c timestamptz NOT NULL,
    c_1cb257f5 uuid NOT NULL,
    c_e2f9f81f varchar(200) NULL,
    c_340bec19 varchar(48) NOT NULL,
    c_48359c89 bigint NOT NULL,
    CONSTRAINT k_f1618a01 PRIMARY KEY (c_e3463fc9),
    CONSTRAINT k_53cc3679 FOREIGN KEY (c_3fdf88c5) REFERENCES t_451e91be (c_b6064cf1),
    CONSTRAINT k_25aaa623 UNIQUE (c_340bec19, c_48359c89),
    CONSTRAINT k_a38d200e FOREIGN KEY (c_340bec19, c_48359c89) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_71fa9285 CHECK (c_515a2107 > 0 AND c_94455323 BETWEEN DATE '2000-01-01' AND DATE '2100-12-31' AND (c_e2f9f81f IS NULL OR length(btrim(c_e2f9f81f)) > 0))
);
CREATE INDEX k_4770ec5e ON t_eba5b513 (c_3fdf88c5, c_94455323 DESC, c_e3463fc9 DESC);

CREATE FUNCTION f_6be4dcce() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'exchange rates are append-only (enter a new rate to correct one)';
END;
$$;
CREATE TRIGGER r_22a4dc76 BEFORE UPDATE OR DELETE ON t_eba5b513 FOR EACH ROW EXECUTE FUNCTION f_6be4dcce();
CREATE TRIGGER r_3e5844f1 BEFORE TRUNCATE ON t_eba5b513 FOR EACH STATEMENT EXECUTE FUNCTION f_6be4dcce();

-- A rate exists only for an ENABLED, NON-BASE company currency (the base currency is implicitly 1; a disabled currency cannot be used).
CREATE FUNCTION f_489b7b28() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM t_f5bc326a cc WHERE cc.c_6da45b20 = NEW.c_3fdf88c5 AND cc.c_702730ab AND NOT cc.c_5cdc1bc7) THEN
        RAISE EXCEPTION 'a rate needs an enabled foreign company currency';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_e25ae556 BEFORE INSERT ON t_eba5b513 FOR EACH ROW EXECUTE FUNCTION f_489b7b28();

-- The effective rate id for a currency on a date (NULL = no rate: the currency cannot be used without an authorized override). Used by the service and,
-- from slice S5, by the posting trigger, so both always choose the same rate.
CREATE FUNCTION f_56eae1de(p_currency varchar, p_date date) RETURNS bigint LANGUAGE sql STABLE AS $$
    SELECT c_e3463fc9 FROM t_eba5b513 WHERE c_3fdf88c5 = p_currency AND c_94455323 <= p_date ORDER BY c_94455323 DESC, c_e3463fc9 DESC LIMIT 1
$$;
REVOKE ALL ON TABLE t_eba5b513 FROM PUBLIC;

-- ======================================================================================================================================
-- Slice S3: cost centers
-- ======================================================================================================================================
-- Cost centers are an ORGANIZATIONAL-ANALYSIS tree that is separate from branches: a cost center MAY carry an optional branch link, but the two concepts are never
-- interchangeable and no rule here forces them to match (how a journal line's branch relates to a cost center's branch is decided at posting integration).
-- A GROUP cost center only organizes (it never receives allocations, so hierarchical totals never double count); a LEAF (non-group) one can receive allocations when
-- ACTIVE. Cost centers are never deleted; the stable identifier is the id; codes can be changed by a controlled, audited operation. Revisions are kept in the
-- ledger history table (entity 3).
CREATE TABLE t_939ae118 (
    c_0770cd8f uuid NOT NULL DEFAULT gen_random_uuid(),
    c_157be526 varchar(30) NOT NULL,
    c_9ce86e1c varchar(200) NULL,
    c_fdaf9504 varchar(200) NULL,
    c_28b981db uuid NULL,
    c_8d396764 uuid NULL,
    c_85d71877 boolean NOT NULL DEFAULT false,
    c_8a8af184 boolean NOT NULL DEFAULT true,
    c_0a692ced bigint NOT NULL DEFAULT 1,
    c_ab43e719 timestamptz NOT NULL,
    c_00a35896 timestamptz NOT NULL,
    CONSTRAINT k_0abaccee PRIMARY KEY (c_0770cd8f),
    CONSTRAINT k_b0879c0d FOREIGN KEY (c_28b981db) REFERENCES t_939ae118 (c_0770cd8f),
    CONSTRAINT k_2e043fa2 FOREIGN KEY (c_8d396764) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_192f90d0 CHECK (c_157be526 ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$'),
    CONSTRAINT k_4b3cb87b CHECK (
        (c_9ce86e1c IS NOT NULL OR c_fdaf9504 IS NOT NULL)
        AND (c_9ce86e1c IS NULL OR char_length(btrim(c_9ce86e1c)) BETWEEN 1 AND 200)
        AND (c_fdaf9504 IS NULL OR char_length(btrim(c_fdaf9504)) BETWEEN 1 AND 200)),
    CONSTRAINT k_b107bf76 CHECK (c_28b981db IS NULL OR c_28b981db <> c_0770cd8f),
    CONSTRAINT k_624f5166 CHECK (c_0a692ced > 0)
);
CREATE UNIQUE INDEX k_6268e18f ON t_939ae118 (upper(c_157be526));
CREATE INDEX k_d04065b3 ON t_939ae118 (c_28b981db);

CREATE FUNCTION f_0b7108b0() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cost centers cannot be deleted'; END IF;
    IF NEW.c_0770cd8f IS DISTINCT FROM OLD.c_0770cd8f THEN RAISE EXCEPTION 'cost center identifiers are immutable'; END IF;
    IF NEW.c_ab43e719 IS DISTINCT FROM OLD.c_ab43e719 THEN RAISE EXCEPTION 'cost center provenance is immutable'; END IF;
    IF NEW.c_0a692ced <> OLD.c_0a692ced + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_7f3a3310 BEFORE UPDATE OR DELETE ON t_939ae118 FOR EACH ROW EXECUTE FUNCTION f_0b7108b0();
CREATE TRIGGER r_820f2fa2 BEFORE TRUNCATE ON t_939ae118 FOR EACH STATEMENT EXECUTE FUNCTION f_0b7108b0();

-- Tree integrity judged at commit: the parent is an active-compatible GROUP, no cycles, a cost center with children is a group, active needs an active parent.
CREATE FUNCTION f_4a0f86c6() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_28b981db IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM t_939ae118 p WHERE p.c_0770cd8f = NEW.c_28b981db AND p.c_85d71877) THEN
            RAISE EXCEPTION 'the parent of a cost center must be a group cost center';
        END IF;
        IF NEW.c_8a8af184 AND EXISTS (SELECT 1 FROM t_939ae118 p WHERE p.c_0770cd8f = NEW.c_28b981db AND NOT p.c_8a8af184) THEN
            RAISE EXCEPTION 'an active cost center requires an active parent';
        END IF;
        IF EXISTS (
            WITH RECURSIVE up(id, parent) AS (
                SELECT a.c_0770cd8f, a.c_28b981db FROM t_939ae118 a WHERE a.c_0770cd8f = NEW.c_28b981db
                UNION
                SELECT a.c_0770cd8f, a.c_28b981db FROM t_939ae118 a JOIN up ON a.c_0770cd8f = up.parent)
            SELECT 1 FROM up WHERE up.id = NEW.c_0770cd8f) THEN
            RAISE EXCEPTION 'a cost center cannot be its own ancestor';
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM t_939ae118 c WHERE c.c_28b981db = NEW.c_0770cd8f) THEN
        IF NOT NEW.c_85d71877 THEN RAISE EXCEPTION 'a cost center with children must be a group cost center'; END IF;
        IF NOT NEW.c_8a8af184 AND EXISTS (SELECT 1 FROM t_939ae118 c WHERE c.c_28b981db = NEW.c_0770cd8f AND c.c_8a8af184) THEN
            RAISE EXCEPTION 'a cost center with an active child must stay active';
        END IF;
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_f46df562 AFTER INSERT OR UPDATE ON t_939ae118 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_4a0f86c6();
REVOKE ALL ON TABLE t_939ae118 FROM PUBLIC;

-- ======================================================================================================================================
-- Slice S4: approval policies and document approval decisions
-- ======================================================================================================================================
-- A policy says: documents of a type (and source: 1 manual, 2 automatic, 3 either), of one branch (or every branch), whose BASE-currency amount is at least a
-- threshold, need N distinct approvers. With NO matching active policy nothing requires approval (the company decides). When several policies match, the
-- STRICTEST (highest N) applies. One SQL function evaluates this, so the service and the posting trigger (slice S5) can never disagree; automatic documents are
-- subject to the same policies. Policies are never deleted (deactivate); editing one never alters recorded decisions.
CREATE TABLE t_8f2462c5 (
    c_a2b53e06 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_9c04c465 varchar(40) NOT NULL,
    c_b130782f smallint NOT NULL,
    c_5d70e226 uuid NULL,
    c_a0b367ec numeric(20,4) NOT NULL,
    c_a874380a smallint NOT NULL,
    c_2de53c75 varchar(200) NULL,
    c_e5bbb507 varchar(200) NULL,
    c_61d707fd boolean NOT NULL DEFAULT true,
    c_c9bff476 bigint NOT NULL DEFAULT 1,
    c_bc3b07c1 timestamptz NOT NULL,
    c_4fb325f1 timestamptz NOT NULL,
    CONSTRAINT k_202ebd22 PRIMARY KEY (c_a2b53e06),
    CONSTRAINT k_9d1f135b FOREIGN KEY (c_5d70e226) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_ecd43dd2 CHECK (c_9c04c465 ~ '^[a-z][a-z_]{0,39}$' AND c_b130782f IN (1, 2, 3) AND c_a0b367ec >= 0 AND c_a874380a BETWEEN 1 AND 5),
    CONSTRAINT k_511df159 CHECK (
        (c_2de53c75 IS NOT NULL OR c_e5bbb507 IS NOT NULL)
        AND (c_2de53c75 IS NULL OR char_length(btrim(c_2de53c75)) BETWEEN 1 AND 200)
        AND (c_e5bbb507 IS NULL OR char_length(btrim(c_e5bbb507)) BETWEEN 1 AND 200)),
    CONSTRAINT k_3c36b22b CHECK (c_c9bff476 > 0)
);
CREATE INDEX k_6d354b7e ON t_8f2462c5 (c_9c04c465) WHERE c_61d707fd;

CREATE FUNCTION f_25cf6e1b() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'approval policies cannot be deleted'; END IF;
    IF NEW.c_a2b53e06 IS DISTINCT FROM OLD.c_a2b53e06 OR NEW.c_9c04c465 IS DISTINCT FROM OLD.c_9c04c465 OR NEW.c_bc3b07c1 IS DISTINCT FROM OLD.c_bc3b07c1 THEN
        RAISE EXCEPTION 'the identity and document type of an approval policy are immutable';
    END IF;
    IF NEW.c_c9bff476 <> OLD.c_c9bff476 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_1fa261cf BEFORE UPDATE OR DELETE ON t_8f2462c5 FOR EACH ROW EXECUTE FUNCTION f_25cf6e1b();
CREATE TRIGGER r_0fceb20e BEFORE TRUNCATE ON t_8f2462c5 FOR EACH STATEMENT EXECUTE FUNCTION f_25cf6e1b();
REVOKE ALL ON TABLE t_8f2462c5 FROM PUBLIC;

-- Approval decisions (append-only). One decision per approver per document REVISION: editing a document creates a new revision, so earlier approvals no longer count.
-- decision 1 = approve, 2 = reject (a rejection needs a comment). The document is polymorphic (type + id), so it is not a foreign key.
CREATE TABLE t_3fa074e5 (
    c_6aeb80ff bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_9adee5c9 varchar(40) NOT NULL,
    c_175a20c9 uuid NOT NULL,
    c_f399e237 bigint NOT NULL,
    c_942d312c uuid NOT NULL,
    c_3a789977 smallint NOT NULL,
    c_9d1b7065 timestamptz NOT NULL,
    c_a3ecb6f1 varchar(500) NULL,
    c_17279abc varchar(48) NOT NULL,
    c_0d6e0bbc bigint NOT NULL,
    CONSTRAINT k_3b3caa4b PRIMARY KEY (c_6aeb80ff),
    CONSTRAINT k_d6960f83 UNIQUE (c_9adee5c9, c_175a20c9, c_f399e237, c_942d312c),
    CONSTRAINT k_e6168660 UNIQUE (c_17279abc, c_0d6e0bbc),
    CONSTRAINT k_0bfb0744 FOREIGN KEY (c_17279abc, c_0d6e0bbc) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_d0d8b4b2 CHECK (c_f399e237 > 0 AND c_3a789977 IN (1, 2)
        AND (c_a3ecb6f1 IS NULL OR char_length(btrim(c_a3ecb6f1)) > 0)
        AND (c_3a789977 = 1 OR c_a3ecb6f1 IS NOT NULL))
);
CREATE FUNCTION f_e2da8d13() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'approval decisions are append-only';
END;
$$;
CREATE TRIGGER r_2356bf28 BEFORE UPDATE OR DELETE ON t_3fa074e5 FOR EACH ROW EXECUTE FUNCTION f_e2da8d13();
CREATE TRIGGER r_518d224c BEFORE TRUNCATE ON t_3fa074e5 FOR EACH STATEMENT EXECUTE FUNCTION f_e2da8d13();
REVOKE ALL ON TABLE t_3fa074e5 FROM PUBLIC;

-- Number of approvals a document needs: the strictest matching active policy (0 = none configured / none matches).
CREATE FUNCTION f_660e4c73(p_doc varchar, p_source smallint, p_branch uuid, p_amount numeric) RETURNS integer LANGUAGE sql STABLE AS $$
    SELECT COALESCE(max(p.c_a874380a), 0)::integer FROM t_8f2462c5 p
    WHERE p.c_61d707fd AND p.c_9c04c465 = p_doc AND (p.c_b130782f = 3 OR p.c_b130782f = p_source)
      AND (p.c_5d70e226 IS NULL OR p.c_5d70e226 = p_branch) AND p_amount >= p.c_a0b367ec
$$;

-- True when the document REVISION has at least the required number of distinct approvers and nobody rejected it (0 required = always satisfied).
CREATE FUNCTION f_7cdbb48f(p_doc varchar, p_id uuid, p_rev bigint, p_required integer) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT p_required <= 0 OR (
        NOT EXISTS (SELECT 1 FROM t_3fa074e5 d WHERE d.c_9adee5c9 = p_doc AND d.c_175a20c9 = p_id AND d.c_f399e237 = p_rev AND d.c_3a789977 = 2)
        AND (SELECT count(DISTINCT d.c_942d312c) FROM t_3fa074e5 d WHERE d.c_9adee5c9 = p_doc AND d.c_175a20c9 = p_id AND d.c_f399e237 = p_rev AND d.c_3a789977 = 1) >= p_required)
$$;

-- ======================================================================================================================================
-- Slice S5: journal entries and the posting engine
-- ======================================================================================================================================
-- Invariants enforced by PostgreSQL (the application is never the only guard):
--   * a journal entry moves only along Draft(1) -> Pending approval(2) / Approved(3) / Cancelled(6); Pending -> Draft / Approved / Cancelled; Approved -> Draft / Posted(4) /
--     Cancelled; Posted -> Reversed(5). Posted, Reversed and Cancelled entries never change otherwise, and nothing is ever deleted;
--   * lines and allocations change only while the entry is a Draft; leaving Pending/Approved for Draft advances the document revision, so earlier approvals stop counting;
--   * Pending/Approved/Posted entries are balanced EXACTLY in base currency (sum of base debits = sum of base credits > 0), with at least two lines;
--   * posting requires: a number, the strictest matching approval policy satisfied for exactly this revision, an open (or authorized Soft Closed) fiscal period,
--     active postable accounts honoring their currency policy, enabled currencies, the effective exchange rate (or a recorded authorized override) and active leaf cost centers;
--   * a reversal mirrors every line of one posted entry (same accounts, currencies and rates, sides swapped) and the original becomes Reversed in the same transaction; because it only cancels earlier
--     postings it is exempt from the active-state and currency-policy checks of accounts, branches, currencies and cost centers (never from the period, approval and balance checks);
--   * a source key (automatic entries) is unique among non-cancelled entries, so a business event cannot be posted twice.

-- Ledger settings (singleton, created on the first write): the account that absorbs base-currency rounding differences of multi-currency entries.
CREATE TABLE t_d8f3ea94 (
    c_1b115900 boolean NOT NULL DEFAULT true,
    c_946b25e2 uuid NULL,
    c_5d6ede30 bigint NOT NULL DEFAULT 1,
    c_b500974f timestamptz NOT NULL,
    CONSTRAINT k_e98861e6 PRIMARY KEY (c_1b115900),
    CONSTRAINT k_050add4c CHECK (c_1b115900 AND c_5d6ede30 > 0),
    CONSTRAINT k_15a8f12e FOREIGN KEY (c_946b25e2) REFERENCES t_11d107a9 (c_29a86598)
);
CREATE FUNCTION f_164bf90a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'ledger settings cannot be deleted'; END IF;
    IF NEW.c_5d6ede30 <> OLD.c_5d6ede30 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d545f73e BEFORE UPDATE OR DELETE ON t_d8f3ea94 FOR EACH ROW EXECUTE FUNCTION f_164bf90a();
CREATE TRIGGER r_5beeca3c BEFORE TRUNCATE ON t_d8f3ea94 FOR EACH STATEMENT EXECUTE FUNCTION f_164bf90a();
REVOKE ALL ON TABLE t_d8f3ea94 FROM PUBLIC;

-- Journal entry header.
CREATE TABLE t_f3158869 (
    c_14f317c6 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_5337d7b9 varchar(80) NULL,
    c_4e4c6448 smallint NOT NULL DEFAULT 1,
    c_960dbf25 smallint NOT NULL DEFAULT 1,
    c_41c437ea smallint NOT NULL DEFAULT 1,
    c_9882fcfa varchar(100) NULL,
    c_4cb99522 date NOT NULL,
    c_a05818cd varchar(500) NULL,
    c_59a672dc uuid NOT NULL,
    c_429fabdf char(3) NOT NULL,
    c_225ac5cd numeric(20,4) NOT NULL DEFAULT 0,
    c_dfe6b7c5 bigint NOT NULL DEFAULT 1,
    c_2a51947a uuid NOT NULL,
    c_12ff75c5 timestamptz NOT NULL,
    c_f0e21870 timestamptz NOT NULL,
    c_41579554 uuid NULL,
    c_7c479dc6 timestamptz NULL,
    c_7e5c5517 uuid NULL,
    c_84bcfb07 timestamptz NULL,
    c_dc0c9baf uuid NULL,
    c_e5cc9a38 boolean NOT NULL DEFAULT false,
    c_e11acf50 uuid NULL,
    c_f509b5de uuid NULL,
    CONSTRAINT k_d9694054 PRIMARY KEY (c_14f317c6),
    CONSTRAINT k_4677dcab FOREIGN KEY (c_59a672dc) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_0e8f8ebe FOREIGN KEY (c_429fabdf) REFERENCES t_451e91be (c_b6064cf1),
    CONSTRAINT k_d69e8b1c FOREIGN KEY (c_dc0c9baf) REFERENCES t_ae996edc (c_d61cbb9c),
    CONSTRAINT k_7c9d7b49 FOREIGN KEY (c_e11acf50) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_ae9028b2 FOREIGN KEY (c_f509b5de) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_eb8d49c6 CHECK (
        c_4e4c6448 BETWEEN 1 AND 6 AND c_960dbf25 BETWEEN 1 AND 3 AND c_41c437ea IN (1, 2) AND c_dfe6b7c5 > 0 AND c_225ac5cd >= 0
        AND (c_41c437ea = 1 OR c_9882fcfa IS NOT NULL)
        AND (c_9882fcfa IS NULL OR char_length(btrim(c_9882fcfa)) BETWEEN 1 AND 100)
        AND (c_a05818cd IS NULL OR char_length(btrim(c_a05818cd)) BETWEEN 1 AND 500)
        AND ((c_960dbf25 = 2) = (c_e11acf50 IS NOT NULL)) AND ((c_960dbf25 = 3) = (c_f509b5de IS NOT NULL))
        AND ((c_4e4c6448 IN (4, 5)) = (c_5337d7b9 IS NOT NULL))
        AND ((c_4e4c6448 IN (4, 5)) = (c_7e5c5517 IS NOT NULL AND c_84bcfb07 IS NOT NULL AND c_dc0c9baf IS NOT NULL))
        AND (c_e5cc9a38 = false OR c_4e4c6448 IN (4, 5))
        AND c_4cb99522 BETWEEN DATE '2000-01-01' AND DATE '2100-12-31')
);
CREATE UNIQUE INDEX k_3218238f ON t_f3158869 (c_5337d7b9) WHERE c_5337d7b9 IS NOT NULL;
CREATE UNIQUE INDEX k_f559dc87 ON t_f3158869 (c_9882fcfa) WHERE c_9882fcfa IS NOT NULL AND c_4e4c6448 <> 6;
CREATE UNIQUE INDEX k_dc49cb81 ON t_f3158869 (c_e11acf50) WHERE c_e11acf50 IS NOT NULL AND c_4e4c6448 <> 6;
CREATE INDEX k_ac858588 ON t_f3158869 (c_4cb99522, c_4e4c6448);
CREATE INDEX k_4e35cc09 ON t_f3158869 (c_4e4c6448, c_59a672dc);

-- Journal line. Amounts are in the line currency; the base amounts are the converted, rounded amounts and are fixed here at save time together with the applied rate
-- (value, id and, for an authorized override, the reason), so posted amounts never depend on later rate changes.
CREATE TABLE t_783c8d98 (
    c_5ffa4fb6 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_39596059 uuid NOT NULL,
    c_d6ebd9a0 integer NOT NULL,
    c_e6bfb072 uuid NOT NULL,
    c_31339018 varchar(500) NULL,
    c_7a2c36af char(3) NOT NULL,
    c_d0e33fe9 numeric(20,4) NOT NULL,
    c_a1befb78 numeric(20,4) NOT NULL,
    c_62c9d327 numeric(20,10) NOT NULL,
    c_2fc5b902 bigint NULL,
    c_7931ab9f varchar(200) NULL,
    c_c560d388 numeric(20,4) NOT NULL,
    c_03200b86 numeric(20,4) NOT NULL,
    c_147d3982 uuid NOT NULL,
    c_781e3043 boolean NOT NULL DEFAULT false,
    c_b75ddd12 uuid NULL,
    CONSTRAINT k_397c59a3 PRIMARY KEY (c_5ffa4fb6),
    CONSTRAINT k_55c6626a FOREIGN KEY (c_39596059) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_c91cc298 FOREIGN KEY (c_e6bfb072) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_d98922ee FOREIGN KEY (c_7a2c36af) REFERENCES t_451e91be (c_b6064cf1),
    CONSTRAINT k_be9adc08 FOREIGN KEY (c_2fc5b902) REFERENCES t_eba5b513 (c_e3463fc9),
    CONSTRAINT k_0bd82149 FOREIGN KEY (c_147d3982) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_f475ca55 FOREIGN KEY (c_b75ddd12) REFERENCES t_783c8d98 (c_5ffa4fb6),
    CONSTRAINT k_bac36c16 UNIQUE (c_39596059, c_d6ebd9a0),
    CONSTRAINT k_433f8d1c CHECK (
        c_d6ebd9a0 > 0 AND c_d0e33fe9 >= 0 AND c_a1befb78 >= 0 AND (c_d0e33fe9 = 0) <> (c_a1befb78 = 0)
        AND c_c560d388 >= 0 AND c_03200b86 >= 0 AND (c_c560d388 = 0) <> (c_03200b86 = 0)
        AND (c_d0e33fe9 > 0) = (c_c560d388 > 0) AND c_62c9d327 > 0
        AND (c_2fc5b902 IS NULL OR c_7931ab9f IS NULL)
        AND (c_7931ab9f IS NULL OR char_length(btrim(c_7931ab9f)) BETWEEN 5 AND 200)
        AND (c_31339018 IS NULL OR char_length(btrim(c_31339018)) BETWEEN 1 AND 500))
);
CREATE INDEX k_76363ffa ON t_783c8d98 (c_e6bfb072);

-- Cost-center allocation of a line: amounts in the line currency AND in base currency; each set sums EXACTLY to the line (checked at commit).
CREATE TABLE t_58417560 (
    c_55521d90 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_0146172b uuid NOT NULL,
    c_71376e84 uuid NOT NULL,
    c_f811132c numeric(20,4) NOT NULL,
    c_aacf931e numeric(20,4) NOT NULL,
    CONSTRAINT k_7e2bee77 PRIMARY KEY (c_55521d90),
    CONSTRAINT k_0de6a859 FOREIGN KEY (c_0146172b) REFERENCES t_783c8d98 (c_5ffa4fb6),
    CONSTRAINT k_9226c56b FOREIGN KEY (c_71376e84) REFERENCES t_939ae118 (c_0770cd8f),
    CONSTRAINT k_80d4e1cf UNIQUE (c_0146172b, c_71376e84),
    CONSTRAINT k_0fbdfaad CHECK (c_f811132c > 0 AND c_aacf931e > 0)
);

-- Journal event log (append-only): every state change of an entry, with its audit event.
-- event: 1 created, 2 updated, 3 submitted, 4 returned to draft, 5 cancelled, 6 approved (entry), 7 rejected, 8 posted, 9 reversal drafted, 10 adjusting drafted, 11 reversed (original), 12 decision recorded.
CREATE TABLE t_b6491d4d (
    c_f8942dfa bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_27d80de0 uuid NOT NULL,
    c_e9c92b2a smallint NOT NULL,
    c_9dd85d91 uuid NOT NULL,
    c_8b0a36c1 timestamptz NOT NULL,
    c_39a332da bigint NOT NULL,
    c_a8566788 varchar(300) NULL,
    c_4fae4433 varchar(48) NOT NULL,
    c_90ad2c77 bigint NOT NULL,
    CONSTRAINT k_d449a216 PRIMARY KEY (c_f8942dfa),
    CONSTRAINT k_64dc210e FOREIGN KEY (c_27d80de0) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_30d47901 UNIQUE (c_4fae4433, c_90ad2c77),
    CONSTRAINT k_f04d6c2f FOREIGN KEY (c_4fae4433, c_90ad2c77) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_2e340a81 CHECK (c_e9c92b2a BETWEEN 1 AND 12 AND c_39a332da > 0)
);
CREATE FUNCTION f_1669dc9b() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'the journal event log is append-only';
END;
$$;
CREATE TRIGGER r_c48bbad2 BEFORE UPDATE OR DELETE ON t_b6491d4d FOR EACH ROW EXECUTE FUNCTION f_1669dc9b();
CREATE TRIGGER r_ff06334f BEFORE TRUNCATE ON t_b6491d4d FOR EACH STATEMENT EXECUTE FUNCTION f_1669dc9b();

-- ------------------------------------------------------------------------------------------------------------------ posting-grade validation of one entry
-- Used for the transitions to Pending/Approved (p_post = false) and Posted (p_post = true). Lines cannot change once the entry leaves Draft, so the answer is stable.
CREATE FUNCTION f_ba4c6a0d(e t_f3158869, p_post boolean) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_base char(3);
    v_lines integer; v_deb numeric; v_cre numeric; v_req integer; v_orig t_f3158869; v_status smallint; v_period uuid; v_pstatus smallint;
BEGIN
    SELECT cc.c_6da45b20 INTO v_base FROM t_f5bc326a cc WHERE cc.c_5cdc1bc7 AND cc.c_702730ab;
    IF v_base IS NULL OR v_base <> e.c_429fabdf THEN RAISE EXCEPTION 'the entry base currency is not the company base currency'; END IF;

    SELECT count(*), COALESCE(sum(l.c_c560d388), 0), COALESCE(sum(l.c_03200b86), 0) INTO v_lines, v_deb, v_cre FROM t_783c8d98 l WHERE l.c_39596059 = e.c_14f317c6;
    IF v_lines < 2 THEN RAISE EXCEPTION 'a journal entry needs at least two lines'; END IF;
    IF v_deb <> v_cre OR v_deb <= 0 THEN RAISE EXCEPTION 'the entry is not balanced in base currency'; END IF;
    IF e.c_225ac5cd <> v_deb THEN RAISE EXCEPTION 'the recorded base total does not match the lines'; END IF;

    -- Currencies enabled for the company; base-currency lines are exact; foreign lines carry a rate and a base amount consistent with their own fields.
    IF e.c_960dbf25 <> 2 AND EXISTS (SELECT 1 FROM t_783c8d98 l WHERE l.c_39596059 = e.c_14f317c6 AND NOT EXISTS (SELECT 1 FROM t_f5bc326a cc WHERE cc.c_6da45b20 = l.c_7a2c36af AND cc.c_702730ab)) THEN
        RAISE EXCEPTION 'a line currency is not enabled for the company';
    END IF;
    IF EXISTS (SELECT 1 FROM t_783c8d98 l WHERE l.c_39596059 = e.c_14f317c6 AND l.c_7a2c36af = v_base
               AND (l.c_62c9d327 <> 1 OR l.c_2fc5b902 IS NOT NULL OR l.c_7931ab9f IS NOT NULL OR l.c_c560d388 <> l.c_d0e33fe9 OR l.c_03200b86 <> l.c_a1befb78)) THEN
        RAISE EXCEPTION 'a base-currency line must use rate 1 and equal base amounts';
    END IF;
    IF EXISTS (SELECT 1 FROM t_783c8d98 l WHERE l.c_39596059 = e.c_14f317c6 AND l.c_7a2c36af <> v_base AND l.c_2fc5b902 IS NULL AND l.c_7931ab9f IS NULL) THEN
        RAISE EXCEPTION 'a foreign-currency line needs an effective rate or a recorded override reason';
    END IF;
    IF EXISTS (SELECT 1 FROM t_783c8d98 l JOIN t_eba5b513 x ON x.c_e3463fc9 = l.c_2fc5b902 WHERE l.c_39596059 = e.c_14f317c6 AND (x.c_3fdf88c5 <> l.c_7a2c36af OR x.c_515a2107 <> l.c_62c9d327)) THEN
        RAISE EXCEPTION 'the applied rate does not match its rate record';
    END IF;

    -- Accounts: active, postable (not a group), currency policy respected.
    IF EXISTS (SELECT 1 FROM t_783c8d98 l JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 WHERE l.c_39596059 = e.c_14f317c6 AND ((NOT a.c_9761052f AND e.c_960dbf25 <> 2) OR a.c_57d34c8a)) THEN
        RAISE EXCEPTION 'every account must be active and postable';
    END IF;
    IF EXISTS (SELECT 1 FROM t_783c8d98 l JOIN t_11d107a9 a ON a.c_29a86598 = l.c_e6bfb072 WHERE l.c_39596059 = e.c_14f317c6
               AND e.c_960dbf25 <> 2 AND ((a.c_dfab26fd = 1 AND l.c_7a2c36af <> v_base) OR (a.c_dfab26fd = 3 AND l.c_7a2c36af <> a.c_76c4ece0))) THEN
        RAISE EXCEPTION 'a line currency violates the currency policy of its account';
    END IF;
    IF e.c_960dbf25 <> 2 AND EXISTS (SELECT 1 FROM t_783c8d98 l JOIN t_8ba6c27d b ON b.c_358158d2 = l.c_147d3982 WHERE l.c_39596059 = e.c_14f317c6 AND NOT b.c_6605fbbc) THEN
        RAISE EXCEPTION 'every line branch must be active';
    END IF;
    -- Allocations: complete sums (also checked at commit) and only active leaf cost centers.
    IF EXISTS (SELECT 1 FROM t_58417560 al JOIN t_783c8d98 l ON l.c_5ffa4fb6 = al.c_0146172b JOIN t_939ae118 c ON c.c_0770cd8f = al.c_71376e84 WHERE l.c_39596059 = e.c_14f317c6 AND (c.c_85d71877 OR (NOT c.c_8a8af184 AND e.c_960dbf25 <> 2))) THEN
        RAISE EXCEPTION 'allocations may only target active leaf cost centers';
    END IF;

    -- A reversal mirrors exactly the lines of one posted entry; an adjusting entry references a posted one.
    IF e.c_960dbf25 = 2 THEN
        SELECT * INTO v_orig FROM t_f3158869 WHERE c_14f317c6 = e.c_e11acf50;
        IF v_orig.c_4e4c6448 NOT IN (4, 5) THEN RAISE EXCEPTION 'only a posted entry can be reversed'; END IF;
        IF (SELECT count(*) FROM t_783c8d98 WHERE c_39596059 = v_orig.c_14f317c6) <> v_lines THEN RAISE EXCEPTION 'a reversal must mirror every line of the original'; END IF;
        IF EXISTS (SELECT 1 FROM t_783c8d98 l LEFT JOIN t_783c8d98 o ON o.c_5ffa4fb6 = l.c_b75ddd12 AND o.c_39596059 = v_orig.c_14f317c6
                   WHERE l.c_39596059 = e.c_14f317c6 AND (o.c_5ffa4fb6 IS NULL OR o.c_e6bfb072 <> l.c_e6bfb072 OR o.c_7a2c36af <> l.c_7a2c36af OR o.c_62c9d327 <> l.c_62c9d327
                       OR o.c_2fc5b902 IS DISTINCT FROM l.c_2fc5b902 OR o.c_d0e33fe9 <> l.c_a1befb78 OR o.c_a1befb78 <> l.c_d0e33fe9
                       OR o.c_c560d388 <> l.c_03200b86 OR o.c_03200b86 <> l.c_c560d388)) THEN
            RAISE EXCEPTION 'a reversal line must mirror its original line';
        END IF;
        IF (SELECT count(DISTINCT c_b75ddd12) FROM t_783c8d98 WHERE c_39596059 = e.c_14f317c6) <> v_lines THEN RAISE EXCEPTION 'every original line must be reversed exactly once'; END IF;
    ELSE
        IF EXISTS (SELECT 1 FROM t_783c8d98 WHERE c_39596059 = e.c_14f317c6 AND c_b75ddd12 IS NOT NULL) THEN RAISE EXCEPTION 'only a reversal may reference original lines'; END IF;
        IF e.c_960dbf25 = 3 THEN
            SELECT c_4e4c6448 INTO v_status FROM t_f3158869 WHERE c_14f317c6 = e.c_f509b5de;
            IF v_status NOT IN (4, 5) THEN RAISE EXCEPTION 'an adjusting entry must reference a posted entry'; END IF;
        END IF;
        -- The applied rate must be the CURRENT effective rate for the entry date (a newer rate entered since the draft was saved forces a refresh) unless it is a recorded override.
        IF p_post AND EXISTS (SELECT 1 FROM t_783c8d98 l WHERE l.c_39596059 = e.c_14f317c6 AND l.c_2fc5b902 IS NOT NULL AND l.c_2fc5b902 IS DISTINCT FROM f_56eae1de(l.c_7a2c36af, e.c_4cb99522)) THEN
            RAISE EXCEPTION 'the applied exchange rate is no longer the effective rate';
        END IF;
    END IF;

    IF p_post THEN
        v_req := f_660e4c73('journal_entry', e.c_41c437ea, e.c_59a672dc, e.c_225ac5cd);
        IF NOT f_7cdbb48f('journal_entry', e.c_14f317c6, e.c_dfe6b7c5, v_req) THEN RAISE EXCEPTION 'the required approvals for this revision are missing'; END IF;
        SELECT fp.c_d61cbb9c, fp.c_0a0b7f19 INTO v_period, v_pstatus FROM t_ae996edc fp WHERE e.c_4cb99522 BETWEEN fp.c_8299fb3d AND fp.c_c0322bcf FOR SHARE;
        IF v_period IS NULL THEN RAISE EXCEPTION 'the entry date is outside every fiscal period'; END IF;
        IF v_pstatus = 3 THEN RAISE EXCEPTION 'the fiscal period is final closed'; END IF;
        IF v_pstatus = 2 AND NOT e.c_e5cc9a38 THEN RAISE EXCEPTION 'the fiscal period is soft closed'; END IF;
        IF v_pstatus = 1 AND e.c_e5cc9a38 THEN RAISE EXCEPTION 'the soft-closed flag is only valid for a soft-closed period'; END IF;
        IF e.c_dc0c9baf IS DISTINCT FROM v_period THEN RAISE EXCEPTION 'the recorded fiscal period is not the period of the entry date'; END IF;
    END IF;
END;
$$;

-- Header guard: transitions, immutability, revision rules and the posting checks.
CREATE FUNCTION f_2dc99411() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'journal entries cannot be deleted'; END IF;
    IF NEW.c_14f317c6 IS DISTINCT FROM OLD.c_14f317c6 OR NEW.c_2a51947a IS DISTINCT FROM OLD.c_2a51947a OR NEW.c_12ff75c5 IS DISTINCT FROM OLD.c_12ff75c5
       OR NEW.c_960dbf25 IS DISTINCT FROM OLD.c_960dbf25 OR NEW.c_41c437ea IS DISTINCT FROM OLD.c_41c437ea OR NEW.c_9882fcfa IS DISTINCT FROM OLD.c_9882fcfa
       OR NEW.c_429fabdf IS DISTINCT FROM OLD.c_429fabdf OR NEW.c_e11acf50 IS DISTINCT FROM OLD.c_e11acf50 OR NEW.c_f509b5de IS DISTINCT FROM OLD.c_f509b5de THEN
        RAISE EXCEPTION 'the identity, provenance and references of a journal entry are immutable';
    END IF;
    IF OLD.c_4e4c6448 = 4 AND NEW.c_4e4c6448 = 5 THEN
        -- A posted entry only becomes Reversed; nothing else about it changes.
        IF NEW.c_5337d7b9 IS DISTINCT FROM OLD.c_5337d7b9 OR NEW.c_4cb99522 IS DISTINCT FROM OLD.c_4cb99522 OR NEW.c_a05818cd IS DISTINCT FROM OLD.c_a05818cd
           OR NEW.c_59a672dc IS DISTINCT FROM OLD.c_59a672dc OR NEW.c_225ac5cd IS DISTINCT FROM OLD.c_225ac5cd OR NEW.c_dfe6b7c5 IS DISTINCT FROM OLD.c_dfe6b7c5
           OR NEW.c_7e5c5517 IS DISTINCT FROM OLD.c_7e5c5517 OR NEW.c_84bcfb07 IS DISTINCT FROM OLD.c_84bcfb07 OR NEW.c_dc0c9baf IS DISTINCT FROM OLD.c_dc0c9baf
           OR NEW.c_e5cc9a38 IS DISTINCT FROM OLD.c_e5cc9a38 OR NEW.c_41579554 IS DISTINCT FROM OLD.c_41579554 OR NEW.c_7c479dc6 IS DISTINCT FROM OLD.c_7c479dc6 THEN
            RAISE EXCEPTION 'a posted journal entry is immutable';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.c_4e4c6448 IN (4, 5, 6) THEN RAISE EXCEPTION 'a posted, reversed or cancelled journal entry is immutable'; END IF;
    IF NOT ((OLD.c_4e4c6448 = 1 AND NEW.c_4e4c6448 IN (1, 2, 3, 6)) OR (OLD.c_4e4c6448 = 2 AND NEW.c_4e4c6448 IN (1, 3, 6)) OR (OLD.c_4e4c6448 = 3 AND NEW.c_4e4c6448 IN (1, 4, 6))) THEN
        RAISE EXCEPTION 'this journal entry state change is not allowed';
    END IF;
    -- Revision: +1 on every Draft content change and on every return to Draft (so earlier approvals no longer count); unchanged otherwise.
    IF NEW.c_dfe6b7c5 <> OLD.c_dfe6b7c5 THEN
        IF NEW.c_dfe6b7c5 <> OLD.c_dfe6b7c5 + 1 OR NEW.c_4e4c6448 <> 1 THEN RAISE EXCEPTION 'the revision may only advance by one on a draft'; END IF;
    ELSIF OLD.c_4e4c6448 IN (2, 3) AND NEW.c_4e4c6448 = 1 THEN
        RAISE EXCEPTION 'returning an entry to draft must advance its revision';
    END IF;
    IF OLD.c_4e4c6448 IN (2, 3) AND NEW.c_4e4c6448 IN (2, 3, 4, 6) AND (NEW.c_4cb99522 IS DISTINCT FROM OLD.c_4cb99522 OR NEW.c_a05818cd IS DISTINCT FROM OLD.c_a05818cd
       OR NEW.c_59a672dc IS DISTINCT FROM OLD.c_59a672dc OR NEW.c_225ac5cd IS DISTINCT FROM OLD.c_225ac5cd) THEN
        RAISE EXCEPTION 'the content of a submitted journal entry cannot change';
    END IF;
    IF NEW.c_4e4c6448 <> 4 AND (NEW.c_5337d7b9 IS NOT NULL OR NEW.c_7e5c5517 IS NOT NULL OR NEW.c_dc0c9baf IS NOT NULL OR NEW.c_e5cc9a38) THEN
        RAISE EXCEPTION 'posting fields are set only when the entry is posted';
    END IF;
    IF NEW.c_4e4c6448 IN (2, 3) AND OLD.c_4e4c6448 = 1 THEN
        IF NEW.c_41579554 IS NULL OR NEW.c_7c479dc6 IS NULL THEN RAISE EXCEPTION 'a submission records its submitter'; END IF;
    END IF;
    IF NEW.c_4e4c6448 IN (2, 3, 4) THEN PERFORM f_ba4c6a0d(NEW, false); END IF;
    IF NEW.c_4e4c6448 = 3 THEN
        IF NOT f_7cdbb48f('journal_entry', NEW.c_14f317c6, NEW.c_dfe6b7c5, f_660e4c73('journal_entry', NEW.c_41c437ea, NEW.c_59a672dc, NEW.c_225ac5cd)) THEN
            RAISE EXCEPTION 'the required approvals for this revision are missing';
        END IF;
    END IF;
    IF NEW.c_4e4c6448 = 4 THEN
        IF NEW.c_5337d7b9 IS NULL THEN RAISE EXCEPTION 'a posted entry needs its official number'; END IF;
        PERFORM f_ba4c6a0d(NEW, true);
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_bb0333e0 BEFORE UPDATE OR DELETE ON t_f3158869 FOR EACH ROW EXECUTE FUNCTION f_2dc99411();
CREATE TRIGGER r_54fc2d18 BEFORE TRUNCATE ON t_f3158869 FOR EACH STATEMENT EXECUTE FUNCTION f_2dc99411();

-- At commit: a posted reversal requires its original to be Reversed, and a Reversed entry requires its posted reversal.
CREATE FUNCTION f_47233bbd() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_4e4c6448 = 4 AND NEW.c_960dbf25 = 2 THEN
        IF NOT EXISTS (SELECT 1 FROM t_f3158869 o WHERE o.c_14f317c6 = NEW.c_e11acf50 AND o.c_4e4c6448 = 5) THEN RAISE EXCEPTION 'posting a reversal marks the original entry as reversed'; END IF;
    END IF;
    IF NEW.c_4e4c6448 = 5 THEN
        IF NOT EXISTS (SELECT 1 FROM t_f3158869 r WHERE r.c_e11acf50 = NEW.c_14f317c6 AND r.c_4e4c6448 = 4) THEN RAISE EXCEPTION 'an entry is reversed only by a posted reversal'; END IF;
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_fa0e9ea3 AFTER INSERT OR UPDATE ON t_f3158869 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_47233bbd();
REVOKE ALL ON TABLE t_f3158869 FROM PUBLIC;

-- Lines: only while the parent entry is a Draft; never deleted outside a draft edit; identity of the parent never changes.
CREATE FUNCTION f_f9323807() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status smallint; v_entry uuid;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.c_39596059 IS DISTINCT FROM OLD.c_39596059 THEN RAISE EXCEPTION 'a journal line cannot move to another entry'; END IF;
    v_entry := CASE WHEN TG_OP = 'DELETE' THEN OLD.c_39596059 ELSE NEW.c_39596059 END;
    SELECT c_4e4c6448 INTO v_status FROM t_f3158869 WHERE c_14f317c6 = v_entry;
    IF v_status IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'journal lines can only change while the entry is a draft'; END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;
CREATE TRIGGER r_9f0254e3 BEFORE INSERT OR UPDATE OR DELETE ON t_783c8d98 FOR EACH ROW EXECUTE FUNCTION f_f9323807();
CREATE FUNCTION f_ad8bda4d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'journal lines cannot be truncated';
END;
$$;
CREATE TRIGGER r_bab00893 BEFORE TRUNCATE ON t_783c8d98 FOR EACH STATEMENT EXECUTE FUNCTION f_ad8bda4d();
CREATE TRIGGER r_f53a91f3 BEFORE TRUNCATE ON t_58417560 FOR EACH STATEMENT EXECUTE FUNCTION f_ad8bda4d();
REVOKE ALL ON TABLE t_783c8d98 FROM PUBLIC;

CREATE FUNCTION f_b6ad8e29() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status smallint; v_line uuid;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.c_0146172b IS DISTINCT FROM OLD.c_0146172b THEN RAISE EXCEPTION 'an allocation cannot move to another line'; END IF;
    v_line := CASE WHEN TG_OP = 'DELETE' THEN OLD.c_0146172b ELSE NEW.c_0146172b END;
    SELECT e.c_4e4c6448 INTO v_status FROM t_783c8d98 l JOIN t_f3158869 e ON e.c_14f317c6 = l.c_39596059 WHERE l.c_5ffa4fb6 = v_line;
    IF v_status IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'allocations can only change while the entry is a draft'; END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;
CREATE TRIGGER r_0c17708a BEFORE INSERT OR UPDATE OR DELETE ON t_58417560 FOR EACH ROW EXECUTE FUNCTION f_b6ad8e29();

-- At commit: the allocations of a line sum EXACTLY to the line amount and to the line base amount (a line without allocations is allowed).
CREATE FUNCTION f_b79be4c8() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_line uuid; n integer; s numeric; sb numeric; l t_783c8d98;
BEGIN
    v_line := CASE WHEN TG_OP = 'DELETE' THEN OLD.c_0146172b ELSE NEW.c_0146172b END;
    SELECT * INTO l FROM t_783c8d98 WHERE c_5ffa4fb6 = v_line;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT count(*), COALESCE(sum(c_f811132c), 0), COALESCE(sum(c_aacf931e), 0) INTO n, s, sb FROM t_58417560 WHERE c_0146172b = v_line;
    IF n > 0 AND (s <> l.c_d0e33fe9 + l.c_a1befb78 OR sb <> l.c_c560d388 + l.c_03200b86) THEN RAISE EXCEPTION 'the allocations of a line must sum exactly to the line amount'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_787dfce2 AFTER INSERT OR UPDATE OR DELETE ON t_58417560 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_b79be4c8();
CREATE FUNCTION f_81b496df() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_58417560 a WHERE a.c_0146172b = NEW.c_5ffa4fb6 GROUP BY a.c_0146172b
               HAVING sum(a.c_f811132c) <> NEW.c_d0e33fe9 + NEW.c_a1befb78 OR sum(a.c_aacf931e) <> NEW.c_c560d388 + NEW.c_03200b86) THEN
        RAISE EXCEPTION 'the allocations of a line must sum exactly to the line amount';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_aee5d9ca AFTER UPDATE ON t_783c8d98 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
    WHEN (OLD.c_d0e33fe9 IS DISTINCT FROM NEW.c_d0e33fe9 OR OLD.c_a1befb78 IS DISTINCT FROM NEW.c_a1befb78 OR OLD.c_c560d388 IS DISTINCT FROM NEW.c_c560d388 OR OLD.c_03200b86 IS DISTINCT FROM NEW.c_03200b86)
    EXECUTE FUNCTION f_81b496df();
REVOKE ALL ON TABLE t_58417560 FROM PUBLIC;
REVOKE ALL ON TABLE t_b6491d4d FROM PUBLIC;
