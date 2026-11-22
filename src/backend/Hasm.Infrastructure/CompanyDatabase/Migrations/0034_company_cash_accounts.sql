-- Migration 0034: the cash and bank account master (Phase 3.14 slice S1; decisions D-155, D-211). Forward-only and ADDITIVE: one new table with its guards; no existing table, function or trigger is altered, nothing is seeded, and nothing here touches the ledger.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0034-cash-bank.cjs (logical dictionary: CompanySchema.CashBank.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.14.md, D-211 Q1, Q2, Q8): a cashbox or a bank account is its own master record, bound 1:1 to ONE postable ACTIVE asset account of the chart, with a required branch, in the BASE currency only (foreign-currency
-- accounts wait for Phase 3.18). It is CONFIGURATION: it holds no balance (the ledger is the source of truth) and creates no journal entry. The full bank account number is sensitive: it is stored ONLY as an authenticated envelope
-- (the key-ring encryption of the attachments), with only the tail its masked form may show (the last four characters of a long number, the last two of a medium one, nothing of a short one) kept apart.
-- Invariants enforced by PostgreSQL:
--   * rows are never removed; the identity, code, kind and creation data never change; every change advances the revision by one;
--   * the code is unique; ONE general-ledger account serves at most ONE cash or bank account;
--   * the bound account must be an ACTIVE, non-group ASSET account whose currency policy admits the base currency (Base, Any, or Specific equal to the base currency); a company without a selected base currency has no cash account;
--   * a cashbox carries no bank details; a bank account's details are all or nothing for the number (cipher, key id and masked form together).

CREATE TABLE t_052e7431 (
    c_4eb7af72 uuid NOT NULL,
    c_dc1fa9b9 smallint NOT NULL,
    c_0b8b16a1 varchar(30) NOT NULL,
    c_2179cb36 varchar(200) NULL,
    c_dd663f2b varchar(200) NULL,
    c_ddeb43d5 uuid NOT NULL,
    c_6878bd12 uuid NOT NULL,
    c_16bd34bd varchar(3) NOT NULL,
    c_f349e330 varchar(200) NULL,
    c_e8940fc8 varchar(200) NULL,
    c_a0b34a66 bytea NULL,
    c_5e1216e6 varchar(40) NULL,
    c_723b3bfd varchar(4) NULL,   -- the part of the number the mask may show (0 to 4 characters, see the masking rule of the application); never more
    c_864b287e boolean NOT NULL DEFAULT true,
    c_452d1535 bigint NOT NULL DEFAULT 1,
    c_567496cd uuid NOT NULL,
    c_3e79105a timestamptz NOT NULL,
    c_2d3a07ea timestamptz NOT NULL,
    CONSTRAINT k_661f8ee0 PRIMARY KEY (c_4eb7af72),
    CONSTRAINT k_06b20e19 FOREIGN KEY (c_ddeb43d5) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_3c0208af FOREIGN KEY (c_6878bd12) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_d52aa117 CHECK (c_452d1535 > 0 AND c_dc1fa9b9 IN (1, 2) AND c_0b8b16a1 ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$'
        AND (c_2179cb36 IS NOT NULL OR c_dd663f2b IS NOT NULL)
        AND (c_2179cb36 IS NULL OR char_length(btrim(c_2179cb36)) >= 1) AND (c_dd663f2b IS NULL OR char_length(btrim(c_dd663f2b)) >= 1)
        AND c_16bd34bd ~ '^[A-Z]{3}$'),
    CONSTRAINT k_1de3834b CHECK (
        (c_dc1fa9b9 = 1 AND c_f349e330 IS NULL AND c_e8940fc8 IS NULL AND c_a0b34a66 IS NULL AND c_5e1216e6 IS NULL AND c_723b3bfd IS NULL)
        OR (c_dc1fa9b9 = 2 AND ((c_a0b34a66 IS NULL AND c_5e1216e6 IS NULL AND c_723b3bfd IS NULL)
            OR (c_a0b34a66 IS NOT NULL AND c_5e1216e6 IS NOT NULL AND c_723b3bfd IS NOT NULL AND char_length(c_723b3bfd) BETWEEN 0 AND 4)))
    )
);
CREATE UNIQUE INDEX k_6e18098e ON t_052e7431 (c_0b8b16a1);
CREATE UNIQUE INDEX k_13c75d2f ON t_052e7431 (c_6878bd12);
CREATE INDEX k_858508b6 ON t_052e7431 (c_ddeb43d5);

