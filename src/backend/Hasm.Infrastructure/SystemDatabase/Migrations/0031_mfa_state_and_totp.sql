-- Migration (system): MFA state and TOTP secrets (Phase 4.1 slice S1, docs/PHASE-4.1.md section 4, decisions D-080, D-096..D-107, D-120). Forward-only, additive: five columns and two constraints on the user table, ONE
-- new table; nothing existing is dropped or replaced (the second-factor failure counters reuse the login throttle table with domain-separated key hashes). Opaque physical names (D-029) are generated deterministically by
-- tools/schema/system-mfa.cjs (logical dictionary: SystemSchema.Mfa.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: the MFA requirement and enrolment STATE lives on the user row (the user row is in the system dump, so a restore never loses the fact that a user is enrolled): mfa_state 0 None, 1 EnrolmentRequired,
-- 2 Enrolled, 3 RecoveryRequired. Only the SECRET goes to the new secret store, which the backup run excludes (SystemSchema.SecretStoreTables, D-120). A user whose row says Enrolled but who has no stored secret is
-- RecoveryRequired and returns only through recovery or the DR procedure, never by enrolling with a password.

ALTER TABLE t_fc83d26e
    ADD COLUMN c_d6d40d4e smallint NOT NULL DEFAULT 0,
    ADD COLUMN c_41065070 timestamptz NULL,
    ADD COLUMN c_a67002ac bytea NULL,
    ADD COLUMN c_d4392f0b timestamptz NULL,
    ADD COLUMN c_2bc5bf3f timestamptz NULL;
ALTER TABLE t_fc83d26e ADD CONSTRAINT k_59603816 CHECK (c_d6d40d4e BETWEEN 0 AND 3);
-- the MFA state moves only along the graph (review M1; MfaStateGraph is the C# twin, a PostgreSQL test pins them equal): None -> EnrolmentRequired or Enrolled (a voluntary enrolment), EnrolmentRequired -> Enrolled,
-- Enrolled -> EnrolmentRequired (recovery, DR) or RecoveryRequired (restore), RecoveryRequired -> EnrolmentRequired (recovery, DR). Nothing ever returns to None: MFA is never silently off.
CREATE FUNCTION f_4dfde580() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_d6d40d4e IS DISTINCT FROM OLD.c_d6d40d4e AND NOT (
        (OLD.c_d6d40d4e = 0 AND NEW.c_d6d40d4e IN (1, 2))
        OR (OLD.c_d6d40d4e = 1 AND NEW.c_d6d40d4e = 2)
        OR (OLD.c_d6d40d4e = 2 AND NEW.c_d6d40d4e IN (1, 3))
        OR (OLD.c_d6d40d4e = 3 AND NEW.c_d6d40d4e = 1)) THEN
        RAISE EXCEPTION 'this MFA state change is not allowed';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_fcfa43d5 BEFORE UPDATE ON t_fc83d26e FOR EACH ROW EXECUTE FUNCTION f_4dfde580();

-- the one-time enrolment code of a bootstrap administrator (every other code lives in its approval request): a SHA-256 hash with an expiry, consumed at most once
ALTER TABLE t_fc83d26e ADD CONSTRAINT k_dc678b25 CHECK (
    (c_a67002ac IS NULL AND c_d4392f0b IS NULL AND c_2bc5bf3f IS NULL)
    OR (octet_length(c_a67002ac) = 32 AND c_d4392f0b IS NOT NULL));

-- TOTP secrets: AES-256-GCM ciphertext under a purpose-bound subkey of the protected key ring; the authenticated data is user id | key id | secret id. state: 0 Pending, 1 Active, 2 Revoked.
-- At most one Active and one Pending secret per user (database-enforced). Rows are never deleted; revocation is state 2.
CREATE TABLE t_47b9462a (
    c_1764cc53 uuid NOT NULL,
    c_f5576bf1 uuid NOT NULL,
    c_87be5b12 bytea NOT NULL,
    c_ab8b74c1 bytea NOT NULL,
    c_e6dc557f bytea NOT NULL,
    c_cdba63b1 varchar(32) NOT NULL,
    c_994e8047 smallint NOT NULL,
    c_34574856 timestamptz NOT NULL,
    c_28149c8a timestamptz NULL,
    c_b51a9e07 bigint NULL,
    c_03357409 integer NOT NULL DEFAULT 0,
    CONSTRAINT k_57b25bb5 PRIMARY KEY (c_1764cc53),
    CONSTRAINT k_0de134bd FOREIGN KEY (c_f5576bf1) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_3edd01ee CHECK (c_994e8047 IN (0, 1, 2)),
    CONSTRAINT k_e871c1d8 CHECK (octet_length(c_87be5b12) = 20 AND octet_length(c_ab8b74c1) = 12 AND octet_length(c_e6dc557f) = 16
        AND c_cdba63b1 ~ '^[a-z0-9][a-z0-9-]{0,31}$' AND c_03357409 >= 0),
    CONSTRAINT k_201e7f66 CHECK ((c_994e8047 = 0 AND c_28149c8a IS NULL) OR (c_994e8047 = 1 AND c_28149c8a IS NOT NULL) OR c_994e8047 = 2)
);
CREATE UNIQUE INDEX k_c60e68dc ON t_47b9462a (c_f5576bf1) WHERE c_994e8047 = 1;
CREATE UNIQUE INDEX k_58d7cfa8 ON t_47b9462a (c_f5576bf1) WHERE c_994e8047 = 0;

CREATE FUNCTION f_55ca2d2a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'TOTP secrets are never deleted or truncated (revoke instead)'; END IF;
    IF NEW.c_1764cc53 IS DISTINCT FROM OLD.c_1764cc53 OR NEW.c_f5576bf1 IS DISTINCT FROM OLD.c_f5576bf1 OR NEW.c_34574856 IS DISTINCT FROM OLD.c_34574856 THEN
        RAISE EXCEPTION 'the identity of a TOTP secret is immutable';
    END IF;
    -- the protected material changes only inside the rewrap procedure, which sets the transaction-local flag
    IF (NEW.c_87be5b12 IS DISTINCT FROM OLD.c_87be5b12 OR NEW.c_ab8b74c1 IS DISTINCT FROM OLD.c_ab8b74c1 OR NEW.c_e6dc557f IS DISTINCT FROM OLD.c_e6dc557f
        OR NEW.c_cdba63b1 IS DISTINCT FROM OLD.c_cdba63b1) AND COALESCE(current_setting('hasm.totp_rewrap', true), '') <> 'on' THEN
        RAISE EXCEPTION 'TOTP secret material changes only through rewrap';
    END IF;
    IF (OLD.c_b51a9e07 IS NOT NULL AND NEW.c_b51a9e07 IS NULL) OR (NEW.c_b51a9e07 IS NOT NULL AND OLD.c_b51a9e07 IS NOT NULL AND NEW.c_b51a9e07 < OLD.c_b51a9e07) THEN
        RAISE EXCEPTION 'the accepted TOTP step never decreases and is never cleared';
    END IF;
    IF NEW.c_994e8047 <> OLD.c_994e8047 AND NOT ((OLD.c_994e8047 = 0 AND NEW.c_994e8047 IN (1, 2)) OR (OLD.c_994e8047 = 1 AND NEW.c_994e8047 = 2)) THEN
        RAISE EXCEPTION 'a TOTP secret moves only from pending to active or revoked, and from active to revoked';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_695ff803 BEFORE UPDATE OR DELETE ON t_47b9462a FOR EACH ROW EXECUTE FUNCTION f_55ca2d2a();
CREATE TRIGGER r_1063c2f6 BEFORE TRUNCATE ON t_47b9462a FOR EACH STATEMENT EXECUTE FUNCTION f_55ca2d2a();
REVOKE ALL ON TABLE t_47b9462a FROM PUBLIC;
