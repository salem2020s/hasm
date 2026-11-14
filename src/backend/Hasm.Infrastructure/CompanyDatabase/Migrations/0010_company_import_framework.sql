-- Migration 0010: generic import framework (Phase 3.2D), built slice by slice before the phase is merged. Forward-only, no DROP, nothing seeded, no data migrated.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.Imports.cs.
--
-- ISOLATION RULE: imports write ONLY to the tables below (staging). Nothing here references a business table; an adapter's apply step (a later slice) is the only code that touches business data,
-- and only after validation, a dry run, an independent approval and a re-validation inside its own audited transaction.

-- ============================================================================================================================================
-- S1 - batches, staged rows, history, settings
-- ============================================================================================================================================

-- One uploaded file and its life cycle. status: 1 uploaded (encrypted original stored), 2 staged (rows parsed), 3 mapped, 4 validated (dry run done), 5 submitted for approval, 6 approved,
-- 7 applying, 8 applied (terminal), 9 failed (recoverable), 10 rejected (terminal), 11 cancelled (terminal), 12 expired (terminal).
-- file_kind: 1 csv, 2 xlsx. The original file lives encrypted outside the database; only its name, size, SHA-256 and key id are recorded here. revision advances by one on every change.
CREATE TABLE t_153392c3 (
    c_bf254544 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_09d09dd9 varchar(64) NOT NULL,
    c_8fafc1ac integer NOT NULL,
    c_fb0dcab8 smallint NOT NULL DEFAULT 1,
    c_4fe5cb24 varchar(255) NOT NULL,
    c_fa83dd88 smallint NOT NULL,
    c_01407ab9 bigint NOT NULL,
    c_071a0752 char(64) NOT NULL,
    c_fbb212f0 varchar(80) NOT NULL,
    c_546fb474 varchar(32) NOT NULL,
    c_83a86bb8 jsonb NOT NULL DEFAULT '{}'::jsonb,
    c_286033ff jsonb NOT NULL DEFAULT '[]'::jsonb,
    c_acae102c jsonb NULL,
    c_e67e2dc1 integer NOT NULL DEFAULT 0,
    c_7b8ea1ad char(64) NULL,
    c_fbc75ef6 char(64) NULL,
    c_a6725cfa jsonb NULL,
    c_2e738c03 bigint NOT NULL DEFAULT 1,
    c_91f9f5d5 varchar(100) NULL,
    c_6c662d5d uuid NOT NULL,
    c_e6a8b09a timestamptz NOT NULL,
    c_68c70c41 timestamptz NOT NULL,
    c_83a915fd uuid NULL,
    c_dacd5ab3 timestamptz NULL,
    c_c130ecd5 timestamptz NOT NULL,
    c_6af863fd timestamptz NULL,
    c_6d2647de timestamptz NULL,
    c_74823892 timestamptz NULL,
    c_c6666cf4 jsonb NULL,
    CONSTRAINT k_1b8c15c2 PRIMARY KEY (c_bf254544),
    CONSTRAINT k_4c690e46 UNIQUE (c_fbb212f0),
    CONSTRAINT k_3333a1db CHECK (
        c_09d09dd9 ~ '^[a-z][a-z0-9._-]{0,63}$' AND c_8fafc1ac >= 1
        AND c_fb0dcab8 BETWEEN 1 AND 12 AND c_fa83dd88 IN (1, 2)
        AND char_length(btrim(c_4fe5cb24)) BETWEEN 1 AND 255
        AND c_01407ab9 > 0 AND c_071a0752 ~ '^[0-9a-f]{64}$'
        AND c_fbb212f0 ~ '^[0-9a-f]{32}\.enc$' AND c_546fb474 ~ '^[a-z0-9][a-z0-9-]{0,31}$'
        AND jsonb_typeof(c_83a86bb8) = 'object' AND jsonb_typeof(c_286033ff) = 'array'
        AND (c_acae102c IS NULL OR jsonb_typeof(c_acae102c) = 'object')
        AND c_e67e2dc1 >= 0 AND (c_7b8ea1ad IS NULL OR c_7b8ea1ad ~ '^[0-9a-f]{64}$')
        AND (c_fbc75ef6 IS NULL OR c_fbc75ef6 ~ '^[0-9a-f]{64}$')
        AND c_2e738c03 >= 1
        AND (c_91f9f5d5 IS NULL OR char_length(c_91f9f5d5) BETWEEN 1 AND 100)),
    CONSTRAINT k_88fd7e4b CHECK (
        ((c_fb0dcab8 IN (8, 10, 11, 12)) = (c_6af863fd IS NOT NULL))
        AND (c_fb0dcab8 NOT IN (3, 4, 5, 6, 7, 8, 9) OR c_acae102c IS NOT NULL)
        AND (c_fb0dcab8 NOT IN (2, 3, 4, 5, 6, 7, 8, 9) OR c_7b8ea1ad IS NOT NULL)
        AND (c_fb0dcab8 NOT IN (5, 6, 7) OR (c_83a915fd IS NOT NULL AND c_dacd5ab3 IS NOT NULL)))
);
-- A client-supplied idempotency key makes a repeated upload return the same batch instead of a second one.
CREATE UNIQUE INDEX k_51a0c819 ON t_153392c3 (c_91f9f5d5) WHERE c_91f9f5d5 IS NOT NULL;
CREATE INDEX k_b51a645d ON t_153392c3 (c_fb0dcab8, c_c130ecd5);

