-- Migration 0030: document links and partial fulfilment (Phase 3.13 slice S2). Forward-only, additive: ONE new table and ONE new guard on the commercial document table; no existing row is read or modified,
-- no existing object is dropped or replaced, and nothing is seeded. Opaque physical names (D-029) generated deterministically by tools/schema/company-0030-document-links.cjs (logical dictionary: CompanySchema.DocumentLink.cs).
-- Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.13.md S2; decisions D-074..D-077): an append-only link from a line of one approved commercial document (the SOURCE, e.g. an order or a delivery) to a line of another approved document of the
-- same party, currency and branch (the TARGET, e.g. a delivery, an invoice or a return), with a kind (1 fulfils, 2 invoices, 3 returns, 4 amends) and a quantity. Ordered, fulfilled, invoiced and returned quantities are
-- DERIVED from these rows and never stored, so a counter cannot drift.
-- Invariants enforced by PostgreSQL:
--   * a link is never changed or removed (no UPDATE, DELETE or TRUNCATE); one link per (source line, target line, kind); one idempotency key per creator;
--   * both documents are APPROVED when the link is written (they are share-locked FIRST, so a concurrent cancel or status change cannot interleave and a link never deadlocks with a document edit), differ, share party, currency and branch, and the named lines belong to them;
--   * OVER-FULFILMENT IS REFUSED (Q3 default: tolerance 0, owner decision pending): the quantity linked from one source line for one kind never exceeds the quantity of that line, and the quantity linked into one target line
--     for one kind never exceeds the quantity of that line. Both lines are locked (FOR NO KEY UPDATE, in id order, so concurrent links never deadlock) BEFORE the sums are read, so concurrent links against the last remaining
--     quantity create exactly the allowed ones;
--   * an approved document that has links cannot leave the approved status (no cancellation, no re-approval): an interim restriction (D-075), stricter than the owner question Q8.

CREATE TABLE t_9551bdbe (
    c_7a1c8993 uuid NOT NULL,
    c_dcc9ea43 uuid NOT NULL,
    c_e817adff uuid NOT NULL,
    c_991145c6 uuid NOT NULL,
    c_8702ed66 uuid NOT NULL,
    c_9c158568 smallint NOT NULL,
    c_b7e71c2d numeric(28,10) NOT NULL,
    c_fb39f390 varchar(64) NOT NULL,
    c_5dadcf5c char(64) NOT NULL,
    c_64a36078 uuid NOT NULL,
    c_db8b5f42 timestamptz NOT NULL,
    CONSTRAINT k_8dadb402 PRIMARY KEY (c_7a1c8993),
    CONSTRAINT k_7dcdbc8a FOREIGN KEY (c_dcc9ea43) REFERENCES t_04d17dc0 (c_cceb8d4c),
    CONSTRAINT k_8b2da23e FOREIGN KEY (c_e817adff) REFERENCES t_33fca2e4 (c_57d9a479),
    CONSTRAINT k_6a7ce2b6 FOREIGN KEY (c_991145c6) REFERENCES t_04d17dc0 (c_cceb8d4c),
    CONSTRAINT k_9fe7a241 FOREIGN KEY (c_8702ed66) REFERENCES t_33fca2e4 (c_57d9a479),
    CONSTRAINT k_30bca16c CHECK (c_9c158568 IN (1, 2, 3, 4) AND c_b7e71c2d > 0 AND c_b7e71c2d < 1000000000
        AND c_dcc9ea43 <> c_991145c6 AND c_e817adff <> c_8702ed66
        AND char_length(c_fb39f390) BETWEEN 8 AND 64 AND c_5dadcf5c ~ '^[0-9a-f]{64}$')
);
CREATE UNIQUE INDEX k_b913bfe7 ON t_9551bdbe (c_e817adff, c_8702ed66, c_9c158568);
CREATE UNIQUE INDEX k_5729c044 ON t_9551bdbe (c_64a36078, c_fb39f390);
CREATE INDEX k_8b2aad00 ON t_9551bdbe (c_e817adff, c_9c158568);
CREATE INDEX k_8ad3cdc1 ON t_9551bdbe (c_8702ed66, c_9c158568);
CREATE INDEX k_bc002ce3 ON t_9551bdbe (c_dcc9ea43);
CREATE INDEX k_87178fab ON t_9551bdbe (c_991145c6);

