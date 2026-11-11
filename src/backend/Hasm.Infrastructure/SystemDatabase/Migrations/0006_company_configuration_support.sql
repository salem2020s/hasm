-- Migration 0006: company configuration support (Phase 3.1A-2).
-- (1) Nullable display-name PROJECTION on the company registry. The company database stays authoritative: these columns only let the
--     company-selection screen show a name to authorized members without opening every company database. Writers must use
--     'WHERE display_name_revision < @new' (monotonic), so a delayed update can never overwrite newer information.
-- (2) The company administration permission codes, added to the preset roles. Preset roles are immutable through the application, so the
--     immutability trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
ALTER TABLE t_f446f4d0
    ADD COLUMN c_5d1a9b34 varchar(200) NULL,
    ADD COLUMN c_b7e20c68 varchar(200) NULL,
    ADD COLUMN c_41f8d6a2 bigint NOT NULL DEFAULT 0;

ALTER TABLE t_f446f4d0
    ADD CONSTRAINT k_9c3e7f15 CHECK (c_41f8d6a2 >= 0
        AND (c_41f8d6a2 > 0 OR (c_5d1a9b34 IS NULL AND c_b7e20c68 IS NULL))
        AND (c_5d1a9b34 IS NULL OR char_length(c_5d1a9b34) BETWEEN 1 AND 200)
        AND (c_b7e20c68 IS NULL OR char_length(c_b7e20c68) BETWEEN 1 AND 200));

-- Database-level monotonic fence for the projection: the revision never decreases and the names change only together with a NEWER revision,
-- so even a buggy or compromised writer holding column-level UPDATE cannot roll a projection back or alter a name at the same revision.
CREATE FUNCTION f_7a2c9e41() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_41f8d6a2 < OLD.c_41f8d6a2 THEN
        RAISE EXCEPTION 'display name revision cannot decrease';
    END IF;
    IF NEW.c_41f8d6a2 = OLD.c_41f8d6a2 AND (NEW.c_5d1a9b34 IS DISTINCT FROM OLD.c_5d1a9b34 OR NEW.c_b7e20c68 IS DISTINCT FROM OLD.c_b7e20c68) THEN
        RAISE EXCEPTION 'display names change only with a newer revision';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_3d8b5f20 BEFORE UPDATE ON t_f446f4d0 FOR EACH ROW EXECUTE FUNCTION f_7a2c9e41();

ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.profile.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.profile.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.settings.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.settings.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.currencies.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.currencies.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.config.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000003', 'company.config.view');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
