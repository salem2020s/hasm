-- Migration 0031: retention policy and legal holds for removed attachments (Phase R slice R1, owner decision D-082). Forward-only, additive (three new tables); no existing row is read or modified and nothing is deleted.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0031-attachment-retention.cjs (logical dictionary: CompanySchema.AttachmentRetention.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: the company-approved legal retention POLICY (versions, each with rules per record kind and sensitivity) and LEGAL HOLDS that block any later purge. R1 only RECORDS them and previews eligibility; no
-- bytes are deleted by this slice (the purge plan and its tables come with R2). The guarded Attachment table is NOT touched. OWNER-PENDING (D-109): the retention basis (counted from the removal date); while it is
-- pending the eligibility check answers retention_basis_pending for everything.
-- Invariants enforced by PostgreSQL:
--   * policy versions, rules and holds are never deleted or truncated; every change to a version or hold advances the revision by one;
--   * a policy version is created as a DRAFT; it is approved by a user OTHER than its author (a CHECK), then only superseded or revoked; superseded and revoked versions never change again; at most one version is Approved;
--   * rules are immutable and can be added only while their version is a draft; a rule is a finite period (1..1200 months) or Indefinite, never both;
--   * a version that shortens a period carries a non-empty written acknowledgement;
--   * a legal hold covers the company, one record or one attachment (consistent columns); it is released at most once, by a user OTHER than the one who placed it, with a reason; a released hold never changes again.

CREATE TABLE t_19ba824f (
    c_3bec91ab uuid NOT NULL,
    c_2f80f53d integer NOT NULL,
    c_19815723 smallint NOT NULL,
    c_4bec6160 date NOT NULL,
    c_25959f38 boolean NOT NULL DEFAULT false,
    c_c6a6f20e varchar(1000) NULL,
    c_fad4cc06 varchar(500) NULL,
    c_fe6ba338 uuid NOT NULL,
    c_e18a8b32 timestamptz NOT NULL,
    c_5a22af29 uuid NULL,
    c_8877fc4b timestamptz NULL,
    c_4dde15b1 uuid NULL,
    c_1de39a05 timestamptz NULL,
    c_8b51651f varchar(500) NULL,
    c_9d91000a timestamptz NULL,
    c_ae9e4262 bigint NOT NULL DEFAULT 1,
    c_d8821501 timestamptz NOT NULL,
    CONSTRAINT k_fb657607 PRIMARY KEY (c_3bec91ab),
    CONSTRAINT k_1b4184cb CHECK (c_2f80f53d > 0 AND c_ae9e4262 > 0 AND c_19815723 BETWEEN 1 AND 4),
    CONSTRAINT k_7e03c713 CHECK (
        (c_19815723 = 1 AND c_5a22af29 IS NULL AND c_8877fc4b IS NULL)
        OR (c_19815723 > 1 AND c_5a22af29 IS NOT NULL AND c_8877fc4b IS NOT NULL AND c_5a22af29 <> c_fe6ba338)),
    CONSTRAINT k_4caec6d0 CHECK (
        (c_19815723 IN (1, 2) AND c_9d91000a IS NULL)
        OR (c_19815723 IN (3, 4) AND c_9d91000a IS NOT NULL)),
    CONSTRAINT k_113dc30f CHECK (
        (c_19815723 = 3 AND c_4dde15b1 IS NOT NULL AND c_1de39a05 IS NOT NULL AND char_length(btrim(c_8b51651f)) >= 1)
        OR (c_19815723 <> 3 AND c_4dde15b1 IS NULL AND c_1de39a05 IS NULL AND c_8b51651f IS NULL)),
    CONSTRAINT k_35e0197a CHECK (c_19815723 = 1 OR NOT c_25959f38 OR char_length(btrim(c_c6a6f20e)) >= 1)
);
CREATE UNIQUE INDEX k_647d4e45 ON t_19ba824f (c_2f80f53d);
CREATE UNIQUE INDEX k_e55ed2db ON t_19ba824f (c_19815723) WHERE c_19815723 = 2;

CREATE FUNCTION f_e03a2fab() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_ae9e4262 <> 1 OR NEW.c_19815723 <> 1 THEN RAISE EXCEPTION 'a retention policy version is created as a draft at revision 1'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'retention policy versions are never removed'; END IF;
    IF NEW.c_3bec91ab IS DISTINCT FROM OLD.c_3bec91ab OR NEW.c_2f80f53d IS DISTINCT FROM OLD.c_2f80f53d
       OR NEW.c_4bec6160 IS DISTINCT FROM OLD.c_4bec6160 OR NEW.c_fad4cc06 IS DISTINCT FROM OLD.c_fad4cc06
       OR NEW.c_fe6ba338 IS DISTINCT FROM OLD.c_fe6ba338 OR NEW.c_e18a8b32 IS DISTINCT FROM OLD.c_e18a8b32 THEN
        RAISE EXCEPTION 'the identity, version, effective date, note and draft data of a retention policy version are immutable';
    END IF;
    IF OLD.c_19815723 IN (3, 4) THEN RAISE EXCEPTION 'a revoked or superseded retention policy version never changes again'; END IF;
    IF NOT ((OLD.c_19815723 = 1 AND NEW.c_19815723 = 2) OR (OLD.c_19815723 = 2 AND NEW.c_19815723 IN (3, 4))) THEN
        RAISE EXCEPTION 'a retention policy version moves only draft -> approved -> revoked or superseded';
    END IF;
    IF OLD.c_19815723 = 2 AND (NEW.c_5a22af29 IS DISTINCT FROM OLD.c_5a22af29 OR NEW.c_8877fc4b IS DISTINCT FROM OLD.c_8877fc4b
       OR NEW.c_25959f38 IS DISTINCT FROM OLD.c_25959f38 OR NEW.c_c6a6f20e IS DISTINCT FROM OLD.c_c6a6f20e) THEN
        RAISE EXCEPTION 'the approval data of a retention policy version never changes';
    END IF;
    IF NEW.c_ae9e4262 <> OLD.c_ae9e4262 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_be16dbd6 BEFORE INSERT OR UPDATE OR DELETE ON t_19ba824f FOR EACH ROW EXECUTE FUNCTION f_e03a2fab();
CREATE TRIGGER r_63688a04 BEFORE TRUNCATE ON t_19ba824f FOR EACH STATEMENT EXECUTE FUNCTION f_e03a2fab();
REVOKE ALL ON TABLE t_19ba824f FROM PUBLIC;

CREATE TABLE t_3069b85b (
    c_4ff62cde uuid NOT NULL,
    c_2696ae88 uuid NOT NULL,
    c_ba38d6cf varchar(40) NOT NULL,
    c_455da9c0 smallint NOT NULL,
    c_999f54bb integer NULL,
    c_7a22c0d8 boolean NOT NULL,
    CONSTRAINT k_f3d6b330 PRIMARY KEY (c_4ff62cde),
    CONSTRAINT k_fdf99ba5 FOREIGN KEY (c_2696ae88) REFERENCES t_19ba824f (c_3bec91ab),
    CONSTRAINT k_d070c05a CHECK (
        c_455da9c0 BETWEEN 0 AND 2
        AND (c_ba38d6cf = '*' OR c_ba38d6cf ~ '^[a-z][a-z0-9_]{0,39}$')
        AND ((c_7a22c0d8 AND c_999f54bb IS NULL) OR (NOT c_7a22c0d8 AND c_999f54bb BETWEEN 1 AND 1200)))
);
CREATE UNIQUE INDEX k_db6dd2d1 ON t_3069b85b (c_2696ae88, c_ba38d6cf, c_455da9c0);

CREATE FUNCTION f_7f3d154c() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF (SELECT c_19815723 FROM t_19ba824f WHERE c_3bec91ab = NEW.c_2696ae88) IS DISTINCT FROM 1 THEN
            RAISE EXCEPTION 'rules can be added only to a draft retention policy version';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'retention rules are immutable and never removed';
END;
$$;
CREATE TRIGGER r_f24869b7 BEFORE INSERT OR UPDATE OR DELETE ON t_3069b85b FOR EACH ROW EXECUTE FUNCTION f_7f3d154c();
CREATE TRIGGER r_6ee597be BEFORE TRUNCATE ON t_3069b85b FOR EACH STATEMENT EXECUTE FUNCTION f_7f3d154c();
REVOKE ALL ON TABLE t_3069b85b FROM PUBLIC;

CREATE TABLE t_282abaa0 (
    c_df040015 uuid NOT NULL,
    c_3a00c0e9 smallint NOT NULL,
    c_2b780988 varchar(40) NULL,
    c_c3580580 uuid NULL,
    c_b7fee892 uuid NULL,
    c_837220ac varchar(500) NOT NULL,
    c_587a4506 varchar(100) NULL,
    c_41d587d4 uuid NOT NULL,
    c_a8d33a3b timestamptz NOT NULL,
    c_d606619a uuid NULL,
    c_71754c89 timestamptz NULL,
    c_66619e8a varchar(500) NULL,
    c_8006a81a bigint NOT NULL DEFAULT 1,
    c_e43644f3 timestamptz NOT NULL,
    CONSTRAINT k_ad5c397b PRIMARY KEY (c_df040015),
    CONSTRAINT k_2f641dfa FOREIGN KEY (c_b7fee892) REFERENCES t_f3dfaa38 (c_65bde0b1),
    CONSTRAINT k_39c7c839 CHECK (c_8006a81a > 0 AND char_length(btrim(c_837220ac)) >= 1),
    CONSTRAINT k_cf0bceba CHECK (
        (c_3a00c0e9 = 1 AND c_2b780988 IS NULL AND c_c3580580 IS NULL AND c_b7fee892 IS NULL)
        OR (c_3a00c0e9 = 2 AND c_2b780988 IS NOT NULL AND c_c3580580 IS NOT NULL AND c_b7fee892 IS NULL)
        OR (c_3a00c0e9 = 3 AND c_b7fee892 IS NOT NULL AND c_2b780988 IS NULL AND c_c3580580 IS NULL)),
    CONSTRAINT k_61331cf0 CHECK (
        (c_d606619a IS NULL AND c_71754c89 IS NULL AND c_66619e8a IS NULL)
        OR (c_d606619a IS NOT NULL AND c_71754c89 IS NOT NULL AND char_length(btrim(c_66619e8a)) >= 1 AND c_d606619a <> c_41d587d4))
);
CREATE INDEX k_268e26a7 ON t_282abaa0 (c_2b780988, c_c3580580) WHERE c_71754c89 IS NULL AND c_3a00c0e9 = 2;
CREATE INDEX k_c283a931 ON t_282abaa0 (c_b7fee892) WHERE c_b7fee892 IS NOT NULL;

CREATE FUNCTION f_82bba01a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_8006a81a <> 1 OR NEW.c_71754c89 IS NOT NULL THEN RAISE EXCEPTION 'a legal hold is placed at revision 1 and is not yet released'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'legal holds are never removed'; END IF;
    IF OLD.c_71754c89 IS NOT NULL THEN RAISE EXCEPTION 'a released legal hold never changes again'; END IF;
    IF NEW.c_df040015 IS DISTINCT FROM OLD.c_df040015 OR NEW.c_3a00c0e9 IS DISTINCT FROM OLD.c_3a00c0e9 OR NEW.c_2b780988 IS DISTINCT FROM OLD.c_2b780988
       OR NEW.c_c3580580 IS DISTINCT FROM OLD.c_c3580580 OR NEW.c_b7fee892 IS DISTINCT FROM OLD.c_b7fee892
       OR NEW.c_837220ac IS DISTINCT FROM OLD.c_837220ac OR NEW.c_587a4506 IS DISTINCT FROM OLD.c_587a4506
       OR NEW.c_41d587d4 IS DISTINCT FROM OLD.c_41d587d4 OR NEW.c_a8d33a3b IS DISTINCT FROM OLD.c_a8d33a3b THEN
        RAISE EXCEPTION 'a legal hold changes only by its release';
    END IF;
    IF NEW.c_71754c89 IS NULL THEN RAISE EXCEPTION 'the only change of a legal hold is its release'; END IF;
    IF NEW.c_8006a81a <> OLD.c_8006a81a + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_49decca8 BEFORE INSERT OR UPDATE OR DELETE ON t_282abaa0 FOR EACH ROW EXECUTE FUNCTION f_82bba01a();
CREATE TRIGGER r_7b7017d6 BEFORE TRUNCATE ON t_282abaa0 FOR EACH STATEMENT EXECUTE FUNCTION f_82bba01a();
REVOKE ALL ON TABLE t_282abaa0 FROM PUBLIC;
