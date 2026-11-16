-- Migration 0018: stocktake (physical count) sheets and their variance (Phase 3.8B-1). Forward-only, additive, no DROP, nothing seeded, no existing data read or modified, no existing constraint changed.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.Stocktake.cs.
--
-- WHAT THIS IS: a stocktake sheet of ONE warehouse: per item the counted quantity (in any unit of the item, converted exactly into the base unit), the BOOK quantity of the costing ledger in the base unit as of the count
-- date (an informational snapshot, retaken at every draft save and at submit, frozen from submit on) and the variance = counted base quantity - book quantity. Draft -> submitted -> approved like stock documents.
-- It deliberately creates NO stock movement, cost operation or accounting entry: an approved stocktake is only an approved count. The adjustment that books the variance is a later slice (3.8B-2), which uses the
-- CURRENT book quantity at that time, not this snapshot.
--
-- Invariants enforced by PostgreSQL (not only by the application):
--   * branch, warehouse and creation data and (once issued) the official number are immutable; stocktakes are never deleted or truncated;
--   * the life cycle is a closed graph: 1 draft -> 2 submitted -> 3 approved; 2 -> 1 (withdrawn); 2 -> 4 rejected; 1, 2, 3 -> 5 cancelled; rejected and cancelled are final;
--   * the header (count date, reference, notes, snapshot time) and every line can change only while the stocktake is a draft;
--   * a stocktake that is submitted or approved has at least one line, every line is counted and every line with a variance has a reason (checked at commit);
--   * separation of duties: the person who decides (approves or rejects) is never the person who submitted;
--   * the official number exists exactly while approved (kept on cancellation) and is unique; an item appears at most once per stocktake;
--   * counted quantity >= 0, counted base quantity = counted quantity x factor exactly, variance = counted base quantity - book quantity exactly;
--   * the event history is append-only and linked to exactly one authenticated business audit event.
-- status: 1 draft, 2 submitted, 3 approved, 4 rejected, 5 cancelled.

CREATE TABLE t_994f8458 (
    c_2ac20938 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_941ef8c3 smallint NOT NULL DEFAULT 1,
    c_b156e8dd uuid NOT NULL,
    c_a28d1691 uuid NOT NULL,
    c_57c0972a date NOT NULL,
    c_04707d57 varchar(100) NULL,
    c_8fcd8e81 varchar(1000) NULL,
    c_96d45bf1 varchar(60) NULL,
    c_e01c5942 uuid NULL,
    c_0554fa6b timestamptz NOT NULL,
    c_943afda5 uuid NOT NULL,
    c_2df1272f timestamptz NOT NULL,
    c_9173aa18 uuid NULL,
    c_1626ea46 timestamptz NULL,
    c_e076b18d uuid NULL,
    c_82d1fb4d timestamptz NULL,
    c_585cd4e6 varchar(500) NULL,
    c_3f2a88ab uuid NULL,
    c_fea3e2d5 timestamptz NULL,
    c_f2d1acab varchar(500) NULL,
    c_94725861 bigint NOT NULL DEFAULT 1,
    c_2990788a timestamptz NOT NULL,
    CONSTRAINT k_1f36f5ef PRIMARY KEY (c_2ac20938),
    CONSTRAINT k_49e1294e FOREIGN KEY (c_b156e8dd) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_7be8ca9b FOREIGN KEY (c_a28d1691) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_cccad747 FOREIGN KEY (c_e01c5942) REFERENCES t_4b5bb222 (c_cae1c56a),
    CONSTRAINT k_7504d581 CHECK (c_941ef8c3 IN (1, 2, 3, 4, 5)),
    CONSTRAINT k_fa085040 CHECK (
        (c_941ef8c3 = 3 AND c_96d45bf1 IS NOT NULL AND c_e01c5942 IS NOT NULL AND c_e076b18d IS NOT NULL)
        OR (c_941ef8c3 IN (1, 2, 4) AND c_96d45bf1 IS NULL)
        OR c_941ef8c3 = 5),
    CONSTRAINT k_cc957678 CHECK (c_e076b18d IS NULL OR c_9173aa18 IS NULL OR c_e076b18d <> c_9173aa18),
    CONSTRAINT k_524d3ac7 CHECK (c_94725861 > 0)
);
CREATE UNIQUE INDEX k_3325d7d5 ON t_994f8458 (c_96d45bf1) WHERE c_96d45bf1 IS NOT NULL;
CREATE INDEX k_6201ed40 ON t_994f8458 (c_a28d1691);
CREATE INDEX k_acaa9997 ON t_994f8458 (c_941ef8c3, c_57c0972a);

