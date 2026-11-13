-- Company database migration 0006: fiscal years and accounting periods (Phase 3.1D).
-- Physical identifiers come from CompanySchema.FiscalYear / FiscalPeriod / FiscalHistory (generated, opaque); this file is immutable once applied.
-- Adds the fiscal calendar STRUCTURE and its state machine only. No journal entry, opening balance or posting behaviour is created: the General
-- Ledger and the posting engine do not exist yet and will call the posting-availability function (below) / FiscalPeriodGate inside their own transactions.
-- No data is seeded; existing company data is neither read nor modified. Privileges come only from 'Hasm.Admin grant-company-runtime'.
--
-- Invariants enforced by PostgreSQL (not by the application):
--   * a fiscal year is exactly 12 months starting on the first day of a month (company-specific fiscal calendar, e.g. July 1);
--   * fiscal years never overlap (exclusion constraint) and form ONE contiguous calendar (no gaps), checked at commit;
--   * a year has exactly its monthly (12) or quarterly (4) periods, contiguous, exact calendar spans, numbered from 1 (checked at commit);
--   * periods never overlap across the whole company (exclusion constraint), so a date belongs to at most one period;
--   * period states: 1 Open, 2 Soft Closed, 3 Final Closed. Only 1<->2 and 2<->3 transitions exist: Open can never jump to Final Closed and
--     Final Closed can never become Open directly; Final Closed always records who final-closed it;
--   * a Closed year (2) has only Final Closed periods and a period cannot leave Final Closed while its year is closed (checked at commit);
--   * years and periods are never deleted or truncated (owner included); their identity, calendar dates and number are immutable;
--     revision advances by exactly one per update;
--   * reopening a Final Closed period or a closed year needs a recorded reason of at least 10 characters (history CHECK).

CREATE TABLE t_4b5bb222 (
    c_cae1c56a uuid NOT NULL DEFAULT gen_random_uuid(),
    c_845d536e varchar(100) NOT NULL,
    c_5fd77e71 date NOT NULL,
    c_a4d1e8a9 date NOT NULL,
    c_394036a1 smallint NOT NULL,
    c_7f40775b varchar(64) NOT NULL,
    c_bd2464a9 smallint NOT NULL DEFAULT 1,
    c_7eb75e1d timestamptz NULL,
    c_8add4e27 uuid NULL,
    c_cd0b96d3 bigint NOT NULL DEFAULT 1,
    c_885c323c timestamptz NOT NULL,
    c_fcfa5d6f uuid NOT NULL,
    CONSTRAINT k_d0f78690 PRIMARY KEY (c_cae1c56a),
    CONSTRAINT k_85ad4328 CHECK (char_length(btrim(c_845d536e)) BETWEEN 1 AND 100),
    CONSTRAINT k_2d6cdf52 CHECK (extract(day FROM c_5fd77e71) = 1),
    CONSTRAINT k_22e787ad CHECK (c_a4d1e8a9 = (c_5fd77e71 + interval '1 year' - interval '1 day')::date),
    CONSTRAINT k_3d1d9fba CHECK (c_394036a1 IN (1, 2)),
    CONSTRAINT k_2ddfe435 CHECK (c_bd2464a9 IN (1, 2)),
    CONSTRAINT k_032ac2d3 CHECK ((c_bd2464a9 = 2) = (c_7eb75e1d IS NOT NULL AND c_8add4e27 IS NOT NULL)),
    CONSTRAINT k_d97650d5 CHECK (c_cd0b96d3 > 0),
    CONSTRAINT k_f3617578 EXCLUDE USING gist (daterange(c_5fd77e71, c_a4d1e8a9, '[]') WITH &&)
);
CREATE UNIQUE INDEX k_d952d054 ON t_4b5bb222 (upper(btrim(c_845d536e)));

CREATE FUNCTION f_436ebc08() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'fiscal years cannot be deleted'; END IF;
    IF NEW.c_cae1c56a IS DISTINCT FROM OLD.c_cae1c56a OR NEW.c_845d536e IS DISTINCT FROM OLD.c_845d536e OR NEW.c_5fd77e71 IS DISTINCT FROM OLD.c_5fd77e71
       OR NEW.c_a4d1e8a9 IS DISTINCT FROM OLD.c_a4d1e8a9 OR NEW.c_394036a1 IS DISTINCT FROM OLD.c_394036a1 OR NEW.c_7f40775b IS DISTINCT FROM OLD.c_7f40775b
       OR NEW.c_885c323c IS DISTINCT FROM OLD.c_885c323c OR NEW.c_fcfa5d6f IS DISTINCT FROM OLD.c_fcfa5d6f THEN
        RAISE EXCEPTION 'the identity and calendar of a fiscal year are immutable';
    END IF;
    IF NEW.c_cd0b96d3 <> OLD.c_cd0b96d3 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_9bbf6926 BEFORE UPDATE OR DELETE ON t_4b5bb222 FOR EACH ROW EXECUTE FUNCTION f_436ebc08();
