-- Migration 0013: stock movement documents (Phase 3.5). Forward-only, no DROP, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.StockDocuments.cs.
--
-- WHAT THIS IS: the document model and life cycle of stock receipts, issues and warehouse-to-warehouse transfers UP TO APPROVAL. It deliberately contains NO quantity-on-hand, NO movement ledger, NO cost layer and NO
-- accounting link: a physical stock effect must be committed atomically with its valuation (FIFO layers / moving weighted average per item and warehouse) and its accounting state, and those engines do not exist yet.
-- Nothing in these tables changes any stock figure; an approved document is only an approved intention that a later slice will post.
--
-- Invariants enforced by PostgreSQL (not only by the application):
--   * document type, branch, creation data and (once issued) the official number are immutable; documents are never deleted or truncated;
--   * the life cycle is a closed graph: 1 draft -> 2 submitted -> 3 approved; 2 -> 1 (withdrawn); 2 -> 4 rejected; 1, 2, 3 -> 5 cancelled; rejected and cancelled are final;
--   * the header (warehouses, date, reference, notes) and every line can change only while the document is a draft;
--   * a document that is no longer a draft has at least one line (checked at commit);
--   * the warehouse columns fit the type (receipt: target only; issue: source only; transfer: two different warehouses of the same branch, the document branch);
--   * separation of duties: the person who decides (approves or rejects) is never the person who submitted;
--   * the official number exists exactly while the document is approved (and is kept when an approved document is cancelled); line quantities are positive and base quantity = quantity x factor exactly;
--   * the event history is append-only and linked to exactly one authenticated business audit event.
-- doc type: 1 receipt, 2 issue, 3 transfer. status: 1 draft, 2 submitted, 3 approved, 4 rejected, 5 cancelled.

CREATE TABLE t_e92d29e5 (
    c_9a59d13e uuid NOT NULL DEFAULT gen_random_uuid(),
    c_bc592f66 smallint NOT NULL,
    c_3fc0f063 smallint NOT NULL DEFAULT 1,
    c_36399733 uuid NOT NULL,
    c_a2143b1c uuid NULL,
    c_fc0a402b uuid NULL,
    c_2580befc date NOT NULL,
    c_88247301 varchar(100) NULL,
    c_bb37c08a varchar(1000) NULL,
    c_308afbee varchar(60) NULL,
    c_3ce67397 uuid NULL,
    c_6b6eb86b uuid NOT NULL,
    c_fd289e25 timestamptz NOT NULL,
    c_afb8d9de uuid NULL,
    c_fb5a1444 timestamptz NULL,
    c_c7d41bd1 uuid NULL,
    c_0829bc7e timestamptz NULL,
    c_a88bb246 varchar(500) NULL,
    c_3816d744 uuid NULL,
    c_afd3ee0f timestamptz NULL,
    c_bd8e717d varchar(500) NULL,
    c_9ba641c8 bigint NOT NULL DEFAULT 1,
    c_18954c0b timestamptz NOT NULL,
    CONSTRAINT k_0fcd5508 PRIMARY KEY (c_9a59d13e),
    CONSTRAINT k_aaf84f1b FOREIGN KEY (c_36399733) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_ebf4a958 FOREIGN KEY (c_a2143b1c) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_ffff26f4 FOREIGN KEY (c_fc0a402b) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_a362f531 FOREIGN KEY (c_3ce67397) REFERENCES t_4b5bb222 (c_cae1c56a),
    CONSTRAINT k_4b4356af CHECK (c_bc592f66 IN (1, 2, 3) AND c_3fc0f063 IN (1, 2, 3, 4, 5)),
    CONSTRAINT k_16edf2e8 CHECK (
        (c_bc592f66 = 1 AND c_fc0a402b IS NOT NULL AND c_a2143b1c IS NULL)
        OR (c_bc592f66 = 2 AND c_a2143b1c IS NOT NULL AND c_fc0a402b IS NULL)
        OR (c_bc592f66 = 3 AND c_a2143b1c IS NOT NULL AND c_fc0a402b IS NOT NULL AND c_a2143b1c <> c_fc0a402b)),
    CONSTRAINT k_d7ed621d CHECK (
        (c_3fc0f063 = 3 AND c_308afbee IS NOT NULL AND c_3ce67397 IS NOT NULL AND c_c7d41bd1 IS NOT NULL)
        OR (c_3fc0f063 IN (1, 2, 4) AND c_308afbee IS NULL)
        OR c_3fc0f063 = 5),
    CONSTRAINT k_4bfa3063 CHECK (c_c7d41bd1 IS NULL OR c_afb8d9de IS NULL OR c_c7d41bd1 <> c_afb8d9de),
    CONSTRAINT k_07dfb9ae CHECK (c_9ba641c8 > 0)
);
CREATE UNIQUE INDEX k_8812f13c ON t_e92d29e5 (c_bc592f66, c_308afbee) WHERE c_308afbee IS NOT NULL;
CREATE INDEX k_8561e099 ON t_e92d29e5 (c_36399733);
CREATE INDEX k_ec56b670 ON t_e92d29e5 (c_a2143b1c);
CREATE INDEX k_aa884833 ON t_e92d29e5 (c_fc0a402b);
CREATE INDEX k_8a3610b6 ON t_e92d29e5 (c_3fc0f063, c_2580befc);

