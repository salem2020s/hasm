-- Migration 0035: the cash and bank account master (Phase 3.14 slice S1; decisions D-155, D-211). Forward-only and ADDITIVE: one new table with its guards; no existing table, function or trigger is altered, nothing is seeded, and nothing here touches the ledger.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0035-cash-bank.cjs (logical dictionary: CompanySchema.CashBank.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.14.md, D-211 Q1, Q2, Q8): a cashbox or a bank account is its own master record, bound 1:1 to ONE postable ACTIVE asset account of the chart, with a required branch, in the BASE currency only (foreign-currency
-- accounts wait for Phase 3.18). It is CONFIGURATION: it holds no balance (the ledger is the source of truth) and creates no journal entry. The full bank account number is sensitive: it is stored ONLY as an authenticated envelope
-- (the key-ring encryption of the attachments), with only the tail its masked form may show (the last four characters of a long number, the last two of a medium one, nothing of a short one) kept apart.
-- Invariants enforced by PostgreSQL:
--   * rows are never removed; the identity, code, kind and creation data never change; every change advances the revision by one;
--   * the code is unique; ONE general-ledger account serves at most ONE cash or bank account;
--   * the bound account must be an ACTIVE, non-group ASSET account whose currency policy admits the base currency (Base, Any, or Specific equal to the base currency); a company without a selected base currency has no cash account;
--   * a cashbox carries no bank details; a bank account's details are all or nothing for the number (cipher, key id and masked form together).

CREATE TABLE t_1f78861e (
    c_a448c834 uuid NOT NULL,
    c_5fe39862 smallint NOT NULL,
    c_527867fc varchar(30) NOT NULL,
    c_0b414e8b varchar(200) NULL,
    c_c4caeaed varchar(200) NULL,
    c_5b26d90b uuid NOT NULL,
    c_320d83b3 uuid NOT NULL,
    c_844ff4fc varchar(3) NOT NULL,
    c_83e141dd varchar(200) NULL,
    c_f5e36075 varchar(200) NULL,
    c_ccdb30c2 bytea NULL,
    c_f251b148 varchar(40) NULL,
    c_b2304c2e varchar(4) NULL,   -- the part of the number the mask may show (0 to 4 characters, see the masking rule of the application); never more
    c_714dc13f boolean NOT NULL DEFAULT true,
    c_dc6d6d38 bigint NOT NULL DEFAULT 1,
    c_8e168623 uuid NOT NULL,
    c_bd37be34 timestamptz NOT NULL,
    c_c3aded7e timestamptz NOT NULL,
    CONSTRAINT k_326664e1 PRIMARY KEY (c_a448c834),
    CONSTRAINT k_5a08688c FOREIGN KEY (c_5b26d90b) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_6a22c2a4 FOREIGN KEY (c_320d83b3) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_2bae471b CHECK (c_dc6d6d38 > 0 AND c_5fe39862 IN (1, 2) AND c_527867fc ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$'
        AND (c_0b414e8b IS NOT NULL OR c_c4caeaed IS NOT NULL)
        AND (c_0b414e8b IS NULL OR char_length(btrim(c_0b414e8b)) >= 1) AND (c_c4caeaed IS NULL OR char_length(btrim(c_c4caeaed)) >= 1)
        AND c_844ff4fc ~ '^[A-Z]{3}$'),
    CONSTRAINT k_e76abd82 CHECK (
        (c_5fe39862 = 1 AND c_83e141dd IS NULL AND c_f5e36075 IS NULL AND c_ccdb30c2 IS NULL AND c_f251b148 IS NULL AND c_b2304c2e IS NULL)
        OR (c_5fe39862 = 2 AND ((c_ccdb30c2 IS NULL AND c_f251b148 IS NULL AND c_b2304c2e IS NULL)
            OR (c_ccdb30c2 IS NOT NULL AND c_f251b148 IS NOT NULL AND c_b2304c2e IS NOT NULL AND char_length(c_b2304c2e) BETWEEN 0 AND 4)))
    )
);
CREATE UNIQUE INDEX k_90977c7b ON t_1f78861e (c_527867fc);
CREATE UNIQUE INDEX k_d821b66d ON t_1f78861e (c_320d83b3);
CREATE INDEX k_2d21029c ON t_1f78861e (c_5b26d90b);

CREATE FUNCTION f_7bfbdf2c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_dc6d6d38 <> 1 THEN RAISE EXCEPTION 'a cash account is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cash and bank accounts are never removed'; END IF;
    IF NEW.c_a448c834 IS DISTINCT FROM OLD.c_a448c834 OR NEW.c_527867fc IS DISTINCT FROM OLD.c_527867fc OR NEW.c_5fe39862 IS DISTINCT FROM OLD.c_5fe39862
       OR NEW.c_8e168623 IS DISTINCT FROM OLD.c_8e168623 OR NEW.c_bd37be34 IS DISTINCT FROM OLD.c_bd37be34 THEN
        RAISE EXCEPTION 'the identity, code, kind and creation data of a cash or bank account are immutable';
    END IF;
    IF NEW.c_dc6d6d38 <> OLD.c_dc6d6d38 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_dbb14039 BEFORE INSERT OR UPDATE OR DELETE ON t_1f78861e FOR EACH ROW EXECUTE FUNCTION f_7bfbdf2c();
CREATE TRIGGER r_f0662166 BEFORE TRUNCATE ON t_1f78861e FOR EACH STATEMENT EXECUTE FUNCTION f_7bfbdf2c();

-- the binding rule, checked whenever a row is written or its account or currency changes: the account is an active, non-group asset account that admits the base currency, and the row's currency IS the base currency
CREATE FUNCTION f_934ca6da() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    a record;
    v_base varchar(3);
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.c_320d83b3 IS NOT DISTINCT FROM OLD.c_320d83b3 AND NEW.c_844ff4fc IS NOT DISTINCT FROM OLD.c_844ff4fc AND NEW.c_714dc13f IS NOT DISTINCT FROM OLD.c_714dc13f THEN
        RETURN NEW;   -- names, bank details and branch do not touch the binding rule
    END IF;
    SELECT cc.c_6da45b20 INTO v_base FROM t_f5bc326a cc WHERE cc.c_5cdc1bc7 AND cc.c_702730ab LIMIT 1;
    IF v_base IS NULL THEN RAISE EXCEPTION 'cash_base_currency_not_selected'; END IF;
    IF NEW.c_844ff4fc <> v_base THEN RAISE EXCEPTION 'cash_account_currency_not_base'; END IF;
    SELECT x.c_0caeac6a AS type, x.c_57d34c8a AS is_group, x.c_9761052f AS is_active, x.c_dfab26fd AS policy, x.c_76c4ece0 AS ccy
      INTO a FROM t_11d107a9 x WHERE x.c_29a86598 = NEW.c_320d83b3;
    IF NOT FOUND THEN RAISE EXCEPTION 'cash_account_gl_not_found'; END IF;
    IF a.type <> 1 THEN RAISE EXCEPTION 'cash_account_gl_not_asset'; END IF;
    IF a.is_group OR NOT a.is_active THEN RAISE EXCEPTION 'cash_account_gl_not_postable'; END IF;
    IF NOT (a.policy IN (1, 2) OR (a.policy = 3 AND a.ccy = v_base)) THEN RAISE EXCEPTION 'cash_account_gl_currency'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_477b5218 BEFORE INSERT OR UPDATE ON t_1f78861e FOR EACH ROW EXECUTE FUNCTION f_934ca6da();

REVOKE ALL ON TABLE t_1f78861e FROM PUBLIC;
