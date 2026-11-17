-- Migration 0023: party registry (Phase 3.9A). Forward-only and additive: three new tables and the company permission codes of the party registry for the preset Company Administrator role.
-- Opaque physical names (D-029); the logical dictionary lives in SystemSchema.Parties.cs. Nothing is seeded into the new tables; no existing data is read or modified except the preset role's permission rows.
--
-- WHAT THIS IS (SRS 3.3, 3.4, 17.4, 19.4): the SHARED IDENTITY of a business party (GlobalPartyId) and the central workflow that changes it.
--   * shared identity: party type, legal names, tax and registration numbers, status. NO balance, account, term, limit or any company-specific value has a column here.
--   * proposals: a company user proposes identifying data (a new identity, or a change of a linked one); an authorized central actor approves (new identity, or link to an existing one) or rejects.
--     A proposal is keyed by (company, request id): the request id is the idempotency key of the company's request log (outbox), so a repeated delivery never creates a second proposal.
--   * links: which LocalPartyId of which company is linked to which GlobalPartyId; created only by an approval; append-only. One company links a shared identity at most once.
-- Invariants enforced by PostgreSQL (not only by the application):
--   * identities, proposals and links are never removed; links never change; the creation data of an identity never changes and its revision advances by one;
--   * a proposal is decided once (pending -> approved or rejected), its proposed content never changes, the decider is never the proposer, a rejection has a note;
--   * identifiers are normalized (upper case, letters, digits, '.', '/', '-') and unique across the registry.
-- party kind: 1 organization, 2 individual. identity status: 1 active, 2 inactive. proposal kind: 1 new identity, 2 change of a linked identity.
-- proposal status: 1 pending, 2 approved, 3 rejected. resolution: 1 new identity created, 2 linked to an existing identity, 3 change applied.

CREATE TABLE t_eabdd8fc (
    c_f8e48d99 uuid NOT NULL,
    c_0a313805 smallint NOT NULL,
    c_ebf7a8f6 varchar(200) NOT NULL,
    c_3d6c80d1 varchar(200) NULL,
    c_45afe91f varchar(50) NULL,
    c_49127565 varchar(50) NULL,
    c_136713b0 smallint NOT NULL DEFAULT 1,
    c_7cc2d0df bigint NOT NULL DEFAULT 1,
    c_abea2e11 uuid NOT NULL,
    c_d6cc2370 timestamptz NOT NULL,
    c_c8ed2c3d timestamptz NOT NULL,
    CONSTRAINT k_750c92d7 PRIMARY KEY (c_f8e48d99),
    CONSTRAINT k_c8b5deee FOREIGN KEY (c_abea2e11) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_3d609b38 CHECK (c_0a313805 IN (1, 2) AND c_136713b0 IN (1, 2) AND c_7cc2d0df > 0
        AND char_length(btrim(c_ebf7a8f6)) >= 1 AND (c_3d6c80d1 IS NULL OR char_length(btrim(c_3d6c80d1)) >= 1)
        AND (c_45afe91f IS NULL OR c_45afe91f ~ '^[A-Z0-9./-]{1,50}$')
        AND (c_49127565 IS NULL OR c_49127565 ~ '^[A-Z0-9./-]{1,50}$'))
);
CREATE UNIQUE INDEX k_8c4a4f3f ON t_eabdd8fc (c_45afe91f) WHERE c_45afe91f IS NOT NULL;
CREATE UNIQUE INDEX k_3f114029 ON t_eabdd8fc (c_49127565) WHERE c_49127565 IS NOT NULL;
CREATE INDEX k_d02a9cf3 ON t_eabdd8fc (lower(c_ebf7a8f6));

CREATE FUNCTION f_987c4744() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'shared party identities are never removed'; END IF;
    IF NEW.c_f8e48d99 IS DISTINCT FROM OLD.c_f8e48d99 OR NEW.c_abea2e11 IS DISTINCT FROM OLD.c_abea2e11 OR NEW.c_d6cc2370 IS DISTINCT FROM OLD.c_d6cc2370 THEN
        RAISE EXCEPTION 'the creation data of a shared party identity is immutable';
    END IF;
    IF NEW.c_7cc2d0df <> OLD.c_7cc2d0df + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_3dbb630e BEFORE UPDATE OR DELETE ON t_eabdd8fc FOR EACH ROW EXECUTE FUNCTION f_987c4744();
CREATE TRIGGER r_304cbcbd BEFORE TRUNCATE ON t_eabdd8fc FOR EACH STATEMENT EXECUTE FUNCTION f_987c4744();
REVOKE ALL ON TABLE t_eabdd8fc FROM PUBLIC;