CREATE FUNCTION f_60401953() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ok boolean;
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'stocktakes cannot be deleted'; END IF;
    IF NEW.c_2ac20938 IS DISTINCT FROM OLD.c_2ac20938 OR NEW.c_b156e8dd IS DISTINCT FROM OLD.c_b156e8dd OR NEW.c_a28d1691 IS DISTINCT FROM OLD.c_a28d1691
       OR NEW.c_943afda5 IS DISTINCT FROM OLD.c_943afda5 OR NEW.c_2df1272f IS DISTINCT FROM OLD.c_2df1272f THEN
        RAISE EXCEPTION 'the branch, warehouse and creation data of a stocktake are immutable';
    END IF;
    IF NEW.c_94725861 <> OLD.c_94725861 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF OLD.c_96d45bf1 IS NOT NULL AND NEW.c_96d45bf1 IS DISTINCT FROM OLD.c_96d45bf1 THEN RAISE EXCEPTION 'an issued stocktake number never changes'; END IF;
    IF NEW.c_941ef8c3 IS DISTINCT FROM OLD.c_941ef8c3 THEN
        ok := (OLD.c_941ef8c3 = 1 AND NEW.c_941ef8c3 IN (2, 5))
           OR (OLD.c_941ef8c3 = 2 AND NEW.c_941ef8c3 IN (1, 3, 4, 5))
           OR (OLD.c_941ef8c3 = 3 AND NEW.c_941ef8c3 = 5);
        IF NOT ok THEN RAISE EXCEPTION 'this stocktake status change is not allowed'; END IF;
    ELSIF OLD.c_941ef8c3 <> 1 THEN
        RAISE EXCEPTION 'a stocktake that is not a draft cannot be edited';
    END IF;
    IF OLD.c_941ef8c3 = 1 AND NEW.c_941ef8c3 = 1 THEN
        IF NEW.c_e076b18d IS NOT NULL OR NEW.c_3f2a88ab IS NOT NULL THEN RAISE EXCEPTION 'a draft has no decision'; END IF;
    ELSIF OLD.c_941ef8c3 <> 1 THEN
        IF NEW.c_57c0972a IS DISTINCT FROM OLD.c_57c0972a OR NEW.c_04707d57 IS DISTINCT FROM OLD.c_04707d57
           OR NEW.c_8fcd8e81 IS DISTINCT FROM OLD.c_8fcd8e81 OR NEW.c_0554fa6b IS DISTINCT FROM OLD.c_0554fa6b THEN
            RAISE EXCEPTION 'the header and snapshot of a stocktake that is not a draft cannot change';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_153d656d BEFORE UPDATE OR DELETE ON t_994f8458 FOR EACH ROW EXECUTE FUNCTION f_60401953();
CREATE TRIGGER r_05f93af4 BEFORE TRUNCATE ON t_994f8458 FOR EACH STATEMENT EXECUTE FUNCTION f_60401953();
REVOKE ALL ON TABLE t_994f8458 FROM PUBLIC;

-- Lines: counted quantity (NULL while not yet counted) in the entered unit, its exact base quantity, the book quantity (base unit) of the snapshot and the variance.
CREATE TABLE t_d1e0ca90 (
    c_f16a23dd uuid NOT NULL,
    c_1cb4b07f integer NOT NULL,
    c_0ee93266 uuid NOT NULL,
    c_db9cde60 uuid NOT NULL,
    c_81e5a821 numeric(28,10) NULL,
    c_8c833228 numeric(28,10) NOT NULL,
    c_48580491 numeric(28,10) NULL,
    c_dd0b869e numeric(28,10) NOT NULL,
    c_368e12dc numeric(28,10) NULL,
    c_25495cbf varchar(200) NULL,
    CONSTRAINT k_dd566b79 PRIMARY KEY (c_f16a23dd, c_1cb4b07f),
    CONSTRAINT k_65bb9df3 FOREIGN KEY (c_f16a23dd) REFERENCES t_994f8458 (c_2ac20938),
    CONSTRAINT k_6155e7ea FOREIGN KEY (c_0ee93266) REFERENCES t_2f2a7184 (c_be51f22e),
    CONSTRAINT k_a0ad6175 FOREIGN KEY (c_db9cde60) REFERENCES t_aa91e313 (c_1a9eb66f),
    CONSTRAINT k_ce971b1c UNIQUE (c_f16a23dd, c_0ee93266),
    CONSTRAINT k_5ac711e2 CHECK (c_1cb4b07f > 0 AND c_8c833228 > 0
        AND ((c_81e5a821 IS NULL AND c_48580491 IS NULL AND c_368e12dc IS NULL)
             OR (c_81e5a821 >= 0 AND c_48580491 IS NOT NULL AND c_368e12dc IS NOT NULL AND c_48580491 = c_81e5a821 * c_8c833228 AND c_368e12dc = c_48580491 - c_dd0b869e))
        AND (c_25495cbf IS NULL OR length(btrim(c_25495cbf)) > 0))
);
CREATE INDEX k_c5290328 ON t_d1e0ca90 (c_0ee93266);
-- Lines can be added, changed or removed only while their stocktake is a draft.
CREATE FUNCTION f_cb0cde13() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    doc uuid;
    st smallint;
