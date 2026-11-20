-- Migration 0027: customer groups (Phase 3.11). Forward-only, additive (two new tables and one nullable column on each of two tables); no existing row is read or modified, nothing is seeded.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0027-customer-groups.cjs (logical dictionary: CompanySchema.CustomerGroups.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 8.1: price lists and discounts by customer GROUP as well as by customer): the company's customer-group master (code, names, active flag), the assignment of a customer party to AT MOST ONE group
-- at a time (one row per party: the group, or none), and the group scope of price lists and discount rules. It is CONFIGURATION: no document or amount exists here. Immutable pricing snapshots of final
-- documents keep the values that applied, so a later regrouping or list change never re-prices them.
-- Invariants enforced by PostgreSQL:
--   * groups and assignments are never removed; a group's code and creation data never change; every change advances the revision by one;
--   * a party has at most one assignment row (primary key on the party), so it belongs to at most one group at a time;
--   * a price list or discount rule is scoped to a customer OR a customer group, never both (the engine refuses both); the group of a price list or rule is immutable (a different group is a new list or rule);
--   * two ACTIVE discount rules never share customer, group, item scope, priority, minimum quantity and start (the earlier tie index, now including the group).

CREATE TABLE t_3198d6e8 (
    c_d69a5fd6 uuid NOT NULL,
    c_1e71fb56 varchar(30) NOT NULL,
    c_eadf1eac varchar(200) NOT NULL,
    c_07fbf6f1 varchar(200) NULL,
    c_9c92c5ec boolean NOT NULL DEFAULT true,
    c_a88fc748 bigint NOT NULL DEFAULT 1,
    c_c7d1447f uuid NOT NULL,
    c_22b66b41 timestamptz NOT NULL,
    c_eb78fae7 timestamptz NOT NULL,
    CONSTRAINT k_90b86af8 PRIMARY KEY (c_d69a5fd6),
    CONSTRAINT k_b15c6ae5 CHECK (c_a88fc748 > 0 AND c_1e71fb56 ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim(c_eadf1eac)) >= 1)
);
CREATE UNIQUE INDEX k_b97e0640 ON t_3198d6e8 (c_1e71fb56);

CREATE FUNCTION f_bacad3cb() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_a88fc748 <> 1 THEN RAISE EXCEPTION 'a customer group is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'customer groups are never removed'; END IF;
    IF NEW.c_d69a5fd6 IS DISTINCT FROM OLD.c_d69a5fd6 OR NEW.c_1e71fb56 IS DISTINCT FROM OLD.c_1e71fb56
       OR NEW.c_c7d1447f IS DISTINCT FROM OLD.c_c7d1447f OR NEW.c_22b66b41 IS DISTINCT FROM OLD.c_22b66b41 THEN
        RAISE EXCEPTION 'the identity, code and creation data of a customer group are immutable';
    END IF;
    IF NEW.c_a88fc748 <> OLD.c_a88fc748 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_4c3c1493 BEFORE INSERT OR UPDATE OR DELETE ON t_3198d6e8 FOR EACH ROW EXECUTE FUNCTION f_bacad3cb();
CREATE TRIGGER r_6b017acb BEFORE TRUNCATE ON t_3198d6e8 FOR EACH STATEMENT EXECUTE FUNCTION f_bacad3cb();
REVOKE ALL ON TABLE t_3198d6e8 FROM PUBLIC;

CREATE TABLE t_b2376138 (
    c_e736c276 uuid NOT NULL,
    c_87956e3c uuid NULL,
    c_9136a17b bigint NOT NULL DEFAULT 1,
    c_d5dabe33 uuid NOT NULL,
    c_f2d52e9f timestamptz NOT NULL,
    c_d71ce346 timestamptz NOT NULL,
    CONSTRAINT k_70a511cf PRIMARY KEY (c_e736c276),
    CONSTRAINT k_90515d83 FOREIGN KEY (c_e736c276) REFERENCES t_8edce6b0 (c_3f494703),
    CONSTRAINT k_e9546629 FOREIGN KEY (c_87956e3c) REFERENCES t_3198d6e8 (c_d69a5fd6),
    CONSTRAINT k_679a96d9 CHECK (c_9136a17b > 0)
);
CREATE INDEX k_b6f38ac9 ON t_b2376138 (c_87956e3c) WHERE c_87956e3c IS NOT NULL;

