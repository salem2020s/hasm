-- Migration 0025: reusable import mapping templates (Phase 3.9C, OD-9 default). Forward-only, additive, nothing seeded, no existing data read or modified.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0025-import-templates.cjs (logical dictionary: CompanySchema.ImportTemplates.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (REPORTING-REQUIREMENTS 3 item 11, ROADMAP 3.9C, OD-9): a company-wide, per-adapter reusable mapping of FILE HEADERS to adapter fields (never column positions), versioned. It is
-- CONFIGURATION: applying a template only pre-fills the ordinary mapping of a batch; validation, dry run, approval and the plan hash are untouched, so a template can never skip them.
-- Invariants enforced by PostgreSQL: a template version is append-only (no update, delete or truncate); versions of one (adapter, name) are 1, 2, 3 ... without gaps; retirement is a new version
-- flagged retired; the content is bounded; names and adapter codes are well formed.

CREATE TABLE t_7c92889b (
    c_7b83e55c uuid NOT NULL,
    c_5a376e25 varchar(60) NOT NULL,
    c_7f523729 varchar(100) NOT NULL,
    c_60a8ef08 integer NOT NULL,
    c_296ae911 boolean NOT NULL DEFAULT false,
    c_8fe0253a text NOT NULL,
    c_e0c74f0e uuid NOT NULL,
    c_963ec9e3 timestamptz NOT NULL,
    CONSTRAINT k_7c909779 PRIMARY KEY (c_7b83e55c),
    CONSTRAINT k_7bc9977c CHECK (c_60a8ef08 > 0 AND c_5a376e25 ~ '^[a-z][a-z0-9-]{1,59}$' AND char_length(btrim(c_7f523729)) BETWEEN 1 AND 100
        AND char_length(c_8fe0253a) BETWEEN 2 AND 65536)
);
CREATE UNIQUE INDEX k_550c1a5f ON t_7c92889b (c_5a376e25, c_7f523729, c_60a8ef08);

CREATE FUNCTION f_2a0c8237() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        -- the next version of a template is exactly one above the latest (serialized by the service per template; the unique index refuses a duplicate)
        IF NEW.c_60a8ef08 <> COALESCE((SELECT max(t.c_60a8ef08) FROM t_7c92889b t WHERE t.c_5a376e25 = NEW.c_5a376e25 AND t.c_7f523729 = NEW.c_7f523729), 0) + 1 THEN
            RAISE EXCEPTION 'a template version must be exactly one above the latest';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'import mapping template versions are append-only';
END;
$$;
CREATE TRIGGER r_34f6c2ad BEFORE INSERT OR UPDATE OR DELETE ON t_7c92889b FOR EACH ROW EXECUTE FUNCTION f_2a0c8237();
CREATE TRIGGER r_fbebafbb BEFORE TRUNCATE ON t_7c92889b FOR EACH STATEMENT EXECUTE FUNCTION f_2a0c8237();
REVOKE ALL ON TABLE t_7c92889b FROM PUBLIC;
