-- Migration 0003: central identity and local authentication foundation (system database).
-- Physical identifiers come from SystemSchema (User, Session, LoginThrottle, UserCompanyAccess); this file is immutable once applied (checksummed).
-- Only password HASHES are stored. Session tokens are stored only as SHA-256 hashes.

-- Users: one stable central identity per person. Users are never deleted (disable instead).
-- state: 0 Active, 1 Disabled, 2 Locked (administrative lock; automatic brute-force blocking lives in the throttle table).
CREATE TABLE t_fc83d26e (
    c_74b9e5ad uuid NOT NULL,
    c_65533e82 varchar(64) NOT NULL,
    c_d4b85ec6 varchar(100) NOT NULL,
    c_6b6d1a87 text NOT NULL,
    c_2e208e57 smallint NOT NULL,
    c_241d633c boolean NOT NULL,
    c_b1b46421 timestamptz NOT NULL,
    c_82befcac timestamptz NOT NULL,
    c_2a818d05 timestamptz NOT NULL,
    c_01184141 timestamptz NULL,
    CONSTRAINT k_02c86823 PRIMARY KEY (c_74b9e5ad),
    CONSTRAINT k_65d29eb5 UNIQUE (c_65533e82),
    CONSTRAINT k_4c15c740 CHECK (c_2e208e57 BETWEEN 0 AND 2),
    CONSTRAINT k_a3c1e5d0 CHECK (c_65533e82 ~ '^[a-z0-9][a-z0-9._@-]{2,63}$'),
    CONSTRAINT k_4d8c1d46 CHECK (char_length(btrim(c_d4b85ec6)) >= 1),
    CONSTRAINT k_ff78c4f1 CHECK (char_length(c_6b6d1a87) >= 60)
);

-- A user's id and login identifier are immutable and rows are never deleted.
CREATE FUNCTION f_6e1e11cd() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'identity rows cannot be deleted';
    END IF;
    IF NEW.c_74b9e5ad IS DISTINCT FROM OLD.c_74b9e5ad OR NEW.c_65533e82 IS DISTINCT FROM OLD.c_65533e82 THEN
        RAISE EXCEPTION 'identity key is immutable';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER r_be490ac9 BEFORE UPDATE OR DELETE ON t_fc83d26e
    FOR EACH ROW EXECUTE FUNCTION f_6e1e11cd();

-- Server-side sessions. Only the SHA-256 of the 256-bit random token is stored.
-- client_kind: 0 Web (cookie), 1 Desktop (bearer). revoked_reason: 0 Logout, 1 Administrative, 2 PasswordChanged.
CREATE TABLE t_5a3ce63e (
    c_43661a7f uuid NOT NULL,
    c_fcb7e5fc uuid NOT NULL,
    c_29e9c869 bytea NOT NULL,
    c_ed958b18 timestamptz NOT NULL,
    c_050c6f1d timestamptz NOT NULL,
    c_b417d62a timestamptz NOT NULL,
    c_2ab88b03 timestamptz NOT NULL,
    c_7d127adf timestamptz NULL,
    c_1fef066e smallint NULL,
    c_7239e57c smallint NOT NULL,
    CONSTRAINT k_2dabc43f PRIMARY KEY (c_43661a7f),
    CONSTRAINT k_72c79840 FOREIGN KEY (c_fcb7e5fc) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_025e9ec5 UNIQUE (c_29e9c869),
    CONSTRAINT k_94e1ff35 CHECK (octet_length(c_29e9c869) = 32),
    CONSTRAINT k_31ff7b77 CHECK (c_7239e57c BETWEEN 0 AND 1),
    CONSTRAINT k_47a2a2e9 CHECK ((c_7d127adf IS NULL) = (c_1fef066e IS NULL) AND (c_1fef066e IS NULL OR c_1fef066e BETWEEN 0 AND 2))
);

CREATE INDEX k_a3b3758c ON t_5a3ce63e (c_fcb7e5fc);

-- Failed-login throttling. key_hash is a SHA-256 of the throttled key, never the key itself.
-- scope: 0 Login identifier (tracked whether or not the account exists), 1 Source address.
CREATE TABLE t_3d3d01d6 (
    c_13311537 smallint NOT NULL,
    c_f17b49b8 bytea NOT NULL,
    c_9472ec58 integer NOT NULL,
    c_e420b5b6 timestamptz NOT NULL,
    c_2dedbd25 timestamptz NULL,
    CONSTRAINT k_251c8a44 PRIMARY KEY (c_13311537, c_f17b49b8),
    CONSTRAINT k_46e44b97 CHECK (c_13311537 BETWEEN 0 AND 1),
    CONSTRAINT k_1af200ce CHECK (octet_length(c_f17b49b8) = 32)
);

-- Explicit user-to-company association (no automatic rows; roles/permissions come with a later RBAC phase).
CREATE TABLE t_a071e305 (
    c_72706bb3 uuid NOT NULL,
    c_8c6c1d90 uuid NOT NULL,
    c_d156ceff timestamptz NOT NULL,
    CONSTRAINT k_ca5aeefe PRIMARY KEY (c_72706bb3, c_8c6c1d90),
    CONSTRAINT k_35815ced FOREIGN KEY (c_72706bb3) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_f4bd087d FOREIGN KEY (c_8c6c1d90) REFERENCES t_f446f4d0 (c_a9a73285)
);
