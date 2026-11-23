-- Migration 0033: a semantically identical ACTIVE approval policy is refused by PostgreSQL (Phase 3.13; owner decision of 2026-11-21). Forward-only and ADDITIVE: one new table, new functions and new
-- triggers only; no existing table, constraint, function or trigger is altered or replaced, and no approval policy row is read for change, modified, deactivated or removed. Opaque physical names (D-029) generated
-- deterministically by tools/schema/company-0033-approval-policy-identity.cjs (logical dictionary: CompanySchema.ApprovalPolicyIdentity.cs). Do not edit by hand: re-run the generator.
--
-- WHAT "IDENTICAL" MEANS. Two policies are semantically identical when they would be picked for exactly the same documents and demand exactly the same approvals: the same document type, source, branch (a policy for
-- ALL branches, NULL, is its own value and differs from every single branch), minimum amount, journal type, rate-override, soft-closed and closing-kind conditions, and the same ORDERED list of stages (required approvals and
-- role of each stage; a classic policy of N approvals is one stage of N without a role). The Arabic and English names, the identifier, the revision and the timestamps are not part of the identity.
-- SOURCE. For the JOURNAL the picker compares the policy source with the source of the entry (3 = either, 1 = manual, 2 = automatic), so the three values are three different selections and the literal value is part of the identity.
-- For every COMMERCIAL document type the picker (migration 0032) takes a policy when its source is IN (1, 3) and a commercial document has no source of its own: manual and any select exactly the same documents, so both
-- are written as 'doc'; an automatic (2) commercial policy is never picked at all and is written as 'never'. The journal is exactly DocumentTypes.UsesApprovalPolicies; every other type in this table is commercial.
-- The model has NO priority and NO effective period today; if either is ever added it MUST enter the fingerprint below (a new migration with a new fingerprint version).
-- Policies that differ in any of the listed parts stay allowed, also when they overlap (for example one for all branches and one for a single branch): the picker of migrations 0009 and 0032 takes the strictest.
-- The scope is this company database, so the company is not part of the fingerprint.
--
-- HOW. The fingerprint is computed BY POSTGRESQL from the stored rows (never supplied by a caller): SHA-256 over a canonical, typed and delimited text. One identity row per policy holds it; a partial UNIQUE index over the
-- identities of ACTIVE policies is the race-safe refusal (two concurrent writers: the second waits for the first and fails). The identity is refreshed by DEFERRED constraint triggers on the policy and on its stages, because
-- the stages of a revision are written after the policy row; a caller may ask for the check earlier with SET CONSTRAINTS ... IMMEDIATE (the service does, to answer 409 before it commits).
-- EXISTING DATA. A deployed database may already hold identical active policies. Nothing is changed for them: the backfill below keeps the OLDEST of each identical group (the one the picker prefers) as the holder of the
-- identity and marks the others as pre-existing; marked rows are left out of the unique index. The mark is written ONLY by this backfill: the guard trigger created after it resets the mark on every later write, so a marked
-- policy that is edited or reactivated must pass the same refusal as any other, and it can always be deactivated.

CREATE FUNCTION f_6e2c6dfc(p_policy uuid) RETURNS text LANGUAGE sql STABLE AS $$
    -- canonical text, version v2 (the source is canonicalized per document type, see SOURCE above): every part is named and typed; the document type is length-prefixed; the amount is numeric(20,4) and so always printed with four decimals; NULL is written as a token no value can produce
    SELECT encode(sha256(convert_to(
           'v2'
        || '|type=' || char_length(p.c_9c04c465)::text || ':' || p.c_9c04c465
        || '|source=' || CASE WHEN p.c_9c04c465 = 'journal_entry' THEN p.c_b130782f::text
                              WHEN p.c_b130782f IN (1, 3) THEN 'doc'
                              ELSE 'never' END
        || '|branch=' || COALESCE('one:' || p.c_5d70e226::text, 'all')
        || '|min=' || p.c_a0b367ec::numeric(20,4)::text
        || '|jtype=' || COALESCE(p.c_1a2f7f5e::text, '-')
        || '|rate=' || COALESCE(p.c_6088d474::integer::text, '-')
        || '|soft=' || COALESCE(p.c_ca9500fa::integer::text, '-')
        || '|closing=' || COALESCE(p.c_43137a95::text, '-')
        || '|stages=' || COALESCE(
               (SELECT string_agg(st.c_002a7676::text || '@' || COALESCE(st.c_22b0a7aa::text, '-'), '>' ORDER BY st.c_6e0d1427)
                  FROM t_e8cadae9 st WHERE st.c_85629458 = p.c_a2b53e06 AND st.c_41aad198 = p.c_c9bff476),
               p.c_a874380a::text || '@-'), 'UTF8')), 'hex')
    FROM t_8f2462c5 p WHERE p.c_a2b53e06 = p_policy
$$;

CREATE TABLE t_9fd0b9d1 (
    c_c15c0ad8 uuid NOT NULL,
    c_d47a541c text NOT NULL,
    c_8b0c4559 boolean NOT NULL,
    c_2e959040 boolean NOT NULL DEFAULT false,
    CONSTRAINT k_7ac5fd06 PRIMARY KEY (c_c15c0ad8),
    CONSTRAINT k_a89e9635 FOREIGN KEY (c_c15c0ad8) REFERENCES t_8f2462c5 (c_a2b53e06),
    CONSTRAINT k_3cdbc534 CHECK (c_d47a541c ~ '^[0-9a-f]{64}$'),
    CONSTRAINT k_43eaad6c CHECK (c_8b0c4559 OR NOT c_2e959040)
);
REVOKE ALL ON TABLE t_9fd0b9d1 FROM PUBLIC;

