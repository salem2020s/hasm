-- Migration 0033: the STOCK-PERIOD-DB guard (owner decision D-220, option b). Forward-only and ADDITIVE: one new function and nine new BEFORE INSERT triggers. No table is created or altered, no
-- existing function, trigger or constraint is replaced, disabled or dropped, nothing is seeded and no existing row is read, validated or modified. Opaque physical names (D-029) generated deterministically by
-- tools/schema/company-0033-stock-period-guard.cjs (logical dictionary: CompanySchema.StockPeriodGuard.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: until now only the services refused a stock or cost posting into a fiscal period that is not Open (FiscalPeriodGate, allowSoftClosed = false), while journals have a database guard
-- (migrations 0008 and 0009). From this migration on PostgreSQL itself refuses to INSERT a stock or cost posting record whose effective date lies in a Soft Closed period, in a Final Closed period, or in no
-- fiscal period at all, whoever writes it (a service, a tool, plain SQL). There is no soft-close override for stock, exactly as in the services.
-- HOW: the trigger function reads the date column named by its argument and calls the posting-availability function of migration 0006, unchanged, with allow_soft_closed = false. That function locks the
-- period row FOR SHARE until the end of the transaction, so a concurrent close (which updates the row) waits for the posting, and a posting that waited for a close sees the closed state and is refused.
-- A posting service already holds that share lock (it calls FiscalPeriodGate before its first insert), so the guard adds no lock and no lock order to any service path.
-- The function returns NEW unchanged and writes nothing. It is not SECURITY DEFINER and sets no search_path: it runs with the rights of the writer, like every other trigger of this schema.
-- Records without an effective date of their own (cost layers and consumptions, which must reference a cost entry) are not guarded here.

CREATE FUNCTION f_6a1d5fb5() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_date date;
BEGIN
    IF TG_NARGS <> 1 THEN RAISE EXCEPTION 'the stock period guard needs the name of the date column'; END IF;
    v_date := (to_jsonb(NEW) ->> TG_ARGV[0])::date;
    IF v_date IS NULL THEN RAISE EXCEPTION 'a stock or cost posting needs an effective date'; END IF;
    PERFORM f_a26691bc(v_date, false);
    RETURN NEW;
END;
$$;

-- cost operations (receipt, issue, transfer, reversal)
CREATE TRIGGER r_e5cf76c4 BEFORE INSERT ON t_a648ca40 FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_a84fdcdc');
-- cost entries (nothing else ties an entry's date to its operation's date)
CREATE TRIGGER r_d7ccbb16 BEFORE INSERT ON t_bc727c1d FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_12665ffb');
-- stock postings (receipt, issue, transfer documents)
CREATE TRIGGER r_c44ed28d BEFORE INSERT ON t_4d4b251f FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_02d7d540');
-- reversals of stock postings (dated on the reversal's own date)
CREATE TRIGGER r_790d35cf BEFORE INSERT ON t_233a2574 FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_487438cf');
-- stock adjustment postings (also the effect of a stocktake)
CREATE TRIGGER r_f5027514 BEFORE INSERT ON t_fb4a8e51 FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_1d635eec');
-- reversals of stock adjustments
CREATE TRIGGER r_e3e56ad6 BEFORE INSERT ON t_a3118c51 FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_b3b8ce31');
-- negative-stock obligations
CREATE TRIGGER r_30a2fbbc BEFORE INSERT ON t_b56b119d FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_1f0961d8');
-- negative-stock settlements (dated on the settling receipt)
CREATE TRIGGER r_f4ee0e04 BEFORE INSERT ON t_d370b1d6 FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_5e337f95');
-- reversals of negative-stock obligations
CREATE TRIGGER r_45428e44 BEFORE INSERT ON t_075e0837 FOR EACH ROW EXECUTE FUNCTION f_6a1d5fb5('c_9f7cb668');