CREATE FUNCTION f_39deefba() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_452d1535 <> 1 THEN RAISE EXCEPTION 'a cash account is created at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cash and bank accounts are never removed'; END IF;
    IF NEW.c_4eb7af72 IS DISTINCT FROM OLD.c_4eb7af72 OR NEW.c_0b8b16a1 IS DISTINCT FROM OLD.c_0b8b16a1 OR NEW.c_dc1fa9b9 IS DISTINCT FROM OLD.c_dc1fa9b9
       OR NEW.c_567496cd IS DISTINCT FROM OLD.c_567496cd OR NEW.c_3e79105a IS DISTINCT FROM OLD.c_3e79105a THEN
        RAISE EXCEPTION 'the identity, code, kind and creation data of a cash or bank account are immutable';
    END IF;
    IF NEW.c_452d1535 <> OLD.c_452d1535 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_f2589b94 BEFORE INSERT OR UPDATE OR DELETE ON t_052e7431 FOR EACH ROW EXECUTE FUNCTION f_39deefba();
CREATE TRIGGER r_7186e842 BEFORE TRUNCATE ON t_052e7431 FOR EACH STATEMENT EXECUTE FUNCTION f_39deefba();

-- the binding rule, checked whenever a row is written or its account or currency changes: the account is an active, non-group asset account that admits the base currency, and the row's currency IS the base currency
CREATE FUNCTION f_774d9c73() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    a record;
    v_base varchar(3);
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.c_6878bd12 IS NOT DISTINCT FROM OLD.c_6878bd12 AND NEW.c_16bd34bd IS NOT DISTINCT FROM OLD.c_16bd34bd AND NEW.c_864b287e IS NOT DISTINCT FROM OLD.c_864b287e THEN
        RETURN NEW;   -- names, bank details and branch do not touch the binding rule
    END IF;
    SELECT cc.c_6da45b20 INTO v_base FROM t_f5bc326a cc WHERE cc.c_5cdc1bc7 AND cc.c_702730ab LIMIT 1;
    IF v_base IS NULL THEN RAISE EXCEPTION 'cash_base_currency_not_selected'; END IF;
    IF NEW.c_16bd34bd <> v_base THEN RAISE EXCEPTION 'cash_account_currency_not_base'; END IF;
    SELECT x.c_0caeac6a AS type, x.c_57d34c8a AS is_group, x.c_9761052f AS is_active, x.c_dfab26fd AS policy, x.c_76c4ece0 AS ccy
      INTO a FROM t_11d107a9 x WHERE x.c_29a86598 = NEW.c_6878bd12;
    IF NOT FOUND THEN RAISE EXCEPTION 'cash_account_gl_not_found'; END IF;
    IF a.type <> 1 THEN RAISE EXCEPTION 'cash_account_gl_not_asset'; END IF;
    IF a.is_group OR NOT a.is_active THEN RAISE EXCEPTION 'cash_account_gl_not_postable'; END IF;
    IF NOT (a.policy IN (1, 2) OR (a.policy = 3 AND a.ccy = v_base)) THEN RAISE EXCEPTION 'cash_account_gl_currency'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_24cc2fef BEFORE INSERT OR UPDATE ON t_052e7431 FOR EACH ROW EXECUTE FUNCTION f_774d9c73();

REVOKE ALL ON TABLE t_052e7431 FROM PUBLIC;
