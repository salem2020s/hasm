-- Migration 0012: cost-center-scoped role assignments (Phase 3.2C, slice S1).
-- The scope check of the assignment table of migration 0004 cannot be extended without a DROP (forbidden), so cost-center-scoped assignments live in their own
-- table with the same shape: a role granted to a company member for ONE cost center (and, by the evaluation rule, its descendants), recording the ACTIVATION
-- EPOCH of the cost center it was granted for (a deactivated-then-reactivated cost center never silently restores old access; evaluated on every request).
-- Revocation is a soft state change; rows are never deleted and nothing else changes. No data is migrated: no cost-center-scoped assignment can exist before.
CREATE TABLE t_83b6fff2 (
    c_c94f910f uuid NOT NULL,
    c_be1b97e6 uuid NOT NULL,
    c_8f432b32 uuid NOT NULL,
    c_4d6410a1 uuid NOT NULL,
    c_8fbf9b9d uuid NOT NULL,
    c_92465d23 bigint NOT NULL,
    c_c8ea3e0d timestamptz NOT NULL,
    c_3a2484e7 uuid NULL,
    c_7bddc39a timestamptz NULL,
    c_01c768f2 uuid NULL,
    CONSTRAINT k_8bb0ef86 PRIMARY KEY (c_c94f910f),
    CONSTRAINT k_dcf9432f FOREIGN KEY (c_be1b97e6, c_8f432b32) REFERENCES t_a071e305 (c_72706bb3, c_8c6c1d90),
    CONSTRAINT k_eb86a5f4 FOREIGN KEY (c_4d6410a1) REFERENCES t_359332ac (c_59b19fa5),
    CONSTRAINT k_bb9af908 FOREIGN KEY (c_3a2484e7) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_b3e94581 FOREIGN KEY (c_01c768f2) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_2a9e912a CHECK (c_01c768f2 IS NULL OR c_7bddc39a IS NOT NULL),
    CONSTRAINT k_9213224c CHECK (c_92465d23 >= 1)
);

-- At most one ACTIVE assignment per (user, company, role, cost center).
CREATE UNIQUE INDEX k_87e878b9 ON t_83b6fff2 (c_be1b97e6, c_8f432b32, c_4d6410a1, c_8fbf9b9d) WHERE c_7bddc39a IS NULL;
CREATE INDEX k_58461e5f ON t_83b6fff2 (c_8f432b32, c_be1b97e6);

CREATE FUNCTION f_b0860418() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'cost-center assignments are never deleted'; END IF;
    IF NEW.c_c94f910f IS DISTINCT FROM OLD.c_c94f910f OR NEW.c_be1b97e6 IS DISTINCT FROM OLD.c_be1b97e6 OR NEW.c_8f432b32 IS DISTINCT FROM OLD.c_8f432b32
       OR NEW.c_4d6410a1 IS DISTINCT FROM OLD.c_4d6410a1 OR NEW.c_8fbf9b9d IS DISTINCT FROM OLD.c_8fbf9b9d OR NEW.c_92465d23 IS DISTINCT FROM OLD.c_92465d23
       OR NEW.c_c8ea3e0d IS DISTINCT FROM OLD.c_c8ea3e0d OR NEW.c_3a2484e7 IS DISTINCT FROM OLD.c_3a2484e7 THEN
        RAISE EXCEPTION 'only the revocation of a cost-center assignment can change';
    END IF;
    IF OLD.c_7bddc39a IS NOT NULL THEN RAISE EXCEPTION 'a revoked cost-center assignment is final'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_e867423b BEFORE UPDATE OR DELETE ON t_83b6fff2 FOR EACH ROW EXECUTE FUNCTION f_b0860418();

-- Permissions of the cost-center accounting policies (3.2C S1) for the preset Company Administrator (the immutability trigger of preset role permissions is paused for exactly this
-- versioned change, inside this transaction, and re-enabled before it ends). Journal and ledger-report permissions already exist; cost-center-scoped assignment needs no new code.
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;
INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.costcenterpolicies.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.costcenterpolicies.configure'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalclosing.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalclosing.configure'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalclosing.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalclosing.reverse');
ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
