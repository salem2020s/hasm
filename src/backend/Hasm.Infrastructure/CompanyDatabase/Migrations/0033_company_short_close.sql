-- Migration 0033: short close of commercial documents (Phase 3.13, owner decision D-201). Forward-only and ADDITIVE: ONE new table, ONE new guard on the document link table and ONE new guard on the commercial
-- document table; no existing object is dropped or replaced (the 0028, 0029 and 0030 functions are untouched), no existing row is read or modified, nothing is seeded. Opaque physical names (D-029) generated
-- deterministically by tools/schema/company-0033-short-close.cjs (logical dictionary: CompanySchema.ShortClose.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.13.md "Short close"; D-201: "An explicitly authorised short-close action may close the remaining quantity with a reason and audit, but must not invent deliveries, invoices or stock
-- movements."): an append-only record that a quantity of a line of an APPROVED commercial document will not be fulfilled or invoiced any more. It writes no link, no stock document, no journal entry. The quantity left
-- open on a line is DERIVED (ordered - max(fulfilled, invoiced) - short closed) and never stored. Reversing a short close is NOT offered (owner question, default: no).
-- Invariants enforced by PostgreSQL:
--   * a row is never changed or removed (no UPDATE, DELETE or TRUNCATE); one row per (creator, idempotency key, line); a reason of 1 to 500 characters without control characters is mandatory;
--   * the document is APPROVED and the line belongs to it; the document is share-locked FIRST and the line locked (FOR NO KEY UPDATE) BEFORE any sum is read, in the order of a link and of a document edit, so
--     concurrent closes and links against the same line are serialized;
--   * TOLERANCE 0 STAYS: the quantity closed on a line never exceeds ordered - max(fulfilled, invoiced) (what was delivered or invoiced is never closed), and after a close a link of kind 1 (fulfils) or 2 (invoices)
--     from that line cannot take more than ordered - closed (an additive guard on the link table, same lock order as the 0030 guard, so the 0030 function is not replaced);
--   * an approved document that has a short close cannot leave the approved status (no cancellation, no re-approval): the same interim restriction as for linked documents (D-075).

CREATE TABLE t_b5c52137 (
    c_8a71c64d uuid NOT NULL,
    c_56c1ac5b uuid NOT NULL,
    c_116e52ec uuid NOT NULL,
    c_b96809d6 uuid NOT NULL,
    c_f61604f4 numeric(28,10) NOT NULL,
    c_3b77bfdf varchar(500) NOT NULL,
    c_9a404142 varchar(64) NOT NULL,
    c_09c440b4 char(64) NOT NULL,
    c_e79d143e uuid NOT NULL,
    c_a5a427fa timestamptz NOT NULL,
    CONSTRAINT k_ce4bad32 PRIMARY KEY (c_8a71c64d),
    CONSTRAINT k_d4a6b5e3 FOREIGN KEY (c_116e52ec) REFERENCES t_04d17dc0 (c_cceb8d4c),
    CONSTRAINT k_ab64ab4a FOREIGN KEY (c_b96809d6) REFERENCES t_33fca2e4 (c_57d9a479),
    CONSTRAINT k_8491a2d4 CHECK (c_f61604f4 > 0 AND c_f61604f4 < 1000000000
        AND char_length(c_3b77bfdf) BETWEEN 1 AND 500 AND c_3b77bfdf !~ '[[:cntrl:]]' AND btrim(c_3b77bfdf) <> ''
        AND char_length(c_9a404142) BETWEEN 8 AND 64 AND c_09c440b4 ~ '^[0-9a-f]{64}$')
);
CREATE UNIQUE INDEX k_559a88a4 ON t_b5c52137 (c_e79d143e, c_9a404142, c_b96809d6);
CREATE INDEX k_15933072 ON t_b5c52137 (c_b96809d6);
CREATE INDEX k_7fb83f09 ON t_b5c52137 (c_116e52ec);
CREATE INDEX k_a929cc6d ON t_b5c52137 (c_56c1ac5b);

CREATE FUNCTION f_753d3c3c() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ldoc uuid; lqty numeric; dstatus smallint; fulfilled numeric; invoiced numeric; closed numeric;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'short close records are append-only'; END IF;
    -- LOCK ORDER (the order of a link and of a document edit: the document row first, its line second): the document is share-locked, a concurrent cancel or re-approval waits for this transaction; THEN the line is
    -- locked BEFORE any sum is read, so every close and every link against the same line is serialized here
    PERFORM 1 FROM t_04d17dc0 d WHERE d.c_cceb8d4c = NEW.c_116e52ec FOR SHARE;
    SELECT d.c_8cb62046 INTO dstatus FROM t_04d17dc0 d WHERE d.c_cceb8d4c = NEW.c_116e52ec;
    PERFORM 1 FROM t_33fca2e4 l WHERE l.c_57d9a479 = NEW.c_b96809d6 FOR NO KEY UPDATE;
    SELECT l.c_84431fb3, l.c_30743cc1 INTO ldoc, lqty FROM t_33fca2e4 l WHERE l.c_57d9a479 = NEW.c_b96809d6;
    IF ldoc IS DISTINCT FROM NEW.c_116e52ec THEN RAISE EXCEPTION 'a short close names a line of its own document'; END IF;
    IF dstatus IS DISTINCT FROM 3 THEN RAISE EXCEPTION 'only an approved document can be closed short'; END IF;
    SELECT COALESCE(sum(k.c_b7e71c2d) FILTER (WHERE k.c_9c158568 = 1), 0), COALESCE(sum(k.c_b7e71c2d) FILTER (WHERE k.c_9c158568 = 2), 0) INTO fulfilled, invoiced
        FROM t_9551bdbe k WHERE k.c_e817adff = NEW.c_b96809d6;
    SELECT COALESCE(sum(s.c_f61604f4), 0) INTO closed FROM t_b5c52137 s WHERE s.c_b96809d6 = NEW.c_b96809d6;
    IF closed + NEW.c_f61604f4 > lqty - GREATEST(fulfilled, invoiced) THEN
        RAISE EXCEPTION 'a short close never exceeds the open quantity (ordered minus the larger of fulfilled and invoiced minus already closed)';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_630632a1 BEFORE INSERT OR UPDATE OR DELETE ON t_b5c52137 FOR EACH ROW EXECUTE FUNCTION f_753d3c3c();
CREATE TRIGGER r_b8933a0b BEFORE TRUNCATE ON t_b5c52137 FOR EACH STATEMENT EXECUTE FUNCTION f_753d3c3c();
REVOKE ALL ON TABLE t_b5c52137 FROM PUBLIC;

-- tolerance 0 after a close: a link that fulfils or invoices a line cannot take more than ordered - closed. This guard takes the SAME locks in the SAME order as the link guard of 0030 (both documents share-locked in
-- id order, then both lines in id order), so whichever of the two guards fires first, no lock order is ever inverted and no sum is read before the line is locked.
CREATE FUNCTION f_fc72a8e2() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    fqty numeric; closed numeric; used numeric;
BEGIN
    IF NEW.c_9c158568 NOT IN (1, 2) THEN RETURN NEW; END IF;
    PERFORM 1 FROM t_04d17dc0 d WHERE d.c_cceb8d4c IN (NEW.c_dcc9ea43, NEW.c_991145c6) ORDER BY d.c_cceb8d4c FOR SHARE;
    PERFORM 1 FROM t_33fca2e4 l WHERE l.c_57d9a479 IN (NEW.c_e817adff, NEW.c_8702ed66) ORDER BY l.c_57d9a479 FOR NO KEY UPDATE;
    SELECT COALESCE(sum(s.c_f61604f4), 0) INTO closed FROM t_b5c52137 s WHERE s.c_b96809d6 = NEW.c_e817adff;
    IF closed > 0 THEN
        SELECT l.c_30743cc1 INTO fqty FROM t_33fca2e4 l WHERE l.c_57d9a479 = NEW.c_e817adff;
        SELECT COALESCE(sum(k.c_b7e71c2d), 0) INTO used FROM t_9551bdbe k WHERE k.c_e817adff = NEW.c_e817adff AND k.c_9c158568 = NEW.c_9c158568;
        IF used + NEW.c_b7e71c2d > fqty - closed THEN RAISE EXCEPTION 'over-fulfilment: the quantity linked from a source line never exceeds its quantity minus what was closed short'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_5359a0d0 BEFORE INSERT ON t_9551bdbe FOR EACH ROW EXECUTE FUNCTION f_fc72a8e2();

-- a document that was closed short stays approved (the same interim restriction as for linked documents, D-075): no cancellation and no re-approval until the owner decides how closed documents may be reversed
CREATE FUNCTION f_93ef8c9e() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.c_8cb62046 = 3 AND NEW.c_8cb62046 <> 3
       AND EXISTS (SELECT 1 FROM t_b5c52137 s WHERE s.c_116e52ec = OLD.c_cceb8d4c) THEN
        RAISE EXCEPTION 'an approved document that was closed short cannot leave the approved status';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_c5bcec34 BEFORE UPDATE ON t_04d17dc0 FOR EACH ROW EXECUTE FUNCTION f_93ef8c9e();