-- Backfill (the ONLY writer of the pre-existing mark): one identity per existing policy. Within a group of identical ACTIVE policies the oldest (creation time, then id: the order of the picker) holds the identity and
-- the others are marked. No policy row is touched.
INSERT INTO t_9fd0b9d1 (c_c15c0ad8, c_d47a541c, c_8b0c4559, c_2e959040)
SELECT x.id, x.fp, x.active, x.active AND x.rn > 1
FROM (SELECT p.c_a2b53e06 AS id, f_6e2c6dfc(p.c_a2b53e06) AS fp, p.c_61d707fd AS active,
             row_number() OVER (PARTITION BY f_6e2c6dfc(p.c_a2b53e06), p.c_61d707fd ORDER BY p.c_bc3b07c1, p.c_a2b53e06) AS rn
      FROM t_8f2462c5 p) x;

-- The race-safe refusal: at most one unmarked identity per fingerprint among ACTIVE policies.
CREATE UNIQUE INDEX k_310a6baa ON t_9fd0b9d1 (c_d47a541c) WHERE c_8b0c4559 AND NOT c_2e959040;

-- How many pre-existing duplicates were marked (0 on a database without duplicates). The same number is read at any time with: SELECT count(*) FROM <identity table> WHERE <pre-existing column>.
DO $$
DECLARE
    v_marked bigint;
BEGIN
    SELECT count(*) INTO v_marked FROM t_9fd0b9d1 i WHERE i.c_2e959040;
    RAISE NOTICE 'approval policy identity: % pre-existing identical active policy row(s) marked; none was changed or deactivated', v_marked;
END;
$$;

-- Guard of the identity table (created AFTER the backfill, so the backfill is the only statement that ever wrote the mark). Whatever a writer supplies, the stored identity is the truth read from the policy and its
-- stages; the mark is reset on every write; rows are never removed. A policy whose identity is NEW (first identity, a changed fingerprint, a reactivation, or a marked row being written) is refused when an identical
-- ACTIVE policy exists that is still marked (those are outside the unique index); every other collision is refused by the unique index itself.
CREATE FUNCTION f_2180d7ba() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_new boolean;
BEGIN
    IF TG_OP <> 'INSERT' AND TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'approval policy identities are never removed'; END IF;
    IF TG_OP = 'UPDATE' AND NEW.c_c15c0ad8 IS DISTINCT FROM OLD.c_c15c0ad8 THEN RAISE EXCEPTION 'the policy of an approval policy identity is immutable'; END IF;
    SELECT f_6e2c6dfc(p.c_a2b53e06), p.c_61d707fd INTO NEW.c_d47a541c, NEW.c_8b0c4559
      FROM t_8f2462c5 p WHERE p.c_a2b53e06 = NEW.c_c15c0ad8;
    IF NOT FOUND THEN RAISE EXCEPTION 'an approval policy identity needs its policy'; END IF;
    NEW.c_2e959040 := false;
    IF TG_OP = 'INSERT' THEN v_new := true;
    ELSE v_new := OLD.c_2e959040 OR NOT OLD.c_8b0c4559 OR OLD.c_d47a541c <> NEW.c_d47a541c;
    END IF;
    IF NEW.c_8b0c4559 AND v_new AND EXISTS (SELECT 1 FROM t_9fd0b9d1 o WHERE o.c_d47a541c = NEW.c_d47a541c
            AND o.c_8b0c4559 AND o.c_2e959040 AND o.c_c15c0ad8 <> NEW.c_c15c0ad8) THEN
        RAISE EXCEPTION 'an identical active approval policy already exists' USING ERRCODE = 'unique_violation', CONSTRAINT = 'k_310a6baa';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d16406a2 BEFORE INSERT OR UPDATE OR DELETE ON t_9fd0b9d1 FOR EACH ROW EXECUTE FUNCTION f_2180d7ba();
CREATE TRIGGER r_64fc6372 BEFORE TRUNCATE ON t_9fd0b9d1 FOR EACH STATEMENT EXECUTE FUNCTION f_2180d7ba();

-- Refresh of one identity: an existing row is rewritten (the guard recomputes it), a missing one is created. Not an upsert on purpose: the guard must see whether the identity existed.
CREATE FUNCTION f_8600242c(p_policy uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    UPDATE t_9fd0b9d1 SET c_d47a541c = c_d47a541c WHERE c_c15c0ad8 = p_policy;
    IF NOT FOUND THEN
        INSERT INTO t_9fd0b9d1 (c_c15c0ad8, c_d47a541c, c_8b0c4559) VALUES (p_policy, repeat('0', 64), false);
    END IF;
END;
$$;

-- Every write of a policy, and every stage written for it, refreshes the identity at COMMIT (or earlier on SET CONSTRAINTS ... IMMEDIATE), when the policy row and the stages of its revision are both in place.
CREATE FUNCTION f_33f9b81a() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM f_8600242c(NEW.c_a2b53e06);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_1370d45a AFTER INSERT OR UPDATE ON t_8f2462c5 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_33f9b81a();

CREATE FUNCTION f_9faf0fe1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM f_8600242c(NEW.c_85629458);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_87b2a531 AFTER INSERT ON t_e8cadae9 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION f_9faf0fe1();
