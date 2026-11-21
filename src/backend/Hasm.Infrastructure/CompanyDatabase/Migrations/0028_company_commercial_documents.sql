-- Migration 0028: commercial document skeleton (Phase 3.13 slice S1a). Forward-only, additive (three new tables); no existing row is read or modified, nothing is seeded.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0028-commercial.cjs (logical dictionary: CompanySchema.Commercial.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 7.2, 7.6, 4.3, 19.2, 19.7; docs/PHASE-3.13.md): the commercial-status track of a sales/purchase document: a header, its lines (item, unit, quantity and an optional warehouse; NO price, tax or cost in
-- this slice) and an append-only history of status changes. It is a business document skeleton: no stock, no journal and no amount exists here. Physical and accounting tracks are later slices and are DERIVED, never flags here.
-- Invariants enforced by PostgreSQL:
--   * documents and events are never removed; the identity (type, branch, creator, idempotency key, payload hash) never changes; every change advances the revision by one;
--   * the status guard allows ONLY draft -> submitted, submitted -> draft (withdraw), and draft/submitted -> cancelled (S1a). Approval, rejection and the cancellation of an approved document are NOT in this guard:
--     a direct statement attempting them is refused. Slice S1b adds them through a new migration that replaces this guard. A cancelled document is final;
--   * a submitted or cancelled document is not edited (a status change edits nothing else); the lines are editable (insert, update, delete) ONLY while the document is a draft;
--   * the events are append-only, one per revision, and each must carry the CURRENT revision and status of its document (events are written after the document change in the same transaction); one idempotency key per (document type, creator).

CREATE TABLE t_04d17dc0 (
    c_cceb8d4c uuid NOT NULL,
    c_56e38e95 varchar(40) NOT NULL,
    c_aae9b28d uuid NOT NULL,
    c_b2463fae uuid NOT NULL,
    c_496bb3d6 char(3) NOT NULL,
    c_e55881ff date NOT NULL,
    c_523bf0a9 smallint NOT NULL,
    c_8cb62046 smallint NOT NULL,
    c_2bf97e4a varchar(500) NULL,
    c_79b8937e varchar(64) NOT NULL,
    c_5412b89c char(64) NOT NULL,
    c_abe96b9c uuid NULL,
    c_d2cb57e3 bigint NOT NULL DEFAULT 1,
    c_6258e1f8 uuid NOT NULL,
    c_b9cf066a timestamptz NOT NULL,
    c_f68f72d4 timestamptz NOT NULL,
    CONSTRAINT k_05d39dd5 PRIMARY KEY (c_cceb8d4c),
    CONSTRAINT k_54e77fb4 FOREIGN KEY (c_aae9b28d) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_262835b9 FOREIGN KEY (c_b2463fae) REFERENCES t_8edce6b0 (c_3f494703),
    CONSTRAINT k_e53cf7db CHECK (c_d2cb57e3 > 0 AND c_56e38e95 ~ '^[a-z][a-z0-9_]{1,39}$' AND c_496bb3d6 ~ '^[A-Z]{3}$'
        AND c_523bf0a9 IN (1, 2) AND c_8cb62046 IN (1, 2, 3, 4, 5) AND c_5412b89c ~ '^[0-9a-f]{64}$'
        AND char_length(c_79b8937e) BETWEEN 8 AND 64)
);
CREATE UNIQUE INDEX k_f728c5f5 ON t_04d17dc0 (c_56e38e95, c_6258e1f8, c_79b8937e);
CREATE INDEX k_25a7421c ON t_04d17dc0 (c_aae9b28d, c_b9cf066a);