CREATE TABLE t_68b1b903 (
    c_1ed401f7 uuid NOT NULL,
    c_153f979f uuid NOT NULL,
    c_87534b06 uuid NOT NULL,
    c_a9dfc171 uuid NOT NULL,
    c_d51f2b1e smallint NOT NULL,
    c_1d897157 uuid NULL,
    c_0d401c90 bigint NULL,
    c_93954331 smallint NOT NULL,
    c_1d21b300 varchar(200) NOT NULL,
    c_a95c5183 varchar(200) NULL,
    c_ce1a1eb7 varchar(50) NULL,
    c_5363aefe varchar(50) NULL,
    c_ae2e3b2c smallint NOT NULL DEFAULT 1,
    c_f684f81e varchar(500) NULL,
    c_820709aa uuid NOT NULL,
    c_68576c0b timestamptz NOT NULL,
    c_80581f31 smallint NOT NULL DEFAULT 1,
    c_a19bd897 smallint NULL,
    c_17d1e5a3 uuid NULL,
    c_c8294d0e uuid NULL,
    c_25b2714d timestamptz NULL,
    c_925b20bb varchar(500) NULL,
    c_445301d4 bigint NOT NULL DEFAULT 1,
    CONSTRAINT k_bb24c9f1 PRIMARY KEY (c_1ed401f7),
    CONSTRAINT k_5bb6036f FOREIGN KEY (c_153f979f) REFERENCES t_f446f4d0 (c_a9a73285),
    CONSTRAINT k_693a7ce4 FOREIGN KEY (c_820709aa) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_83ffe1f8 FOREIGN KEY (c_c8294d0e) REFERENCES t_fc83d26e (c_74b9e5ad),
    CONSTRAINT k_d7289a19 FOREIGN KEY (c_1d897157) REFERENCES t_eabdd8fc (c_f8e48d99),
    CONSTRAINT k_06ae1580 FOREIGN KEY (c_17d1e5a3) REFERENCES t_eabdd8fc (c_f8e48d99),
    CONSTRAINT k_3151851f UNIQUE (c_153f979f, c_87534b06),
    CONSTRAINT k_30e82a25 CHECK (c_d51f2b1e IN (1, 2) AND c_93954331 IN (1, 2) AND c_ae2e3b2c IN (1, 2) AND c_80581f31 IN (1, 2, 3) AND c_445301d4 > 0
        AND ((c_d51f2b1e = 1 AND c_1d897157 IS NULL AND c_0d401c90 IS NULL AND c_ae2e3b2c = 1) OR (c_d51f2b1e = 2 AND c_1d897157 IS NOT NULL AND c_0d401c90 > 0))
        AND char_length(btrim(c_1d21b300)) >= 1 AND (c_a95c5183 IS NULL OR char_length(btrim(c_a95c5183)) >= 1)
        AND (c_ce1a1eb7 IS NULL OR c_ce1a1eb7 ~ '^[A-Z0-9./-]{1,50}$')
        AND (c_5363aefe IS NULL OR c_5363aefe ~ '^[A-Z0-9./-]{1,50}$')),
    CONSTRAINT k_8602fc77 CHECK (
        (c_80581f31 = 1 AND c_c8294d0e IS NULL AND c_25b2714d IS NULL AND c_a19bd897 IS NULL AND c_17d1e5a3 IS NULL AND c_925b20bb IS NULL)
        OR (c_80581f31 = 2 AND c_c8294d0e IS NOT NULL AND c_25b2714d IS NOT NULL AND c_17d1e5a3 IS NOT NULL
            AND ((c_d51f2b1e = 1 AND c_a19bd897 IN (1, 2)) OR (c_d51f2b1e = 2 AND c_a19bd897 = 3 AND c_17d1e5a3 = c_1d897157)))
        OR (c_80581f31 = 3 AND c_c8294d0e IS NOT NULL AND c_25b2714d IS NOT NULL AND c_a19bd897 IS NULL AND c_17d1e5a3 IS NULL
            AND c_925b20bb IS NOT NULL AND char_length(btrim(c_925b20bb)) >= 1)),
    CONSTRAINT k_585ecab3 CHECK (c_c8294d0e IS NULL OR c_c8294d0e <> c_820709aa)
);
-- At most one undecided proposal per local party of a company.
CREATE UNIQUE INDEX k_ee312585 ON t_68b1b903 (c_153f979f, c_a9dfc171) WHERE c_80581f31 = 1;
CREATE INDEX k_8bcf8142 ON t_68b1b903 (c_80581f31, c_68576c0b);

