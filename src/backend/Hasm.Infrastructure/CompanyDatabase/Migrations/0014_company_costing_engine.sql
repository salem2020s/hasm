-- Migration 0014: inventory costing engine (Phase 3.6). Forward-only, no DROP, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.Costing.cs.
--
-- WHAT THIS IS: the persistence of the costing engine (FIFO cost layers and moving weighted average per ITEM x WAREHOUSE, D-017 / D-018). It is a COSTING structure, not a stock-movement feature: nothing in the
-- application writes to these tables from a document, a screen or a job in this phase. They are written only by the costing service inside a caller's transaction (tests today; the posting slice later, atomically
-- with the document status and its accounting state). Amounts are in the company base currency, rounded to its minor units only where a proportion has to be taken (a partial issue), with the remainder to the last
-- unit so that nothing is ever lost or invented: a layer's amount and the amount of everything issued from it always add up exactly.
--
-- Invariants enforced by PostgreSQL (not only by the application):
--   * quantities and values are never negative; a position with no quantity has no value; a layer never holds more than it received nor less than nothing (no negative FIFO layer);
--   * the valuation method of a position, its item and its warehouse are immutable (a method change is never silent);
--   * every entry of a position carries the next sequence number (a deterministic order; concurrent postings serialise on the position row);
--   * entries, consumptions and operations are append-only (an operation can only be marked reversed, once); a reversal of an entry exists at most once; one operation is reversed at most once;
--   * nothing is ever deleted or truncated.
-- kind of an operation: 1 receipt, 2 issue, 3 transfer, 4 reversal. kind of an entry: 1 receipt, 2 issue, 3 transfer out, 4 transfer in. direction: 1 into the position, -1 out of it (a reversal flips it).

CREATE TABLE t_a648ca40 (
    c_f47f15da uuid NOT NULL DEFAULT gen_random_uuid(),
    c_b45c4009 varchar(100) NOT NULL,
    c_7f2ea712 smallint NOT NULL,
    c_1ef8e158 char(64) NOT NULL,
    c_7114c862 varchar(40) NULL,
    c_b18070d3 uuid NULL,
    c_a84fdcdc date NOT NULL,
    c_aba24d32 uuid NOT NULL,
    c_9bef7859 timestamptz NOT NULL,
    c_03a68cea uuid NULL,
    c_710cc274 uuid NULL,
    CONSTRAINT k_4034fa9b PRIMARY KEY (c_f47f15da),
    CONSTRAINT k_e74e9377 UNIQUE (c_b45c4009),
    CONSTRAINT k_eebf577b FOREIGN KEY (c_03a68cea) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_ed7cf326 FOREIGN KEY (c_710cc274) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_f356c89a CHECK (c_7f2ea712 IN (1, 2, 3, 4) AND char_length(btrim(c_b45c4009)) >= 1 AND (c_7f2ea712 = 4) = (c_03a68cea IS NOT NULL))
);
CREATE UNIQUE INDEX k_99f6719a ON t_a648ca40 (c_03a68cea) WHERE c_03a68cea IS NOT NULL;
CREATE FUNCTION f_9c827423() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_LEVEL = 'STATEMENT' OR TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'costing operations are never deleted'; END IF;
    IF OLD.c_710cc274 IS NOT NULL OR NEW.c_710cc274 IS NULL THEN RAISE EXCEPTION 'an operation can only be marked reversed, once'; END IF;
    IF NEW.c_f47f15da IS DISTINCT FROM OLD.c_f47f15da OR NEW.c_b45c4009 IS DISTINCT FROM OLD.c_b45c4009 OR NEW.c_7f2ea712 IS DISTINCT FROM OLD.c_7f2ea712
       OR NEW.c_1ef8e158 IS DISTINCT FROM OLD.c_1ef8e158 OR NEW.c_a84fdcdc IS DISTINCT FROM OLD.c_a84fdcdc
       OR NEW.c_03a68cea IS DISTINCT FROM OLD.c_03a68cea OR NEW.c_aba24d32 IS DISTINCT FROM OLD.c_aba24d32 THEN
        RAISE EXCEPTION 'a costing operation is immutable';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_dfcc7b00 BEFORE UPDATE OR DELETE ON t_a648ca40 FOR EACH ROW EXECUTE FUNCTION f_9c827423();
