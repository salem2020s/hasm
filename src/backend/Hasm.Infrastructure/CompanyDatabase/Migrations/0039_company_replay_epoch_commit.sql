-- Migration 0039: the commit-time checks of a replay execution grant (Phase 3.8D threat T2, slice A3; design docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md sections 4.3, 4.4 and 16, message version hasm-replay-v5).
-- Forward-only and ADDITIVE: new functions and 15 new DEFERRED constraint triggers (one on each table the epoch text or the grant message reads, plus the execution and grant-use tables); no table is created or altered, nothing existing is replaced or switched off.
-- The company runtime role still holds SELECT only on the grant tables and on the four execution tables: nothing can reach these triggers through the application, activation stays closed.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0039-replay-epoch-commit.cjs (logical dictionary: CompanySchema.ReplayEpoch.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS: at commit every execution of the transaction must have exactly ONE grant use whose grant is the execution's (plan and executor), has not expired (database clock, epoch seconds), whose key was not revoked
-- before the grant was accepted, whose signature VERIFIES over the message rebuilt NOW from the rows (a closure, line or approval changed after the grant was accepted fails), and whose signed epoch digest equals the SHA-256 of the
-- EPOCH TEXT of design 4.4 rebuilt from the rows this execution wrote: operations (O), entries (E), layers (L), consumptions (C), the remaining figures of every layer touched (R) and the totals of every position touched (P).
-- A position's final figures are bound by its P line (the position row) and by the running figures of its last entry (E line); the cost position guard of 0014 keeps the two equal, so a mismatch cannot exist in the rows. The journal binding (design 4.8) is slice A4. A deviation anywhere (one amount, one quantity, an extra or missing row) changes the text and is refused.
-- Each trigger is named so that it fires AFTER the 0036 deferred trigger of the same table on the same event. Name resolution (design 4.5): no search_path and no SECURITY DEFINER; every relation is written public.<name>; the text uses only
-- fixed formats (lower-case uuids, decimal integers, numeric at its declared scale 10 or 6, dates by to_char with a fixed pattern, LF as chr(10)), so no session setting can change a byte.

-- the epoch text of an execution (design 4.4): O, E, L, C, R and P lines in this order, LF-joined with no trailing LF; an execution with no rows gives the empty string
CREATE FUNCTION public.f_0eb1b095(p_execution uuid) RETURNS text LANGUAGE sql AS $$
    WITH x AS (
        SELECT e.c_07602789 AS posting FROM public.t_5b1a018a e WHERE e.c_02771c43 = p_execution
    ), mem AS (
        SELECT m.c_317cffcf AS op, m.c_1977183c AS role, m.c_6968655e AS orig
          FROM public.t_3bd31c9d m WHERE m.c_e909f2d4 = p_execution
    ), ent AS (
        SELECT e.c_ed61ae1a AS id, e.c_9ee52677 AS item, e.c_0f65c03c AS wh, e.c_2affbce9 AS seq, m.role AS role, e.c_4f9e777a AS reverses,
               e.c_4105796c AS kind, e.c_e0cd1a14 AS direction, e.c_12665ffb AS d, e.c_dba27822 AS q, e.c_ece66ed5 AS a,
               e.c_3437e13d AS qa, e.c_30804877 AS va
          FROM public.t_bc727c1d e JOIN mem m ON m.op = e.c_eb34b7d8
    ), lay AS (
        SELECT l.c_d784aea8 AS id, l.c_c4178514 AS item, l.c_f0903b24 AS wh, l.c_bf7d08a1 AS seq, en.seq AS entry_seq, l.c_c50486e0 AS oq,
               l.c_f5a791ec AS oa, l.c_1c8be8cf AS source_layer
          FROM public.t_3147676c l JOIN ent en ON en.id = l.c_07583c02
    ), con AS (
        SELECT en.item AS e_item, en.wh AS e_wh, en.seq AS e_seq, l.c_c4178514 AS l_item, l.c_f0903b24 AS l_wh, l.c_bf7d08a1 AS l_seq, c.c_9b2217bf AS layer_id,
               c.c_13147040 AS q, c.c_b7bc1937 AS a, c.c_90e0409c AS restores
          FROM public.t_6df3b92e c JOIN ent en ON en.id = c.c_b17f9a0c JOIN public.t_3147676c l ON l.c_d784aea8 = c.c_9b2217bf
    ), touched AS (
        SELECT id FROM lay UNION SELECT layer_id FROM con
    ), lines AS (
        SELECT 1 AS grp, m.role::bigint AS n0, m.orig AS u1, NULL::uuid AS u2, NULL::bigint AS n1, NULL::uuid AS u3, NULL::uuid AS u4, NULL::bigint AS n2, NULL::boolean AS b1,
               'O|' || m.role::text || '|' || COALESCE(m.orig::text, '-') || '|' || o.c_7f2ea712::text || '|' || to_char(o.c_a84fdcdc, 'YYYY-MM-DD')
               || '|' || COALESCE(o.c_03a68cea::text, '-') || '|' || COALESCE(encode(sha256(convert_to(o.c_7114c862, 'UTF8')), 'hex'), '-')
               || '|' || CASE WHEN o.c_b18070d3 IS NULL THEN '-' WHEN o.c_b18070d3 = (SELECT posting FROM x) THEN 'execution-posting' ELSE o.c_b18070d3::text END
               || '|' || encode(sha256(convert_to(o.c_b45c4009, 'UTF8')), 'hex') || '|' || o.c_aba24d32::text AS line
          FROM mem m JOIN public.t_a648ca40 o ON o.c_f47f15da = m.op
        UNION ALL
        SELECT 2, 0, e.item, e.wh, e.seq, NULL, NULL, NULL, NULL,
               'E|' || e.item::text || '|' || e.wh::text || '|' || e.seq::text || '|' || e.role::text || '|'
               || CASE e.role WHEN 1 THEN COALESCE(e.reverses::text, '-')
                              WHEN 3 THEN COALESCE((SELECT g.c_ea245623::text FROM public.t_c38ba586 g
                                                     WHERE g.c_1a01c03e = e.id AND g.c_dcb2e603 = p_execution), '-')
                              ELSE '-' END
               || '|' || e.kind::text || '|' || e.direction::text || '|' || to_char(e.d, 'YYYY-MM-DD') || '|' || e.q::numeric(28,10)::text || '|' || e.a::numeric(28,6)::text
               || '|' || e.qa::numeric(28,10)::text || '|' || e.va::numeric(28,6)::text
          FROM ent e
        UNION ALL
        SELECT 3, 0, l.item, l.wh, l.seq, NULL, NULL, NULL, NULL,
               'L|' || l.item::text || '|' || l.wh::text || '|' || l.seq::text || '|' || l.entry_seq::text || '|' || l.oq::numeric(28,10)::text || '|' || l.oa::numeric(28,6)::text
               || '|' || COALESCE((SELECT s.c_c4178514::text || '/' || s.c_f0903b24::text || '/' || s.c_bf7d08a1::text
                                     FROM public.t_3147676c s WHERE s.c_d784aea8 = l.source_layer), '-')
          FROM lay l
        UNION ALL
        SELECT 4, 0, c.e_item, c.e_wh, c.e_seq, c.l_item, c.l_wh, c.l_seq, c.restores,
               'C|' || c.e_item::text || '|' || c.e_wh::text || '|' || c.e_seq::text || '|' || c.l_item::text || '|' || c.l_wh::text || '|' || c.l_seq::text
               || '|' || c.q::numeric(28,10)::text || '|' || c.a::numeric(28,6)::text || '|' || CASE WHEN c.restores THEN 't' ELSE 'f' END
          FROM con c
        UNION ALL
        SELECT 5, 0, l.c_c4178514, l.c_f0903b24, l.c_bf7d08a1, NULL, NULL, NULL, NULL,
               'R|' || l.c_c4178514::text || '|' || l.c_f0903b24::text || '|' || l.c_bf7d08a1::text || '|' || l.c_69f58f45::numeric(28,10)::text
               || '|' || l.c_d25b66c0::numeric(28,6)::text
          FROM public.t_3147676c l JOIN touched t ON t.id = l.c_d784aea8
        UNION ALL
        SELECT 6, 0, g.item, g.wh, NULL, NULL, NULL, NULL, NULL,
               'P|' || g.item::text || '|' || g.wh::text || '|' || (g.first_seq - 1)::text || '|' || po.c_ad2af2e5::text || '|' || po.c_8e61b559::numeric(28,10)::text
               || '|' || po.c_2e1921d0::numeric(28,6)::text
               || '|' || (SELECT count(*) FROM public.t_3147676c o WHERE o.c_c4178514 = g.item AND o.c_f0903b24 = g.wh AND o.c_69f58f45 > 0)::text
          FROM (SELECT e.item, e.wh, min(e.seq) AS first_seq FROM ent e GROUP BY e.item, e.wh) g
          JOIN public.t_a2903683 po ON po.c_3380c61e = g.item AND po.c_f0a91938 = g.wh
    )
    SELECT COALESCE(string_agg(line, chr(10) ORDER BY grp, n0, u1, u2, n1, u3, u4, n2, b1), '') FROM lines
$$;

-- every commit-time rule for ONE execution; raises a reason when any fails
CREATE FUNCTION public.f_217072b6(p_execution uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    x public.t_5b1a018a%ROWTYPE;
    g public.t_d9cc2112%ROWTYPE;
    k public.t_23985c22%ROWTYPE;
    v_uses bigint;
    v_head text;
BEGIN
    SELECT * INTO x FROM public.t_5b1a018a WHERE c_02771c43 = p_execution;
    IF NOT FOUND THEN RAISE EXCEPTION 'execution_not_found'; END IF;
    SELECT count(*) INTO v_uses FROM public.t_a5e631f4 WHERE c_809c486e = p_execution;
    IF v_uses <> 1 THEN RAISE EXCEPTION 'execution_needs_exactly_one_grant_use'; END IF;
    SELECT gr.* INTO g FROM public.t_d9cc2112 gr JOIN public.t_a5e631f4 u ON u.c_632bac5e = gr.c_40570bad WHERE u.c_809c486e = p_execution;
    IF g.c_47bafbcb <> x.c_934901c6 OR g.c_773b4e4b <> x.c_17e57b73 THEN
        RAISE EXCEPTION 'grant_does_not_match_the_execution';
    END IF;
    IF extract(epoch from clock_timestamp()) > g.c_ff742bad THEN RAISE EXCEPTION 'grant_expired_at_commit'; END IF;
    SELECT * INTO k FROM public.t_23985c22 WHERE c_f57af9ad = g.c_e660f536;
    IF NOT FOUND THEN RAISE EXCEPTION 'grant_key_unknown'; END IF;
    IF k.c_d7d63a67 IS NOT NULL AND g.c_467d52a0 >= k.c_d7d63a67 THEN RAISE EXCEPTION 'grant_key_revoked'; END IF;
    -- the signature again, over the message rebuilt NOW from the rows
    v_head := public.f_b91ccdde(x.c_934901c6, g.c_4e121a52::text, g.c_773b4e4b);
    IF v_head IS NULL THEN RAISE EXCEPTION 'grant_plan_not_found'; END IF;
    IF NOT public.f_327807fe(k.c_9c9307e0, g.c_5cf7b9d7, convert_to(public.f_71bbfd33(v_head, g.c_e31707eb,
            g.c_40570bad, g.c_2394a822, g.c_e660f536, g.c_31ac9d98, g.c_ff742bad), 'UTF8')) THEN
        RAISE EXCEPTION 'grant_signature_invalid_at_commit';
    END IF;
    -- the epoch text rebuilt from the rows hashes to the signed epoch digest
    IF encode(sha256(convert_to(public.f_0eb1b095(p_execution), 'UTF8')), 'hex') <> g.c_4e121a52::text THEN RAISE EXCEPTION 'epoch_differs_from_the_signed_epoch'; END IF;
END;
$$;

-- H1 (review of PR 238): a check queued only at INSERT of the execution can be fired early (SET CONSTRAINTS ... IMMEDIATE) and later changes would commit unchecked. So EVERY table the epoch text or the grant message reads carries
-- a deferred row trigger on INSERT, UPDATE and DELETE that re-runs the full check for every execution written by THIS transaction (created_xact = pg_current_xact_id()); the last write of the transaction therefore queues a
-- check that runs after it (at commit, or at the end of that statement in immediate mode). The cost is one full check per queued event at commit (accepted; measured in the tests, see the pull request).
CREATE FUNCTION public.f_246d6027() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_execution uuid;
BEGIN
    FOR v_execution IN SELECT x.c_02771c43 FROM public.t_5b1a018a x WHERE x.c_fbd0bc4b = pg_current_xact_id() LOOP
        PERFORM public.f_217072b6(v_execution);
    END LOOP;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER r_eec39f16 AFTER INSERT OR UPDATE OR DELETE ON public.t_5b1a018a DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_21aaa89e AFTER INSERT OR UPDATE OR DELETE ON public.t_a5e631f4 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_59cea2b3 AFTER INSERT OR UPDATE OR DELETE ON public.t_a648ca40 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_b4fcc7fe AFTER INSERT OR UPDATE OR DELETE ON public.t_3bd31c9d DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_a8f8c2e8 AFTER INSERT OR UPDATE OR DELETE ON public.t_bc727c1d DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_af96927f AFTER INSERT OR UPDATE OR DELETE ON public.t_3147676c DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_c6743ae1 AFTER INSERT OR UPDATE OR DELETE ON public.t_6df3b92e DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_9698bfcc AFTER INSERT OR UPDATE OR DELETE ON public.t_a2903683 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_c2ddf748 AFTER INSERT OR UPDATE OR DELETE ON public.t_c38ba586 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_2de0392b AFTER INSERT OR UPDATE OR DELETE ON public.t_d29d5b43 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_e2f99ea3 AFTER INSERT OR UPDATE OR DELETE ON public.t_a1458a29 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_63128ac4 AFTER INSERT OR UPDATE OR DELETE ON public.t_33eef4c2 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_039cd845 AFTER INSERT OR UPDATE OR DELETE ON public.t_209d0f15 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_790740c4 AFTER INSERT OR UPDATE OR DELETE ON public.t_23985c22 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
CREATE CONSTRAINT TRIGGER r_5efc60ea AFTER INSERT OR UPDATE OR DELETE ON public.t_d9cc2112 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f_246d6027();