BEGIN
    IF TG_LEVEL = 'STATEMENT' THEN RAISE EXCEPTION 'stocktake lines cannot be removed in bulk'; END IF;
    IF TG_OP = 'DELETE' THEN doc := OLD.c_f16a23dd; ELSE doc := NEW.c_f16a23dd; END IF;
    SELECT c_941ef8c3 INTO st FROM t_994f8458 WHERE c_2ac20938 = doc;
    IF st IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'the lines of a stocktake that is not a draft cannot change'; END IF;
    IF TG_OP = 'UPDATE' AND NEW.c_f16a23dd IS DISTINCT FROM OLD.c_f16a23dd THEN RAISE EXCEPTION 'a line cannot move to another stocktake'; END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_b11caaa0 BEFORE INSERT OR UPDATE OR DELETE ON t_d1e0ca90 FOR EACH ROW EXECUTE FUNCTION f_cb0cde13();
CREATE TRIGGER r_a6de3584 BEFORE TRUNCATE ON t_d1e0ca90 FOR EACH STATEMENT EXECUTE FUNCTION f_cb0cde13();
REVOKE ALL ON TABLE t_d1e0ca90 FROM PUBLIC;

-- A stocktake that leaves the draft state (submitted or approved) is complete: at least one line, every line counted, every variance explained (judged at commit, after the lines of the same transaction exist).
CREATE FUNCTION f_f2c2efbb() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_941ef8c3 IN (2, 3) THEN
        IF NOT EXISTS (SELECT 1 FROM t_d1e0ca90 WHERE c_f16a23dd = NEW.c_2ac20938) THEN
            RAISE EXCEPTION 'a submitted or approved stocktake needs at least one line';
        END IF;
        IF EXISTS (SELECT 1 FROM t_d1e0ca90 WHERE c_f16a23dd = NEW.c_2ac20938 AND (c_81e5a821 IS NULL OR (c_368e12dc <> 0 AND c_25495cbf IS NULL))) THEN
            RAISE EXCEPTION 'every line of a submitted or approved stocktake is counted and every variance has a reason';
        END IF;
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_36837069 AFTER INSERT OR UPDATE ON t_994f8458 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_f2c2efbb();

-- Life-cycle history (append-only): one row per transition, linked to exactly one authenticated business audit event written in the SAME transaction.
-- kind: 1 created, 2 updated, 3 submitted, 4 withdrawn, 5 approved, 6 rejected, 7 cancelled, 8 sheet generated.
CREATE TABLE t_8176e273 (
    c_8c10458e bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_4cd1da85 uuid NOT NULL,
    c_a4d2bd13 smallint NOT NULL,
    c_39cecf36 smallint NULL,
    c_47e8cb8b smallint NOT NULL,
    c_c8706afa uuid NOT NULL,
    c_c879da72 timestamptz NOT NULL,
    c_6f8397d2 varchar(500) NULL,
    c_fdb4b98c varchar(48) NOT NULL,
    c_bdaaec06 bigint NOT NULL,
    CONSTRAINT k_dabcb4b1 PRIMARY KEY (c_8c10458e),
    CONSTRAINT k_28297939 FOREIGN KEY (c_4cd1da85) REFERENCES t_994f8458 (c_2ac20938),
    CONSTRAINT k_8b4a36f2 FOREIGN KEY (c_fdb4b98c, c_bdaaec06) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_b214e2d8 UNIQUE (c_fdb4b98c, c_bdaaec06),
    CONSTRAINT k_101d5d5c CHECK (c_a4d2bd13 BETWEEN 1 AND 8 AND c_47e8cb8b BETWEEN 1 AND 5 AND (c_39cecf36 IS NULL OR c_39cecf36 BETWEEN 1 AND 5))
);
CREATE INDEX k_12b5d1ab ON t_8176e273 (c_4cd1da85, c_8c10458e);
CREATE FUNCTION f_4b8fb32f() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'stocktake events are append-only';
END;
$$;
CREATE TRIGGER r_0188ca28 BEFORE UPDATE OR DELETE ON t_8176e273 FOR EACH ROW EXECUTE FUNCTION f_4b8fb32f();
CREATE TRIGGER r_1b8e85b1 BEFORE TRUNCATE ON t_8176e273 FOR EACH STATEMENT EXECUTE FUNCTION f_4b8fb32f();
REVOKE ALL ON TABLE t_8176e273 FROM PUBLIC;