-- Life-cycle guard for EVERY writer: batches are never deleted; identity, provenance and the file evidence are immutable; the revision advances by one; only the transitions below exist;
-- a terminal batch (applied, rejected, cancelled, expired) changes no further except the retention bookkeeping (purge timestamps, key id after a re-wrap).
CREATE FUNCTION f_1d952876() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'import batches are never deleted'; END IF;
    IF NEW.c_bf254544 IS DISTINCT FROM OLD.c_bf254544 OR NEW.c_09d09dd9 IS DISTINCT FROM OLD.c_09d09dd9
       OR NEW.c_4fe5cb24 IS DISTINCT FROM OLD.c_4fe5cb24 OR NEW.c_fa83dd88 IS DISTINCT FROM OLD.c_fa83dd88
       OR NEW.c_01407ab9 IS DISTINCT FROM OLD.c_01407ab9 OR NEW.c_071a0752 IS DISTINCT FROM OLD.c_071a0752
       OR NEW.c_fbb212f0 IS DISTINCT FROM OLD.c_fbb212f0 OR NEW.c_6c662d5d IS DISTINCT FROM OLD.c_6c662d5d
       OR NEW.c_e6a8b09a IS DISTINCT FROM OLD.c_e6a8b09a OR NEW.c_91f9f5d5 IS DISTINCT FROM OLD.c_91f9f5d5 THEN
        RAISE EXCEPTION 'the identity and the file evidence of an import batch are immutable';
    END IF;
    IF NEW.c_2e738c03 <> OLD.c_2e738c03 + 1 THEN RAISE EXCEPTION 'the revision of an import batch must advance by one'; END IF;
    IF OLD.c_fb0dcab8 IN (8, 10, 11, 12) THEN
        IF NEW.c_fb0dcab8 <> OLD.c_fb0dcab8 THEN RAISE EXCEPTION 'a finished import batch cannot change state'; END IF;
        IF NEW.c_8fafc1ac IS DISTINCT FROM OLD.c_8fafc1ac OR NEW.c_83a86bb8 IS DISTINCT FROM OLD.c_83a86bb8
           OR NEW.c_286033ff IS DISTINCT FROM OLD.c_286033ff OR NEW.c_acae102c IS DISTINCT FROM OLD.c_acae102c
           OR NEW.c_e67e2dc1 IS DISTINCT FROM OLD.c_e67e2dc1 OR NEW.c_7b8ea1ad IS DISTINCT FROM OLD.c_7b8ea1ad
           OR NEW.c_fbc75ef6 IS DISTINCT FROM OLD.c_fbc75ef6 OR NEW.c_a6725cfa IS DISTINCT FROM OLD.c_a6725cfa
           OR NEW.c_83a915fd IS DISTINCT FROM OLD.c_83a915fd OR NEW.c_dacd5ab3 IS DISTINCT FROM OLD.c_dacd5ab3
           OR NEW.c_c130ecd5 IS DISTINCT FROM OLD.c_c130ecd5 OR NEW.c_6af863fd IS DISTINCT FROM OLD.c_6af863fd
           OR NEW.c_c6666cf4 IS DISTINCT FROM OLD.c_c6666cf4 THEN
            RAISE EXCEPTION 'a finished import batch is immutable';
        END IF;
        IF (OLD.c_6d2647de IS NOT NULL AND NEW.c_6d2647de IS DISTINCT FROM OLD.c_6d2647de)
           OR (OLD.c_74823892 IS NOT NULL AND NEW.c_74823892 IS DISTINCT FROM OLD.c_74823892) THEN
            RAISE EXCEPTION 'the purge record of an import batch is final';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_fb0dcab8 <> OLD.c_fb0dcab8 AND (OLD.c_fb0dcab8, NEW.c_fb0dcab8) NOT IN (
        (1,2),(1,11),(1,12),(2,3),(2,11),(2,12),(3,2),(3,4),(3,11),(3,12),(4,2),(4,3),(4,5),(4,11),(4,12),(5,3),(5,4),(5,6),(5,10),(5,11),(5,12),
        (6,3),(6,4),(6,7),(6,11),(6,12),(7,8),(7,9),(9,2),(9,3),(9,4),(9,7),(9,11)) THEN
        RAISE EXCEPTION 'this import batch state change is not allowed';
    END IF;
    IF NEW.c_6d2647de IS NOT NULL OR NEW.c_74823892 IS NOT NULL THEN
        RAISE EXCEPTION 'only a finished import batch can be purged';
    END IF;
    -- (S3) The approval is bound to the revision it was given for: set only by Submitted -> Approved (to that very revision), cleared when the batch goes back to Staged, Mapped or Validated.
    IF NEW.c_bd7e35c3 IS DISTINCT FROM OLD.c_bd7e35c3
       AND NOT ((OLD.c_fb0dcab8 = 5 AND NEW.c_fb0dcab8 = 6 AND NEW.c_bd7e35c3 = OLD.c_2e738c03)
                OR (NEW.c_bd7e35c3 IS NULL AND NEW.c_fb0dcab8 IN (2, 3, 4))) THEN
        RAISE EXCEPTION 'the approval of an import is bound to the revision it was given for';
    END IF;
    -- (S3) A submitted batch becomes Approved (or Rejected) only with the independent decision of exactly this revision: nobody who created or submitted it, no rejection when approving.
    IF OLD.c_fb0dcab8 = 5 AND NEW.c_fb0dcab8 IN (6, 10) THEN
        IF NOT EXISTS (SELECT 1 FROM t_3fa074e5 d WHERE d.c_9adee5c9 = 'import_batch' AND d.c_175a20c9 = OLD.c_bf254544
                       AND d.c_f399e237 = OLD.c_2e738c03 AND d.c_3a789977 = CASE WHEN NEW.c_fb0dcab8 = 6 THEN 1 ELSE 2 END
                       AND d.c_942d312c <> OLD.c_6c662d5d AND d.c_942d312c IS DISTINCT FROM OLD.c_83a915fd) THEN
            RAISE EXCEPTION 'the import needs the independent decision of exactly this revision';
        END IF;
        IF NEW.c_fb0dcab8 = 6 AND EXISTS (SELECT 1 FROM t_3fa074e5 d WHERE d.c_9adee5c9 = 'import_batch' AND d.c_175a20c9 = OLD.c_bf254544
                       AND d.c_f399e237 = OLD.c_2e738c03 AND d.c_3a789977 = 2) THEN
            RAISE EXCEPTION 'a rejected import cannot be approved';
        END IF;
    END IF;
    -- (S3) What was validated and approved cannot change while the batch is submitted, approved, applying or failed.
    IF OLD.c_fb0dcab8 IN (5, 6, 7, 9) AND NEW.c_fb0dcab8 IN (5, 6, 7, 9)
       AND (NEW.c_fbc75ef6 IS DISTINCT FROM OLD.c_fbc75ef6 OR NEW.c_a6725cfa IS DISTINCT FROM OLD.c_a6725cfa
            OR NEW.c_acae102c IS DISTINCT FROM OLD.c_acae102c OR NEW.c_7b8ea1ad IS DISTINCT FROM OLD.c_7b8ea1ad) THEN
        RAISE EXCEPTION 'the validated content of a submitted or approved import cannot change';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_5fecf89f BEFORE UPDATE OR DELETE ON t_153392c3 FOR EACH ROW EXECUTE FUNCTION f_1d952876();
