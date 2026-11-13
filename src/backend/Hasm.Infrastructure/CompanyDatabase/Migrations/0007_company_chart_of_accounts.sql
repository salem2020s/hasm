-- Company database migration 0007: Chart of Accounts (Phase 3.2A).
-- Physical identifiers come from CompanySchema.Account / AccountSettings / AccountRevision (generated, opaque); this file is immutable once applied.
-- Adds the chart STRUCTURE only: accounts, the chart settings and the append-only history. No journal, balance, posting or opening balance is created,
-- no data is seeded (an empty chart is the starting state; a starter template is applied by an audited user action), and existing company data is
-- neither read nor modified. Privileges come only from 'Hasm.Admin grant-company-runtime'.
--
-- Invariants enforced by PostgreSQL (not by the application):
--   * the hierarchy is the parent relationship (self foreign key), without a fixed depth; an account is never its own ancestor (cycle check);
--   * a parent is always a GROUP account; a group account is never postable; an account that is not a group has no children;
--   * a child has the same account type (and therefore the same statement) as its parent;
--   * type -> statement is fixed: Assets, Liabilities and Equity are Balance Sheet, Revenue and Expenses are Profit and Loss; the nature (debit or
--     credit) is stored separately, so contra accounts are possible;
--   * an active account has an active parent (a group with an active child cannot be deactivated);
--   * codes are normalised upper case, unique case-insensitively within the company; they are NOT immutable (controlled, audited re-coding by a
--     separate permission): the stable identifier is the id, which never changes;
--   * accounts are never deleted or truncated (owner included); revision advances by exactly one per update;
--   * currency policy: 1 base currency only, 2 any enabled currency, 3 one specific currency (code required exactly then).

CREATE TABLE t_11d107a9 (
    c_29a86598 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_9d786d7c varchar(30) NOT NULL,
    c_f03e9240 varchar(200) NULL,
    c_188c6a2d varchar(200) NULL,
    c_1f4de422 uuid NULL,
    c_0caeac6a smallint NOT NULL,
    c_db118818 smallint NOT NULL,
    c_4f5cb580 smallint NOT NULL,
    c_57d34c8a boolean NOT NULL DEFAULT false,
    c_9761052f boolean NOT NULL DEFAULT true,
    c_dfab26fd smallint NOT NULL DEFAULT 1,
    c_76c4ece0 char(3) NULL,
    c_3d7a4cad varchar(40) NULL,
    c_3513bcb9 bigint NOT NULL DEFAULT 1,
    c_a6a28d08 timestamptz NOT NULL,
    c_f05f84fd timestamptz NOT NULL,
    CONSTRAINT k_eb12da06 PRIMARY KEY (c_29a86598),
    CONSTRAINT k_f3085d01 FOREIGN KEY (c_1f4de422) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_63c689e6 FOREIGN KEY (c_76c4ece0) REFERENCES t_451e91be (c_b6064cf1),
    CONSTRAINT k_9e64a52d CHECK (c_9d786d7c ~ '^[A-Z0-9][A-Z0-9._-]{0,29}$'),
    CONSTRAINT k_d075fab3 CHECK (
        (c_f03e9240 IS NOT NULL OR c_188c6a2d IS NOT NULL)
        AND (c_f03e9240 IS NULL OR char_length(btrim(c_f03e9240)) BETWEEN 1 AND 200)
        AND (c_188c6a2d IS NULL OR char_length(btrim(c_188c6a2d)) BETWEEN 1 AND 200)),
    CONSTRAINT k_60532cbe CHECK (c_0caeac6a BETWEEN 1 AND 5),
    CONSTRAINT k_d8639d1c CHECK (c_db118818 IN (1, 2)),
    CONSTRAINT k_9a12cdb1 CHECK (c_4f5cb580 = CASE WHEN c_0caeac6a <= 3 THEN 1 ELSE 2 END),
    CONSTRAINT k_84707f17 CHECK (c_dfab26fd IN (1, 2, 3) AND ((c_dfab26fd = 3) = (c_76c4ece0 IS NOT NULL))),
    CONSTRAINT k_3ceee586 CHECK (c_1f4de422 IS NULL OR c_1f4de422 <> c_29a86598),
    CONSTRAINT k_a2c24e8d CHECK (c_3513bcb9 > 0)
);
CREATE UNIQUE INDEX k_7f3d3ed8 ON t_11d107a9 (upper(c_9d786d7c));
CREATE INDEX k_9ea9cb31 ON t_11d107a9 (c_1f4de422);

CREATE FUNCTION f_9e328199() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'accounts cannot be deleted'; END IF;
    IF NEW.c_29a86598 IS DISTINCT FROM OLD.c_29a86598 THEN RAISE EXCEPTION 'account identifiers are immutable'; END IF;
    IF NEW.c_a6a28d08 IS DISTINCT FROM OLD.c_a6a28d08 OR NEW.c_3d7a4cad IS DISTINCT FROM OLD.c_3d7a4cad THEN
        RAISE EXCEPTION 'account provenance is immutable';
    END IF;
    IF NEW.c_3513bcb9 <> OLD.c_3513bcb9 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_7a5a4200 BEFORE UPDATE OR DELETE ON t_11d107a9 FOR EACH ROW EXECUTE FUNCTION f_9e328199();
CREATE TRIGGER r_09ffdb5e BEFORE TRUNCATE ON t_11d107a9 FOR EACH STATEMENT EXECUTE FUNCTION f_9e328199();

