-- Migration 0034: the STOCK-PERIOD-DB guard (owner decision D-220, option b). Forward-only and ADDITIVE: two new functions and eleven new BEFORE INSERT triggers. No table is created or altered, no
-- existing function, trigger or constraint is replaced, disabled or dropped, nothing is seeded and no existing row is read, validated or modified. Opaque physical names (D-029) generated deterministically by
-- tools/schema/company-0034-stock-period-guard.cjs (logical dictionary: CompanySchema.StockPeriodGuard.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: until now only the services refused a stock or cost posting into a fiscal period that is not Open (FiscalPeriodGate, allowSoftClosed = false), while journals have a database guard
-- (migrations 0008 and 0009). From this migration on PostgreSQL itself refuses to INSERT a stock or cost posting record whose effective date lies in a Soft Closed period, in a Final Closed period, or in no
-- fiscal period at all, whoever writes it (a service, a tool, plain SQL). There is no soft-close override for stock, exactly as in the services.
-- HOW: the trigger function reads the date column named by its argument and calls the posting-availability function of migration 0006, unchanged, with allow_soft_closed = false. That function locks the
-- period row FOR SHARE until the end of the transaction, so a concurrent close (which updates the row) waits for the posting, and a posting that waited for a close sees the closed state and is refused.
-- A posting service already holds that share lock (it calls FiscalPeriodGate before its first insert), so the guard adds no lock and no lock order to any service path.
-- The function returns NEW unchanged and writes nothing. It is not SECURITY DEFINER and sets no search_path: it runs with the rights of the writer, like every other trigger of this schema.
-- Cost layers and cost consumptions have no date of their own and must reference a cost entry (NOT NULL foreign key): a second function reads the date of the REFERENCED entry (the entry that inserts the
-- record, written in the same transaction by the engine; never the receipt date of an old layer a consumption draws on) and applies the same check. An id that matches no entry is passed on unchanged so the
-- foreign key gives its usual refusal. Same properties as above: additive, no lock beyond the share lock, no dynamic SQL, not SECURITY DEFINER.

CREATE FUNCTION f_981674fd() RETURNS trigger LANGUAGE plpgsql AS $$
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

-- The date of the referenced cost entry, for the records that carry none.
CREATE FUNCTION f_92619584() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_date date;
BEGIN
    IF TG_NARGS <> 1 THEN RAISE EXCEPTION 'the stock period entry guard needs the name of the cost entry column'; END IF;
    SELECT e.c_12665ffb INTO v_date FROM t_bc727c1d e WHERE e.c_ed61ae1a = (to_jsonb(NEW) ->> TG_ARGV[0])::bigint;
    IF v_date IS NOT NULL THEN PERFORM f_a26691bc(v_date, false); END IF;
    RETURN NEW;
END;
$$;

-- cost operations (receipt, issue, transfer, reversal)
CREATE TRIGGER r_efa9edbc BEFORE INSERT ON t_a648ca40 FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_a84fdcdc');
-- cost entries (nothing else ties an entry's date to its operation's date)
CREATE TRIGGER r_ee383eac BEFORE INSERT ON t_bc727c1d FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_12665ffb');
-- stock postings (receipt, issue, transfer documents)
CREATE TRIGGER r_1af886fa BEFORE INSERT ON t_4d4b251f FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_02d7d540');
-- reversals of stock postings (dated on the reversal's own date)
CREATE TRIGGER r_229c0fee BEFORE INSERT ON t_233a2574 FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_487438cf');
-- stock adjustment postings (also the effect of a stocktake)
CREATE TRIGGER r_c64c00ae BEFORE INSERT ON t_fb4a8e51 FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_1d635eec');
-- reversals of stock adjustments
CREATE TRIGGER r_00d5f5bf BEFORE INSERT ON t_a3118c51 FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_b3b8ce31');
-- negative-stock obligations
CREATE TRIGGER r_1e9a1dfd BEFORE INSERT ON t_b56b119d FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_1f0961d8');
-- negative-stock settlements (dated on the settling receipt)
CREATE TRIGGER r_2daa25c7 BEFORE INSERT ON t_d370b1d6 FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_5e337f95');
-- reversals of negative-stock obligations
CREATE TRIGGER r_9751bdbe BEFORE INSERT ON t_075e0837 FOR EACH ROW EXECUTE FUNCTION f_981674fd('c_9f7cb668');
-- cost layers (dated by the cost entry that creates them)
CREATE TRIGGER r_5150b0a5 BEFORE INSERT ON t_3147676c FOR EACH ROW EXECUTE FUNCTION f_92619584('c_07583c02');
-- cost consumptions and restorations (dated by the cost entry that records them, not by the consumed layer)
CREATE TRIGGER r_4277fa33 BEFORE INSERT ON t_6df3b92e FOR EACH ROW EXECUTE FUNCTION f_92619584('c_b17f9a0c');
