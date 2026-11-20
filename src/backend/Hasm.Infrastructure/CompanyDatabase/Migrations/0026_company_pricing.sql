-- Migration 0026: pricing configuration (Phase 3.11 slice 2b). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0026-pricing.cjs (logical dictionary: CompanySchema.Pricing.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 8.1-8.4): the company's pricing policy (rounding scales, margin control, and the company-configurable precedence of price lists and discounts; owner decision P), its price lists with
-- their price entries (per item or item group, unit and quantity tier) and its percentage discount rules. It is CONFIGURATION: no document, amount or journal exists here. A final document stores an immutable
-- pricing snapshot of what applied (PricingSnapshots), so a later change of any row below can never re-price a document that already exists.
-- NOT here yet (a later slice, forward migration): the customer-group scope. There is no customer-group registry in the system, so no group column exists; the engine's group scope stays unused until then.
-- Invariants enforced by PostgreSQL:
--   * price lists, price entries and discount rules are never removed; their code/identity/scope and creation data never change; every change advances the revision by one;
--   * a policy version is append-only and is exactly one above the latest (versions 1, 2, 3 ...); scales, margin, action and precedences are within their ranges;
--   * a price list is for ONE customer or general; its currency is three capital letters; its dates are in range; an entry names an item OR an item group (never both, never neither), a unit, a
--     non-negative quantity tier and a non-negative price within the engine's bounds; two entries of one list never share the same item/group, unit and tier;
--   * a discount rule has a percentage within 0-100 and a non-negative minimum quantity; its item scope is an item, an item group or neither; two active rules never share scope, priority, minimum quantity and start.
-- margin action: 1 warn, 2 require approval, 3 block. list precedence: 1 scope then priority (default), 2 priority then scope. discount precedence: 1 specificity then priority (default), 2 priority then specificity.

CREATE TABLE t_fb7a5f4a (
    c_3e07154d uuid NOT NULL,
    c_9cce6756 integer NOT NULL,
    c_81333834 smallint NOT NULL,
    c_a2344737 smallint NOT NULL,
    c_f26d749a numeric(9,4) NULL,
    c_0b6d8588 smallint NOT NULL,
    c_3aee7e6d smallint NOT NULL,
    c_71e39ef8 smallint NOT NULL,
    c_a40ad790 uuid NOT NULL,
    c_ec695a6f timestamptz NOT NULL,
    CONSTRAINT k_bed466bc PRIMARY KEY (c_3e07154d),
    CONSTRAINT k_c1939d98 CHECK (c_9cce6756 > 0 AND c_81333834 BETWEEN 0 AND 6 AND c_a2344737 BETWEEN 0 AND 6
        AND (c_f26d749a IS NULL OR c_f26d749a BETWEEN -1000 AND 100) AND c_0b6d8588 IN (1, 2, 3)
        AND c_3aee7e6d IN (1, 2) AND c_71e39ef8 IN (1, 2))
);
CREATE UNIQUE INDEX k_9a554aec ON t_fb7a5f4a (c_9cce6756);

CREATE FUNCTION f_acc1fb28() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_9cce6756 <> COALESCE((SELECT max(p.c_9cce6756) FROM t_fb7a5f4a p), 0) + 1 THEN
            RAISE EXCEPTION 'a pricing policy version must be exactly one above the latest';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'pricing policy versions are append-only';
END;
$$;
CREATE TRIGGER r_40a32852 BEFORE INSERT OR UPDATE OR DELETE ON t_fb7a5f4a FOR EACH ROW EXECUTE FUNCTION f_acc1fb28();
CREATE TRIGGER r_00b1998f BEFORE TRUNCATE ON t_fb7a5f4a FOR EACH STATEMENT EXECUTE FUNCTION f_acc1fb28();
REVOKE ALL ON TABLE t_fb7a5f4a FROM PUBLIC;

CREATE TABLE t_1657774e (
    c_2b80e293 uuid NOT NULL,
    c_801c02aa varchar(30) NOT NULL,
    c_40dfc2f5 varchar(200) NOT NULL,
    c_a4a8358e varchar(200) NULL,
    c_9748ea58 integer NOT NULL,
    c_76c6a627 char(3) NOT NULL,
    c_5eff1bf4 date NOT NULL,
    c_55ee99d3 date NULL,
    c_82f2e9c3 boolean NOT NULL DEFAULT true,
    c_becb6ce0 uuid NULL,
    c_9c26e24a bigint NOT NULL DEFAULT 1,
    c_78f23c8f uuid NOT NULL,
    c_162def65 timestamptz NOT NULL,
    c_719ddc53 timestamptz NOT NULL,
    CONSTRAINT k_5808eda2 PRIMARY KEY (c_2b80e293),
    CONSTRAINT k_5cb5268c FOREIGN KEY (c_becb6ce0) REFERENCES t_8edce6b0 (c_3f494703),
    CONSTRAINT k_3539e40f CHECK (c_9c26e24a > 0 AND c_801c02aa ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim(c_40dfc2f5)) >= 1
        AND c_9748ea58 BETWEEN 0 AND 1000000 AND c_76c6a627 ~ '^[A-Z]{3}$' AND (c_55ee99d3 IS NULL OR c_55ee99d3 >= c_5eff1bf4))
);
CREATE UNIQUE INDEX k_652a5a53 ON t_1657774e (c_801c02aa);

