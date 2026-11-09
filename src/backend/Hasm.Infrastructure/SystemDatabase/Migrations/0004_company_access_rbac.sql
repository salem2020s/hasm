-- Migration 0004: company access and role-based access control (system database).
-- Physical identifiers come from SystemSchema (Role, RolePermission, RoleAssignment, AccessAudit, plus additions to
-- UserCompanyAccess and Session); this file is immutable once applied (checksummed).
-- Authorization metadata is central: users, memberships, roles and assignments reference companies by their logical id only.
-- Branch and warehouse scope ids are opaque uuids of per-company data and therefore cannot carry foreign keys.

-- Membership lifecycle: a membership is revoked (never deleted) so history stays reconstructable. Rows created by earlier phases (none by code) stay valid.
ALTER TABLE t_a071e305
    ADD COLUMN c_2e108925 uuid NULL,
    ADD COLUMN c_79ae7bab timestamptz NULL,
    ADD COLUMN c_cf899c17 uuid NULL,
    ADD CONSTRAINT k_0bbbaa10 FOREIGN KEY (c_2e108925) REFERENCES t_fc83d26e (c_74b9e5ad),
    ADD CONSTRAINT k_4b8ded2d FOREIGN KEY (c_cf899c17) REFERENCES t_fc83d26e (c_74b9e5ad);

-- Company-selection context: the company a session currently works in. Chosen only through a server-side membership check and re-validated on every request.
ALTER TABLE t_5a3ce63e
    ADD COLUMN c_f26c54a5 uuid NULL,
    ADD CONSTRAINT k_ba7a6e5a FOREIGN KEY (c_f26c54a5) REFERENCES t_f446f4d0 (c_a9a73285);

-- Roles. Preset roles (company id NULL, preset key set) ship with the software, are visible to every company and are immutable.
-- Custom roles belong to exactly one company. Roles are never deleted; deactivated roles grant nothing.
CREATE TABLE t_359332ac (
    c_59b19fa5 uuid NOT NULL,
    c_0f3f541d uuid NULL,
    c_65735af7 varchar(64) NULL,
    c_bb4f89f0 varchar(100) NOT NULL,
    c_64bdf264 boolean NOT NULL,
    c_9f7e535b timestamptz NOT NULL,
    c_c093e1d1 uuid NULL,
    c_1dd9dad5 timestamptz NOT NULL,
    CONSTRAINT k_ce37d6de PRIMARY KEY (c_59b19fa5),
    CONSTRAINT k_1813aa7a FOREIGN KEY (c_0f3f541d) REFERENCES t_f446f4d0 (c_a9a73285),
    CONSTRAINT k_3376e530 FOREIGN KEY (c_c093e1d1) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_88edaaae CHECK ((c_65735af7 IS NULL) = (c_0f3f541d IS NOT NULL)),
    CONSTRAINT k_e35e76dd CHECK (char_length(btrim(c_bb4f89f0)) >= 1),
    CONSTRAINT k_e7e6c274 UNIQUE (c_65735af7)
);

-- Role names are unique per company (case-insensitive); preset names form their own namespace.
CREATE UNIQUE INDEX k_8bc611fb ON t_359332ac (coalesce(c_0f3f541d, '00000000-0000-0000-0000-000000000000'::uuid), lower(c_bb4f89f0));

-- Permissions of a role: structured codes 'resource.action' validated against the code catalog by the Backend.
CREATE TABLE t_3e3d4119 (
    c_0258a1a8 uuid NOT NULL,
    c_27be2f9e varchar(96) NOT NULL,
    CONSTRAINT k_31fe7478 PRIMARY KEY (c_0258a1a8, c_27be2f9e),
    CONSTRAINT k_2522e0fd FOREIGN KEY (c_0258a1a8) REFERENCES t_359332ac (c_59b19fa5),
    CONSTRAINT k_42a38028 CHECK (c_27be2f9e ~ '^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*){1,3}$')
);