CREATE FUNCTION f_bf0551e2() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_9136a17b <> 1 THEN RAISE EXCEPTION 'a customer group assignment is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'customer group assignments are never removed'; END IF;
    IF NEW.c_e736c276 IS DISTINCT FROM OLD.c_e736c276
       OR NEW.c_d5dabe33 IS DISTINCT FROM OLD.c_d5dabe33 OR NEW.c_f2d52e9f IS DISTINCT FROM OLD.c_f2d52e9f THEN
        RAISE EXCEPTION 'the party and creation data of a customer group assignment are immutable';
    END IF;
    IF NEW.c_9136a17b <> OLD.c_9136a17b + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_abee346f BEFORE INSERT OR UPDATE OR DELETE ON t_b2376138 FOR EACH ROW EXECUTE FUNCTION f_bf0551e2();
CREATE TRIGGER r_0240c962 BEFORE TRUNCATE ON t_b2376138 FOR EACH STATEMENT EXECUTE FUNCTION f_bf0551e2();
REVOKE ALL ON TABLE t_b2376138 FROM PUBLIC;

-- price lists: the optional group scope (a customer OR a group, never both; the group is fixed at creation)
ALTER TABLE t_1657774e ADD COLUMN c_6bb2b0c0 uuid NULL;
ALTER TABLE t_1657774e ADD CONSTRAINT k_c1198024 FOREIGN KEY (c_6bb2b0c0) REFERENCES t_3198d6e8 (c_d69a5fd6);
ALTER TABLE t_1657774e ADD CONSTRAINT k_9b0d0436 CHECK (c_becb6ce0 IS NULL OR c_6bb2b0c0 IS NULL);

CREATE OR REPLACE FUNCTION f_4fab1a41() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_9c26e24a <> 1 THEN RAISE EXCEPTION 'a price list is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'price lists are never removed'; END IF;
    IF NEW.c_2b80e293 IS DISTINCT FROM OLD.c_2b80e293 OR NEW.c_801c02aa IS DISTINCT FROM OLD.c_801c02aa OR NEW.c_76c6a627 IS DISTINCT FROM OLD.c_76c6a627
       OR NEW.c_becb6ce0 IS DISTINCT FROM OLD.c_becb6ce0 OR NEW.c_6bb2b0c0 IS DISTINCT FROM OLD.c_6bb2b0c0
       OR NEW.c_78f23c8f IS DISTINCT FROM OLD.c_78f23c8f OR NEW.c_162def65 IS DISTINCT FROM OLD.c_162def65 THEN
        RAISE EXCEPTION 'the identity, code, currency, customer, customer group and creation data of a price list are immutable';
    END IF;
    IF NEW.c_9c26e24a <> OLD.c_9c26e24a + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;

-- discount rules: the optional group scope, and the active-tie index REPLACED by one that includes the group (new generated name: names are never reused)
ALTER TABLE t_98209637 ADD COLUMN c_2a3bedc2 uuid NULL;
ALTER TABLE t_98209637 ADD CONSTRAINT k_84c65bb7 FOREIGN KEY (c_2a3bedc2) REFERENCES t_3198d6e8 (c_d69a5fd6);
ALTER TABLE t_98209637 ADD CONSTRAINT k_812d07a0 CHECK (c_267fdd7a IS NULL OR c_2a3bedc2 IS NULL);

DROP INDEX k_7396728f;
CREATE UNIQUE INDEX k_c912a6df ON t_98209637 (COALESCE(c_267fdd7a, '00000000-0000-0000-0000-000000000000'::uuid),
    COALESCE(c_2a3bedc2, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(c_27971215, '00000000-0000-0000-0000-000000000000'::uuid),
    COALESCE(c_1561271a, '00000000-0000-0000-0000-000000000000'::uuid), c_13ac6fbc, c_4aa76d4e, c_8d69b845) WHERE c_8e780ef0;

CREATE OR REPLACE FUNCTION f_86f87c19() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_fa711175 <> 1 THEN RAISE EXCEPTION 'a discount rule is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'discount rules are never removed'; END IF;
    IF NEW.c_f367d2eb IS DISTINCT FROM OLD.c_f367d2eb OR NEW.c_1b96af38 IS DISTINCT FROM OLD.c_1b96af38
       OR NEW.c_267fdd7a IS DISTINCT FROM OLD.c_267fdd7a OR NEW.c_2a3bedc2 IS DISTINCT FROM OLD.c_2a3bedc2
       OR NEW.c_27971215 IS DISTINCT FROM OLD.c_27971215 OR NEW.c_1561271a IS DISTINCT FROM OLD.c_1561271a
       OR NEW.c_c8c197dc IS DISTINCT FROM OLD.c_c8c197dc OR NEW.c_43b97137 IS DISTINCT FROM OLD.c_43b97137 THEN
        RAISE EXCEPTION 'the identity, code, scope and creation data of a discount rule are immutable';
    END IF;
    IF NEW.c_fa711175 <> OLD.c_fa711175 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