CREATE FUNCTION f_bc72c55d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_8cb62046 <> 1 THEN RAISE EXCEPTION 'a commercial document is created as a draft'; END IF;
        IF NEW.c_d2cb57e3 <> 1 THEN RAISE EXCEPTION 'a commercial document is created at revision 1'; END IF;
        IF NEW.c_abe96b9c IS NOT NULL THEN RAISE EXCEPTION 'a draft has no submitter'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'commercial documents are never removed'; END IF;
    IF NEW.c_cceb8d4c IS DISTINCT FROM OLD.c_cceb8d4c OR NEW.c_56e38e95 IS DISTINCT FROM OLD.c_56e38e95
       OR NEW.c_aae9b28d IS DISTINCT FROM OLD.c_aae9b28d OR NEW.c_6258e1f8 IS DISTINCT FROM OLD.c_6258e1f8
       OR NEW.c_b9cf066a IS DISTINCT FROM OLD.c_b9cf066a OR NEW.c_79b8937e IS DISTINCT FROM OLD.c_79b8937e
       OR NEW.c_5412b89c IS DISTINCT FROM OLD.c_5412b89c THEN
        RAISE EXCEPTION 'the identity of a commercial document is immutable';
    END IF;
    IF NEW.c_d2cb57e3 <> OLD.c_d2cb57e3 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NOT ((OLD.c_8cb62046 = 1 AND NEW.c_8cb62046 IN (1, 2, 5)) OR (OLD.c_8cb62046 = 2 AND NEW.c_8cb62046 IN (1, 5))) THEN
        RAISE EXCEPTION 'illegal commercial document status transition';
    END IF;
    IF NEW.c_8cb62046 <> OLD.c_8cb62046 AND (NEW.c_b2463fae IS DISTINCT FROM OLD.c_b2463fae OR NEW.c_496bb3d6 IS DISTINCT FROM OLD.c_496bb3d6
       OR NEW.c_e55881ff IS DISTINCT FROM OLD.c_e55881ff OR NEW.c_523bf0a9 IS DISTINCT FROM OLD.c_523bf0a9
       OR NEW.c_2bf97e4a IS DISTINCT FROM OLD.c_2bf97e4a) THEN
        RAISE EXCEPTION 'a status change does not edit the document';
    END IF;
    IF NEW.c_8cb62046 = 2 AND NEW.c_abe96b9c IS NULL THEN RAISE EXCEPTION 'a submitted document names its submitter'; END IF;
    IF NEW.c_8cb62046 = 1 AND NEW.c_abe96b9c IS NOT NULL THEN RAISE EXCEPTION 'a draft has no submitter'; END IF;
    IF NEW.c_8cb62046 = 5 AND NEW.c_abe96b9c IS DISTINCT FROM OLD.c_abe96b9c THEN RAISE EXCEPTION 'cancellation keeps the submitter record'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_af347c21 BEFORE INSERT OR UPDATE OR DELETE ON t_04d17dc0 FOR EACH ROW EXECUTE FUNCTION f_bc72c55d();
CREATE TRIGGER r_4f5b9645 BEFORE TRUNCATE ON t_04d17dc0 FOR EACH STATEMENT EXECUTE FUNCTION f_bc72c55d();
REVOKE ALL ON TABLE t_04d17dc0 FROM PUBLIC;

CREATE TABLE t_33fca2e4 (
    c_57d9a479 uuid NOT NULL,
    c_84431fb3 uuid NOT NULL,
    c_25794504 integer NOT NULL,
    c_48ec30ff uuid NOT NULL,
    c_fec3491f uuid NOT NULL,
    c_30743cc1 numeric(28,10) NOT NULL,
    c_1c240a49 uuid NULL,
    CONSTRAINT k_1cb4a02d PRIMARY KEY (c_57d9a479),
    CONSTRAINT k_3512f5e0 FOREIGN KEY (c_84431fb3) REFERENCES t_04d17dc0 (c_cceb8d4c),
    CONSTRAINT k_d563e33c FOREIGN KEY (c_48ec30ff) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_3564c568 FOREIGN KEY (c_fec3491f) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_0d4db743 FOREIGN KEY (c_1c240a49) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_62b3d49c CHECK (c_25794504 BETWEEN 1 AND 5000 AND c_30743cc1 > 0 AND c_30743cc1 < 1000000000)
);
CREATE UNIQUE INDEX k_3f5fce2c ON t_33fca2e4 (c_84431fb3, c_25794504);

