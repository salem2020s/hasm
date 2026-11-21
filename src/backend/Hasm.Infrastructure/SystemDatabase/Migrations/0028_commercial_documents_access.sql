-- Migration 0028: commercial document permissions (Phase 3.13 slice S1a). Adds the branch-scoped company.commercialdocs.view, create, update, submit and cancel permission codes to the preset Company Administrator role.
-- (Approval arrives with S1b and is deliberately not granted to that preset.) Preset roles are immutable through the application, so the immutability trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.commercialdocs.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.commercialdocs.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.commercialdocs.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.commercialdocs.submit'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.commercialdocs.cancel');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
