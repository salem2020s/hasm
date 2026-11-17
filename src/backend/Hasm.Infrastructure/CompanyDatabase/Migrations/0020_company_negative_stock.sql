-- Migration 0020: negative-stock exception (Phase 3.8B-3). Forward-only and ADDITIVE: new tables, functions and triggers, two added nullable columns on the cost position, two on the inventory accounting version;
-- nothing removed, nothing seeded, no existing data read or modified, NO existing constraint changed or relaxed. Opaque physical names (D-029); the logical dictionary lives in CompanySchema.NegativeStock.cs
-- (added columns: CompanySchema.Costing.cs and CompanySchema.InventoryAccounting.cs).
--
-- WHAT THIS IS (docs/PHASE-3.8B-3.md, design Revision 4): an issue that exceeds the stock of an eligible moving-average item may, by an explicit and segregated exception approval, be split into the COVERED part (the
-- unchanged costing engine, real moving average) and the UNCOVERED part, which never touches the cost position: it becomes a provisional OBLIGATION valued at a provisional total. A later stock-document receipt of the
-- item and warehouse SETTLES open obligations oldest first (only the rest enters the position), and the difference between the actual and the provisional value is booked to a variance account. The cost position stays a
-- true physical stock: never a negative quantity or value, no value without quantity, no negative layer (0014 untouched).
--
-- Schema decision (docs/PHASE-3.8B-3.md, DB-1): widening the operation-kind CHECK k_f356c89a would need ALTER TABLE ... DROP CONSTRAINT, which the migration guard (MigrationScript) refuses with no allow path. So a
-- provisional operation keeps its natural kind (2 issue / 1 receipt / 4 reversal) and may have ZERO entries ONLY when it belongs to an obligation, a settlement, or reverses such an operation (deferred check below).
--
-- The posting LINE and the stock reversal referenced by an obligation, a settlement or an obligation reversal are checked by their insert triggers rather than by foreign keys, so the existing append-only
-- guards of those tables (0016, 0017) keep answering a TRUNCATE exactly as before.
--
-- Invariants enforced by PostgreSQL:
--   * obligations, settlements, obligation reversals, exception approvals, settings versions and eligibility flags are append-only (never updated, deleted or truncated);
--   * open quantity of an obligation = original - net settled - (original when reversed), DERIVED (never stored); net settlement between 0 and the original quantity and provisional total, exact remainder on the last
--     match; a settlement reversal negates exactly one settlement once; a receipt line settles at most its own quantity and amount; an obligation with a net settlement cannot be reversed;
--   * an OPEN obligation implies the cost position quantity is 0 (deferred constraint triggers on the position, the obligation and the settlement: judged at commit);
--   * an obligation needs an eligible item (flag, moving average position, no tracking) and an issue posting line of the same item and warehouse; an approval belongs to an approved issue document and is never given by its creator;
--   * guarded changes while an obligation is open (R12): eligibility flag, item tracking, group, deactivation; valuation policy of a group in the chain; company default valuation when the resolution would change;
--     warehouse deactivation;
--   * a manual journal line on an obligation account is refused (automatic postings only; reversal entries mirror an automatic entry);
--   * the last average (value, quantity) pair of a cost position is written by its entries only (current figures while it holds quantity, retained when it falls to zero; no backfill).

-- ---------------------------------------------------------------------------------------------------------------- cost position: last average pair (additive, no backfill)
ALTER TABLE t_a2903683 ADD COLUMN c_97877d45 numeric(28,10) NULL;
ALTER TABLE t_a2903683 ADD COLUMN c_548e8abe numeric(28,6) NULL;
ALTER TABLE t_a2903683 ADD CONSTRAINT k_351f45a9 CHECK ((c_97877d45 IS NULL AND c_548e8abe IS NULL) OR (c_97877d45 > 0 AND c_548e8abe >= 0));
CREATE FUNCTION f_9b70c8c4() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_97877d45 IS NOT NULL OR NEW.c_548e8abe IS NOT NULL THEN RAISE EXCEPTION 'the last average of a cost position is written by its entries only'; END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_8e61b559 > 0 THEN
        IF NEW.c_97877d45 IS DISTINCT FROM NEW.c_8e61b559 OR NEW.c_548e8abe IS DISTINCT FROM NEW.c_2e1921d0 THEN
            RAISE EXCEPTION 'the last average of a cost position holding stock is its current quantity and value';
        END IF;
    ELSIF NEW.c_97877d45 IS DISTINCT FROM OLD.c_97877d45 OR NEW.c_548e8abe IS DISTINCT FROM OLD.c_548e8abe THEN
        RAISE EXCEPTION 'an empty cost position keeps its last average';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_9040ab41 BEFORE INSERT OR UPDATE ON t_a2903683 FOR EACH ROW EXECUTE FUNCTION f_9b70c8c4();

