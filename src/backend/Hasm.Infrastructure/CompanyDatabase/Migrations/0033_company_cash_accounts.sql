-- Migration 0033: the cash and bank account master (Phase 3.14 slice S1; decisions D-155, D-211). Forward-only and ADDITIVE: one new table with its guards; no existing table, function or trigger is altered, nothing is seeded, and nothing here touches the ledger.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0033-cash-bank.cjs (logical dictionary: CompanySchema.CashBank.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.14.md, D-211 Q1, Q2, Q8): a cashbox or a bank account is its own master record, bound 1:1 to ONE postable ACTIVE asset account of the chart, with a required branch, in the BASE currency only (foreign-currency
-- accounts wait for Phase 3.18). It is CONFIGURATION: it holds no balance (the ledger is the source of truth) and creates no journal entry. The full bank account number is sensitive: it is stored ONLY as an authenticated envelope
-- (the key-ring encryption of the attachments), with its last four characters kept apart as the masked form.
-- Invariants enforced by PostgreSQL:
--   * rows are never removed; the identity, code, kind and creation data never change; every change advances the revision by one;
--   * the code is unique; ONE general-ledger account serves at most ONE cash or bank account;
--   * the bound account must be an ACTIVE, non-group ASSET account whose currency policy admits the base currency (Base, Any, or Specific equal to the base currency); a company without a selected base currency has no cash account;
--   * a cashbox carries no bank details; a bank account's details are all or nothing for the number (cipher, key id and masked form together).

CREATE TABLE t_e5bfccb2 (
    c_5d553d43 uuid NOT NULL,
    c_398cc300 smallint NOT NULL,
    c_8b08aa89 varchar(30) NOT NULL,
    c_520fe1ee varchar(200) NULL,
    c_30661f41 varchar(200) NULL,
    c_cd14ebb9 uuid NOT NULL,
    c_cc88abe9 uuid NOT NULL,
    c_28304e9a varchar(3) NOT NULL,
    c_97462626 varchar(200) NULL,
    c_0312661d varchar(200) NULL,
    c_d768009d bytea NULL,
    c_591f62da varchar(40) NULL,
    c_279de9f0 varchar(4) NULL,
    c_8bfe9504 boolean NOT NULL DEFAULT true,
    c_a4dfb00c bigint NOT NULL DEFAULT 1,
    c_9b75fbe2 uuid NOT NULL,
    c_a05c4bef timestamptz NOT NULL,
    c_35724976 timestamptz NOT NULL,
    CONSTRAINT k_03d67366 PRIMARY KEY (c_5d553d43),
    CONSTRAINT k_1ec8e737 FOREIGN KEY (c_cd14ebb9) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_e4fee1da FOREIGN KEY (c_cc88abe9) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_7894c194 CHECK (c_a4dfb00c > 0 AND c_398cc300 IN (1, 2) AND c_8b08aa89 ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$'
        AND (c_520fe1ee IS NOT NULL OR c_30661f41 IS NOT NULL)
        AND (c_520fe1ee IS NULL OR char_length(btrim(c_520fe1ee)) >= 1) AND (c_30661f41 IS NULL OR char_length(btrim(c_30661f41)) >= 1)
        AND c_28304e9a ~ '^[A-Z]{3}$'),
    CONSTRAINT k_ba5d4990 CHECK (
        (c_398cc300 = 1 AND c_97462626 IS NULL AND c_0312661d IS NULL AND c_d768009d IS NULL AND c_591f62da IS NULL AND c_279de9f0 IS NULL)
        OR (c_398cc300 = 2 AND ((c_d768009d IS NULL AND c_591f62da IS NULL AND c_279de9f0 IS NULL)
            OR (c_d768009d IS NOT NULL AND c_591f62da IS NOT NULL AND c_279de9f0 IS NOT NULL AND char_length(c_279de9f0) BETWEEN 1 AND 4)))
    )
);
CREATE UNIQUE INDEX k_29bb3551 ON t_e5bfccb2 (c_8b08aa89);
CREATE UNIQUE INDEX k_35c7e635 ON t_e5bfccb2 (c_cc88abe9);
CREATE INDEX k_20500d1a ON t_e5bfccb2 (c_cd14ebb9);

CREATE FUNCTION f_c546df4c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_a4dfb00c <> 1 THEN RAISE EXCEPTION 'a cash account is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cash and bank accounts are never removed'; END IF;
    IF NEW.c_5d553d43 IS DISTINCT FROM OLD.c_5d553d43 OR NEW.c_8b08aa89 IS DISTINCT FROM OLD.c_8b08aa89 OR NEW.c_398cc300 IS DISTINCT FROM OLD.c_398cc300
       OR NEW.c_9b75fbe2 IS DISTINCT FROM OLD.c_9b75fbe2 OR NEW.c_a05c4bef IS DISTINCT FROM OLD.c_a05c4bef THEN
        RAISE EXCEPTION 'the identity, code, kind and creation data of a cash or bank account are immutable';
    END IF;
    IF NEW.c_a4dfb00c <> OLD.c_a4dfb00c + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_09de7cc1 BEFORE INSERT OR UPDATE OR DELETE ON t_e5bfccb2 FOR EACH ROW EXECUTE FUNCTION f_c546df4c();
CREATE TRIGGER r_ba76ef66 BEFORE TRUNCATE ON t_e5bfccb2 FOR EACH STATEMENT EXECUTE FUNCTION f_c546df4c();

-- the binding rule, checked whenever a row is written or its account or currency changes: the account is an active, non-group asset account that admits the base currency, and the row's currency IS the base currency
CREATE FUNCTION f_3a0dbb72() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    a record;
    v_base varchar(3);
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.c_cc88abe9 IS NOT DISTINCT FROM OLD.c_cc88abe9 AND NEW.c_28304e9a IS NOT DISTINCT FROM OLD.c_28304e9a AND NEW.c_8bfe9504 IS NOT DISTINCT FROM OLD.c_8bfe9504 THEN
        RETURN NEW;   -- names, bank details and branch do not touch the binding rule
    END IF;
    SELECT cc.c_6da45b20 INTO v_base FROM t_f5bc326a cc WHERE cc.c_5cdc1bc7 AND cc.c_702730ab LIMIT 1;
    IF v_base IS NULL THEN RAISE EXCEPTION 'cash_base_currency_not_selected'; END IF;
    IF NEW.c_28304e9a <> v_base THEN RAISE EXCEPTION 'cash_account_currency_not_base'; END IF;
    SELECT x.c_0caeac6a AS type, x.c_57d34c8a AS is_group, x.c_9761052f AS is_active, x.c_dfab26fd AS policy, x.c_76c4ece0 AS ccy
      INTO a FROM t_11d107a9 x WHERE x.c_29a86598 = NEW.c_cc88abe9;
    IF NOT FOUND THEN RAISE EXCEPTION 'cash_account_gl_not_found'; END IF;
    IF a.type <> 1 THEN RAISE EXCEPTION 'cash_account_gl_not_asset'; END IF;
    IF a.is_group OR NOT a.is_active THEN RAISE EXCEPTION 'cash_account_gl_not_postable'; END IF;
    IF NOT (a.policy IN (1, 2) OR (a.policy = 3 AND a.ccy = v_base)) THEN RAISE EXCEPTION 'cash_account_gl_currency'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d58c586f BEFORE INSERT OR UPDATE ON t_e5bfccb2 FOR EACH ROW EXECUTE FUNCTION f_3a0dbb72();

REVOKE ALL ON TABLE t_e5bfccb2 FROM PUBLIC;