CREATE FUNCTION f_4fab1a41() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_9c26e24a <> 1 THEN RAISE EXCEPTION 'a price list is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'price lists are never removed'; END IF;
    IF NEW.c_2b80e293 IS DISTINCT FROM OLD.c_2b80e293 OR NEW.c_801c02aa IS DISTINCT FROM OLD.c_801c02aa OR NEW.c_76c6a627 IS DISTINCT FROM OLD.c_76c6a627
       OR NEW.c_becb6ce0 IS DISTINCT FROM OLD.c_becb6ce0
       OR NEW.c_78f23c8f IS DISTINCT FROM OLD.c_78f23c8f OR NEW.c_162def65 IS DISTINCT FROM OLD.c_162def65 THEN
        RAISE EXCEPTION 'the identity, code, currency, customer and creation data of a price list are immutable';
    END IF;
    IF NEW.c_9c26e24a <> OLD.c_9c26e24a + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_a1a44649 BEFORE INSERT OR UPDATE OR DELETE ON t_1657774e FOR EACH ROW EXECUTE FUNCTION f_4fab1a41();
CREATE TRIGGER r_c2e87bea BEFORE TRUNCATE ON t_1657774e FOR EACH STATEMENT EXECUTE FUNCTION f_4fab1a41();
REVOKE ALL ON TABLE t_1657774e FROM PUBLIC;

CREATE TABLE t_868c8e1c (
    c_dfa7640b uuid NOT NULL,
    c_913e8d87 uuid NOT NULL,
    c_3ba7b34f uuid NULL,
    c_9f8c86ae uuid NULL,
    c_31012b99 uuid NOT NULL,
    c_8062b379 numeric(28,10) NOT NULL,
    c_7f8d2784 numeric(28,6) NOT NULL,
    c_9735076c boolean NOT NULL DEFAULT true,
    c_041f090c bigint NOT NULL DEFAULT 1,
    c_b387838d uuid NOT NULL,
    c_2e7d5e75 timestamptz NOT NULL,
    c_651df18b timestamptz NOT NULL,
    CONSTRAINT k_1cbdd59a PRIMARY KEY (c_dfa7640b),
    CONSTRAINT k_53f8e7cc FOREIGN KEY (c_913e8d87) REFERENCES t_1657774e (c_2b80e293),
    CONSTRAINT k_18ff5667 FOREIGN KEY (c_3ba7b34f) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_71f3a198 FOREIGN KEY (c_9f8c86ae) REFERENCES t_2640a66e (c_01551296),
    CONSTRAINT k_ac2bddd1 FOREIGN KEY (c_31012b99) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_33890f76 CHECK (c_041f090c > 0 AND ((c_3ba7b34f IS NULL) <> (c_9f8c86ae IS NULL))
        AND c_8062b379 >= 0 AND c_8062b379 < 1000000000 AND c_7f8d2784 >= 0 AND c_7f8d2784 <= 1000000000000)
);
CREATE UNIQUE INDEX k_d71ba409 ON t_868c8e1c (c_913e8d87, c_3ba7b34f, c_31012b99, c_8062b379) WHERE c_3ba7b34f IS NOT NULL;
CREATE UNIQUE INDEX k_1d6354ca ON t_868c8e1c (c_913e8d87, c_9f8c86ae, c_31012b99, c_8062b379) WHERE c_9f8c86ae IS NOT NULL;

CREATE FUNCTION f_2a39ec32() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_041f090c <> 1 THEN RAISE EXCEPTION 'a price entry is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'price entries are never removed'; END IF;
    IF NEW.c_dfa7640b IS DISTINCT FROM OLD.c_dfa7640b OR NEW.c_913e8d87 IS DISTINCT FROM OLD.c_913e8d87
       OR NEW.c_3ba7b34f IS DISTINCT FROM OLD.c_3ba7b34f OR NEW.c_9f8c86ae IS DISTINCT FROM OLD.c_9f8c86ae
       OR NEW.c_31012b99 IS DISTINCT FROM OLD.c_31012b99 OR NEW.c_8062b379 IS DISTINCT FROM OLD.c_8062b379
       OR NEW.c_b387838d IS DISTINCT FROM OLD.c_b387838d OR NEW.c_2e7d5e75 IS DISTINCT FROM OLD.c_2e7d5e75 THEN
        RAISE EXCEPTION 'the identity, list, scope, unit, tier and creation data of a price entry are immutable';
    END IF;
    IF NEW.c_041f090c <> OLD.c_041f090c + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_b3574d75 BEFORE INSERT OR UPDATE OR DELETE ON t_868c8e1c FOR EACH ROW EXECUTE FUNCTION f_2a39ec32();