-- ---------------------------------------------------------------------------------------------------------------- inventory accounting configuration: obligation account O and variance account V (additive nullable columns)
ALTER TABLE t_a78f5224 ADD COLUMN c_17133030 uuid NULL;
ALTER TABLE t_a78f5224 ADD COLUMN c_3dcd9eb2 uuid NULL;
ALTER TABLE t_a78f5224 ADD CONSTRAINT k_3d8110d0 FOREIGN KEY (c_17133030) REFERENCES t_11d107a9 (c_29a86598);
ALTER TABLE t_a78f5224 ADD CONSTRAINT k_807d7ceb FOREIGN KEY (c_3dcd9eb2) REFERENCES t_11d107a9 (c_29a86598);

-- ---------------------------------------------------------------------------------------------------------------- company enablement (versioned, append-only; off until a version enables it)
CREATE TABLE t_9b016b79 (
    c_475ca92d uuid NOT NULL,
    c_a2849f5d integer NOT NULL,
    c_bb298bfd boolean NOT NULL,
    c_cae55ed1 boolean NOT NULL,
    c_7d8ae708 varchar(500) NULL,
    c_3f08c397 uuid NOT NULL,
    c_318fd3bb timestamptz NOT NULL,
    CONSTRAINT k_44066dde PRIMARY KEY (c_475ca92d),
    CONSTRAINT k_7046f466 UNIQUE (c_a2849f5d),
    CONSTRAINT k_d19853a8 CHECK (c_a2849f5d > 0)
);
CREATE FUNCTION f_e3b7d97a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'negative-stock settings versions are append-only';
END;
$$;
CREATE TRIGGER r_8526da8e BEFORE UPDATE OR DELETE ON t_9b016b79 FOR EACH ROW EXECUTE FUNCTION f_e3b7d97a();
CREATE TRIGGER r_23f4b5f4 BEFORE TRUNCATE ON t_9b016b79 FOR EACH STATEMENT EXECUTE FUNCTION f_e3b7d97a();
REVOKE ALL ON TABLE t_9b016b79 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- per-item eligibility flag (append-only history; the latest row of an item is its current flag)
CREATE TABLE t_3fb62185 (
    c_dd9030ed bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_59aa8519 uuid NOT NULL,
    c_7d66a9cb boolean NOT NULL,
    c_08a5b83a varchar(500) NULL,
    c_f807da02 uuid NOT NULL,
    c_f2a97a77 timestamptz NOT NULL,
    CONSTRAINT k_9ed92816 PRIMARY KEY (c_dd9030ed),
    CONSTRAINT k_e154f3ad FOREIGN KEY (c_59aa8519) REFERENCES t_2f2a7184 (c_be51f22e)
);
CREATE INDEX k_e7f8e42d ON t_3fb62185 (c_59aa8519, c_dd9030ed);
CREATE FUNCTION f_6c6fdd4a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'negative-stock eligibility flags are append-only';
END;
$$;
CREATE TRIGGER r_96bfe539 BEFORE UPDATE OR DELETE ON t_3fb62185 FOR EACH ROW EXECUTE FUNCTION f_6c6fdd4a();
CREATE TRIGGER r_be34e15c BEFORE TRUNCATE ON t_3fb62185 FOR EACH STATEMENT EXECUTE FUNCTION f_6c6fdd4a();
REVOKE ALL ON TABLE t_3fb62185 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- exception approval (append-only; a separate audited event per approver and snapshot)
CREATE TABLE t_40ad576c (
    c_faadf366 uuid NOT NULL,
    c_7da52180 uuid NOT NULL,
    c_459b8474 bigint NOT NULL,
    c_e75cc8cf uuid NOT NULL,
    c_76d044fe timestamptz NOT NULL,
    c_f02786a1 varchar(500) NOT NULL,
    c_c8ec123f char(64) NOT NULL,
    c_0516bfb2 text NOT NULL,
    CONSTRAINT k_25eb4425 PRIMARY KEY (c_faadf366),
    CONSTRAINT k_33e249b1 FOREIGN KEY (c_7da52180) REFERENCES t_e92d29e5 (c_9a59d13e),
    CONSTRAINT k_ea767cd5 UNIQUE (c_7da52180, c_c8ec123f, c_e75cc8cf),
    CONSTRAINT k_979715cd CHECK (c_459b8474 > 0 AND length(btrim(c_f02786a1)) > 0 AND char_length(c_0516bfb2) BETWEEN 2 AND 200000)
);
CREATE FUNCTION f_3b9f8478() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'negative-stock exception approvals are append-only';
END;
$$;
CREATE TRIGGER r_88f32ccf BEFORE UPDATE OR DELETE ON t_40ad576c FOR EACH ROW EXECUTE FUNCTION f_3b9f8478();
CREATE TRIGGER r_aa4f2217 BEFORE TRUNCATE ON t_40ad576c FOR EACH STATEMENT EXECUTE FUNCTION f_3b9f8478();
REVOKE ALL ON TABLE t_40ad576c FROM PUBLIC;
-- An approval belongs to an APPROVED, not yet executed ISSUE document and is never given by the document's creator (segregation of duties; the poster is checked at posting).
CREATE FUNCTION f_c918fc84() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ok boolean;
BEGIN
    SELECT (d.c_bc592f66 = 2 AND d.c_3fc0f063 = 3 AND d.c_6b6eb86b <> NEW.c_e75cc8cf) INTO ok FROM t_e92d29e5 d WHERE d.c_9a59d13e = NEW.c_7da52180;
    IF ok IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'an exception approval belongs to an approved issue document and is never given by its creator';
    END IF;
    IF EXISTS (SELECT 1 FROM t_4d4b251f WHERE c_eb889fd0 = NEW.c_7da52180) THEN
        RAISE EXCEPTION 'an executed document cannot be approved for the exception';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_e59c775c BEFORE INSERT ON t_40ad576c FOR EACH ROW EXECUTE FUNCTION f_c918fc84();

