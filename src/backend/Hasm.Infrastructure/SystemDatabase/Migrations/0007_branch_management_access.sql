-- Migration 0007: branch-scoped access (Phase 3.1B).
-- (1) Role assignments record the ACTIVATION EPOCH of the branch they were granted for (scope_epoch; NULL for company and warehouse scope,
--     required for branch scope, immutable). A branch-scoped grant is effective only while the branch is active AND its current epoch equals
--     the recorded one (evaluated on every request by AccessService), so deactivating and reactivating a branch never silently restores
--     old access: a new, explicit and authorized assignment is required.
--     The migration refuses to run if branch-scoped assignments already exist (none can: branches did not exist before this phase).
-- (2) The branch permission codes are added to the preset Company Administrator role. Preset roles are immutable through the application,
--     so the immutability trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_48ebc0f2 WHERE c_c7cc8c47 = 1) THEN
        RAISE EXCEPTION 'branch-scoped role assignments already exist; migration 0007 refuses to continue';
    END IF;
END;
$$;

ALTER TABLE t_48ebc0f2 ADD COLUMN c_f18dd4c7 bigint NULL;
ALTER TABLE t_48ebc0f2 ADD CONSTRAINT k_57b77fbb CHECK (
    (c_c7cc8c47 = 1 AND c_f18dd4c7 IS NOT NULL AND c_f18dd4c7 >= 1) OR (c_c7cc8c47 <> 1 AND c_f18dd4c7 IS NULL));

CREATE FUNCTION f_1f99d034() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_f18dd4c7 IS DISTINCT FROM OLD.c_f18dd4c7 THEN
        RAISE EXCEPTION 'the activation epoch of an assignment is immutable';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_1f9a28a4 BEFORE UPDATE ON t_48ebc0f2 FOR EACH ROW EXECUTE FUNCTION f_1f99d034();

ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.branches.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.branches.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.branchstructure.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.branchstructure.activate'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.branchstructure.designate');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