CREATE TRIGGER r_a60866cf BEFORE TRUNCATE ON t_868c8e1c FOR EACH STATEMENT EXECUTE FUNCTION f_2a39ec32();
REVOKE ALL ON TABLE t_868c8e1c FROM PUBLIC;

CREATE TABLE t_98209637 (
    c_f367d2eb uuid NOT NULL,
    c_1b96af38 varchar(30) NOT NULL,
    c_81a14063 varchar(200) NOT NULL,
    c_4fb2d017 varchar(200) NULL,
    c_13ac6fbc integer NOT NULL,
    c_8d69b845 date NOT NULL,
    c_0bf5e727 date NULL,
    c_8e780ef0 boolean NOT NULL DEFAULT true,
    c_c97ab5e7 numeric(9,4) NOT NULL,
    c_4aa76d4e numeric(28,10) NOT NULL DEFAULT 0,
    c_267fdd7a uuid NULL,
    c_27971215 uuid NULL,
    c_1561271a uuid NULL,
    c_fa711175 bigint NOT NULL DEFAULT 1,
    c_c8c197dc uuid NOT NULL,
    c_43b97137 timestamptz NOT NULL,
    c_e5d5986c timestamptz NOT NULL,
    CONSTRAINT k_073f88d8 PRIMARY KEY (c_f367d2eb),
    CONSTRAINT k_9419e60f FOREIGN KEY (c_267fdd7a) REFERENCES t_8edce6b0 (c_3f494703),
    CONSTRAINT k_6207d24b FOREIGN KEY (c_27971215) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_8356fffe FOREIGN KEY (c_1561271a) REFERENCES t_2640a66e (c_01551296),
    CONSTRAINT k_b3a43836 CHECK (c_fa711175 > 0 AND c_1b96af38 ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim(c_81a14063)) >= 1
        AND c_13ac6fbc BETWEEN 0 AND 1000000 AND (c_0bf5e727 IS NULL OR c_0bf5e727 >= c_8d69b845)
        AND c_c97ab5e7 >= 0 AND c_c97ab5e7 <= 100 AND c_4aa76d4e >= 0 AND c_4aa76d4e < 1000000000
        AND NOT (c_27971215 IS NOT NULL AND c_1561271a IS NOT NULL))
);
CREATE UNIQUE INDEX k_38393890 ON t_98209637 (c_1b96af38);
-- two ACTIVE rules of exactly the same scope, priority, minimum quantity and start can never both apply cleanly (the engine refuses such a tie as ambiguous), so PostgreSQL refuses to hold them: one badly
-- configured pair can never block pricing. (NULL scope columns are compared as the all-zero uuid.)
CREATE UNIQUE INDEX k_7396728f ON t_98209637 (COALESCE(c_267fdd7a, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(c_27971215, '00000000-0000-0000-0000-000000000000'::uuid),
    COALESCE(c_1561271a, '00000000-0000-0000-0000-000000000000'::uuid), c_13ac6fbc, c_4aa76d4e, c_8d69b845) WHERE c_8e780ef0;

CREATE FUNCTION f_86f87c19() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_fa711175 <> 1 THEN RAISE EXCEPTION 'a discount rule is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'discount rules are never removed'; END IF;
    IF NEW.c_f367d2eb IS DISTINCT FROM OLD.c_f367d2eb OR NEW.c_1b96af38 IS DISTINCT FROM OLD.c_1b96af38
       OR NEW.c_267fdd7a IS DISTINCT FROM OLD.c_267fdd7a OR NEW.c_27971215 IS DISTINCT FROM OLD.c_27971215
       OR NEW.c_1561271a IS DISTINCT FROM OLD.c_1561271a
       OR NEW.c_c8c197dc IS DISTINCT FROM OLD.c_c8c197dc OR NEW.c_43b97137 IS DISTINCT FROM OLD.c_43b97137 THEN
        RAISE EXCEPTION 'the identity, code, scope and creation data of a discount rule are immutable';
    END IF;
    IF NEW.c_fa711175 <> OLD.c_fa711175 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_04e2e792 BEFORE INSERT OR UPDATE OR DELETE ON t_98209637 FOR EACH ROW EXECUTE FUNCTION f_86f87c19();
CREATE TRIGGER r_6916ba5d BEFORE TRUNCATE ON t_98209637 FOR EACH STATEMENT EXECUTE FUNCTION f_86f87c19();
REVOKE ALL ON TABLE t_98209637 FROM PUBLIC;