-- ---------------------------------------------------------------------------------------------------------------- the obligation (immutable). source: 1 last average, 2 last stock-document receipt, 3 manual (justified)
CREATE TABLE t_b56b119d (
    c_89d0d4ae uuid NOT NULL,
    c_700da1f4 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_05baef20 uuid NOT NULL,
    c_971e8265 uuid NOT NULL,
    c_e58fa36c uuid NOT NULL,
    c_64379fe3 uuid NOT NULL,
    c_acdfca73 integer NOT NULL,
    c_d366d343 uuid NOT NULL,
    c_1872eef0 uuid NOT NULL,
    c_b9765532 uuid NOT NULL,
    c_f8f8295b uuid NOT NULL,
    c_ab2fc7e6 uuid NOT NULL,
    c_e6b8b42b uuid NOT NULL,
    c_d0597f45 uuid NOT NULL,
    c_1f0961d8 date NOT NULL,
    c_95d329bc numeric(28,10) NOT NULL,
    c_af214a8a numeric(28,6) NOT NULL,
    c_dde517b8 smallint NOT NULL,
    c_667e4cc5 bigint NULL,
    c_93fad1df numeric(28,10) NULL,
    c_5db1a6de numeric(28,6) NULL,
    c_e631ea3e varchar(500) NULL,
    c_10088048 uuid NOT NULL,
    c_225cbcd3 uuid NOT NULL,
    c_70194123 timestamptz NOT NULL,
    CONSTRAINT k_f2195551 PRIMARY KEY (c_89d0d4ae),
    CONSTRAINT k_83e403f6 UNIQUE (c_700da1f4),
    CONSTRAINT k_3f84d074 UNIQUE (c_64379fe3, c_acdfca73),
    CONSTRAINT k_9cf439a6 FOREIGN KEY (c_05baef20) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_cea1e750 FOREIGN KEY (c_971e8265) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_c43a0bb2 FOREIGN KEY (c_e58fa36c) REFERENCES t_a2903683 (c_6b4abdd9),
    CONSTRAINT k_42837e93 FOREIGN KEY (c_64379fe3) REFERENCES t_4d4b251f (c_fc71418e),
    CONSTRAINT k_6b66be21 FOREIGN KEY (c_1872eef0) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_124184dc FOREIGN KEY (c_b9765532) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_01dff459 FOREIGN KEY (c_f8f8295b) REFERENCES t_a78f5224 (c_3bffe448),
    CONSTRAINT k_445b35b0 FOREIGN KEY (c_ab2fc7e6) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_b83c3d92 FOREIGN KEY (c_e6b8b42b) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_7341fbdd FOREIGN KEY (c_d0597f45) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_e2e7b5d8 FOREIGN KEY (c_667e4cc5) REFERENCES t_bc727c1d (c_ed61ae1a),
    CONSTRAINT k_0dffb30c FOREIGN KEY (c_10088048) REFERENCES t_40ad576c (c_faadf366),
    CONSTRAINT k_a133520d CHECK (c_95d329bc > 0 AND c_af214a8a > 0 AND c_acdfca73 > 0 AND c_dde517b8 IN (1, 2, 3)
        AND ((c_dde517b8 = 3) = (c_e631ea3e IS NOT NULL))
        AND (c_e631ea3e IS NULL OR length(btrim(c_e631ea3e)) > 0)
        AND ((c_dde517b8 = 2) = (c_667e4cc5 IS NOT NULL))
        AND ((c_dde517b8 = 3) = (c_93fad1df IS NULL))
        AND ((c_93fad1df IS NULL) = (c_5db1a6de IS NULL))
        AND (c_93fad1df IS NULL OR (c_93fad1df > 0 AND c_5db1a6de > 0))
        AND c_ab2fc7e6 <> c_d0597f45 AND c_ab2fc7e6 <> c_e6b8b42b)
);
CREATE INDEX k_6579d292 ON t_b56b119d (c_05baef20, c_971e8265, c_1f0961d8, c_700da1f4);
CREATE INDEX k_9e35380a ON t_b56b119d (c_1872eef0);
CREATE FUNCTION f_f4b3e62e() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'negative-stock obligations are immutable';
END;
$$;
CREATE TRIGGER r_bec254b7 BEFORE UPDATE OR DELETE ON t_b56b119d FOR EACH ROW EXECUTE FUNCTION f_f4b3e62e();
CREATE TRIGGER r_121ad895 BEFORE TRUNCATE ON t_b56b119d FOR EACH STATEMENT EXECUTE FUNCTION f_f4b3e62e();
REVOKE ALL ON TABLE t_b56b119d FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- settlements (append-only; a reversal is its own row with negative figures)
CREATE TABLE t_d370b1d6 (
    c_f91bd136 uuid NOT NULL,
    c_70203e95 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_07e3dad0 uuid NOT NULL,
    c_58257cfe uuid NOT NULL,
    c_42a1e7ee integer NOT NULL,
    c_7208ba7b uuid NOT NULL,
    c_23eac371 numeric(28,10) NOT NULL,
    c_6f6ab47d numeric(28,6) NOT NULL,
    c_e5a64555 numeric(28,10) NOT NULL,
    c_e4745db7 numeric(28,6) NOT NULL,
    c_4972eb11 numeric(28,6) NOT NULL,
    c_ec59e35f numeric(28,6) NOT NULL,
    c_5e337f95 date NOT NULL,
    c_2fe8c930 uuid NULL,
    c_094f81ac uuid NULL,
    c_53ef40f0 uuid NULL,
    c_a5208414 uuid NOT NULL,
    c_4aa8f1a9 timestamptz NOT NULL,
    CONSTRAINT k_afe4960e PRIMARY KEY (c_f91bd136),
    CONSTRAINT k_82720473 UNIQUE (c_70203e95),
    CONSTRAINT k_fa315d14 FOREIGN KEY (c_07e3dad0) REFERENCES t_b56b119d (c_89d0d4ae),
    CONSTRAINT k_26bc3485 FOREIGN KEY (c_58257cfe) REFERENCES t_4d4b251f (c_fc71418e),
    CONSTRAINT k_58734e01 FOREIGN KEY (c_7208ba7b) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_05b49006 FOREIGN KEY (c_2fe8c930) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_037e1cf5 FOREIGN KEY (c_094f81ac) REFERENCES t_d370b1d6 (c_f91bd136),
    CONSTRAINT k_9cf92817 CHECK (c_42a1e7ee > 0 AND c_23eac371 > 0 AND c_6f6ab47d >= 0
        AND c_ec59e35f = c_4972eb11 - c_e4745db7
        AND ((c_094f81ac IS NULL) = (c_53ef40f0 IS NULL))
        AND ((c_094f81ac IS NULL AND c_e5a64555 > 0 AND c_e4745db7 >= 0 AND c_4972eb11 >= 0)
          OR (c_094f81ac IS NOT NULL AND c_e5a64555 < 0 AND c_e4745db7 <= 0 AND c_4972eb11 <= 0)))
);
CREATE UNIQUE INDEX k_48fca9a0 ON t_d370b1d6 (c_07e3dad0, c_58257cfe, c_42a1e7ee) WHERE c_094f81ac IS NULL;
CREATE UNIQUE INDEX k_a76cd6a6 ON t_d370b1d6 (c_094f81ac) WHERE c_094f81ac IS NOT NULL;
CREATE INDEX k_a3a30314 ON t_d370b1d6 (c_07e3dad0);
CREATE INDEX k_c6673ebe ON t_d370b1d6 (c_58257cfe, c_42a1e7ee);
CREATE INDEX k_3cd9be74 ON t_d370b1d6 (c_7208ba7b);
CREATE FUNCTION f_0e81c3ee() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'negative-stock settlements are append-only';
END;
$$;
CREATE TRIGGER r_88e59b65 BEFORE UPDATE OR DELETE ON t_d370b1d6 FOR EACH ROW EXECUTE FUNCTION f_0e81c3ee();
CREATE TRIGGER r_7ec87d7f BEFORE TRUNCATE ON t_d370b1d6 FOR EACH STATEMENT EXECUTE FUNCTION f_0e81c3ee();
REVOKE ALL ON TABLE t_d370b1d6 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- obligation reversal (append-only): the originating issue was reversed
CREATE TABLE t_075e0837 (
    c_f722aa60 uuid NOT NULL,
    c_4caf1cf3 uuid NOT NULL,
    c_4dfed26f uuid NOT NULL,
    c_c79ef8bc uuid NOT NULL,
    c_9f7cb668 date NOT NULL,
    c_00c3c788 uuid NOT NULL,
    c_87f8853f timestamptz NOT NULL,
    CONSTRAINT k_12b907e6 PRIMARY KEY (c_f722aa60),
    CONSTRAINT k_f1809d3e UNIQUE (c_4caf1cf3),
    CONSTRAINT k_43a2aab3 FOREIGN KEY (c_4caf1cf3) REFERENCES t_b56b119d (c_89d0d4ae),
    CONSTRAINT k_100a99ed FOREIGN KEY (c_c79ef8bc) REFERENCES t_a648ca40 (c_f47f15da)
);
CREATE FUNCTION f_23d36ecb() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'negative-stock obligation reversals are append-only';
END;
$$;
CREATE TRIGGER r_610ffd2a BEFORE UPDATE OR DELETE ON t_075e0837 FOR EACH ROW EXECUTE FUNCTION f_23d36ecb();
CREATE TRIGGER r_b6426fe8 BEFORE TRUNCATE ON t_075e0837 FOR EACH STATEMENT EXECUTE FUNCTION f_23d36ecb();
REVOKE ALL ON TABLE t_075e0837 FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------- shared functions
-- The valuation method an item resolves to: the nearest group of its chain with a method, otherwise the company default (as CompanyInventoryService resolves it).
CREATE FUNCTION f_23735cac(item uuid) RETURNS smallint LANGUAGE plpgsql STABLE AS $$
DECLARE
    m smallint;
