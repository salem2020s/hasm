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