CREATE FUNCTION f_46efd57c() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE doc uuid; st smallint;
BEGIN
    IF TG_OP = 'DELETE' THEN doc := OLD.c_84431fb3; ELSE doc := NEW.c_84431fb3; END IF;
    SELECT d.c_8cb62046 INTO st FROM t_04d17dc0 d WHERE d.c_cceb8d4c = doc FOR SHARE;   -- a share lock conflicts with a concurrent status UPDATE (FOR NO KEY UPDATE): a line write can never commit beside a submit or cancel
    IF st IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'the lines of a commercial document are editable only while it is a draft'; END IF;
    IF TG_OP = 'UPDATE' AND NEW.c_84431fb3 IS DISTINCT FROM OLD.c_84431fb3 THEN RAISE EXCEPTION 'a line never moves to another document'; END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_e0546034 BEFORE INSERT OR UPDATE OR DELETE ON t_33fca2e4 FOR EACH ROW EXECUTE FUNCTION f_46efd57c();
CREATE FUNCTION f_76b17d5c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'commercial document lines are never truncated';
END;
$$;
CREATE TRIGGER r_e63b4d8a BEFORE TRUNCATE ON t_33fca2e4 FOR EACH STATEMENT EXECUTE FUNCTION f_76b17d5c();
REVOKE ALL ON TABLE t_33fca2e4 FROM PUBLIC;

CREATE TABLE t_9ff18b1b (
    c_aaa11be9 uuid NOT NULL,
    c_20dec8a8 uuid NOT NULL,
    c_1304e0f0 smallint NOT NULL,
    c_d2a4f738 smallint NULL,
    c_166ae51d smallint NOT NULL,
    c_dc2643b7 bigint NOT NULL,
    c_277ed4a5 uuid NOT NULL,
    c_fe5fd9b3 varchar(500) NULL,
    c_155e5a36 timestamptz NOT NULL,
    CONSTRAINT k_1f157111 PRIMARY KEY (c_aaa11be9),
    CONSTRAINT k_d39369a2 FOREIGN KEY (c_20dec8a8) REFERENCES t_04d17dc0 (c_cceb8d4c),
    CONSTRAINT k_70ff0005 CHECK (c_1304e0f0 BETWEEN 1 AND 9 AND c_166ae51d BETWEEN 1 AND 5 AND (c_d2a4f738 IS NULL OR c_d2a4f738 BETWEEN 1 AND 5) AND c_dc2643b7 > 0)
);
CREATE UNIQUE INDEX k_b806d405 ON t_9ff18b1b (c_20dec8a8, c_dc2643b7);

CREATE FUNCTION f_7d3cbe20() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        -- an event describes what ALREADY happened to its document in this transaction: its revision and resulting status must be the document's current ones (no forged or future event)
        IF NOT EXISTS (SELECT 1 FROM t_04d17dc0 d WHERE d.c_cceb8d4c = NEW.c_20dec8a8 AND d.c_d2cb57e3 = NEW.c_dc2643b7 AND d.c_8cb62046 = NEW.c_166ae51d) THEN
            RAISE EXCEPTION 'a commercial document event must carry the current revision and status of its document';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'commercial document events are append-only';
END;
$$;
CREATE TRIGGER r_00ab55a9 BEFORE INSERT OR UPDATE OR DELETE ON t_9ff18b1b FOR EACH ROW EXECUTE FUNCTION f_7d3cbe20();
CREATE TRIGGER r_74b04d9e BEFORE TRUNCATE ON t_9ff18b1b FOR EACH STATEMENT EXECUTE FUNCTION f_7d3cbe20();
REVOKE ALL ON TABLE t_9ff18b1b FROM PUBLIC;
