-- Migration 0011: financial statement classification (Phase 3.3 S3). Forward-only, no DROP, nothing seeded, no data migrated.
-- Opaque physical names (D-029); the logical dictionary lives in CompanySchema.Statements.cs.
--
-- The Chart of Accounts stays the accounting structure; the presentation of the financial statements is a SEPARATE, company-level mapping of accounts to statement sections. A mapping is VERSIONED: every
-- change creates a new immutable version holding the complete mapping, so a statement can always be reproduced with the version it was produced with, and changing the classification never reinterprets an
-- earlier run. Accounts are referenced by identity (never by code), so re-coding or moving an account keeps its classification.

-- One immutable version of the mapping. source: 1 manual, 2 derived from a starter template. content_hash identifies the exact entry set (an unchanged mapping never creates a version).
CREATE TABLE t_1fcba8f0 (
    c_85c806d1 uuid NOT NULL DEFAULT gen_random_uuid(),
    c_f09acb95 integer NOT NULL,
    c_41ef1ee1 timestamptz NOT NULL,
    c_7c815034 uuid NOT NULL,
    c_e26999dd smallint NOT NULL,
    c_17be4333 varchar(500) NULL,
    c_d1bdae31 integer NOT NULL,
    c_15ccac28 char(64) NOT NULL,
    CONSTRAINT k_583c2e19 PRIMARY KEY (c_85c806d1),
    CONSTRAINT k_23a6458c UNIQUE (c_f09acb95),
    CONSTRAINT k_dcf31000 CHECK (c_f09acb95 >= 1 AND c_e26999dd IN (1, 2) AND c_d1bdae31 >= 0)
);
CREATE FUNCTION f_7189b7b8() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'statement mapping versions are immutable';
END;
$$;
CREATE TRIGGER r_c4942dde BEFORE UPDATE OR DELETE ON t_1fcba8f0 FOR EACH ROW EXECUTE FUNCTION f_7189b7b8();
CREATE TRIGGER r_1f5ee9ca BEFORE TRUNCATE ON t_1fcba8f0 FOR EACH STATEMENT EXECUTE FUNCTION f_7189b7b8();
REVOKE ALL ON TABLE t_1fcba8f0 FROM PUBLIC;

-- The entries of a version: an account (group or postable) and the statement section it is presented in. section: 1 current assets, 2 non-current assets, 3 current liabilities, 4 non-current liabilities,
-- 5 equity capital, 6 retained earnings, 7 other equity, 10 operating revenue, 11 revenue reductions, 12 cost of sales, 13 operating expenses, 14 other income, 15 finance and other expenses, 16 direct service costs.
-- A group's entry applies to its whole subtree (a more specific entry of a descendant wins), so the hierarchy is never double counted.
CREATE TABLE t_e69c0053 (
    c_146d13d4 uuid NOT NULL,
    c_48e48219 uuid NOT NULL,
    c_a1dc02c3 smallint NOT NULL,
    CONSTRAINT k_97bc0743 PRIMARY KEY (c_146d13d4, c_48e48219),
    CONSTRAINT k_ac1b72ef FOREIGN KEY (c_146d13d4) REFERENCES t_1fcba8f0 (c_85c806d1),
    CONSTRAINT k_0fc7a88d FOREIGN KEY (c_48e48219) REFERENCES t_11d107a9 (c_29a86598),
    CONSTRAINT k_5120fa8a CHECK (c_a1dc02c3 IN (1, 2, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16))
);
CREATE INDEX k_27f31c89 ON t_e69c0053 (c_48e48219);
CREATE FUNCTION f_bd61d548() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'statement mapping entries are immutable';
END;
$$;
CREATE TRIGGER r_94a99b30 BEFORE UPDATE OR DELETE ON t_e69c0053 FOR EACH ROW EXECUTE FUNCTION f_bd61d548();
CREATE TRIGGER r_76b4e5c4 BEFORE TRUNCATE ON t_e69c0053 FOR EACH STATEMENT EXECUTE FUNCTION f_bd61d548();

-- PostgreSQL enforces which sections an account type may be presented in (assets: 1-2, liabilities: 3-4, equity: 5-7, revenue: 10, 11, 14, expenses: 12, 13, 15, 16), whoever writes the row.
CREATE FUNCTION f_97c1ad60() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    t smallint;
BEGIN
    SELECT a.c_0caeac6a INTO t FROM t_11d107a9 a WHERE a.c_29a86598 = NEW.c_48e48219;
    IF t IS NULL THEN RAISE EXCEPTION 'unknown account'; END IF;
    IF NOT ((t = 1 AND NEW.c_a1dc02c3 IN (1, 2)) OR (t = 2 AND NEW.c_a1dc02c3 IN (3, 4)) OR (t = 3 AND NEW.c_a1dc02c3 IN (5, 6, 7))
         OR (t = 4 AND NEW.c_a1dc02c3 IN (10, 11, 14)) OR (t = 5 AND NEW.c_a1dc02c3 IN (12, 13, 15, 16))) THEN
        RAISE EXCEPTION 'the statement section does not fit the account type';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_fe076bfb BEFORE INSERT ON t_e69c0053 FOR EACH ROW EXECUTE FUNCTION f_97c1ad60();
REVOKE ALL ON TABLE t_e69c0053 FROM PUBLIC;
