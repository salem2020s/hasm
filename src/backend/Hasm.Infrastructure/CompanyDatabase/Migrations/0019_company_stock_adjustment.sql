-- Migration 0019: stock adjustments generated from an approved stocktake, their posting and their reversal (Phase 3.8B-2). Forward-only and ADDITIVE: new tables, one added nullable column, new triggers; nothing
-- removed, nothing seeded, no existing data read or modified, no existing constraint changed. The costing engine (migration 0014) and the stock document tables (0013, 0016, 0017) are untouched.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.StockAdjustment.cs and CompanySchema.InventoryAccounting.cs (added column).
--
-- WHAT THIS IS: an approved stocktake yields at most ONE adjustment, dated on its count date in its warehouse, with one line per item whose variance against the book quantity AT GENERATION is not zero
-- (direction +1 surplus = inflow, -1 shortage = outflow; quantity = |counted - book| in the base unit). The adjustment is born approved (its content is fully determined by the approved count) and is numbered.
-- Posting it (once) moves the stock through the costing engine (shortages = one issue operation at actual cost, surpluses = one receipt operation at the amounts entered by the poster) and creates one balanced
-- draft journal entry against the configuration's new adjustment counter account, in one transaction; reversing it (once) takes both operations back and handles the journal like a stock document reversal (3.7C).
--
-- Invariants enforced by PostgreSQL:
--   * one adjustment per stocktake (unique), only for an APPROVED stocktake of the same branch and warehouse, dated on its count date, with a unique number; never updated, deleted or truncated;
--   * an adjustment has at least one line (checked at commit); lines: one per item, quantity > 0, direction +1/-1 and quantity x direction = counted - book EXACTLY; lines are append-only and cannot be added after posting;
--   * an approved stocktake that has an adjustment can never be cancelled;
--   * one posting per adjustment, at least one costing operation, each operation used once; one reversal per posting; postings, their lines and reversals are append-only;
--   * the inventory accounting configuration gains an optional adjustment counter account (versions saved before simply have none; nothing with value can be adjusted until a new version names one).

-- ---------------------------------------------------------------------------------------------------------------- accounting configuration (additive nullable column)
ALTER TABLE t_a78f5224 ADD COLUMN c_76c92b02 uuid NULL;
ALTER TABLE t_a78f5224 ADD CONSTRAINT k_cceb2706 FOREIGN KEY (c_76c92b02) REFERENCES t_11d107a9 (c_29a86598);

-- ---------------------------------------------------------------------------------------------------------------- the adjustment
CREATE TABLE t_ef411fe9 (
    c_a56d5ec0 uuid NOT NULL,
    c_2f4d598c uuid NOT NULL,
    c_415b4325 uuid NOT NULL,
    c_79686333 uuid NOT NULL,
    c_b71d44ca date NOT NULL,
    c_f1979515 varchar(60) NOT NULL,
    c_eb67c083 uuid NOT NULL,
    c_9e3ca302 uuid NOT NULL,
    c_f6b01cc1 timestamptz NOT NULL,
    CONSTRAINT k_5aeddbc2 PRIMARY KEY (c_a56d5ec0),
    CONSTRAINT k_eea5b3fb UNIQUE (c_2f4d598c),
    CONSTRAINT k_b78c7db1 UNIQUE (c_f1979515),
    CONSTRAINT k_78644625 FOREIGN KEY (c_2f4d598c) REFERENCES t_994f8458 (c_2ac20938),
    CONSTRAINT k_08494743 FOREIGN KEY (c_415b4325) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_1c1e384e FOREIGN KEY (c_79686333) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_7e9b2f72 FOREIGN KEY (c_eb67c083) REFERENCES t_4b5bb222 (c_cae1c56a)
);
CREATE FUNCTION f_246d3559() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock adjustments are immutable';
END;
$$;
CREATE TRIGGER r_52eebea3 BEFORE UPDATE OR DELETE ON t_ef411fe9 FOR EACH ROW EXECUTE FUNCTION f_246d3559();
CREATE TRIGGER r_9fe06ad3 BEFORE TRUNCATE ON t_ef411fe9 FOR EACH STATEMENT EXECUTE FUNCTION f_246d3559();
REVOKE ALL ON TABLE t_ef411fe9 FROM PUBLIC;

-- An adjustment belongs to an APPROVED stocktake of the same branch and warehouse and is dated on its count date.
CREATE FUNCTION f_b31406b7() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ok boolean;
BEGIN
    SELECT (s.c_941ef8c3 = 3 AND s.c_a28d1691 = NEW.c_79686333 AND s.c_b156e8dd = NEW.c_415b4325 AND s.c_57c0972a = NEW.c_b71d44ca)
      INTO ok FROM t_994f8458 s WHERE s.c_2ac20938 = NEW.c_2f4d598c;
    IF ok IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'an adjustment belongs to an approved stocktake of the same branch, warehouse and date';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_799efe30 BEFORE INSERT ON t_ef411fe9 FOR EACH ROW EXECUTE FUNCTION f_b31406b7();

