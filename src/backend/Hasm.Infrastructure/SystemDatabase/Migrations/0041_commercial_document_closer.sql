-- Migration 0041: the short-close preset (Phase 3.13, owner decision D-201; owner decision X4-PRESET-ROLE, Option A, D-254; plan docs/X4-PRESET-ROLES-PLAN.md section 3.3).
-- Commercial Document Closer: view and close of commercial documents (company.commercialdocs.view, company.commercialdocs.close; both branch-scoped). NOT privileged (Close is not a privileged action and the resource is ordinary).
-- View is seeded with Close because the action is taken on a document the holder must read (its revision is the precondition of the request). The role holds no create, update, submit, approve or cancel permission.
-- The preset is not part of Company Administrator and is not assigned by this migration: it is assigned offline only (Hasm.Admin grant-commercial-document-closer). No existing role gains a permission.
-- Granting it activates no financial feature: a short close writes no delivery, invoice, stock movement or journal entry (D-201), and no document type is registered as short-closable until the concrete order types exist.
-- Preset roles and their permissions are immutable through the application, so BOTH immutability triggers are paused for exactly this versioned change (a role row and its permission rows are inserted), inside this transaction, and re-enabled before it ends.
ALTER TABLE t_359332ac DISABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_359332ac (c_59b19fa5, c_0f3f541d, c_65735af7, c_bb4f89f0, c_64bdf264, c_9f7e535b, c_c093e1d1, c_1dd9dad5) VALUES
    ('5c1b0f6e-0000-4a00-8000-00000000000c', NULL, 'commercial-document-closer', 'Commercial Document Closer', true, now(), NULL, now());

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-00000000000c', 'company.commercialdocs.view'),
    ('5c1b0f6e-0000-4a00-8000-00000000000c', 'company.commercialdocs.close');

ALTER TABLE t_359332ac ENABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
