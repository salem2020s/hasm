import { ApiError, type LocalParty, type LocalPartyInput, type LocalPartySummary, type PartyCapabilities, type PartyKind, type PartyReconciliation, type SharedIdentityInput } from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, Field, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";

/**
 * Parties (Phase 3.9A): this company's local party records and their link to the centrally approved SHARED identity. ONE implementation for Web and Windows Desktop; the screen only drives the server,
 * which enforces every rule (company-wide permissions, the session company only, central approval, idempotent delivery, audit). The shared identity is identifying data only: there is no balance on it.
 * The editor uses the shared Tabs standard (Local data, Shared identity, Requests); the identity and the actions stay outside the tabs, and switching tabs never saves or resets input.
 */
const PAGE = 25;
const blank = (s: string) => (s.trim() ? s.trim() : null);

interface IdentityForm { kind: string; legalNameAr: string; legalNameEn: string; taxNumber: string; registrationNumber: string; status: string; note: string }
interface Form { code: string; displayName: string; notes: string; isActive: boolean; identity: IdentityForm }
const emptyIdentity: IdentityForm = { kind: "1", legalNameAr: "", legalNameEn: "", taxNumber: "", registrationNumber: "", status: "1", note: "" };
const snapLocal = (f: Form) => JSON.stringify([f.code, f.displayName, f.notes, f.isActive]);
const snapIdentity = (f: Form) => JSON.stringify(f.identity);
type Scope = "all" | "local" | "identity";
const identityInput = (i: IdentityForm, change: boolean, viewedSharedRevision?: number): SharedIdentityInput => ({
  kind: i.kind ? (Number(i.kind) as PartyKind) : null, legalNameAr: blank(i.legalNameAr), legalNameEn: blank(i.legalNameEn), taxNumber: blank(i.taxNumber), registrationNumber: blank(i.registrationNumber),
  status: change ? (Number(i.status) as 1 | 2) : null, note: blank(i.note),
  ...(change && viewedSharedRevision !== undefined ? { viewedSharedRevision } : {}),
});

type Editor = { mode: "new" } | { mode: "edit"; id: string };

function useErrorText() {
  const { t } = useI18n();
  return useCallback((e: unknown) => {
    if (e instanceof ApiError) {
      const k = `pty.error.${e.code}`;
      if (t(k) !== k) return t(k);
      if (e.status === 403) return t("error.forbidden");
      return t("error.generic");
    }
    return t("error.network");
  }, [t]);
}
const fieldText = (t: (k: string) => string, code: string) => { for (const k of [`pty.field.${code}`, `field.${code}`]) if (t(k) !== k) return t(k); return t("field.invalid"); };
const tabOf = (field: string): "details" | "identity" => (field.startsWith("identity") || ["kind", "legalNameAr", "legalNameEn", "taxNumber", "registrationNumber", "status", "note"].includes(field) ? "identity" : "details");

