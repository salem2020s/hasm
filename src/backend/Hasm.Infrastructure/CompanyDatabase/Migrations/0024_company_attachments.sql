-- Migration 0024: attachments (Phase 3.12). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0024-attachments.cjs (logical dictionary: CompanySchema.Attachments.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (SRS 14.3, 14.4, 17.1, 17.2): the METADATA of a file kept on the server (never in the database): which business record it belongs to (a registered kind and the record id; a polymorphic
-- reference is validated by the service, which is the only writer), its display name and type, its size, the SHA-256 of the PLAINTEXT (verified on every read), its sensitivity, the opaque name of the
-- stored file, and for a sensitive file the id of the key that wraps it. The file itself is plain on disk when normal and application-level encrypted (envelope: AES-256-GCM, versioned master key) when sensitive.
-- Invariants enforced by PostgreSQL:
--   * a row is never removed: an attachment is REMOVED (state 2) with a reason, once, and a removed row never changes again; every change advances the revision by one;
--   * identity, record, name, type, size, hash, sensitivity, stored name and creation data are immutable; only the key id of an ACTIVE sensitive attachment may change (re-wrap) and the state may go 1 -> 2;
--   * a sensitive attachment has a key id and a normal one has none; the hash is 64 lowercase hex digits; the size is positive; a removal carries who, when and why.
-- sensitivity: 1 normal, 2 sensitive. state: 1 active, 2 removed.

CREATE TABLE t_f3dfaa38 (
    c_65bde0b1 uuid NOT NULL,
    c_9b5672bf varchar(40) NOT NULL,
    c_88621505 uuid NOT NULL,
    c_ba5b0b71 varchar(200) NOT NULL,
    c_0bb3ac73 varchar(100) NOT NULL,
    c_8eae406a bigint NOT NULL,
    c_5b654f92 char(64) NOT NULL,
    c_91e3f5ff smallint NOT NULL,
    c_8555e256 varchar(40) NOT NULL,
    c_6903efdb varchar(40) NULL,
    c_24b87a8d smallint NOT NULL DEFAULT 1,
    c_b8117d60 uuid NULL,
    c_9ba8ed5a timestamptz NULL,
    c_60c54f97 varchar(500) NULL,
    c_23480f06 bigint NOT NULL DEFAULT 1,
    c_54c77349 uuid NOT NULL,
    c_a606ea69 timestamptz NOT NULL,
    c_fce76408 timestamptz NOT NULL,
    CONSTRAINT k_aa3bd653 PRIMARY KEY (c_65bde0b1),
    CONSTRAINT k_7a087736 CHECK (c_23480f06 > 0 AND c_9b5672bf ~ '^[a-z][a-z0-9_]{1,39}$' AND char_length(btrim(c_ba5b0b71)) >= 1
        AND c_8eae406a > 0 AND c_5b654f92 ~ '^[0-9a-f]{64}$' AND c_91e3f5ff IN (1, 2) AND c_24b87a8d IN (1, 2)
        AND c_8555e256 ~ '^[0-9a-f]{32}\.att$' AND ((c_91e3f5ff = 2) = (c_6903efdb IS NOT NULL))
        AND ((c_24b87a8d = 2) = (c_9ba8ed5a IS NOT NULL AND c_b8117d60 IS NOT NULL AND char_length(btrim(coalesce(c_60c54f97, ''))) >= 1)))
);
CREATE UNIQUE INDEX k_1de1986f ON t_f3dfaa38 (c_8555e256);
CREATE INDEX k_24d04ff8 ON t_f3dfaa38 (c_9b5672bf, c_88621505);

CREATE FUNCTION f_02fd4151() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_23480f06 <> 1 OR NEW.c_24b87a8d <> 1 THEN RAISE EXCEPTION 'an attachment is created active at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'attachments are never removed from the database'; END IF;
    IF OLD.c_24b87a8d = 2 THEN RAISE EXCEPTION 'a removed attachment never changes again'; END IF;
    IF NEW.c_65bde0b1 IS DISTINCT FROM OLD.c_65bde0b1 OR NEW.c_9b5672bf IS DISTINCT FROM OLD.c_9b5672bf OR NEW.c_88621505 IS DISTINCT FROM OLD.c_88621505
       OR NEW.c_ba5b0b71 IS DISTINCT FROM OLD.c_ba5b0b71 OR NEW.c_0bb3ac73 IS DISTINCT FROM OLD.c_0bb3ac73 OR NEW.c_8eae406a IS DISTINCT FROM OLD.c_8eae406a
       OR NEW.c_5b654f92 IS DISTINCT FROM OLD.c_5b654f92 OR NEW.c_91e3f5ff IS DISTINCT FROM OLD.c_91e3f5ff OR NEW.c_8555e256 IS DISTINCT FROM OLD.c_8555e256
       OR NEW.c_54c77349 IS DISTINCT FROM OLD.c_54c77349 OR NEW.c_a606ea69 IS DISTINCT FROM OLD.c_a606ea69 THEN
        RAISE EXCEPTION 'the record, name, type, size, hash, sensitivity, stored name and creation data of an attachment are immutable';
    END IF;
    IF NEW.c_23480f06 <> OLD.c_23480f06 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NEW.c_24b87a8d = 1 AND (NEW.c_b8117d60 IS NOT NULL OR NEW.c_9ba8ed5a IS NOT NULL OR NEW.c_60c54f97 IS NOT NULL) THEN RAISE EXCEPTION 'removal data on an active attachment'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_31e1bcc1 BEFORE INSERT OR UPDATE OR DELETE ON t_f3dfaa38 FOR EACH ROW EXECUTE FUNCTION f_02fd4151();
CREATE TRIGGER r_f7dc93c3 BEFORE TRUNCATE ON t_f3dfaa38 FOR EACH STATEMENT EXECUTE FUNCTION f_02fd4151();
REVOKE ALL ON TABLE t_f3dfaa38 FROM PUBLIC;