CREATE TRIGGER r_bdcb866c BEFORE TRUNCATE ON t_a648ca40 FOR EACH STATEMENT EXECUTE FUNCTION f_9c827423();
REVOKE ALL ON TABLE t_a648ca40 FROM PUBLIC;

-- One cost position per item and warehouse (the unit of costing and of locking). method: 1 FIFO, 2 moving weighted average; fixed at the first entry.
CREATE TABLE t_a2903683 (
    c_6b4abdd9 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_3380c61e uuid NOT NULL,
    c_f0a91938 uuid NOT NULL,
    c_a71bfccf smallint NOT NULL,
    c_8e61b559 numeric(28,10) NOT NULL DEFAULT 0,
    c_2e1921d0 numeric(28,6) NOT NULL DEFAULT 0,
    c_ad2af2e5 bigint NOT NULL DEFAULT 0,
    c_3c880b93 date NULL,
    c_b6a6317a timestamptz NOT NULL,
    c_7672cc5e timestamptz NOT NULL,
    CONSTRAINT k_369b1663 PRIMARY KEY (c_6b4abdd9),
    CONSTRAINT k_ceb4d486 FOREIGN KEY (c_3380c61e) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_1be4b387 FOREIGN KEY (c_f0a91938) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_ee055292 UNIQUE (c_3380c61e, c_f0a91938),
    CONSTRAINT k_81ff32eb CHECK (c_a71bfccf IN (1, 2) AND c_8e61b559 >= 0 AND c_2e1921d0 >= 0 AND (c_8e61b559 > 0 OR c_2e1921d0 = 0) AND c_ad2af2e5 >= 0)
);
CREATE FUNCTION f_589a0a19() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_LEVEL = 'STATEMENT' OR TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cost positions are never deleted'; END IF;
    IF NEW.c_6b4abdd9 IS DISTINCT FROM OLD.c_6b4abdd9 OR NEW.c_3380c61e IS DISTINCT FROM OLD.c_3380c61e
       OR NEW.c_f0a91938 IS DISTINCT FROM OLD.c_f0a91938 OR NEW.c_b6a6317a IS DISTINCT FROM OLD.c_b6a6317a THEN
        RAISE EXCEPTION 'the item and warehouse of a cost position are immutable';
    END IF;
    IF NEW.c_a71bfccf IS DISTINCT FROM OLD.c_a71bfccf THEN RAISE EXCEPTION 'the valuation method of a cost position never changes silently'; END IF;
    IF NEW.c_ad2af2e5 <> OLD.c_ad2af2e5 + 1 THEN RAISE EXCEPTION 'a cost position advances by exactly one entry at a time'; END IF;
    IF OLD.c_3c880b93 IS NOT NULL AND (NEW.c_3c880b93 IS NULL OR NEW.c_3c880b93 < OLD.c_3c880b93) THEN
        RAISE EXCEPTION 'a cost position does not accept back-dated entries';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_5de79b17 BEFORE UPDATE OR DELETE ON t_a2903683 FOR EACH ROW EXECUTE FUNCTION f_589a0a19();
CREATE TRIGGER r_b7546021 BEFORE TRUNCATE ON t_a2903683 FOR EACH STATEMENT EXECUTE FUNCTION f_589a0a19();
REVOKE ALL ON TABLE t_a2903683 FROM PUBLIC;