-- Role assignments: a user holds a role in one company with a scope (0 Company-wide, 1 Branch, 2 Warehouse; scope id only for 1 and 2).
-- An assignment requires a membership row; Backend authorization additionally requires the membership to be active.
CREATE TABLE t_48ebc0f2 (
    c_11c1f4c7 uuid NOT NULL,
    c_96082bd4 uuid NOT NULL,
    c_bc3a9c98 uuid NOT NULL,
    c_e3896a58 uuid NOT NULL,
    c_c7cc8c47 smallint NOT NULL,
    c_162127ba uuid NULL,
    c_216ac47e timestamptz NOT NULL,
    c_42fb57a8 uuid NULL,
    c_e4e52db8 timestamptz NULL,
    c_9f9a9d22 uuid NULL,
    CONSTRAINT k_283ee178 PRIMARY KEY (c_11c1f4c7),
    CONSTRAINT k_0147ffd4 FOREIGN KEY (c_96082bd4, c_bc3a9c98) REFERENCES t_a071e305 (c_72706bb3, c_8c6c1d90),
    CONSTRAINT k_1d3c1ee7 FOREIGN KEY (c_e3896a58) REFERENCES t_359332ac (c_59b19fa5),
    CONSTRAINT k_3324fb20 FOREIGN KEY (c_42fb57a8) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_b500a403 FOREIGN KEY (c_9f9a9d22) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_c3c0623f CHECK ((c_c7cc8c47 = 0 AND c_162127ba IS NULL) OR (c_c7cc8c47 IN (1, 2) AND c_162127ba IS NOT NULL)),
    CONSTRAINT k_77a2fff4 CHECK (c_9f9a9d22 IS NULL OR c_e4e52db8 IS NOT NULL)
);

-- At most one ACTIVE assignment per (user, company, role, scope).
CREATE UNIQUE INDEX k_faf323d8 ON t_48ebc0f2
    (c_96082bd4, c_bc3a9c98, c_e3896a58, c_c7cc8c47, coalesce(c_162127ba, '00000000-0000-0000-0000-000000000000'::uuid))
    WHERE c_e4e52db8 IS NULL;
CREATE INDEX k_5f919f68 ON t_48ebc0f2 (c_bc3a9c98, c_96082bd4);

-- Append-only audit trail of access changes. event: 1 MembershipGranted, 2 MembershipRevoked, 3 RoleCreated,
-- 4 RolePermissionsChanged, 5 RoleDeactivated, 6 RoleAssigned, 7 RoleAssignmentRevoked. Actor NULL = offline administrative tool.
-- Detail holds short structured text (permission codes, scope), never secrets.
CREATE TABLE t_dcb6ad92 (
    c_a8808fec bigint GENERATED ALWAYS AS IDENTITY,
    c_b8a31332 timestamptz NOT NULL,
    c_627d2433 uuid NULL,
    c_19755f4e uuid NOT NULL,
    c_dfddf84e uuid NULL,
    c_a664f509 uuid NULL,
    c_b052e578 smallint NOT NULL,
    c_9a300568 varchar(1000) NULL,
    CONSTRAINT k_bc822b29 PRIMARY KEY (c_a8808fec),
    CONSTRAINT k_2272e470 FOREIGN KEY (c_627d2433) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_df82964a FOREIGN KEY (c_19755f4e) REFERENCES t_f446f4d0 (c_a9a73285),
    CONSTRAINT k_e9e81c87 FOREIGN KEY (c_dfddf84e) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_4e027754 CHECK (c_b052e578 BETWEEN 1 AND 7),
    CONSTRAINT k_8415bbea CHECK (c_9a300568 IS NULL OR char_length(c_9a300568) <= 1000)
);
CREATE INDEX k_f3321107 ON t_dcb6ad92 (c_19755f4e, c_a8808fec);

CREATE FUNCTION f_6521eb62() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'access audit rows are append-only';
END;
$$;
CREATE TRIGGER r_1edfb742 BEFORE UPDATE OR DELETE ON t_dcb6ad92
    FOR EACH ROW EXECUTE FUNCTION f_6521eb62();

