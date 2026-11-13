-- Migration 0011: General Ledger foundation permissions (Phase 3.2B). Extended slice by slice before the phase is merged.
-- Adds the permission codes to the preset Company Administrator role. Preset roles are immutable through the application, so the immutability
-- trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
-- company.journal.approve is NOT in the Company Administrator preset; it belongs to the Journal Approver preset and to custom roles.
ALTER TABLE t_359332ac DISABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

-- Journal Approver: the delegated preset that can approve journal entries (approval is deliberately NOT part of Company Administrator; separation of duties is enforced per entry).
INSERT INTO t_359332ac (c_59b19fa5, c_0f3f541d, c_65735af7, c_bb4f89f0, c_64bdf264, c_9f7e535b, c_c093e1d1, c_1dd9dad5) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000005', NULL, 'journal-approver', 'Journal Approver', true, now(), NULL, now());

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.numbering.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.numbering.configure'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.exchangerates.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.exchangerates.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.costcenters.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.costcenters.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.costcenters.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.costcenters.activate'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.costcenters.recode'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.costcenters.restructure'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.approvalpolicies.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.approvalpolicies.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.approvalpolicies.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.approvalpolicies.activate'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.journal.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.journal.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.journal.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.journal.submit'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.journal.post'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.journal.reverse'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.journalsoftclosed.post'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.journalrate.override'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.ledgersettings.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.ledgersettings.configure'),
    ('5c1b0f6e-0000-4a00-8000-000000000005', 'company.journal.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000005', 'company.journal.approve');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
ALTER TABLE t_359332ac ENABLE TRIGGER r_d3408f58;
