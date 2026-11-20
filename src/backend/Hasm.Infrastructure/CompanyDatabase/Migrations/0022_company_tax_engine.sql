-- Migration 0022: tax engine configuration (Phase 3.10 slice 2b). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0022-tax.cjs (logical dictionary: CompanySchema.Tax.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 6.1-6.3): the company's taxes, their DATED rate versions, item and party classifications and their assignments, the rules that say which taxes apply to which operation of which
-- document type, and the versioned rounding policy. It is CONFIGURATION: no document, amount or journal exists here. A final document stores an immutable snapshot of what applied (TaxSnapshots),
-- so a later change of any row below can never alter a document that already exists.
-- Invariants enforced by PostgreSQL:
--   * taxes, classes and rules are never removed; code/kind/identity and creation data never change; every change advances the revision by one;
--   * a rate version is append-only (no update, delete or truncate) and is strictly later than every earlier version of its tax; its treatment/rate are consistent (only Standard has a rate);
--   * a rounding-policy version is append-only; an item is assigned an item class and a party a party class (never the other kind);
--   * a rule names taxes and classes of the company only; its dates, priority and treatment override are within range.
-- tax class kind: 1 item, 2 party. treatment: 1 standard, 2 zero rated, 3 exempt, 4 not applicable. override: 2, 3 or 4 (null = the dated rate). rounding mode: 1 half away from zero, 2 half even; level: 1 per line, 2 per document.

CREATE TABLE t_e04d482b (
    c_0139c549 uuid NOT NULL,
    c_47bf6f5b varchar(30) NOT NULL,
    c_0c594730 varchar(200) NOT NULL,
    c_0c29a6a4 varchar(200) NULL,
    c_e902abc4 boolean NOT NULL DEFAULT true,
    c_c7fc9456 bigint NOT NULL DEFAULT 1,
    c_ca4d9325 uuid NOT NULL,
    c_0c814649 timestamptz NOT NULL,
    c_189aacfd timestamptz NOT NULL,
    CONSTRAINT k_bcc3a564 PRIMARY KEY (c_0139c549),
    CONSTRAINT k_09de7588 CHECK (c_c7fc9456 > 0 AND c_47bf6f5b ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim(c_0c594730)) >= 1)
);
CREATE UNIQUE INDEX k_38f9ee55 ON t_e04d482b (c_47bf6f5b);

CREATE FUNCTION f_0d952084() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_c7fc9456 <> 1 THEN RAISE EXCEPTION 'a tax is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'taxes are never removed'; END IF;
    IF NEW.c_0139c549 IS DISTINCT FROM OLD.c_0139c549 OR NEW.c_47bf6f5b IS DISTINCT FROM OLD.c_47bf6f5b
       OR NEW.c_ca4d9325 IS DISTINCT FROM OLD.c_ca4d9325 OR NEW.c_0c814649 IS DISTINCT FROM OLD.c_0c814649 THEN
        RAISE EXCEPTION 'the identity, code and creation data of a tax are immutable';
    END IF;
    IF NEW.c_c7fc9456 <> OLD.c_c7fc9456 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_489cb040 BEFORE INSERT OR UPDATE OR DELETE ON t_e04d482b FOR EACH ROW EXECUTE FUNCTION f_0d952084();
CREATE TRIGGER r_a0345c53 BEFORE TRUNCATE ON t_e04d482b FOR EACH STATEMENT EXECUTE FUNCTION f_0d952084();
REVOKE ALL ON TABLE t_e04d482b FROM PUBLIC;

CREATE TABLE t_df098e34 (
    c_9f254f93 uuid NOT NULL,
    c_70daac0c uuid NOT NULL,
    c_cf776350 date NOT NULL,
    c_84f44e34 smallint NOT NULL,
    c_cb40192d numeric(10,6) NOT NULL,
    c_6a90dddd uuid NOT NULL,
    c_a03780b4 timestamptz NOT NULL,
    CONSTRAINT k_3818443c PRIMARY KEY (c_9f254f93),
    CONSTRAINT k_9fea9fcd FOREIGN KEY (c_70daac0c) REFERENCES t_e04d482b (c_0139c549),
    CONSTRAINT k_5393499e CHECK (c_84f44e34 IN (1, 2, 3, 4) AND c_cb40192d BETWEEN 0 AND 1000
        AND (c_84f44e34 = 1 OR c_cb40192d = 0))
);
CREATE UNIQUE INDEX k_6e9f93ca ON t_df098e34 (c_70daac0c, c_cf776350);

