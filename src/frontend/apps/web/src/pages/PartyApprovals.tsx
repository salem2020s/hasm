import { ApiError, type PartyProposal, type PartyProposalSummary, type SharedIdentity } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, Field, Loading, PageHeader } from "../components";

/**
 * Central approval of shared party data (Phase 3.9A), for identity administrators only (the server checks it again inside the deciding transaction). ONE implementation for Web and Desktop.
 * Approve a new-identity proposal as a NEW identity or by LINKING it to an existing identity of the registry; approve or reject a change proposal. The proposer can never decide.
 */
function useErrorText() {
  const { t } = useI18n();
  return useCallback((e: unknown) => {
    if (e instanceof ApiError) {
      const k = `ppa.error.${e.code}`;
      if (t(k) !== k) return t(k);
      if (e.status === 403) return t("error.forbidden");
      return t("error.generic");
    }
    return t("error.network");
  }, [t]);
}

const companyName = (p: { companyNameAr: string | null; companyNameEn: string | null; companyId: string }, locale: string) =>
  (locale === "ar" ? p.companyNameAr ?? p.companyNameEn : p.companyNameEn ?? p.companyNameAr) ?? p.companyId;

export function PartyApprovalsPage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const errText = useErrorText();
  const [status, setStatus] = useState<"" | "1" | "2" | "3">("1");
  const [rows, setRows] = useState<PartyProposalSummary[] | null>(null);
  const [open, setOpen] = useState<PartyProposal | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [rejecting, setRejecting] = useState(false);
  const [note, setNote] = useState("");
  const [term, setTerm] = useState("");
  const [found, setFound] = useState<SharedIdentity[]>([]);

  const admin = !!auth.user?.isIdentityAdministrator;
  const load = useCallback(async () => {
    if (!admin) return;
    try { setRows((await auth.api.listPartyProposals(status ? (Number(status) as 1 | 2 | 3) : undefined)).proposals); setError(null); }
    catch (e) { if (!auth.handleError(e)) setError(errText(e)); }
  }, [auth.api, auth.handleError, status, errText, admin]);
  useEffect(() => { void load(); }, [load]);

  if (!admin) return <Banner kind="error" testId="forbidden">{t("ppa.only_admins")}</Banner>;
  if (error && !rows) return <Banner kind="error">{error}</Banner>;
  if (!rows) return <Loading />;

  async function act(run: () => Promise<PartyProposal>, message: string) {
    setBusy(true); setError(null);
    try { setOpen(await run()); setNotice(message); await load(); }
    catch (e) { if (!auth.handleError(e)) setError(errText(e)); } finally { setBusy(false); }
  }

  if (open) {
    const mine = open.proposedBy === auth.user?.id;
    const pending = open.status === 1;
    const candidates = [...open.matches, ...found.filter((f) => !open.matches.some((m) => m.globalPartyId === f.globalPartyId))];
    return (
      <section aria-labelledby="page-title" data-testid="ppa-proposal">
        <PageHeader title={open.legalNameAr} description={`${companyName(open, locale)} — ${t(`pty.request_kind.${open.kind}`)}`} actions={<span className="chip" data-testid="ppa-status">{t(`ppa.status.${open.status}`)}</span>} />
        {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
        {error && <Banner kind="error" testId="form-error">{error}</Banner>}
        {mine && pending && <Banner kind="warning" testId="ppa-self">{t("ppa.self")}</Banner>}
        <Card title={t("pty.tab.identity")}>
          <dl className="details">
            <dt>{t("pty.kind")}</dt><dd>{t(`pty.kind.${open.partyKind}`)}</dd>
            <dt>{t("pty.legal_name_en")}</dt><dd><bdi dir="ltr">{open.legalNameEn ?? ""}</bdi></dd>
            <dt>{t("pty.tax_number")}</dt><dd><bdi dir="ltr">{open.taxNumber ?? ""}</bdi></dd>
            <dt>{t("pty.registration_number")}</dt><dd><bdi dir="ltr">{open.registrationNumber ?? ""}</bdi></dd>
            {open.kind === 2 && <><dt>{t("pty.identity_status")}</dt><dd>{t(`pty.identity_status.${open.proposedStatus}`)}</dd></>}
            <dt>{t("pty.proposal_note")}</dt><dd><bdi>{open.note ?? ""}</bdi></dd>
            <dt>{t("ppa.proposed_by")}</dt><dd><bdi>{open.proposedByName ?? ""}</bdi></dd>
            {open.target && <><dt>{t("ppa.target")}</dt><dd><bdi>{open.target.legalNameAr}</bdi> <bdi dir="ltr">{open.target.taxNumber ?? ""}</bdi></dd></>}
            {open.resolution && <><dt>{t("ppa.status")}</dt><dd>{t(`ppa.resolution.${open.resolution}`)}</dd></>}
            {open.decisionNote && <><dt>{t("ppa.reject_note")}</dt><dd><bdi>{open.decisionNote}</bdi></dd></>}
          </dl>
        </Card>
        {open.kind === 1 && pending && (
          <Card title={t("ppa.matches")}>
            <div className="toolbar">
              <Field id="ppa-search" label={t("ppa.search")} hint={t("ppa.search_hint")}>{(aria) => <input {...aria} type="search" value={term} onChange={(e) => setTerm(e.target.value)} />}</Field>
              <button type="button" disabled={!term.trim()} onClick={() => void auth.api.searchSharedIdentities(term.trim()).then((r) => setFound(r.identities)).catch((e) => setError(errText(e)))}>{t("ppa.search")}</button>
            </div>
            {candidates.length === 0 ? <p className="hint">{t("ppa.no_matches")}</p> : (
              <ul data-testid="ppa-matches">{candidates.map((m) => (
                <li key={m.globalPartyId}><bdi>{m.legalNameAr}</bdi> — <bdi dir="ltr">{m.taxNumber ?? ""} {m.registrationNumber ?? ""}</bdi>{" "}
                  <button type="button" disabled={busy || mine || m.status !== 1} onClick={() => void act(() => auth.api.approvePartyProposal(open.id, m.globalPartyId, open.revision), t("ppa.approved"))}>{t("ppa.link")}</button></li>
              ))}</ul>
            )}
          </Card>
        )}
        <div className="actions" data-testid="ppa-actions">
          <button type="button" onClick={() => { setOpen(null); setNotice(null); setFound([]); setTerm(""); }}>{t("ppa.back")}</button>
          {pending && <button type="button" className="primary" data-testid="ppa-approve" disabled={busy || mine} onClick={() => void act(() => auth.api.approvePartyProposal(open.id, null, open.revision), t("ppa.approved"))}>{open.kind === 1 ? t("ppa.approve_new") : t("ppa.approve_change")}</button>}
          {pending && <button type="button" className="danger" data-testid="ppa-reject" disabled={busy || mine} onClick={() => { setNote(""); setRejecting(true); }}>{t("ppa.reject")}</button>}
        </div>
        {rejecting && (
          <ConfirmDialog title={t("ppa.reject")} message={t("ppa.reject_note")} confirmLabel={t("form.confirm")} onCancel={() => setRejecting(false)}
            onConfirm={() => { setRejecting(false); if (!note.trim()) { setError(t("ppa.note_required")); return; } void act(() => auth.api.rejectPartyProposal(open.id, note.trim(), open.revision), t("ppa.rejected")); }}>
            <Field id="ppa-note" label={t("ppa.reject_note")} required>{(aria) => <textarea {...aria} value={note} rows={3} maxLength={500} onChange={(e) => setNote(e.target.value)} />}</Field>
          </ConfirmDialog>
        )}
      </section>
    );
  }

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("ppa.title")} description={t("ppa.subtitle")} />
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      {error && <Banner kind="error">{error}</Banner>}
      <Card title={t("ppa.title")}>
        <div className="toolbar">
          <Field id="ppa-status" label={t("ppa.status")}>{(aria) => <select {...aria} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}><option value="">{t("inv.filter_all")}</option>{(["1", "2", "3"] as const).map((s) => <option key={s} value={s}>{t(`ppa.status.${s}`)}</option>)}</select>}</Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("ppa.title")}</caption>
            <thead><tr><th scope="col">{t("ppa.company")}</th><th scope="col">{t("ppa.kind")}</th><th scope="col">{t("pty.legal_name")}</th><th scope="col">{t("pty.tax_number")}</th><th scope="col">{t("ppa.proposed_at")}</th><th scope="col">{t("ppa.status")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
            <tbody>
              {rows.length === 0 && <tr><td colSpan={7}>{t("ppa.none")}</td></tr>}
              {rows.map((p) => (
                <tr key={p.id} data-testid={`ppa-${p.id}`}>
                  <td><bdi>{companyName(p, locale)}</bdi></td><td>{t(`pty.request_kind.${p.kind}`)}</td><td><bdi>{p.legalNameAr}</bdi></td><td><bdi dir="ltr">{p.taxNumber ?? ""}</bdi></td>
                  <td><bdi dir="ltr">{p.proposedAtUtc.slice(0, 19).replace("T", " ")}</bdi></td><td>{t(`ppa.status.${p.status}`)}</td>
                  <td><button type="button" onClick={() => void auth.api.getPartyProposal(p.id).then((v) => { setNotice(null); setOpen(v); }).catch((e) => setError(errText(e)))}>{t("ppa.open")}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </section>
  );
}
