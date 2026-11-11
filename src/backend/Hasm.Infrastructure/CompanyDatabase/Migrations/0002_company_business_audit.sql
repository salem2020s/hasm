-- Company database migration 0002: company-business cryptographic audit (Phase 3.1A-1).
-- Physical identifiers come from CompanySchema.BusinessAudit; this file is immutable once applied (checksummed).
-- Adds ONE append-only table that holds hash-chained, key-authenticated events of the stream 'business:<company id>'. It lives in the
-- company database so a business mutation and its audit event commit or roll back in the SAME PostgreSQL transaction. The event
-- format, hashing and authentication codes are those of the system audit (computed by the Backend with keys that never enter the
-- database). Existing company data is untouched: this migration only adds objects.
-- The table is owned by the provisioning/migration account. The API runtime account is granted SELECT and INSERT only
-- (never UPDATE, DELETE, TRUNCATE, ownership or DDL) by 'Hasm.Admin grant-company-runtime'.

CREATE TABLE t_2db8cf1f (
    c_839d1168 varchar(48) NOT NULL,
    c_689dc6c4 bigint NOT NULL,
    c_ef116a9d uuid NOT NULL,
    c_f02e2cd0 uuid NOT NULL,
    c_e01c5070 timestamptz NOT NULL,
    c_4c6c6496 smallint NOT NULL,
    c_b11011ac uuid NULL,
    c_deb363a5 uuid NULL,
    c_629e971c uuid NULL,
    c_9881ea62 varchar(1000) NULL,
    c_b4c43b5e smallint NOT NULL,
    c_0e8a7d69 varchar(32) NOT NULL,
    c_d8a88c1d bytea NOT NULL,
    c_a9921270 bytea NOT NULL,
    c_b0fdf392 bytea NOT NULL,
    CONSTRAINT k_00c2489b PRIMARY KEY (c_839d1168, c_689dc6c4),
    CONSTRAINT k_b10664e1 UNIQUE (c_ef116a9d),
    -- The stream is exactly the business stream of the company carried by the row (domain-separated from the 'company:' stream).
    CONSTRAINT k_18ddb3ac CHECK (c_839d1168 = 'business:' || replace(c_f02e2cd0::text, '-', '')),
    CONSTRAINT k_a89e988d CHECK (c_689dc6c4 >= 1 AND octet_length(c_d8a88c1d) = 32 AND octet_length(c_a9921270) = 32 AND octet_length(c_b0fdf392) = 32),
    CONSTRAINT k_00e125c1 CHECK (c_9881ea62 IS NULL OR char_length(c_9881ea62) <= 1000),
    -- Business event types start at 1000 (the range reserved for later phases).
    CONSTRAINT k_5837bd14 CHECK (c_4c6c6496 >= 1000 AND c_b4c43b5e >= 1 AND c_0e8a7d69 ~ '^[a-z0-9][a-z0-9-]{0,31}$')
);

-- Rows can only be appended. UPDATE, row DELETE and TRUNCATE are refused for every role including the owner. A superuser or a DBA who
-- disables the trigger is outside the database's own protection; such tampering is detected by the verifier through the keyed
-- authentication codes and the external checkpoints.
CREATE FUNCTION f_d0f1d840() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit events are append-only';
END;
$$;
CREATE TRIGGER r_4427e195 BEFORE UPDATE OR DELETE ON t_2db8cf1f
    FOR EACH ROW EXECUTE FUNCTION f_d0f1d840();
CREATE TRIGGER r_6b55b14e BEFORE TRUNCATE ON t_2db8cf1f
    FOR EACH STATEMENT EXECUTE FUNCTION f_d0f1d840();

-- An event can only be appended for the company this database belongs to (identity binding of migration 0001).
CREATE FUNCTION f_104f29c0() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_f02e2cd0 IS DISTINCT FROM (SELECT c_2d7eb73b FROM t_209d0f15) THEN
        RAISE EXCEPTION 'audit event does not belong to this company';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_44f2cd47 BEFORE INSERT ON t_2db8cf1f
    FOR EACH ROW EXECUTE FUNCTION f_104f29c0();

REVOKE ALL ON TABLE t_2db8cf1f FROM PUBLIC;