-- An approved stocktake whose adjustment was generated can never be cancelled (the count is booked, or about to be booked, by that adjustment).
CREATE FUNCTION f_5c74ab1f() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_ef411fe9 WHERE c_2f4d598c = NEW.c_2ac20938) THEN
        RAISE EXCEPTION 'a stocktake with a generated adjustment cannot be cancelled';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_677dd89c BEFORE UPDATE ON t_994f8458 FOR EACH ROW WHEN (NEW.c_941ef8c3 = 5 AND OLD.c_941ef8c3 <> 5) EXECUTE FUNCTION f_5c74ab1f();

-- ---------------------------------------------------------------------------------------------------------------- its lines
CREATE TABLE t_98f8a941 (
    c_132a508f uuid NOT NULL,
    c_17befa84 integer NOT NULL,
    c_b47d52ef uuid NOT NULL,
    c_2219608a uuid NOT NULL,
    c_4679f0ab smallint NOT NULL,
    c_d4b14b3f numeric(28,10) NOT NULL,
    c_18db4018 numeric(28,10) NOT NULL,
    c_73fc0c09 numeric(28,10) NOT NULL,
    c_b785c5b9 varchar(200) NULL,
    CONSTRAINT k_d395bcde PRIMARY KEY (c_132a508f, c_17befa84),
    CONSTRAINT k_466532f3 UNIQUE (c_132a508f, c_b47d52ef),
    CONSTRAINT k_506d9cd6 FOREIGN KEY (c_132a508f) REFERENCES t_ef411fe9 (c_a56d5ec0),
    CONSTRAINT k_1599407e FOREIGN KEY (c_b47d52ef) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_1920d25b FOREIGN KEY (c_2219608a) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_6c3e195a CHECK (c_17befa84 > 0 AND c_4679f0ab IN (1, -1) AND c_d4b14b3f > 0
        AND c_18db4018 >= 0 AND c_73fc0c09 >= 0
        AND c_d4b14b3f * c_4679f0ab = c_18db4018 - c_73fc0c09)
);
-- Lines are append-only, and none can be added once the adjustment is posted.
CREATE FUNCTION f_28ae24c1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF EXISTS (SELECT 1 FROM t_fb4a8e51 WHERE c_c4df29e1 = NEW.c_132a508f) THEN
            RAISE EXCEPTION 'a posted stock adjustment cannot gain lines';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'stock adjustment lines are append-only';
END;
$$;
REVOKE ALL ON TABLE t_98f8a941 FROM PUBLIC;

-- An adjustment has at least one line (judged at commit, after the lines of the same transaction exist).
CREATE FUNCTION f_00c34d24() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM t_98f8a941 WHERE c_132a508f = NEW.c_a56d5ec0) THEN
        RAISE EXCEPTION 'a stock adjustment needs at least one line';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_8c4f72a8 AFTER INSERT ON t_ef411fe9 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_00c34d24();

-- ---------------------------------------------------------------------------------------------------------------- posting (append-only)
CREATE TABLE t_fb4a8e51 (
    c_345ead50 uuid NOT NULL,
    c_c4df29e1 uuid NOT NULL,
    c_1d635eec date NOT NULL,
    c_f9dda402 timestamptz NOT NULL,
    c_d8a23a29 uuid NOT NULL,
    c_cba9bae0 uuid NULL,
    c_112c1630 uuid NULL,
    c_20b44cf8 uuid NULL,
    c_654d3c97 uuid NULL,
    c_0533fd18 numeric(28,6) NOT NULL,
    CONSTRAINT k_d4fc9e8b PRIMARY KEY (c_345ead50),
    CONSTRAINT k_51f39043 UNIQUE (c_c4df29e1),
    CONSTRAINT k_fc0e18c2 UNIQUE (c_cba9bae0),
    CONSTRAINT k_20d5b573 UNIQUE (c_112c1630),
    CONSTRAINT k_10b4e5e5 FOREIGN KEY (c_c4df29e1) REFERENCES t_ef411fe9 (c_a56d5ec0),
    CONSTRAINT k_0eaa3589 FOREIGN KEY (c_cba9bae0) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_43dc28bd FOREIGN KEY (c_112c1630) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_d9c1c449 FOREIGN KEY (c_20b44cf8) REFERENCES t_a78f5224 (c_3bffe448),
    CONSTRAINT k_f87e6441 FOREIGN KEY (c_654d3c97) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_74ccd88f CHECK (c_0533fd18 >= 0
        AND (c_cba9bae0 IS NOT NULL OR c_112c1630 IS NOT NULL)
        AND (c_cba9bae0 IS DISTINCT FROM c_112c1630)
        AND ((c_654d3c97 IS NULL) OR (c_20b44cf8 IS NOT NULL)))
);
CREATE INDEX k_c1dfafaa ON t_fb4a8e51 (c_654d3c97);
CREATE FUNCTION f_35814604() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock adjustment postings are append-only';
END;
$$;
CREATE TRIGGER r_71f9c6b0 BEFORE UPDATE OR DELETE ON t_fb4a8e51 FOR EACH ROW EXECUTE FUNCTION f_35814604();
CREATE TRIGGER r_076558a2 BEFORE TRUNCATE ON t_fb4a8e51 FOR EACH STATEMENT EXECUTE FUNCTION f_35814604();
REVOKE ALL ON TABLE t_fb4a8e51 FROM PUBLIC;