-- The costing ledger: one immutable row per movement of cost through a position, with the running quantity and value after it.
CREATE TABLE t_bc727c1d (
    c_ed61ae1a bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_eb34b7d8 uuid NOT NULL,
    c_98321e7f uuid NOT NULL,
    c_9ee52677 uuid NOT NULL,
    c_0f65c03c uuid NOT NULL,
    c_4105796c smallint NOT NULL,
    c_e0cd1a14 smallint NOT NULL,
    c_12665ffb date NOT NULL,
    c_dba27822 numeric(28,10) NOT NULL,
    c_ece66ed5 numeric(28,6) NOT NULL,
    c_3437e13d numeric(28,10) NOT NULL,
    c_30804877 numeric(28,6) NOT NULL,
    c_2affbce9 bigint NOT NULL,
    c_4f9e777a bigint NULL,
    c_585a4312 timestamptz NOT NULL,
    CONSTRAINT k_3d37b6c0 PRIMARY KEY (c_ed61ae1a),
    CONSTRAINT k_732ebe05 FOREIGN KEY (c_eb34b7d8) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_c5a919b3 FOREIGN KEY (c_98321e7f) REFERENCES t_a2903683 (c_6b4abdd9),
    CONSTRAINT k_4d09b17c FOREIGN KEY (c_9ee52677) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_1abaedac FOREIGN KEY (c_0f65c03c) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_4976785f FOREIGN KEY (c_4f9e777a) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_0b3e9de8 CHECK (c_4105796c IN (1, 2, 3, 4) AND c_e0cd1a14 IN (1, -1) AND c_dba27822 > 0 AND c_ece66ed5 >= 0
        AND c_3437e13d >= 0 AND c_30804877 >= 0 AND (c_3437e13d > 0 OR c_30804877 = 0) AND c_2affbce9 > 0)
);
CREATE UNIQUE INDEX k_214c34ab ON t_bc727c1d (c_98321e7f, c_2affbce9);
CREATE UNIQUE INDEX k_c8fdc6c0 ON t_bc727c1d (c_4f9e777a) WHERE c_4f9e777a IS NOT NULL;
CREATE INDEX k_ecc9213f ON t_bc727c1d (c_eb34b7d8);
CREATE FUNCTION f_8b9d471d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'costing entries are append-only';
END;
$$;
CREATE TRIGGER r_911c728a BEFORE UPDATE OR DELETE ON t_bc727c1d FOR EACH ROW EXECUTE FUNCTION f_8b9d471d();
CREATE TRIGGER r_317ff2e0 BEFORE TRUNCATE ON t_bc727c1d FOR EACH STATEMENT EXECUTE FUNCTION f_8b9d471d();
REVOKE ALL ON TABLE t_bc727c1d FROM PUBLIC;

