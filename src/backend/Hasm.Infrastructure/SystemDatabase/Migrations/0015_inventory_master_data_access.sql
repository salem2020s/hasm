-- Migration 0015: inventory master data permissions (Phase 3.4). Adds the permission codes to the preset Company Administrator role.
-- Preset roles are immutable through the application, so the immutability trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.units.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.units.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.units.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.units.activate'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.itemgroups.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.itemgroups.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.itemgroups.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.itemgroups.activate'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.items.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.items.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.items.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.items.activate'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.barcodes.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.barcodes.activate'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.inventorysettings.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.inventorysettings.configure');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