-- the line guard reads the posting table, so its triggers are created now
CREATE TRIGGER r_48cdd797 BEFORE INSERT OR UPDATE OR DELETE ON t_98f8a941 FOR EACH ROW EXECUTE FUNCTION f_28ae24c1();
CREATE TRIGGER r_4ca060a3 BEFORE TRUNCATE ON t_98f8a941 FOR EACH STATEMENT EXECUTE FUNCTION f_28ae24c1();

-- The value booked per line: the engine's actual cost for a shortage, the amount entered by the poster for a surplus.
CREATE TABLE t_9d086874 (
    c_7286ee2d uuid NOT NULL,
    c_f8909b7a integer NOT NULL,
    c_e80bb402 numeric(28,6) NOT NULL,
    CONSTRAINT k_ec5f1f18 PRIMARY KEY (c_7286ee2d, c_f8909b7a),
    CONSTRAINT k_de63672f FOREIGN KEY (c_7286ee2d) REFERENCES t_fb4a8e51 (c_345ead50),
    CONSTRAINT k_643c4bc3 CHECK (c_f8909b7a > 0 AND c_e80bb402 >= 0)
);
CREATE FUNCTION f_7eb60451() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock adjustment posting lines are append-only';
END;
$$;
CREATE TRIGGER r_b1a3d6ae BEFORE UPDATE OR DELETE ON t_9d086874 FOR EACH ROW EXECUTE FUNCTION f_7eb60451();
CREATE TRIGGER r_0ce5e3d8 BEFORE TRUNCATE ON t_9d086874 FOR EACH STATEMENT EXECUTE FUNCTION f_7eb60451();
REVOKE ALL ON TABLE t_9d086874 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- reversal (append-only). JournalAction as in 3.7C: 0 no journal, 1 nothing posted left to undo (cancelled), 2 draft reversal entry created.
CREATE TABLE t_a3118c51 (
    c_1c5f7ed2 uuid NOT NULL,
    c_822aba00 uuid NOT NULL,
    c_1f9a474c uuid NULL,
    c_8538a39d uuid NULL,
    c_463eb3d1 smallint NOT NULL,
    c_c77ab4aa uuid NULL,
    c_b3b8ce31 date NOT NULL,
    c_4f5c797f timestamptz NOT NULL,
    c_9b9982a1 uuid NOT NULL,
    c_3a86d3ef varchar(500) NOT NULL,
    CONSTRAINT k_9533342b PRIMARY KEY (c_1c5f7ed2),
    CONSTRAINT k_7178c141 UNIQUE (c_822aba00),
    CONSTRAINT k_febfff89 UNIQUE (c_1f9a474c),
    CONSTRAINT k_4d03aeb7 UNIQUE (c_8538a39d),
    CONSTRAINT k_986ed903 FOREIGN KEY (c_822aba00) REFERENCES t_fb4a8e51 (c_345ead50),
    CONSTRAINT k_27b97d74 FOREIGN KEY (c_1f9a474c) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_9ad61c31 FOREIGN KEY (c_8538a39d) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_25ffd26d FOREIGN KEY (c_c77ab4aa) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_796abcf4 CHECK (c_463eb3d1 IN (0, 1, 2)
        AND ((c_463eb3d1 = 0) = (c_c77ab4aa IS NULL))
        AND (c_1f9a474c IS NOT NULL OR c_8538a39d IS NOT NULL)
        AND length(btrim(c_3a86d3ef)) > 0)
);
CREATE FUNCTION f_cdab459e() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock adjustment reversals are append-only';
END;
$$;
CREATE TRIGGER r_2fcc8129 BEFORE UPDATE OR DELETE ON t_a3118c51 FOR EACH ROW EXECUTE FUNCTION f_cdab459e();
CREATE TRIGGER r_0430fa66 BEFORE TRUNCATE ON t_a3118c51 FOR EACH STATEMENT EXECUTE FUNCTION f_cdab459e();
REVOKE ALL ON TABLE t_a3118c51 FROM PUBLIC;
