-- Migration 0010: Chart of Accounts permissions (Phase 3.2A).
-- Adds the company.accounts.* permission codes to the preset Company Administrator role (view, create, update, activate, re-code, restructure, configure).
-- Preset roles are immutable through the application, so the immutability trigger is paused for exactly this versioned change, inside this
-- transaction, and re-enabled before it ends.
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.accounts.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.accounts.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.accounts.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.accounts.activate'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.accounts.recode'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.accounts.restructure'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.accounts.configure');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
