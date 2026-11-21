-- Migration 0030: retention of removed attachments (Phase R slice R1, decision D-082). Adds company.attachmentretention.view, configure, hold and execute to the preset Company Administrator role and creates the
-- Retention Approver preset (view + approve). Approve (approving a policy version, revoking it, releasing a legal hold, approving a purge plan) is NOT in the Company Administrator preset (separation of duties): the Retention
-- Approver is a SENSITIVE grant, delegated offline through the administrative tool because a company administrator cannot grant a permission they do not hold. Preset roles are immutable through the application, so the
-- immutability triggers are paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
ALTER TABLE t_359332ac DISABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_359332ac (c_59b19fa5, c_0f3f541d, c_65735af7, c_bb4f89f0, c_64bdf264, c_9f7e535b, c_c093e1d1, c_1dd9dad5) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000008', NULL, 'retention-approver', 'Retention Approver', true, now(), NULL, now());

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachmentretention.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachmentretention.configure'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachmentretention.hold'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.attachmentretention.execute'),
    ('5c1b0f6e-0000-4a00-8000-000000000008', 'company.attachmentretention.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000008', 'company.attachmentretention.approve');

ALTER TABLE t_359332ac ENABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
