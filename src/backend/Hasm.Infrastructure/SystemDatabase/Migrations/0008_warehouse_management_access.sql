-- Migration 0008: warehouse-scoped access (Phase 3.1C).
-- (1) Warehouse-scoped role assignments record the ACTIVATION EPOCH of the warehouse they were granted for, in their own column
--     (warehouse_scope_epoch: NULL unless the scope is Warehouse, required for warehouse scope, immutable). The branch column of migration 0007
--     (scope_epoch) is NOT altered: its CHECK constraint cannot be changed without a DROP, which the migration rules forbid.
--     A warehouse-scoped grant is effective only while the warehouse is active AND its current epoch equals the recorded one (evaluated on every
--     request by AccessService), so deactivating and reactivating a warehouse never silently restores old access.
--     The migration refuses to run if warehouse-scoped assignments already exist (none can: warehouses did not exist before this phase).
-- (2) The warehouse permission codes are added to the preset Company Administrator role (immutability trigger paused for exactly this versioned
--     change, inside this transaction, and re-enabled before it ends).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_48ebc0f2 WHERE c_c7cc8c47 = 2) THEN
        RAISE EXCEPTION 'warehouse-scoped role assignments already exist; migration 0008 refuses to continue';
    END IF;
END;
$$;

ALTER TABLE t_48ebc0f2 ADD COLUMN c_6bdbb40f bigint NULL;
ALTER TABLE t_48ebc0f2 ADD CONSTRAINT k_163f9a59 CHECK (
    (c_c7cc8c47 = 2 AND c_6bdbb40f IS NOT NULL AND c_6bdbb40f >= 1) OR (c_c7cc8c47 <> 2 AND c_6bdbb40f IS NULL));

CREATE FUNCTION f_248f918f() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_6bdbb40f IS DISTINCT FROM OLD.c_6bdbb40f THEN
        RAISE EXCEPTION 'the activation epoch of an assignment is immutable';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_771e0081 BEFORE UPDATE ON t_48ebc0f2 FOR EACH ROW EXECUTE FUNCTION f_248f918f();

ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.warehouses.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.warehouses.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.warehousestructure.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.warehousestructure.activate');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