CREATE TRIGGER r_1b9d677b BEFORE TRUNCATE ON t_153392c3 FOR EACH STATEMENT EXECUTE FUNCTION f_1d952876();
REVOKE ALL ON TABLE t_153392c3 FROM PUBLIC;

-- The staged rows of a batch: the cells exactly as read (raw, canonical text), their hash, and what later slices add (normalized values, validation errors, the explicit resolution of a conflict).
-- status: 1 staged, 2 valid, 3 invalid, 4 conflict, 5 resolved/skipped, 6 applied. Rows exist only while the batch is staged; they are replaced wholesale when the file is re-staged and frozen
-- from submission on; a finished batch may have them purged by retention.
CREATE TABLE t_863ea0e3 (
    c_32965e1f uuid NOT NULL,
    c_e026b5e7 integer NOT NULL,
    c_9d1ea166 jsonb NOT NULL,
    c_d92532f0 char(64) NOT NULL,
    c_e9835db7 jsonb NULL,
    c_78c427bb smallint NOT NULL DEFAULT 1,
    c_aa830dd8 jsonb NULL,
    c_d5784b67 jsonb NULL,
    CONSTRAINT k_5849e3f8 PRIMARY KEY (c_32965e1f, c_e026b5e7),
    CONSTRAINT k_09bddbe2 FOREIGN KEY (c_32965e1f) REFERENCES t_153392c3 (c_bf254544),
    CONSTRAINT k_ec11ddf4 CHECK (c_e026b5e7 >= 1 AND jsonb_typeof(c_9d1ea166) = 'array' AND c_d92532f0 ~ '^[0-9a-f]{64}$' AND c_78c427bb BETWEEN 1 AND 9)
);
CREATE INDEX k_6f38ce4b ON t_863ea0e3 (c_32965e1f, c_78c427bb, c_e026b5e7);
CREATE FUNCTION f_bc416a22() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_status smallint;
BEGIN
    IF TG_OP NOT IN ('INSERT', 'UPDATE', 'DELETE') THEN RAISE EXCEPTION 'staged import rows cannot be emptied in bulk'; END IF;
    SELECT b.c_fb0dcab8 INTO v_status FROM t_153392c3 b WHERE b.c_bf254544 = CASE WHEN TG_OP = 'DELETE' THEN OLD.c_32965e1f ELSE NEW.c_32965e1f END;
    IF TG_OP = 'INSERT' THEN
        IF v_status IS DISTINCT FROM 2 THEN RAISE EXCEPTION 'rows can only be staged while the batch is being staged'; END IF;
        RETURN NEW;
    ELSIF TG_OP = 'UPDATE' THEN
        IF NEW.c_32965e1f IS DISTINCT FROM OLD.c_32965e1f OR NEW.c_e026b5e7 IS DISTINCT FROM OLD.c_e026b5e7
           OR NEW.c_9d1ea166 IS DISTINCT FROM OLD.c_9d1ea166 OR NEW.c_d92532f0 IS DISTINCT FROM OLD.c_d92532f0 THEN
            RAISE EXCEPTION 'the content of a staged row is immutable';
        END IF;
        IF v_status NOT IN (3, 4, 7, 9) THEN RAISE EXCEPTION 'staged rows are frozen for this state of the batch'; END IF;
        RETURN NEW;
    END IF;
    IF v_status NOT IN (1, 2, 3, 4, 9, 8, 10, 11, 12) THEN RAISE EXCEPTION 'staged rows are frozen for this state of the batch'; END IF;
    RETURN OLD;