export function PartiesPage() {
  const { t } = useI18n();
  const auth = useAuth();
  const errText = useErrorText();
  const errRef = useRef(errText);
  errRef.current = errText;
  const { guard } = useUnsaved();
  const [caps, setCaps] = useState<PartyCapabilities | null>(null);
  const [rows, setRows] = useState<LocalPartySummary[] | null>(null);
  const [total, setTotal] = useState(0);
  const [search, setSearch] = useState("");
  const [offset, setOffset] = useState(0);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [report, setReport] = useState<PartyReconciliation | null>(null);
  const [busy, setBusy] = useState(false);
  const [registryDown, setRegistryDown] = useState(false);

  useEffect(() => { void auth.api.getPartyCapabilities().then(setCaps).catch((e) => { if (!auth.handleError(e)) setLoadError(errRef.current(e)); }); }, [auth.api, auth.handleError]);
  const load = useCallback(async () => {
    try { const p = await auth.api.listParties({ search: search.trim() || undefined, offset, limit: PAGE }); setRows(p.parties); setTotal(p.total); setRegistryDown(!!p.registryUnavailable); setLoadError(null); }
    catch (e) { if (!auth.handleError(e)) setLoadError(errRef.current(e)); }
  }, [auth.api, auth.handleError, search, offset]);
  useEffect(() => { const h = setTimeout(() => void load(), 250); return () => clearTimeout(h); }, [load]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!caps || !rows) return <Loading />;
  if (editor) {
    return <PartyEditor key={editor.mode === "edit" ? editor.id : "new"} editor={editor} caps={caps} onClose={() => guard(() => { setEditor(null); void load(); })} onChanged={(m) => { setNotice(m); void load(); }} onSwitch={(id) => setEditor({ mode: "edit", id })} />;
  }

  async function run(f: () => Promise<void>) { setBusy(true); try { await f(); } catch (e) { if (!auth.handleError(e)) setNotice(errText(e)); } finally { setBusy(false); } }
  const sync = () => run(async () => { const r = await auth.api.syncParties(); if (r.registryUnavailable) setRegistryDown(true); setNotice(t("pty.synced", { delivered: String(r.delivered), applied: String(r.applied), failed: String(r.failed), waiting: String(r.waiting) })); await load(); });
  const reconcile = (repair: boolean) => run(async () => { const rep = await auth.api.reconcileParties(repair); setReport(rep); if (rep.registryUnavailable) setRegistryDown(true); await load(); });

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("pty.title")} description={t("pty.subtitle")}
        actions={<>
          {(caps.create || caps.submit || caps.configure) && <button type="button" disabled={busy} data-testid="pty-sync" onClick={() => void sync()}>{t("pty.sync")}</button>}
          {caps.configure && <button type="button" disabled={busy} data-testid="pty-reconcile" onClick={() => void reconcile(false)}>{t("pty.reconcile")}</button>}
          {caps.configure && <button type="button" disabled={busy} onClick={() => void reconcile(true)}>{t("pty.reconcile_repair")}</button>}
          {caps.create && <button type="button" className="primary" onClick={() => guard(() => { setNotice(null); setEditor({ mode: "new" }); })}>{t("pty.new")}</button>}
        </>} />
      <Banner kind="info" testId="pty-isolation">{t("pty.isolation_note")}</Banner>
      {registryDown && <Banner kind="warning" testId="pty-registry-down">{t("pty.registry_unavailable")}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      {report && (
        <Card title={t("pty.findings")}>
          {report.findings.length === 0 ? <p data-testid="pty-reconciled-ok">{t("pty.reconciled_ok")}</p> : (
            <ul data-testid="pty-findings">{report.findings.map((f, i) => <li key={i}>{t(`pty.finding.${f.code}`)}{f.proposalId ? <> (<bdi dir="ltr">{f.proposalId}</bdi>)</> : null} — {f.repaired ? t("pty.repaired") : f.repairable ? t("pty.repairable") : t("pty.report_only")}</li>)}</ul>
          )}
        </Card>
      )}
      <Card title={t("pty.list")}>
        <div className="toolbar">
          <Field id="pty-search" label={t("pty.search")} hint={t("pty.search_hint")}>{(aria) => <input {...aria} type="search" value={search} onChange={(e) => { setOffset(0); setSearch(e.target.value); }} autoComplete="off" />}</Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("pty.list")}</caption>
            <thead><tr><th scope="col">{t("pty.code")}</th><th scope="col">{t("pty.display_name")}</th><th scope="col">{t("pty.legal_name")}</th><th scope="col">{t("pty.link_state")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
            <tbody>
              {rows.length === 0 && <tr><td colSpan={5}>{total === 0 && !search ? t("pty.none") : t("pty.none_filtered")}</td></tr>}
              {rows.map((p) => (
                <tr key={p.id} data-testid={`pty-${p.id}`}>
                  <td><bdi dir="ltr">{p.code}</bdi></td><td><bdi>{p.displayName}</bdi></td><td><bdi>{p.legalNameAr ?? ""}</bdi></td>
                  <td><span className={`chip ${p.linkState === 2 ? "chip-ok" : p.linkState === 3 ? "chip-off" : ""}`}>{t(`pty.link.${p.linkState}`)}</span>{p.hasOpenRequest && <> <span className="chip">{t("pty.open_request")}</span></>}</td>
                  <td><button type="button" onClick={() => guard(() => { setNotice(null); setEditor({ mode: "edit", id: p.id }); })}>{t("inv.view")}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="toolbar" data-testid="paging">
          <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>{t("inv.items.previous")}</button>
          <span aria-live="polite">{t("inv.items.paging", { from: String(total === 0 ? 0 : offset + 1), to: String(offset + rows.length), total: String(total) })}</span>
          <button type="button" disabled={offset + PAGE >= total} onClick={() => setOffset(offset + PAGE)}>{t("inv.items.next")}</button>
        </div>
      </Card>
    </section>
  );
}

function IdentityFields({ prefix, value, onChange, errors, readOnly, change }: { prefix: string; value: IdentityForm; onChange: (v: IdentityForm) => void; errors: Record<string, string>; readOnly: boolean; change: boolean }) {
  const { t } = useI18n();
  const e = (f: string) => errors[`${prefix}${f}`];
  const set = (p: Partial<IdentityForm>) => onChange({ ...value, ...p });
  return (
    <div className="fields">
      <Field id="pty-kind" label={t("pty.kind")} required error={e("kind")}>{(aria) => <select {...aria} value={value.kind} disabled={readOnly} onChange={(x) => set({ kind: x.target.value })}><option value="1">{t("pty.kind.1")}</option><option value="2">{t("pty.kind.2")}</option></select>}</Field>
      <Field id="pty-lar" label={t("pty.legal_name_ar")} required error={e("legalNameAr")}>{(aria) => <input {...aria} value={value.legalNameAr} readOnly={readOnly} maxLength={200} onChange={(x) => set({ legalNameAr: x.target.value })} />}</Field>
      <Field id="pty-len" label={t("pty.legal_name_en")} error={e("legalNameEn")}>{(aria) => <input {...aria} dir="ltr" value={value.legalNameEn} readOnly={readOnly} maxLength={200} onChange={(x) => set({ legalNameEn: x.target.value })} />}</Field>
      <Field id="pty-tax" label={t("pty.tax_number")} hint={t("pty.identifier_hint")} error={e("taxNumber")}>{(aria) => <input {...aria} dir="ltr" value={value.taxNumber} readOnly={readOnly} maxLength={60} onChange={(x) => set({ taxNumber: x.target.value })} />}</Field>
      <Field id="pty-reg" label={t("pty.registration_number")} error={e("registrationNumber")}>{(aria) => <input {...aria} dir="ltr" value={value.registrationNumber} readOnly={readOnly} maxLength={60} onChange={(x) => set({ registrationNumber: x.target.value })} />}</Field>
      {change && <Field id="pty-status" label={t("pty.identity_status")} error={e("status")}>{(aria) => <select {...aria} value={value.status} disabled={readOnly} onChange={(x) => set({ status: x.target.value })}><option value="1">{t("pty.identity_status.1")}</option><option value="2">{t("pty.identity_status.2")}</option></select>}</Field>}
      <Field id="pty-note" label={t("pty.proposal_note")} error={e("note")}>{(aria) => <textarea {...aria} value={value.note} readOnly={readOnly} rows={2} maxLength={500} onChange={(x) => set({ note: x.target.value })} />}</Field>
    </div>
  );
}

function PartyEditor({ editor, caps, onClose, onChanged, onSwitch }: { editor: Editor; caps: PartyCapabilities; onClose: () => void; onChanged: (m: string) => void; onSwitch: (id: string) => void }) {
  const { t } = useI18n();
  const auth = useAuth();
  const errText = useErrorText();
  const isNew = editor.mode === "new";
  const [doc, setDoc] = useState<LocalParty | null>(null);
  const [loading, setLoading] = useState(!isNew);
  const [form, setForm] = useState<Form>({ code: "", displayName: "", notes: "", isActive: true, identity: emptyIdentity });
  const [baseLocal, setBaseLocal] = useState(() => snapLocal(form));
  const [baseIdentity, setBaseIdentity] = useState(() => snapIdentity(form));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState<null | "local" | "shared">(null);
  // the shared identity revision the user was LOOKING AT: set only when the identity was loaded for display, never by a local Save reload
  const [viewedRevision, setViewedRevision] = useState<number | undefined>(undefined);
  const [identityStale, setIdentityStale] = useState(false);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState("details");

  // Reloading after Save refreshes ONLY the local data, after Propose ONLY the identity: edits the user made in the other section stay (review follow-up of 3.9A).
  const load = useCallback(async (scope: Scope = "all") => {
    if (editor.mode !== "edit") return;
    try {
      const d = await auth.api.getParty(editor.id);
      const i = d.identity;
      const identity: IdentityForm = i ? { kind: String(i.kind), legalNameAr: i.legalNameAr, legalNameEn: i.legalNameEn ?? "", taxNumber: i.taxNumber ?? "", registrationNumber: i.registrationNumber ?? "", status: String(i.status), note: "" } : emptyIdentity;
      const fresh: Form = { code: d.code, displayName: d.displayName, notes: d.notes ?? "", isActive: d.isActive, identity };
      setDoc(d);
      if (scope !== "local") { setViewedRevision(i?.revision); setIdentityStale(false); }
      else setIdentityStale(i?.revision !== viewedRevisionRef.current);
      setForm((cur) => ({
        ...(scope === "identity" ? { code: cur.code, displayName: cur.displayName, notes: cur.notes, isActive: cur.isActive } : { code: fresh.code, displayName: fresh.displayName, notes: fresh.notes, isActive: fresh.isActive }),
        identity: scope === "local" ? cur.identity : identity,
      }));
      if (scope !== "identity") setBaseLocal(snapLocal(fresh));
      if (scope !== "local") setBaseIdentity(snapIdentity(fresh));
      setErrors({}); setConflict(null); setLoading(false);
    } catch (e) { if (!auth.handleError(e)) { setError(errText(e)); setLoading(false); } }
  }, [auth.api, auth.handleError, editor, errText]);
  useEffect(() => { void load("all"); }, [load]);

  const viewedRevisionRef = useRef<number | undefined>(undefined);
  viewedRevisionRef.current = viewedRevision;
  const dirtyLocal = snapLocal(form) !== baseLocal;
  const dirtyIdentity = snapIdentity(form) !== baseIdentity;
  const dirty = dirtyLocal || dirtyIdentity;
  useReportUnsaved("party-form", dirty, t("pty.title"));
  const canEditLocal = isNew ? caps.create : caps.update;
  const open = doc?.requests.find((r) => r.status !== 3);
  const canPropose = !!doc && caps.submit && !open && doc.linkState !== 1;
  const change = doc?.linkState === 2;

  function showServerError(err: unknown) {
    if (auth.handleError(err)) return;
    if (err instanceof ApiError && err.status === 412) { setConflict(err.code === "shared_revision_conflict" ? "shared" : "local"); return; }
    if (err instanceof ApiError && err.status === 400) {
      const out: Record<string, string> = {};
      // a proposal reports bare identity field names; the form keys them under "identity."
      for (const f of err.details.fields ?? []) out[tabOf(f.field) === "identity" && !f.field.startsWith("identity.") ? `identity.${f.field}` : f.field] = fieldText(t, f.code);
      setErrors(out); setError(t("form.fix_errors"));
      const first = (["details", "identity"] as const).find((id) => Object.keys(out).some((k) => tabOf(k) === id));
      if (first) setTab(first);
      return;
    }
    setError(errText(err));
  }

  const localInput = (): LocalPartyInput => ({ code: form.code.trim(), displayName: form.displayName.trim(), notes: blank(form.notes), isActive: form.isActive, ...(isNew ? { identity: identityInput(form.identity, false) } : {}) });

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!canEditLocal) return;
    const v: Record<string, string> = {};
    if (!form.code.trim()) v.code = t("field.required");
    if (!form.displayName.trim()) v.displayName = t("field.required");
    if (isNew && !form.identity.legalNameAr.trim()) v["identity.legalNameAr"] = t("field.required");
    setErrors(v);
    if (Object.keys(v).length) { setError(t("form.fix_errors")); const first = (["details", "identity"] as const).find((id) => Object.keys(v).some((k) => tabOf(k) === id)); if (first) setTab(first); return; }
    setBusy(true); setError(null); setConflict(null);
    try {
      if (editor.mode === "new") { const d = await auth.api.createParty(localInput()); setBaseLocal(snapLocal(form)); setBaseIdentity(snapIdentity(form)); onChanged(t("pty.created")); onSwitch(d.id); }
      else { await auth.api.updateParty(editor.id, localInput(), doc!.revision); onChanged(t("pty.saved")); await load("local"); }
    } catch (err) { showServerError(err); } finally { setBusy(false); }
  }

  async function propose() {
    if (!doc) return;
    setBusy(true); setError(null); setConflict(null);
    try { await auth.api.proposeParty(doc.id, identityInput(form.identity, change, viewedRevision), doc.revision); onChanged(t("pty.proposed")); await load("identity"); }
    catch (err) { showServerError(err); } finally { setBusy(false); }
  }

  if (loading) return <Loading />;
  const invalid = new Set(Object.keys(errors).map(tabOf));
  const tabs: TabItem[] = [
    {
      id: "details", label: t("pty.tab.details"), invalid: invalid.has("details"), dirty: dirtyLocal,
      content: (
        <div className="fields">
          <Field id="pty-code" label={t("pty.code")} required hint={t("pty.code_hint")} error={errors.code}>{(aria) => <input {...aria} dir="ltr" value={form.code} readOnly={!canEditLocal} maxLength={30} onChange={(e) => setForm((f) => ({ ...f, code: e.target.value }))} autoComplete="off" />}</Field>
          <Field id="pty-name" label={t("pty.display_name")} required error={errors.displayName}>{(aria) => <input {...aria} value={form.displayName} readOnly={!canEditLocal} maxLength={200} onChange={(e) => setForm((f) => ({ ...f, displayName: e.target.value }))} />}</Field>
          <Field id="pty-notes" label={t("pty.notes")} error={errors.notes}>{(aria) => <textarea {...aria} value={form.notes} readOnly={!canEditLocal} rows={3} maxLength={1000} onChange={(e) => setForm((f) => ({ ...f, notes: e.target.value }))} />}</Field>
          <label className="check"><input type="checkbox" checked={form.isActive} disabled={!canEditLocal} onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))} /> {t("pty.active")}</label>
        </div>
      ),
    },
    {
      id: "identity", label: t("pty.tab.identity"), invalid: invalid.has("identity"), dirty: dirtyIdentity,
      content: (
        <div data-testid="pty-identity">
          {!isNew && !doc?.identity && <p className="hint" data-testid="pty-identity-none">{t("pty.identity_none")}</p>}
          <p className="hint">{t("pty.propose_hint")}</p>
          <IdentityFields prefix="identity." value={form.identity} errors={errors} readOnly={!(isNew ? caps.create : canPropose)} change={change} onChange={(identity) => setForm((f) => ({ ...f, identity }))} />
          {canPropose && <button type="button" data-testid="pty-propose" disabled={busy} onClick={() => void propose()}>{change ? t("pty.propose") : t("pty.propose_again")}</button>}
        </div>
      ),
    },
    ...(doc ? [{
      id: "requests", label: t("pty.tab.requests"),
      content: (
        <div className="table-wrap">
          <table className="data-table" data-testid="pty-requests">
            <caption className="visually-hidden">{t("pty.tab.requests")}</caption>
            <thead><tr><th scope="col">{t("ppa.kind")}</th><th scope="col">{t("pty.legal_name")}</th><th scope="col">{t("pty.request_status")}</th><th scope="col">{t("pty.attempts")}</th><th scope="col">{t("pty.last_error")}</th><th scope="col">{t("pty.created_at")}</th></tr></thead>
            <tbody>{doc.requests.map((r) => (
              <tr key={r.id}><td>{t(`pty.request_kind.${r.kind}`)}</td><td><bdi>{r.legalNameAr}</bdi></td><td>{r.outcome ? t(`pty.outcome.${r.outcome}`) : t(`pty.request_status.${r.status}`)}{r.decisionNote && <> — <bdi data-testid="pty-decision-note">{r.decisionNote}</bdi></>}</td><td className="num">{r.attempts}</td><td><bdi dir="ltr">{r.lastErrorCode ?? ""}</bdi></td><td><bdi dir="ltr">{r.createdAtUtc.slice(0, 19).replace("T", " ")}</bdi></td></tr>
            ))}</tbody>
          </table>
        </div>
      ),
    } as TabItem] : []),
  ];
  const activeTab = tabs.some((x) => x.id === tab) ? tab : "details";

  return (
    <section aria-labelledby="page-title" data-testid="pty-editor">
      <PageHeader title={doc ? `${doc.code} — ${doc.displayName}` : t("pty.new")} description={doc ? doc.identity?.legalNameAr ?? t("pty.identity_none") : t("pty.new_subtitle")}
        actions={doc && <span className={`chip ${doc.linkState === 2 ? "chip-ok" : doc.linkState === 3 ? "chip-off" : ""}`} data-testid="pty-link">{t(`pty.link.${doc.linkState}`)}</span>} />
      <Banner kind="info">{t("pty.isolation_note")}</Banner>
      {doc?.registryUnavailable && <Banner kind="warning" testId="pty-registry-down">{t("pty.registry_unavailable")}</Banner>}
      {conflict === "local" && <Banner kind="error" testId="form-conflict">{t("pty.error.revision_conflict")} <button type="button" onClick={() => void load("local")}>{t("form.reload_latest")}</button></Banner>}
      {conflict === "shared" && <Banner kind="error" testId="form-conflict-shared">{t("pty.error.shared_revision_conflict")} <button type="button" onClick={() => void load("identity")}>{t("pty.reload_identity")}</button></Banner>}
      {identityStale && !conflict && dirtyIdentity && <Banner kind="warning" testId="pty-identity-stale">{t("pty.identity_changed_elsewhere")} <button type="button" onClick={() => void load("identity")}>{t("pty.reload_identity")}</button></Banner>}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={save} noValidate>
        <Tabs label={t("pty.tabs_label")} tabs={tabs} value={activeTab} onChange={setTab} testId="pty-tabs" />
        <div className="actions" data-testid="pty-actions">
          <button type="button" onClick={onClose}>{t("inv.cancel")}</button>
          {canEditLocal && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : isNew ? t("pty.create") : t("inv.save")}</button>}
        </div>
      </form>
    </section>
  );
}
