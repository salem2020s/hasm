-- Migration 0005: cryptographic audit integrity (system database).
-- Physical identifiers come from SystemSchema (AuditEvent); this file is immutable once applied (checksummed).
-- Adds ONE new append-only table holding hash-chained, key-authenticated events for independent streams (the system stream and
-- one stream per company). Existing audit history (the access audit table of migration 0004) is left untouched: it is neither
-- rewritten nor backfilled (history before this migration cannot be authenticated retroactively) and remains readable.
-- Hashes and authentication codes are computed by the Backend with keys that never enter the database.

CREATE TABLE t_7e93ef23 (
    c_8a49147d varchar(48) NOT NULL,
    c_0aeec256 bigint NOT NULL,
    c_05b414b5 uuid NOT NULL,
    c_4260f38b uuid NULL,
    c_00969b7e timestamptz NOT NULL,
    c_318d4b7d smallint NOT NULL,
    c_e99d8b01 uuid NULL,
    c_ae727dec uuid NULL,
    c_135114d2 uuid NULL,
    c_6d3e3fa8 bigint NULL,
    c_b48ab4d7 varchar(1000) NULL,
    c_3ed2f8e1 smallint NOT NULL,
    c_2e582193 varchar(32) NOT NULL,
    c_1fecbd88 bytea NOT NULL,
    c_59ee706d bytea NOT NULL,
    c_c64705b9 bytea NOT NULL,
    CONSTRAINT k_20f7f980 PRIMARY KEY (c_8a49147d, c_0aeec256),
    CONSTRAINT k_4b99ec45 UNIQUE (c_05b414b5),
    CONSTRAINT k_688a599e FOREIGN KEY (c_4260f38b) REFERENCES t_f446f4d0 (c_a9a73285),
    CONSTRAINT k_134e62e4 FOREIGN KEY (c_e99d8b01) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_fb29a999 FOREIGN KEY (c_ae727dec) REFERENCES t_fc83d26e (c_74b9e5ad),
    -- A stream is either the system stream (no company) or exactly the stream of the company carried by the row.
    CONSTRAINT k_7ff931be CHECK (c_8a49147d = CASE WHEN c_4260f38b IS NULL THEN 'system'
                                                   ELSE 'company:' || replace(c_4260f38b::text, '-', '') END),
    CONSTRAINT k_67c74be8 CHECK (c_0aeec256 >= 1 AND octet_length(c_1fecbd88) = 32 AND octet_length(c_59ee706d) = 32 AND octet_length(c_c64705b9) = 32),
    CONSTRAINT k_38f3d334 CHECK (c_b48ab4d7 IS NULL OR char_length(c_b48ab4d7) <= 1000),
    CONSTRAINT k_8473aed1 CHECK (c_318d4b7d >= 1 AND c_3ed2f8e1 >= 1 AND c_2e582193 ~ '^[a-z0-9][a-z0-9-]{0,31}$')
);

CREATE INDEX k_642bc5ec ON t_7e93ef23 (c_6d3e3fa8) WHERE c_6d3e3fa8 IS NOT NULL;

-- Rows can only be appended. Any UPDATE or row removal is refused, even for the table owner (a superuser or a DBA who disables
-- the trigger is outside the database's own protection; such tampering is detected by the verifier through the keyed
-- authentication codes and the external checkpoints).
CREATE FUNCTION f_359f6a00() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit events are append-only';
END;
$$;
CREATE TRIGGER r_11080dd7 BEFORE UPDATE OR DELETE ON t_7e93ef23
    FOR EACH ROW EXECUTE FUNCTION f_359f6a00();

-- No implicit access for other roles: the runtime account is granted SELECT and INSERT only (see docs/DEVELOPMENT.md).
REVOKE ALL ON TABLE t_7e93ef23 FROM PUBLIC;