CREATE FUNCTION f_62c4b763() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ok boolean;
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'stock documents cannot be deleted'; END IF;
    IF NEW.c_9a59d13e IS DISTINCT FROM OLD.c_9a59d13e OR NEW.c_bc592f66 IS DISTINCT FROM OLD.c_bc592f66
       OR NEW.c_36399733 IS DISTINCT FROM OLD.c_36399733 OR NEW.c_6b6eb86b IS DISTINCT FROM OLD.c_6b6eb86b
       OR NEW.c_fd289e25 IS DISTINCT FROM OLD.c_fd289e25 THEN
        RAISE EXCEPTION 'the type, branch and creation data of a stock document are immutable';
    END IF;
    IF NEW.c_9ba641c8 <> OLD.c_9ba641c8 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF OLD.c_308afbee IS NOT NULL AND NEW.c_308afbee IS DISTINCT FROM OLD.c_308afbee THEN RAISE EXCEPTION 'an issued document number never changes'; END IF;
    IF NEW.c_3fc0f063 IS DISTINCT FROM OLD.c_3fc0f063 THEN
        ok := (OLD.c_3fc0f063 = 1 AND NEW.c_3fc0f063 IN (2, 5))
           OR (OLD.c_3fc0f063 = 2 AND NEW.c_3fc0f063 IN (1, 3, 4, 5))
           OR (OLD.c_3fc0f063 = 3 AND NEW.c_3fc0f063 = 5);
        IF NOT ok THEN RAISE EXCEPTION 'this stock document status change is not allowed'; END IF;
    ELSIF OLD.c_3fc0f063 <> 1 THEN
        RAISE EXCEPTION 'a stock document that is not a draft cannot be edited';
    END IF;
    IF OLD.c_3fc0f063 = 1 AND NEW.c_3fc0f063 = 1 THEN
        -- editing a draft: the decision and cancellation columns stay empty
        IF NEW.c_c7d41bd1 IS NOT NULL OR NEW.c_3816d744 IS NOT NULL THEN RAISE EXCEPTION 'a draft has no decision'; END IF;
    ELSIF OLD.c_3fc0f063 <> 1 THEN
        IF NEW.c_a2143b1c IS DISTINCT FROM OLD.c_a2143b1c OR NEW.c_fc0a402b IS DISTINCT FROM OLD.c_fc0a402b
           OR NEW.c_2580befc IS DISTINCT FROM OLD.c_2580befc OR NEW.c_88247301 IS DISTINCT FROM OLD.c_88247301
           OR NEW.c_bb37c08a IS DISTINCT FROM OLD.c_bb37c08a THEN
            RAISE EXCEPTION 'the header of a stock document that is not a draft cannot change';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_5a3d0a7d BEFORE UPDATE OR DELETE ON t_e92d29e5 FOR EACH ROW EXECUTE FUNCTION f_62c4b763();
CREATE TRIGGER r_9db8154d BEFORE TRUNCATE ON t_e92d29e5 FOR EACH STATEMENT EXECUTE FUNCTION f_62c4b763();
REVOKE ALL ON TABLE t_e92d29e5 FROM PUBLIC;