CREATE FUNCTION f_d88fa6f7() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'tax rate versions are append-only'; END IF;
    IF EXISTS (SELECT 1 FROM t_df098e34 WHERE c_70daac0c = NEW.c_70daac0c AND c_cf776350 >= NEW.c_cf776350) THEN
        RAISE EXCEPTION 'a tax rate version must be later than every earlier version of its tax';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_ec94428e BEFORE INSERT OR UPDATE OR DELETE ON t_df098e34 FOR EACH ROW EXECUTE FUNCTION f_d88fa6f7();
CREATE TRIGGER r_64a765c8 BEFORE TRUNCATE ON t_df098e34 FOR EACH STATEMENT EXECUTE FUNCTION f_d88fa6f7();
REVOKE ALL ON TABLE t_df098e34 FROM PUBLIC;

CREATE TABLE t_a9bf30e4 (
    c_e595c897 uuid NOT NULL,
    c_dc68c2c9 smallint NOT NULL,
    c_db731d56 varchar(30) NOT NULL,
    c_458bbf27 varchar(200) NOT NULL,
    c_40d3243b varchar(200) NULL,
    c_a679836c boolean NOT NULL DEFAULT true,
    c_cbeb38c5 bigint NOT NULL DEFAULT 1,
    c_da667131 uuid NOT NULL,
    c_affcf1e3 timestamptz NOT NULL,
    c_7ba4cdf0 timestamptz NOT NULL,
    CONSTRAINT k_52804c6b PRIMARY KEY (c_e595c897),
    CONSTRAINT k_90fc0eaf CHECK (c_dc68c2c9 IN (1, 2) AND c_cbeb38c5 > 0 AND c_db731d56 ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim(c_458bbf27)) >= 1)
);
CREATE UNIQUE INDEX k_be5ff668 ON t_a9bf30e4 (c_e595c897, c_dc68c2c9);
CREATE UNIQUE INDEX k_07e791cd ON t_a9bf30e4 (c_dc68c2c9, c_db731d56);

CREATE FUNCTION f_fd47ac3d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_cbeb38c5 <> 1 THEN RAISE EXCEPTION 'a tax class is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'tax classes are never removed'; END IF;
    IF NEW.c_e595c897 IS DISTINCT FROM OLD.c_e595c897 OR NEW.c_dc68c2c9 IS DISTINCT FROM OLD.c_dc68c2c9 OR NEW.c_db731d56 IS DISTINCT FROM OLD.c_db731d56
       OR NEW.c_da667131 IS DISTINCT FROM OLD.c_da667131 OR NEW.c_affcf1e3 IS DISTINCT FROM OLD.c_affcf1e3 THEN
        RAISE EXCEPTION 'the identity, kind, code and creation data of a tax class are immutable';
    END IF;
    IF NEW.c_cbeb38c5 <> OLD.c_cbeb38c5 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_2a21418e BEFORE INSERT OR UPDATE OR DELETE ON t_a9bf30e4 FOR EACH ROW EXECUTE FUNCTION f_fd47ac3d();
CREATE TRIGGER r_46a154f8 BEFORE TRUNCATE ON t_a9bf30e4 FOR EACH STATEMENT EXECUTE FUNCTION f_fd47ac3d();
REVOKE ALL ON TABLE t_a9bf30e4 FROM PUBLIC;