CREATE TRIGGER r_27d9fdf6 BEFORE TRUNCATE ON t_4b5bb222 FOR EACH STATEMENT EXECUTE FUNCTION f_436ebc08();
REVOKE ALL ON TABLE t_4b5bb222 FROM PUBLIC;

CREATE TABLE t_ae996edc (
    c_d61cbb9c uuid NOT NULL DEFAULT gen_random_uuid(),
    c_957d240b uuid NOT NULL,
    c_c97ce291 smallint NOT NULL,
    c_8299fb3d date NOT NULL,
    c_c0322bcf date NOT NULL,
    c_0a0b7f19 smallint NOT NULL DEFAULT 1,
    c_2f01f8f1 timestamptz NULL,
    c_9b0a4ba4 uuid NULL,
    c_35fb3486 uuid NULL,
    c_1ac927f4 bigint NOT NULL DEFAULT 1,
    c_38f12ad5 timestamptz NOT NULL,
    CONSTRAINT k_338d8749 PRIMARY KEY (c_d61cbb9c),
    CONSTRAINT k_e1a8e61d FOREIGN KEY (c_957d240b) REFERENCES t_4b5bb222 (c_cae1c56a),
    CONSTRAINT k_4b9544db UNIQUE (c_957d240b, c_c97ce291),
    CONSTRAINT k_e60bb6bd CHECK (c_c97ce291 BETWEEN 1 AND 12),
    CONSTRAINT k_708c49b9 CHECK (c_8299fb3d <= c_c0322bcf),
    CONSTRAINT k_bf15df5a CHECK (c_0a0b7f19 IN (1, 2, 3)),
    CONSTRAINT k_0dfcb7aa CHECK (c_0a0b7f19 <> 3 OR c_35fb3486 IS NOT NULL),
    CONSTRAINT k_fc6b7a32 CHECK (c_1ac927f4 > 0),
    CONSTRAINT k_626845f8 EXCLUDE USING gist (daterange(c_8299fb3d, c_c0322bcf, '[]') WITH &&)
);
CREATE INDEX k_35062bb2 ON t_ae996edc (c_957d240b);

CREATE FUNCTION f_c2791eca() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'fiscal periods cannot be deleted'; END IF;
    IF NEW.c_d61cbb9c IS DISTINCT FROM OLD.c_d61cbb9c OR NEW.c_957d240b IS DISTINCT FROM OLD.c_957d240b OR NEW.c_c97ce291 IS DISTINCT FROM OLD.c_c97ce291
       OR NEW.c_8299fb3d IS DISTINCT FROM OLD.c_8299fb3d OR NEW.c_c0322bcf IS DISTINCT FROM OLD.c_c0322bcf OR NEW.c_38f12ad5 IS DISTINCT FROM OLD.c_38f12ad5 THEN
        RAISE EXCEPTION 'the identity and calendar of a fiscal period are immutable';
    END IF;
    IF NEW.c_1ac927f4 <> OLD.c_1ac927f4 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NEW.c_0a0b7f19 <> OLD.c_0a0b7f19 AND NOT ((OLD.c_0a0b7f19, NEW.c_0a0b7f19) IN ((1, 2), (2, 1), (2, 3), (3, 2))) THEN
        RAISE EXCEPTION 'invalid fiscal period state transition';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_0a7cd8d4 BEFORE UPDATE OR DELETE ON t_ae996edc FOR EACH ROW EXECUTE FUNCTION f_c2791eca();
CREATE TRIGGER r_02899443 BEFORE TRUNCATE ON t_ae996edc FOR EACH STATEMENT EXECUTE FUNCTION f_c2791eca();
REVOKE ALL ON TABLE t_ae996edc FROM PUBLIC;

-- Calendar consistency, judged at commit (so a year and its periods are inserted together, and multi-statement changes are judged as a whole).
CREATE FUNCTION f_4ef79731() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- every year has exactly its periods: right count, numbered from 1 without gaps, exact monthly/quarterly calendar spans
    IF EXISTS (
        SELECT 1 FROM t_4b5bb222 y
        WHERE (SELECT count(*) FROM t_ae996edc p WHERE p.c_957d240b = y.c_cae1c56a) <> CASE y.c_394036a1 WHEN 1 THEN 12 ELSE 4 END
           OR EXISTS (
                SELECT 1 FROM t_ae996edc p
                WHERE p.c_957d240b = y.c_cae1c56a
                  AND (p.c_8299fb3d <> (y.c_5fd77e71 + make_interval(months => (p.c_c97ce291 - 1) * CASE y.c_394036a1 WHEN 1 THEN 1 ELSE 3 END))::date
                    OR p.c_c0322bcf <> (p.c_8299fb3d + make_interval(months => CASE y.c_394036a1 WHEN 1 THEN 1 ELSE 3 END) - interval '1 day')::date))
    ) THEN
        RAISE EXCEPTION 'a fiscal year must consist exactly of its monthly or quarterly periods';
    END IF;
    -- the fiscal years form one contiguous calendar (no gaps): at most one year has no predecessor ending the day before it starts
    IF (SELECT count(*) FROM t_4b5bb222 y WHERE NOT EXISTS (SELECT 1 FROM t_4b5bb222 q WHERE q.c_a4d1e8a9 = y.c_5fd77e71 - 1)) > 1 THEN
        RAISE EXCEPTION 'fiscal years must be contiguous';
    END IF;
    -- a closed year has only Final Closed periods
    IF EXISTS (SELECT 1 FROM t_4b5bb222 y WHERE y.c_bd2464a9 = 2 AND EXISTS (SELECT 1 FROM t_ae996edc p WHERE p.c_957d240b = y.c_cae1c56a AND p.c_0a0b7f19 <> 3)) THEN
        RAISE EXCEPTION 'a closed fiscal year requires every period to be final closed';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_9cb5c87a AFTER INSERT OR UPDATE ON t_4b5bb222 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_4ef79731();
