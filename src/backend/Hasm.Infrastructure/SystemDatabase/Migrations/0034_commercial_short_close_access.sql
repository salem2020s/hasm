-- Migration 0034 (provisional number; the final number follows the merge order): the short-close permission (Phase 3.13, owner decision D-201). Adds the branch-scoped company.commercialdocs.close permission code to the preset
-- Company Administrator role, so that the action can be held and delegated by a role (the service refuses to grant permissions its actor does not hold); the action itself is audited, needs a reason and is never an approval.
-- Preset roles are immutable through the application, so the immutability trigger is paused for exactly this versioned change, inside this transaction, and re-enabled before it ends (the same pattern as migration 0028).
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.commercialdocs.close');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