-- Tree integrity, judged at commit (so a template or a restructuring is judged as a whole): for the changed account and its direct children.
CREATE FUNCTION f_d830138f() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.c_1f4de422 IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM t_11d107a9 p WHERE p.c_29a86598 = NEW.c_1f4de422 AND p.c_57d34c8a) THEN
            RAISE EXCEPTION 'the parent of an account must be a group account';
        END IF;
        IF EXISTS (SELECT 1 FROM t_11d107a9 p WHERE p.c_29a86598 = NEW.c_1f4de422 AND p.c_0caeac6a <> NEW.c_0caeac6a) THEN
            RAISE EXCEPTION 'an account has the type of its parent';
        END IF;
        IF NEW.c_9761052f AND EXISTS (SELECT 1 FROM t_11d107a9 p WHERE p.c_29a86598 = NEW.c_1f4de422 AND NOT p.c_9761052f) THEN
            RAISE EXCEPTION 'an active account requires an active parent';
        END IF;
        IF EXISTS (
            WITH RECURSIVE up(id, parent) AS (
                SELECT a.c_29a86598, a.c_1f4de422 FROM t_11d107a9 a WHERE a.c_29a86598 = NEW.c_1f4de422
                UNION
                SELECT a.c_29a86598, a.c_1f4de422 FROM t_11d107a9 a JOIN up ON a.c_29a86598 = up.parent)
            SELECT 1 FROM up WHERE up.id = NEW.c_29a86598) THEN
            RAISE EXCEPTION 'an account cannot be its own ancestor';
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM t_11d107a9 c WHERE c.c_1f4de422 = NEW.c_29a86598) THEN
        IF NOT NEW.c_57d34c8a THEN RAISE EXCEPTION 'an account with children must be a group account'; END IF;
        IF EXISTS (SELECT 1 FROM t_11d107a9 c WHERE c.c_1f4de422 = NEW.c_29a86598 AND c.c_0caeac6a <> NEW.c_0caeac6a) THEN
            RAISE EXCEPTION 'an account has the type of its parent';
        END IF;
        IF NOT NEW.c_9761052f AND EXISTS (SELECT 1 FROM t_11d107a9 c WHERE c.c_1f4de422 = NEW.c_29a86598 AND c.c_9761052f) THEN
            RAISE EXCEPTION 'an account with an active child must stay active';
        END IF;
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_36b31b96 AFTER INSERT OR UPDATE ON t_11d107a9 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_d830138f();
REVOKE ALL ON TABLE t_11d107a9 FROM PUBLIC;

-- Chart settings (singleton, created on the first write: a read never creates it). Controls the automatic assignment of account codes.
CREATE TABLE t_4eaf5544 (
    c_683db677 boolean NOT NULL DEFAULT true,
    c_38b81f61 smallint NOT NULL DEFAULT 2,
    c_a8ea27c4 boolean NOT NULL DEFAULT true,
    c_c8affba4 bigint NOT NULL DEFAULT 1,
    c_d64f53d2 timestamptz NOT NULL,
    CONSTRAINT k_d1288af6 PRIMARY KEY (c_683db677),
    CONSTRAINT k_ed83d910 CHECK (c_683db677),
    CONSTRAINT k_212ceaf1 CHECK (c_38b81f61 BETWEEN 1 AND 4),
    CONSTRAINT k_9992af71 CHECK (c_c8affba4 > 0)
);
CREATE FUNCTION f_2c5c2f2d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'chart settings cannot be deleted'; END IF;
    IF NEW.c_c8affba4 <> OLD.c_c8affba4 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_45534741 BEFORE UPDATE OR DELETE ON t_4eaf5544 FOR EACH ROW EXECUTE FUNCTION f_2c5c2f2d();
CREATE TRIGGER r_0b02c127 BEFORE TRUNCATE ON t_4eaf5544 FOR EACH STATEMENT EXECUTE FUNCTION f_2c5c2f2d();
REVOKE ALL ON TABLE t_4eaf5544 FROM PUBLIC;

-- Chart history (append-only): one row per audited change, linked to exactly one authenticated business audit event written in the SAME transaction.
-- entity: 1 account, 2 chart settings, 3 template application (the entity id is polymorphic, so it is not a foreign key; the service writes it with the change).
-- Accounts created by a template have revision 1 and no row of their own: the single template-application row and its audit event cover them.
CREATE TABLE t_6bc0a848 (
    c_8730cef3 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_6f3728e4 smallint NOT NULL,
    c_477f65d3 uuid NOT NULL,
    c_9617c4f4 bigint NOT NULL,
    c_2c14f733 uuid NOT NULL,
    c_e3ff7f89 timestamptz NOT NULL,
    c_8ba4ef1c jsonb NOT NULL,
    c_53c3941c varchar(48) NOT NULL,
    c_6c9c32b6 bigint NOT NULL,
    CONSTRAINT k_51737778 PRIMARY KEY (c_8730cef3),
    CONSTRAINT k_259d9b61 UNIQUE (c_6f3728e4, c_477f65d3, c_9617c4f4),
    CONSTRAINT k_5e0159a3 UNIQUE (c_53c3941c, c_6c9c32b6),
    CONSTRAINT k_6515f868 FOREIGN KEY (c_53c3941c, c_6c9c32b6) REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_e47aaa6d CHECK (c_6f3728e4 IN (1, 2, 3) AND c_9617c4f4 > 0 AND jsonb_typeof(c_8ba4ef1c) = 'object')
);
CREATE FUNCTION f_1b681536() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'chart history is append-only';
END;
$$;
CREATE TRIGGER r_74e6f2fe BEFORE UPDATE OR DELETE ON t_6bc0a848 FOR EACH ROW EXECUTE FUNCTION f_1b681536();
CREATE TRIGGER r_c1f53c3c BEFORE TRUNCATE ON t_6bc0a848 FOR EACH STATEMENT EXECUTE FUNCTION f_1b681536();
REVOKE ALL ON TABLE t_6bc0a848 FROM PUBLIC;
