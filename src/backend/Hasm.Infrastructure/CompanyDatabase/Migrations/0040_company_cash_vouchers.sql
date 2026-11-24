-- Migration 0040: cash vouchers WITHOUT any financial posting (Phase 3.14 slice S2a; decisions D-156, D-211, D-214). Forward-only and ADDITIVE: three new tables with their guards and one new guard trigger on the cash account
-- master; no existing table, column, constraint or function is altered, nothing is seeded, and NOTHING here touches the ledger, the numbering or any balance.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0040-cash-vouchers.cjs (logical dictionary: CompanySchema.CashVouchers.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.14.md sections 4 and 6; D-211 Q2, Q3, Q11): a receipt (kind 1) or payment (kind 2) voucher on ONE cash or bank account, in the branch and the (base) currency of that account, with an optional party
-- reference and counter lines (an explicitly chosen counter account and a positive amount). D-214 keeps financial posting for cash and bank CLOSED: this slice has no approval, no official number, no journal entry, no link to
-- one and no reversal. A voucher here is a draft, a submitted draft or a cancelled one; the columns and the status values of the later slice (S2b) are added by its own migration.
-- Invariants enforced by PostgreSQL:
--   * vouchers, lines and events are never removed; the identity of a voucher (kind, cash account, branch, currency, creator, idempotency key, payload hash) never changes; every change advances the revision by one;
--   * the status guard allows ONLY draft -> draft (an edit), draft -> submitted, submitted -> draft (withdraw) and draft/submitted -> cancelled. Approval, rejection and every later state are NOT in this guard: a direct statement
--     attempting them is refused. A cancelled voucher is final; a status change edits nothing; a submitted or cancelled voucher is not edited;
--   * a voucher is created, and submitted, only on an ACTIVE cash or bank account whose branch and currency are the voucher's, the currency being the base currency (Q2);
--   * lines are APPEND-ONLY line sets: every edit of a draft writes a whole new set under the new revision (the voucher names its current set); a set is closed once its revision has its history event; a line is written only
--     while the voucher is a draft; no line is ever updated or deleted, so the history of a draft's lines is kept;
--   * a counter account is an active, non-group account that admits the base currency, is NOT bound to any cash or bank account (that would be a transfer in disguise, Q7) and is NOT the control account of any party role (Q3);
--     checked when a line is written and again for every current line when the voucher is submitted; a submitted voucher has at least one line;
--   * the events are append-only, one per revision, each carrying the CURRENT revision and status of its voucher, and every revision of a voucher has its event when its transaction commits;
--   * (cash account master, D-211 Q1 and the plan's section 4) the branch and the bound general-ledger account of a cash or bank account are FROZEN once any voucher refers to it, and it is not deactivated while one of its
--     vouchers is not cancelled (in this slice every voucher that is not cancelled is open: nothing can be approved yet).

CREATE TABLE t_05fb4037 (
    c_9d4a288c uuid NOT NULL,
    c_9654ba02 smallint NOT NULL,
    c_02b9651f uuid NOT NULL,
    c_59c696ca uuid NOT NULL,
    c_341dcfaf varchar(3) NOT NULL,
    c_c8ad43f1 date NOT NULL,
    c_29bf6274 uuid NULL,
    c_9f6e60fd varchar(100) NULL,
    c_5cbb6e5e varchar(500) NULL,
    c_8ced6547 smallint NOT NULL,
    c_9ff27adb bigint NOT NULL,
    c_cfabc5a3 varchar(64) NOT NULL,
    c_3b9edb3e char(64) NOT NULL,
    c_5bf7c985 uuid NULL,
    c_11264714 uuid NOT NULL,   -- the last EDITOR of the content (a status change does not change it); the independent-approver rule of the later slice compares against it
    c_95ebf428 bigint NOT NULL DEFAULT 1,
    c_c5ed1cb8 uuid NOT NULL,
    c_4e924c83 timestamptz NOT NULL,
    c_edce1ab4 timestamptz NOT NULL,
    CONSTRAINT k_6fac42ac PRIMARY KEY (c_9d4a288c),
    CONSTRAINT k_08324567 FOREIGN KEY (c_02b9651f) REFERENCES t_1f78861e (c_a448c834),
    CONSTRAINT k_06c48e06 FOREIGN KEY (c_59c696ca) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_6cfa0fd3 FOREIGN KEY (c_29bf6274) REFERENCES t_8edce6b0 (c_3f494703),
    -- the value ranges are deliberately wider than this slice uses (a constraint is never dropped): the guard function below, which a later migration replaces, admits only what the slice implements
    CONSTRAINT k_06bd89fa CHECK (c_95ebf428 > 0 AND c_9ff27adb > 0 AND c_9ff27adb <= c_95ebf428 AND c_9654ba02 IN (1, 2)
        AND c_8ced6547 BETWEEN 1 AND 9 AND c_341dcfaf ~ '^[A-Z]{3}$' AND c_3b9edb3e ~ '^[0-9a-f]{64}$' AND char_length(c_cfabc5a3) BETWEEN 8 AND 64
        AND (c_9f6e60fd IS NULL OR char_length(btrim(c_9f6e60fd)) >= 1) AND (c_5cbb6e5e IS NULL OR char_length(btrim(c_5cbb6e5e)) >= 1))
);
CREATE UNIQUE INDEX k_17b8f0fe ON t_05fb4037 (c_c5ed1cb8, c_cfabc5a3);
CREATE INDEX k_d921d403 ON t_05fb4037 (c_59c696ca, c_4e924c83);
CREATE INDEX k_bd2030d0 ON t_05fb4037 (c_02b9651f, c_8ced6547);

CREATE TABLE t_731339c2 (
    c_864167bb uuid NOT NULL,
    c_7cea0fae uuid NOT NULL,
    c_cc728a10 bigint NOT NULL,   -- the revision of the voucher that wrote this line set
    c_98340e99 integer NOT NULL,
    c_31170b6e uuid NOT NULL,
    c_f17f8942 numeric(20,4) NOT NULL,
    c_ada5bc87 varchar(500) NULL,
    CONSTRAINT k_5c5caa02 PRIMARY KEY (c_864167bb),
    CONSTRAINT k_11071fc0 FOREIGN KEY (c_7cea0fae) REFERENCES t_05fb4037 (c_9d4a288c),
    CONSTRAINT k_9eb4a987 FOREIGN KEY (c_31170b6e) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_0fa888a2 CHECK (c_cc728a10 > 0 AND c_98340e99 BETWEEN 1 AND 499 AND c_f17f8942 > 0 AND c_f17f8942 <= 999999999999999
        AND (c_ada5bc87 IS NULL OR char_length(btrim(c_ada5bc87)) >= 1))
);
CREATE UNIQUE INDEX k_a15d50ce ON t_731339c2 (c_7cea0fae, c_cc728a10, c_98340e99);
CREATE INDEX k_d0e51ea7 ON t_731339c2 (c_31170b6e);

CREATE TABLE t_ba09cd09 (
    c_7822f105 uuid NOT NULL,
    c_abddc114 uuid NOT NULL,
    c_351c5cdd smallint NOT NULL,
    c_c9bb8a25 smallint NULL,
    c_bbe96a4d smallint NOT NULL,
    c_e2361087 bigint NOT NULL,
    c_b6f36b6c uuid NOT NULL,
    c_d7fdbf7e varchar(500) NULL,
    c_3b2a326e timestamptz NOT NULL,
    CONSTRAINT k_eaac3421 PRIMARY KEY (c_7822f105),
    CONSTRAINT k_95f3d008 FOREIGN KEY (c_abddc114) REFERENCES t_05fb4037 (c_9d4a288c),
    CONSTRAINT k_5f734d93 CHECK (c_351c5cdd BETWEEN 1 AND 30 AND c_bbe96a4d BETWEEN 1 AND 9 AND (c_c9bb8a25 IS NULL OR c_c9bb8a25 BETWEEN 1 AND 9) AND c_e2361087 > 0)
);
CREATE UNIQUE INDEX k_4e5dee0c ON t_ba09cd09 (c_abddc114, c_e2361087);

-- the account rule of a voucher: an ACTIVE cash or bank account of the voucher's branch, in the base currency. The share lock conflicts with every change of the account row, so a voucher is never written beside a
-- deactivation or a re-binding of its account (the account's own guard below then sees the committed voucher).
CREATE FUNCTION f_fc5224e6(p_account uuid, p_branch uuid, p_currency varchar) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    a record;
    v_base varchar(3);
BEGIN
    SELECT cc.c_6da45b20 INTO v_base FROM t_f5bc326a cc WHERE cc.c_5cdc1bc7 AND cc.c_702730ab LIMIT 1;
    IF v_base IS NULL THEN RAISE EXCEPTION 'cash_base_currency_not_selected'; END IF;
    SELECT x.c_714dc13f AS is_active, x.c_5b26d90b AS branch, x.c_844ff4fc AS ccy INTO a FROM t_1f78861e x WHERE x.c_a448c834 = p_account FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'cash_voucher_account_not_found'; END IF;
    IF NOT a.is_active THEN RAISE EXCEPTION 'cash_voucher_account_inactive'; END IF;
    IF a.branch IS DISTINCT FROM p_branch THEN RAISE EXCEPTION 'cash_voucher_branch_mismatch'; END IF;
    IF a.ccy IS DISTINCT FROM v_base OR p_currency IS DISTINCT FROM v_base THEN RAISE EXCEPTION 'cash_voucher_currency_not_base'; END IF;
END;
$$;

-- the counter-account rule (docs/PHASE-3.14.md section 6, fail closed): postable and active, admits the base currency, not bound to a cash or bank account, not the control account of any party role
CREATE FUNCTION f_cfd48c73(p_account uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    a record;
    v_base varchar(3);
BEGIN
    SELECT cc.c_6da45b20 INTO v_base FROM t_f5bc326a cc WHERE cc.c_5cdc1bc7 AND cc.c_702730ab LIMIT 1;
    IF v_base IS NULL THEN RAISE EXCEPTION 'cash_base_currency_not_selected'; END IF;
    SELECT x.c_57d34c8a AS is_group, x.c_9761052f AS is_active, x.c_dfab26fd AS policy, x.c_76c4ece0 AS ccy INTO a FROM t_11d107a9 x WHERE x.c_29a86598 = p_account;
    IF NOT FOUND THEN RAISE EXCEPTION 'cash_voucher_counter_not_found'; END IF;
    IF a.is_group OR NOT a.is_active THEN RAISE EXCEPTION 'cash_voucher_counter_not_postable'; END IF;
    IF NOT (a.policy IN (1, 2) OR (a.policy = 3 AND a.ccy = v_base)) THEN RAISE EXCEPTION 'cash_voucher_counter_currency'; END IF;
    IF EXISTS (SELECT 1 FROM t_1f78861e c WHERE c.c_320d83b3 = p_account) THEN RAISE EXCEPTION 'cash_voucher_counter_is_cash_account'; END IF;
    IF EXISTS (SELECT 1 FROM t_167d29f0 r WHERE r.c_47108498 = p_account) THEN RAISE EXCEPTION 'cash_voucher_counter_is_control_account'; END IF;
END;
$$;

CREATE FUNCTION f_a5d773e0() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    l record;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_8ced6547 <> 1 THEN RAISE EXCEPTION 'a cash voucher is created as a draft'; END IF;
        IF NEW.c_95ebf428 <> 1 OR NEW.c_9ff27adb <> 1 THEN RAISE EXCEPTION 'a cash voucher is created at revision 1'; END IF;
        IF NEW.c_5bf7c985 IS NOT NULL THEN RAISE EXCEPTION 'a draft has no submitter'; END IF;
        IF NEW.c_11264714 IS DISTINCT FROM NEW.c_c5ed1cb8 THEN RAISE EXCEPTION 'the creator is the first editor of a cash voucher'; END IF;
        PERFORM f_fc5224e6(NEW.c_02b9651f, NEW.c_59c696ca, NEW.c_341dcfaf);
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cash vouchers are never removed'; END IF;
    IF NEW.c_9d4a288c IS DISTINCT FROM OLD.c_9d4a288c OR NEW.c_9654ba02 IS DISTINCT FROM OLD.c_9654ba02 OR NEW.c_02b9651f IS DISTINCT FROM OLD.c_02b9651f
       OR NEW.c_59c696ca IS DISTINCT FROM OLD.c_59c696ca OR NEW.c_341dcfaf IS DISTINCT FROM OLD.c_341dcfaf OR NEW.c_c5ed1cb8 IS DISTINCT FROM OLD.c_c5ed1cb8
       OR NEW.c_4e924c83 IS DISTINCT FROM OLD.c_4e924c83 OR NEW.c_cfabc5a3 IS DISTINCT FROM OLD.c_cfabc5a3
       OR NEW.c_3b9edb3e IS DISTINCT FROM OLD.c_3b9edb3e THEN
        RAISE EXCEPTION 'the identity of a cash voucher is immutable';
    END IF;
    IF NEW.c_95ebf428 <> OLD.c_95ebf428 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NOT ((OLD.c_8ced6547 = 1 AND NEW.c_8ced6547 IN (1, 2, 5)) OR (OLD.c_8ced6547 = 2 AND NEW.c_8ced6547 IN (1, 5))) THEN
        RAISE EXCEPTION 'illegal cash voucher status transition';
    END IF;
    IF NEW.c_8ced6547 <> OLD.c_8ced6547 THEN
        IF NEW.c_c8ad43f1 IS DISTINCT FROM OLD.c_c8ad43f1 OR NEW.c_29bf6274 IS DISTINCT FROM OLD.c_29bf6274 OR NEW.c_9f6e60fd IS DISTINCT FROM OLD.c_9f6e60fd
           OR NEW.c_5cbb6e5e IS DISTINCT FROM OLD.c_5cbb6e5e OR NEW.c_9ff27adb IS DISTINCT FROM OLD.c_9ff27adb OR NEW.c_11264714 IS DISTINCT FROM OLD.c_11264714 THEN
            RAISE EXCEPTION 'a status change does not edit the cash voucher';
        END IF;
    ELSIF NEW.c_9ff27adb <> NEW.c_95ebf428 THEN
        RAISE EXCEPTION 'an edit of a draft writes a new line set under the new revision';
    END IF;
    IF NEW.c_8ced6547 = 2 AND NEW.c_5bf7c985 IS NULL THEN RAISE EXCEPTION 'a submitted cash voucher names its submitter'; END IF;
    IF NEW.c_8ced6547 = 1 AND NEW.c_5bf7c985 IS NOT NULL THEN RAISE EXCEPTION 'a draft has no submitter'; END IF;
    IF NEW.c_8ced6547 = 5 AND NEW.c_5bf7c985 IS DISTINCT FROM OLD.c_5bf7c985 THEN RAISE EXCEPTION 'cancellation keeps the submitter record'; END IF;
    IF NEW.c_8ced6547 = 2 THEN
        -- submission re-checks everything a draft was checked for: the account may have been deactivated, or a counter account bound, made a control account or deactivated, since the lines were written
        PERFORM f_fc5224e6(NEW.c_02b9651f, NEW.c_59c696ca, NEW.c_341dcfaf);
        IF NOT EXISTS (SELECT 1 FROM t_731339c2 x WHERE x.c_7cea0fae = NEW.c_9d4a288c AND x.c_cc728a10 = NEW.c_9ff27adb) THEN
            RAISE EXCEPTION 'cash_voucher_lines_required';
        END IF;
        FOR l IN SELECT x.c_31170b6e AS account FROM t_731339c2 x WHERE x.c_7cea0fae = NEW.c_9d4a288c AND x.c_cc728a10 = NEW.c_9ff27adb LOOP
            PERFORM f_cfd48c73(l.account);
        END LOOP;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_37ce8fc9 BEFORE INSERT OR UPDATE OR DELETE ON t_05fb4037 FOR EACH ROW EXECUTE FUNCTION f_a5d773e0();
CREATE TRIGGER r_64cd089f BEFORE TRUNCATE ON t_05fb4037 FOR EACH STATEMENT EXECUTE FUNCTION f_a5d773e0();

-- every revision of a voucher has its history event when the transaction commits (the event is written after the change, so the check is deferred)
CREATE FUNCTION f_ac6f19b8() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM t_ba09cd09 e WHERE e.c_abddc114 = NEW.c_9d4a288c AND e.c_e2361087 = NEW.c_95ebf428) THEN
        RAISE EXCEPTION 'every revision of a cash voucher has its history event';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_1cafc83e AFTER INSERT OR UPDATE ON t_05fb4037 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_ac6f19b8();
REVOKE ALL ON TABLE t_05fb4037 FROM PUBLIC;

CREATE FUNCTION f_f52f0656() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v record;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'cash voucher lines are append-only'; END IF;
    -- a share lock conflicts with a concurrent status UPDATE of the voucher: a line can never commit beside a submit or a cancel
    SELECT d.c_8ced6547 AS status, d.c_95ebf428 AS revision, d.c_9ff27adb AS lines_revision INTO v FROM t_05fb4037 d WHERE d.c_9d4a288c = NEW.c_7cea0fae FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'a cash voucher line belongs to a voucher'; END IF;
    IF v.status <> 1 THEN RAISE EXCEPTION 'the lines of a cash voucher are written only while it is a draft'; END IF;
    IF NEW.c_cc728a10 <> v.revision OR NEW.c_cc728a10 <> v.lines_revision THEN RAISE EXCEPTION 'a line set carries the current revision of its cash voucher'; END IF;
    IF EXISTS (SELECT 1 FROM t_ba09cd09 e WHERE e.c_abddc114 = NEW.c_7cea0fae AND e.c_e2361087 = NEW.c_cc728a10) THEN
        RAISE EXCEPTION 'the line set of a recorded revision is closed';
    END IF;
    PERFORM f_cfd48c73(NEW.c_31170b6e);
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_68c3d0b9 BEFORE INSERT OR UPDATE OR DELETE ON t_731339c2 FOR EACH ROW EXECUTE FUNCTION f_f52f0656();
CREATE TRIGGER r_9bb13b2f BEFORE TRUNCATE ON t_731339c2 FOR EACH STATEMENT EXECUTE FUNCTION f_f52f0656();
REVOKE ALL ON TABLE t_731339c2 FROM PUBLIC;

CREATE FUNCTION f_b14fead7() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'cash voucher events are append-only'; END IF;
    -- the events of this slice: 1 created, 2 updated, 3 submitted, 4 withdrawn, 5 cancelled, each with the statuses it connects
    IF NOT ((NEW.c_351c5cdd = 1 AND NEW.c_c9bb8a25 IS NULL AND NEW.c_bbe96a4d = 1)
         OR (NEW.c_351c5cdd = 2 AND NEW.c_c9bb8a25 = 1 AND NEW.c_bbe96a4d = 1)
         OR (NEW.c_351c5cdd = 3 AND NEW.c_c9bb8a25 = 1 AND NEW.c_bbe96a4d = 2)
         OR (NEW.c_351c5cdd = 4 AND NEW.c_c9bb8a25 = 2 AND NEW.c_bbe96a4d = 1)
         OR (NEW.c_351c5cdd = 5 AND NEW.c_c9bb8a25 IN (1, 2) AND NEW.c_bbe96a4d = 5)) THEN
        RAISE EXCEPTION 'unknown cash voucher event';
    END IF;
    IF (NEW.c_351c5cdd = 1) <> (NEW.c_e2361087 = 1) THEN RAISE EXCEPTION 'the first event of a cash voucher is its creation'; END IF;
    -- an event describes what ALREADY happened to its voucher in this transaction: its revision and resulting status must be the voucher's current ones (no forged or future event)
    IF NOT EXISTS (SELECT 1 FROM t_05fb4037 d WHERE d.c_9d4a288c = NEW.c_abddc114 AND d.c_95ebf428 = NEW.c_e2361087 AND d.c_8ced6547 = NEW.c_bbe96a4d) THEN
        RAISE EXCEPTION 'a cash voucher event must carry the current revision and status of its voucher';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_e4d5f8d7 BEFORE INSERT OR UPDATE OR DELETE ON t_ba09cd09 FOR EACH ROW EXECUTE FUNCTION f_b14fead7();
CREATE TRIGGER r_541eda9e BEFORE TRUNCATE ON t_ba09cd09 FOR EACH STATEMENT EXECUTE FUNCTION f_b14fead7();
REVOKE ALL ON TABLE t_ba09cd09 FROM PUBLIC;

-- the cash account master, now that a document can refer to it (docs/PHASE-3.14.md section 4): the branch and the bound ledger account are frozen once ANY voucher refers to the account (a different binding is a new cash or
-- bank account), and the account is not deactivated while one of its vouchers is open. In this slice a voucher is open unless it is cancelled (nothing can be approved yet); the slice that adds approval replaces this function.
-- The row is already locked when this BEFORE trigger runs, and a voucher takes a share lock on it, so neither rule can be raced.
CREATE FUNCTION f_3be56995() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.c_5b26d90b IS DISTINCT FROM OLD.c_5b26d90b OR NEW.c_320d83b3 IS DISTINCT FROM OLD.c_320d83b3)
       AND EXISTS (SELECT 1 FROM t_05fb4037 v WHERE v.c_02b9651f = OLD.c_a448c834) THEN
        RAISE EXCEPTION 'cash_account_binding_frozen';
    END IF;
    IF OLD.c_714dc13f AND NOT NEW.c_714dc13f
       AND EXISTS (SELECT 1 FROM t_05fb4037 v WHERE v.c_02b9651f = OLD.c_a448c834 AND v.c_8ced6547 <> 5) THEN
        RAISE EXCEPTION 'cash_account_has_open_documents';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_8cdf2eae BEFORE UPDATE ON t_1f78861e FOR EACH ROW EXECUTE FUNCTION f_3be56995();
