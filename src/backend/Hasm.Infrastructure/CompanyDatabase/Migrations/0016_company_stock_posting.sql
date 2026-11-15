-- Migration 0016: atomic stock posting records (Phase 3.7B). Forward-only, no DROP, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.StockPosting.cs.
--
-- WHAT THIS IS: the durable, append-only trace of the posting (execution) of an APPROVED stock document: which costing operation moved the stock and its value, which accounting configuration version
-- was used, which journal entry carries the accounting side, and the explicit accounting state history. The document itself is never changed (its status stays approved): executed means the existence of
-- exactly one posting row, so the physical movement and the financial journal status are two separate, explicit facts (SRS 7.4, 19.7; ARCHITECTURE 7). Receipt cost amounts entered at posting time are kept per line.
-- Nothing here is ever updated or deleted; a later reversal or accounting-state change appends an event instead.

CREATE TABLE t_4d4b251f (
    c_fc71418e uuid NOT NULL DEFAULT gen_random_uuid(),
    c_eb889fd0 uuid NOT NULL,
    c_78f9abc2 smallint NOT NULL,
    c_02d7d540 date NOT NULL,
    c_b88be209 timestamptz NOT NULL,
    c_793ebee9 uuid NOT NULL,
    c_4ce1587f uuid NOT NULL,
    c_fa7b9500 uuid NULL,
    c_d667f7a4 uuid NULL,
    c_6fb26986 numeric(28,6) NOT NULL,
    CONSTRAINT k_2a9cf3b5 PRIMARY KEY (c_fc71418e),
    CONSTRAINT k_b529fec3 UNIQUE (c_eb889fd0),
    CONSTRAINT k_cc1974ee UNIQUE (c_4ce1587f),
    CONSTRAINT k_4f60a33d FOREIGN KEY (c_eb889fd0) REFERENCES t_e92d29e5 (c_9a59d13e),
    CONSTRAINT k_e40048b5 FOREIGN KEY (c_4ce1587f) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_fc4061bb FOREIGN KEY (c_fa7b9500) REFERENCES t_a78f5224 (c_3bffe448),
    CONSTRAINT k_f2a632a1 FOREIGN KEY (c_d667f7a4) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_a64a7947 CHECK (c_78f9abc2 IN (1, 2, 3) AND c_6fb26986 >= 0
        AND ((c_d667f7a4 IS NULL) OR (c_fa7b9500 IS NOT NULL)))
);
CREATE INDEX k_999c64c1 ON t_4d4b251f (c_d667f7a4);
CREATE FUNCTION f_ffd78df3() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock postings are append-only';
END;
$$;
CREATE TRIGGER r_53a924b0 BEFORE UPDATE OR DELETE ON t_4d4b251f FOR EACH ROW EXECUTE FUNCTION f_ffd78df3();
CREATE TRIGGER r_3763de91 BEFORE TRUNCATE ON t_4d4b251f FOR EACH STATEMENT EXECUTE FUNCTION f_ffd78df3();
REVOKE ALL ON TABLE t_4d4b251f FROM PUBLIC;

CREATE TABLE t_7eb4c679 (
    c_ecf9865c uuid NOT NULL,
    c_8c33b614 integer NOT NULL,
    c_7a455438 uuid NOT NULL,
    c_1f1c9cdf uuid NOT NULL,
    c_b691de84 numeric(28,10) NOT NULL,
    c_ea5e66bd numeric(28,6) NOT NULL,
    CONSTRAINT k_28b222ec PRIMARY KEY (c_ecf9865c, c_8c33b614),
    CONSTRAINT k_ec4d973c FOREIGN KEY (c_ecf9865c) REFERENCES t_4d4b251f (c_fc71418e),
    CONSTRAINT k_f4fb4d0c FOREIGN KEY (c_7a455438) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_bda7550f FOREIGN KEY (c_1f1c9cdf) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_13876eb4 CHECK (c_8c33b614 > 0 AND c_b691de84 > 0 AND c_ea5e66bd >= 0)
);
CREATE FUNCTION f_a5d2d207() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock posting lines are append-only';
END;
$$;
CREATE TRIGGER r_de410774 BEFORE UPDATE OR DELETE ON t_7eb4c679 FOR EACH ROW EXECUTE FUNCTION f_a5d2d207();
CREATE TRIGGER r_f0b3acfd BEFORE TRUNCATE ON t_7eb4c679 FOR EACH STATEMENT EXECUTE FUNCTION f_a5d2d207();
REVOKE ALL ON TABLE t_7eb4c679 FROM PUBLIC;

-- Accounting state history (append-only). Kind: 1 = recorded with a draft journal awaiting its normal approval and posting (accounting pending); 2 = recorded and no journal is required (zero value or a transfer inside one inventory account).
-- Later phases append further kinds (journal posted, journal rejected, reversed); the CURRENT state is the latest event of the posting.
CREATE TABLE t_906dd3bf (
    c_e164dc28 bigint GENERATED ALWAYS AS IDENTITY,
    c_eb6f593b uuid NOT NULL,
    c_8ed3a437 smallint NOT NULL,
    c_ce401ba2 uuid NULL,
    c_83d19b52 timestamptz NOT NULL,
    c_c16e977b uuid NOT NULL,
    c_70218d90 varchar(500) NULL,
    CONSTRAINT k_4a246e07 PRIMARY KEY (c_e164dc28),
    CONSTRAINT k_dd32cefa FOREIGN KEY (c_eb6f593b) REFERENCES t_4d4b251f (c_fc71418e),
    CONSTRAINT k_ab2dd267 FOREIGN KEY (c_ce401ba2) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_0e605b26 CHECK (c_8ed3a437 BETWEEN 1 AND 20)
);
CREATE INDEX k_cf27714e ON t_906dd3bf (c_eb6f593b, c_e164dc28);
CREATE FUNCTION f_c9a6ad54() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock posting events are append-only';
END;
$$;
CREATE TRIGGER r_a3ef992d BEFORE UPDATE OR DELETE ON t_906dd3bf FOR EACH ROW EXECUTE FUNCTION f_c9a6ad54();
CREATE TRIGGER r_5c54812f BEFORE TRUNCATE ON t_906dd3bf FOR EACH STATEMENT EXECUTE FUNCTION f_c9a6ad54();
REVOKE ALL ON TABLE t_906dd3bf FROM PUBLIC;

-- An executed document can never be cancelled: its movement is undone only by a reversal (a later phase), never by cancelling the document. Enforced here so no code path can bypass it.
CREATE FUNCTION f_9ca15a1d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_4d4b251f WHERE c_eb889fd0 = NEW.c_9a59d13e) THEN
        RAISE EXCEPTION 'an executed stock document cannot be cancelled';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_45999b93 BEFORE UPDATE ON t_e92d29e5 FOR EACH ROW WHEN (NEW.c_3fc0f063 = 5 AND OLD.c_3fc0f063 <> 5) EXECUTE FUNCTION f_9ca15a1d();
