-- Migration 0026: attachment permissions (Phase 3.12). Adds the company-wide company.attachments.view|upload|delete and company.attachmentssensitive.view|upload permission codes to the preset Company
-- Administrator role. Preset roles are immutable through the application, so the immutability trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachments.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachments.upload'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachments.delete'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachmentssensitive.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachmentssensitive.upload');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