CREATE FUNCTION f_b2ac0b80() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    fdoc uuid; tdoc uuid; fqty numeric; tqty numeric; fstatus smallint; tstatus smallint;
    fparty uuid; tparty uuid; fcur char(3); tcur char(3); fbranch uuid; tbranch uuid; used numeric;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'document links are append-only'; END IF;
    -- LOCK ORDER (same as a document edit: the document row first, its lines second, so a link and a material edit can never deadlock): the two documents are share-locked in id order, a concurrent status change
    -- (cancel, re-approval) waits for this transaction and this transaction sees the status it left; THEN the two lines are locked in id order BEFORE any sum is read, so every link against the same line is serialized
    -- here and the statements below see every link that committed before the lock was granted
    PERFORM 1 FROM t_04d17dc0 d WHERE d.c_cceb8d4c IN (NEW.c_dcc9ea43, NEW.c_991145c6) ORDER BY d.c_cceb8d4c FOR SHARE;
    SELECT d.c_8cb62046, d.c_b2463fae, d.c_496bb3d6, d.c_aae9b28d INTO fstatus, fparty, fcur, fbranch FROM t_04d17dc0 d WHERE d.c_cceb8d4c = NEW.c_dcc9ea43;
    SELECT d.c_8cb62046, d.c_b2463fae, d.c_496bb3d6, d.c_aae9b28d INTO tstatus, tparty, tcur, tbranch FROM t_04d17dc0 d WHERE d.c_cceb8d4c = NEW.c_991145c6;
    PERFORM 1 FROM t_33fca2e4 l WHERE l.c_57d9a479 IN (NEW.c_e817adff, NEW.c_8702ed66) ORDER BY l.c_57d9a479 FOR NO KEY UPDATE;
    SELECT l.c_84431fb3, l.c_30743cc1 INTO fdoc, fqty FROM t_33fca2e4 l WHERE l.c_57d9a479 = NEW.c_e817adff;
    SELECT l.c_84431fb3, l.c_30743cc1 INTO tdoc, tqty FROM t_33fca2e4 l WHERE l.c_57d9a479 = NEW.c_8702ed66;
    IF fdoc IS DISTINCT FROM NEW.c_dcc9ea43 OR tdoc IS DISTINCT FROM NEW.c_991145c6 THEN RAISE EXCEPTION 'a link names lines of its own documents'; END IF;
    IF fstatus IS DISTINCT FROM 3 OR tstatus IS DISTINCT FROM 3 THEN RAISE EXCEPTION 'both documents of a link must be approved'; END IF;
    IF fparty IS DISTINCT FROM tparty OR fcur IS DISTINCT FROM tcur OR fbranch IS DISTINCT FROM tbranch THEN RAISE EXCEPTION 'linked documents must share the party, the currency and the branch'; END IF;
    IF NEW.c_b7e71c2d > fqty OR NEW.c_b7e71c2d > tqty THEN RAISE EXCEPTION 'a link never exceeds the quantity of either line'; END IF;
    IF NEW.c_9c158568 IN (1, 2, 3) THEN
        SELECT COALESCE(sum(k.c_b7e71c2d), 0) INTO used FROM t_9551bdbe k WHERE k.c_e817adff = NEW.c_e817adff AND k.c_9c158568 = NEW.c_9c158568;
        IF used + NEW.c_b7e71c2d > fqty THEN RAISE EXCEPTION 'over-fulfilment: the quantity linked from a source line never exceeds its quantity'; END IF;
        SELECT COALESCE(sum(k.c_b7e71c2d), 0) INTO used FROM t_9551bdbe k WHERE k.c_8702ed66 = NEW.c_8702ed66 AND k.c_9c158568 = NEW.c_9c158568;
        IF used + NEW.c_b7e71c2d > tqty THEN RAISE EXCEPTION 'over-fulfilment: the quantity linked into a target line never exceeds its quantity'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_88a936d8 BEFORE INSERT OR UPDATE OR DELETE ON t_9551bdbe FOR EACH ROW EXECUTE FUNCTION f_b2ac0b80();
CREATE TRIGGER r_e6e0c105 BEFORE TRUNCATE ON t_9551bdbe FOR EACH STATEMENT EXECUTE FUNCTION f_b2ac0b80();
REVOKE ALL ON TABLE t_9551bdbe FROM PUBLIC;

-- a document that has links stays approved (interim restriction D-075): no cancellation and no re-approval until the owner decides how linked documents may be reversed
CREATE FUNCTION f_fdb0007a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.c_8cb62046 = 3 AND NEW.c_8cb62046 <> 3
       AND EXISTS (SELECT 1 FROM t_9551bdbe k WHERE k.c_dcc9ea43 = OLD.c_cceb8d4c OR k.c_991145c6 = OLD.c_cceb8d4c) THEN
        RAISE EXCEPTION 'an approved document that has links cannot leave the approved status';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_209f3a7f BEFORE UPDATE ON t_04d17dc0 FOR EACH ROW EXECUTE FUNCTION f_fdb0007a();