CREATE TABLE t_3fe810e4 (
    c_463e5d17 uuid NOT NULL,
    c_61bd3ea9 uuid NOT NULL,
    c_881bbb32 smallint NOT NULL DEFAULT 1,
    c_0890d8a8 uuid NOT NULL,
    c_705c2947 timestamptz NOT NULL,
    CONSTRAINT k_b6045f2a PRIMARY KEY (c_463e5d17),
    CONSTRAINT k_dfefa076 FOREIGN KEY (c_463e5d17) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_d7f2e27e FOREIGN KEY (c_61bd3ea9, c_881bbb32) REFERENCES t_a9bf30e4 (c_e595c897, c_dc68c2c9),
    CONSTRAINT k_d36811ca CHECK (c_881bbb32 = 1)
);
REVOKE ALL ON TABLE t_3fe810e4 FROM PUBLIC;

CREATE TABLE t_078a3dd6 (
    c_0d3e8a7e uuid NOT NULL,
    c_017d316b uuid NOT NULL,
    c_9986b956 smallint NOT NULL DEFAULT 2,
    c_ab55a43d uuid NOT NULL,
    c_c9b6d394 timestamptz NOT NULL,
    CONSTRAINT k_4853ec9c PRIMARY KEY (c_0d3e8a7e),
    CONSTRAINT k_858c2c07 FOREIGN KEY (c_0d3e8a7e) REFERENCES t_8edce6b0 (c_3f494703),
    CONSTRAINT k_2f750ec4 FOREIGN KEY (c_017d316b, c_9986b956) REFERENCES t_a9bf30e4 (c_e595c897, c_dc68c2c9),
    CONSTRAINT k_17f98ccb CHECK (c_9986b956 = 2)
);
REVOKE ALL ON TABLE t_078a3dd6 FROM PUBLIC;

CREATE TABLE t_7c5f20ac (
    c_e9395319 uuid NOT NULL,
    c_bd7de28f varchar(40) NOT NULL,
    c_21abc0bb varchar(40) NOT NULL,
    c_5d2e0056 uuid NULL,
    c_536cb8da smallint GENERATED ALWAYS AS (CASE WHEN c_5d2e0056 IS NULL THEN NULL ELSE 1::smallint END) STORED,
    c_2dd7fbab uuid NULL,
    c_27fb63b1 smallint GENERATED ALWAYS AS (CASE WHEN c_2dd7fbab IS NULL THEN NULL ELSE 2::smallint END) STORED,
    c_2ab8fa72 uuid NOT NULL,
    c_7936078e integer NOT NULL,
    c_f6472029 date NOT NULL,
    c_9aa140df date NULL,
    c_1ff105ff boolean NOT NULL DEFAULT true,
    c_6919c09d smallint NULL,
    c_681f9a43 bigint NOT NULL DEFAULT 1,
    c_d68cb266 uuid NOT NULL,
    c_6395211f timestamptz NOT NULL,
    c_8876aa35 timestamptz NOT NULL,
    CONSTRAINT k_46c80fc4 PRIMARY KEY (c_e9395319),
    CONSTRAINT k_4c8ac526 FOREIGN KEY (c_2ab8fa72) REFERENCES t_e04d482b (c_0139c549),
    CONSTRAINT k_58507ead FOREIGN KEY (c_5d2e0056, c_536cb8da) REFERENCES t_a9bf30e4 (c_e595c897, c_dc68c2c9),
    CONSTRAINT k_88fa87db FOREIGN KEY (c_2dd7fbab, c_27fb63b1) REFERENCES t_a9bf30e4 (c_e595c897, c_dc68c2c9),
    CONSTRAINT k_610e8d6f CHECK (c_681f9a43 > 0 AND c_7936078e BETWEEN 1 AND 1000 AND (c_9aa140df IS NULL OR c_9aa140df >= c_f6472029)
        AND (c_6919c09d IS NULL OR c_6919c09d IN (2, 3, 4))
        AND c_bd7de28f ~ '^[a-z][a-z0-9_.-]{0,39}$' AND c_21abc0bb ~ '^[a-z][a-z0-9_.-]{0,39}$')
);
CREATE INDEX k_51ae7510 ON t_7c5f20ac (c_bd7de28f, c_21abc0bb);

