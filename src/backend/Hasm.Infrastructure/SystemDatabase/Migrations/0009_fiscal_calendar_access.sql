-- Migration 0009: fiscal calendar permissions (Phase 3.1D).
-- Adds the fiscal-calendar permission codes to the preset Company Administrator role. Two exceptional permissions are deliberately NOT added
-- (segregation of duties): company.fiscalperiods.reopenfinal (reopen a Final Closed period) and company.fiscalyears.reopen (reopen a closed year);
-- a custom role must hold them. Preset roles are immutable through the application, so the immutability trigger is paused for exactly this versioned
-- change, inside this transaction, and re-enabled before it ends.
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalyears.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalyears.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalyears.close'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalperiods.close'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalperiods.finalclose'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalperiods.reopen');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
