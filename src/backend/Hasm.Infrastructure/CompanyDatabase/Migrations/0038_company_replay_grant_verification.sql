-- Migration 0038: insert-time verification of a replay execution grant (Phase 3.8D threat T2, slice A2; design docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md sections 4.3 to 4.5 and 16, message version hasm-replay-v5).
-- Forward-only and ADDITIVE: new functions and two new BEFORE INSERT triggers on the two grant tables of 0037; no table is created or altered, no existing function or trigger is replaced or switched off. INERT: the company
-- runtime role still holds SELECT only on the grant tables (no INSERT grant), nothing calls these functions but the two triggers, and no execution can be written.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0038-replay-grant-verification.cjs (logical dictionary: CompanySchema.ReplayGrant.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: the grant message of design 4.4 is REBUILT here from the rows of the plan, its approval, its closure lines and the company identity (never taken from the grant), and the signature is verified over those exact bytes with
-- the verifier of 0037. The builders are pure functions of their arguments (formatting only) plus thin row readers; every text uses only fixed formats (lower-case uuids, decimal integers, numeric at its declared scale, dates by
-- to_char with a fixed pattern, LF as chr(10)), so TimeZone, DateStyle, IntervalStyle, bytea_output, extra_float_digits and lc_numeric cannot change a byte (design 4.5).
-- The insert of a grant is refused unless: the plan is approved and its approval is sound (0036); the key exists and is not revoked at the database clock; the key's signer (when it names one) is the grant's signer; the window is at
-- most 2 hours (D4) and contains the database clock (epoch seconds); the signer is independent of the plan (creator, event editors, document creator, editors and submitter), is not the approver and is not the executor; the
-- attestation is well formed, names the plan's creator and the approval's approver, uses a known method, has confirmation times inside the allowed band and carries the request digest rebuilt from the rows; and the signature
-- verifies. The insert of a grant USE is refused unless the grant's plan and executor are the execution's and the window still holds (the commit-time re-verification and the epoch and journal bindings are slices A3 and A4).
-- Name resolution (design 4.5): no search_path and no SECURITY DEFINER; every relation is written public.<name>; the company runtime role holds neither TEMPORARY nor CREATE. EXECUTE on these functions stays with PUBLIC (decision of slice A2,
-- review L1 of PR 233): they are invoker-rights readers of rows the caller may already read, they write nothing, and the triggers that call them run as the inserting role, which therefore needs to execute them.

-- one plan line, exactly as the lines text of 4.4 writes it
CREATE FUNCTION public.f_4ac1befb(p_no integer, p_posting uuid, p_date date, p_period uuid, p_branch uuid, p_account uuid, p_debit numeric, p_credit numeric) RETURNS text LANGUAGE sql AS $$
    SELECT p_no::text || '|' || COALESCE(p_posting::text, '-') || '|' || to_char(p_date, 'YYYY-MM-DD') || '|' || p_period::text || '|' || p_branch::text || '|' || p_account::text
        || '|' || p_debit::numeric(28,6)::text || '|' || p_credit::numeric(28,6)::text
$$;

-- the lines text of a plan: one line per plan line ordered by line number, LF-joined, no trailing LF; a plan with no lines gives the empty string
CREATE FUNCTION public.f_0d106e4a(p_plan uuid) RETURNS text LANGUAGE sql AS $$
    SELECT COALESCE(string_agg(public.f_4ac1befb(l.c_d3162a08, l.c_20686467, l.c_3ef0fd55, l.c_87722ae4,
        l.c_7e924492, l.c_e4446dd3, l.c_e92133d8, l.c_89f20e1e), chr(10) ORDER BY l.c_d3162a08), '')
    FROM public.t_33eef4c2 l WHERE l.c_d2a0bbdc = p_plan
$$;

-- the first 17 lines of the grant message (version through executor), LF-joined: this is also what the request digest covers
CREATE FUNCTION public.f_d1f54594(p_company uuid, p_plan uuid, p_revision bigint, p_approval uuid, p_approver uuid, p_creator uuid, p_document uuid, p_document_revision bigint, p_item uuid, p_warehouse uuid,
    p_payload_hash text, p_fingerprints text, p_closure text, p_lines_hash text, p_epoch text, p_executor uuid) RETURNS text LANGUAGE sql AS $$
    SELECT 'hasm-replay-v5' || chr(10) || 'grant' || chr(10) || 'company=' || p_company::text || chr(10) || 'plan=' || p_plan::text || chr(10) || 'revision=' || p_revision::text
        || chr(10) || 'approval=' || p_approval::text || chr(10) || 'approver=' || p_approver::text || chr(10) || 'creator=' || p_creator::text
        || chr(10) || 'document=' || p_document::text || '@' || p_document_revision::text || chr(10) || 'item=' || p_item::text || chr(10) || 'warehouse=' || p_warehouse::text
        || chr(10) || 'payload=' || p_payload_hash || chr(10) || 'fingerprints=' || p_fingerprints || chr(10) || 'closure=' || p_closure || chr(10) || 'lines=' || p_lines_hash
        || chr(10) || 'epoch=' || p_epoch || chr(10) || 'executor=' || p_executor::text
$$;

-- the request digest: the first 16 hex characters of the SHA-256 of those 17 lines
CREATE FUNCTION public.f_5a1d9b15(p_head text) RETURNS text LANGUAGE sql AS $$
    SELECT left(encode(sha256(convert_to(p_head, 'UTF8')), 'hex'), 16)
$$;

-- the whole grant message: the 17 lines, then the attestation hash, the grant id, the signer, the key id and the window
CREATE FUNCTION public.f_71bbfd33(p_head text, p_attestation text, p_grant uuid, p_signer uuid, p_key text, p_not_before bigint, p_not_after bigint) RETURNS text LANGUAGE sql AS $$
    SELECT p_head || chr(10) || 'attestation=' || encode(sha256(convert_to(p_attestation, 'UTF8')), 'hex') || chr(10) || 'grant=' || p_grant::text || chr(10) || 'signer=' || p_signer::text
        || chr(10) || 'key=' || p_key || chr(10) || 'not-before=' || p_not_before::text || chr(10) || 'not-after=' || p_not_after::text
$$;

-- the 17 lines of a plan, read from its rows: NULL when the plan or its approval does not exist
CREATE FUNCTION public.f_b91ccdde(p_plan uuid, p_epoch text, p_executor uuid) RETURNS text LANGUAGE sql AS $$
    SELECT public.f_d1f54594(i.c_2d7eb73b, p.c_57ecf0f3, a.c_1409be1e, p.c_360cd595, a.c_263ac270,
        p.c_30e744d0, p.c_8a883ba7, p.c_54faa029, p.c_958849c3, p.c_03c8ca55,
        encode(sha256(convert_to(p.c_d2183406, 'UTF8')), 'hex'),
        p.c_6185829f::text || ',' || p.c_7ee34d64::text || ',' || p.c_469d9275::text || ',' || p.c_8755e9a4::text
            || ',' || p.c_0c818e38::text || ',' || p.c_9523686c::text || ',' || p.c_0ada19bb::text,
        p.c_f00fd9d5::text, encode(sha256(convert_to(public.f_0d106e4a(p.c_57ecf0f3), 'UTF8')), 'hex'), p_epoch, p_executor)
    FROM public.t_d29d5b43 p
    JOIN public.t_a1458a29 a ON a.c_ea73ad4f = p.c_360cd595 AND a.c_ded9e195 = p.c_57ecf0f3
    CROSS JOIN public.t_209d0f15 i
    WHERE p.c_57ecf0f3 = p_plan
$$;

-- NULL when the attestation (design 4.4, N2) is well formed and consistent with the plan and the request, otherwise the reason
CREATE FUNCTION public.f_281c7417(p_attestation text, p_plan uuid, p_epoch text, p_executor uuid, p_not_before bigint) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    l text[];
    v_creator uuid;
    v_approver uuid;
    v_head text;
    v_at bigint;
BEGIN
    IF p_attestation IS NULL THEN RETURN 'attestation_malformed'; END IF;
    l := string_to_array(p_attestation, chr(10));
    IF array_length(l, 1) IS DISTINCT FROM 6 OR l[1] <> 'hasm-replay-attestation-v1' THEN RETURN 'attestation_malformed'; END IF;
    IF l[2] !~ '^creator=[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}[|](in-person|phone|video)[|](0|[1-9][0-9]{0,11})$' THEN RETURN 'attestation_creator_line'; END IF;
    IF l[3] !~ '^approver=[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}[|](in-person|phone|video)[|](0|[1-9][0-9]{0,11})$' THEN RETURN 'attestation_approver_line'; END IF;
    IF l[4] !~ '^request=[0-9a-f]{16}$' THEN RETURN 'attestation_request_line'; END IF;
    IF l[5] !~ '^participants=(0|[1-9][0-9]{0,8})$' THEN RETURN 'attestation_participants_line'; END IF;
    IF left(l[6], 5) <> 'note=' OR char_length(substr(l[6], 6)) > 500 THEN RETURN 'attestation_note'; END IF;
    SELECT p.c_30e744d0, a.c_263ac270 INTO v_creator, v_approver
      FROM public.t_d29d5b43 p
      JOIN public.t_a1458a29 a ON a.c_ea73ad4f = p.c_360cd595 AND a.c_ded9e195 = p.c_57ecf0f3
     WHERE p.c_57ecf0f3 = p_plan;
    IF NOT FOUND THEN RETURN 'attestation_plan_not_found'; END IF;
    IF split_part(split_part(l[2], '|', 1), '=', 2) <> v_creator::text THEN RETURN 'attestation_creator_mismatch'; END IF;
    IF split_part(split_part(l[3], '|', 1), '=', 2) <> v_approver::text THEN RETURN 'attestation_approver_mismatch'; END IF;
    v_at := split_part(l[2], '|', 3)::bigint;
    IF v_at > p_not_before + 300 OR v_at < p_not_before - 86400 THEN RETURN 'attestation_creator_time'; END IF;
    v_at := split_part(l[3], '|', 3)::bigint;
    IF v_at > p_not_before + 300 OR v_at < p_not_before - 86400 THEN RETURN 'attestation_approver_time'; END IF;
    v_head := public.f_b91ccdde(p_plan, p_epoch, p_executor);
    IF v_head IS NULL THEN RETURN 'attestation_plan_not_found'; END IF;
    IF substr(l[4], 9) <> public.f_5a1d9b15(v_head) THEN RETURN 'attestation_request_mismatch'; END IF;
    RETURN NULL;
END;
$$;

-- the insert-time rules of a grant (design 4.3)
CREATE FUNCTION public.f_6f7af27d() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p public.t_d29d5b43%ROWTYPE;
    k public.t_23985c22%ROWTYPE;
    v_approver uuid;
    v_problem text;
    v_now numeric;
    v_head text;
BEGIN
    SELECT * INTO p FROM public.t_d29d5b43 WHERE c_57ecf0f3 = NEW.c_47bafbcb;
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_plan_not_found'; END IF;
    IF p.c_b38a0440 <> 2 OR p.c_360cd595 IS NULL THEN RAISE EXCEPTION 'grant_plan_not_approved'; END IF;
    v_problem := public.f_73471693(p.c_57ecf0f3, p.c_360cd595);
    IF v_problem IS NOT NULL THEN RAISE EXCEPTION 'grant_plan_approval_unsound: %', v_problem; END IF;
    SELECT * INTO k FROM public.t_23985c22 WHERE c_f57af9ad = NEW.c_e660f536;
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_key_unknown'; END IF;
    IF k.c_d7d63a67 IS NOT NULL AND clock_timestamp() >= k.c_d7d63a67 THEN RAISE EXCEPTION 'grant_key_revoked'; END IF;
    -- a key with no signer is the installation key of resolution (ii), which is not built: until it is, every key must name the person it belongs to, and the grant's signer must be that person (decision D1: a separately registered signer)
    IF k.c_83f7d43c IS NULL THEN RAISE EXCEPTION 'grant_key_has_no_signer'; END IF;
    IF k.c_83f7d43c <> NEW.c_2394a822 THEN RAISE EXCEPTION 'grant_signer_is_not_the_key_owner'; END IF;
    IF NEW.c_ff742bad - NEW.c_31ac9d98 > 7200 THEN RAISE EXCEPTION 'grant_window_longer_than_2_hours'; END IF;
    v_now := extract(epoch from clock_timestamp());
    IF v_now < NEW.c_31ac9d98 OR v_now > NEW.c_ff742bad THEN RAISE EXCEPTION 'grant_outside_its_window'; END IF;
    SELECT a.c_263ac270 INTO v_approver FROM public.t_a1458a29 a WHERE a.c_ea73ad4f = p.c_360cd595;
    IF NOT public.f_e08495de(p.c_57ecf0f3, NEW.c_2394a822) OR NEW.c_2394a822 = v_approver
       OR NEW.c_2394a822 = NEW.c_773b4e4b THEN
        RAISE EXCEPTION 'grant_signer_not_independent';
    END IF;
    v_problem := public.f_281c7417(NEW.c_e31707eb, p.c_57ecf0f3, NEW.c_4e121a52::text, NEW.c_773b4e4b, NEW.c_31ac9d98);
    IF v_problem IS NOT NULL THEN RAISE EXCEPTION 'grant_attestation_invalid: %', v_problem; END IF;
    v_head := public.f_b91ccdde(p.c_57ecf0f3, NEW.c_4e121a52::text, NEW.c_773b4e4b);
    IF v_head IS NULL THEN RAISE EXCEPTION 'grant_plan_not_found'; END IF;
    IF NOT public.f_327807fe(k.c_9c9307e0, NEW.c_5cf7b9d7, convert_to(public.f_71bbfd33(v_head, NEW.c_e31707eb,
            NEW.c_40570bad, NEW.c_2394a822, NEW.c_e660f536, NEW.c_31ac9d98, NEW.c_ff742bad), 'UTF8')) THEN
        RAISE EXCEPTION 'grant_signature_invalid';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_9f146b0a BEFORE INSERT ON public.t_d9cc2112 FOR EACH ROW EXECUTE FUNCTION public.f_6f7af27d();

-- the insert-time rules of a grant use: the grant is the execution's (plan and executor) and its window still holds
CREATE FUNCTION public.f_ac4f8bf5() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    g public.t_d9cc2112%ROWTYPE;
    x public.t_5b1a018a%ROWTYPE;
    v_now numeric;
BEGIN
    SELECT * INTO g FROM public.t_d9cc2112 WHERE c_40570bad = NEW.c_632bac5e;
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_use_grant_not_found'; END IF;
    SELECT * INTO x FROM public.t_5b1a018a WHERE c_02771c43 = NEW.c_809c486e;
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_use_execution_not_found'; END IF;
    IF x.c_934901c6 <> g.c_47bafbcb THEN RAISE EXCEPTION 'grant_use_wrong_plan'; END IF;
    IF x.c_17e57b73 <> g.c_773b4e4b THEN RAISE EXCEPTION 'grant_use_wrong_executor'; END IF;
    v_now := extract(epoch from clock_timestamp());
    IF v_now < g.c_31ac9d98 OR v_now > g.c_ff742bad THEN RAISE EXCEPTION 'grant_use_outside_its_window'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d93b3b80 BEFORE INSERT ON public.t_a5e631f4 FOR EACH ROW EXECUTE FUNCTION public.f_ac4f8bf5();