CREATE FUNCTION f_17797338() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_681f9a43 <> 1 THEN RAISE EXCEPTION 'a tax rule is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'tax rules are never removed'; END IF;
    IF NEW.c_e9395319 IS DISTINCT FROM OLD.c_e9395319 OR NEW.c_d68cb266 IS DISTINCT FROM OLD.c_d68cb266 OR NEW.c_6395211f IS DISTINCT FROM OLD.c_6395211f THEN
        RAISE EXCEPTION 'the identity and creation data of a tax rule are immutable';
    END IF;
    IF NEW.c_681f9a43 <> OLD.c_681f9a43 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_571751a2 BEFORE INSERT OR UPDATE OR DELETE ON t_7c5f20ac FOR EACH ROW EXECUTE FUNCTION f_17797338();
CREATE TRIGGER r_19448c6f BEFORE TRUNCATE ON t_7c5f20ac FOR EACH STATEMENT EXECUTE FUNCTION f_17797338();
REVOKE ALL ON TABLE t_7c5f20ac FROM PUBLIC;

CREATE TABLE t_90ffcb0e (
    c_1ca4b385 uuid NOT NULL,
    c_b3cf7851 uuid NOT NULL,
    CONSTRAINT k_7265aadc PRIMARY KEY (c_1ca4b385, c_b3cf7851),
    CONSTRAINT k_72c1c4e5 FOREIGN KEY (c_1ca4b385) REFERENCES t_7c5f20ac (c_e9395319),
    CONSTRAINT k_26e035e1 FOREIGN KEY (c_b3cf7851) REFERENCES t_e04d482b (c_0139c549)
);
-- a rule's tax base may change only in the SAME transaction that wrote the rule row itself (insert, or an update that advanced its revision): the base can never be edited behind the
-- revision fence. The rule row's xmin equals the current transaction id exactly when this transaction inserted or updated it.
-- Limitation (fail-closed): inside a savepoint or subtransaction the row xmin is a subtransaction id, so such a write is refused; no savepoint is used on this path.
CREATE FUNCTION f_831e3a7a() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE rid uuid; written boolean;
BEGIN
    IF TG_OP = 'DELETE' THEN rid := OLD.c_1ca4b385; ELSE rid := NEW.c_1ca4b385; END IF;
    SELECT (xmin::text = (txid_current() & 4294967295)::text) INTO written FROM t_7c5f20ac WHERE c_e9395319 = rid;
    IF written IS DISTINCT FROM true THEN RAISE EXCEPTION 'a tax rule base changes only together with its rule (revision advance)'; END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_93dc07ce BEFORE INSERT OR DELETE ON t_90ffcb0e FOR EACH ROW EXECUTE FUNCTION f_831e3a7a();
REVOKE ALL ON TABLE t_90ffcb0e FROM PUBLIC;

CREATE TABLE t_606b37f9 (
    c_8c0b99b4 uuid NOT NULL,
    c_42084474 integer NOT NULL,
    c_fe4f8faa smallint NOT NULL,
    c_99491b7d smallint NOT NULL,
    c_a80c2980 smallint NOT NULL,
    c_f3778e71 uuid NOT NULL,
    c_f15797ce timestamptz NOT NULL,
    CONSTRAINT k_d0cc5cd9 PRIMARY KEY (c_8c0b99b4),
    CONSTRAINT k_9de4114e CHECK (c_42084474 > 0 AND c_fe4f8faa BETWEEN 0 AND 6 AND c_99491b7d IN (1, 2) AND c_a80c2980 IN (1, 2))
);
CREATE UNIQUE INDEX k_234b7463 ON t_606b37f9 (c_42084474);

CREATE FUNCTION f_2abc1fa9() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'tax policy versions are append-only'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_a23d45d4 BEFORE INSERT OR UPDATE OR DELETE ON t_606b37f9 FOR EACH ROW EXECUTE FUNCTION f_2abc1fa9();
CREATE TRIGGER r_796a2e90 BEFORE TRUNCATE ON t_606b37f9 FOR EACH STATEMENT EXECUTE FUNCTION f_2abc1fa9();
REVOKE ALL ON TABLE t_606b37f9 FROM PUBLIC;