BEGIN
    WITH RECURSIVE chain(id, parent, valuation, depth) AS (
        SELECT g.c_01551296, g.c_57852ce5, g.c_cde997ad, 1 FROM t_2640a66e g JOIN t_2f2a7184 i ON i.c_61dee773 = g.c_01551296 WHERE i.c_be51f22e = item
        UNION ALL
        SELECT g.c_01551296, g.c_57852ce5, g.c_cde997ad, c.depth + 1 FROM t_2640a66e g JOIN chain c ON g.c_01551296 = c.parent WHERE c.depth < 32
    )
    SELECT valuation INTO m FROM chain WHERE valuation IS NOT NULL ORDER BY depth LIMIT 1;
    IF m IS NULL THEN SELECT c_f0c103a8 INTO m FROM t_1cf46c0f LIMIT 1; END IF;
    RETURN m;
END;
$$;
-- True when the group is the item's group or one of its parents.
CREATE FUNCTION f_6efd1234(item uuid, grp uuid) RETURNS boolean LANGUAGE plpgsql STABLE AS $$
BEGIN
    RETURN EXISTS (
        WITH RECURSIVE chain(id, parent, depth) AS (
            SELECT g.c_01551296, g.c_57852ce5, 1 FROM t_2640a66e g JOIN t_2f2a7184 i ON i.c_61dee773 = g.c_01551296 WHERE i.c_be51f22e = item
            UNION ALL
            SELECT g.c_01551296, g.c_57852ce5, c.depth + 1 FROM t_2640a66e g JOIN chain c ON g.c_01551296 = c.parent WHERE c.depth < 32
        )
        SELECT 1 FROM chain WHERE id = grp);