END;
$$;
CREATE TRIGGER r_48c1ce71 BEFORE INSERT OR UPDATE OR DELETE ON t_863ea0e3 FOR EACH ROW EXECUTE FUNCTION f_bc416a22();
CREATE TRIGGER r_90085969 BEFORE TRUNCATE ON t_863ea0e3 FOR EACH STATEMENT EXECUTE FUNCTION f_bc416a22();
REVOKE ALL ON TABLE t_863ea0e3 FROM PUBLIC;

-- Append-only history of every change of a batch (state changes, mapping, resolutions, approval, application, retention), next to its authenticated audit event.
CREATE TABLE t_c0e5f7b5 (
    c_cb757793 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_3320c60b uuid NOT NULL,
    c_fb9ffb64 smallint NOT NULL,
    c_2c1b0a9d bigint NOT NULL,
    c_34bfefc6 uuid NULL,
    c_d59455f6 timestamptz NOT NULL,
    c_b9457705 jsonb NOT NULL,
    c_b4596169 varchar(48) NOT NULL,
    c_32a27469 bigint NOT NULL,
    CONSTRAINT k_b3f26402 PRIMARY KEY (c_cb757793),
    CONSTRAINT k_3a8abcc7 FOREIGN KEY (c_3320c60b) REFERENCES t_153392c3 (c_bf254544),
    CONSTRAINT k_346a3500 UNIQUE (c_b4596169, c_32a27469),
    CONSTRAINT k_2f57aaa8 FOREIGN KEY (c_b4596169, c_32a27469) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_85514d93 CHECK (c_fb9ffb64 BETWEEN 1 AND 40 AND c_2c1b0a9d >= 1 AND jsonb_typeof(c_b9457705) = 'object')
);
CREATE INDEX k_3d53edde ON t_c0e5f7b5 (c_3320c60b, c_cb757793);
CREATE FUNCTION f_384516a4() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'the import history is append-only';
END;
$$;
CREATE TRIGGER r_8670411f BEFORE UPDATE OR DELETE ON t_c0e5f7b5 FOR EACH ROW EXECUTE FUNCTION f_384516a4();
CREATE TRIGGER r_1f644842 BEFORE TRUNCATE ON t_c0e5f7b5 FOR EACH STATEMENT EXECUTE FUNCTION f_384516a4();
REVOKE ALL ON TABLE t_c0e5f7b5 FROM PUBLIC;