CREATE TABLE t_53dfc3d8 (
    c_15325713 uuid NOT NULL,
    c_f7be759d integer NOT NULL,
    c_2ca8e448 uuid NOT NULL,
    c_038c5606 uuid NOT NULL,
    c_65dbf88c numeric(28,10) NOT NULL,
    c_cbc761e0 numeric(28,10) NOT NULL,
    c_010de8d8 numeric(28,10) NOT NULL,
    c_90c87357 varchar(200) NULL,
    CONSTRAINT k_bb95eac0 PRIMARY KEY (c_15325713, c_f7be759d),
    CONSTRAINT k_1aed6392 FOREIGN KEY (c_15325713) REFERENCES t_e92d29e5 (c_9a59d13e),
    CONSTRAINT k_9dfee29e FOREIGN KEY (c_2ca8e448) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_26d9b156 FOREIGN KEY (c_038c5606) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_2460e30c CHECK (c_f7be759d > 0 AND c_65dbf88c > 0 AND c_cbc761e0 > 0
        AND c_010de8d8 = c_65dbf88c * c_cbc761e0)
);
CREATE INDEX k_327b54c8 ON t_53dfc3d8 (c_2ca8e448);
-- Lines can be added, changed or removed only while their document is a draft.
CREATE FUNCTION f_9f62c5ce() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    doc uuid;
    st smallint;
BEGIN
    IF TG_LEVEL = 'STATEMENT' THEN RAISE EXCEPTION 'stock document lines cannot be removed in bulk'; END IF;
    IF TG_OP = 'DELETE' THEN doc := OLD.c_15325713; ELSE doc := NEW.c_15325713; END IF;
    SELECT c_3fc0f063 INTO st FROM t_e92d29e5 WHERE c_9a59d13e = doc;
    IF st IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'the lines of a stock document that is not a draft cannot change'; END IF;
    IF TG_OP = 'UPDATE' AND NEW.c_15325713 IS DISTINCT FROM OLD.c_15325713 THEN RAISE EXCEPTION 'a line cannot move to another document'; END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d9b5ae38 BEFORE INSERT OR UPDATE OR DELETE ON t_53dfc3d8 FOR EACH ROW EXECUTE FUNCTION f_9f62c5ce();
CREATE TRIGGER r_6ed8a105 BEFORE TRUNCATE ON t_53dfc3d8 FOR EACH STATEMENT EXECUTE FUNCTION f_9f62c5ce();
REVOKE ALL ON TABLE t_53dfc3d8 FROM PUBLIC;

-- A document that leaves the draft state has at least one line (judged at commit, after the lines of the same transaction exist).
CREATE FUNCTION f_ec93103d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_3fc0f063 IN (2, 3) AND NOT EXISTS (SELECT 1 FROM t_53dfc3d8 WHERE c_15325713 = NEW.c_9a59d13e) THEN
        RAISE EXCEPTION 'a submitted or approved stock document needs at least one line';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_54fc93c3 AFTER INSERT OR UPDATE ON t_e92d29e5 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_ec93103d();

-- Life-cycle history (append-only): one row per transition, linked to exactly one authenticated business audit event written in the SAME transaction. kind: 1 created, 2 updated, 3 submitted, 4 withdrawn, 5 approved, 6 rejected, 7 cancelled.
CREATE TABLE t_b9231a6b (
    c_e01af6ca bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_fd72fc35 uuid NOT NULL,
    c_2fe7652c smallint NOT NULL,
    c_49806027 smallint NULL,
    c_a54fce39 smallint NOT NULL,
    c_2a6bae0c uuid NOT NULL,
    c_29c24808 timestamptz NOT NULL,
    c_268f7f43 varchar(500) NULL,
    c_fd2e47ac varchar(48) NOT NULL,
    c_4684b80a bigint NOT NULL,
    CONSTRAINT k_cf23fc0d PRIMARY KEY (c_e01af6ca),
    CONSTRAINT k_1af8575f FOREIGN KEY (c_fd72fc35) REFERENCES t_e92d29e5 (c_9a59d13e),
    CONSTRAINT k_eb56ac03 FOREIGN KEY (c_fd2e47ac, c_4684b80a) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_ed8d2288 UNIQUE (c_fd2e47ac, c_4684b80a),
    CONSTRAINT k_851f3da2 CHECK (c_2fe7652c BETWEEN 1 AND 7 AND c_a54fce39 BETWEEN 1 AND 5)
);
CREATE INDEX k_9b16ac44 ON t_b9231a6b (c_fd72fc35, c_e01af6ca);
CREATE FUNCTION f_08e6696d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stock document events are append-only';
END;
$$;
CREATE TRIGGER r_1f81a29f BEFORE UPDATE OR DELETE ON t_b9231a6b FOR EACH ROW EXECUTE FUNCTION f_08e6696d();
CREATE TRIGGER r_cae3df7d BEFORE TRUNCATE ON t_b9231a6b FOR EACH STATEMENT EXECUTE FUNCTION f_08e6696d();
REVOKE ALL ON TABLE t_b9231a6b FROM PUBLIC;
