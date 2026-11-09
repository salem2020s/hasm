-- Company database migration 0001: identity binding (minimal company-specific schema).
-- Physical identifiers come from CompanySchema; this file is immutable once applied (checksummed).
CREATE TABLE t_209d0f15 (
    c_8709851b boolean NOT NULL DEFAULT true,
    c_2d7eb73b uuid NOT NULL,
    c_a77d8875 timestamptz NOT NULL,
    CONSTRAINT k_bf65319b PRIMARY KEY (c_8709851b),
    CONSTRAINT k_8fd0da3b CHECK (c_8709851b)
);

-- Exactly one identity row, written once and never changed or removed.
CREATE FUNCTION f_0b15f60c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'company identity is immutable';
END;
$$;

CREATE TRIGGER r_ec68b932 BEFORE UPDATE OR DELETE ON t_209d0f15
    FOR EACH ROW EXECUTE FUNCTION f_0b15f60c();