-- Company import settings (one row, created on first change): how long finished batches keep their encrypted original and staged rows, and how long a pending batch stays valid before it expires.
CREATE TABLE t_2b1f2f8b (
    c_bbe100fd boolean NOT NULL DEFAULT true,
    c_4961c4dd integer NOT NULL,
    c_629dd306 integer NOT NULL,
    c_fd7097a7 bigint NOT NULL,
    c_6067a496 timestamptz NOT NULL,
    CONSTRAINT k_bb8c3d2f PRIMARY KEY (c_bbe100fd),
    CONSTRAINT k_bc473b3c CHECK (c_bbe100fd AND c_4961c4dd BETWEEN 1 AND 3650 AND c_629dd306 BETWEEN 1 AND 3650 AND c_fd7097a7 >= 1)
);
CREATE FUNCTION f_d1eedf38() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'import settings cannot be deleted'; END IF;
    IF NEW.c_fd7097a7 <> OLD.c_fd7097a7 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_09ffaa9f BEFORE UPDATE OR DELETE ON t_2b1f2f8b FOR EACH ROW EXECUTE FUNCTION f_d1eedf38();
CREATE TRIGGER r_583f09bb BEFORE TRUNCATE ON t_2b1f2f8b FOR EACH STATEMENT EXECUTE FUNCTION f_d1eedf38();
REVOKE ALL ON TABLE t_2b1f2f8b FROM PUBLIC;


