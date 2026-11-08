-- Migration 0001: minimal company registry (system database).
-- Physical identifiers come from SystemSchema; this file is immutable once applied (checksummed).
CREATE TABLE t_f446f4d0 (
    c_a9a73285 uuid NOT NULL,
    c_30fd1c11 varchar(63) NOT NULL,
    c_08f915ea smallint NOT NULL,
    c_15ceb585 timestamptz NOT NULL,
    c_d806142f timestamptz NOT NULL,
    CONSTRAINT k_78d272fe PRIMARY KEY (c_a9a73285),
    CONSTRAINT k_b3d5d2a2 UNIQUE (c_30fd1c11),
    CONSTRAINT k_3aeb4060 CHECK (c_08f915ea BETWEEN 0 AND 3),
    CONSTRAINT k_debceeec CHECK (c_30fd1c11 ~ '^[a-z][a-z0-9_]{0,62}$' AND c_30fd1c11 !~ '^pg_')
);

-- Registry identity (id, physical database name) is immutable; rows are never deleted.
CREATE FUNCTION f_c52b26ca() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'registry rows cannot be deleted';
    END IF;
    IF NEW.c_30fd1c11 IS DISTINCT FROM OLD.c_30fd1c11 OR NEW.c_a9a73285 IS DISTINCT FROM OLD.c_a9a73285 THEN
        RAISE EXCEPTION 'registry identity is immutable';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER r_d395935d BEFORE UPDATE OR DELETE ON t_f446f4d0
    FOR EACH ROW EXECUTE FUNCTION f_c52b26ca();
