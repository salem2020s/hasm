-- Migration 0015: inventory accounting configuration (Phase 3.7A). Forward-only, no DROP, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.InventoryAccounting.cs.
--
-- WHAT THIS IS: the company-level configuration that a later posting slice needs to build the accounting side of a stock movement: which ledger account holds the inventory of each item group (with a company default)
-- and which counter accounts take the other side of a receipt and of an issue. NOTHING IS HARD-CODED and nothing is seeded: until a company configures it, nothing can be posted. A configuration is a VERSION: every
-- change creates a new immutable version holding the complete mapping, so a posting can always be traced to the exact configuration it used, and an old posting is never re-interpreted. Versions are append-only.
-- Whether an account is suitable (active, postable, an asset account for inventory, not a foreign-currency-only account) is validated by the service against the live chart at save time (the chart can change later;
-- a posting re-validates at posting time); the foreign keys keep every referenced account and group real.

CREATE TABLE t_a78f5224 (
    c_3bffe448 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_004ae1f7 integer NOT NULL,
    c_cc3839ff timestamptz NOT NULL,
    c_daea169c uuid NOT NULL,
    c_f7b0ed06 varchar(500) NULL,
    c_924e88aa uuid NULL,
    c_0edb6645 uuid NULL,
    c_1661ba21 uuid NULL,
    c_3ab635fe integer NOT NULL,
    c_767c84c9 char(64) NOT NULL,
    CONSTRAINT k_7bd31da5 PRIMARY KEY (c_3bffe448),
    CONSTRAINT k_6843e5c1 UNIQUE (c_004ae1f7),
    CONSTRAINT k_753f71eb FOREIGN KEY (c_924e88aa) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_8bf4d88f FOREIGN KEY (c_0edb6645) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_11728294 FOREIGN KEY (c_1661ba21) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_883e3b4b CHECK (c_004ae1f7 > 0 AND c_3ab635fe >= 0)
);
CREATE FUNCTION f_969e1275() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'inventory accounting versions are immutable';
END;
$$;
CREATE TRIGGER r_e0da33d6 BEFORE UPDATE OR DELETE ON t_a78f5224 FOR EACH ROW EXECUTE FUNCTION f_969e1275();
CREATE TRIGGER r_c90a9345 BEFORE TRUNCATE ON t_a78f5224 FOR EACH STATEMENT EXECUTE FUNCTION f_969e1275();
REVOKE ALL ON TABLE t_a78f5224 FROM PUBLIC;

CREATE TABLE t_af40caae (
    c_8b2f8137 uuid NOT NULL,
    c_57750caa uuid NOT NULL,
    c_f406a771 uuid NOT NULL,
    CONSTRAINT k_58cc7193 PRIMARY KEY (c_8b2f8137, c_57750caa),
    CONSTRAINT k_6ea0b3d4 FOREIGN KEY (c_8b2f8137) REFERENCES t_a78f5224 (c_3bffe448),
    CONSTRAINT k_4fc40bed FOREIGN KEY (c_57750caa) REFERENCES t_2640a66e (c_01551296),
    CONSTRAINT k_a437cec0 FOREIGN KEY (c_f406a771) REFERENCES t_11d107a9 (c_29a86598)
);
CREATE INDEX k_09ac4cbd ON t_af40caae (c_f406a771);
CREATE FUNCTION f_4853d4c1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'inventory accounting versions are immutable';
END;
$$;
CREATE TRIGGER r_51c58d49 BEFORE UPDATE OR DELETE ON t_af40caae FOR EACH ROW EXECUTE FUNCTION f_4853d4c1();
CREATE TRIGGER r_3fcf0a46 BEFORE TRUNCATE ON t_af40caae FOR EACH STATEMENT EXECUTE FUNCTION f_4853d4c1();
REVOKE ALL ON TABLE t_af40caae FROM PUBLIC;