-- ============================================================================================================================================
-- S2 - mapping, normalization, validation and the dry run
-- ============================================================================================================================================
-- Per staged row, the analysis of the dry run: the exact operation application would perform, the explanation, the resolutions the adapter allows for a conflict and (for a repeated row) the row it
-- repeats. sequence orders the operations of the plan (an adapter may need parents before children). Both are rewritten by every dry run while the batch is Mapped, Validated or Failed (the row guard).
ALTER TABLE t_863ea0e3 ADD COLUMN c_451e180b jsonb NULL;
ALTER TABLE t_863ea0e3 ADD COLUMN c_d3b66a5c integer NOT NULL DEFAULT 0;
CREATE INDEX k_47e681bf ON t_863ea0e3 (c_32965e1f, c_d3b66a5c, c_e026b5e7);

-- A batch that has been validated (or is further along) always carries its plan and the canonical hash of that plan.
ALTER TABLE t_153392c3 ADD CONSTRAINT k_dfd28252 CHECK (
    c_fb0dcab8 NOT IN (4, 5, 6, 7, 8, 9) OR (c_fbc75ef6 IS NOT NULL AND c_a6725cfa IS NOT NULL AND jsonb_typeof(c_a6725cfa) = 'object'));


-- ============================================================================================================================================
-- S3 - independent approval and controlled application
-- ============================================================================================================================================
-- The revision at which the approving decision was cast. It is set ONLY by the transition from Submitted to Approved (by that very revision), survives applying/failed/applied, and is cleared whenever the
-- batch goes back to Mapped or Validated: an approval can never outlive the content it was given for. Validated or approved content (mapping, rows hash, plan) cannot change while submitted, approved,
-- applying or failed (guard in the batch trigger above).
ALTER TABLE t_153392c3 ADD COLUMN c_bd7e35c3 bigint NULL;
ALTER TABLE t_153392c3 ADD CONSTRAINT k_a8f55915 CHECK (
    (c_fb0dcab8 NOT IN (6, 7, 8, 9) OR c_bd7e35c3 IS NOT NULL)
    AND (c_fb0dcab8 NOT IN (1, 2, 3, 4, 5, 10) OR c_bd7e35c3 IS NULL)
    AND (c_fb0dcab8 <> 8 OR c_c6666cf4 IS NOT NULL));

-- Decisions on an import are recorded in the shared approval-decision table (document type 'import_batch', bound to the batch revision). For every writer: a decision is accepted only on a SUBMITTED
-- batch and only for its CURRENT revision, and never from the person who created or submitted it (separation of duties in PostgreSQL as well as in the service; no company setting can lower it).
CREATE FUNCTION f_1425577b() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    b record;
BEGIN
    IF NEW.c_9adee5c9 <> 'import_batch' THEN RETURN NEW; END IF;
    SELECT x.c_fb0dcab8 AS status, x.c_2e738c03 AS revision, x.c_6c662d5d AS created_by, x.c_83a915fd AS submitted_by INTO b
    FROM t_153392c3 x WHERE x.c_bf254544 = NEW.c_175a20c9;
    IF NOT FOUND THEN RAISE EXCEPTION 'unknown import batch'; END IF;
    IF b.status <> 5 OR b.revision <> NEW.c_f399e237 THEN RAISE EXCEPTION 'a decision applies only to the submitted revision of an import'; END IF;
    IF NEW.c_942d312c = b.created_by OR NEW.c_942d312c = b.submitted_by THEN
        RAISE EXCEPTION 'an import needs an independent approver: the creator and the submitter cannot decide';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_94be4b81 BEFORE INSERT ON t_3fa074e5 FOR EACH ROW EXECUTE FUNCTION f_1425577b();