-- Preset roles. Fixed ids; permission sets must equal PresetRoles in the Backend catalog (pinned by a test).
INSERT INTO t_359332ac (c_59b19fa5, c_0f3f541d, c_65735af7, c_bb4f89f0, c_64bdf264, c_9f7e535b, c_c093e1d1, c_1dd9dad5) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', NULL, 'company-administrator', 'Company Administrator', true, now(), NULL, now()),
    ('5c1b0f6e-0000-4a00-8000-000000000002', NULL, 'access-manager', 'Access Manager', true, now(), NULL, now()),
    ('5c1b0f6e-0000-4a00-8000-000000000003', NULL, 'access-auditor', 'Access Auditor', true, now(), NULL, now());

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.members.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.members.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.members.delete'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.roles.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.roles.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.roles.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.roles.delete'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.assignments.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.assignments.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.assignments.delete'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'access.audit.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000002', 'access.members.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000002', 'access.members.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000002', 'access.members.delete'),
    ('5c1b0f6e-0000-4a00-8000-000000000002', 'access.roles.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000002', 'access.assignments.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000002', 'access.assignments.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000002', 'access.assignments.delete'),
    ('5c1b0f6e-0000-4a00-8000-000000000003', 'access.members.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000003', 'access.roles.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000003', 'access.assignments.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000003', 'access.audit.view');

-- Immutability guards, created after seeding so that only the seed itself may write preset rows.
CREATE FUNCTION f_8131c8b7() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'roles cannot be deleted';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_65735af7 IS NOT NULL THEN
            RAISE EXCEPTION 'preset roles are immutable';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.c_65735af7 IS NOT NULL THEN
        RAISE EXCEPTION 'preset roles are immutable';
    END IF;
    IF NEW.c_59b19fa5 IS DISTINCT FROM OLD.c_59b19fa5 OR NEW.c_0f3f541d IS DISTINCT FROM OLD.c_0f3f541d
       OR NEW.c_65735af7 IS DISTINCT FROM OLD.c_65735af7 THEN
        RAISE EXCEPTION 'role identity is immutable';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d3408f58 BEFORE INSERT OR UPDATE OR DELETE ON t_359332ac
    FOR EACH ROW EXECUTE FUNCTION f_8131c8b7();

CREATE FUNCTION f_73f996bc() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    target uuid;
BEGIN
    IF TG_OP = 'INSERT' THEN target := NEW.c_0258a1a8; ELSE target := OLD.c_0258a1a8; END IF;
    IF EXISTS (SELECT 1 FROM t_359332ac WHERE c_59b19fa5 = target AND c_65735af7 IS NOT NULL) THEN
        RAISE EXCEPTION 'preset role permissions are immutable';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'role permissions are replaced, not updated';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_2601e368 BEFORE INSERT OR UPDATE OR DELETE ON t_3e3d4119
    FOR EACH ROW EXECUTE FUNCTION f_73f996bc();

-- A role may only be assigned in the company that owns it (preset roles: any company); the assigned user/company/role/scope are immutable.
CREATE FUNCTION f_69804ba6() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'role assignments are revoked, not deleted';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NOT EXISTS (SELECT 1 FROM t_359332ac WHERE c_59b19fa5 = NEW.c_e3896a58
                       AND (c_0f3f541d IS NULL OR c_0f3f541d = NEW.c_bc3a9c98)) THEN
            RAISE EXCEPTION 'role does not belong to this company';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.c_11c1f4c7 IS DISTINCT FROM OLD.c_11c1f4c7 OR NEW.c_96082bd4 IS DISTINCT FROM OLD.c_96082bd4
       OR NEW.c_bc3a9c98 IS DISTINCT FROM OLD.c_bc3a9c98 OR NEW.c_e3896a58 IS DISTINCT FROM OLD.c_e3896a58
       OR NEW.c_c7cc8c47 IS DISTINCT FROM OLD.c_c7cc8c47 OR NEW.c_162127ba IS DISTINCT FROM OLD.c_162127ba THEN
        RAISE EXCEPTION 'role assignment identity is immutable';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_3bdbbc04 BEFORE INSERT OR UPDATE OR DELETE ON t_48ebc0f2
    FOR EACH ROW EXECUTE FUNCTION f_69804ba6();
