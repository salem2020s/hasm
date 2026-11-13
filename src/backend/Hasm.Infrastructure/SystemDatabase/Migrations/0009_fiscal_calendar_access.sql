-- Migration 0009: fiscal calendar permissions and the Fiscal Approver role (Phase 3.1D).
-- (1) The routine fiscal permissions and the right to REQUEST an exceptional reopening are added to the preset Company Administrator.
-- (2) The right to APPROVE an exceptional reopening (company.fiscalreopen.approve) is deliberately NOT given to the Company Administrator
--     (segregation of duties: the people who run the calendar and request reopenings do not approve them). It belongs to the new preset role
--     Fiscal Approver. An ordinary administrator cannot assign that role (the service refuses to grant permissions the actor does not hold);
--     it is delegated only through the offline administrative tool (grant-fiscal-approver), which is audited and also serves as the recovery
--     procedure for a company with no configured approver.
-- Preset roles are immutable through the application, so the immutability triggers are paused for exactly this versioned change, inside this
-- transaction, and re-enabled before it ends.
ALTER TABLE t_359332ac DISABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_359332ac (c_59b19fa5, c_0f3f541d, c_65735af7, c_bb4f89f0, c_64bdf264, c_9f7e535b, c_c093e1d1, c_1dd9dad5) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000004', NULL, 'fiscal-approver', 'Fiscal Approver', true, now(), NULL, now());

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalyears.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalyears.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalyears.close'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalperiods.close'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalperiods.finalclose'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalperiods.reopen'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalreopen.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalreopen.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000004', 'company.fiscalreopen.approve'),
    ('5c1b0f6e-0000-4a00-8000-000000000004', 'company.fiscalreopen.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000004', 'company.fiscalyears.view');

ALTER TABLE t_359332ac ENABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