-- FIFO cost layers: what a receipt brought in and what is left of it. Only the remaining figures ever change, within [0, original].
CREATE TABLE t_3147676c (
    c_d784aea8 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_88f16993 uuid NOT NULL,
    c_c4178514 uuid NOT NULL,
    c_f0903b24 uuid NOT NULL,
    c_07583c02 bigint NOT NULL,
    c_bf7d08a1 bigint NOT NULL,
    c_c50486e0 numeric(28,10) NOT NULL,
    c_f5a791ec numeric(28,6) NOT NULL,
    c_69f58f45 numeric(28,10) NOT NULL,
    c_d25b66c0 numeric(28,6) NOT NULL,
    c_1c8be8cf uuid NULL,
    c_4f86bd91 timestamptz NOT NULL,
    CONSTRAINT k_b8c27d8c PRIMARY KEY (c_d784aea8),
    CONSTRAINT k_6ee66f43 FOREIGN KEY (c_88f16993) REFERENCES t_a2903683 (c_6b4abdd9),
    CONSTRAINT k_5b0f7de6 FOREIGN KEY (c_07583c02) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_72b1a97d FOREIGN KEY (c_1c8be8cf) REFERENCES t_3147676c (c_d784aea8),
    CONSTRAINT k_65138e7a CHECK (c_c50486e0 > 0 AND c_f5a791ec >= 0 AND c_69f58f45 >= 0 AND c_69f58f45 <= c_c50486e0
        AND c_d25b66c0 >= 0 AND c_d25b66c0 <= c_f5a791ec AND (c_69f58f45 > 0 OR c_d25b66c0 = 0))
);
CREATE UNIQUE INDEX k_27306962 ON t_3147676c (c_88f16993, c_bf7d08a1);
CREATE INDEX k_ccd72f79 ON t_3147676c (c_88f16993, c_bf7d08a1) WHERE c_69f58f45 > 0;
CREATE FUNCTION f_b16f3723() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_LEVEL = 'STATEMENT' OR TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cost layers are never deleted'; END IF;
    IF NEW.c_d784aea8 IS DISTINCT FROM OLD.c_d784aea8 OR NEW.c_88f16993 IS DISTINCT FROM OLD.c_88f16993 OR NEW.c_c4178514 IS DISTINCT FROM OLD.c_c4178514
       OR NEW.c_f0903b24 IS DISTINCT FROM OLD.c_f0903b24 OR NEW.c_07583c02 IS DISTINCT FROM OLD.c_07583c02 OR NEW.c_bf7d08a1 IS DISTINCT FROM OLD.c_bf7d08a1
       OR NEW.c_c50486e0 IS DISTINCT FROM OLD.c_c50486e0 OR NEW.c_f5a791ec IS DISTINCT FROM OLD.c_f5a791ec
       OR NEW.c_1c8be8cf IS DISTINCT FROM OLD.c_1c8be8cf OR NEW.c_4f86bd91 IS DISTINCT FROM OLD.c_4f86bd91 THEN
        RAISE EXCEPTION 'only the remaining figures of a cost layer can change';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_05e5e9b8 BEFORE UPDATE OR DELETE ON t_3147676c FOR EACH ROW EXECUTE FUNCTION f_b16f3723();
CREATE TRIGGER r_2e25b0dd BEFORE TRUNCATE ON t_3147676c FOR EACH STATEMENT EXECUTE FUNCTION f_b16f3723();
REVOKE ALL ON TABLE t_3147676c FROM PUBLIC;

-- Traceability: which layers an outflow consumed (and which a reversal restored), with the exact quantity and amount.
CREATE TABLE t_6df3b92e (
    c_22ac1076 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_b17f9a0c bigint NOT NULL,
    c_9b2217bf uuid NOT NULL,
    c_13147040 numeric(28,10) NOT NULL,
    c_b7bc1937 numeric(28,6) NOT NULL,
    c_90e0409c boolean NOT NULL DEFAULT false,
    CONSTRAINT k_18cb3079 PRIMARY KEY (c_22ac1076),
    CONSTRAINT k_4a2a4b1e FOREIGN KEY (c_b17f9a0c) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_47252d98 FOREIGN KEY (c_9b2217bf) REFERENCES t_3147676c (c_d784aea8),
    CONSTRAINT k_37922aa8 CHECK (c_13147040 > 0 AND c_b7bc1937 >= 0)
);
CREATE INDEX k_f9b0a9ea ON t_6df3b92e (c_9b2217bf);
CREATE INDEX k_a48dd68c ON t_6df3b92e (c_b17f9a0c);
CREATE FUNCTION f_4bf3449c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'cost consumptions are append-only';
END;
$$;
CREATE TRIGGER r_4b65e872 BEFORE UPDATE OR DELETE ON t_6df3b92e FOR EACH ROW EXECUTE FUNCTION f_4bf3449c();
CREATE TRIGGER r_d9027ce4 BEFORE TRUNCATE ON t_6df3b92e FOR EACH STATEMENT EXECUTE FUNCTION f_4bf3449c();
REVOKE ALL ON TABLE t_6df3b92e FROM PUBLIC;
