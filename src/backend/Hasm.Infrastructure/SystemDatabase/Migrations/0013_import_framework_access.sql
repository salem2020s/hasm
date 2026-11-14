-- Migration 0013: import framework permissions (Phase 3.2D). Extended slice by slice before the phase is merged.
-- Adds the permission codes to the preset Company Administrator role. Preset roles are immutable through the application, so the immutability
-- trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
-- company.imports.approve is NOT in the Company Administrator preset (approval of an import is a separate duty; the uploader and submitter can never approve their own import in any case);
-- it belongs to the Import Approver preset and, through it, to custom roles of people who hold it.
ALTER TABLE t_359332ac DISABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

-- Import Approver: the delegated preset that can approve imports (like Journal Approver, delegated offline by Hasm.Admin because an administrator cannot grant a permission they do not hold).
INSERT INTO t_359332ac (c_59b19fa5, c_0f3f541d, c_65735af7, c_bb4f89f0, c_64bdf264, c_9f7e535b, c_c093e1d1, c_1dd9dad5) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000006', NULL, 'import-approver', 'Import Approver', true, now(), NULL, now());

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.imports.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.imports.upload'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.imports.configure'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.imports.submit'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.imports.apply'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.imports.cancel'),
    ('5c1b0f6e-0000-4a00-8000-000000000006', 'company.imports.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000006', 'company.imports.approve');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
ALTER TABLE t_359332ac ENABLE TRIGGER r_d3408f58;
