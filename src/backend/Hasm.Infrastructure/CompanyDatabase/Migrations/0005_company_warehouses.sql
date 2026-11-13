-- Company database migration 0005: warehouses (Phase 3.1C).
-- Physical identifiers come from CompanySchema.Warehouse (generated, opaque); this file is immutable once applied (checksummed).
-- Adds the Warehouse table, its integrity triggers and its append-only revision history. NO data is seeded and existing company data is
-- neither read nor modified (a Trial company may create its one allowed warehouse). Privileges come only from 'Hasm.Admin grant-company-runtime'.
--
-- Invariants enforced by PostgreSQL (not by the application):
--   * every warehouse belongs to exactly one branch (foreign key); the branch association is immutable;
--   * code: normalised upper case, unique (so unique case-insensitively), immutable;
--   * warehouses are never deleted or truncated (owner included);
--   * revision advances by exactly one per update; the activation epoch advances by exactly one on every change of the active flag and never otherwise;
--   * at commit no ACTIVE warehouse belongs to an INACTIVE branch (deferred constraint triggers on both tables: a warehouse cannot be created or
--     reactivated in an inactive branch, and a branch cannot be deactivated while it has active warehouses).

CREATE TABLE t_d2006f04 (
    c_a9f73cc2 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_e5c7450f varchar(20) NOT NULL,
    c_46594f28 uuid NOT NULL,
    c_50b75a87 varchar(200) NULL,
    c_7222ad5f varchar(200) NULL,
    c_49cbc053 boolean NOT NULL DEFAULT true,
    c_e67b716c bigint NOT NULL DEFAULT 1,
    c_cb813fae varchar(200) NULL,
    c_4c0b4e36 varchar(200) NULL,
    c_88303a9c varchar(100) NULL,
    c_5233109b varchar(100) NULL,
    c_7aa520de varchar(20) NULL,
    c_9e684076 varchar(30) NULL,
    c_10fcba74 varchar(254) NULL,
    c_2f78c781 varchar(200) NULL,
    c_0fd28ae9 bigint NOT NULL DEFAULT 1,
    c_f1a1b71a timestamptz NOT NULL,
    c_6b856378 timestamptz NOT NULL,
    CONSTRAINT k_75e42766 PRIMARY KEY (c_a9f73cc2),
    CONSTRAINT k_41c97f41 FOREIGN KEY (c_46594f28) REFERENCES t_8ba6c27d (c_358158d2),
    CONSTRAINT k_c973cec7 CHECK (c_e5c7450f ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT k_0788216a CHECK (
        (c_50b75a87 IS NOT NULL OR c_7222ad5f IS NOT NULL)
        AND (c_50b75a87 IS NULL OR char_length(btrim(c_50b75a87)) BETWEEN 1 AND 200)
        AND (c_7222ad5f IS NULL OR char_length(btrim(c_7222ad5f)) BETWEEN 1 AND 200)),
    CONSTRAINT k_08f9cdea CHECK (c_e67b716c >= 1),
    CONSTRAINT k_916f0f66 CHECK (c_0fd28ae9 > 0)
);
CREATE UNIQUE INDEX k_bd8b1492 ON t_d2006f04 (upper(c_e5c7450f));
CREATE INDEX k_24562720 ON t_d2006f04 (c_46594f28);

CREATE FUNCTION f_bf193561() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'warehouses cannot be deleted'; END IF;
    IF NEW.c_a9f73cc2 IS DISTINCT FROM OLD.c_a9f73cc2 THEN RAISE EXCEPTION 'warehouse identifiers are immutable'; END IF;
    IF NEW.c_e5c7450f IS DISTINCT FROM OLD.c_e5c7450f THEN RAISE EXCEPTION 'warehouse codes are immutable'; END IF;
    IF NEW.c_46594f28 IS DISTINCT FROM OLD.c_46594f28 THEN RAISE EXCEPTION 'the branch of a warehouse is immutable'; END IF;
    IF NEW.c_f1a1b71a IS DISTINCT FROM OLD.c_f1a1b71a THEN RAISE EXCEPTION 'warehouse creation time is immutable'; END IF;
    IF NEW.c_0fd28ae9 <> OLD.c_0fd28ae9 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NEW.c_49cbc053 IS DISTINCT FROM OLD.c_49cbc053 THEN
        IF NEW.c_e67b716c <> OLD.c_e67b716c + 1 THEN RAISE EXCEPTION 'activation epoch must advance by one on activation or deactivation'; END IF;
    ELSIF NEW.c_e67b716c <> OLD.c_e67b716c THEN
        RAISE EXCEPTION 'activation epoch changes only with the active flag';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_6b48247b BEFORE UPDATE OR DELETE ON t_d2006f04 FOR EACH ROW EXECUTE FUNCTION f_bf193561();
CREATE TRIGGER r_c49466ee BEFORE TRUNCATE ON t_d2006f04 FOR EACH STATEMENT EXECUTE FUNCTION f_bf193561();

-- No active warehouse in an inactive branch (deferred to commit so multi-statement changes are judged as a whole).
CREATE FUNCTION f_1080838c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM t_d2006f04 w JOIN t_8ba6c27d b ON b.c_358158d2 = w.c_46594f28 WHERE w.c_49cbc053 AND NOT b.c_6605fbbc) THEN
        RAISE EXCEPTION 'an active warehouse requires an active branch';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_c2627527 AFTER INSERT OR UPDATE ON t_d2006f04
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_1080838c();
CREATE CONSTRAINT TRIGGER r_a3fc163f AFTER UPDATE ON t_8ba6c27d
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_1080838c();

REVOKE ALL ON TABLE t_d2006f04 FROM PUBLIC;

-- Warehouse revision history (append-only): one row per audited change, linked to exactly one authenticated business audit event written in
-- the SAME transaction (composite foreign key, unique) and to its warehouse. Revision 1 (creation) has a row, unlike the seeded main branch.
CREATE TABLE t_49dad4e5 (
    c_292de6c9 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_029c85f8 uuid NOT NULL,
    c_671480fa bigint NOT NULL,
    c_fd03261e uuid NOT NULL,
    c_e0da49ec timestamptz NOT NULL,
    c_a0d45e59 jsonb NOT NULL,
    c_aeac7f2b varchar(48) NOT NULL,
    c_21cb4a92 bigint NOT NULL,
    CONSTRAINT k_db659dbf PRIMARY KEY (c_292de6c9),
    CONSTRAINT k_53cd24cb UNIQUE (c_029c85f8, c_671480fa),
    CONSTRAINT k_dc2938dd UNIQUE (c_aeac7f2b, c_21cb4a92),
    CONSTRAINT k_3ee8ec0b FOREIGN KEY (c_029c85f8) REFERENCES t_d2006f04 (c_a9f73cc2),
    CONSTRAINT k_3f3b3d0c FOREIGN KEY (c_aeac7f2b, c_21cb4a92) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_a1a2fb06 CHECK (c_671480fa > 0 AND jsonb_typeof(c_a0d45e59) = 'object')
);
CREATE FUNCTION f_da4f889c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'warehouse revisions are append-only';
END;
$$;
CREATE TRIGGER r_01ed82c3 BEFORE UPDATE OR DELETE ON t_49dad4e5 FOR EACH ROW EXECUTE FUNCTION f_da4f889c();
CREATE TRIGGER r_6a02c14c BEFORE TRUNCATE ON t_49dad4e5 FOR EACH STATEMENT EXECUTE FUNCTION f_da4f889c();

REVOKE ALL ON TABLE t_49dad4e5 FROM PUBLIC;
