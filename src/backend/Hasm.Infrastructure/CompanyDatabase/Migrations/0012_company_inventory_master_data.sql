-- Migration 0012: inventory master data (Phase 3.4). Forward-only, no DROP, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.Inventory.cs.
--
-- MASTER DATA ONLY: units of measure, item groups, items, alternate units with exact conversion factors, barcodes and the company inventory settings. There is NO stock movement, quantity, cost layer or
-- accounting entry in this migration. Invariants enforced by PostgreSQL (not only by the application):
--   * codes are normalised upper case, unique and immutable; masters are never deleted or truncated (deactivated instead);
--   * revision advances by exactly one per update (optimistic concurrency);
--   * unit decimals, item type and the item base unit are immutable; barcode value, item, unit and kind are immutable (only the active flag changes);
--   * the item-group tree has no cycles and a bounded depth;
--   * at commit: no alternate unit equals the item base unit, the item default purchase/sales unit and every barcode unit are the base unit or an alternate unit of that item.
-- Valuation method values: 1 FIFO, 2 moving weighted average (D-017). Item type: 1 goods, 2 service (a service is never tracked).

-- Company inventory settings: one row at most (created on first save). DefaultValuation NULL = not decided yet (stockable goods then require a group-level method).
CREATE TABLE t_1cf46c0f (
    c_0868f51b smallint NOT NULL DEFAULT 1,
    c_f0c103a8 smallint NULL,
    c_b8aab360 bigint NOT NULL DEFAULT 1,
    c_eecac29f bigint NOT NULL DEFAULT 1,
    c_764b950c timestamptz NOT NULL,
    CONSTRAINT k_c0a58025 PRIMARY KEY (c_0868f51b),
    CONSTRAINT k_41ce5d2f CHECK (c_0868f51b = 1 AND (c_f0c103a8 IS NULL OR c_f0c103a8 IN (1, 2)) AND c_b8aab360 >= 1 AND c_eecac29f > 0)
);
CREATE FUNCTION f_33791e7e() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'inventory settings cannot be deleted'; END IF;
    IF NEW.c_eecac29f <> OLD.c_eecac29f + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NEW.c_b8aab360 < OLD.c_b8aab360 THEN RAISE EXCEPTION 'the item number counter never moves backwards'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_0c1c1961 BEFORE UPDATE OR DELETE ON t_1cf46c0f FOR EACH ROW EXECUTE FUNCTION f_33791e7e();
CREATE TRIGGER r_65746198 BEFORE TRUNCATE ON t_1cf46c0f FOR EACH STATEMENT EXECUTE FUNCTION f_33791e7e();
REVOKE ALL ON TABLE t_1cf46c0f FROM PUBLIC;