CREATE FUNCTION f_96b67c1d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'party proposals are never removed'; END IF;
    IF OLD.c_80581f31 <> 1 THEN RAISE EXCEPTION 'a decided party proposal is final'; END IF;
    IF NEW.c_80581f31 NOT IN (2, 3) THEN RAISE EXCEPTION 'a party proposal is decided only once, by approval or rejection'; END IF;
    IF NEW.c_1ed401f7 IS DISTINCT FROM OLD.c_1ed401f7 OR NEW.c_153f979f IS DISTINCT FROM OLD.c_153f979f OR NEW.c_87534b06 IS DISTINCT FROM OLD.c_87534b06
       OR NEW.c_a9dfc171 IS DISTINCT FROM OLD.c_a9dfc171 OR NEW.c_d51f2b1e IS DISTINCT FROM OLD.c_d51f2b1e OR NEW.c_1d897157 IS DISTINCT FROM OLD.c_1d897157 OR NEW.c_0d401c90 IS DISTINCT FROM OLD.c_0d401c90
       OR NEW.c_93954331 IS DISTINCT FROM OLD.c_93954331 OR NEW.c_1d21b300 IS DISTINCT FROM OLD.c_1d21b300 OR NEW.c_a95c5183 IS DISTINCT FROM OLD.c_a95c5183
       OR NEW.c_ce1a1eb7 IS DISTINCT FROM OLD.c_ce1a1eb7 OR NEW.c_5363aefe IS DISTINCT FROM OLD.c_5363aefe OR NEW.c_ae2e3b2c IS DISTINCT FROM OLD.c_ae2e3b2c
       OR NEW.c_f684f81e IS DISTINCT FROM OLD.c_f684f81e OR NEW.c_820709aa IS DISTINCT FROM OLD.c_820709aa OR NEW.c_68576c0b IS DISTINCT FROM OLD.c_68576c0b THEN
        RAISE EXCEPTION 'the proposed content of a party proposal is immutable';
    END IF;
    IF NEW.c_445301d4 <> OLD.c_445301d4 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_5230ef3e BEFORE UPDATE OR DELETE ON t_68b1b903 FOR EACH ROW EXECUTE FUNCTION f_96b67c1d();
CREATE TRIGGER r_b02a8650 BEFORE TRUNCATE ON t_68b1b903 FOR EACH STATEMENT EXECUTE FUNCTION f_96b67c1d();
REVOKE ALL ON TABLE t_68b1b903 FROM PUBLIC;

CREATE TABLE t_0c91bd31 (
    c_303d1fea uuid NOT NULL,
    c_b150ec00 uuid NOT NULL,
    c_a02c7beb uuid NOT NULL,
    c_53499268 uuid NOT NULL,
    c_71001026 timestamptz NOT NULL,
    CONSTRAINT k_d74b4c0f PRIMARY KEY (c_303d1fea, c_b150ec00),
    CONSTRAINT k_dee379d4 UNIQUE (c_303d1fea, c_a02c7beb),
    CONSTRAINT k_e276b776 FOREIGN KEY (c_303d1fea) REFERENCES t_f446f4d0 (c_a9a73285),
    CONSTRAINT k_8b26c8de FOREIGN KEY (c_a02c7beb) REFERENCES t_eabdd8fc (c_f8e48d99),
    CONSTRAINT k_c3989189 FOREIGN KEY (c_53499268) REFERENCES t_68b1b903 (c_1ed401f7)
);
CREATE INDEX k_ed782a9b ON t_0c91bd31 (c_a02c7beb);

CREATE FUNCTION f_7cd7a401() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'party links are append-only';
END;
$$;
CREATE TRIGGER r_94e983f3 BEFORE UPDATE OR DELETE ON t_0c91bd31 FOR EACH ROW EXECUTE FUNCTION f_7cd7a401();
CREATE TRIGGER r_468671c8 BEFORE TRUNCATE ON t_0c91bd31 FOR EACH STATEMENT EXECUTE FUNCTION f_7cd7a401();
REVOKE ALL ON TABLE t_0c91bd31 FROM PUBLIC;

-- Company permissions of the party registry for the preset Company Administrator role. Preset roles are immutable through the application, so the immutability trigger is paused for exactly
-- this versioned change, inside this transaction, and re-enabled before it ends. No company permission approves a proposal (central approval is reserved to identity administrators).
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.parties.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.parties.create'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.parties.update'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.parties.submit'),
    ('5c1b0f6e-0000-4a00-8000-000000000001', 'company.parties.configure');

ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