CREATE CONSTRAINT TRIGGER r_3cb5f09f AFTER INSERT OR UPDATE ON t_ae996edc DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_4ef79731();

-- Fiscal history (append-only): one row per audited change, linked to exactly one authenticated business audit event written in the SAME
-- transaction. entity: 1 fiscal year, 2 fiscal period (the entity id is polymorphic, so it is not a foreign key; the service writes it with the change).
CREATE TABLE t_94cc83c6 (
    c_306cda79 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_3fd27d56 smallint NOT NULL,
    c_7bf32b79 uuid NOT NULL,
    c_6eb10f43 bigint NOT NULL,
    c_2d933826 uuid NOT NULL,
    c_4b5a9505 timestamptz NOT NULL,
    c_0877ff9e jsonb NOT NULL,
    c_4ad5cf82 smallint NULL,
    c_7eed0210 smallint NULL,
    c_05625748 varchar(500) NULL,
    c_b97f7f5a varchar(48) NOT NULL,
    c_9bdd5615 bigint NOT NULL,
    CONSTRAINT k_68cf7849 PRIMARY KEY (c_306cda79),
    CONSTRAINT k_3f90b693 UNIQUE (c_3fd27d56, c_7bf32b79, c_6eb10f43),
    CONSTRAINT k_043ceda3 UNIQUE (c_b97f7f5a, c_9bdd5615),
    CONSTRAINT k_af39351e FOREIGN KEY (c_b97f7f5a, c_9bdd5615) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_4a9b15cc CHECK (c_3fd27d56 IN (1, 2) AND c_6eb10f43 > 0 AND jsonb_typeof(c_0877ff9e) = 'object'),
    -- reopening a Final Closed period (3 -> 2) or a closed year (2 -> 1) needs a recorded reason
    CONSTRAINT k_2fb5c89d CHECK (NOT ((c_3fd27d56 = 2 AND c_4ad5cf82 = 3 AND c_7eed0210 = 2) OR (c_3fd27d56 = 1 AND c_4ad5cf82 = 2 AND c_7eed0210 = 1))
        OR (c_05625748 IS NOT NULL AND char_length(btrim(c_05625748)) >= 10))
);
CREATE FUNCTION f_253b0ace() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'fiscal history is append-only';
END;
$$;
CREATE TRIGGER r_2608e160 BEFORE UPDATE OR DELETE ON t_94cc83c6 FOR EACH ROW EXECUTE FUNCTION f_253b0ace();
CREATE TRIGGER r_e67b01c2 BEFORE TRUNCATE ON t_94cc83c6 FOR EACH STATEMENT EXECUTE FUNCTION f_253b0ace();
REVOKE ALL ON TABLE t_94cc83c6 FROM PUBLIC;

-- Posting availability, for the future General Ledger / posting engine (called inside ITS transaction, on its connection): returns the id of the
-- period containing the date, locking the period row FOR SHARE so a concurrent close (which updates it) waits for in-flight postings and later
-- postings see the closed state. Refuses: no period for the date, Final Closed always, Soft Closed unless the caller is authorized to post into it.
CREATE FUNCTION f_a26691bc(posting_date date, allow_soft_closed boolean) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
    pid uuid;
    st smallint;
BEGIN
    SELECT p.c_d61cbb9c, p.c_0a0b7f19 INTO pid, st FROM t_ae996edc p WHERE posting_date BETWEEN p.c_8299fb3d AND p.c_c0322bcf FOR SHARE;
    IF pid IS NULL THEN RAISE EXCEPTION 'no fiscal period covers the posting date'; END IF;
    IF st = 3 THEN RAISE EXCEPTION 'the fiscal period is final closed'; END IF;
    IF st = 2 AND NOT allow_soft_closed THEN RAISE EXCEPTION 'the fiscal period is soft closed'; END IF;
    RETURN pid;
END;
$$;
