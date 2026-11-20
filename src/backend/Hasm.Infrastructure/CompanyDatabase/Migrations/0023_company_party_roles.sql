-- Migration 0023: customers and suppliers (Phase 3.9B). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0023-party-roles.cjs (logical dictionary: CompanySchema.PartyRoles.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 7.1, 10.1): a company-local ROLE of a local party (customer, supplier or both), its commercial terms (payment mode and days), credit-limit DATA, the control account
-- (receivables / payables) it posts to, and the company's sales representatives (OD-3 default: a company-local record with an optional user link and no commission calculation).
-- It is CONFIGURATION: no document, balance or journal exists here. The shared identity of the party stays in the system database and carries none of this.
-- Invariants enforced by PostgreSQL:
--   * roles and representatives are never removed (deactivated); identity, party, role and creation data never change; every change advances the revision by one;
--   * a party has at most one role of each kind; a role is customer (1) or supplier (2); only a customer has a representative;
--   * the control account of a customer is an active, postable ASSET account and that of a supplier an active, postable LIABILITY account (checked on every write that sets it);
--   * the credit limit is a non-negative amount in the company base currency (null = none recorded); payment terms are within range.
-- payment mode: 1 cash, 2 credit, 3 mixed.

CREATE TABLE t_14747344 (
    c_6e1e379c uuid NOT NULL,
    c_f8fc6d93 varchar(30) NOT NULL,
    c_7a70162d varchar(200) NOT NULL,
    c_f43a73e1 varchar(200) NULL,
    c_2e62362e uuid NULL,
    c_bf647cbe boolean NOT NULL DEFAULT true,
    c_5a3324d0 bigint NOT NULL DEFAULT 1,
    c_b0fb6c15 uuid NOT NULL,
    c_b2d1aba5 timestamptz NOT NULL,
    c_d5edad17 timestamptz NOT NULL,
    CONSTRAINT k_359e90bf PRIMARY KEY (c_6e1e379c),
    CONSTRAINT k_b544a5f1 CHECK (c_5a3324d0 > 0 AND c_f8fc6d93 ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim(c_7a70162d)) >= 1)
);
CREATE UNIQUE INDEX k_df54a630 ON t_14747344 (c_f8fc6d93);
CREATE UNIQUE INDEX k_93d7252a ON t_14747344 (c_2e62362e) WHERE c_2e62362e IS NOT NULL;

CREATE FUNCTION f_8056a8fc() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_5a3324d0 <> 1 THEN RAISE EXCEPTION 'a sales representative is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'sales representatives are never removed'; END IF;
    IF NEW.c_6e1e379c IS DISTINCT FROM OLD.c_6e1e379c OR NEW.c_f8fc6d93 IS DISTINCT FROM OLD.c_f8fc6d93
       OR NEW.c_b0fb6c15 IS DISTINCT FROM OLD.c_b0fb6c15 OR NEW.c_b2d1aba5 IS DISTINCT FROM OLD.c_b2d1aba5 THEN
        RAISE EXCEPTION 'the identity, code and creation data of a sales representative are immutable';
    END IF;
    IF NEW.c_5a3324d0 <> OLD.c_5a3324d0 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_fd9eec5f BEFORE INSERT OR UPDATE OR DELETE ON t_14747344 FOR EACH ROW EXECUTE FUNCTION f_8056a8fc();
CREATE TRIGGER r_7215f828 BEFORE TRUNCATE ON t_14747344 FOR EACH STATEMENT EXECUTE FUNCTION f_8056a8fc();
REVOKE ALL ON TABLE t_14747344 FROM PUBLIC;

