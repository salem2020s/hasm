-- Migration 0021: company-local party records and the request log (outbox) of the party registry (Phase 3.9A). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.Parties.cs.
--
-- WHAT THIS IS (SRS 3.3, 3.4, 17.4): the LocalPartyId of a business party in THIS company (company-local code, the company's own display name, notes, active flag) and its link to the shared
-- identity (GlobalPartyId) of the system database, plus the reliable request log for the cross-database requests (no distributed transaction exists): every request is written here first, in the
-- same transaction as the local change and its audit event; its id is the idempotency key the system database uses, so a retry never creates a duplicate.
-- No balance, account, term or limit exists here yet (customers and suppliers are Phase 3.9B).
-- Invariants enforced by PostgreSQL:
--   * local parties and requests are never removed; a local party is created pending; the GlobalPartyId of a local party, once set, never changes; link state 2 (linked) exactly when a GlobalPartyId is set;
--   * link state transitions: 1 pending -> 2 linked or 3 rejected; 3 rejected -> 1 pending (a new proposal); linked is final;
--   * a request's kind, target and proposed values never change; a completed request is final; at most one open (pending or delivered) request per local party.
-- link state: 1 pending central decision, 2 linked, 3 proposal rejected. request kind: 1 new shared identity, 2 change of the shared data of a linked party.
-- request status: 1 pending delivery, 2 delivered, 3 completed. outcome: 1 approved, 2 rejected, 3 refused by the registry.

CREATE TABLE t_8edce6b0 (
    c_3f494703 uuid NOT NULL,
    c_dd3886dd varchar(30) NOT NULL,
    c_9aa7c8f8 varchar(200) NOT NULL,
    c_feceaad5 varchar(1000) NULL,
    c_85cb3b72 boolean NOT NULL DEFAULT true,
    c_6b6f8108 smallint NOT NULL DEFAULT 1,
    c_2d9bc856 uuid NULL,
    c_733f0b71 bigint NOT NULL DEFAULT 1,
    c_80d142cf uuid NOT NULL,
    c_4316c3c1 timestamptz NOT NULL,
    c_d28d5811 timestamptz NOT NULL,
    CONSTRAINT k_52285519 PRIMARY KEY (c_3f494703),
    CONSTRAINT k_2abae7c6 CHECK (c_6b6f8108 IN (1, 2, 3) AND (c_6b6f8108 = 2) = (c_2d9bc856 IS NOT NULL) AND c_733f0b71 > 0
        AND c_dd3886dd ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$' AND char_length(btrim(c_9aa7c8f8)) >= 1)
);
CREATE UNIQUE INDEX k_d9cc7405 ON t_8edce6b0 (c_dd3886dd);
CREATE UNIQUE INDEX k_76724b46 ON t_8edce6b0 (c_2d9bc856) WHERE c_2d9bc856 IS NOT NULL;

CREATE FUNCTION f_4af6e893() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_6b6f8108 <> 1 OR NEW.c_733f0b71 <> 1 THEN RAISE EXCEPTION 'a local party is created pending, at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'local parties are never removed'; END IF;
    IF NEW.c_3f494703 IS DISTINCT FROM OLD.c_3f494703 OR NEW.c_80d142cf IS DISTINCT FROM OLD.c_80d142cf OR NEW.c_4316c3c1 IS DISTINCT FROM OLD.c_4316c3c1 THEN
        RAISE EXCEPTION 'the identity and creation data of a local party are immutable';
    END IF;
    IF OLD.c_2d9bc856 IS NOT NULL AND NEW.c_2d9bc856 IS DISTINCT FROM OLD.c_2d9bc856 THEN RAISE EXCEPTION 'the shared identity of a linked local party never changes'; END IF;
    IF NEW.c_6b6f8108 IS DISTINCT FROM OLD.c_6b6f8108
       AND NOT ((OLD.c_6b6f8108 = 1 AND NEW.c_6b6f8108 IN (2, 3)) OR (OLD.c_6b6f8108 = 3 AND NEW.c_6b6f8108 = 1)) THEN
        RAISE EXCEPTION 'this link state change of a local party is not allowed';
    END IF;
    IF NEW.c_733f0b71 <> OLD.c_733f0b71 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_3ff1c4c8 BEFORE INSERT OR UPDATE OR DELETE ON t_8edce6b0 FOR EACH ROW EXECUTE FUNCTION f_4af6e893();
CREATE TRIGGER r_4631adb3 BEFORE TRUNCATE ON t_8edce6b0 FOR EACH STATEMENT EXECUTE FUNCTION f_4af6e893();
REVOKE ALL ON TABLE t_8edce6b0 FROM PUBLIC;

CREATE TABLE t_ba81b291 (
    c_32333a7b uuid NOT NULL,
    c_66c55e86 uuid NOT NULL,
    c_0c8f01f0 smallint NOT NULL,
    c_a49afa8d uuid NULL,
    c_6ce5dcfb smallint NOT NULL,
    c_51c1ef2f varchar(200) NOT NULL,
    c_efa6fc18 varchar(200) NULL,
    c_5f57af39 varchar(50) NULL,
    c_b52f0b6e varchar(50) NULL,
    c_0e95693d smallint NOT NULL DEFAULT 1,
    c_991403a6 varchar(500) NULL,
    c_ec870880 smallint NOT NULL DEFAULT 1,
    c_7a8c0700 smallint NULL,
    c_215a5e58 uuid NULL,
    c_a7b2180c integer NOT NULL DEFAULT 0,
    c_6f84193e timestamptz NULL,
    c_9aeea05e varchar(64) NULL,
    c_284d429a uuid NOT NULL,
    c_36f6903f timestamptz NOT NULL,
    c_8c242e76 timestamptz NULL,
    c_933eb4ea timestamptz NULL,
    c_b6864ce1 bigint NOT NULL DEFAULT 1,
    CONSTRAINT k_1fc60f73 PRIMARY KEY (c_32333a7b),
    CONSTRAINT k_3782fac2 FOREIGN KEY (c_66c55e86) REFERENCES t_8edce6b0 (c_3f494703),
    CONSTRAINT k_dfff7486 CHECK (c_0c8f01f0 IN (1, 2) AND c_6ce5dcfb IN (1, 2) AND c_0e95693d IN (1, 2) AND c_ec870880 IN (1, 2, 3) AND c_a7b2180c >= 0 AND c_b6864ce1 > 0
        AND ((c_0c8f01f0 = 1 AND c_a49afa8d IS NULL AND c_0e95693d = 1) OR (c_0c8f01f0 = 2 AND c_a49afa8d IS NOT NULL))
        AND char_length(btrim(c_51c1ef2f)) >= 1 AND (c_efa6fc18 IS NULL OR char_length(btrim(c_efa6fc18)) >= 1)
        AND (c_5f57af39 IS NULL OR c_5f57af39 ~ '^[A-Z0-9./-]{1,50}$')
        AND (c_b52f0b6e IS NULL OR c_b52f0b6e ~ '^[A-Z0-9./-]{1,50}$')
        AND (c_ec870880 = 1 OR c_8c242e76 IS NOT NULL OR c_7a8c0700 = 3)
        AND ((c_ec870880 = 3) = (c_7a8c0700 IS NOT NULL AND c_933eb4ea IS NOT NULL))
        AND (c_7a8c0700 IS NULL OR c_7a8c0700 IN (1, 2, 3))
        AND ((c_215a5e58 IS NOT NULL) = (c_7a8c0700 IS NOT DISTINCT FROM 1))
        AND (c_9aeea05e IS NULL OR c_9aeea05e ~ '^[a-z0-9_]{1,64}$'))
);
-- At most one open request (pending or delivered) per local party.
CREATE UNIQUE INDEX k_468c1d6e ON t_ba81b291 (c_66c55e86) WHERE c_ec870880 IN (1, 2);
CREATE INDEX k_2c342abc ON t_ba81b291 (c_66c55e86, c_36f6903f);
CREATE INDEX k_2ca207bc ON t_ba81b291 (c_ec870880);

CREATE FUNCTION f_50f3a8a1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_ec870880 <> 1 OR NEW.c_a7b2180c <> 0 OR NEW.c_b6864ce1 <> 1 THEN RAISE EXCEPTION 'a request is created pending, without attempts, at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'party requests are never removed'; END IF;
    IF OLD.c_ec870880 = 3 THEN RAISE EXCEPTION 'a completed party request is final'; END IF;
    IF NEW.c_ec870880 < OLD.c_ec870880 THEN RAISE EXCEPTION 'a party request never goes back'; END IF;
    IF NEW.c_32333a7b IS DISTINCT FROM OLD.c_32333a7b OR NEW.c_66c55e86 IS DISTINCT FROM OLD.c_66c55e86 OR NEW.c_0c8f01f0 IS DISTINCT FROM OLD.c_0c8f01f0
       OR NEW.c_a49afa8d IS DISTINCT FROM OLD.c_a49afa8d OR NEW.c_6ce5dcfb IS DISTINCT FROM OLD.c_6ce5dcfb
       OR NEW.c_51c1ef2f IS DISTINCT FROM OLD.c_51c1ef2f OR NEW.c_efa6fc18 IS DISTINCT FROM OLD.c_efa6fc18 OR NEW.c_5f57af39 IS DISTINCT FROM OLD.c_5f57af39
       OR NEW.c_b52f0b6e IS DISTINCT FROM OLD.c_b52f0b6e OR NEW.c_0e95693d IS DISTINCT FROM OLD.c_0e95693d OR NEW.c_991403a6 IS DISTINCT FROM OLD.c_991403a6
       OR NEW.c_284d429a IS DISTINCT FROM OLD.c_284d429a OR NEW.c_36f6903f IS DISTINCT FROM OLD.c_36f6903f THEN
        RAISE EXCEPTION 'the content of a party request is immutable';
    END IF;
    IF NEW.c_a7b2180c < OLD.c_a7b2180c THEN RAISE EXCEPTION 'attempts never decrease'; END IF;
    IF NEW.c_b6864ce1 <> OLD.c_b6864ce1 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_1a458c19 BEFORE INSERT OR UPDATE OR DELETE ON t_ba81b291 FOR EACH ROW EXECUTE FUNCTION f_50f3a8a1();
CREATE TRIGGER r_9b2d5a99 BEFORE TRUNCATE ON t_ba81b291 FOR EACH STATEMENT EXECUTE FUNCTION f_50f3a8a1();
REVOKE ALL ON TABLE t_ba81b291 FROM PUBLIC;
