-- Migration 0017: reversal of executed stock documents (Phase 3.7C). Forward-only, no DROP, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.StockReversal.cs.
--
-- WHAT THIS IS: the durable, append-only record that an executed stock document (a StockPosting) was reversed: the compensating costing operation that took the stock effect back, what happened to the accounting side
-- (JournalAction: 0 = no journal existed, 1 = the journal had not been posted yet and was cancelled, 2 = the journal had been posted and a draft reversal entry was created that follows the ordinary approval and posting
-- workflow), the effective date, who, when and why. A posting is reversed at most once. Nothing is ever updated or deleted.

CREATE TABLE t_233a2574 (
    c_52d9badb uuid NOT NULL DEFAULT gen_random_uuid(),
    c_005e3448 uuid NOT NULL,
    c_60b20e96 uuid NOT NULL,
    c_c101e068 smallint NOT NULL,
    c_14b5b750 uuid NULL,
    c_487438cf date NOT NULL,
    c_66848f31 timestamptz NOT NULL,
    c_89094e1a uuid NOT NULL,
    c_8c797b62 varchar(500) NOT NULL,
    CONSTRAINT k_204ddd61 PRIMARY KEY (c_52d9badb),
    CONSTRAINT k_0fc305cb UNIQUE (c_005e3448),
    CONSTRAINT k_7d76d818 UNIQUE (c_60b20e96),
    CONSTRAINT k_7c20d7a1 FOREIGN KEY (c_005e3448) REFERENCES t_4d4b251f (c_fc71418e),
    CONSTRAINT k_4e9b1ca4 FOREIGN KEY (c_60b20e96) REFERENCES t_a648ca40 (c_f47f15da),
    CONSTRAINT k_50c2e5cd FOREIGN KEY (c_14b5b750) REFERENCES t_f3158869 (c_14f317c6),
    CONSTRAINT k_783eddc2 CHECK (c_c101e068 IN (0, 1, 2) AND ((c_c101e068 = 0) = (c_14b5b750 IS NULL)) AND length(btrim(c_8c797b62)) > 0)
);
CREATE FUNCTION f_1d38aef8() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock posting reversals are append-only';
END;
$$;
CREATE TRIGGER r_9fae11d0 BEFORE UPDATE OR DELETE ON t_233a2574 FOR EACH ROW EXECUTE FUNCTION f_1d38aef8();
CREATE TRIGGER r_bc1373c4 BEFORE TRUNCATE ON t_233a2574 FOR EACH STATEMENT EXECUTE FUNCTION f_1d38aef8();
REVOKE ALL ON TABLE t_233a2574 FROM PUBLIC;
