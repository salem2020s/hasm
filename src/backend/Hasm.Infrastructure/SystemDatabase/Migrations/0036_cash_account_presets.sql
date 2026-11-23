-- Migration 0036 (provisional number; the final number follows the merge order): the two cash and bank account presets (Phase 3.14 slice S1; owner decision X4-PRESET-ROLE, Option A, 2026-11-22; plan docs/X4-PRESET-ROLES-PLAN.md section 3.2).
-- Cash Account Manager: view, create, update, activate. PRIVILEGED (update and activate are privileged, D-158), so mandatory MFA applies. Read reach: every cash and bank account of the branches the assignment covers; it cannot read a full account number.
-- Cash Account Sensitive Viewer: view and the sensitive view (the full number is shown only with company.cashaccountssensitive.view AND company.cashaccounts.view on the account's branch, D-155). PRIVILEGED (resource company.cashaccountssensitive). Read reach: every cash and bank
-- account of the branches the assignment covers, including the FULL account number; it holds no create, update or activate permission.
-- Neither preset is part of Company Administrator and neither is assigned by this migration: they are assigned offline only (Hasm.Admin grant-cash-account-manager / grant-cash-account-sensitive-viewer). Granting them activates no financial feature: cash and bank posting stays closed (D-214).
-- Preset roles and their permissions are immutable through the application, so BOTH immutability triggers are paused for exactly this versioned change (a role row and its permission rows are inserted), inside this transaction, and re-enabled before it ends.
ALTER TABLE t_359332ac DISABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_359332ac (c_59b19fa5, c_0f3f541d, c_65735af7, c_bb4f89f0, c_64bdf264, c_9f7e535b, c_c093e1d1, c_1dd9dad5) VALUES
    ('5c1b0f6e-0000-4a00-8000-00000000000a', NULL, 'cash-account-manager', 'Cash Account Manager', true, now(), NULL, now()),
    ('5c1b0f6e-0000-4a00-8000-00000000000b', NULL, 'cash-account-sensitive-viewer', 'Cash Account Sensitive Viewer', true, now(), NULL, now());

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-00000000000a', 'company.cashaccounts.view'),
    ('5c1b0f6e-0000-4a00-8000-00000000000a', 'company.cashaccounts.create'),
    ('5c1b0f6e-0000-4a00-8000-00000000000a', 'company.cashaccounts.update'),
    ('5c1b0f6e-0000-4a00-8000-00000000000a', 'company.cashaccounts.activate'),
    ('5c1b0f6e-0000-4a00-8000-00000000000b', 'company.cashaccounts.view'),
    ('5c1b0f6e-0000-4a00-8000-00000000000b', 'company.cashaccountssensitive.view');

ALTER TABLE t_359332ac ENABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