CREATE TABLE t_167d29f0 (
    c_d8d1f8b4 uuid NOT NULL,
    c_bb325288 uuid NOT NULL,
    c_18acc766 smallint NOT NULL,
    c_0c57ccff boolean NOT NULL DEFAULT true,
    c_cee72371 smallint NOT NULL,
    c_5d83aadb integer NOT NULL DEFAULT 0,
    c_d8ac0b3d numeric(20,4) NULL,
    c_47108498 uuid NULL,
    c_41942bb5 uuid NULL,
    c_8cdd44f5 bigint NOT NULL DEFAULT 1,
    c_15b5004d uuid NOT NULL,
    c_a7d6c746 timestamptz NOT NULL,
    c_1acd90ab timestamptz NOT NULL,
    CONSTRAINT k_93c3ee5c PRIMARY KEY (c_d8d1f8b4),
    CONSTRAINT k_8753d8e2 FOREIGN KEY (c_bb325288) REFERENCES t_8edce6b0 (c_3f494703),
    CONSTRAINT k_ad438c67 FOREIGN KEY (c_47108498) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_38480ae8 FOREIGN KEY (c_41942bb5) REFERENCES t_14747344 (c_6e1e379c),
    CONSTRAINT k_df0c792a CHECK (c_8cdd44f5 > 0 AND c_18acc766 IN (1, 2) AND c_cee72371 IN (1, 2, 3) AND c_5d83aadb BETWEEN 0 AND 3650
        AND (c_d8ac0b3d IS NULL OR c_d8ac0b3d >= 0) AND (c_41942bb5 IS NULL OR c_18acc766 = 1))
);
CREATE UNIQUE INDEX k_33195c96 ON t_167d29f0 (c_bb325288, c_18acc766);
CREATE INDEX k_49cad412 ON t_167d29f0 (c_47108498) WHERE c_47108498 IS NOT NULL;

CREATE FUNCTION f_052174d7() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE acc_type smallint; acc_group boolean; acc_active boolean;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_8cdd44f5 <> 1 THEN RAISE EXCEPTION 'a party role is created at revision 1'; END IF;
    ELSIF TG_OP = 'UPDATE' THEN
        IF NEW.c_d8d1f8b4 IS DISTINCT FROM OLD.c_d8d1f8b4 OR NEW.c_bb325288 IS DISTINCT FROM OLD.c_bb325288 OR NEW.c_18acc766 IS DISTINCT FROM OLD.c_18acc766
           OR NEW.c_15b5004d IS DISTINCT FROM OLD.c_15b5004d OR NEW.c_a7d6c746 IS DISTINCT FROM OLD.c_a7d6c746 THEN
            RAISE EXCEPTION 'the identity, party, kind and creation data of a party role are immutable';
        END IF;
        IF NEW.c_8cdd44f5 <> OLD.c_8cdd44f5 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    ELSE
        RAISE EXCEPTION 'party roles are never removed';
    END IF;
    -- the control account must suit the kind of role whenever it is set or changed
    IF NEW.c_47108498 IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.c_47108498 IS DISTINCT FROM OLD.c_47108498) THEN
        SELECT a.c_0caeac6a, a.c_57d34c8a, a.c_9761052f INTO acc_type, acc_group, acc_active FROM t_11d107a9 a WHERE a.c_29a86598 = NEW.c_47108498;
        IF acc_group OR NOT acc_active THEN RAISE EXCEPTION 'the control account must be an active, postable account'; END IF;
        IF NEW.c_18acc766 = 1 AND acc_type <> 1 THEN RAISE EXCEPTION 'the control account of a customer must be an asset account'; END IF;
        IF NEW.c_18acc766 = 2 AND acc_type <> 2 THEN RAISE EXCEPTION 'the control account of a supplier must be a liability account'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_4d24d0eb BEFORE INSERT OR UPDATE OR DELETE ON t_167d29f0 FOR EACH ROW EXECUTE FUNCTION f_052174d7();
CREATE TRIGGER r_e6073ef7 BEFORE TRUNCATE ON t_167d29f0 FOR EACH STATEMENT EXECUTE FUNCTION f_052174d7();
REVOKE ALL ON TABLE t_167d29f0 FROM PUBLIC;
