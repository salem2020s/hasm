-- Migration 0037: the replay authorization registry (Phase 3.8D threat T2, slice A1; owner decisions D-248 to D-253; design docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md sections 4.1 to 4.3 and 4.5). Forward-only and ADDITIVE:
-- three new tables, their guards and one verification function; no existing table, function or trigger is changed, replaced or switched off. INERT: nothing calls the verifier, the application role holds only SELECT,
-- no grant is issued and no execution is possible (activation stays closed).
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0037-replay-signer-registry.cjs (logical dictionary: CompanySchema.ReplayAuthorization.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: only PUBLIC keys are stored. A signer key is RSA with a modulus of exactly 3072 bits and exponent 65537 (RSASSA-PKCS1-v1_5, SHA-256). The registry is written by the database OWNER only (the company runtime role holds
-- SELECT and nothing else, see CompanyRuntimePrivileges), is append-only apart from the one-time revoke-from (design 4.1, 4.6), and registered-at is set by the database clock, never supplied.
-- The two grant tables are EMPTY SHELLS of design 4.3 (append-only, structural checks only): the insert-time verification, the single-use rule and the commit-time checks arrive with slices A2 to A4. Because a row written before
-- those checks exist would escape them, the application role gets NO INSERT on these tables in this slice.
-- The verifier public.rsa_verify (design 4.2) is plain PL/pgSQL with invoker rights and no extension. It refuses unless the signature has exactly 384 bytes and is below the modulus, computes s^65537 mod n with 16 squarings and one
-- multiplication (mod() only, never a division) and compares the WHOLE expected encoding 00 01 FF.. 00 DigestInfo sha256(message) as a number, so no padding parse can be forged. Name resolution (design 4.5): no search_path and no
-- SECURITY DEFINER; every relation is written public.<name> inside function bodies, and the company runtime role holds neither TEMPORARY nor CREATE, so nothing can stand in for a name the functions use.

CREATE TABLE public.t_23985c22 (
    c_f57af9ad varchar(40) NOT NULL,
    c_9c9307e0 numeric NOT NULL,
    c_83f7d43c uuid NULL,
    c_2ad04b7d timestamptz NOT NULL DEFAULT clock_timestamp(),
    c_d7d63a67 timestamptz NULL,
    CONSTRAINT k_08f520fd PRIMARY KEY (c_f57af9ad),
    CONSTRAINT k_09a0a9a2 CHECK (c_f57af9ad ~ '^[a-z0-9-]{1,40}$'
        AND trunc(c_9c9307e0) = c_9c9307e0 AND mod(c_9c9307e0, 2) = 1
        AND c_9c9307e0 >= power(2::numeric, 3071) AND c_9c9307e0 < power(2::numeric, 3072))
);

CREATE FUNCTION public.f_d1876eea() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_d7d63a67 IS NOT NULL THEN RAISE EXCEPTION 'a replay signer key is registered unrevoked'; END IF;
        NEW.c_2ad04b7d := clock_timestamp();   -- never supplied by the writer
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'replay signer keys are never removed'; END IF;
    IF NEW.c_f57af9ad IS DISTINCT FROM OLD.c_f57af9ad OR NEW.c_9c9307e0 IS DISTINCT FROM OLD.c_9c9307e0
       OR NEW.c_83f7d43c IS DISTINCT FROM OLD.c_83f7d43c OR NEW.c_2ad04b7d IS DISTINCT FROM OLD.c_2ad04b7d THEN
        RAISE EXCEPTION 'a replay signer key is immutable except for its one-time revoke-from';
    END IF;
    IF OLD.c_d7d63a67 IS NOT NULL THEN RAISE EXCEPTION 'the revoke-from of a replay signer key is set once'; END IF;
    IF NEW.c_d7d63a67 IS NULL THEN RAISE EXCEPTION 'an update of a replay signer key only sets its revoke-from'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_6185cf0c BEFORE INSERT OR UPDATE OR DELETE ON public.t_23985c22 FOR EACH ROW EXECUTE FUNCTION public.f_d1876eea();
CREATE TRIGGER r_44b8b097 BEFORE TRUNCATE ON public.t_23985c22 FOR EACH STATEMENT EXECUTE FUNCTION public.f_d1876eea();

-- the grant shell of design 4.3: the stored columns only; the message the signature covers is rebuilt from rows by later slices
CREATE TABLE public.t_d9cc2112 (
    c_40570bad uuid NOT NULL,
    c_e660f536 varchar(40) NOT NULL,
    c_47bafbcb uuid NOT NULL,
    c_773b4e4b uuid NOT NULL,
    c_2394a822 uuid NOT NULL,
    c_31ac9d98 bigint NOT NULL,
    c_ff742bad bigint NOT NULL,
    c_4e121a52 char(64) NOT NULL,
    c_e31707eb text NOT NULL,
    c_5cf7b9d7 bytea NOT NULL,
    c_467d52a0 timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT k_fb59dc05 PRIMARY KEY (c_40570bad),
    CONSTRAINT k_9dd70192 FOREIGN KEY (c_e660f536) REFERENCES public.t_23985c22 (c_f57af9ad),
    CONSTRAINT k_dd2cb76f FOREIGN KEY (c_47bafbcb) REFERENCES public.t_d29d5b43 (c_57ecf0f3),
    CONSTRAINT k_c4f1369b CHECK (c_31ac9d98 >= 0 AND c_ff742bad > c_31ac9d98
        AND c_4e121a52 ~ '^[0-9a-f]{64}$' AND char_length(c_e31707eb) >= 1 AND length(c_5cf7b9d7) = 384)
);
CREATE INDEX k_fcb6e5b6 ON public.t_d9cc2112 (c_47bafbcb);
CREATE INDEX k_9d043160 ON public.t_d9cc2112 (c_e660f536);

-- a grant is used at most once and an execution has at most one grant (design 4.3)
CREATE TABLE public.t_a5e631f4 (
    c_809c486e uuid NOT NULL,
    c_632bac5e uuid NOT NULL,
    CONSTRAINT k_00d2d39d PRIMARY KEY (c_809c486e),
    CONSTRAINT k_ca640ac4 UNIQUE (c_632bac5e),
    CONSTRAINT k_0ce6a270 FOREIGN KEY (c_809c486e) REFERENCES public.t_5b1a018a (c_02771c43),
    CONSTRAINT k_c2ce1b7e FOREIGN KEY (c_632bac5e) REFERENCES public.t_d9cc2112 (c_40570bad)
);

-- append-only guards, as in 0036: a row is written once (the grant's accepted-at is the database clock) and is never updated, deleted or truncated
CREATE FUNCTION public.f_8628d9f4() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' AND TG_TABLE_NAME = 't_d9cc2112' THEN
        NEW.c_467d52a0 := clock_timestamp();   -- never supplied by the writer
        RETURN NEW;
    END IF;
    IF TG_OP = 'INSERT' THEN RETURN NEW; END IF;
    RAISE EXCEPTION 'replay grants and their uses are append-only';
END;
$$;
CREATE TRIGGER r_0f3451bf BEFORE INSERT OR UPDATE OR DELETE ON public.t_d9cc2112 FOR EACH ROW EXECUTE FUNCTION public.f_8628d9f4();
CREATE TRIGGER r_af560658 BEFORE TRUNCATE ON public.t_d9cc2112 FOR EACH STATEMENT EXECUTE FUNCTION public.f_8628d9f4();
CREATE TRIGGER r_edac16a2 BEFORE INSERT OR UPDATE OR DELETE ON public.t_a5e631f4 FOR EACH ROW EXECUTE FUNCTION public.f_8628d9f4();
CREATE TRIGGER r_bbefa1f4 BEFORE TRUNCATE ON public.t_a5e631f4 FOR EACH STATEMENT EXECUTE FUNCTION public.f_8628d9f4();

-- the verifier (design 4.2): TRUE only for a valid RSASSA-PKCS1-v1_5 / SHA-256 signature, exponent 65537, modulus of exactly 3072 bits; anything else is FALSE (never an error for bad input)
CREATE FUNCTION public.f_327807fe(p_modulus numeric, p_signature bytea, p_message bytea) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE
    c_digest_info CONSTANT bytea := decode('3031300d060960864801650304020105000420', 'hex');
    v_s numeric := 0;
    v_em numeric := 0;
    v_r numeric;
    v_expected bytea;
    i integer;
BEGIN
    IF p_modulus IS NULL OR p_signature IS NULL OR p_message IS NULL THEN RETURN false; END IF;
    IF length(p_signature) <> 384 THEN RETURN false; END IF;
    IF trunc(p_modulus) <> p_modulus OR mod(p_modulus, 2) <> 1 OR p_modulus < power(2::numeric, 3071) OR p_modulus >= power(2::numeric, 3072) THEN RETURN false; END IF;
    FOR i IN 0..383 LOOP v_s := v_s * 256 + get_byte(p_signature, i); END LOOP;
    IF v_s >= p_modulus THEN RETURN false; END IF;
    v_r := v_s;
    FOR i IN 1..16 LOOP v_r := mod(v_r * v_r, p_modulus); END LOOP;
    v_r := mod(v_r * v_s, p_modulus);
    v_expected := decode('0001', 'hex') || decode(repeat('ff', 330), 'hex') || decode('00', 'hex') || c_digest_info || sha256(p_message);
    FOR i IN 0..383 LOOP v_em := v_em * 256 + get_byte(v_expected, i); END LOOP;
    RETURN v_r = v_em;
END;
$$;

REVOKE ALL ON TABLE public.t_23985c22 FROM PUBLIC;
REVOKE ALL ON TABLE public.t_d9cc2112 FROM PUBLIC;
REVOKE ALL ON TABLE public.t_a5e631f4 FROM PUBLIC;
