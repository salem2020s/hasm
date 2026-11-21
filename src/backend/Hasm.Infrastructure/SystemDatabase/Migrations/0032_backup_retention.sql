-- Migration (Phase 4.3 slice 6, D-134, D-135): backup legal holds and recovery-chain pins. Forward-only and additive: two new tables, nothing seeded, no existing data read or changed.
-- Its number is the next free system number when it merges (allocation rule in docs/INTEGRATION-STATUS.md); the opaque names are derived from a seed WITHOUT the number (SystemSchema.BackupRetention.cs).
--
-- Backup legal hold (D-134): scope 1 = one run (run id), scope 2 = one company's runs, optionally limited to runs started within a local-date range [from, to] (open ends allowed).
-- A reason is required. A hold is never deleted; it is released at most once (all three release columns set together), and nothing else of it ever changes. Release is not offered by the
-- administrative tool until the Phase 4.1 named login and independent approval exist; the columns exist so that the guard is final now.
-- Recovery-chain pin (D-135): a run an active recovery chain needs (kind 1 = safety backup of a cutover, slice 7; kind 2 = other recovery chain) until expires_at. Append-only, never changed or removed.

CREATE TABLE t_4caa7490 (
    c_cac29687 uuid NOT NULL,
    c_1ea28b23 smallint NOT NULL,
    c_65d245cf varchar(32) NULL,
    c_29d8b5b4 uuid NULL,
    c_a88bdba2 date NULL,
    c_5cb55291 date NULL,
    c_d77f1c27 varchar(500) NOT NULL,
    c_a231fa62 varchar(80) NOT NULL,
    c_5d6a8ce2 timestamptz NOT NULL,
    c_cc1f1452 varchar(80) NULL,
    c_32d04987 timestamptz NULL,
    c_d2976757 varchar(500) NULL,
    CONSTRAINT k_624dbb7d PRIMARY KEY (c_cac29687),
    CONSTRAINT k_9ae8fd8d CHECK (
        ((c_1ea28b23 = 1 AND c_65d245cf ~ '^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$' AND c_29d8b5b4 IS NULL AND c_a88bdba2 IS NULL AND c_5cb55291 IS NULL)
         OR (c_1ea28b23 = 2 AND c_65d245cf IS NULL AND c_29d8b5b4 IS NOT NULL AND (c_a88bdba2 IS NULL OR c_5cb55291 IS NULL OR c_a88bdba2 <= c_5cb55291)))
        AND char_length(btrim(c_d77f1c27)) >= 3 AND char_length(btrim(c_a231fa62)) >= 1),
    CONSTRAINT k_b0ad7db5 CHECK ((c_cc1f1452 IS NULL AND c_32d04987 IS NULL AND c_d2976757 IS NULL)
        OR (char_length(btrim(c_cc1f1452)) >= 1 AND c_32d04987 IS NOT NULL AND char_length(btrim(c_d2976757)) >= 3))
);

CREATE FUNCTION f_af9948c7() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_cc1f1452 IS NOT NULL OR NEW.c_32d04987 IS NOT NULL OR NEW.c_d2976757 IS NOT NULL THEN RAISE EXCEPTION 'a backup legal hold is placed unreleased'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'backup legal holds are never removed'; END IF;
    IF OLD.c_32d04987 IS NOT NULL THEN RAISE EXCEPTION 'a released backup legal hold never changes'; END IF;
    IF NEW.c_cac29687 IS DISTINCT FROM OLD.c_cac29687 OR NEW.c_1ea28b23 IS DISTINCT FROM OLD.c_1ea28b23 OR NEW.c_65d245cf IS DISTINCT FROM OLD.c_65d245cf
       OR NEW.c_29d8b5b4 IS DISTINCT FROM OLD.c_29d8b5b4 OR NEW.c_a88bdba2 IS DISTINCT FROM OLD.c_a88bdba2 OR NEW.c_5cb55291 IS DISTINCT FROM OLD.c_5cb55291
       OR NEW.c_d77f1c27 IS DISTINCT FROM OLD.c_d77f1c27 OR NEW.c_a231fa62 IS DISTINCT FROM OLD.c_a231fa62 OR NEW.c_5d6a8ce2 IS DISTINCT FROM OLD.c_5d6a8ce2 THEN
        RAISE EXCEPTION 'only the release of a backup legal hold may be recorded';
    END IF;
    IF NEW.c_32d04987 IS NULL THEN RAISE EXCEPTION 'a backup legal hold update must release it'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_42e8f242 BEFORE INSERT OR UPDATE OR DELETE ON t_4caa7490 FOR EACH ROW EXECUTE FUNCTION f_af9948c7();
CREATE TRIGGER r_c27e1c2c BEFORE TRUNCATE ON t_4caa7490 FOR EACH STATEMENT EXECUTE FUNCTION f_af9948c7();
REVOKE ALL ON TABLE t_4caa7490 FROM PUBLIC;

CREATE TABLE t_035c414c (
    c_ed15eadd uuid NOT NULL,
    c_b02f6b9d varchar(32) NOT NULL,
    c_b8353210 smallint NOT NULL,
    c_3813c927 varchar(200) NOT NULL,
    c_56cd5893 varchar(80) NOT NULL,
    c_75514e76 timestamptz NOT NULL,
    c_57a322de timestamptz NOT NULL,
    CONSTRAINT k_2e07034e PRIMARY KEY (c_ed15eadd),
    CONSTRAINT k_580dd48e CHECK (c_b02f6b9d ~ '^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$' AND c_b8353210 IN (1, 2) AND char_length(btrim(c_3813c927)) >= 3
        AND char_length(btrim(c_56cd5893)) >= 1 AND c_57a322de > c_75514e76)
);

CREATE FUNCTION f_86d2edd8() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'recovery-chain pins are append-only';
END;
$$;
CREATE TRIGGER r_2b255a22 BEFORE UPDATE OR DELETE ON t_035c414c FOR EACH ROW EXECUTE FUNCTION f_86d2edd8();
CREATE TRIGGER r_107d8e66 BEFORE TRUNCATE ON t_035c414c FOR EACH STATEMENT EXECUTE FUNCTION f_86d2edd8();
REVOKE ALL ON TABLE t_035c414c FROM PUBLIC;