END;
$$;
-- The open quantity of an obligation: original - net settled, and nothing once its issue was reversed (derived, never stored).
CREATE FUNCTION f_a21c7b04(obligation uuid) RETURNS numeric LANGUAGE plpgsql STABLE AS $$
DECLARE
    q numeric;
BEGIN
    IF EXISTS (SELECT 1 FROM t_075e0837 WHERE c_4caf1cf3 = obligation) THEN RETURN 0; END IF;
    SELECT o.c_95d329bc - coalesce((SELECT sum(s.c_e5a64555) FROM t_d370b1d6 s WHERE s.c_07e3dad0 = obligation), 0) INTO q
      FROM t_b56b119d o WHERE o.c_89d0d4ae = obligation;
    RETURN coalesce(q, 0);
END;
$$;
-- True when the item (optionally in one warehouse) has an open obligation.
CREATE FUNCTION f_cf64ca57(item uuid, warehouse uuid) RETURNS boolean LANGUAGE plpgsql STABLE AS $$
BEGIN
    RETURN EXISTS (SELECT 1 FROM t_b56b119d o WHERE (item IS NULL OR o.c_05baef20 = item) AND (warehouse IS NULL OR o.c_971e8265 = warehouse) AND f_a21c7b04(o.c_89d0d4ae) > 0);
END;
$$;
-- R3: an open obligation implies the cost position holds no quantity (judged at commit by the deferred triggers below).
CREATE FUNCTION f_8821e766(item uuid, warehouse uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_a2903683 p WHERE p.c_3380c61e = item AND p.c_f0a91938 = warehouse AND p.c_8e61b559 > 0) AND f_cf64ca57(item, warehouse) THEN
        RAISE EXCEPTION 'an open negative-stock obligation requires an empty cost position (item %, warehouse %)', item, warehouse;
    END IF;
END;
$$;
CREATE FUNCTION f_485217dd() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM f_8821e766(NEW.c_3380c61e, NEW.c_f0a91938);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_7c0b96d2 AFTER UPDATE ON t_a2903683 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.c_8e61b559 > 0) EXECUTE FUNCTION f_485217dd();
CREATE FUNCTION f_5bfbcb2c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM f_8821e766(NEW.c_05baef20, NEW.c_971e8265);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_484122e8 AFTER INSERT ON t_b56b119d DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_5bfbcb2c();
CREATE FUNCTION f_4b1603d4() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    i uuid; w uuid;
BEGIN
    SELECT o.c_05baef20, o.c_971e8265 INTO i, w FROM t_b56b119d o WHERE o.c_89d0d4ae = NEW.c_07e3dad0;
    PERFORM f_8821e766(i, w);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_77f82118 AFTER INSERT ON t_d370b1d6 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_4b1603d4();

