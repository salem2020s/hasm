-- Migration 0017: inventory accounting configuration permissions (Phase 3.7A). Adds the permission codes to the preset Company Administrator role.
-- Preset roles are immutable through the application, so the immutability trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.inventoryaccounting.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.inventoryaccounting.configure');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
