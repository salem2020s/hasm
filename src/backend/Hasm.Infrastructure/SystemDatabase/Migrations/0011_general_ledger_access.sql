-- Migration 0011: General Ledger foundation permissions (Phase 3.2B). Extended slice by slice before the phase is merged.
-- Adds the permission codes to the preset Company Administrator role. Preset roles are immutable through the application, so the immutability
-- trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends.
-- Approval and posting-related permissions that must be delegated (never held by the Company Administrator preset) are NOT added here.
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.numbering.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.numbering.configure');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