-- ---------------------------------------------------------------------------------------------------------------- rules on insert
-- Eligibility flag: only active goods without lot, serial or expiry tracking whose valuation resolves to moving average and that hold no FIFO position; never removed while an obligation is open.
CREATE FUNCTION f_89cdb0aa() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM 1 FROM t_2f2a7184 WHERE c_be51f22e = NEW.c_59aa8519 FOR UPDATE;              -- serialises with an obligation being created for the item (FOR SHARE there)
    IF NEW.c_7d66a9cb THEN
        IF NOT EXISTS (SELECT 1 FROM t_2f2a7184 i WHERE i.c_be51f22e = NEW.c_59aa8519 AND i.c_74e6031c = 1 AND i.c_18451cd8 AND NOT i.c_5a28ea5e AND NOT i.c_f913e227 AND NOT i.c_6617f8f5) THEN
            RAISE EXCEPTION 'negative_exception_item_not_eligible: only active goods without lot, serial or expiry tracking';
        END IF;
        IF f_23735cac(NEW.c_59aa8519) IS DISTINCT FROM 2 OR EXISTS (SELECT 1 FROM t_a2903683 p WHERE p.c_3380c61e = NEW.c_59aa8519 AND p.c_a71bfccf <> 2) THEN
            RAISE EXCEPTION 'negative_exception_item_not_eligible: only moving weighted average items';
        END IF;
    ELSIF f_cf64ca57(NEW.c_59aa8519, NULL) THEN
        RAISE EXCEPTION 'negative_obligation_open';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_e5805e00 BEFORE INSERT ON t_3fb62185 FOR EACH ROW EXECUTE FUNCTION f_89cdb0aa();

-- Obligation (backstop of the service): eligible item at this moment (flag, active untracked goods, moving average position of this item and warehouse), an issue posting of this document with this operation,
-- an approval of this document, a counter account that is not the obligation account.
CREATE FUNCTION f_b0ee255f() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    g uuid; depth integer := 0;
BEGIN
    -- the valuation policy the item resolves to may change under the inventory master lock (not the posting lock): every group of the chain and the company settings are locked FOR SHARE, so a concurrent policy
    -- change waits for this transaction (and its guard trigger then sees the obligation) or this check sees the committed change
    SELECT c_61dee773 INTO g FROM t_2f2a7184 WHERE c_be51f22e = NEW.c_05baef20;
    WHILE g IS NOT NULL AND depth < 32 LOOP
        PERFORM 1 FROM t_2640a66e WHERE c_01551296 = g FOR SHARE;
        SELECT c_57852ce5 INTO g FROM t_2640a66e WHERE c_01551296 = g;
        depth := depth + 1;
    END LOOP;
    PERFORM 1 FROM t_1cf46c0f FOR SHARE;
    PERFORM 1 FROM t_2f2a7184 WHERE c_be51f22e = NEW.c_05baef20 FOR SHARE;                      -- serialises with a concurrent change of the item (its guard trigger then sees this obligation)
    PERFORM 1 FROM t_a2903683 WHERE c_6b4abdd9 = NEW.c_e58fa36c FOR UPDATE;
    IF NOT EXISTS (SELECT 1 FROM t_2f2a7184 i WHERE i.c_be51f22e = NEW.c_05baef20 AND i.c_74e6031c = 1 AND i.c_18451cd8 AND NOT i.c_5a28ea5e AND NOT i.c_f913e227 AND NOT i.c_6617f8f5)
       OR (SELECT f.c_7d66a9cb FROM t_3fb62185 f WHERE f.c_59aa8519 = NEW.c_05baef20 ORDER BY f.c_dd9030ed DESC LIMIT 1) IS DISTINCT FROM true
       OR NOT EXISTS (SELECT 1 FROM t_a2903683 p WHERE p.c_6b4abdd9 = NEW.c_e58fa36c AND p.c_3380c61e = NEW.c_05baef20 AND p.c_f0a91938 = NEW.c_971e8265 AND p.c_a71bfccf = 2)
       OR f_23735cac(NEW.c_05baef20) IS DISTINCT FROM 2 THEN
        RAISE EXCEPTION 'an obligation needs an eligible moving average item without tracking';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM t_4d4b251f p JOIN t_7eb4c679 l ON l.c_ecf9865c = p.c_fc71418e AND l.c_8c33b614 = NEW.c_acdfca73
                   WHERE p.c_fc71418e = NEW.c_64379fe3 AND p.c_78f9abc2 = 2 AND p.c_eb889fd0 = NEW.c_d366d343 AND p.c_4ce1587f = NEW.c_1872eef0
                     AND p.c_d667f7a4 = NEW.c_b9765532 AND p.c_02d7d540 = NEW.c_1f0961d8
                     AND l.c_7a455438 = NEW.c_05baef20 AND l.c_1f1c9cdf = NEW.c_971e8265 AND l.c_b691de84 >= NEW.c_95d329bc) THEN
        RAISE EXCEPTION 'an obligation belongs to an issue posting line of the same item and warehouse';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM t_40ad576c a WHERE a.c_faadf366 = NEW.c_10088048 AND a.c_7da52180 = NEW.c_d366d343 AND a.c_e75cc8cf <> NEW.c_225cbcd3) THEN
        RAISE EXCEPTION 'an obligation needs an exception approval of its document by someone other than the poster';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_07b8067c BEFORE INSERT ON t_b56b119d FOR EACH ROW EXECUTE FUNCTION f_b0ee255f();

