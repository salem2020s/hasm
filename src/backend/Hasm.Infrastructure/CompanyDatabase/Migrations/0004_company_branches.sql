-- Company database migration 0004: branches (Phase 3.1B).
-- Physical identifiers come from CompanySchema.Branch (generated, opaque); this file is immutable once applied (checksummed).
-- Adds the Branch table, its integrity triggers, its append-only revision history, and the main branch of the company. Existing company data is not read or modified.
-- Privileges come only from 'Hasm.Admin grant-company-runtime' (see CompanyRuntimePrivileges); this file contains none.
--
-- Invariants enforced by PostgreSQL (not by the application):
--   * code: normalised upper case, unique (so unique case-insensitively), immutable;
--   * at most one main branch (partial unique index) and exactly one active main branch at commit (deferred constraint trigger);
--   * a main branch is always active; branches are never deleted or truncated (owner included);
--   * revision advances by exactly one per update; the activation epoch advances by exactly one on every change of the active flag
--     and never otherwise (a stale assignment can therefore never match a later activation).
-- The main branch is created by this migration (Option A): it applies to a new company at provisioning (before the company is Ready)
-- and to an existing company at migrate-companies. It is NOT a user action, so no business audit event is fabricated: its provenance is
-- seed_migration = 4 (the version recorded in the migration journal). Its revision is 1 and no ConfigRevision row exists for revision 1;
-- the first audited change writes revision 2.

CREATE TABLE t_8ba6c27d (
    c_358158d2 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_b6f1610c varchar(20) NOT NULL,
    c_867f4322 varchar(200) NULL,
    c_5fb942ca varchar(200) NULL,
    c_6605fbbc boolean NOT NULL DEFAULT true,
    c_800064c9 boolean NOT NULL DEFAULT false,
    c_ca87abff bigint NOT NULL DEFAULT 1,
    c_dfcc1567 varchar(200) NULL,
    c_f2e7f7b2 varchar(200) NULL,
    c_021a26d9 varchar(100) NULL,
    c_93898dcd varchar(100) NULL,
    c_656adcd4 varchar(20) NULL,
    c_85e55384 varchar(30) NULL,
    c_b06bd18e varchar(254) NULL,
    c_18b78b39 varchar(200) NULL,
    c_6c940bd3 smallint NULL,
    c_7bf26af0 bigint NOT NULL DEFAULT 1,
    c_fd99167e timestamptz NOT NULL,
    c_2e3703ee timestamptz NOT NULL,
    CONSTRAINT k_148f3917 PRIMARY KEY (c_358158d2),
    CONSTRAINT k_b4deb97b CHECK (c_b6f1610c ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT k_eb34de98 CHECK (
        (c_867f4322 IS NOT NULL OR c_5fb942ca IS NOT NULL)
        AND (c_867f4322 IS NULL OR char_length(btrim(c_867f4322)) BETWEEN 1 AND 200)
        AND (c_5fb942ca IS NULL OR char_length(btrim(c_5fb942ca)) BETWEEN 1 AND 200)),
    CONSTRAINT k_b6b8f9bf CHECK (NOT c_800064c9 OR c_6605fbbc),
    CONSTRAINT k_dd62a9c0 CHECK (c_ca87abff >= 1),
    CONSTRAINT k_dc2b3047 CHECK (c_7bf26af0 > 0),
    CONSTRAINT k_90ce2e86 CHECK (c_6c940bd3 IS NULL OR c_6c940bd3 = 4)
);
-- Case-insensitive uniqueness of the code within the company (the check constraint already forces upper case).
CREATE UNIQUE INDEX k_24dea8d2 ON t_8ba6c27d (upper(c_b6f1610c));
-- At most one main branch.
CREATE UNIQUE INDEX k_d9b51fde ON t_8ba6c27d (c_800064c9) WHERE c_800064c9;

-- Row guard: no deletion, immutable identity columns, revision fence, activation-epoch rule.
CREATE FUNCTION f_f2c1b913() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'branches cannot be deleted'; END IF;
    IF NEW.c_358158d2 IS DISTINCT FROM OLD.c_358158d2 THEN RAISE EXCEPTION 'branch identifiers are immutable'; END IF;
    IF NEW.c_b6f1610c IS DISTINCT FROM OLD.c_b6f1610c THEN RAISE EXCEPTION 'branch codes are immutable'; END IF;
    IF NEW.c_fd99167e IS DISTINCT FROM OLD.c_fd99167e OR NEW.c_6c940bd3 IS DISTINCT FROM OLD.c_6c940bd3 THEN
        RAISE EXCEPTION 'branch provenance is immutable';
    END IF;
    IF NEW.c_7bf26af0 <> OLD.c_7bf26af0 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NEW.c_6605fbbc IS DISTINCT FROM OLD.c_6605fbbc THEN
        IF NEW.c_ca87abff <> OLD.c_ca87abff + 1 THEN RAISE EXCEPTION 'activation epoch must advance by one on activation or deactivation'; END IF;
    ELSIF NEW.c_ca87abff <> OLD.c_ca87abff THEN
        RAISE EXCEPTION 'activation epoch changes only with the active flag';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_21c6ea13 BEFORE UPDATE OR DELETE ON t_8ba6c27d FOR EACH ROW EXECUTE FUNCTION f_f2c1b913();
CREATE TRIGGER r_55d4e2e7 BEFORE TRUNCATE ON t_8ba6c27d FOR EACH STATEMENT EXECUTE FUNCTION f_f2c1b913();

-- Exactly one (active) main branch at commit. Deferred so that a main-branch reassignment (unset the old, set the new) is atomic.
-- The partial unique index above covers "at most one" immediately; this covers "at least one" for every insert or update.
CREATE FUNCTION f_2ae6347c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT count(*) FROM t_8ba6c27d WHERE c_800064c9 AND c_6605fbbc) <> 1 THEN
        RAISE EXCEPTION 'exactly one active main branch is required';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_30431c75 AFTER INSERT OR UPDATE ON t_8ba6c27d
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_2ae6347c();