-- Units of measure.
CREATE TABLE t_aa91e313 (
    c_1a9eb66f uuid NOT NULL DEFAULT gen_random_uuid(),
    c_e98ea32d varchar(20) NOT NULL,
    c_2dbd90f5 varchar(100) NULL,
    c_02e63ef2 varchar(100) NULL,
    c_1ee2fd51 smallint NOT NULL,
    c_e6766677 boolean NOT NULL DEFAULT true,
    c_6b82548d bigint NOT NULL DEFAULT 1,
    c_1bd324ca timestamptz NOT NULL,
    c_2fc80e7c timestamptz NOT NULL,
    CONSTRAINT k_a85e079b PRIMARY KEY (c_1a9eb66f),
    CONSTRAINT k_9788a119 CHECK (c_e98ea32d ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT k_4bab0b6f CHECK (
        (c_2dbd90f5 IS NOT NULL OR c_02e63ef2 IS NOT NULL)
        AND (c_2dbd90f5 IS NULL OR char_length(btrim(c_2dbd90f5)) BETWEEN 1 AND 100)
        AND (c_02e63ef2 IS NULL OR char_length(btrim(c_02e63ef2)) BETWEEN 1 AND 100)),
    CONSTRAINT k_64d7d117 CHECK (c_1ee2fd51 BETWEEN 0 AND 6),
    CONSTRAINT k_cb953b32 CHECK (c_6b82548d > 0)
);
CREATE UNIQUE INDEX k_e0764ec7 ON t_aa91e313 (upper(c_e98ea32d));
CREATE FUNCTION f_ad66057e() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'units cannot be deleted'; END IF;
    IF NEW.c_1a9eb66f IS DISTINCT FROM OLD.c_1a9eb66f THEN RAISE EXCEPTION 'unit identifiers are immutable'; END IF;
    IF NEW.c_e98ea32d IS DISTINCT FROM OLD.c_e98ea32d THEN RAISE EXCEPTION 'unit codes are immutable'; END IF;
    IF NEW.c_1ee2fd51 IS DISTINCT FROM OLD.c_1ee2fd51 THEN RAISE EXCEPTION 'unit decimals are immutable'; END IF;
    IF NEW.c_1bd324ca IS DISTINCT FROM OLD.c_1bd324ca THEN RAISE EXCEPTION 'unit creation time is immutable'; END IF;
    IF NEW.c_6b82548d <> OLD.c_6b82548d + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_40966aed BEFORE UPDATE OR DELETE ON t_aa91e313 FOR EACH ROW EXECUTE FUNCTION f_ad66057e();
CREATE TRIGGER r_93f8f8d3 BEFORE TRUNCATE ON t_aa91e313 FOR EACH STATEMENT EXECUTE FUNCTION f_ad66057e();
REVOKE ALL ON TABLE t_aa91e313 FROM PUBLIC;

-- Item groups: a tree (parent optional). Valuation NULL = inherit from the parent, then from the company default.
CREATE TABLE t_2640a66e (
    c_01551296 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_3ac4d7b1 varchar(20) NOT NULL,
    c_969e6e5c varchar(200) NULL,
    c_b1774f64 varchar(200) NULL,
    c_57852ce5 uuid NULL,
    c_cde997ad smallint NULL,
    c_bc9669d9 boolean NOT NULL DEFAULT true,
    c_c629b321 bigint NOT NULL DEFAULT 1,
    c_42e56d9e timestamptz NOT NULL,
    c_bef3cba1 timestamptz NOT NULL,
    CONSTRAINT k_92e65533 PRIMARY KEY (c_01551296),
    CONSTRAINT k_af185ecd FOREIGN KEY (c_57852ce5) REFERENCES t_2640a66e (c_01551296),
    CONSTRAINT k_be07a826 CHECK (c_3ac4d7b1 ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT k_9e4fca1b CHECK (
        (c_969e6e5c IS NOT NULL OR c_b1774f64 IS NOT NULL)
        AND (c_969e6e5c IS NULL OR char_length(btrim(c_969e6e5c)) BETWEEN 1 AND 200)
        AND (c_b1774f64 IS NULL OR char_length(btrim(c_b1774f64)) BETWEEN 1 AND 200)),
    CONSTRAINT k_c7f5b161 CHECK (c_cde997ad IS NULL OR c_cde997ad IN (1, 2)),
    CONSTRAINT k_dd9b7645 CHECK (c_c629b321 > 0),
    CONSTRAINT k_bd3f4920 CHECK (c_57852ce5 IS DISTINCT FROM c_01551296)
);
CREATE UNIQUE INDEX k_d7b9b2d7 ON t_2640a66e (upper(c_3ac4d7b1));
CREATE INDEX k_889e2742 ON t_2640a66e (c_57852ce5);
CREATE FUNCTION f_4864e4e9() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'item groups cannot be deleted'; END IF;
    IF NEW.c_01551296 IS DISTINCT FROM OLD.c_01551296 THEN RAISE EXCEPTION 'item group identifiers are immutable'; END IF;
    IF NEW.c_3ac4d7b1 IS DISTINCT FROM OLD.c_3ac4d7b1 THEN RAISE EXCEPTION 'item group codes are immutable'; END IF;
    IF NEW.c_42e56d9e IS DISTINCT FROM OLD.c_42e56d9e THEN RAISE EXCEPTION 'item group creation time is immutable'; END IF;
    IF NEW.c_c629b321 <> OLD.c_c629b321 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_50b0eeae BEFORE UPDATE OR DELETE ON t_2640a66e FOR EACH ROW EXECUTE FUNCTION f_4864e4e9();
CREATE TRIGGER r_9ea931de BEFORE TRUNCATE ON t_2640a66e FOR EACH STATEMENT EXECUTE FUNCTION f_4864e4e9();
-- The tree has no cycle and at most 8 levels (the new parent chain is followed upwards before the row is stored).
CREATE FUNCTION f_f2149d9d() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    cur uuid := NEW.c_57852ce5;
    depth integer := 1;
BEGIN
    WHILE cur IS NOT NULL LOOP
        IF cur = NEW.c_01551296 THEN RAISE EXCEPTION 'an item group cannot be its own ancestor'; END IF;
        depth := depth + 1;
        IF depth > 8 THEN RAISE EXCEPTION 'the item group tree is limited to 8 levels'; END IF;
        SELECT c_57852ce5 INTO cur FROM t_2640a66e WHERE c_01551296 = cur;
    END LOOP;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_1f7882ef BEFORE INSERT OR UPDATE ON t_2640a66e FOR EACH ROW EXECUTE FUNCTION f_f2149d9d();
REVOKE ALL ON TABLE t_2640a66e FROM PUBLIC;

-- Items. Valuation is NOT stored here: it is resolved from the group chain and the company default (no per-item override). The tracking flags are attributes only (their behaviour belongs to the stock phase).
CREATE TABLE t_2f2a7184 (
    c_be51f22e uuid NOT NULL DEFAULT gen_random_uuid(),
    c_947f1dff varchar(30) NOT NULL,
    c_8875cec0 varchar(200) NULL,
    c_b29bc61e varchar(200) NULL,
    c_74e6031c smallint NOT NULL,
    c_61dee773 uuid NOT NULL,
    c_63bb9134 uuid NOT NULL,
    c_2f3a7e14 uuid NULL,
    c_b3b84b9e uuid NULL,
    c_5a28ea5e boolean NOT NULL DEFAULT false,
    c_f913e227 boolean NOT NULL DEFAULT false,
    c_6617f8f5 boolean NOT NULL DEFAULT false,
    c_3e1ffc1f varchar(1000) NULL,
    c_18451cd8 boolean NOT NULL DEFAULT true,
    c_9ddd31a8 bigint NOT NULL DEFAULT 1,
    c_37070396 timestamptz NOT NULL,
    c_bc0909f7 timestamptz NOT NULL,
    CONSTRAINT k_2c09623b PRIMARY KEY (c_be51f22e),
    CONSTRAINT k_7a471ec8 FOREIGN KEY (c_61dee773) REFERENCES t_2640a66e (c_01551296),
    CONSTRAINT k_2b8dd36c FOREIGN KEY (c_63bb9134) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_8b0377cc FOREIGN KEY (c_2f3a7e14) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_8bd06da4 FOREIGN KEY (c_b3b84b9e) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_4eb56038 CHECK (c_947f1dff ~ '^[A-Z0-9][A-Z0-9_-]{0,29}$'),
    CONSTRAINT k_d4fdd415 CHECK (
        (c_8875cec0 IS NOT NULL OR c_b29bc61e IS NOT NULL)
        AND (c_8875cec0 IS NULL OR char_length(btrim(c_8875cec0)) BETWEEN 1 AND 200)
        AND (c_b29bc61e IS NULL OR char_length(btrim(c_b29bc61e)) BETWEEN 1 AND 200)),
    CONSTRAINT k_5b735e8c CHECK (c_74e6031c IN (1, 2)),
    CONSTRAINT k_de22e5a1 CHECK (c_74e6031c = 1 OR NOT (c_5a28ea5e OR c_f913e227 OR c_6617f8f5)),
    CONSTRAINT k_5b4b06f2 CHECK (c_9ddd31a8 > 0)
);
CREATE UNIQUE INDEX k_7f281e96 ON t_2f2a7184 (upper(c_947f1dff));
CREATE INDEX k_3e715f4f ON t_2f2a7184 (c_61dee773);
CREATE FUNCTION f_f5abe175() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'items cannot be deleted'; END IF;
    IF NEW.c_be51f22e IS DISTINCT FROM OLD.c_be51f22e THEN RAISE EXCEPTION 'item identifiers are immutable'; END IF;
    IF NEW.c_947f1dff IS DISTINCT FROM OLD.c_947f1dff THEN RAISE EXCEPTION 'item codes are immutable'; END IF;
    IF NEW.c_74e6031c IS DISTINCT FROM OLD.c_74e6031c THEN RAISE EXCEPTION 'the item type is immutable'; END IF;
    IF NEW.c_63bb9134 IS DISTINCT FROM OLD.c_63bb9134 THEN RAISE EXCEPTION 'the base unit of an item is immutable'; END IF;
    IF NEW.c_37070396 IS DISTINCT FROM OLD.c_37070396 THEN RAISE EXCEPTION 'item creation time is immutable'; END IF;
    IF NEW.c_9ddd31a8 <> OLD.c_9ddd31a8 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_8dbf3f39 BEFORE UPDATE OR DELETE ON t_2f2a7184 FOR EACH ROW EXECUTE FUNCTION f_f5abe175();
CREATE TRIGGER r_27c96766 BEFORE TRUNCATE ON t_2f2a7184 FOR EACH STATEMENT EXECUTE FUNCTION f_f5abe175();
REVOKE ALL ON TABLE t_2f2a7184 FROM PUBLIC;

-- Alternate units of an item: 1 alternate unit = Factor base units (exact decimal, > 0).
CREATE TABLE t_8c617180 (
    c_e2f980ec uuid NOT NULL,
    c_42ad8d14 uuid NOT NULL,
    c_60e69705 numeric(28,10) NOT NULL,
    CONSTRAINT k_62eb16a5 PRIMARY KEY (c_e2f980ec, c_42ad8d14),
    CONSTRAINT k_84b52957 FOREIGN KEY (c_e2f980ec) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_42c9f654 FOREIGN KEY (c_42ad8d14) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_acd1f8cb CHECK (c_60e69705 > 0)
);
CREATE INDEX k_8323e61c ON t_8c617180 (c_42ad8d14);
REVOKE ALL ON TABLE t_8c617180 FROM PUBLIC;

-- Barcodes: company-wide unique (inactive ones keep their value), never deleted; only the active flag changes. Kind: 1 EAN-13, 2 EAN-8, 3 UPC-A, 4 internal.
CREATE TABLE t_51879811 (
    c_2b1f6575 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_5280289d uuid NOT NULL,
    c_dd549278 uuid NOT NULL,
    c_a0859993 varchar(50) NOT NULL,
    c_a5064116 smallint NOT NULL,
    c_4454cfd3 boolean NOT NULL DEFAULT true,
    c_c5f9ecc6 timestamptz NOT NULL,
    c_426b5488 timestamptz NOT NULL,
    CONSTRAINT k_b2af90d2 PRIMARY KEY (c_2b1f6575),
    CONSTRAINT k_b7c25893 FOREIGN KEY (c_5280289d) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_3ded30be FOREIGN KEY (c_dd549278) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_cc7d58e1 CHECK (c_a0859993 ~ '^[A-Z0-9][A-Z0-9._/-]{0,49}$'),
    CONSTRAINT k_20406eb1 CHECK (c_a5064116 IN (1, 2, 3, 4))
);
CREATE UNIQUE INDEX k_164a91f5 ON t_51879811 (upper(c_a0859993));
CREATE INDEX k_04e5b067 ON t_51879811 (c_5280289d);
CREATE FUNCTION f_def5eab6() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'barcodes cannot be deleted'; END IF;
    IF NEW.c_2b1f6575 IS DISTINCT FROM OLD.c_2b1f6575 OR NEW.c_5280289d IS DISTINCT FROM OLD.c_5280289d
       OR NEW.c_dd549278 IS DISTINCT FROM OLD.c_dd549278 OR NEW.c_a0859993 IS DISTINCT FROM OLD.c_a0859993
       OR NEW.c_a5064116 IS DISTINCT FROM OLD.c_a5064116 OR NEW.c_c5f9ecc6 IS DISTINCT FROM OLD.c_c5f9ecc6 THEN
        RAISE EXCEPTION 'only the active flag of a barcode can change';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_db83e3fe BEFORE UPDATE OR DELETE ON t_51879811 FOR EACH ROW EXECUTE FUNCTION f_def5eab6();
CREATE TRIGGER r_72e317a1 BEFORE TRUNCATE ON t_51879811 FOR EACH STATEMENT EXECUTE FUNCTION f_def5eab6();
REVOKE ALL ON TABLE t_51879811 FROM PUBLIC;

-- Cross-table unit rules, judged at commit so an item and its units can be written in one transaction.
CREATE FUNCTION f_d5bd9b80(p_item uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_8c617180 u JOIN t_2f2a7184 i ON i.c_be51f22e = u.c_e2f980ec
               WHERE i.c_be51f22e = p_item AND u.c_42ad8d14 = i.c_63bb9134) THEN
        RAISE EXCEPTION 'an alternate unit cannot be the base unit of the item';
    END IF;
    IF EXISTS (SELECT 1 FROM t_2f2a7184 i WHERE i.c_be51f22e = p_item AND (
            (i.c_2f3a7e14 IS NOT NULL AND i.c_2f3a7e14 <> i.c_63bb9134
                AND NOT EXISTS (SELECT 1 FROM t_8c617180 u WHERE u.c_e2f980ec = i.c_be51f22e AND u.c_42ad8d14 = i.c_2f3a7e14))
         OR (i.c_b3b84b9e IS NOT NULL AND i.c_b3b84b9e <> i.c_63bb9134
                AND NOT EXISTS (SELECT 1 FROM t_8c617180 u WHERE u.c_e2f980ec = i.c_be51f22e AND u.c_42ad8d14 = i.c_b3b84b9e)))) THEN
        RAISE EXCEPTION 'the purchase and sales units must be the base unit or an alternate unit of the item';
    END IF;
    IF EXISTS (SELECT 1 FROM t_51879811 b JOIN t_2f2a7184 i ON i.c_be51f22e = b.c_5280289d
               WHERE i.c_be51f22e = p_item AND b.c_dd549278 <> i.c_63bb9134
                 AND NOT EXISTS (SELECT 1 FROM t_8c617180 u WHERE u.c_e2f980ec = i.c_be51f22e AND u.c_42ad8d14 = b.c_dd549278)) THEN
        RAISE EXCEPTION 'a barcode unit must be the base unit or an alternate unit of the item';
    END IF;
END;
$$;
CREATE FUNCTION f_b0e186fc() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN PERFORM f_d5bd9b80(NEW.c_be51f22e); RETURN NULL; END;
$$;
CREATE FUNCTION f_26717e64() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN PERFORM f_d5bd9b80(OLD.c_e2f980ec); ELSE PERFORM f_d5bd9b80(NEW.c_e2f980ec); END IF;
    RETURN NULL;
END;
$$;
CREATE FUNCTION f_a9da9aff() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN PERFORM f_d5bd9b80(NEW.c_5280289d); RETURN NULL; END;
$$;
CREATE CONSTRAINT TRIGGER r_12570388 AFTER INSERT OR UPDATE ON t_2f2a7184 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_b0e186fc();
CREATE CONSTRAINT TRIGGER r_00fa1bae AFTER INSERT OR UPDATE OR DELETE ON t_8c617180 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_26717e64();
CREATE CONSTRAINT TRIGGER r_4972557f AFTER INSERT OR UPDATE ON t_51879811 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_a9da9aff();