-- Settlement: a forward match belongs to a receipt posting line of the same item and warehouse, dated on or after the obligation, of an obligation that is not reversed; a reversal negates exactly one forward match
-- of a reversed receipt posting. Net figures stay within the obligation, exact remainder on the last match; a receipt line settles at most its own quantity and amount (all of its amount once all of its quantity).
CREATE FUNCTION f_14fbf8bd() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ob record; orig record; nq numeric; np numeric; lq numeric; la numeric;
BEGIN
    SELECT * INTO ob FROM t_b56b119d WHERE c_89d0d4ae = NEW.c_07e3dad0;
    PERFORM 1 FROM t_a2903683 WHERE c_6b4abdd9 = ob.c_e58fa36c FOR UPDATE;               -- every change of an obligation is serialised on its cost position row
    IF NEW.c_094f81ac IS NULL THEN
        IF EXISTS (SELECT 1 FROM t_075e0837 WHERE c_4caf1cf3 = NEW.c_07e3dad0) THEN RAISE EXCEPTION 'a reversed obligation cannot be settled'; END IF;
        IF NEW.c_5e337f95 < ob.c_1f0961d8 THEN RAISE EXCEPTION 'a receipt dated before the obligation cannot settle it'; END IF;
        IF NOT EXISTS (SELECT 1 FROM t_4d4b251f p JOIN t_7eb4c679 l ON l.c_ecf9865c = p.c_fc71418e AND l.c_8c33b614 = NEW.c_42a1e7ee
                       WHERE p.c_fc71418e = NEW.c_58257cfe AND p.c_78f9abc2 = 1 AND p.c_4ce1587f = NEW.c_7208ba7b AND p.c_02d7d540 = NEW.c_5e337f95
                         AND l.c_7a455438 = ob.c_05baef20 AND l.c_1f1c9cdf = ob.c_971e8265 AND l.c_b691de84 = NEW.c_23eac371 AND l.c_ea5e66bd = NEW.c_6f6ab47d) THEN
            RAISE EXCEPTION 'a settlement belongs to a stock-document receipt line of the same item and warehouse';
        END IF;
    ELSE
        SELECT * INTO orig FROM t_d370b1d6 WHERE c_f91bd136 = NEW.c_094f81ac;
        IF orig.c_f91bd136 IS NULL OR orig.c_094f81ac IS NOT NULL OR orig.c_07e3dad0 <> NEW.c_07e3dad0
           OR orig.c_58257cfe <> NEW.c_58257cfe OR orig.c_42a1e7ee <> NEW.c_42a1e7ee OR orig.c_7208ba7b <> NEW.c_7208ba7b
           OR orig.c_23eac371 <> NEW.c_23eac371 OR orig.c_6f6ab47d <> NEW.c_6f6ab47d
           OR NEW.c_e5a64555 <> -orig.c_e5a64555 OR NEW.c_e4745db7 <> -orig.c_e4745db7 OR NEW.c_4972eb11 <> -orig.c_4972eb11
           OR NOT EXISTS (SELECT 1 FROM t_233a2574 rv WHERE rv.c_52d9badb = NEW.c_53ef40f0 AND rv.c_005e3448 = NEW.c_58257cfe) THEN
            RAISE EXCEPTION 'a settlement reversal negates exactly one settlement of a reversed receipt posting';
        END IF;
    END IF;
    SELECT coalesce(sum(c_e5a64555), 0), coalesce(sum(c_e4745db7), 0) INTO nq, np FROM t_d370b1d6 WHERE c_07e3dad0 = NEW.c_07e3dad0;
    nq := nq + NEW.c_e5a64555; np := np + NEW.c_e4745db7;
    IF nq < 0 OR nq > ob.c_95d329bc OR np < 0 OR np > ob.c_af214a8a OR (nq = ob.c_95d329bc AND np <> ob.c_af214a8a) THEN
        RAISE EXCEPTION 'the net settlement of an obligation stays within its quantity and provisional total, with the exact remainder on the last match';
    END IF;
    SELECT coalesce(sum(c_e5a64555), 0), coalesce(sum(c_4972eb11), 0) INTO lq, la FROM t_d370b1d6
     WHERE c_58257cfe = NEW.c_58257cfe AND c_42a1e7ee = NEW.c_42a1e7ee;
    lq := lq + NEW.c_e5a64555; la := la + NEW.c_4972eb11;
    IF lq < 0 OR lq > NEW.c_23eac371 OR la < 0 OR la > NEW.c_6f6ab47d OR (lq = NEW.c_23eac371 AND la <> NEW.c_6f6ab47d) THEN
        RAISE EXCEPTION 'a receipt line settles at most its own quantity and amount';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_5344ceda BEFORE INSERT ON t_d370b1d6 FOR EACH ROW EXECUTE FUNCTION f_14fbf8bd();