REVOKE ALL ON TABLE t_8ba6c27d FROM PUBLIC;

-- Branch revision history (append-only), the sibling of ConfigRevision (migration 0003) for branches: one row per audited change, linked
-- to exactly one authenticated business audit event written in the SAME transaction (composite foreign key, unique) and to its branch.
-- (ConfigRevision itself is untouched: widening its entity vocabulary would need a destructive ALTER, which the migration rules forbid.)
CREATE TABLE t_cd2deb17 (
    c_61c1cc7d bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_ef189f30 uuid NOT NULL,
    c_bcd685d4 bigint NOT NULL,
    c_6f8886b4 uuid NOT NULL,
    c_43fb2a4b timestamptz NOT NULL,
    c_30b95919 jsonb NOT NULL,
    c_5bb64bd0 varchar(48) NOT NULL,
    c_5cca8b25 bigint NOT NULL,
    CONSTRAINT k_1c3524fa PRIMARY KEY (c_61c1cc7d),
    CONSTRAINT k_5b1819b6 UNIQUE (c_ef189f30, c_bcd685d4),
    CONSTRAINT k_095db982 UNIQUE (c_5bb64bd0, c_5cca8b25),
    CONSTRAINT k_388a9277 FOREIGN KEY (c_ef189f30) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_b8e1b7fd FOREIGN KEY (c_5bb64bd0, c_5cca8b25) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    -- Revision 1 of the seeded main branch has no row (it is created by the migration, not by a user action); the first audited change is revision 2.
    CONSTRAINT k_f0445506 CHECK (c_bcd685d4 > 0 AND jsonb_typeof(c_30b95919) = 'object')
);
CREATE FUNCTION f_a74edb08() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'branch revisions are append-only';
END;
$$;
CREATE TRIGGER r_e5e0dff0 BEFORE UPDATE OR DELETE ON t_cd2deb17 FOR EACH ROW EXECUTE FUNCTION f_a74edb08();
CREATE TRIGGER r_2dfce87e BEFORE TRUNCATE ON t_cd2deb17 FOR EACH STATEMENT EXECUTE FUNCTION f_a74edb08();

REVOKE ALL ON TABLE t_cd2deb17 FROM PUBLIC;

-- The main branch (Arabic text written as Unicode escapes in logical order: الفرع الرئيسي). Only when the company has no branch.
INSERT INTO t_8ba6c27d (c_b6f1610c, c_867f4322, c_5fb942ca, c_6605fbbc, c_800064c9, c_ca87abff, c_6c940bd3, c_7bf26af0, c_fd99167e, c_2e3703ee)
SELECT 'MAIN', U&'\0627\0644\0641\0631\0639 \0627\0644\0631\0626\064A\0633\064A', 'Main Branch', true, true, 1, 4, 1, now(), now()
WHERE NOT EXISTS (SELECT 1 FROM t_8ba6c27d);

-- Initialization guard: the migration (and therefore provisioning or migrate-companies for this company) rolls back unless the
-- company ends with exactly one active main branch.
DO $$
BEGIN
    IF (SELECT count(*) FROM t_8ba6c27d WHERE c_800064c9 AND c_6605fbbc) <> 1 THEN
        RAISE EXCEPTION 'branch initialization failed: exactly one active main branch is required';
    END IF;
END;
$$;