-- Obligation reversal: only of an obligation without net settlement, by the reversal of its own issue posting.
CREATE FUNCTION f_1f6fd2d7() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ob record;
BEGIN
    SELECT * INTO ob FROM t_b56b119d WHERE c_89d0d4ae = NEW.c_4caf1cf3;
    PERFORM 1 FROM t_a2903683 WHERE c_6b4abdd9 = ob.c_e58fa36c FOR UPDATE;
    IF coalesce((SELECT sum(c_e5a64555) FROM t_d370b1d6 WHERE c_07e3dad0 = NEW.c_4caf1cf3), 0) <> 0 THEN
        RAISE EXCEPTION 'an obligation with a settlement cannot be reversed: reverse its settlements first';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM t_233a2574 rv WHERE rv.c_52d9badb = NEW.c_4dfed26f AND rv.c_005e3448 = ob.c_64379fe3 AND rv.c_60b20e96 = NEW.c_c79ef8bc) THEN
        RAISE EXCEPTION 'an obligation is reversed only by the reversal of its own issue posting';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_da893055 BEFORE INSERT ON t_075e0837 FOR EACH ROW EXECUTE FUNCTION f_1f6fd2d7();

-- ---------------------------------------------------------------------------------------------------------------- costing operations without entries (DB-1): only for the negative-stock exception, judged at commit
CREATE FUNCTION f_6df04d67() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_bc727c1d WHERE c_eb34b7d8 = NEW.c_f47f15da) THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM t_b56b119d WHERE c_1872eef0 = NEW.c_f47f15da) OR EXISTS (SELECT 1 FROM t_d370b1d6 WHERE c_7208ba7b = NEW.c_f47f15da) THEN RETURN NULL; END IF;
    IF NEW.c_7f2ea712 = 4 AND NOT EXISTS (SELECT 1 FROM t_bc727c1d WHERE c_eb34b7d8 = NEW.c_03a68cea)
       AND (EXISTS (SELECT 1 FROM t_b56b119d WHERE c_1872eef0 = NEW.c_03a68cea) OR EXISTS (SELECT 1 FROM t_d370b1d6 WHERE c_7208ba7b = NEW.c_03a68cea)) THEN
        RETURN NULL;
    END IF;
    RAISE EXCEPTION 'a costing operation without entries is allowed only for a negative-stock obligation or settlement';
END;
$$;
CREATE CONSTRAINT TRIGGER r_d621465c AFTER INSERT ON t_a648ca40 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_6df04d67();

-- ---------------------------------------------------------------------------------------------------------------- the obligation account accepts automatic postings only (R9)
CREATE FUNCTION f_e324c81a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_a78f5224 v WHERE v.c_17133030 = NEW.c_e6bfb072)
       AND EXISTS (SELECT 1 FROM t_f3158869 e WHERE e.c_14f317c6 = NEW.c_39596059 AND e.c_41c437ea = 1 AND e.c_960dbf25 <> 2) THEN
        RAISE EXCEPTION 'account_automatic_only';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_3bc14726 BEFORE INSERT OR UPDATE ON t_783c8d98 FOR EACH ROW EXECUTE FUNCTION f_e324c81a();

-- ---------------------------------------------------------------------------------------------------------------- guarded changes while an obligation is open (R12)
CREATE FUNCTION f_c9eb2c1d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF f_cf64ca57(NEW.c_be51f22e, NULL) THEN RAISE EXCEPTION 'negative_obligation_open'; END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_b3836cf7 AFTER UPDATE ON t_2f2a7184 FOR EACH ROW
    WHEN (OLD.c_61dee773 IS DISTINCT FROM NEW.c_61dee773 OR OLD.c_5a28ea5e IS DISTINCT FROM NEW.c_5a28ea5e OR OLD.c_f913e227 IS DISTINCT FROM NEW.c_f913e227 OR OLD.c_6617f8f5 IS DISTINCT FROM NEW.c_6617f8f5
          OR OLD.c_74e6031c IS DISTINCT FROM NEW.c_74e6031c OR (OLD.c_18451cd8 AND NOT NEW.c_18451cd8))
    EXECUTE FUNCTION f_c9eb2c1d();
CREATE FUNCTION f_d497cd12() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_b56b119d o WHERE f_a21c7b04(o.c_89d0d4ae) > 0 AND f_6efd1234(o.c_05baef20, NEW.c_01551296)) THEN
        RAISE EXCEPTION 'negative_obligation_open';
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_1e0d1868 AFTER UPDATE ON t_2640a66e FOR EACH ROW
    WHEN (OLD.c_cde997ad IS DISTINCT FROM NEW.c_cde997ad OR OLD.c_57852ce5 IS DISTINCT FROM NEW.c_57852ce5)
    EXECUTE FUNCTION f_d497cd12();
CREATE FUNCTION f_681eae1e() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_b56b119d o WHERE f_a21c7b04(o.c_89d0d4ae) > 0 AND f_23735cac(o.c_05baef20) IS DISTINCT FROM 2) THEN
        RAISE EXCEPTION 'negative_obligation_open';
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_b2c26a63 AFTER UPDATE ON t_1cf46c0f FOR EACH ROW WHEN (OLD.c_f0c103a8 IS DISTINCT FROM NEW.c_f0c103a8) EXECUTE FUNCTION f_681eae1e();
CREATE FUNCTION f_8503be21() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF f_cf64ca57(NULL, NEW.c_a9f73cc2) THEN RAISE EXCEPTION 'negative_obligation_open'; END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER r_dc7fee2b AFTER UPDATE ON t_d2006f04 FOR EACH ROW WHEN (OLD.c_49cbc053 AND NOT NEW.c_49cbc053) EXECUTE FUNCTION f_8503be21();
